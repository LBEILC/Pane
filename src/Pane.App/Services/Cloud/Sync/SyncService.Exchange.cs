using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Cloud;

internal sealed partial class SyncService
{
    bool Enabled(string category, SyncOptions options) =>
        category == "settings" && options.Settings || category == "usage" && options.Usage || categories.TryGetValue(category,out var handler) && handler.Enabled(options);

    // 先记下实际改动与时间，再发送请求。网络失败后沿用同一份待提交内容。
    void Capture(SyncJournal j, byte[] key, string device, SyncOptions options)
    {
        foreach(var category in categories.Values) if (category.Enabled(options)) category.Capture(j,key,device);
        if (options.Settings)
        {
            while (edits.TryDequeue(out var edit))
            {
                var before = SyncData.Leaves(edit.Before);
                foreach(var (path,value) in SyncData.Leaves(edit.After))
                    if (!before.TryGetPropertyValue(path,out var old) || !JsonNode.DeepEquals(value,old)) editTimes[path] = edit.At;
            }
            var leaves = SyncData.Leaves(store.Current);
            foreach (var (path,value) in leaves)
            {
                if (j.Settings.TryGetPropertyValue(path,out var old) && JsonNode.DeepEquals(old,value)) continue;
                var id = SyncCrypto.Id(key,"settings",path);
                if (j.Records.GetValueOrDefault(id)?.Unknown != null) continue;
                var rec = Local(j,id,"settings",device);
                rec.Body!["k"] = path;
                rec.Body["val"] = WithExtensions(value,old,rec.Body["val"]);
                rec.Body["at"] = j.Settings.ContainsKey(path) ? Math.Max(EditTime(path), (long?)rec.Body["at"] + 1 ?? 0) : 0;
            }
            j.Settings = leaves;
        }
        if (options.Usage)
        {
            var own = SyncCrypto.Id(key,"usage",device);
            if (j.Records.GetValueOrDefault(own)?.Unknown != null) return;
            var usage = SyncData.Usage(store.DirectoryPath);
            if (!JsonNode.DeepEquals(j.Usage,usage))
            {
                var rec = Local(j,own,"usage",device);
                foreach(var (name,value) in usage) rec.Body![name] = value?.DeepClone();
                rec.Body!["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                j.Usage = usage;
            }
        }
    }
    long EditTime(string path)
    {
        if (editTimes.TryGetValue(path,out var at)) return at;
        return new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(store.DirectoryPath,"settings.json"))).ToUnixTimeMilliseconds();
    }
    // 只更新本版本认识的子字段。对象型热键或列表条目的扩展字段沿用云端值。
    static JsonNode? WithExtensions(JsonNode? value,JsonNode? known,JsonNode? original)
    {
        if (value is JsonObject fields && original is JsonObject before)
        {
            var result = before.DeepClone().AsObject();
            foreach(var (name,node) in fields) result[name] = WithExtensions(node,known?[name],before[name]);
            return result;
        }
        if (value is JsonArray items && known is JsonArray previous && original is JsonArray raw)
        {
            var result = new JsonArray();
            var knownItems = previous.ToList();
            foreach(var item in items)
            {
                int index = knownItems.FindIndex(v => JsonNode.DeepEquals(v,item) || item is JsonObject obj &&
                    new[]{"Id","Name","Keyword"}.Any(k => obj[k] != null && JsonNode.DeepEquals(obj[k],v?[k])));
                ((IList<JsonNode?>)result).Add(index >= 0 && index < raw.Count ? WithExtensions(item,previous[index],raw[index]) : item?.DeepClone());
            }
            return result;
        }
        return value?.DeepClone();
    }
    static SyncRecord Local(SyncJournal j,string id,string c,string device)
    {
        if (!j.Records.TryGetValue(id,out var rec)) j.Records[id] = rec = new() { Category = c };
        rec.Body ??= new() { ["v"] = 1 };
        rec.Device = device;
        rec.Dirty = true;
        return rec;
    }

    async Task<bool> Initial(SyncJournal j,string owner,string token,byte[] key,string device,SyncOptions options,CancellationToken ct)
    {
        var cloud = new List<JsonObject>();
        long cursor = 0;
        do
        {
            var reply = await account.SyncRequest("sync",new() { ["initial"] = true, ["cursor"] = cursor, ["changes"] = new JsonArray() },token,ct);
            foreach (var node in Rows(reply,"changes")) cloud.Add(node);
            cursor = (long)reply["seq"]!;
            if ((bool?)reply["more"] != true) break;
        } while(true);
        ct.ThrowIfCancellationRequested();
        bool switched = j.Account != null && j.Account != owner;
        if (switched && j.Records.Values.Any(r => r.Base > 0 && r.Body != null) && cloud.Count > 0 && choice == null)
        {
            Status(SyncState.Paused,SyncPauseReason.NeedsMergeChoice);
            return false;
        }
        var previous = switched ? j.Records.Values.Where(r => r.Category == "settings" && r.Body != null).ToDictionary(r => (string)r.Body!["k"]!) : null;
        if (switched)
        {
            j.Records.Clear(); j.Settings.Clear(); j.Usage = null; j.LastSuccess = null;
        }
        var mode = switched ? choice : null;
        var seen = cloud.Select(r => (string)r["id"]!).ToHashSet();
        foreach(var (id,r) in j.Records.ToArray())
        {
            if (seen.Contains(id) || r.Unknown != null) continue;
            if (r.Category == "settings" && r.Body?["k"] is {} path && !j.Settings.ContainsKey(path.GetValue<string>())) continue;
            if (r.Category == "settings" && r.Body != null ||
                r.Category == "usage" && id == SyncCrypto.Id(key,"usage",device))
            { r.Base = 0; r.Dirty = true; }
            else if (r.Category is not ("notes" or "clip" or "cliptags" or "control")) j.Records.Remove(id);
        }
        // 全量快照没有墓碑。本机有值但没有对应记录时，让 Capture 用当前值重新建队。
        if (options.Settings)
            foreach(string path in j.Settings.Select(p => p.Key).ToArray())
            {
                string id = SyncCrypto.Id(key,"settings",path);
                if (!seen.Contains(id) && j.Records.GetValueOrDefault(id)?.Body == null) j.Settings.Remove(path);
            }
        Capture(j,key,device,options);
        if (NotesEnabled(options))
            foreach (var (id, r) in j.Records.ToArray())
            {
                if (r.Category != "notes" || seen.Contains(id) || r.Unknown != null) continue;
                if (r.Dirty) r.Base = 0;
                else if (r.Body != null)
                    await Receive(j,new() { ["id"]=id, ["c"]="notes", ["seq"]=0, ["deleted"]=true },key,device,options,new(),ct,initial:true);
            }
        if (mode == SyncMergeChoice.Merge)
            foreach(var r in j.Records.Values.Where(r => r.Category == "settings" && r.Body != null))
            {
                var old = previous!.GetValueOrDefault((string)r.Body!["k"]!);
                r.Body["at"] = old?.Body != null && JsonNode.DeepEquals(old.Body["val"],r.Body["val"]) ? (long?)old.Body["at"] ?? 0 : EditTime((string)r.Body["k"]!);
            }
        var patch = new JsonObject();
        if (mode == SyncMergeChoice.UseCloud && options.Settings)
        {
            var defaults = SyncData.Leaves(store.Defaults);
            foreach(var (path,_) in j.Settings)
            {
                var id = SyncCrypto.Id(key,"settings",path);
                if (!seen.Contains(id)) { patch[path] = defaults[path]?.DeepClone(); j.Records.Remove(id); }
            }
        }
        string own = SyncCrypto.Id(key,"usage",device);
        foreach(var wire in cloud)
        {
            var id = (string)wire["id"]!;
            var c = (string)wire["c"]!;
            if (mode == SyncMergeChoice.UseLocal && Enabled(c,options))
            {
                var remote = Decode(wire,key);
                if (remote.Unknown == null && (c == "usage" || remote.Body?["k"] is {} k && j.Settings.ContainsKey((string)k!)))
                {
                    if (!j.Records.TryGetValue(id,out var rec)) j.Records[id] = rec = new() { Category = c };
                    if (rec.Body != null && remote.Body != null)
                        foreach (var (name,value) in remote.Body)
                            if (!rec.Body.ContainsKey(name)) rec.Body[name] = value?.DeepClone();
                    rec.Base = remote.Base; rec.DeleteFirst = true; rec.Dirty = true;
                    continue;
                }
            }
            await Receive(j,wire,key,device,options,patch,ct,initial:true,forceCloud:mode == SyncMergeChoice.UseCloud);
        }
        if (mode == SyncMergeChoice.UseCloud && options.Usage)
        {
            SyncData.ReplaceUsage(store,seen.Contains(own) ? j.Records[own].Body : null);
            j.Usage = SyncData.Usage(store.DirectoryPath);
            if (!seen.Contains(own)) j.Records.Remove(own);
        }
        Apply(j,patch,options,own);
        ct.ThrowIfCancellationRequested();
        j.NotesEnabled = NotesEnabled(options);
        j.ClipboardEnabled = ClipboardEnabled(options); j.SettingsEnabled = options.Settings; j.UsageEnabled = options.Usage;
        j.KeyId = CloudKeys.Check(key);
        j.Account = owner; j.Initialized = true; j.Cursor = cursor;
        j.Save(file);
        choice = null;
        return true;
    }
    static IEnumerable<JsonObject> Rows(JsonObject reply,string name) =>
        (reply[name] as JsonArray ?? []).Select(n => n!.AsObject());
    static SyncRecord Decode(JsonObject wire,byte[] key)
    {
        string c = (string)wire["c"]!, id = (string)wire["id"]!;
        var rec = new SyncRecord { Category = c, Device = (string?)wire["dev"] ?? "", Base = (long)wire["seq"]! };
        if (c is not ("usage" or "settings")) rec.Unknown = wire.DeepClone().AsObject();
        else if ((bool?)wire["deleted"] != true)
        {
            rec.Body = JsonNode.Parse(SyncCrypto.Decrypt(key,c,id,(string)wire["data"]!))!.AsObject();
            if ((int?)rec.Body["v"] != 1) { rec.Unknown = wire.DeepClone().AsObject(); rec.Body = null; }
            else if (c == "settings" && SyncCrypto.Id(key,c,(string)rec.Body["k"]!) != id)
                throw new InvalidDataException();
        }
        return rec;
    }
    async Task Receive(SyncJournal j,JsonObject wire,byte[] key,string device,SyncOptions options,JsonObject patch,CancellationToken ct,bool initial = false,bool forceCloud = false)
    {
        string id = (string)wire["id"]!;
        if (categories.TryGetValue((string)wire["c"]!,out var handler) && handler.Enabled(options))
        {
            await handler.Receive(j,wire,key,initial,ct);
            return;
        }
        if ((string?)wire["c"] == "notes") return;
        var remote = Decode(wire,key);
        j.Records.TryGetValue(id,out var local);
        if (!initial && local != null && local.Base >= remote.Base) return;
        var c = remote.Category;
        if (local != null && (local.Dirty || !initial && c == "settings" && remote.Body != null && local.Body != null) && !forceCloud && remote.Unknown == null)
        {
            long left = (long?)local.Body?["at"] ?? 0, right = (long?)remote.Body?["at"] ?? 0;
            if (local.DeleteFirst || c == "usage" && id == SyncCrypto.Id(key,c,device) ||
                c == "settings" && (!initial || left > 0) && (left > right || left == right && string.CompareOrdinal(local.Device,remote.Device)>0))
            {
                local.Base = remote.Base;
                local.Dirty = true;
                if (remote.Body != null && local.Body != null)
                {
                    foreach(var (name,value) in remote.Body)
                        if (!local.Body.ContainsKey(name)) local.Body[name] = value?.DeepClone();
                    if (c == "settings" && (string?)local.Body["k"] is {} pathKey)
                        local.Body["val"] = WithExtensions(local.Body["val"],j.Settings[pathKey],remote.Body["val"]);
                }
                return;
            }
        }
        j.Records[id] = remote;
        if (c != "settings" || !options.Settings || remote.Unknown != null) return;
        var path = (string?)(remote.Body ?? local?.Body)?["k"];
        if (path == null || !j.Settings.ContainsKey(path)) return;
        patch[path] = (remote.Body == null ? SyncData.Leaves(store.Defaults)[path] : remote.Body["val"])?.DeepClone();
    }
    void Apply(SyncJournal j,JsonObject patch,SyncOptions options,string own)
    {
        if (patch.Count > 0)
            store.UpdateFromSync(current =>
            {
                var now = SyncData.Leaves(current);
                var safe = new JsonObject();
                foreach(var (path,value) in patch)
                {
                    if (!JsonNode.DeepEquals(now[path],j.Settings[path])) continue;
                    if (!JsonNode.DeepEquals(now[path],value)) safe[path] = value?.DeepClone();
                    j.Settings[path] = value?.DeepClone();
                }
                if (safe.Count == 0) return current;
                var next = SyncData.Apply(current,safe);
                var projected = SyncData.Leaves(next);
                foreach(var (path,_) in safe) j.Settings[path] = projected[path]?.DeepClone();
                return next;
            });
        if (!options.Usage) return;
        var counts = new Dictionary<string,int>();
        var commands = new List<string>();
        foreach(var (id,r) in j.Records.OrderByDescending(p => (long?)p.Value.Body?["at"] ?? 0))
        {
            if (id == own || r.Category != "usage" || r.Body == null) continue;
            foreach(var (item,count) in r.Body["counts"]?.AsObject() ?? new())
                counts[item] = (int)Math.Clamp(counts.GetValueOrDefault(item) + (long)count!,0,int.MaxValue);
            foreach(var raw in r.Body["history"]?.AsArray() ?? []) if ((string?)raw is {} s) commands.Add(s);
        }
        store.OtherCounts = counts;
        store.OtherCommands = commands.Distinct(StringComparer.Ordinal).ToArray();
    }
    async Task Exchange(SyncJournal j,string token,byte[] key,string device,SyncOptions options,bool forceUsage,CancellationToken ct)
    {
        bool Eligible(SyncRecord r)
        {
            if(!r.Dirty || !Enabled(r.Category,options))return false;
            if(r.Category=="clip" && r.Body!=null && (options.ClipScope==ClipSyncScope.Text && (string?)r.Body["kind"]=="Image" ||
                r.LocalProgram!=null && options.ExcludedApps?.Contains(r.LocalProgram,StringComparer.OrdinalIgnoreCase)==true))return false;
            return r.Category!="usage" || forceUsage || Environment.TickCount64>=Interlocked.Read(ref usageDue);
        }
        bool more;
        do
        {
            ct.ThrowIfCancellationRequested();
            Capture(j,key,device,options);
            j.Save(file);
            var batch = new JsonArray();
            var pending = j.Records.Where(p => Eligible(p.Value)).OrderBy(p=>p.Value.Category=="clip" && ((bool?)p.Value.Body?["pinned"]==true || p.Value.Body?["tags"] is JsonArray {Count:>0})?0:p.Value.Category=="control"?1:2);
            bool deleting = pending.Any(p => p.Value.DeleteFirst);
            int bytes = 0;
            foreach(var (id,r) in pending.Where(p => !deleting || p.Value.DeleteFirst).Take(500))
            {
                JsonObject change = new() { ["id"] = id, ["c"] = r.Category, ["base"] = r.Base };
                if (categories.TryGetValue(r.Category,out var handler)) change = await handler.Encode(id,r,key,ct);
                else if (r.DeleteFirst || r.Body == null) change["deleted"] = true;
                else change["data"] = SyncCrypto.Encrypt(key,r.Category,id,r.Body.ToJsonString());
                bytes += ((string?)change["data"])?.Length + 128 ?? 128;
                if (bytes > 4*1024*1024-1024 && batch.Count != 0) break;
                ((IList<JsonNode?>)batch).Add(change);
            }
            if(batch.Count>0 && options.Clipboard)j.Save(file);
            var reply = await account.SyncRequest("sync",new() { ["changes"] = batch, ["cursor"] = j.Cursor },token,ct);
            ct.ThrowIfCancellationRequested();
            foreach(var accepted in Rows(reply,"accepted"))
            {
                var r = j.Records[(string)accepted["id"]!];
                r.Base = (long)accepted["seq"]!;
                r.Device = device;
                if(r.Category=="clip")AcknowledgeClip((string)accepted["id"]!,r);
                if (r.Category == "notes") r.NoteBaseHash = NotesCategory.TextHash(r.Body);
                r.Dirty = r.DeleteFirst && r.Body != null;
                r.DeleteFirst = false;
            }
            Capture(j,key,device,options);
            var patch = new JsonObject();
            foreach(var wire in Rows(reply,"conflicts").Concat(Rows(reply,"changes"))) await Receive(j,wire,key,device,options,patch,ct);
            foreach(var rejected in Rows(reply,"rejected"))
            {
                if (!categories.TryGetValue((string)rejected["c"]!,out var handler)) throw new InvalidDataException();
                handler.Rejected(j,rejected);
            }
            Apply(j,patch,options,SyncCrypto.Id(key,"usage",device));
            ct.ThrowIfCancellationRequested();
            j.Cursor = (long)reply["seq"]!;
            j.Save(file);
            more = (bool?)reply["more"] == true || j.Records.Values.Any(Eligible);
        } while(more);
    }
}
