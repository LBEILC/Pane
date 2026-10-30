using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace Pane.Gestures;

public sealed class WindowsSystem(Action pause,Func<bool>? isActive=null) : ISystemOperations
{
    sealed class InputLease : IDisposable {public void Dispose()=>Win.PaneEndInput();}
    public IDisposable AcquireInput(){if(Win.PaneBeginInput()==0)throw new InvalidOperationException("输入事务繁忙，本次按键已放弃");return new InputLease();}
    void Guard(){if(isActive?.Invoke()==false)throw new InvalidOperationException("服务已停止，取消系统操作");}
    public void Activate(GestureTarget target)
    {
        Guard();
        if (!Win.IsWindow(target.Window)) throw new InvalidOperationException("目标窗口已关闭");
        Win.GetWindowThreadProcessId(target.Window, out uint pid);
        if (pid != target.ProcessId) throw new InvalidOperationException("目标窗口已更换");
        if (Win.GetForegroundWindow() == target.Window) return;
        Win.SetForegroundWindow(target.Window);
        for (int i=0;i<10 && Win.GetForegroundWindow()!=target.Window;i++) Thread.Sleep(10);
        if (Win.GetForegroundWindow()!=target.Window) throw new InvalidOperationException("系统拒绝切换目标窗口到前台");
    }
    public bool IsKeyDown(ushort key) => (Win.GetAsyncKeyState(key) & 0x8000)!=0;
    public void Key(ushort key,bool down) { if(Win.PaneKey(key,down?1:0,0)==0) throw new InvalidOperationException("发送按键失败，可能受 UIPI 权限限制"); }
    public void Character(char character,bool down) { if(Win.PaneKey(character,down?1:0,1)==0) throw new InvalidOperationException("发送文字失败"); }
    public (ushort Key,KeyModifiers Modifiers) MapCharacter(char character)
    {
        short k=Win.VkKeyScanW(character);if(k==-1)throw new NotSupportedException("当前键盘布局不能映射带修饰的字符");
        return ((ushort)(k&255), ((k&0x100)!=0?KeyModifiers.Shift:0)|((k&0x200)!=0?KeyModifiers.Ctrl:0)|((k&0x400)!=0?KeyModifiers.Alt:0));
    }
    public string ReadSelection(GestureTarget target) {Guard();var b=new StringBuilder(32768);int r=Win.PaneSelection(target.Window,b,b.Capacity);Guard();if(r<0)throw new InvalidOperationException($"选区读取已放弃（焦点、快照、权限或超时，代码 {r}）");return b.ToString();}
    public string? ExplorerFolder(GestureTarget target) {if(!string.Equals(Path.GetFileName(target.ExePath),"explorer.exe",StringComparison.OrdinalIgnoreCase))return null;var b=new StringBuilder(32768);return Win.PaneExplorer(target.Window,b,b.Capacity)!=0?b.ToString():null;}
    public void Run(string command,bool showWindow,string directory,IReadOnlyDictionary<string,string> variables)
    {
        Guard();
        // 命令正文由用户提供，原样作为 cmd 的 /c 或 /k 参数；变量经子进程环境传递。
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe")) {
            UseShellExecute=false,CreateNoWindow=!showWindow,WindowStyle=showWindow?ProcessWindowStyle.Normal:ProcessWindowStyle.Hidden,WorkingDirectory=directory,
            Arguments=(showWindow?"/d /k ":"/d /c ")+command };
        foreach(var pair in variables)info.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(info)??throw new InvalidOperationException("命令未启动");
    }
    public void Window(GestureTarget target,WindowAction action) {if(Win.PaneWindow(target.Window,(int)action)==0)throw new InvalidOperationException("窗口操作失败");}
    public void Open(string target) {Guard();using var p=Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}
    public void Pause()=>pause();
}

