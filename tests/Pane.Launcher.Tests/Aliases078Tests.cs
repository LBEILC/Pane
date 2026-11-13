using System.Reflection;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class Index078(PersistedIndex value):ILauncherIndex
    {
        public PersistedIndex? Load()=>value;
        public Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token)=>Task.FromResult(value);
    }
    static void Publish078(LauncherSearch search,PersistedIndex value)=>typeof(LauncherSearch).GetMethod("Publish",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(search,[value]);

    static async Task Aliases078Tests()
    {
        int before=checks;var table=new PinyinTable();
        IndexedApp App(string id,string name,string exe,string? description=null,bool packaged=false)=>new(id,table.Create(name),exe,packaged,null){
            Target=exe,SecondaryNames=SecondaryAppNames.Collect(table,name,exe,packaged,_=>description)};
        IndexedApp[] apps=[App("app:exact","OW Alpha",@"C:\Fixture\Overwatch.exe"),App("app:prefix","Beta",@"C:\Fixture\Beta.exe"),
            App("app:display","OW",@"C:\Fixture\Display.exe"),App("app:secondary","守望先锋",@"C:\Fixture\Overwatch.exe","OW"),
            App("app:discord","Discord",@"C:\Fixture\Update.exe","Updater"),App("app:store","Store",@"C:\Fixture\Overwatch.exe","OW",true)];
        var value=new PersistedIndex(IndexStorage.Version,new(apps,[],[]),new(0,0,0,0,apps.Length,0,0),DateTimeOffset.UtcNow);
        string root=TestResults.NewDirectory("078-");
        var store=new SettingsStore(root,Defaults());
        using var search=new LauncherSearch(store,new Index078(value),new FakeFiles(),new FakeExecutor(),actionPlatform:new ActionPlatform());
        await search.Indexing;IAppAliases aliases=search;int changed=0;aliases.Changed+=(_,_)=>changed++;
        Check(aliases.Get("absent").Count==0&&aliases.All().Count==0,"078 初始别名为空");
        aliases.Set("app:exact",[" OW ","ow","", "\t", "守望"]);
        Check(aliases.Get("app:exact").SequenceEqual(["OW","守望"])&&changed==1,"078 规范化及事件");
        foreach(var invalid in new[]{Enumerable.Range(0,9).Select(i=>"a"+i).ToArray(),new[]{new string('x',33)},new[]{"a b"},new[]{"a\tb"},new[]{"a\nb"},new[]{"a\u3000b"}}){
            try{aliases.Set("app:exact",invalid);Check(false,"078 应拒绝无效别名");}catch(ArgumentException){Check(aliases.Get("app:exact").SequenceEqual(["OW","守望"]),"078 拒绝无效别名且不覆盖");}
        }
        aliases.Set("app:prefix",["OWmore"]);
        var rows=(await search.SearchAsync("ow",default)).Items.Where(i=>i.Kind==LauncherItemKind.App).ToArray();
        Check(rows.Select(i=>i.Id).SequenceEqual(["app:exact","app:prefix","app:display","app:secondary"]),"078 四档顺序");
        Check(rows.Count(i=>i.Id=="app:exact")==1&&rows[0].Id=="app:exact","078 多路命中去重且完全别名第一");
        Check(rows.Last().Highlights?.Count==0,"078 次要名字不高亮标题");
        Check(!(await search.SearchAsync("more",default)).Items.Any(i=>i.Id=="app:prefix"),"078 别名不匹配子串");
        Check(!(await search.SearchAsync("shouwang",default)).Items.Any(i=>i.Id=="app:exact"),"078 别名不转拼音");
        Check((await search.SearchAsync("overwatch",default)).Items.Any(i=>i.Id=="app:secondary"),"078 英文exe命中中文应用");
        Check(!(await search.SearchAsync("update",default)).Items.Any(i=>i.Id=="app:discord"),"078 通用exe和说明不命中");
        Check(apps[^1].SecondaryNames.Length==0,"078 商店应用无次要名字");
        Check(SecondaryAppNames.Collect(table," overwatch ",@"C:\Fixture\Overwatch.exe",false,_=>"OVERWATCH").Length==0,"078 与显示名重复过滤");
        Check(SecondaryAppNames.Collect(table,"其他",@"C:\Fixture\Overwatch.exe",false,_=>" overwatch ").Length==1,"078 次要名字互相去重");
        Check(SecondaryAppNames.Collect(table,"其他",@"C:\Fixture\a.exe",true,_=>throw new Exception()).Length==0,"078 商店不读元数据");
        foreach(string name in new[]{"launcher","update","updater","setup","uninstall","unins000","app","main","start","run","client","helper","install","crash","crashpad","reporter","readme","help","卸载","卸載","帮助","幫助","說明"})
            Check(SecondaryAppNames.Collect(table,"其他",@"C:\Fixture\"+name+".exe",false,_=>name).Length==0,"078 通用名过滤 "+name);
        var chinese=SecondaryAppNames.Collect(table,"其他",@"C:\Fixture\银行工具.exe",false,_=>"Visual Studio Code");
        Check(chinese.Any(n=>n.Match("yhgj")!=null)&&chinese.Any(n=>n.Match("vsc")!=null),"078 次要名字沿用拼音及首字母");
        var reload=new SettingsStore(root,Defaults());reload.Load();
        using(var restarted=new LauncherSearch(reload,new Index078(value),new FakeFiles(),new FakeExecutor())){
            await restarted.Indexing;Check(((IAppAliases)restarted).Get("app:exact").SequenceEqual(["OW","守望"]),"078 重启持久化");
        }
        foreach(var app in apps){
            var item=new LauncherItem(app.Id,LauncherItemKind.App,LauncherGroups.Applications,app.Name.Name,null,new("app"));
            Check((await search.GetActionsAsync(item,default)).Where(a=>a!=LauncherItemAction.HideFromSearch).Last()==LauncherItemAction.EditAliases,"078 所有索引应用菜单末项 "+app.Id);
        }
        foreach(var kind in new[]{LauncherItemKind.File,LauncherItemKind.Folder,LauncherItemKind.Command,LauncherItemKind.App}){
            var item=new LauncherItem(kind==LauncherItemKind.App?"app:missing":@"file:C:\Fixture\test.txt",kind,"","",null,new("doc"));
            Check(!(await search.GetActionsAsync(item,default)).Contains(LauncherItemAction.EditAliases),"078 非索引应用不提供编辑 "+kind);
        }
        try{await search.RunAsync(rows[0],LauncherItemAction.EditAliases,default);Check(false,"078 不执行编辑动作");}catch(NotSupportedException){Check(true,"078 编辑动作留在界面");}
        var iconFile=Path.Combine(root,"fake-icon.png");File.WriteAllText(iconFile,"");
        int events=changed;
        Publish078(search,value with{Snapshot=value.Snapshot with{Apps=[..apps.Select(a=>a.Id=="app:exact"?a with{Name=table.Create("新名字"),DarkIcon=iconFile}:a)]}});
        var info=aliases.All().Single(a=>a.AppId=="app:exact");
        Check(changed>events&&info.Name=="新名字"&&info.IconPath==search.Apps().Single(a=>a.Id==info.AppId).Icon,"078 重建名字图标事件且共用图标");
        Publish078(search,value with{Snapshot=value.Snapshot with{Apps=apps.Where(a=>a.Id!="app:exact").ToArray()}});
        Check(aliases.All().Single(a=>a.AppId=="app:exact") is {Missing:true,Name:"OW Alpha"},"078 缺失保留设置时名字");
        Check(aliases.All().Select(a=>a.Name).SequenceEqual(aliases.All().Select(a=>a.Name).Order(StringComparer.CurrentCultureIgnoreCase)),"078 列表按名称排序");
        var merged=AppSettingsMigration.Apply(store.Current with{Launcher=store.Current.Launcher with{AppAliases=[new("old1","旧一",["OW","One"]),new("old2","旧二",["ow","Two"])]}},new Dictionary<string,string>{["old1"]="new",["old2"]="new"});
        Check(merged.Launcher.AppAliases.Single().Aliases.SequenceEqual(["OW","One","Two"]),"078 合并保留首次大小写并纳入后续别名");
        aliases.Set("app:exact",["OW","a1","a2","a3","a4","a5","a6","a7"]);
        aliases.Set("app:prefix",["ow","extra"]);
        Publish078(search,value with{Snapshot=value.Snapshot with{Apps=[apps[0] with{Id="app:new"}]},Metrics=value.Metrics with{Aliases=new(){["app:exact"]="app:new",["app:prefix"]="app:new"}}});
        Check(aliases.Get("app:new").SequenceEqual(["OW","a1","a2","a3","a4","a5","a6","a7"])&&aliases.Get("app:exact").Count==0&&aliases.All().Count==1,"078 Id合并去重截断及迁移");
        aliases.Set("app:new",[]);Check(aliases.All().Count==0&&!(await search.SearchAsync("a1",default)).Items.Any(i=>i.Kind==LauncherItemKind.App),"078 清除立即生效");
        var source=new ApplicationSource(new FakeExecutor(),_=>{});
        var ranked=await source.SearchAsync(new("ow",value.Snapshot,Defaults().Launcher with{AppAliases=[new("app:exact","",["ow"]),new("app:prefix","",["OW"])]},new Dictionary<string,AppUse>{["app:prefix"]=new(10,DateTimeOffset.UtcNow)}),default);
        Check(ranked[0].Id=="app:prefix"&&ranked[1].Id=="app:exact","078 同档使用频率排序");
        var nullable=JsonSerializer.Deserialize("{\"Hotkey\":{\"Modifiers\":\"Ctrl\",\"VirtualKey\":32},\"AppAliases\":null}",SettingsJson.Default.LauncherSettings)!;
        Check(nullable.AppAliases.Length==0,"078 null设置归空数组");
        string indexPath=Path.Combine(root,"launcher-index.json");IndexStorage.Write(indexPath,value);
        Check(IndexStorage.Read(indexPath).Snapshot.Apps[3].SecondaryNames.Any(n=>n.Match("overwatch")!=null),"078 次要名字快照往返");
        IndexStorage.Write(indexPath,value with{Version=1});var provider=new ProcessLauncherIndex(root);
        Check(provider.Load()==null,"078 旧版索引无错失效");
        using(var rebuilt=new LauncherSearch(new SettingsStore(Path.Combine(root,"rebuild"),Defaults()),provider,new FakeFiles(),new FakeExecutor())){
            await rebuilt.Indexing;Check(rebuilt.IndexReady&&rebuilt.IndexError==null&&IndexStorage.Read(indexPath).Version==IndexStorage.Version,"078 旧索引自动重建");
        }
        Console.WriteLine($"PASS 078 assertions={checks-before}");
    }
}
