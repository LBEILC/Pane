using System.Diagnostics;
using System.Text.Json;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    // 只在测试 exe 中支持故障夹具；产品索引模式没有故障注入开关。
    static int FixtureWorker(string[] args){
        try{
            var request=JsonSerializer.Deserialize(File.ReadAllText(args[2]),IndexJson.Default.IndexRequest)!;
            string mode=request.Favorites.FirstOrDefault()?.Path??"";
            if(mode=="timeout"){Thread.Sleep(Timeout.Infinite);return 9;}
            if(mode=="error"){File.WriteAllText(args[3]+".tmp","partial");return 7;}
            IndexStorage.Write(args[3],IndexBuilder.Build(new FakeCatalog(),request.Favorites,default));return 0;
        }catch{return 8;}
    }
    static bool Exited(int id){try{using var p=Process.GetProcessById(id);return p.HasExited;}catch(ArgumentException){return true;}}
    static async Task IndexTests(){
        int before=checks,loads=PinyinTable.Loads;string directory=Path.Combine(AppContext.BaseDirectory,"results","index-"+Guid.NewGuid().ToString("N"));
        var provider=new ProcessLauncherIndex(directory);Check(provider.Load()==null,"first run no index");
        var value=await provider.BuildAsync([new(@"C:\fixture\银行.txt",false)],default);
        Check(value.Snapshot.Apps.Length==2&&value.Snapshot.Apps[0].Name.Match("weixin")!=null&&value.Snapshot.Favorites[0].Name.Match("yh")!=null,"snapshot roundtrip names/tokens");
        Check(provider.Outcome=="completed"&&provider.LastExitCode==0&&Exited(provider.LastProcessId),"completed worker exits");
        Console.WriteLine($"worker completed exit={provider.LastExitCode};exited={Exited(provider.LastProcessId)};ms={provider.LastMilliseconds:F2};peakMiB={provider.PeakWorkingSet/1048576.0:F2}");
        byte[] saved=File.ReadAllBytes(Path.Combine(directory,"launcher-index.json"));
        var slow=new ProcessLauncherIndex(directory,timeout:TimeSpan.FromMilliseconds(300));
        try{await slow.BuildAsync([new("timeout",false)],default);Check(false,"timeout required");}catch(OperationCanceledException){Check(slow.Outcome=="timeout"&&Exited(slow.LastProcessId),"timeout worker exits");}
        Check(saved.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"launcher-index.json"))),"timeout keeps previous result");
        Console.WriteLine($"worker timeout exit={slow.LastExitCode};exited={Exited(slow.LastProcessId)};ms={slow.LastMilliseconds:F2}");
        try{await provider.BuildAsync([new("error",false)],default);Check(false,"error required");}catch(IOException){Check(provider.LastExitCode==7&&Exited(provider.LastProcessId),"error worker exits");}
        Check(saved.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"launcher-index.json")))&&!File.Exists(Path.Combine(directory,"launcher-index-candidate.json.tmp")),"error keeps previous and removes partial");
        Console.WriteLine($"worker error exit={provider.LastExitCode};exited={Exited(provider.LastProcessId)}");
        var first=new ProcessLauncherIndex(directory,timeout:TimeSpan.FromMilliseconds(300));
        var queued=new ProcessLauncherIndex(directory);
        var firstWork=first.BuildAsync([new("timeout",false)],default);
        var secondWork=queued.BuildAsync([],default);
        Check(first.LastProcessId!=0&&queued.LastProcessId==0&&!secondWork.IsCompleted,"only one index worker starts while another is running");
        try{await firstWork;}catch(OperationCanceledException){}
        await secondWork;
        Check(Exited(first.LastProcessId)&&Exited(queued.LastProcessId)&&queued.Outcome=="completed","queued worker starts after predecessor exits");
        Check(PinyinTable.Loads==loads,"index worker does not load pinyin in parent");
        var store=new SettingsStore(directory,Defaults());var timer=Stopwatch.StartNew();
        using(var service=new LauncherSearch(store,new CachedSlowIndex(provider),new FakeFiles(),new FakeExecutor())){
            var result=await service.SearchAsync("wx",default);
            Check(result.Items.Count(i=>i.Group=="应用")==2&&!service.Indexing.IsCompleted,"second startup searchable before rebuild");
            Console.WriteLine($"cached firstSearchMs={timer.Elapsed.TotalMilliseconds:F3};indexPending={!service.Indexing.IsCompleted}");
        }
        Check(PinyinTable.Loads==loads+2,"settings and extra commands load their existing pinyin tables");
        string target=Path.Combine(directory,"fixture.exe"),cache=Path.Combine(directory,"launcher-icons");Directory.CreateDirectory(cache);File.WriteAllText(target,"v1");
        int extracts=0;int Extract(string path){extracts++;File.WriteAllText(path,File.ReadAllText(target));return 1;}
        string old=IconCache.Key("same-app",target),oldPath=IconCache.GetOrCreate(cache,old,Extract,out _)!;
        Check(IconCache.GetOrCreate(cache,old,Extract,out _)==oldPath&&extracts==1,"unchanged target reuses extracted icon");
        File.WriteAllText(target,"version 2 icon");File.SetLastWriteTimeUtc(target,DateTime.UtcNow.AddMinutes(1));
        string updated=IconCache.Key("same-app",target),newPath=IconCache.GetOrCreate(cache,updated,Extract,out _)!;
        Check(old!=updated&&IconCache.Key("same-app",target)==updated,"upgrade invalidates/stable cache key");
        IconCache.Cleanup(directory,[newPath]);Check(!File.Exists(oldPath)&&File.ReadAllText(newPath)=="version 2 icon"&&extracts==2,"upgrade extracts new content and cleans old icon");
        await FirstHomeTest(value,directory);
        Console.WriteLine($"index assertions={checks-before};parentPinyinLoadDelta={PinyinTable.Loads-loads};workersExited=True");
    }
    static async Task FirstHomeTest(PersistedIndex value,string directory){
        var pending=new ControlledIndex(value);var store=new SettingsStore(Path.Combine(directory,"first-home"),Defaults() with{Launcher=Defaults().Launcher with{PinnedApps=["app:1"]}});
        using var service=new LauncherSearch(store,pending,new FakeFiles(),new FakeExecutor());
        Check((await service.SearchAsync("",default)).Items.Count==0,"first run temporarily empty home");
        pending.Ready.TrySetResult();await service.Indexing;
        Check((await service.SearchAsync("",default)).Items.Single().Id=="app:1","next home query sees completed index without input change");
    }
    sealed class ControlledIndex(PersistedIndex value):ILauncherIndex {
        public TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PersistedIndex? Load()=>null;
        public async Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token){await Ready.Task.WaitAsync(token);return value;}
    }
    sealed class CachedSlowIndex(ProcessLauncherIndex provider):ILauncherIndex {
        public PersistedIndex? Load()=>provider.Load();
        public async Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token){await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}
    }
}