internal static class Win
{
    const string Dll="Pane.Native.dll";
    [DllImport(Dll)] internal static extern int PaneStart(int observe);
    [DllImport(Dll)] internal static extern int PaneEnterTestDesktop();
    [DllImport(Dll)] internal static extern void PaneLeaveTestDesktop();
    [DllImport(Dll)] internal static extern ulong PanePolicyBegin();
    [DllImport(Dll)] internal static extern void PanePressPolicyBegin(int enabled,int timeout,int fullscreen,int paused,int superButton,int hold,int superFullscreen);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern void PanePressPolicyRule(string path,string? windowClass,int disabled,int super);
    [DllImport(Dll)] internal static extern void PanePressPolicyCommit();
    [DllImport(Dll)] internal static extern void PanePolicyCommit(ulong revision);
    [DllImport(Dll)] internal static extern int PanePolicyDirty();
    [DllImport(Dll)] internal static extern int PaneWGesturesPresent();
    [DllImport(Dll)] internal static extern void PaneStop();
    [DllImport(Dll)] internal static extern void PaneSetWakeEvent(nint handle);
    [DllImport(Dll)] internal static extern int PanePoll(out NativeEvent value);
    [DllImport(Dll)] internal static extern int PaneOverflow();
    [DllImport(Dll)] internal static extern void PaneCancelCapture();
    [DllImport(Dll)] internal static extern void PaneCancelCommands();
    [DllImport(Dll)] internal static extern int PaneBeginInput();
    [DllImport(Dll)] internal static extern void PaneEndInput();
    [DllImport(Dll)] internal static extern void PaneSuperPolicy(int button,int hold);
    [DllImport(Dll)] internal static extern void PanePolicy(nint window,uint pid,int x,int y,int dpi,int blocked,int enabled,int timeout);
    [DllImport(Dll)] internal static extern void PaneStats(out ulong count,out double mean,out double max,out int error,out int unhook);
    [DllImport(Dll)] internal static extern int PaneKey(ushort key,int down,int unicode);
    [DllImport(Dll)] internal static extern int PaneReplay(int button);
    [DllImport(Dll)] internal static extern int PaneWindow(nint window,int action);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneExplorer(nint target,StringBuilder output,int capacity);
    [DllImport(Dll,CharSet=CharSet.Unicode)] internal static extern int PaneSelection(nint target,StringBuilder output,int capacity);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeEvent {public int Kind,X,Y,Value;public nint Window;public uint Pid,Dpi;}
    [StructLayout(LayoutKind.Sequential)] internal struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] internal struct Monitor {public uint Size;public Rect Bounds,Work;public uint Flags;}
    [StructLayout(LayoutKind.Sequential)] internal struct Message {public nint Window;public uint Id;public nuint W;public nint L;public uint Time;public Point Point;public uint Private;}
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassNameW(nint window,StringBuilder value,int count);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern short VkKeyScanW(char character);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point,uint flags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfoW(nint monitor,ref Monitor info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor,int kind,out uint x,out uint y);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window,int id);
    [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message msg,nint window,uint min,uint max,uint remove);
    [DllImport("user32.dll",SetLastError=true)] internal static extern uint MsgWaitForMultipleObjectsEx(uint count,nint[] handles,uint milliseconds,uint mask,uint flags);
    [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern bool QueryFullProcessImageNameW(nint handle,uint flags,StringBuilder path,ref uint capacity);
}

public static class WindowTargets
{
    public static GestureTarget Describe(nint window,GesturePoint start,GesturePoint end)
    {
        Win.GetWindowThreadProcessId(window,out uint pid);var path=new StringBuilder(32768);uint capacity=32768;
        var h=Win.OpenProcess(0x1000,false,pid);try {if(h!=0)Win.QueryFullProcessImageNameW(h,0,path,ref capacity);}finally {if(h!=0)Win.CloseHandle(h);}
        var cls=new StringBuilder(256);Win.GetClassNameW(window,cls,cls.Capacity);
        return new(window,pid,path.ToString(),cls.ToString(),start,end);
    }
    internal static (GestureTarget Target,int Dpi,bool Fullscreen) UnderCursor()
    {
        Win.GetCursorPos(out var p);var h=Win.GetAncestor(Win.WindowFromPoint(p),2);
        // 跳过远程控制的分层鼠标穿透叠加层；不把该层当成全屏应用。
        while(h!=0 && (Win.GetWindowLongPtrW(h,-20).ToInt64()&0x80020)==0x80020) {
            h=Win.GetWindow(h,2);
            while(h!=0 && (!Win.IsWindowVisible(h)||!Win.GetWindowRect(h,out var r)||p.X<r.Left||p.X>=r.Right||p.Y<r.Top||p.Y>=r.Bottom))h=Win.GetWindow(h,2);
        }
        var target=Describe(h,new(p.X,p.Y),new(p.X,p.Y));var monitor=Win.MonitorFromPoint(p,2);Win.GetDpiForMonitor(monitor,0,out uint dpi,out _);
        var info=new Win.Monitor{Size=(uint)Marshal.SizeOf<Win.Monitor>()};bool full=false;
        if(target.WindowClass is not ("Progman" or "WorkerW")&&Win.GetMonitorInfoW(monitor,ref info)&&Win.GetWindowRect(h,out var rect))
            full=rect.Left<=info.Bounds.Left&&rect.Top<=info.Bounds.Top&&rect.Right>=info.Bounds.Right&&rect.Bottom>=info.Bounds.Bottom;
        return (target,(int)(dpi==0?96:dpi),full);
    }
}
