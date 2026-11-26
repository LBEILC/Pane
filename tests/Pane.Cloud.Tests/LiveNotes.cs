using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Cloud;
using Pane.Services.Memo;
using Pane.Settings;

internal static partial class Program
{
    static async Task LiveNotes(Func<string,string,SettingsStore> create,Func<string,bool,string> sql)
    {
        var a = create("a","memo");
        var b = create("b","memo");
        sql("UPDATE pane_devices SET name=upper(id) WHERE uid='memo'",false);
        var options = new SyncOptions(Enabled:true,Settings:false,Usage:false);
        NoteStore Open(SettingsStore s)
        {
            s.Save(s.Current with { SyncOptions=options });
            return new(() => NoteDisk.FolderFor(s.DirectoryPath,null),s.StorageSync,File.Delete);
        }
        var left = Open(a);
        var right = Open(b);
        await Task.WhenAll(left.Completion,right.Completion);
        var note = left.Add("初稿");
        await using var aa = new AccountService(a.DirectoryPath,"1.0");
        await using var ab = new AccountService(b.DirectoryPath,"1.0");
        await using var sa = new SyncService(a,aa,notes:left);
        await using var sb = new SyncService(b,ab,notes:right);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        async Task Flush(SyncService first,SyncService second)
        {
            await first.SyncNowAsync(ct);
            await second.SyncNowAsync(ct);
            await Task.WhenAll(left.Completion,right.Completion);
        }
        string BodyFile(NoteStore store,string id)
        {
            var row = NoteDisk.ReadIndex(store.Folder).Single(r => r.Id == id);
            return Path.Combine(store.Folder,row.FileName);
        }
        await Flush(sa,sb);
        Check(File.ReadAllText(BodyFile(right,note.Id)) == "初稿","新建同步到文件和索引");
        int events = 0;
        right.Changed += (_,_) => Interlocked.Increment(ref events);
        left.Update(note.Id,"修订");
        left.SetPinned(note.Id,true);
        await Flush(sa,sb);
        var undo = right.Find(note.Id)!;
        Check(undo is { Text:"修订",Pinned:true } && events>0 &&
            NoteDisk.ReadIndex(right.Folder).Single().Pinned,"正文、固定、变更通知");
        left.Delete(note.Id);
        await Flush(sa,sb);
        Check(right.All.Count == 0 && !NoteDisk.Bodies(right.Folder).Any() && NoteDisk.ReadIndex(right.Folder).Length==0,"删除同步到文件和索引");
        right.Restore(undo);
        await Flush(sb,sa);
        Check(left.Find(note.Id) == undo,"另一台撤销删除");

        left.CreateTag("分类",2);
        left.SetTags(note.Id,["分类"]);
        await Flush(sa,sb);
        Check(right.Find(note.Id)!.Tags.SequenceEqual(["分类"]) && right.Tags.Contains(new("分类",2)),"A 打标签，B 收到定义和引用");
        left.UpdateTag("分类","分类",5);
        await Flush(sa,sb);
        Check(right.Tags.Contains(new("分类",5)),"修改色点同步");

        // 通过相同服务端提交旧版加密正文，显式省略 tags。
        var oldBody = new JsonObject { ["v"]=1, ["text"]="旧设备修改", ["pinned"]=true,
            ["created"]=note.Created.ToUnixTimeMilliseconds(), ["modified"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        long seq = long.Parse(sql($"SELECT seq FROM pane_records WHERE id='{note.Id}'",true));
        var oldChange = new JsonObject { ["c"]="notes", ["id"]=note.Id, ["base"]=seq,
            ["data"]=SyncCrypto.Encrypt(aa.DataKey,"notes",note.Id,oldBody.ToJsonString()) };
        var oldReply = await aa.SyncRequest("sync",new() { ["cursor"]=seq, ["changes"]=new JsonArray(oldChange) },"memo-a",ct);
        Check(oldReply["accepted"]!.AsArray().Count==1,"服务端收到旧版记录");
        await Flush(sb,sa);
        Check(right.Find(note.Id) is {Text:"旧设备修改"} oldResult && oldResult.Tags.SequenceEqual(["分类"]),"旧版缺少标签字段保留 B 的标签");
        await Offline();
        right.UpdateTag("分类","分类",1);
        left.DeleteTag("分类");
        await Rejoin();
        await Flush(sb,sa);
        Check(left.Tags.Count==0 && right.Tags.Count==0 && right.Find(note.Id)!.Tags.Count==0,"删除定义后往返同步仍为空");

        string longText = Convert.ToBase64String(RandomNumberGenerator.GetBytes(96000));
        left.Update(note.Id,longText);
        await Flush(sa,sb);
        var stored = sql($"SELECT data FROM pane_records WHERE id='{note.Id}'",true);
        var payload = JsonNode.Parse(SyncCrypto.Decrypt(aa.DataKey,"notes",note.Id,stored))!;
        Check(payload["text"] == null && payload["richBlocks"] is JsonArray { Count:1 } &&
            sql("SELECT count(*) FROM pane_blobs WHERE kind='rich' AND linked=1",true)=="1" &&
            File.ReadAllText(BodyFile(right,note.Id))==longText,"压缩长正文走 rich 块并还原");

        async Task Offline()
        {
            await sa.ApplyAsync(options with { Enabled=false },ct);
            await sb.ApplyAsync(options with { Enabled=false },ct);
        }
        async Task Rejoin()
        {
            await sa.ApplyAsync(options,ct);
            await sa.SyncNowAsync(ct);
            await sb.ApplyAsync(options,ct);
            await Flush(sb,sa);
        }
        await Offline();
        left.Update(note.Id,"A 的修改");
        right.Update(note.Id,"B 的修改");
        left.SetTags(note.Id,["A 分类"]);
        right.SetTags(note.Id,["B 分类"]);
        await Rejoin();
        foreach (var (store,own,other) in new[]{(left,"A","B"),(right,"B","A")})
            Check(store.All.Count==2 && store.All.Any(n=>n.Text==own+" 的修改" && n.From==null) &&
                store.All.Any(n=>n.Text==other+" 的修改" && n.From==other) &&
                store.All.All(n=>n.Tags.SequenceEqual([n.Text[..1]+" 分类"])),"正文冲突两份、各自标签及对方电脑名 "+own);

        await Offline();
        left.UpdateTag("A 分类","A 分类",1);
        left.SetTags(note.Id,["较早"]);
        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= left.Find(note.Id)!.Modified.ToUnixTimeMilliseconds())
            await Task.Delay(1,ct);
        right.UpdateTag("A 分类","A 分类",4);
        right.SetTags(note.Id,["较晚"]);
        await Rejoin();
        Check(new[]{left,right}.All(s=>s.All.Count==2 && s.Find(note.Id)!.Tags.SequenceEqual(["较晚"]) &&
            s.Tags.Contains(new("A 分类",4))),"只改标签和色点分别取较新版本");

        await Offline();
        left.SetPinned(note.Id,false);
        right.SetPinned(note.Id,false);
        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= left.Find(note.Id)!.Modified.ToUnixTimeMilliseconds())
            await Task.Delay(1,ct);
        right.SetPinned(note.Id,true);
        await Rejoin();
        Check(left.All.Count==2 && right.All.Count==2 && left.Find(note.Id)!.Pinned && right.Find(note.Id)!.Pinned,"固定冲突只留较新状态");

        await Offline();
        left.Delete(note.Id);
        right.Update(note.Id,"删除期间的修订");
        await Rejoin();
        Check(left.Find(note.Id)?.Text=="删除期间的修订" && right.Find(note.Id)?.Text==left.Find(note.Id)?.Text,"删除和编辑保留修订");

        async Task PauseAndResume(bool module)
        {
            if (module) sa.SetNotes(null);
            else await sa.ApplyAsync(options with { Notes=false },ct);
            await Flush(sa,sb);
            var onlyA = left.Add("A 暂存");
            var onlyB = right.Add("B 暂存");
            await Flush(sb,sa);
            Check(right.Find(onlyA.Id)==null && left.Find(onlyB.Id)==null &&
                sql($"SELECT count(*) FROM pane_records WHERE id='{onlyA.Id}'",true)=="0","停用期间双向隔离");
            if (module) sa.SetNotes(left);
            else await sa.ApplyAsync(options,ct);
            await Flush(sa,sb);
            Check(right.Find(onlyA.Id)?.Text==onlyA.Text && left.Find(onlyB.Id)?.Text==onlyB.Text,"重新启用后双向补齐");
        }
        await PauseAndResume(false);
        await PauseAndResume(true);
        File.WriteAllText(BodyFile(left,note.Id),"外部编辑");
        left.Refresh();
        await left.Completion;
        await Flush(sa,sb);
        Check(File.ReadAllText(BodyFile(right,note.Id))=="外部编辑","Refresh 后上传外部编辑");
        Console.WriteLine($"PASS live notes assertions={checks}");
    }
}
