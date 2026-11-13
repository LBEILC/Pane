using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static async Task DirectHidden098Tests()
    {
        int before=checks;string root=TestResults.NewDirectory("098-");Directory.CreateDirectory(root);
        var executor=new FakeExecutor();var files=new FakeFiles();var platform=new ActionPlatform();
        var value=IndexBuilder.Build(new FakeCatalog(20),[],default);
        var store=new SettingsStore(root,Defaults());
        using var search=new LauncherSearch(store,new Index078(value),files,executor,new Environment037(),new Process037(),actionPlatform:platform);
        await search.Indexing;
        foreach(var (input,url) in new[]{("https://github.com","https://github.com/"),("HTTP://a.b/c?d=1","http://a.b/c?d=1"),("www.bing.com","https://www.bing.com/"),("  https://github.com  ","https://github.com/")}){
            var item=(await search.SearchAsync(input,default)).Items.Single(i=>i.Group==LauncherGroups.Direct);
            Check(item.Id=="direct:url:"+url&&item.Title==url&&item.Kind==LauncherItemKind.Web&&item.Icon.Glyph=="globe"&&item.Subtitle==Strings.Launcher_DirectOpenUrl,"098 网址规范化 "+input);
            Check((await search.GetActionsAsync(item,default)).Count==0,"098 网址无附加操作");
            await search.ExecuteAsync(item,default);Check(executor.Calls.Last()=="open:"+url+":False","098 网址经执行器打开");
        }
        foreach(string query in new[]{"http://","ftp://x","javascript:alert(1)","file:///C:/","ms-settings:display","go https://x","https://a.b/ a","github.com",@"\\server\share","relative.txt",Path.Combine(root,"absent")})
            Check(!(await search.SearchAsync(query,default)).Items.Any(i=>i.Group==LauncherGroups.Direct),"098 不识别 "+query);
        string file=Path.Combine(root,"Fixture.txt");File.WriteAllText(file,"fixture");
        foreach(var (input,path,kind) in new[]{(root,root,LauncherItemKind.Folder),(file.Replace('\\','/'),file,LauncherItemKind.File),("%TEMP%",Path.GetFullPath(Environment.ExpandEnvironmentVariables("%TEMP%")),LauncherItemKind.Folder),(Path.GetPathRoot(root)!,Path.GetPathRoot(root)!,LauncherItemKind.Folder)}){
            var item=(await search.SearchAsync(input,default)).Items.Single(i=>i.Group==LauncherGroups.Direct);
            Check(item.Id=="direct:path:"+path&&item.Title==path&&item.Kind==kind&&item.Icon.Glyph==(kind==LauncherItemKind.Folder?"folder":"doc"),"098 路径规范化和类型");
            Check(item.Subtitle==(kind==LauncherItemKind.Folder?Strings.Launcher_DirectOpenFolder:Strings.Launcher_DirectOpenFile),"098 路径副标题");
            Check((await search.GetActionsAsync(item,default)).SequenceEqual([LauncherItemAction.OpenLocation,LauncherItemAction.CopyPath,LauncherItemAction.Properties]),"098 路径动作");
            await search.ExecuteAsync(item,default);Check(executor.Calls.Last()=="open:"+path+":False","098 路径经执行器打开");
            await search.RunAsync(item,LauncherItemAction.CopyPath,default);Check(executor.Calls.Last()=="copy:"+path,"098 复制路径");
            await search.RunAsync(item,LauncherItemAction.OpenLocation,default);Check(executor.Calls.Last()=="reveal:"+path,"098 打开位置");
            await search.RunAsync(item,LauncherItemAction.Properties,default);Check(platform.Property==path,"098 属性路径");
        }
        Check(!File.Exists(Path.Combine(root,"launcher-usage.json")),"098 直达不写使用记录");
        var forged=new LauncherItem("direct:url:javascript:alert(1)",LauncherItemKind.Web,LauncherGroups.Direct,"",null,new("globe"));
        try{await search.ExecuteAsync(forged,default);Check(false,"098 非HTTP执行被拒绝");}catch(LauncherActionException){Check(true,"098 非HTTP执行被拒绝");}
        files.Delay=1000;LauncherResult? partial=null;int partialCount=0;
        var complete=await search.SearchAsync("https://github.com",r=>{partial=r;partialCount++;},default);
        Check(partialCount==1&&partial!.Items.Any(i=>i.Group==LauncherGroups.Direct)&&complete.Items.Any(i=>i.Group==LauncherGroups.Direct),"098 首批和最终包含网址");
        partial=null;await search.SearchAsync(root,r=>partial=r,default);
        Check(partial?.Items.Any(i=>i.Group==LauncherGroups.Direct)==true,"098 首批包含路径");
        files.Delay=0;
        var direct=((ILauncherSource[])typeof(LauncherSearch).GetField("sources",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(search)!).OfType<DirectSource>().Single();
        var realProbe=direct.Probe;using var release=new ManualResetEventSlim();var ended=new TaskCompletionSource();
        direct.Probe=_=>{try{release.Wait();return LauncherItemKind.File;}finally{ended.TrySetResult();}};
        files.ReturnRow=true;var watch=Stopwatch.StartNew();
        try{var timed=await search.SearchAsync(file,default);Check(watch.ElapsedMilliseconds<700&&!timed.Items.Any(i=>i.Group==LauncherGroups.Direct)&&timed.Items.Any(i=>i.Group==LauncherGroups.Files),"098 存在检查超时保留其他来源");}
        finally{release.Set();await ended.Task;direct.Probe=realProbe;files.ReturnRow=false;}
        direct.Probe=_=>throw new UnauthorizedAccessException();
        Check(!(await search.SearchAsync(file,default)).Items.Any(i=>i.Group==LauncherGroups.Direct),"098 不可访问静默忽略");direct.Probe=realProbe;
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();int calls=0;try{await search.SearchAsync(file,_=>calls++,cancel.Token);Check(false,"098 取消");}catch(OperationCanceledException){Check(calls==0,"098 取消后无首批");}
        }
        var app=(await search.SearchAsync("wx",default)).Items.First(i=>i.Kind==LauncherItemKind.App);
        var command=(await search.SearchAsync("锁屏",default)).Items.Single(i=>i.Id=="command:lock");
        var system=(await search.SearchAsync("display",default)).Items.First(i=>i.Group==LauncherGroups.Settings);
        foreach(var item in new[]{app,command,system})Check((await search.GetActionsAsync(item,default)).Last()==LauncherItemAction.HideFromSearch,"098 可隐藏且在末尾");
        foreach(var item in new[]{app with{Kind=LauncherItemKind.File},app with{Kind=LauncherItemKind.Folder},app with{Group=LauncherGroups.Direct},command with{Id="power:panel"},command with{Id="terminal:fixture"}})
            Check(!(await search.GetActionsAsync(item,default)).Contains(LauncherItemAction.HideFromSearch),"098 不可隐藏类型");
        await search.ExecuteAsync(app,default);store.Update(s=>s with{Launcher=s.Launcher with{PinnedApps=[app.Id]}});
        string usage=File.ReadAllText(Path.Combine(root,"launcher-usage.json"));
        foreach(var item in new[]{app,command,system}){await search.RunAsync(item,LauncherItemAction.HideFromSearch,default);await search.RunAsync(item,LauncherItemAction.HideFromSearch,default);}
        Check(store.Current.Launcher.HiddenItems.Length==3,"098 重复隐藏不追加");
        Check(!(await search.SearchAsync("wx",default)).Items.Any(i=>i.Id==app.Id)&&!(await search.SearchAsync("锁屏",default)).Items.Any(i=>i.Id==command.Id)&&!(await search.SearchAsync("display",default)).Items.Any(i=>i.Id==system.Id),"098 各来源隐藏");
        var home=(await search.SearchAsync("",default)).Items;
        Check(!home.Any(i=>i.Id==app.Id&&i.Group==LauncherGroups.Recent)&&home.Any(i=>i.Id==app.Id&&i.Group==LauncherGroups.Pinned),"098 最近隐藏固定保留");
        Check(File.ReadAllText(Path.Combine(root,"launcher-usage.json"))==usage,"098 使用记录不删除");
        Check(search.List().Select(i=>i.Id).SequenceEqual(new[]{app.Id,command.Id,system.Id})&&search.List().SequenceEqual(new[]{app,command,system}.Select(i=>i with{Highlights=null})),"098 列表顺序及当前标题图标副标题");
        var reload=new SettingsStore(root,Defaults());reload.Load();
        using(var restarted=new LauncherSearch(reload,new Index078(value),new FakeFiles(),new FakeExecutor(),new Environment037(),new Process037())){
            await restarted.Indexing;Check(reload.Current.Launcher.HiddenItems.Length==3&&!(await restarted.SearchAsync("wx",default)).Items.Any(i=>i.Id==app.Id),"098 重载仍隐藏");
        }
        search.Unhide(app.Id);Check((await search.SearchAsync("wx",default)).Items.Any(i=>i.Id==app.Id),"098 恢复立即生效");
        reload.Load();Check(!reload.Current.Launcher.HiddenItems.Any(e=>e.Id==app.Id),"098 恢复写盘");
        search.Unhide("missing");Check(store.Current.Launcher.HiddenItems.Length==2,"098 恢复不存在Id不改变");
        await search.RunAsync(app,LauncherItemAction.HideFromSearch,default);
        Publish078(search,value with{Snapshot=value.Snapshot with{Apps=[]}});
        Check(search.List().Last() is {Title:"微信",Subtitle:null,Icon.Glyph:"app"},"098 卸载后保存名字及默认图标");
        var migrated=value with{Snapshot=value.Snapshot with{Apps=[value.Snapshot.Apps[0] with{Id="app:merged"}]},Metrics=value.Metrics with{Aliases=new(){[app.Id]="app:merged",["app:old"]="app:merged"}}};
        Publish078(search,migrated);
        Check(store.Current.Launcher.HiddenItems.Last().Id=="app:merged"&&!(await search.SearchAsync("wx",default)).Items.Any(i=>i.Kind==LauncherItemKind.App),"098 Id迁移后仍隐藏");
        store.Update(s=>s with{Launcher=s.Launcher with{HiddenItems=[new("app:old","旧名字")]}});
        Check(!(await search.SearchAsync("wx",default)).Items.Any(i=>i.Kind==LauncherItemKind.App)&&search.List().Single().Title=="微信","098 合并旧Id过滤及列表解析");
        store.Update(s=>s with{Launcher=s.Launcher with{HiddenItems=[new("app:old","旧名字"),new("app:merged","新名字")]}});
        Publish078(search,migrated);Check(store.Current.Launcher.HiddenItems.Single() is {Id:"app:merged",Name:"旧名字"},"098 合并保持最早隐藏顺序与名字");
        search.Unhide("app:merged");Check((await search.SearchAsync("wx",default)).Items.Any(i=>i.Id=="app:merged"),"098 合并后恢复");
        foreach(bool enabled in new[]{true,false,true}){
            store.Update(s=>s with{Launcher=s.Launcher with{SearchSystemSettings=enabled}});
            Check((await search.SearchAsync("display",default)).Items.Any(i=>i.Group==LauncherGroups.Settings)==enabled,"098 系统设置开关立即生效");
            Check((await search.SearchAsync("锁屏",default)).Items.Any(i=>i.Id=="command:lock")&&(await search.SearchAsync("设置",default)).Items.Any(i=>i.Id=="command:settings"),"098 Pane命令保留");
        }
        var extra=(await search.SearchAsync("logoff",default)).Items.Single(i=>i.Id=="command:logoff");
        await search.RunAsync(extra,LauncherItemAction.HideFromSearch,default);
        Check(!(await search.SearchAsync("logoff",default)).Items.Any(i=>i.Id==extra.Id)&&search.List().Single()==extra,"098 扩展命令隐藏及列表文案一致");
        var context=new SourceContext("app",value.Snapshot,Defaults().Launcher,new Dictionary<string,AppUse>());
        var source=new ApplicationSource(executor,_=>{});
        var all=await source.SearchAsync(context,default);
        var filtered=await source.SearchAsync(context with{Settings=context.Settings with{HiddenItems=[new(all[0].Id,all[0].Title)]}},default);
        Check(filtered.Count==all.Count&&!filtered.Any(i=>i.Id==all[0].Id),"098 隐藏在条数限制前且复制上下文读取新设置");
        Console.WriteLine($"PASS 098 assertions={checks-before};realExecutions=0;clipboardWrites=0;browserLaunches=0");
    }
}
