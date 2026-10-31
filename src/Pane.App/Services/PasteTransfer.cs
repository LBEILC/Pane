using System.Runtime.InteropServices;
using System.Text;
namespace Pane.Services.Clipboard;

public sealed class ClipboardTargetClosedException():InvalidOperationException("目标窗口已关闭");
public sealed class ClipboardPermissionException():UnauthorizedAccessException("系统拒绝向更高完整性级别的窗口输入");
public sealed class ClipboardFocusException():InvalidOperationException("没能切回原来的窗口，内容已经复制好了，按 Ctrl+V 就能贴");

// 仅由一次输入事务调用；没有常驻计时器或前台监听。
internal interface IPastePlatform
{
    nint Foreground {get;}
    bool Exists(nint window);
    uint Process(nint window);
    nint Root(nint window);
    uint Thread(nint window);
    bool Activate(nint window);
    bool Attach(uint from,uint to,bool attach);
    void Top(nint window);
    void Restore(nint window);
    bool Physical(int key);
    bool Logical(int key);
    bool Key(ushort key,bool down);
    bool? Higher(nint window);
    string Class(nint window);
    int Error {get;}
    long Now {get;}
    void Wait();
}
internal sealed class PasteTransfer(IPastePlatform platform,Action<string>? log=null)
{
    internal static readonly int[] Modifiers=[160,161,162,163,164,165,91,92];
    internal bool Equivalent(nint target,nint foreground)=>target!=0&&foreground!=0&&
        (target==foreground||platform.Root(target)!=0&&platform.Root(target)==platform.Root(foreground)||
         platform.Process(target)!=0&&platform.Process(target)==platform.Process(foreground));
    internal bool Focus(nint target,CancellationToken token)
    {
        platform.Restore(target);
        bool activated=platform.Activate(target);
        if(!activated||!Equivalent(target,platform.Foreground)){
            uint from=platform.Thread(platform.Foreground),to=platform.Thread(target);
            bool attached=from!=0&&to!=0&&from!=to&&platform.Attach(from,to,true);
            try{platform.Activate(target);platform.Top(target);}
            finally{if(attached)platform.Attach(from,to,false);}
        }
        long end=platform.Now+300;
        do{token.ThrowIfCancellationRequested();if(Equivalent(target,platform.Foreground))return true;
            if(!platform.Exists(target)||platform.Now>=end)return false;platform.Wait();}while(true);
    }
    internal void PrepareModifiers(CancellationToken token,int timeout=1500)
    {
        long end=platform.Now+timeout;
        while(Modifiers.Any(platform.Physical)){
            token.ThrowIfCancellationRequested();if(platform.Now>=end)throw new TimeoutException("等待修饰键松开超时");platform.Wait();
        }
        foreach(int key in Modifiers){
            token.ThrowIfCancellationRequested();
            // 再查一次物理状态，不能抬起用户仍按着的键。
            if(platform.Logical(key)&&!platform.Physical(key)&&!platform.Key((ushort)key,false))
                throw new InvalidOperationException("输入被系统拒绝");
        }
    }
    internal void Run(nint target,Action write,CancellationToken token,long started)
    {
        string step="target";int error=0;
        try{
            token.ThrowIfCancellationRequested();if(!platform.Exists(target))throw new ClipboardTargetClosedException();
            step="write";write();
            step="focus";if(!Focus(target,token))throw Failure(target);
            step="modifiers";PrepareModifiers(token);
            token.ThrowIfCancellationRequested();step="confirm";
            if(!platform.Exists(target))throw new ClipboardTargetClosedException();
            if(!Equivalent(target,platform.Foreground))throw Failure(target);
            step="input";
            // 紧邻发送再检查，避免等待期间用户重新按住修饰键。
            if(Modifiers.Any(platform.Physical))throw new TimeoutException("修饰键尚未松开");
            if(platform.Higher(target)==true)throw new ClipboardPermissionException();
            SendChord();
        }catch(Exception e){
            error=step=="write"?Marshal.GetLastPInvokeError():platform.Error;
            long elapsed=platform.Now-started;
            var foreground=platform.Foreground;
            log?.Invoke($"step={step} failure={e.GetType().Name} lastError={error} targetClass={Clean(platform.Class(target))} higherIntegrity={platform.Higher(target)?.ToString()??"unknown"} foregroundClass={Clean(platform.Class(foreground))} sameProcess={platform.Process(target)!=0&&platform.Process(target)==platform.Process(foreground)} sameRootOwner={platform.Root(target)!=0&&platform.Root(target)==platform.Root(foreground)} elapsedMs={elapsed}");
            if(e is not (OperationCanceledException or TimeoutException or ClipboardTargetClosedException or ClipboardPermissionException or ClipboardFocusException)&&(step is "modifiers" or "input")&&platform.Higher(target)==true)throw new ClipboardPermissionException();
            throw;
        }
    }
    Exception Failure(nint target)=>!platform.Exists(target)?new ClipboardTargetClosedException():platform.Higher(target)==true?new ClipboardPermissionException():new ClipboardFocusException();
    void SendChord()
    {
        bool control=false,v=false;
        try{
            if(!platform.Key(17,true))throw new InvalidOperationException("输入被系统拒绝");control=true;
            if(!platform.Key(0x56,true))throw new InvalidOperationException("输入被系统拒绝");v=true;
        }finally{
            int error=platform.Error;
            bool releasedV=!v||platform.Key(0x56,false),releasedControl=!control||platform.Key(17,false);
            if(!releasedV||!releasedControl)throw new InvalidOperationException("输入被系统拒绝");
            Marshal.SetLastPInvokeError(error);
        }
    }
    static string Clean(string value)=>value.Replace('\r',' ').Replace('\n',' ');
    internal static void WriteLog(string line)
    {
        try{lock(LogLock){string path=Path.Combine(Pane.Hosting.RuntimeMode.Current.DataDirectory,"logs","paste.log");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+line+Environment.NewLine);}}
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    static readonly object LogLock=new();
}
internal sealed class WindowsPastePlatform:IPastePlatform
{
    // 在调用方（面板 UI 线程）记录逻辑状态，后台 STA 不共享它的键盘队列。
    readonly HashSet<int> logicalAtStart=PasteTransfer.Modifiers.Where(k=>(GetKeyState(k)&0x8000)!=0).ToHashSet();
    int lastError;
    public nint Foreground=>SelectionNative.GetForegroundWindow();
    public bool Exists(nint w)=>IsWindow(w);
    public uint Process(nint w){SelectionNative.GetWindowThreadProcessId(w,out uint pid);return pid;}
    public uint Thread(nint w)=>SelectionNative.GetWindowThreadProcessId(w,out _);
    public nint Root(nint w)=>GetAncestor(w,3);
    public bool Activate(nint w){bool result=SelectionNative.SetForegroundWindow(w);lastError=Marshal.GetLastPInvokeError();return result;}
    public bool Attach(uint from,uint to,bool attach){bool result=AttachThreadInput(from,to,attach);if(attach)lastError=Marshal.GetLastPInvokeError();return result;}
    public void Top(nint w){if(!BringWindowToTop(w))lastError=Marshal.GetLastPInvokeError();}
    public void Restore(nint w){if(IsIconic(w))ShowWindowAsync(w,9);}
    public bool Physical(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    public bool Logical(int key)=>logicalAtStart.Contains(key)||(GetKeyState(key)&0x8000)!=0;
    public bool Key(ushort key,bool down){bool result=SelectionNative.PaneKey(key,down?1:0,0)!=0;if(!result)lastError=Marshal.GetLastPInvokeError();return result;}
    public int Error=>lastError;
    public long Now=>Environment.TickCount64;
    public void Wait()=>System.Threading.Thread.Sleep(10);
    public string Class(nint w){var name=new StringBuilder(256);SelectionNative.GetClassName(w,name,name.Capacity);return name.ToString();}
    public bool? Higher(nint w){uint? target=Integrity(Process(w)),own=Integrity((uint)Environment.ProcessId);return target.HasValue&&own.HasValue?target>own:null;}
    static uint? Integrity(uint pid)
    {
        nint process=OpenProcess(0x1000,false,pid);if(process==0)return null;
        try{if(!OpenProcessToken(process,8,out nint token))return null;
            try{GetTokenInformation(token,25,0,0,out int size);if(size<=0)return null;var buffer=Marshal.AllocHGlobal(size);
                try{if(!GetTokenInformation(token,25,buffer,size,out _))return null;var sid=Marshal.ReadIntPtr(buffer);byte count=Marshal.ReadByte(GetSidSubAuthorityCount(sid));return count==0?null:(uint)Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)(count-1)));}
                finally{Marshal.FreeHGlobal(buffer);}
            }finally{CloseHandle(token);}
        }finally{CloseHandle(process);}
    }
    [DllImport("user32.dll")] static extern bool IsWindow(nint w);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint w,uint flags);
    [DllImport("user32.dll",SetLastError=true)] static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("user32.dll",SetLastError=true)] static extern bool BringWindowToTop(nint w);
    [DllImport("user32.dll")] static extern bool IsIconic(nint w);
    [DllImport("user32.dll",SetLastError=true)] static extern bool ShowWindowAsync(nint w,int command);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("kernel32.dll",SetLastError=true)] static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(nint token,int kind,nint data,int size,out int needed);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthority(nint sid,uint index);
}
