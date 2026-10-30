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
}
