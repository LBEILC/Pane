using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Memo;

namespace Pane.Services.Cloud;

internal sealed partial class SyncService
{
    volatile NoteStore? notes;
    bool NotesEnabled(SyncOptions options) => options.Notes && notes != null;
    void NotesChanged(object? sender, EventArgs args)
    {
        if (NotesEnabled(Options)) Wake();
    }

    internal void SetNotes(NoteStore? value)
    {
        lock (gate)
        {
            if (notes == value || disposed) return;
            if (notes != null) notes.Changed -= NotesChanged;
            notes = value;
            if (value != null) value.Changed += NotesChanged;
            Restart(true);
        }
    }

    sealed class NotesCategory(SyncService engine) : ISyncCategory
    {
        public string Name => "notes";
        public bool Enabled(SyncOptions options) => engine.NotesEnabled(options);
        NoteStore Store => engine.notes ?? throw new OperationCanceledException();

        internal static string? TextHash(JsonObject? body) => body?["text"] == null ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((string)body["text"]!)));

        static string DefinitionId(byte[] key,string name) => SyncCrypto.Id(key,"notes","tag/" + name.ToUpperInvariant());
        static JsonObject TagBody(MemoTag tag) => new()
        {
            ["v"] = 2, ["definition"] = JsonSerializer.SerializeToNode(tag,MemoJson.Default.MemoTag)
        };

        static string[] TagNames(JsonObject body) => body["tags"] is JsonArray names
            ? names.Select(n => (string)n!).ToArray() : [];

        static void CaptureOne(SyncJournal j,string device,string id,Note? note)
        {
            var record = j.Records.GetValueOrDefault(id);
            if (record?.Unknown != null) return;
            var body = record?.Body;
            if (note == null)
            {
                if (body != null) Local(j,id,"notes",device).Body = null;
                return;
            }
            long created = note.Created.ToUnixTimeMilliseconds(), modified = note.Modified.ToUnixTimeMilliseconds();
            if (body != null && (string?)body["text"] == note.Text && (bool?)body["pinned"] == note.Pinned &&
                (long?)body["created"] == created && (long?)body["modified"] == modified &&
                body["tags"] != null && TagNames(body).SequenceEqual(note.Tags)) return;
            body = Local(j,id,"notes",device).Body!;
            body["tags"] = new JsonArray(note.Tags.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            body["text"] = note.Text;
            body["pinned"] = note.Pinned;
            body["created"] = created;
            body["modified"] = modified;
        }

        public void Capture(SyncJournal j,byte[] key,string device)
        {
            var all = Store.All;
            var seen = new HashSet<string>();
            foreach (var tag in Store.Definitions)
            {
                string id = DefinitionId(key,tag.Name);
                seen.Add(id);
                var record = j.Records.GetValueOrDefault(id);
                var body = TagBody(tag);
                if (record?.Unknown == null && !JsonNode.DeepEquals(record?.Body,body))
                    Local(j,id,Name,device).Body = body;
            }
            foreach (var note in all)
            {
                CaptureOne(j,device,note.Id,note);
                seen.Add(note.Id);
            }
            foreach (var (id,r) in j.Records)
                if (r.Category == Name && !seen.Contains(id)) CaptureOne(j,device,id,null);
        }

        public async Task<JsonObject> Encode(string id,SyncRecord record,byte[] key,CancellationToken ct)
        {
            var wire = new JsonObject { ["c"]=Name, ["id"]=id, ["base"]=record.Base };
            if (record.Body == null) { wire["deleted"]=true; return wire; }
            var body = record.Body.DeepClone().AsObject();
            if (body["definition"] != null)
            {
                wire["data"] = SyncCrypto.Encrypt(key,Name,id,body.ToJsonString());
                return wire;
            }
            using var compressed = new MemoryStream();
            using (var zip = new DeflateStream(compressed,CompressionLevel.Fastest,true))
                zip.Write(Encoding.UTF8.GetBytes((string)body["text"]!));
            // JSON 转义可能扩大密文；同时遵守服务端单条 64 KiB 上限。
            string? inline = compressed.Length <= 48*1024 ? SyncCrypto.Encrypt(key,Name,id,body.ToJsonString()) : null;
            if (inline == null || CloudKeys.Decode(inline).Length > 64*1024)
            {
                var blocks = new JsonArray();
                var bytes = compressed.ToArray();
                // 单块密文上限为 2.5 MiB，超出时沿用剪贴板的分块大小。
                for (int pos=0; pos<bytes.Length; pos+=2400000)
                {
                    var part = bytes.AsMemory(pos,Math.Min(2400000,bytes.Length-pos));
                    ((IList<JsonNode?>)blocks).Add(JsonValue.Create(await engine.Upload(key,"rich",part.ToArray(),ct)));
                }
                body["text"] = null;
                body["richBlocks"] = blocks;
                wire["blobs"] = blocks.DeepClone();
                inline = SyncCrypto.Encrypt(key,Name,id,body.ToJsonString());
            }
            wire["data"] = inline;
            return wire;
        }

        string? From(JsonObject body)
        {
            string? origin = (string?)body["conflictDevice"];
            if (origin == null || origin == engine.localDeviceId) return null;
            return engine.OtherDevices.FirstOrDefault(d => d.Id == origin)?.Name ?? "其他电脑";
        }

        Note Materialize(string id,JsonObject b) => new(id,(string)b["text"]!, (bool)b["pinned"]!,
            DateTimeOffset.FromUnixTimeMilliseconds((long)b["created"]!),
            DateTimeOffset.FromUnixTimeMilliseconds((long)b["modified"]!),From(b)) { Tags = TagNames(b) };

        JsonObject Merge(SyncRecord local,JsonObject result,string? remoteHash,string device,out JsonObject? copy)
        {
            copy = null;
            var left = local.Body!;
            bool sameText = (string?)left["text"] == (string?)result["text"];
            bool leftEdited = local.NoteBaseHash != TextHash(left);
            bool rightEdited = local.NoteBaseHash != remoteHash;
            bool newer = (long)left["modified"]! > (long)result["modified"]!;
            if (!sameText && leftEdited && rightEdited)
            {
                copy = result.DeepClone().AsObject();
                copy["conflictDevice"] = device;
                result = left.DeepClone().AsObject();
                result["conflictDevice"] = engine.localDeviceId;
            }
            else if (sameText) result = newer ? left : result;
            else
            {
                var merged = (leftEdited ? left : result).DeepClone().AsObject();
                var latest = newer ? left : result;
                merged["tags"] = latest["tags"]?.DeepClone();
                merged["pinned"] = latest["pinned"]!.DeepClone();
                merged["modified"] = latest["modified"]!.DeepClone();
                result = merged;
            }
            return result;
        }

        public async Task Receive(SyncJournal j,JsonObject wire,byte[] key,bool initial,CancellationToken ct)
        {
            string id = (string)wire["id"]!, device = (string?)wire["dev"] ?? "";
            long seq = (long)wire["seq"]!;
            var local = j.Records.GetValueOrDefault(id);
            if (!initial && local != null && local.Base >= seq) return;
            var remote = new SyncRecord { Category=Name, Base=seq, Device=device };
            if ((bool?)wire["deleted"] != true)
            {
                var body = JsonNode.Parse(SyncCrypto.Decrypt(key,Name,id,(string)wire["data"]!))!.AsObject();
                if ((int?)body["v"] == 2 && body["definition"] is JsonObject definition)
                {
                    var tag = definition.Deserialize(MemoJson.Default.MemoTag) ?? throw new InvalidDataException();
                    if (DefinitionId(key,tag.Name) != id || tag.Name.Length is 0 or > 30 || tag.Name.Trim() != tag.Name)
                        throw new InvalidDataException();
                    var winner = Store.MergeDefinition(tag,out var persisted);
                    await persisted;
                    remote.Body = TagBody(winner);
                    remote.Dirty = winner != tag;
                    j.Records[id] = remote;
                    return;
                }
                if ((int?)body["v"] != 1) { remote.Unknown=wire.DeepClone().AsObject(); j.Records[id]=remote; return; }
                if (body["richBlocks"] is JsonArray blocks)
                {
                    using var bytes = new MemoryStream();
                    foreach (var block in blocks) bytes.Write(await engine.Download((string)block!,"rich",key,ct));
                    bytes.Position = 0;
                    using var zip = new DeflateStream(bytes,CompressionMode.Decompress);
                    using var reader = new StreamReader(zip,Encoding.UTF8,detectEncodingFromByteOrderMarks:false);
                    body["text"] = await reader.ReadToEndAsync(ct);
                    body.Remove("richBlocks");
                }
                remote.Body = body;
                remote.NoteBaseHash = TextHash(body);
            }
            // 旧客户端把 notes 原样记作未知类别。确认版本可读后，首次合并也要采集已有的本机正文。
            if (local?.Unknown != null) j.Records.Remove(id);
            var store = Store;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var current = store.Find(id);
                CaptureOne(j,engine.localDeviceId,id,current);
                local = j.Records.GetValueOrDefault(id);
                var result = remote.Body;
                if (result != null && result["tags"] == null && current != null)
                {
                    result = result.DeepClone().AsObject();
                    result["tags"] = new JsonArray(current.Tags.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
                }
                JsonObject? copy = null;
                if (local?.Dirty == true)
                {
                    if (initial && local.Base == seq && wire["dev"] != null) return;
                    if (local.Body != null && result == null) result = local.Body;
                    else if (local.Body != null && result != null)
                    {
                        result = Merge(local,result,remote.NoteBaseHash,device,out copy);
                    }
                }
                if (!store.ApplySynced(id,current,result == null ? null : Materialize(id,result),out var saved)) continue;
                await saved;
                if (copy != null)
                {
                    // 同一服务端版本的冲突重试时使用同一个副本 id。
                    string copyId = SyncCrypto.Id(key,Name,id + "/" + seq);
                    if (store.ApplySynced(copyId,null,Materialize(copyId,copy),out saved)) await saved;
                    if (!j.Records.TryGetValue(copyId,out var previousCopy))
                        Local(j,copyId,Name,engine.localDeviceId).Body = copy;
                    else if (previousCopy.Base == 0 && previousCopy.Body != null)
                        previousCopy.Body["conflictDevice"] = device;
                }
                remote.Dirty = !JsonNode.DeepEquals(result,remote.Body);
                remote.Body = result;
                j.Records[id] = remote;
                return;
            }
        }

        public void Rejected(SyncJournal j,JsonObject rejection) => throw new InvalidDataException();
    }
}
