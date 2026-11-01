using System.Diagnostics;
using System.Text.Json;
using Pane.Services.Clipboard;
using Pane.Settings;

static partial class Program
{
    static async Task Until(Func<bool> condition,int milliseconds=15000)
    {
        var watch=Stopwatch.StartNew();
        while(!condition()){if(watch.ElapsedMilliseconds>milliseconds)throw new TimeoutException("等待测试条件超时");await Task.Delay(20);}
    }
    static string TestRoot()=>Path.Combine(AppContext.BaseDirectory,"results",Guid.NewGuid().ToString("N"));
    static async Task PersistenceTests()
    {
        int before=checks;string root=TestRoot();var source=new FakeSource();var storage=new FaultStorage();
        string logPath=Path.Combine(root,"logs","clipboard.log");
        using(var history=new ClipboardHistory(new SettingsStore(root,Defaults()),source,new FakeWriter(),new SwitchImages(),storage)){
            await history.Ready;int problems=0,cleared=0;
            history.Changed+=(_,_)=>{if(history.StorageProblem!=null)Interlocked.Increment(ref problems);else Interlocked.Increment(ref cleared);};
            source.Emit(new(Text:"private-fixture-name.png"));
            await Until(()=>storage.Success.IsSet&&history.StorageProblem==null);
            Check(history.StorageFailures==4&&problems==4&&cleared>=2,"four exceptions recover and notify");
            Check(storage.Times.Count==5,"all attempts survived");
            for(int i=1;i<5;i++)Check((storage.Times[i]-storage.Times[i-1]).TotalMilliseconds>=ClipboardHistory.RetryDelay(i)-30,"retry backoff "+i);
            Check(ClipboardHistory.RetryDelay(100)==10000,"retry capped at ten seconds");
            Check(new ClipboardStorage().Load(Path.Combine(root,"clipboard","history.json"))[0].Entry.Text=="private-fixture-name.png","recovery durable");
            string log=File.ReadAllText(logPath);
            foreach(string type in new[]{"IOException","UnauthorizedAccessException","InvalidOperationException","NotSupportedException"})Check(log.Contains("type="+type),"failure log "+type);
            Check(log.Contains("step=serialize")&&log.Contains("hresult=0x")&&log.Contains("save-recovered"),"structured failure and recovery");
            Check(!log.Contains("private-fixture-name.png"),"log excludes exception message and content");
            // 正常空闲只等待事件；一次测量同时核对保存次数没有增加。
            using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;int calls=storage.Times.Count;
            await Task.Delay(1500);process.Refresh();double cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            Check(storage.Times.Count==calls,"idle no storage polling");Console.WriteLine($"storage idle windowMs=1500 cpuMs={cpuMs:F3} saves=0");
        }
        Check(File.ReadAllText(logPath).Contains("loop-exit"),"exit logged");
        // 日志轮换保留一个旧文件，总量不随失败次数无限增长。
        var logger=new ClipboardLog(root);for(int i=0;i<4000;i++)logger.Write("save-failed",new IOException("private-fixture-name.png"),"serialize",i);
        Check(new FileInfo(logPath).Length<=ClipboardLog.Limit&&new FileInfo(logPath+".1").Length<=ClipboardLog.Limit,"bounded rotated logs");
        await StarvationTest();await CleanupTests();
        Console.WriteLine($"PASS persistence assertions={checks-before}");
    }
    static async Task StarvationTest()
    {
        string root=TestRoot();var source=new FakeSource();var storage=new FaultStorage{Faults=false};
        using var history=new ClipboardHistory(new SettingsStore(root,Defaults()),source,new FakeWriter(),new SwitchImages(),storage);await history.Ready;
        // 独立协调线程恢复线程池上限，避免测试自己的 async 续体无法恢复。
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator=new Thread(()=>{
            ThreadPool.GetMinThreads(out int min,out int minIo);ThreadPool.GetMaxThreads(out int max,out int maxIo);
            using var release=new ManualResetEventSlim();using var occupied=new ManualResetEventSlim();
            try{
                if(!ThreadPool.SetMinThreads(1,minIo)||!ThreadPool.SetMaxThreads(1,maxIo))throw new Exception("无法限制测试进程线程池");
                ThreadPool.QueueUserWorkItem(_=>{occupied.Set();release.Wait();});
                if(!occupied.Wait(10000))throw new Exception("线程池占用失败");
                source.Emit(new(Text:"starved"));
                Check(storage.Success.Wait(5000),"dedicated storage survives occupied pool");
                Check(new ClipboardStorage().Load(Path.Combine(root,"clipboard","history.json")).Length==1,"starved save durable");
            }catch(Exception e){done.TrySetException(e);}
            finally{release.Set();ThreadPool.SetMaxThreads(max,maxIo);ThreadPool.SetMinThreads(min,minIo);done.TrySetResult();}
        }){IsBackground=true};coordinator.Start();await done.Task;coordinator.Join();
    }
    static async Task CleanupTests()
    {
        foreach(bool corrupt in new[]{false,true}){
            string root=TestRoot(),directory=Path.Combine(root,"clipboard");Directory.CreateDirectory(directory);
            var source=new FakeSource();var settings=new SettingsStore(root,Defaults());
            using(var history=new ClipboardHistory(settings,source,new FakeWriter(),new SwitchImages())){await history.Ready;source.Emit(new(Png:[1]));}
            string[] kept=Directory.GetFiles(directory,"*.png");
            string[] extras=["orphan.png","left.input","left.tmp"];foreach(string name in extras)File.WriteAllText(Path.Combine(directory,name),"fixture");
            if(corrupt)File.WriteAllText(Path.Combine(directory,"history.json"),"broken-json");
            var next=new ClipboardHistory(settings,new FakeSource(),new FakeWriter(),new SwitchImages());
            try{await next.Ready;Check(!corrupt,"load success");}catch(JsonException){Check(corrupt,"load failure");}
            finally{try{next.Dispose();}catch(JsonException){}}
            Check(kept.All(File.Exists),"referenced files kept");
            Check(extras.All(name=>File.Exists(Path.Combine(directory,name))==corrupt),"cleanup only after successful load");
        }
    }
    sealed class FaultStorage:IClipboardStorage
    {
        public bool Faults=true;
        public readonly ManualResetEventSlim Success=new();
        public readonly List<DateTimeOffset> Times=[];
        public StoredClip[] Load(string index)=>new ClipboardStorage().Load(index);
        public void Save(string index,StoredClip[] entries,Action<string> step)
        {
            Times.Add(DateTimeOffset.UtcNow);step("serialize");
            if(Faults)switch(Times.Count){case 1:throw new IOException("private-fixture-name.png");case 2:throw new UnauthorizedAccessException("private-fixture-name.png");case 3:throw new InvalidOperationException("private-fixture-name.png");case 4:throw new NotSupportedException("private-fixture-name.png");}
            new ClipboardStorage().Save(index,entries,step);Success.Set();
        }
    }
}
