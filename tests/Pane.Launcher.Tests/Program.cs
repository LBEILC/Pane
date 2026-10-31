using System.Diagnostics;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static int checks;
    static void Check(bool condition,string label){checks++;if(!condition)throw new Exception(label);}
    static PaneSettings Defaults()=>new(1,new(false,true,ThemeMode.Dark,"#FF5874C9",20,1,new(.2,.2,1,1,1,0),new(.2,.2,1,1,1,0)),new(new(KeyModifiers.Ctrl,32)),new(new(KeyModifiers.Ctrl,86)));
    static async Task Main(string[] args)
    {
        if(args.FirstOrDefault()=="--launcher-index"){Environment.ExitCode=FixtureWorker(args);return;}
        try{await Run(args);}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
    }
    static async Task Run(string[] args)
    {
        if(args.Contains("--files-performance")){await FilePerformance();return;}
        if(args.Contains("--probe")){await Probe();return;}
        ApplicationNameTests();
        CalculatorBehaviorTests();
        string directory=Path.Combine(AppContext.BaseDirectory,"results",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var defaults=Defaults();var store=new SettingsStore(directory,defaults);int corrupt=0;store.CorruptFile+=(_,_)=>corrupt++;
        var json=JsonSerializer.Serialize(defaults,SettingsJson.Default.PaneSettings);
        using(var document=JsonDocument.Parse(json)){
            var old="{"+string.Join(',',document.RootElement.EnumerateObject().Select(p=>"\""+p.Name+"\":"+(p.Name=="Launcher"?"{\"Hotkey\":{\"Modifiers\":\"Ctrl\",\"VirtualKey\":32}}":p.Value.GetRawText())))+"}";
            File.WriteAllText(Path.Combine(directory,"settings.json"),old);
        }
        store.Load();var l=store.Current.Launcher;
        Check(corrupt==0&&l.HideOnBlur&&l.KeepInput==KeepInputMode.Clear&&l.AutoAttachSeconds==3&&l.FileResultCount==8&&l.Favorites.Length==0&&l.PinnedApps.Length==0&&l.SearchEngines.Length==5&&l.SearchEngines[1].Keyword=="g"&&l.DefaultEngine=="bing","old settings defaults without recovery");
        // issue 8：存过的搜索站点和旧默认（4 个）完全一样 → 补上 Google；改过的不动
        SearchEngine[] oldDefault=[new("必应","bing","https://www.bing.com/search?q={query}"),new("百度","bd","https://www.baidu.com/s?wd={query}"),new("GitHub","gh","https://github.com/search?q={query}"),new("哔哩哔哩","b","https://search.bilibili.com/all?keyword={query}")];
        Check((l with{SearchEngines=oldDefault}).SearchEngines.Length==5&&(l with{SearchEngines=oldDefault[..3]}).SearchEngines.Length==3,"old default engines gain Google, custom lists kept");
        store.Save(defaults with{Launcher=l with{HideOnBlur=false,KeepInput=KeepInputMode.Always,AutoAttachSeconds=0,FileResultCount=12,Favorites=[new(@"C:\测试\银行.txt",false)],PinnedApps=["app:2","app:1"],DefaultEngine="gh"}});store.Load();
        Check(!store.Current.Launcher.HideOnBlur&&store.Current.Launcher.KeepInput==KeepInputMode.Always&&store.Current.Launcher.FileResultCount==12&&store.Current.Launcher.Favorites.Length==1&&store.Current.Launcher.DefaultEngine=="gh","new fields roundtrip");
        foreach(var invalid in new[]{l with{FileResultCount=7},l with{AutoAttachSeconds=2},l with{DefaultEngine="missing"},l with{SearchEngines=[new("bad","x","file:///x?{query}")]}}){try{store.Save(defaults with{Launcher=invalid});throw new Exception("accepted invalid settings");}catch(ArgumentException){checks++;}}
        var fake=new FakeExecutor();var files=new FakeFiles();using var service=new LauncherSearch(store,new FixtureIndex(new FakeCatalog()),files,fake);await service.Indexing;
        Check(service.IndexReady&&service.IndexError==null,"index ready");
        await OriginalApplicationTests(store,service,files,fake);
        await OriginalFavoriteTests(service);
        await OriginalWebTests(service);
        await FileWindowTests();
        await OriginalCalculationTests(service,fake);
        await OriginalAiTests(service);
        await OriginalCommandTests(service,fake);
        Check((await service.SearchAsync("剪贴板",default)).Items.Any(i=>i.Id=="command:clipboard"),"clipboard command enabled");
        store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Enabled=false}});
        Check(!(await service.SearchAsync("剪贴板",default)).Items.Any(i=>i.Id=="command:clipboard"),"clipboard command disabled immediately");
        store.Save(store.Current with{Clipboard=store.Current.Clipboard with{Enabled=true}});
        Check((await service.SearchAsync("剪贴板",default)).Items.Any(i=>i.Id=="command:clipboard"),"clipboard command reenabled immediately");
        LauncherResult result;
        var clip=new ClipEntry("fixture",ClipKind.Files,null,[@"C:\fixture\one.txt",@"C:\fixture\two.txt"],null,null,null,"test",null,DateTimeOffset.UtcNow,false);
        await service.RunAsync("open",clip,default);Check(fake.Calls.TakeLast(2).SequenceEqual([@"open:C:\fixture\one.txt:False",@"open:C:\fixture\two.txt:False"]),"all attached files open through fake");
        await service.RunAsync("reveal",clip,default);Check(fake.Calls.TakeLast(2).SequenceEqual([@"reveal:C:\fixture\one.txt",@"reveal:C:\fixture\two.txt"]),"all attached files reveal through fake");
        await service.RunAsync("copy-path",clip,default);Check(fake.Calls.Last()=="copy:"+string.Join(Environment.NewLine,clip.Files!),"copy full paths through fake");
        await service.RunAsync("web-search",clip with{Kind=ClipKind.Text,Text="a b",Files=null},default);Check(fake.Calls.Last().Contains("a%20b"),"text web attachment");
        foreach(var action in new[]{"save-image","copy-image"})try{await service.RunAsync(action,clip with{Kind=ClipKind.Image},default);throw new Exception("image accepted");}catch(NotSupportedException){checks++;}
        int before=files.Calls;await service.SearchAsync("a",default);Check(files.Calls==before,"one char no files");await service.SearchAsync("ab",default);Check(files.Calls==before+1&&files.LastLimit==3,"file result count");
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await service.SearchAsync("wx",cancelled.Token);throw new Exception("no cancellation");}catch(OperationCanceledException){checks++;}}
        store.Save(store.Current with{Launcher=store.Current.Launcher with{Favorites=[new(@"C:\new\记事本.txt",false)]}});await service.Indexing;Check((await service.SearchAsync("jsb",default)).Items.Any(i=>i.Group=="常用"),"settings refresh favorites");
        store.Save(store.Current with{Launcher=store.Current.Launcher with{Favorites=Enumerable.Range(0,5).Select(i=>new FavoriteEntry(@"C:\new\微信"+i+".txt",false)).ToArray()}});await service.Indexing;
        files.ReturnRow=true;result=await service.SearchAsync("wx",default);Check(result.FileTotal==234,"launcher file total");Check(result.Items.Count(i=>i.Group=="常用")==3,"favorite cap");
        string[] groups=["计算","应用","常用","文件","命令","AI","网页"];var order=result.Items.Select(i=>Array.IndexOf(groups,i.Group)).ToArray();Check(order.SequenceEqual(order.Order()),"group order");
        files.Delay=1000;using(var cancelled=new CancellationTokenSource(20)){var timer=Stopwatch.StartNew();try{await service.SearchAsync("wx",cancelled.Token);throw new Exception("no in-flight cancellation");}catch(OperationCanceledException){Check(timer.ElapsedMilliseconds<200,"in-flight cancellation prompt");}}files.Delay=0;
        var perfStore=new SettingsStore(Path.Combine(directory,"performance"),defaults);using var perf=new LauncherSearch(perfStore,new FixtureIndex(new FakeCatalog(1000)),new FakeFiles(),fake);await perf.Indexing;
        string[] queries=["wx","weixin","app","test","not-found","应用","999","1+2","gh 中文","abc"];
        Check((await perf.SearchAsync("app",default)).Items.Count(i=>i.Kind==LauncherItemKind.App)==14,"application cap");
        foreach(var query in queries)await perf.SearchAsync(query,default);
        var timings=new List<double>();for(int i=0;i<100;i++){var timer=Stopwatch.StartNew();await perf.SearchAsync(queries[i%queries.Length],default);timings.Add(timer.Elapsed.TotalMilliseconds);}
        timings.Sort();Console.WriteLine($"search apps=1000 samples=100 medianMs={timings[50]:F4} maxMs={timings[^1]:F4}");Check(timings[^1]<=15,"1000 app latency");
        Console.WriteLine($"PASS original launcher assertions={checks}");
        await SourceTests();await IndexTests();await IconTests();
        Console.WriteLine($"PASS launcher assertions={checks};realExecutions=0;clipboardWrites=0;browserLaunches=0");
    }
    static async Task Probe()
    {
        var timer=Stopwatch.StartNew();var table=new PinyinTable();var index=new WindowsAppCatalog(Path.Combine(AppContext.BaseDirectory,"results","probe")).Build(table,default);var m=index.Metrics;
        Console.WriteLine($"catalog appsFolder={m.AppsFolder};shortcuts={m.Shortcuts};filtered={m.Filtered};duplicates={m.Duplicates};total={m.Total};acceptedShell={m.AcceptedAppsFolder};acceptedLinks={m.AcceptedShortcuts};icons={m.Icons};indexMs={m.Milliseconds:F2};withTableMs={timer.Elapsed.TotalMilliseconds:F2}");
        foreach(var (hr,count) in m.IconFailures)Console.WriteLine($"iconFailure={hr:X8};count={count}");
        var files=new EverythingSearch();await files.RefreshAsync(default);Console.WriteLine($"everything state={files.State};version={files.Version}");timer.Restart();var rows=await files.SearchAsync("pane",8,default);Console.WriteLine($"everything count={rows.Count};queryMs={timer.Elapsed.TotalMilliseconds:F2};state={files.State}");
        if(index.Apps.Length==0||files.State!=FileSearchState.Ready||rows.Count==0)Environment.ExitCode=1;
    }
    sealed class FakeExecutor:ILauncherExecutor
    {
        public List<string> Calls=[];
        public Task OpenAsync(string t,bool p,CancellationToken c){c.ThrowIfCancellationRequested();Calls.Add($"open:{t}:{p}");return Task.CompletedTask;}
        public Task RevealAsync(string t,CancellationToken c){Calls.Add("reveal:"+t);return Task.CompletedTask;}
        public Task CopyAsync(string t,CancellationToken c){Calls.Add("copy:"+t);return Task.CompletedTask;}
        public Task CommandAsync(string t,CancellationToken c){Calls.Add("command:"+t);return Task.CompletedTask;}
    }
    sealed class FakeFiles:ILauncherFiles
    {
        public int Calls,LastLimit,Delay;public bool ReturnRow;
        public async Task<IReadOnlyList<LauncherItem>> SearchAsync(string q,int limit,CancellationToken token){Calls++;LastLimit=limit;if(Delay>0)await Task.Delay(Delay,token);return ReturnRow?new LauncherFileRows([new("file:C:\\fixture.txt",LauncherItemKind.File,"文件","fixture.txt",@"C:\",new("doc"))],234):[];}
    }
    sealed class FixtureIndex(IAppCatalog catalog):ILauncherIndex {
        public PersistedIndex? Load()=>null;
        public Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token)=>Task.FromResult(IndexBuilder.Build(catalog,favorites,token));
    }
    sealed class FakeCatalog(int count=2):IAppCatalog
    {
        public AppIndex Build(PinyinTable table,CancellationToken token){var apps=Enumerable.Range(1,count).Select(i=>new IndexedApp("app:"+i,table.Create(i<=2?"微信":"应用 App test "+i),"launch:"+i,false,null)).ToArray();return new(apps,new(count,0,0,0,count,0,0));}
    }
}
