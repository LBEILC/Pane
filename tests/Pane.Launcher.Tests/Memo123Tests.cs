using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Services.Memo;
using Pane.Settings;
using System.Text;

static partial class Program
{
    static async Task Memo123Tests()
    {
        string root = TestResults.NewDirectory("123-");
        string folder = Path.Combine(root,"备忘录");
        var recycled = new List<string>();
        var location = new LargeFilesLocation(Path.Combine(root,"clipboard"),true,null,false);
        var notes = new NoteStore(() => NoteDisk.FolderFor(root,location),new object(),p => { recycled.Add(p); File.Move(p,Path.Combine(root,"回收站.md")); });
        await notes.Completion;
        var first = notes.Add("# 无线:网络\nWiFi 密码");
        await notes.Completion;
        var row = NoteDisk.ReadIndex(folder).Single();
        string path = Path.Combine(folder,row.FileName);
        Check(row.Id == first.Id && row.FileName == $"无线网络 {first.Id[..8]}.md" && NoteDisk.ValidId(row.Id)
            && File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes(first.Text)) &&
            row.Created == first.Created && row.Modified == first.Modified && !row.Pinned && row.From == null,"新建正文、文件名与索引");
        var changed = notes.Update(first.Id,"# 无线网络\n" + new string('a',60) + "WiFi" + new string('b',50))!;
        var twin = notes.Add(changed.Text);
        notes.Update(twin.Id,"# WiFi\n另一条");
        var third = notes.Add("# WiFi\n新建");
        await notes.Completion;
        var files = NoteDisk.ReadIndex(folder);
        Check(files.Length == 3 && files.Select(r=>r.FileName).Distinct().Count()==3 &&
            files.Count(r=>r.FileName.StartsWith("WiFi "))==2,"同标题独立文件");
        var renamed = notes.Update(first.Id,changed.Text.Replace("无线网络","网络配置"))!;
        await notes.Completion;
        var current = NoteDisk.ReadIndex(folder).Single(r=>r.Id==first.Id);
        Check(renamed.Id==first.Id && !File.Exists(path) && current.FileName.StartsWith("网络配置 "),"改标题保留 id");
        var hits = notes.Search("wifi",10);
        Check(hits.Count==3 && hits[0].TitleHighlights!=null && hits[^1].Snippet==new string('a',40)+"WiFi"+new string('b',40) &&
            hits[^1].SnippetHighlights!.Single()==new TextSpan(40,4),"标题优先、正文片段和高亮");
        Check(notes.Search("wlpz",1).Single().Note.Id==first.Id && notes.Search("wangluopeizhi",1).Single().Note.Id==first.Id,"拼音首字母与全拼");

        notes.Add("wifi 第四条");
        var settings = new SettingsStore(root,Defaults());
        using var search = new LauncherSearch(settings,new Index076([]),new Files121(""),new FakeExecutor(),new Environment037(),new Process037(),actionPlatform:new ActionPlatform());
        search.NoteProvider=()=>notes;
        await search.Indexing;
        var items=(await search.SearchAsync("wifi",default)).Items.Where(i=>i.Group==LauncherGroups.Notes).ToArray();
        Check(items.Length==3 && items.All(i=>i.Kind==LauncherItemKind.Note && i.Id.StartsWith("note:")),"启动器备忘录三条");
        await search.ExecuteAsync(items[0],default);
        Check((await search.SearchAsync("备忘录",default)).Items.Any(i=>i.Id=="command:memo"),"备忘录命令");
        search.NoteProvider=()=>null;
        Check(!(await search.SearchAsync("wifi",default)).Items.Any(i=>i.Group==LauncherGroups.Notes) &&
            !(await search.SearchAsync("备忘录",default)).Items.Any(i=>i.Id=="command:memo"),"能力停用");

        await notes.Completion;
        path=Path.Combine(folder,current.FileName);
        File.WriteAllText(path,"外部编辑");
        File.WriteAllText(Path.Combine(folder,"来自编辑器.md"),"外部新建");
        File.Delete(Path.Combine(folder,files.Single(r=>r.Id==third.Id).FileName));
        notes.Refresh(); await notes.Completion;
        var external=notes.All.Single(n=>n.Text=="外部新建");
        Check(notes.All.Single(n=>n.Id==first.Id) is var edited && edited.Text=="外部编辑" && edited.Modified==File.GetLastWriteTimeUtc(path) &&
            notes.All.All(n=>n.Id!=third.Id),"刷新对上外部修改、新增和删除");
        notes.Delete(external.Id); await notes.Completion;
        Check(recycled.Count==1 && !File.Exists(recycled[0]),"删除走假回收站");
        notes.Restore(external); await notes.Completion;
        Check(notes.All.Contains(external) && NoteDisk.ReadIndex(folder).Any(r=>r.Id==external.Id && r.Created==external.Created),"按原 id 和时间撤销删除");
        var texts=notes.All.Select(n=>n.Text).Order().ToArray();
        File.Delete(Path.Combine(folder,NoteDisk.Index));
        notes.Refresh(); await notes.Completion;
        Check(notes.All.Select(n=>n.Text).Order().SequenceEqual(texts) && NoteDisk.ReadIndex(folder).Length==texts.Length,"索引重建保留正文");
    }
}
