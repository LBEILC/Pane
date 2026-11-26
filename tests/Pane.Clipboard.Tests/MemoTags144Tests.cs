using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Clipboard;
using Pane.Services.Memo;
using Pane.Settings;

static partial class Program
{
    static async Task MemoTags144Tests()
    {
        string root = TestResults.NewDirectory("144-");
        DataStorage Storage(string device) => new(new SettingsStore(Path.Combine(root,device),Defaults()),()=>null,()=>{});
        NoteStore Open(DataStorage storage) => new(()=>storage.CurrentMemoFolder,new object(),File.Delete);
        var source = Storage("source");
        var store = Open(source);
        await store.Completion;
        var a = store.Add("会议纪要",[" Work ","work","归档"],new Dictionary<string,int?> { ["WORK"]=2 });
        var b = store.Add("其他",["Work"],new Dictionary<string,int?> { ["work"]=4 });
        store.Add("无标签");
        await store.Completion;
        string body = Path.Combine(store.Folder,NoteDisk.ReadIndex(store.Folder).Single(r => r.Id == a.Id).FileName);
        var lastWrite = File.GetLastWriteTimeUtc(body);
        Check(a.Tags.SequenceEqual(["Work","归档"]) && store.Tags.SequenceEqual([new NoteTag("Work",2),new("归档",null)]),"按顺序新建、去重并保留原色");
        store.SetTags(a.Id,["Work"," 新增 ","新增"]);
        var modified = store.Find(a.Id)!.Modified;
        store.SetTags(a.Id,["work","新增"]);
        Check(modified > a.Modified && store.Find(a.Id)!.Modified == modified && store.Tags.Last() == new NoteTag("新增",null),"自动新建和幂等设置");
        bool Duplicate()
        {
            try { store.UpdateTag("Work","新增",1); return false; }
            catch (ArgumentException e) { return e.Message.Contains("同名"); }
        }
        Check(Duplicate(),"改名重名报错");
        store.UpdateTag("work","工作",3);
        store.SetPinned(b.Id,true);
        Check(store.Find(b.Id)!.Tags.SequenceEqual(["工作"]) && store.Find(a.Id)!.Tags.SequenceEqual(["工作","新增"]),"全部引用随改名更新");
        Check(store.Search("#工",10).Select(m=>m.Note.Id).SequenceEqual([b.Id,a.Id]) &&
            store.Search("#工 #新",10).Single().Note.Id == a.Id && store.Search("#工 会议",10).Single().Note.Id == a.Id &&
            store.Search("#",10).Count == 2,"前缀、交集、关键词、单独井号和固定排序");
        store.DeleteTag("新增");
        await store.Completion;
        Check(File.ReadAllText(body)==a.Text && File.GetLastWriteTimeUtc(body)==lastWrite,"只改索引");
        store = Open(source);
        await store.Completion;
        Check(store.Find(a.Id)!.Tags.SequenceEqual(["工作"]) && store.Tags.SequenceEqual([new NoteTag("工作",3),new("归档",null)]),"重启保留标签与定义顺序");
        File.WriteAllText(body,"外部修改");
        File.Move(body,Path.Combine(store.Folder,"编辑器改名.md"));
        store.Refresh();
        await store.Completion;
        Check(store.Find(a.Id) is {Text:"外部修改"} edited && edited.Tags.SequenceEqual(["工作"]),"外部改正文并改名保留 id 和标签");

        var dest = Storage("dest");
        var local = Open(dest);
        await local.Completion;
        local.CreateTag("本地",5);
        local.Restore(a with { Modified = a.Modified.AddDays(-1), Tags = ["本地"] });
        local.Restore(b with { Modified = b.Modified.AddDays(1), Tags = ["本地"] });
        await local.Completion;
        string backup = Path.Combine(root,"备份.zip");
        await source.ExportAsync(backup,new(false,false,ClipboardExport.None),null,default);
        await dest.StageImportAsync(backup,new(false,false,false,DataImportMode.Merge),null,default);
        DataStorage.ApplyPending(Path.Combine(root,"dest"));
        local.Refresh();
        await local.Completion;
        Check(dest.LastImport is {Error:null} && local.Find(a.Id)!.Tags.SequenceEqual(["工作"]) &&
            local.Find(b.Id)!.Tags.SequenceEqual(["本地"]) && local.Tags.Contains(new NoteTag("工作",3)) &&
            local.Tags.Contains(new NoteTag("归档",null)) && local.Tags.Contains(new NoteTag("本地",5)),"归档合并保留两端较新标签和独立定义");

        var indexPath = Path.Combine(store.Folder,NoteDisk.Index);
        var old = JsonNode.Parse(File.ReadAllBytes(indexPath))!["Rows"]!.AsArray();
        foreach (var row in old) row!.AsObject().Remove("Tags");
        NoteDisk.Atomic(indexPath,System.Text.Encoding.UTF8.GetBytes(old.ToJsonString()),true);
        store = Open(source);
        await store.Completion;
        Check(store.Find(a.Id)?.Text=="外部修改" && store.All.All(n=>n.Tags.Count==0) && store.Tags.Count==0,"旧数组索引升级为空标签");
        Console.WriteLine("PASS 144 标签管理、检索、外部改名、旧索引、备份导入与合并");
    }
}
