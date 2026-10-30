using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Pane.Hosting;
internal static class SingleInstance
{
    static Mutex? mutex;
    static readonly string key="Pane-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName+"|"+(RuntimeMode.Current.IsProduction?"production":"development")+"|"+Environment.GetEnvironmentVariable("PANE_INSTANCE"))))[..24];
    public static bool Enter(LaunchCommand command) {
        mutex=new Mutex(true,"Local\\"+key,out bool first);
        if(first){if(command==LaunchCommand.Exit){Release();return false;}return true;}
        if(command==LaunchCommand.Autostart){mutex.Dispose();mutex=null;return false;}
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try {
            using(var pipe=new NamedPipeClientStream(".",key,PipeDirection.InOut,PipeOptions.CurrentUserOnly)){
                pipe.Connect(5000);
                using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};writer.WriteLine(command==LaunchCommand.Exit?"exit":"show-launcher");
                if(command==LaunchCommand.Exit){using var reader=new StreamReader(pipe,leaveOpen:true);var reply=reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1,10000-watch.ElapsedMilliseconds))).GetAwaiter().GetResult();
                    if(!int.TryParse(reply,out int pid))throw new IOException("实例响应无效");
                    // 持有内核句柄直到退出；附着式 Process 对象在进程消失时不保证保留退出信息。
                    using var process=OpenProcess(0x101000,false,pid);
                    if(process.IsInvalid){if(Marshal.GetLastWin32Error()!=87)Environment.ExitCode=1;}
                    else {
                        uint wait=WaitForSingleObject(process,(uint)Math.Max(1,10000-watch.ElapsedMilliseconds));
                        if(wait==258)throw new TimeoutException();
                        if(wait!=0||!GetExitCodeProcess(process,out uint code)||code!=0)Environment.ExitCode=1;
                    }
                }
            }
        }catch(Exception e) when(e is IOException or TimeoutException){Environment.ExitCode=1;}
        finally{mutex.Dispose();mutex=null;}
        return false;
    }
    public static async Task Listen(Action<string> command,CancellationToken cancel) {
        while(!cancel.IsCancellationRequested) {
            try {using var pipe=new NamedPipeServerStream(key,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);using var reader=new StreamReader(pipe,leaveOpen:true);var line=await reader.ReadLineAsync(cancel).ConfigureAwait(false);
                if(line=="exit"){using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);}
                if(line is "exit" or "show-launcher" or "show-clipboard")command(line);
            } catch(OperationCanceledException){break;}catch(IOException){}
        }
    }
    public static void Release(){mutex?.ReleaseMutex();mutex?.Dispose();mutex=null;}
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
}
