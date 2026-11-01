using System.IO.Pipes;
using Pane.Services;
namespace Pane;

// 017 临时部署的只读探针：只在显式观察、独立实例、指定数据目录同时满足时启用。
// 通过内存管道传回计数，不写正式版结果文件，也不接收控制命令。
public sealed partial class App
{
    static bool ReleaseProbeEnabled=>Environment.GetEnvironmentVariable("PANE_OBSERVE")=="1"&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_INSTANCE"))&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_DATA_DIR"));
    double hotkeysReadyMs;
    Task? releaseProbe;
    IStartupRegistry CreateStartupRegistry()=>ReleaseProbeEnabled?new ProbeRegistry():new WindowsStartupRegistry();
    sealed class ProbeRegistry:IStartupRegistry
    {
        public string? Read()=>null;
        public bool Disabled=>false;
        public void Write(string command)=>throw new InvalidOperationException("隔离测试禁止写注册表");
        public void Delete()=>throw new InvalidOperationException("隔离测试禁止写注册表");
    }
    void StartReleaseProbe(){
        if(!ReleaseProbeEnabled)return;
        releaseProbe=Task.Run(async()=>{
            if(Environment.GetEnvironmentVariable("PANE_CLIPBOARD_PROBE")=="045"){
                await RunClipboardPersistenceProbe();return;
            }
            while(!cancellation.IsCancellationRequested){
                try {
                    using var pipe=new NamedPipeServerStream("Pane-release-probe-"+Environment.ProcessId,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token);
                    var snapshot=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if(!queue.TryEnqueue(()=>snapshot.TrySetResult($"ready={ready};hotkeys={shell?.RegisteredCount};hotkeysMs={hotkeysReadyMs:F2};tray={shell?.TrayCreated};visible={panels.Values.Count(p=>p.Shown)};hook={gestureService?.State};clipboard={clipboardSource?.Registered};production={IsProduction}")))break;
                    string value=await snapshot.Task.WaitAsync(cancellation.Token);
                    using var writer=new StreamWriter(pipe){AutoFlush=true};await writer.WriteLineAsync(value);
                }catch(OperationCanceledException){break;}catch(IOException){}
            }
        });
    }
    async Task RunClipboardPersistenceProbe()
    {
        // 显式夹具标记、隔离数据、观察模式三者缺一不可；不经过系统剪贴板。
        string data=Store.DirectoryPath;
        if(!IsProduction||!ReleaseProbeEnabled||
            string.Equals(Path.GetFullPath(data).TrimEnd('\\'),Path.GetFullPath(Pane.Settings.SettingsStore.ProductionDirectory).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||
            !File.Exists(Path.Combine(data,"045-fixture"))||Store.Current.General.StartWithWindows||Store.Current.Update.AutoUpdate||
            File.Exists(Path.Combine(data,"winv-original.json")))throw new InvalidOperationException("045 探针需要安全隔离夹具");
        string result=Path.Combine(data,"045-result.txt");
        try{
            await clipboardService!.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            int loaded=clipboardService.Snapshot().Count;
            var watch=System.Diagnostics.Stopwatch.StartNew();
            while(!ready){if(watch.Elapsed.TotalSeconds>30)throw new TimeoutException();await Task.Delay(50);}
            byte[] png=File.ReadAllBytes(Path.Combine(data,"045-fixture.png"));
            clipboardService.Capture(new(Text:"045 synthetic text\0"));
            clipboardService.Capture(new(Files:[Path.Combine(data,"045-fixture.png")]));
            clipboardService.Capture(new(Png:png));
            int expected=loaded+3;
            string index=Path.Combine(data,"clipboard","history.json");watch.Restart();
            while(true){
                if(watch.Elapsed.TotalSeconds>15)throw new TimeoutException();
                if(File.Exists(index)){
                    var stored=new Pane.Services.Clipboard.ClipboardStorage().Load(index);
                    if(stored.Length==expected&&(Environment.GetEnvironmentVariable("PANE_CLIPBOARD_PROBE_BASELINE")=="1"||stored.Any(e=>e.Entry.Kind==Pane.Contracts.ClipKind.Files&&e.Entry.Thumbnail!=null)))break;
                }
                await Task.Delay(25);
            }
            double saveMs=watch.Elapsed.TotalMilliseconds;
            await Task.Delay(2000);
            using var process=System.Diagnostics.Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;
            await Task.Delay(5000);process.Refresh();double cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            File.WriteAllText(result,$"production={IsProduction};loaded={loaded};saved={expected};storageFailures={clipboardService.StorageFailures};saveMs={saveMs:F2};idleWindowMs=5000;idleCpuMs={cpuMs:F3};systemClipboardRead=0;systemClipboardWrite=0;startupRegistry=stub;autoUpdate=False;winvJournal=False");
        }catch(Exception e){File.WriteAllText(result,"failed="+e.GetType().Name);Environment.ExitCode=1;}
        finally{queue.TryEnqueue(Shutdown);}
    }
}
