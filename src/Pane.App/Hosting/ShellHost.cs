using System.Runtime.InteropServices;
using Pane.Settings;
namespace Pane.Hosting;
public sealed record HotkeyConflict(string Target,Hotkey Hotkey,int Error);
internal sealed class ShellHost : IDisposable, Pane.Services.WinV.IWinVHotkey
{
    bool winVRequested;
    volatile bool stopping;
    readonly uint taskbarCreated=RegisterWindowMessageW("TaskbarCreated");
    readonly System.Collections.Concurrent.ConcurrentQueue<(bool Enabled,TaskCompletionSource<bool> Done)> winVRequests=new();
    public event Action<bool>? WinVRegistrationChanged;
    public Task<bool> SetAsync(bool enabled)
    {
        var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        winVRequests.Enqueue((enabled,done));
        if(stopping||!PostMessageW(window,0x8004,0,0))done.TrySetException(new IOException("热键宿主已退出"));
        return done.Task;
    }
    bool ApplyWinV()
    {
        if(registered.Contains(3))
        {
            if(!Native.UnregisterHotKey(shellWindow,3))throw new IOException("无法释放 Win+V 热键");
            registered.Remove(3);
        }
        lock(conflicts)conflicts.RemoveAll(c=>c.Target=="winv");
        bool active=false;
        if(winVRequested&&!suspended)
        {
            var key=new Hotkey(KeyModifiers.Win,86);
            active=clipboard==key&&registered.Contains(2);
            if(!active){Register(3,"winv",key);active=registered.Contains(3);}
        }
        WinVRegistrationChanged?.Invoke(active);
        return active;
    }
    readonly Native.Proc proc;
    readonly nint window;
    readonly Action<string> command;
    readonly List<int> registered=[];
    readonly Microsoft.UI.Dispatching.DispatcherQueue queue;
    readonly Thread thread;
    readonly ManualResetEventSlim ready=new();
    Exception? error;
    NotifyIcon data;
    bool tray;
    volatile bool suspended;
    public bool GesturesPaused {get;set;}
    PaneSettings? currentSettings;
    readonly HashSet<string> notices=[];
    readonly System.Collections.Concurrent.ConcurrentQueue<string> notifications=new();
    public void SetHotkeysSuspended(bool value){if(suspended==value)return;suspended=value;if(currentSettings is {} s)Apply(s);}
    public void NotifyOnce(string text){lock(notices){if(!notices.Add(text))return;}notifications.Enqueue(text);PostMessageW(shellWindow,0x8003,0,0);}
    Hotkey? launcher,clipboard;
    public event EventHandler<HotkeyConflict>? HotkeyOccupied;
    readonly List<HotkeyConflict> conflicts=[];
    public IReadOnlyList<HotkeyConflict> Conflicts {get {lock(conflicts)return conflicts.ToArray();}}
    public List<string> Diagnostics {get;}=[];
    public int RegisteredCount=>registered.Count;
    public bool TrayCreated=>tray;
    public bool CleanupSucceeded {get;private set;}=true;
    const uint Callback=0x8001;
    public ShellHost(Action<string> command,PaneSettings settings) {
        this.command=command;queue=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();proc=WndProc;
        thread=new Thread(()=>{try{
            var wc=new Native.WndClass{size=(uint)Marshal.SizeOf<Native.WndClass>(),proc=proc,name="Pane011Shell",instance=Native.GetModuleHandleW(null)};Native.RegisterClassExW(ref wc);
            shellWindow=Native.CreateWindowExW(0,wc.name!,"Pane",0,0,0,0,0,0,0,wc.instance,0);
            data=new NotifyIcon{Size=(uint)Marshal.SizeOf<NotifyIcon>(),Window=shellWindow,Id=1,Flags=7,Callback=Callback,Icon=AppIcon.Small,Tip="Pane",Info="",Title=""};
            ApplyCore(settings);ready.Set();while(GetMessageW(out var msg,0,0,0)>0){TranslateMessage(ref msg);DispatchMessageW(ref msg);}
        }catch(Exception ex){error=ex;Cleanup();ready.Set();}}){IsBackground=true,Name="Pane 热键与托盘"};
        thread.Start();ready.Wait();window=shellWindow;if(error!=null)throw error;
    }
    nint shellWindow;
    PaneSettings? pending;
    public void Apply(PaneSettings settings){pending=settings;PostMessageW(window,0x8002,0,0);}
    static Hotkey Override(string name,Hotkey value){
        var text=Environment.GetEnvironmentVariable("PANE_DEV_HOTKEYS");if(string.IsNullOrWhiteSpace(text))return value;
        foreach(var pair in text.Split(';')){var parts=pair.Split('=',2);if(parts.Length!=2||parts[0]!=name)continue;
            var keys=parts[1].Split('+');KeyModifiers mods=0;foreach(var k in keys[..^1])mods|=Enum.Parse<KeyModifiers>(k,true);
            string last=keys[^1];ushort vk=last.Equals("Space",StringComparison.OrdinalIgnoreCase)?(ushort)32:last.Length==1?(ushort)char.ToUpperInvariant(last[0]):throw new FormatException("开发热键格式无效");return new(mods,vk);
        }return value;
    }
    void ApplyCore(PaneSettings settings){
        currentSettings=settings;
        var a=Override("launcher",settings.Launcher.Hotkey);var b=Override("clipboard",settings.Clipboard.Hotkey);
        {foreach(var id in registered)Native.UnregisterHotKey(shellWindow,id);registered.Clear();lock(conflicts)conflicts.Clear();launcher=a;clipboard=b;if(!suspended){Register(1,"launcher",a);Register(2,"clipboard",b);}}
        ApplyWinV();
        if(settings.General.ShowTrayIcon&&!tray){tray=Shell_NotifyIconW(0,ref data);if(!tray)throw new InvalidOperationException("托盘创建失败");}
        if(!settings.General.ShowTrayIcon&&tray){Shell_NotifyIconW(2,ref data);tray=false;}
    }
    void Register(int id,string name,Hotkey key){
        var chord=Pane.Services.HotkeyConversions.ToGesture(key);
        uint mods=0x4000;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Ctrl))mods|=2;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Alt))mods|=1;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Shift))mods|=4;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Win))mods|=8;
        bool ok=Native.RegisterHotKey(shellWindow,id,mods,key.VirtualKey);int code=ok?0:Marshal.GetLastWin32Error();if(ok)registered.Add(id);
        lock(Diagnostics)Diagnostics.Add($"{name}={key.Modifiers}+{key.VirtualKey};registered={ok};error={code}");
        if(!ok){var conflict=new HotkeyConflict(name,key,code);lock(conflicts)conflicts.Add(conflict);queue.TryEnqueue(()=>HotkeyOccupied?.Invoke(this,conflict));
            // 设计加（2026-10-30）：注册失败时提示一次，否则按了没反应也不知道为什么
            var what=name=="launcher"?"启动器":"剪贴板";NotifyOnce($"{Pane.Views.Controls.HotkeyBox.Text(key).Replace("Space","空格")} 被其他程序占用，{what}的热键没设上；到 设置 → {what} 里换一个");}
    }
    nint WndProc(nint h,uint m,nuint w,nint l){
        // 资源管理器重启后重新登记已有图标，不改变菜单或通知行为。
        if(taskbarCreated!=0&&m==taskbarCreated){tray=false;if(currentSettings?.General.ShowTrayIcon==true)tray=Shell_NotifyIconW(0,ref data);return 0;}
        if(m==0x8004){while(winVRequests.TryDequeue(out var request)){try{winVRequested=request.Enabled;request.Done.TrySetResult(ApplyWinV());}catch(Exception e){request.Done.TrySetException(e);}}return 0;}
        if(m==0x312){Dispatch(w==1?"show-launcher":"show-clipboard");return 0;}
        if(m==0x8003){while(notifications.TryDequeue(out var text)){data.Info=text;data.Title="Pane";data.Flags=0x10;Shell_NotifyIconW(1,ref data);data.Flags=7;}return 0;}
        if(m==0x8002){if(pending is {} s)ApplyCore(s);return 0;}
        // 设计改（2026-10-30）：右键弹 Pane 自己的玻璃菜单（App.Tray.cs），不再用系统菜单。
        // 位置在这里、右键那一刻取：图标在任务栏折叠区里时，焦点一换折叠区就收起，之后再取就不准了
        if(m==Callback){if((uint)l==0x202)Dispatch("show-launcher");if((uint)l==0x205){
            Native.GetCursorPos(out var point);Native.SetForegroundWindow(h);
            Dispatch($"tray-menu {point.X},{point.Y}");
        }return 0;}
        if(m==0x10){Cleanup();PostQuitMessage(0);return 0;}
        return Native.DefWindowProcW(h,m,w,l);
    }
    void Dispatch(string value)=>queue.TryEnqueue(()=>command(value));
    void Cleanup(){
        stopping=true;
        while(winVRequests.TryDequeue(out var request))request.Done.TrySetException(new IOException("热键宿主已退出"));
        foreach(var id in registered)CleanupSucceeded &= Native.UnregisterHotKey(shellWindow,id);registered.Clear();
        if(tray){CleanupSucceeded &= Shell_NotifyIconW(2,ref data);tray=false;}
        if(data.Icon!=0){DestroyIcon(data.Icon);data.Icon=0;}
        if(shellWindow!=0){Native.DestroyWindow(shellWindow);shellWindow=0;}
    }
    public void Dispose(){if(thread.IsAlive){PostMessageW(window,0x10,0,0);thread.Join();}ready.Dispose();}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct NotifyIcon {
        public uint Size;public nint Window;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Title;
        public uint InfoFlags;public Guid Guid;public nint Balloon;
    }
    [StructLayout(LayoutKind.Sequential)] struct Message {public nint H;public uint M;public nuint W;public nint L;public uint Time;public Native.Point Point;public uint Private;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint m,ref NotifyIcon data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint LoadImageW(nint h,string file,uint type,int x,int y,uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern int GetMessageW(out Message msg,nint h,uint min,uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref Message msg);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
}
