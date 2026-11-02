using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class Environment037:ILauncherEnvironment
    {
        public string Home=>@"D:\测试";public bool HibernateAvailable {get;set;}=true;
        public HashSet<string> Missing=new(StringComparer.OrdinalIgnoreCase);
        public string? FindProgram(string name)=>Missing.Contains(name)?null:Path.Combine(@"C:\Windows\System32",name);
    }
    sealed class Process037:ILaunchProcess
    {
        public List<LaunchRequest> Requests=[];public Exception? Error;
        public Task StartAsync(LaunchRequest request,CancellationToken token){token.ThrowIfCancellationRequested();if(Error!=null)throw Error;Requests.Add(request);return Task.CompletedTask;}
    }
    sealed class Http037:HttpMessageHandler
    {
        public int Calls;public Uri? Url;public string Body="[\"test\",[\"one\",\"two\"]]";public HttpStatusCode Status=HttpStatusCode.OK;public bool Delay,Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;Url=request.RequestUri;Check(!request.Headers.Contains("Cookie")&&!request.Headers.Contains("Authorization"),"建议不发送身份信息");if(Delay)await Task.Delay(5000,token);if(Fail)throw new HttpRequestException();return new(Status){Content=new StringContent(Body)};}
    }
    sealed class Clock037:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    static async Task SystemTerminalTests(bool live=false)
    {
        string directory=Path.Combine(AppContext.BaseDirectory,"results","037-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var store=new SettingsStore(directory,Defaults());var platform=new Environment037();var process=new Process037();var executor=new FakeExecutor();var files=new FakeFiles{ReturnRow=true};
        try{WindowsLauncherExecutor.CheckResult(-1223);Check(false,"系统组件 UAC 取消");}catch(System.ComponentModel.Win32Exception e){Check(e.NativeErrorCode==1223,"系统组件保留 UAC 取消码");}
        var programs=new TerminalPrograms([], ["python","powershell","ping","pwsh","print","pip","pydoc","pytest"],false);
        using var service=new LauncherSearch(store,new FixtureIndex(new FakeCatalog()),files,executor,platform,process,programs);await service.Indexing;
        Check(SystemSettingsCatalog.All.Length is >=150 and <=250,"清单数量");
        Check(SystemSettingsCatalog.All.Select(e=>e.Key).Distinct().Count()==SystemSettingsCatalog.All.Length,"清单标识唯一");
        foreach(var entry in SystemSettingsCatalog.All)Check(entry.Title.Length>0&&entry.English.Length>0&&entry.RequiredFile.Length>0&&entry.Glyph=="gear","清单字段:"+entry.Key);
        foreach(var query in new[]{"环境变量","系统变量","PATH","huanjingbianliang","hjbl","Environment Variables"})Check((await service.SearchAsync(query,default)).Items.Any(i=>i.Id=="system:environment"),"系统设置匹配:"+query);
        var env=(await service.SearchAsync("PATH",default)).Items.First(i=>i.Id=="system:environment");await service.ExecuteAsync(env,default);
        Check(process.Requests[^1].Arguments.SequenceEqual(["sysdm.cpl,EditEnvironmentVariables"])&&!process.Requests[^1].Elevated,"环境变量参数");
        var display=(await service.SearchAsync("显示设置",default)).Items.First(i=>i.Id=="system:display");await service.ExecuteAsync(display,default);Check(executor.Calls[^1]=="open:ms-settings:display:False","URI 走现有执行器");
        Check(File.ReadAllText(Path.Combine(directory,"launcher-usage.json")).Contains("system:display"),"设置记录使用次数");
        var source=new SystemSettingsSource(executor,process,platform,_=>{});var context=new SourceContext("privacy",LauncherSnapshot.Empty,store.Current.Launcher,new Dictionary<string,AppUse>{{"system:privacy-videos",new(99,DateTimeOffset.UtcNow)}});
        var ranked=await source.SearchAsync(context,default);Check(ranked.First().Id=="system:privacy","匹配分数优先于使用次数");
        context=context with{Query="权限"};ranked=await source.SearchAsync(context,default);Check(ranked.First().Id=="system:privacy-videos","同分按使用次数排序");
        var table=new PinyinTable();var app=new IndexedApp("app:display",table.Create("显示设置"),"fake",false,null);
        var appContext=context with{Query="显示设置",Snapshot=new([app],[],[])};var appRows=await new ApplicationSource(executor,_=>{}).SearchAsync(appContext,default);
        var rows=(await service.SearchAsync("显示设置",default)).Items;Check(rows.FindIndex(i=>i.Group==LauncherGroups.Settings)<rows.FindIndex(i=>i.Group==LauncherGroups.Files),"设置在文件之前");Check(new ApplicationSource(executor,_=>{}).Order<source.Order&&appRows.Count==1,"应用在设置之前");
        platform.Missing.Add("gpedit.msc");var unavailable=new SystemSettingsSource(executor,process,platform,_=>{});Check(!(await unavailable.SearchAsync(context with{Query="组策略"},default)).Any(i=>i.Id=="system:gpedit.msc"),"过滤缺失组件");
        foreach(var query in new[]{"注销","zhuxiao","zx","logoff","sign out"})Check((await service.SearchAsync(query,default)).Items.Any(i=>i.Id=="command:logoff"),"注销匹配:"+query);
        foreach(var query in new[]{"休眠","xiumian","xm","hibernate"})Check((await service.SearchAsync(query,default)).Items.Any(i=>i.Id=="command:hibernate"),"休眠匹配:"+query);
        platform.HibernateAvailable=false;Check((await new ExtraCommandSource(executor,platform).SearchAsync(context with{Query="休眠"},default)).Count==0,"过滤不可用休眠");
        foreach(var id in new[]{"command:logoff","command:hibernate"}){await service.ExecuteAsync(new(id,LauncherItemKind.Command,LauncherGroups.Commands,"","",new("moon")),default);Check(executor.Calls[^1]==id,"系统命令假执行器:"+id);}
        foreach(var q in new[]{">","> ",">   "})Check((await service.SearchAsync(q,default)).Items[0].Id=="terminal:hint","空命令:"+q);
        var hint=(await service.SearchAsync(">",default)).Items[0];int before=process.Requests.Count;await service.ExecuteAsync(hint,default);Check(process.Requests.Count==before,"提示项不执行");
        string command="echo \"a b\" & echo %PATH% | find \"a\"";
        var terminal=(await service.SearchAsync(">"+command,default)).Items[0];Check(terminal.Subtitle==command&&terminal.Group==LauncherGroups.Terminal&&terminal.Hotkey=="Enter","终端项契约");
        before=files.Calls;var result=await service.SearchAsync(">py",default);Check(files.Calls==before&&result.Items.All(i=>i.Group is LauncherGroups.Terminal or LauncherGroups.Completion),"终端不查其他来源");Check(result.Items.Any(i=>i.Title=="python")&&result.Items.Count<=7,"程序名前缀和数量上限");
        Check((await service.SearchAsync(">python a",default)).Items.Count==1,"只补第一词");
        await service.ExecuteAsync(terminal,default);var request=process.Requests[^1];Check(request.FileName.EndsWith("wt.exe")&&request.Arguments.SequenceEqual(["-w","0","new-tab","-d",platform.Home,"--",@"C:\Windows\System32\cmd.exe","/k",command])&&request.WorkingDirectory==platform.Home&&!request.Elevated,"wt 参数数组与原始命令");
        platform.Missing.Add("wt.exe");await service.ExecuteElevatedAsync(terminal,default);request=process.Requests[^1];Check(request.FileName.EndsWith("cmd.exe")&&request.Arguments.SequenceEqual(["/k",command])&&request.Elevated,"cmd 管理员参数");
        platform.Missing.Remove("wt.exe");await service.ExecuteElevatedAsync(terminal,default);Check(process.Requests[^1].Elevated&&process.Requests[^1].FileName.EndsWith("wt.exe")&&process.Requests[^1].Arguments[1]=="new","wt 管理员独立窗口分支");
        var spaced=(await service.SearchAsync("> secret",default)).Items[0];Check(spaced.Subtitle=="secret","前导空格解析");await service.ExecuteAsync(spaced,default);
        var history=(TerminalHistory)service.TerminalHistory;Check(history.Match("secret").Length==0,"前导空格不保存");
        history.Clear();for(int i=0;i<25;i++)history.Record("echo "+i);Check(history.Match("").Length==20&&history.Match("")[0]=="echo 24"&&!history.Match("").Contains("echo 0"),"最近20条");history.Record("echo 20");Check(history.Match("")[0]=="echo 20"&&history.Match("").Count(s=>s=="echo 20")==1,"历史去重移至最前");
        Check(new TerminalHistory(store).Match("").SequenceEqual(history.Match("")),"历史持久化");
        var completion=(await service.SearchAsync(">echo 2",default)).Items[1];Check(completion.Id=="complete:echo 20"&&completion.Kind==LauncherItemKind.Action,"补全顺序和契约");await service.ExecuteAsync(completion,default);Check(process.Requests[^1].Arguments[^1]=="echo 20","补全直接执行");
        store.Save(store.Current with{Launcher=store.Current.Launcher with{RememberCommands=false}});history.Record("private");Check(history.Match("").Length==0&&(await service.SearchAsync(">echo",default)).Items.Count==1,"关闭记录不补历史");Check((await service.SearchAsync(">py",default)).Items.Count>1,"关闭历史仍补程序");
        history.Clear();store.Save(store.Current with{Launcher=store.Current.Launcher with{RememberCommands=true}});Check(new TerminalHistory(store).Match("").Length==0,"Clear 持久化");
        process.Error=new System.ComponentModel.Win32Exception(1223);try{await service.ExecuteElevatedAsync(terminal,default);Check(false,"应保留 UAC 错误码");}catch(System.ComponentModel.Win32Exception e){Check(e.NativeErrorCode==1223&&history.Match("").Length==0,"取消UAC不记历史，宿主静默");}process.Error=null;
        var path=Path.Combine(directory,"path");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"python.exe"),"");File.WriteAllText(Path.Combine(path,"ignore.txt"),"");using(var discovered=new TerminalPrograms([path],["custom"],true)){
            Check(discovered.Match("py").SequenceEqual(["python"]),"PATH 真实假目录扫描");Check(discovered.Match("ignore").Length==0&&discovered.Match("custom").Length==1,"过滤非程序并合并 App Paths");
            File.WriteAllText(Path.Combine(path,"newtool.exe"),"");var deadline=Stopwatch.StartNew();while(discovered.Match("newtool").Length==0&&deadline.ElapsedMilliseconds<2000)await Task.Delay(10);Check(discovered.Match("newtool").Length==1,"目录通知增加程序");
            File.Delete(Path.Combine(path,"newtool.exe"));deadline.Restart();while(discovered.Match("newtool").Length!=0&&deadline.ElapsedMilliseconds<2000)await Task.Delay(10);Check(discovered.Match("newtool").Length==0,"目录通知删除程序");
        }
        var bing=new SearchEngine("必应","bing","https://www.bing.com/search?q={query}");var baidu=new SearchEngine("百度","bd","https://www.baidu.com/s?wd={query}");var http=new Http037();var clock=new Clock037();bool enabled=true;
        using(var web=new WebSuggestions(()=>enabled,http,clock)){
            Check((await web.SuggestAsync(bing,"天气",default)).SequenceEqual(["one","two"]),"必应解析");Check(http.Url!.Host=="api.bing.com"&&http.Url.Query.Contains("%E5"),"查询编码和固定端点");await web.SuggestAsync(bing,"天气",default);Check(http.Calls==1,"一分钟内缓存");clock.Now+=TimeSpan.FromSeconds(61);await web.SuggestAsync(bing,"天气",default);Check(http.Calls==2,"缓存过期");
            Check((await web.SuggestAsync(baidu,"天气",default)).Count==2&&http.Url!.Host=="suggestion.baidu.com","百度解析和站点隔离");
            http.Body="[\"many\",[\"0\",\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\"]]";Check((await web.SuggestAsync(bing,"many",default)).SequenceEqual(["0","1","2","3","4","5","6","7"]),"最多8条并保留站点顺序");
            before=http.Calls;await web.SuggestAsync(new("未知","x","https://example.com?q={query}"),"x",default);enabled=false;await web.SuggestAsync(bing,"disabled",default);Check(http.Calls==before,"未知站点和关闭开关不联网");enabled=true;
            http.Body="bad";Check((await web.SuggestAsync(bing,"bad",default)).Count==0,"损坏响应返回空");http.Status=HttpStatusCode.InternalServerError;Check((await web.SuggestAsync(bing,"500",default)).Count==0,"HTTP 失败为空");http.Status=HttpStatusCode.OK;http.Fail=true;Check((await web.SuggestAsync(bing,"network",default)).Count==0,"网络失败为空");http.Fail=false;http.Delay=true;var timer=Stopwatch.StartNew();Check((await web.SuggestAsync(bing,"timeout",default)).Count==0&&timer.ElapsedMilliseconds<1500,"一秒超时返回空");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();before=http.Calls;Check((await web.SuggestAsync(bing,"cancel",cancelled.Token)).Count==0&&http.Calls==before,"已取消不请求");
        }
        Console.WriteLine($"catalog total={SystemSettingsCatalog.All.Length};"+string.Join(';',SystemSettingsCatalog.All.GroupBy(e=>e.Category).Select(g=>$"{g.Key}={g.Count()}")));
        foreach(var query in new[]{"wx","计算器","chrome","设置","notepad.exe"}){
            for(int i=0;i<5;i++)await service.SearchAsync(query,default);var times=new List<double>();for(int i=0;i<20;i++){var timer=Stopwatch.StartNew();await service.SearchAsync(query,default);times.Add(timer.Elapsed.TotalMilliseconds);}times.Sort();Console.WriteLine($"service query={query};n=20;medianMs={(times[9]+times[10])/2:F3};p90Ms={times[17]:F3}");
        }
        if(live){using var web=new WebSuggestions(()=>true);foreach(var engine in new[]{bing,baidu}){var timer=Stopwatch.StartNew();var suggestions=await web.SuggestAsync(engine,"天气",default);Console.WriteLine($"live site={engine.Keyword};count={suggestions.Count};ms={timer.Elapsed.TotalMilliseconds:F1}");}}
        Console.WriteLine($"PASS 037 assertions={checks};realExecutions=0;clipboardReads=0");
    }
}

static class Rows037
{
    public static int FindIndex(this IReadOnlyList<LauncherItem> rows,Func<LauncherItem,bool> predicate){for(int i=0;i<rows.Count;i++)if(predicate(rows[i]))return i;return -1;}
}
