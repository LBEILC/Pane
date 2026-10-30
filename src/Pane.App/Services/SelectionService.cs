using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pane.Contracts;
namespace Pane.Services;

// 014 在 WM_CLIPBOARDUPDATE 中传入序列号调用 TryConsume(sequence)。
// 活动事务忽略接下来的 N 次；完成后保留精确序列号，覆盖延后派送的通知。
public static class ClipboardHistorySuppression
{
    static int remaining;
    static readonly object sync=new();
    static readonly HashSet<uint> sequences=[];
    public static void IgnoreSequence(uint sequence){lock(sync){if(sequences.Count>=128)sequences.Clear();sequences.Add(sequence);}}
    public static bool TryConsume(uint sequence){lock(sync){if(sequences.Remove(sequence))return true;}return TryConsume();}
    public static void IgnoreNext(int count)=>Interlocked.Add(ref remaining,count);
    public static bool TryConsume(){while(true){int n=Volatile.Read(ref remaining);if(n==0)return false;if(Interlocked.CompareExchange(ref remaining,n-1,n)==n)return true;}}
    public static void CancelPending()=>Interlocked.Exchange(ref remaining,0);
}
internal interface ISelectionPlatform
{
    string Target(nint window)=>"fixture";
    string Diagnostics=>"";
    nint Foreground {get;}
    SelectionResult Direct(nint window);
    object Snapshot();
    uint Sequence {get;}
    void Copy(nint window);
    string? ReadText();
    void Restore(object snapshot);
    void Release(object snapshot);
}
internal sealed class SelectionReader(ISelectionPlatform platform,Func<bool> fallback,SelectionLog? log=null) : ISelectionReader
{
    readonly SemaphoreSlim gate=new(1);
    readonly SelectionLog? logger=log??(platform is WindowsSelectionPlatform?new SelectionLog(Pane.Hosting.RuntimeMode.Current.DataDirectory):null);
    public async Task<SelectionResult> ReadAsync(CancellationToken cancellationToken)
    {
        nint window=platform.Foreground;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(600);
        try {
            if(!await gate.WaitAsync(0,deadline.Token)){logger?.Write($"target={platform.Target(window)} gate=busy result=Timeout length=0 totalMs=0");return new(null,window,SelectionFailure.Timeout);}
            var token=deadline.Token;var work=Sta.Run(()=>{try{return Read(window,token);}finally{gate.Release();}});
            return await work.WaitAsync(deadline.Token);
        }catch(OperationCanceledException){cancellationToken.ThrowIfCancellationRequested();return new(null,window,SelectionFailure.Timeout);}
    }
    SelectionResult Read(nint window,CancellationToken token)
    {
        var trace=new StringBuilder("target="+platform.Target(window));var total=Stopwatch.StartNew();var step=Stopwatch.StartNew();
        SelectionFailure final=SelectionFailure.Timeout;int length=0;
        T Measure<T>(string name,Func<T> action){step.Restart();try{return action();}finally{trace.Append($" {name}Ms={step.Elapsed.TotalMilliseconds:F2}");if(name is "uia" or "snapshot" or "restore")trace.Append(' ').Append(platform.Diagnostics);}}
        SelectionResult Finish(SelectionResult value){final=value.Failure;length=value.Text?.Length??0;return value;}
        try{
        var result=Measure("uia",()=>platform.Direct(window));trace.Append($" uia={result.Failure}");token.ThrowIfCancellationRequested();
        if(result.Failure is SelectionFailure.None or SelectionFailure.Elevated or SelectionFailure.Timeout||!fallback())return Finish(result);
        object? saved=null;bool copying=false;uint before=platform.Sequence;
        try {
            try{saved=Measure("snapshot",platform.Snapshot);}catch(Exception e) when(e is InvalidOperationException or ExternalException){return Finish(new(null,window,SelectionFailure.ClipboardUnavailable));}token.ThrowIfCancellationRequested();
            if(platform.Sequence!=before||platform.Foreground!=window)return new(null,window,SelectionFailure.Timeout);
            ClipboardHistorySuppression.IgnoreNext(2);copying=true;Measure("copy",()=>{platform.Copy(window);return true;});
            step.Restart();
            long end=Environment.TickCount64+250;
            while(platform.Sequence==before&&Environment.TickCount64<end){token.ThrowIfCancellationRequested();Thread.Sleep(5);}
            trace.Append($" waitMs={step.Elapsed.TotalMilliseconds:F2} sequenceChanged={platform.Sequence!=before}");token.ThrowIfCancellationRequested();
            if(platform.Sequence==before)return new(null,window,SelectionFailure.Timeout);
            string? text=Measure("text",platform.ReadText);return Finish(new(text,window,string.IsNullOrEmpty(text)?SelectionFailure.Empty:SelectionFailure.None));
        }catch(OperationCanceledException){throw;}
        catch(Exception e) when(e is InvalidOperationException or ExternalException){return Finish(new(null,window,SelectionFailure.Unsupported));}
        finally{
            try{if(saved!=null){if(copying){if(platform.Sequence!=before)ClipboardHistorySuppression.IgnoreSequence(platform.Sequence);Measure("restore",()=>{platform.Restore(saved);return true;});ClipboardHistorySuppression.IgnoreSequence(platform.Sequence);}else platform.Release(saved);}}
            finally{ClipboardHistorySuppression.CancelPending();}
        }
        }catch(OperationCanceledException){final=SelectionFailure.Timeout;throw;}
        catch(Exception e) when(e is InvalidOperationException or ExternalException){trace.Append(" restoreFailed=True");return Finish(new(null,window,SelectionFailure.Unsupported));}
        catch{final=SelectionFailure.Unsupported;trace.Append(" exception=True");throw;}
        finally{logger?.Write($"{trace} result={final} length={length} totalMs={total.Elapsed.TotalMilliseconds:F2}");}
    }
}
internal static class Sta
{
    public static Task<T> Run<T>(Func<T> action){var source=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>{int hr=SelectionNative.OleInitialize(0);try{source.SetResult(action());}catch(Exception e){source.SetException(e);}finally{if(hr>=0)SelectionNative.OleUninitialize();}}){IsBackground=true,Name="Pane 选区"};thread.SetApartmentState(ApartmentState.STA);thread.Start();return source.Task;}
}
internal sealed class WindowsSelectionPlatform : ISelectionPlatform
{
    public string Diagnostics=>Marshal.PtrToStringUni(SelectionNative.PaneSelectionDiagnostics())??"";
    public string Target(nint window){var cls=new StringBuilder(256);SelectionNative.GetClassName(window,cls,cls.Capacity);SelectionNative.GetWindowThreadProcessId(window,out uint pid);string name="unknown";try{using var p=Process.GetProcessById((int)pid);name=p.ProcessName+".exe";}catch(ArgumentException){}catch(System.ComponentModel.Win32Exception){}return name+" class="+cls;}
    public nint Foreground=>SelectionNative.GetForegroundWindow();
    public SelectionResult Direct(nint window){int code=SelectionNative.PaneSelectionDirectAlloc(window,out nint text);try{return new(code==0?Marshal.PtrToStringUni(text):null,window,(SelectionFailure)code);}finally{Marshal.FreeCoTaskMem(text);}}
    public object Snapshot(){nint data=SelectionNative.PaneClipboardSnapshot();if(data==0)throw new InvalidOperationException("无法完整保存剪贴板");return data;}
    public uint Sequence=>SelectionNative.GetClipboardSequenceNumber();
    public void Copy(nint window){if(Foreground!=window)throw new InvalidOperationException("前台窗口已变化");SelectionNative.Chord(0x43);}
    public string? ReadText(){int code=SelectionNative.PaneClipboardTextAlloc(out nint text);try{return code!=0?Marshal.PtrToStringUni(text):null;}finally{Marshal.FreeCoTaskMem(text);}}
    public void Release(object snapshot)=>SelectionNative.PaneClipboardRelease((nint)snapshot);
    public void Restore(object snapshot){if(SelectionNative.PaneClipboardRestore((nint)snapshot)==0)throw new InvalidOperationException("剪贴板恢复失败");}
}
internal sealed class SelectionActions : ISelectionActions
{
    public Task SearchWebAsync(string text,CancellationToken token){token.ThrowIfCancellationRequested();Process.Start(new ProcessStartInfo("https://www.bing.com/search?q="+Uri.EscapeDataString(text)){UseShellExecute=true})?.Dispose();return Task.CompletedTask;}
    public Task CopyAsync(string text,CancellationToken token)=>Sta.Run(()=>{token.ThrowIfCancellationRequested();Write(text);return true;});
    public Task ReplaceSelectionAsync(string text,nint window,CancellationToken token)=>Sta.Run(()=>{
        token.ThrowIfCancellationRequested();if(!SelectionNative.SetForegroundWindow(window))throw new InvalidOperationException("无法切回原窗口");
        Write(text);long end=Environment.TickCount64+1500;
        while(SelectionNative.ModifiersDown()){token.ThrowIfCancellationRequested();if(Environment.TickCount64>=end)throw new TimeoutException("修饰键尚未松开");Thread.Sleep(10);}
        if(SelectionNative.GetForegroundWindow()!=window)throw new InvalidOperationException("原窗口已失去焦点");SelectionNative.Chord(0x56);return true;
    });
    static void Write(string text){if(SelectionNative.PaneClipboardWrite(text)==0)throw new InvalidOperationException("无法写入剪贴板");}
}
internal static class SelectionNative
{
    const string Dll="Pane.Native.dll";
    [DllImport(Dll)] internal static extern nint PaneSelectionDiagnostics();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetClassNameW")] internal static extern int GetClassName(nint window,StringBuilder name,int capacity);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneSelectionDirectAlloc(nint window,out nint text);
    [DllImport(Dll)] internal static extern nint PaneClipboardSnapshot();
    [DllImport(Dll)] internal static extern void PaneClipboardRelease(nint data);
    [DllImport(Dll)] internal static extern int PaneClipboardRestore(nint data);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneClipboardTextAlloc(out nint text);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneClipboardWrite(string text);
    [DllImport(Dll)] static extern int PaneKey(ushort key,int down,int unicode);
    [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] internal static extern void OleUninitialize();
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    internal static bool ModifiersDown()=>new[]{16,17,18,91,92}.Any(k=>(GetAsyncKeyState(k)&0x8000)!=0);
    internal static void Chord(ushort key){long end=Environment.TickCount64+100;while(ModifiersDown()){if(Environment.TickCount64>=end)throw new InvalidOperationException("修饰键尚未松开");Thread.Sleep(5);}try{if(PaneKey(17,1,0)==0||PaneKey(key,1,0)==0)throw new InvalidOperationException("输入被系统拒绝");}finally{PaneKey(key,0,0);PaneKey(17,0,0);}}
}
