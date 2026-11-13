using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static async Task Recent090Tests()
    {
        int before=checks;
        // 同名应用用于验证按 Id 移除，以及使用次数与时间不再影响排序。
        foreach(bool merged in new[]{false,true}){
            string dir=TestResults.NewDirectory("090-");Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,"launcher-usage.json");
            var now=DateTimeOffset.UtcNow;
            var uses=new Dictionary<string,AppUse>{[merged?"old":"app:2"]=new(7,now),["app:1"]=new(1,now.AddDays(-1))};
            File.WriteAllText(path,JsonSerializer.Serialize(uses,LauncherJson.Default.DictionaryStringAppUse));
            var table=new PinyinTable();
            var apps=Enumerable.Range(1,3).Select(i=>new IndexedApp("app:"+i,table.Create("测试应用"),"launch:"+i,false,null)).ToArray();
            var value=new PersistedIndex(IndexStorage.Version,new(apps,[],[]),new(3,0,0,0,3,0,0){Aliases=merged?new(){["old"]="app:2"}:[]},now);
            var store=new SettingsStore(dir,Defaults());
            store.Save(Defaults() with{Launcher=Defaults().Launcher with{PinnedApps=["app:2"],AppAliases=[new("app:2","测试应用",["fixture"])]}});
            var executor=new FakeExecutor();
            using(var search=new LauncherSearch(store,new CachedIconIndex(value),new FakeFiles(),executor,actionPlatform:new ActionPlatform())){
                await search.Indexing;
                var rows=(await search.SearchAsync("测试应用",default)).Items.Where(i=>i.Kind==LauncherItemKind.App).ToArray();
                var used=rows.Single(i=>i.Id=="app:2");var unused=rows.Single(i=>i.Id=="app:3");
                Check(rows[0].Id==used.Id,"090 使用记录提高同名应用排序");
                Check((await search.GetActionsAsync(used,default)).Where(a=>a!=LauncherItemAction.HideFromSearch).Last()==LauncherItemAction.RemoveFromRecent,"090 用过的应用末尾提供移除，含合并 Id");
                Check(!(await search.GetActionsAsync(unused,default)).Contains(LauncherItemAction.RemoveFromRecent),"090 同名未用应用不提供移除");
                foreach(var kind in new[]{LauncherItemKind.File,LauncherItemKind.Folder,LauncherItemKind.Command})
                    Check(!(await search.GetActionsAsync(used with{Kind=kind},default)).Contains(LauncherItemAction.RemoveFromRecent),"090 非应用不提供移除 "+kind);
                Check((await search.SearchAsync("",default)).Items.Any(i=>i.Id==used.Id&&i.Group==LauncherGroups.Recent),"090 移除前首页最近使用存在");
                await search.RunAsync(used,LauncherItemAction.RemoveFromRecent,default);
                var saved=JsonSerializer.Deserialize(File.ReadAllText(path),LauncherJson.Default.DictionaryStringAppUse)!;
                Check(saved.Count==1&&saved["app:1"]==uses["app:1"],"090 整条使用记录写盘删除且其他记录不变");
                Check(!(await search.GetActionsAsync(used,default)).Contains(LauncherItemAction.RemoveFromRecent),"090 内存记录立即删除");
                var home=await search.SearchAsync("",default);
                Check(!home.Items.Any(i=>i.Id==used.Id&&i.Group==LauncherGroups.Recent),"090 首页最近使用立即移除");
                Check(home.Items.Any(i=>i.Id==used.Id&&i.Group==LauncherGroups.Pinned),"090 已固定保留");
                rows=(await search.SearchAsync("测试应用",default)).Items.Where(i=>i.Kind==LauncherItemKind.App).ToArray();
                Check(rows.Length==3&&rows[0].Id=="app:1","090 索引保留且移除后无使用排序加成");
                Check(((IAppAliases)search).Get(used.Id).SequenceEqual(["fixture"]),"090 别名保留");
                Check(executor.Calls.Count==0,"090 移除不调用应用执行器");
            }
            var reloaded=new SettingsStore(dir,Defaults());reloaded.Load();
            using(var search=new LauncherSearch(reloaded,new CachedIconIndex(value),new FakeFiles(),executor,actionPlatform:new ActionPlatform())){
                await search.Indexing;
                var home=await search.SearchAsync("",default);
                Check(!home.Items.Any(i=>i.Id=="app:2"&&i.Group==LauncherGroups.Recent),"090 重载并重新合并后记录仍不存在");
                var pinned=home.Items.Single(i=>i.Id=="app:2"&&i.Group==LauncherGroups.Pinned);
                Check(!(await search.GetActionsAsync(pinned,default)).Contains(LauncherItemAction.RemoveFromRecent),"090 重载后固定项无使用记录");
            }
        }
        Console.WriteLine($"090 assertions={checks-before};realExecutions=0");
    }
}
