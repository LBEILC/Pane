using System.Diagnostics;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class Platform076(string blocked):LauncherActionPlatform,IDisposable
    {
        internal readonly ManualResetEventSlim Entered=new(),Release=new();
        internal int Calls;
        internal override ActionTarget? Resolve(string path){
            Interlocked.Increment(ref Calls);
            if(path==blocked){Entered.Set();Release.Wait();}
            return new(path,[]);
        }
        internal override IReadOnlyList<UninstallEntry> ReadUninstall()=>[];
        internal override Task PropertiesAsync(string path,CancellationToken token)=>Task.CompletedTask;
        public void Dispose(){Release.Set();Entered.Dispose();Release.Dispose();}
    }
    sealed class Index076(IndexedApp[] apps):ILauncherIndex
    {
        public PersistedIndex? Load()=>null;
        public Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token)=>Task.FromResult(
            new PersistedIndex(1,new(apps,[],[]),new(0,0,0,0,apps.Length,0,0),DateTimeOffset.UtcNow));
    }
    static async Task Remember076Tests()
    {
        int before=checks;
        string root=TestResults.NewDirectory("076-");
        string opened=Empty057(root,"Opened.exe"),indexed=Empty057(root,"Indexed.lnk"),portable=Empty057(root,"Portable.lnk");
        var table=new PinyinTable();
        // 三个解析入口都必须在锁外，分别停住它们。
        foreach(string stage in new[]{"opened","indexed","portable"}){
            var store=new SettingsStore(Path.Combine(root,stage),Defaults());
            store.Save(Defaults() with{Launcher=Defaults().Launcher with{Portable=[Portable057(portable)]}});
            using var platform=new Platform076(stage=="opened"?opened:stage=="indexed"?indexed:portable);
            using var search=new LauncherSearch(store,new Index076(stage=="indexed"?[new("app:fixture",table.Create("Fixture"),indexed,false,null)]:[]),new FakeFiles(),new Executor057(),actionPlatform:platform);
            await Settle057(search);
            await search.SearchAsync("fixture",default);
            Task remembering=search.OpenFileAsync(opened,default);
            try{
                Check(platform.Entered.Wait(TimeSpan.FromSeconds(2)),"076 进入阻塞解析 "+stage);
                var timer=Stopwatch.StartNew();
                await Task.Run(search.RefreshIndex).WaitAsync(TimeSpan.FromMilliseconds(200));
                Check(timer.ElapsedMilliseconds<200,"076 阻塞期间刷新及时返回 "+stage);
                timer.Restart();await search.SearchAsync("fixture",default).WaitAsync(TimeSpan.FromMilliseconds(200));
                Check(timer.ElapsedMilliseconds<200,"076 阻塞期间搜索及时返回 "+stage);
                int calls=platform.Calls;
                for(int i=0;i<20;i++)await search.OpenFileAsync(opened,default).WaitAsync(TimeSpan.FromMilliseconds(200));
                Check(platform.Calls==calls,"076 后续打开不增加解析任务 "+stage);
                if(stage=="portable"){
                    store.Update(s=>s with{Launcher=s.Launcher with{Portable=[],AppRows=2}});
                    await Settle057(search);
                }
            }finally{platform.Release.Set();await remembering.WaitAsync(TimeSpan.FromSeconds(2));}
            await Settle057(search);
            Check(stage=="portable"?store.Current.Launcher.Portable.Length==0&&store.Current.Launcher.AppRows==2:
                store.Current.Launcher.Portable.Any(e=>e.Path==opened&&e.Kind==PortableKind.Learned),"076 完成记忆或放弃过期设置 "+stage);
            var dispose=Stopwatch.StartNew();search.Dispose();Check(dispose.ElapsedMilliseconds<200,"076 释放后 Dispose 及时返回 "+stage);
        }
        // 只有快照变化、设置引用未变，也不能提交旧解析结果。
        var snapshotStore=new SettingsStore(Path.Combine(root,"snapshot"),Defaults());
        using(var platform=new Platform076(opened))
        using(var search=new LauncherSearch(snapshotStore,new Index076([]),new FakeFiles(),new Executor057(),actionPlatform:platform)){
            await Settle057(search);var remember=search.RememberOpenedAsync(opened);
            try{
                Check(platform.Entered.Wait(TimeSpan.FromSeconds(2)),"076 快照场景进入解析");
                var publish=typeof(LauncherSearch).GetMethod("Publish",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                publish.Invoke(search,[new PersistedIndex(1,new([],[],[]),new(0,0,0,0,0,0,0),DateTimeOffset.UtcNow)]);
            }finally{platform.Release.Set();await remember.WaitAsync(TimeSpan.FromSeconds(2));}
            Check(snapshotStore.Current.Launcher.Portable.Length==0,"076 快照引用改变放弃旧结果");
        }
        var timeoutStore=new SettingsStore(Path.Combine(root,"timeout"),Defaults());
        using(var platform=new Platform076(opened)){
            var search=new LauncherSearch(timeoutStore,new Index076([]),new FakeFiles(),new Executor057(),actionPlatform:platform);
            await Settle057(search);var timer=Stopwatch.StartNew();var remember=search.RememberOpenedAsync(opened);
            try{
                Check(platform.Entered.Wait(TimeSpan.FromSeconds(2)),"076 超时场景进入解析");
                await remember.WaitAsync(TimeSpan.FromSeconds(7));
                Check(timer.Elapsed>=TimeSpan.FromSeconds(4.5)&&timer.Elapsed<TimeSpan.FromSeconds(7),"076 五秒停止等待");
                await search.RememberOpenedAsync(opened);
                Check(platform.Calls==1,"076 超时后仍占用唯一解析名额");
                await Task.Run(search.Dispose).WaitAsync(TimeSpan.FromMilliseconds(200));
                Check(true,"076 未释放 Shell 时 Dispose 也不等待记忆任务");
            }finally{
                platform.Release.Set();
                // 等待工作线程退出，避免销毁仍被假解析器使用的事件。
                var active=typeof(LauncherSearch).GetField("remembering",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                while((int)active.GetValue(search)!!=0)await Task.Delay(5,deadline.Token);
                search.Dispose();
            }
            Check(timeoutStore.Current.Launcher.Portable.Length==0,"076 超时结果不补写");
        }
        var useStore=new SettingsStore(Path.Combine(root,"usage"),Defaults());
        using(var platform=new Platform076(""))
        using(var search=new LauncherSearch(useStore,new Index076([new("app:known",table.Create("Known"),opened,false,null){Target=opened}]),new FakeFiles(),new Executor057(),actionPlatform:platform)){
            await Settle057(search);await search.OpenFileAsync(opened,default);
            var usage=JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(useStore.DirectoryPath,"launcher-usage.json")),LauncherJson.Default.DictionaryStringAppUse)!;
            Check(usage["app:known"].Count==1&&useStore.Current.Launcher.Portable.Length==0,"076 已索引只记录一次使用");
        }
        Console.WriteLine($"PASS 076 assertions={checks-before}");
    }
}
