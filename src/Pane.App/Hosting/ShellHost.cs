using Pane.Localization;
using System.Runtime.InteropServices;
using Pane.Settings;
namespace Pane.Hosting;
public sealed record HotkeyConflict(string Target,Hotkey Hotkey,int Error);
internal sealed class ShellHost : IDisposable, Pane.Services.WinV.IWinVHotkey
{
    // 复用隐藏顶层窗口接收广播，模块停止时移除订阅，不增加线程。
    internal event Action? PowerTimeChanged;
    internal int PowerTimeListeners => PowerTimeChanged?.GetInvocationList().Length ?? 0;
    bool winVRequested;
    volatile bool winVActive;
    public bool OriginalClipboardHotkeySuppressed {get;private set;}
    volatile bool stopping;
    readonly uint taskbarCreated=RegisterWindowMessageW("TaskbarCreated");
    readonly System.Collections.Concurrent.ConcurrentQueue<(bool Enabled,TaskCompletionSource<bool> Done)> winVRequests=new();
    readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> barriers=new();
    // 等待前面的重绑消息完成，不改变 Win+V 的选择。
    internal Task FlushAsync()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        barriers.Enqueue(done);
        if(stopping||!PostMessageW(shellWindow,0x8006,0,0))done.TrySetException(new IOException(Strings.Common_HotkeyHostStopped));
        return done.Task;
    }
    public event Action<bool>? WinVRegistrationChanged;
    public Task<bool> SetAsync(bool enabled)
    {
        var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        winVRequests.Enqueue((enabled,done));
        if(stopping||!PostMessageW(window,0x8004,0,0))done.TrySetException(new IOException(Strings.Common_HotkeyHostStopped));
        return done.Task;
    }
    bool ApplyWinV()
    {
        if(registered.Contains(3))
        {
            if(!Native.UnregisterHotKey(shellWindow,3))throw new IOException(Strings.Common_ReleaseWinVFailed);
            registered.Remove(3);
        }
        lock(conflicts)conflicts.RemoveAll(c=>c.Target=="winv");
        bool active=false;
        if(winVRequested&&!suspended&&(currentSettings is {} settings&&(settings.Modules?.GetValueOrDefault("clipboard",settings.Clipboard.Enabled)??settings.Clipboard.Enabled)))
        {
            var key=new Hotkey(KeyModifiers.Win,86);
            active=clipboard==key&&registered.Contains(2);
            if(!active){Register(3,"winv",key);active=registered.Contains(3);}
        }
        winVActive=active;
        return active;
    }
    readonly Native.Proc proc;
    readonly nint window;
    readonly Action<string> command;
    readonly List<int> registered=[];
    readonly HashSet<Hotkey> registeredChords=[];
    readonly Microsoft.UI.Dispatching.DispatcherQueue queue;
    readonly Thread thread;
    readonly ManualResetEventSlim ready=new();
    Exception? error;
    NotifyIcon data;
    volatile bool tray;
    readonly Func<bool> createTray;
    static readonly uint[] retryDelays=[1000,3000,10000];
    int retryIndex;
    nuint retryTimer;
    void CancelTrayRetry(){
        if(retryTimer!=0)KillTimer(shellWindow,retryTimer);
        retryTimer=0;
    }
    void TryTray(){
        if(tray||currentSettings?.General.ShowTrayIcon!=true)return;
        tray=createTray();
        if(tray){retryIndex=0;return;}
        lock(diagnostics)diagnostics.Add("tray=create-failed");
        if(retryIndex<retryDelays.Length)
            retryTimer=SetTimer(shellWindow,(nuint)(100+retryIndex),retryDelays[retryIndex++],0);
    }
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
    readonly List<string> diagnostics=[];
    public List<string> Diagnostics {get {lock(diagnostics)return [..diagnostics,$"panelkeys={(Pane.Services.PanelKeyRouter.HookInstalled?1:0)}"];}}
    public int RegisteredCount=>registered.Count;
    public bool TrayCreated=>tray;
    volatile bool keyboardStarted;
    public bool KeyboardHookActive=>keyboardStarted&&PaneKeyboardActive()!=0;
    nuint keyboardGeneration;
    void StopKeyboardHook(){if(keyboardStarted){PaneKeyboardStop();keyboardStarted=false;}}
    public bool CleanupSucceeded {get;private set;}=true;
    const uint Callback=0x8001;
    public ShellHost(Action<string> command,PaneSettings settings,Func<bool>? createTray=null) {
        this.createTray=createTray??(()=>Shell_NotifyIconW(0,ref data));
        this.command=command;queue=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();proc=WndProc;
        thread=new Thread(()=>{try{
            var wc=new Native.WndClass{size=(uint)Marshal.SizeOf<Native.WndClass>(),proc=proc,name="Pane011Shell",instance=Native.GetModuleHandleW(null)};Native.RegisterClassExW(ref wc);
            shellWindow=Native.CreateWindowExW(0,wc.name!,"Pane",0,0,0,0,0,0,0,wc.instance,0);
            data=new NotifyIcon{Size=(uint)Marshal.SizeOf<NotifyIcon>(),Window=shellWindow,Id=1,Flags=7,Callback=Callback,Icon=AppIcon.Small,Tip="Pane",Info="",Title=""};
            ApplyCore(settings);ready.Set();while(GetMessageW(out var msg,0,0,0)>0){TranslateMessage(ref msg);DispatchMessageW(ref msg);}
        }catch(Exception ex){error=ex;Cleanup();ready.Set();}}){IsBackground=true,Name="Pane 热键与托盘"}; // 不翻译：线程名称
        thread.Start();ready.Wait();window=shellWindow;if(error!=null)throw error;
    }
    nint shellWindow;
    PaneSettings? pending;
    public void Apply(PaneSettings settings){pending=settings;PostMessageW(window,0x8002,0,0);}
    static Hotkey Override(string name,Hotkey value){
        var text=Environment.GetEnvironmentVariable("PANE_DEV_HOTKEYS");if(string.IsNullOrWhiteSpace(text))return value;
        foreach(var pair in text.Split(';')){var parts=pair.Split('=',2);if(parts.Length!=2||parts[0]!=name)continue;
            return ShellHotkeyBindings.ParseOverride(parts[1]);
        }return value;
    }
    void ApplyCore(PaneSettings settings){
        currentSettings=settings;
        lock(diagnostics)diagnostics.Clear();
        var a=Override("launcher",settings.Launcher.Hotkey);var b=Override("clipboard",settings.Clipboard.Hotkey);
        lock(conflicts)conflicts.Clear();launcher=a;clipboard=b;
        // 先停止旧线程；代数丢弃已经排队的旧配置通知。
        keyboardGeneration++;StopKeyboardHook();
        var words=settings.Words.Hotkey;
        if(words!=null)words=Override("words",words);
        bool wordsEnabled=settings.Modules?.GetValueOrDefault("words",true)??true;
        var memo = (settings.Modules?.GetValueOrDefault("memo",true) ?? true) ? settings.Memo.Hotkey : null;
        registeredChords.Clear();
        bool active=ShellHotkeyBindings.Rebind(registered,id=>Native.UnregisterHotKey(shellWindow,id),Register,a,b,settings.Modules?.GetValueOrDefault("clipboard",settings.Clipboard.Enabled)??settings.Clipboard.Enabled,suspended,settings.Modules?.GetValueOrDefault("launcher",true)??true,settings.Clipboard.KeepHotkeyWithWinV,ApplyWinV,words,wordsEnabled,memo);
        AppBindings.Bind(settings.Launcher.AppHotkeys,!suspended&&(settings.Modules?.GetValueOrDefault("launcher",true)??true),AppExists,(id,key)=>!registeredChords.Contains(key)&&RegisterChord(id,key,out _));
        OriginalClipboardHotkeySuppressed=active&&!settings.Clipboard.KeepHotkeyWithWinV;
        var keys=ShellHotkeyBindings.DoubleTapKeys(a,b,!OriginalClipboardHotkeySuppressed&&(settings.Modules?.GetValueOrDefault("clipboard",settings.Clipboard.Enabled)??settings.Clipboard.Enabled),suspended,settings.Modules?.GetValueOrDefault("launcher",true)??true);
        uint wordsKey=wordsEnabled&&!suspended&&words?.IsValidDoubleTap==true?words.VirtualKey:0u;
        if(wordsKey!=0&&(wordsKey==keys.Launcher||wordsKey==keys.Clipboard)){RegistrationResult("words",words!,false,1409);wordsKey=0;}
        uint memoKey=!suspended && memo?.IsValidDoubleTap==true ? memo.VirtualKey : 0u;
        if(memoKey!=0 && (memoKey==keys.Launcher || memoKey==keys.Clipboard || memoKey==wordsKey)){RegistrationResult("memo",memo!,false,1409);memoKey=0;}
        if(keys.Launcher!=0||keys.Clipboard!=0||wordsKey!=0||memoKey!=0){
            int code=PaneKeyboardStart(shellWindow,0x8005,keyboardGeneration,keys.Launcher,keys.Clipboard,wordsKey,memoKey,settings.General.DoubleTapPauseInFullscreen?1:0);
            keyboardStarted=code==0;
            if(code!=0){if(keys.Launcher!=0)RegistrationResult("launcher",a,false,code);if(keys.Clipboard!=0)RegistrationResult("clipboard",b,false,code);}
            if(memoKey!=0)RegistrationResult("memo",memo!,code==0,code);
            if(wordsKey!=0)RegistrationResult("words",words!,code==0,code);
        }
        lock(diagnostics){diagnostics.RemoveAll(d=>d.StartsWith("keyhook="));diagnostics.Add($"keyhook={(KeyboardHookActive?1:0)}");}
        WinVRegistrationChanged?.Invoke(active);
        if(settings.General.ShowTrayIcon){if(retryTimer==0)TryTray();}
        else{CancelTrayRetry();retryIndex=0;}
        if(!settings.General.ShowTrayIcon&&tray){Shell_NotifyIconW(2,ref data);tray=false;}
    }
    internal Pane.Services.Launcher.AppHotkeyBindings AppBindings {get;} = new();
    internal Func<string,bool> AppExists {get;set;} = _=>false;
    internal Action<string>? PressApp {get;set;}
    void Register(int id,string name,Hotkey key){
        if(key.DoubleTap){RegistrationResult(name,key,false,87);return;}
        bool ok=RegisterChord(id,key,out int code);
        RegistrationResult(name,key,ok,code);
    }
    bool RegisterChord(int id,Hotkey key,out int code){
        var chord=Pane.Services.HotkeyConversions.ToGesture(key);
        uint mods=ShellHotkeyBindings.NoRepeat;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Ctrl))mods|=2;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Alt))mods|=1;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Shift))mods|=4;if(chord.Modifiers.HasFlag(Pane.Gestures.KeyModifiers.Win))mods|=8;
        bool ok=Native.RegisterHotKey(shellWindow,id,mods,key.VirtualKey);code=ok?0:Marshal.GetLastWin32Error();if(ok){registered.Add(id);registeredChords.Add(key);}
        return ok;
    }
    void RegistrationResult(string name,Hotkey key,bool ok,int code){
        lock(diagnostics)diagnostics.Add($"{name}={key.Modifiers}+{key.VirtualKey};registered={ok};error={code}");
        if(!ok){var conflict=new HotkeyConflict(name,key,code);lock(conflicts)conflicts.Add(conflict);queue.TryEnqueue(()=>HotkeyOccupied?.Invoke(this,conflict));
            // 设计加（2026-10-30）：注册失败时提示一次，否则按了没反应也不知道为什么
            var what=name=="launcher"?Strings.Common_Launcher:name=="words"?Strings.Module_Words_Name:name=="memo"?Strings.Memo_Title:Strings.Common_Clipboard;NotifyOnce(string.Format(Strings.Setup_HotkeyConflict, (object?)(Pane.Views.Controls.HotkeyBox.Text(key).Replace("Space",Strings.Common_SpaceKey)), (object?)(what), (object?)(what)));}
    }
    nint WndProc(nint h,uint m,nuint w,nint l){
        // 在广播线程直接清标记，不依赖 UI 调度在系统结束会话前执行。
        if(NativeCrashRecovery.Current?.EndSession(m,w)==true)return 0;
        // 只处理 AUTOMATIC 恢复广播，避免随后 RESUMESUSPEND 重复延长倒数。
        if(m==0x1e || m==0x218 && w==0x12){PowerTimeChanged?.Invoke();return m==0x218?1:0;}
        if(m==0x8006){while(barriers.TryDequeue(out var done))done.TrySetResult();return 0;}
        // 资源管理器重启后重新登记已有图标，不改变菜单或通知行为。
        if(taskbarCreated!=0&&m==taskbarCreated){CancelTrayRetry();retryIndex=0;tray=false;TryTray();return 0;}
        if(m==0x113&&retryTimer!=0&&w==retryTimer){CancelTrayRetry();TryTray();return 0;}
        if(m==0x8004){while(winVRequests.TryDequeue(out var request)){try{winVRequested=request.Enabled;if(currentSettings is {} settings)ApplyCore(settings);request.Done.TrySetResult(winVActive);}catch(Exception e){request.Done.TrySetException(e);}}return 0;}
        if(m==0x312){if(!suspended&&AppBindings.AppFor((int)w) is {} app){PressApp?.Invoke(app);return 0;}if(ShellHotkeyBindings.Command(w) is {} hotkey)Dispatch(hotkey);return 0;}
        if(m==0x8005){if(!suspended&&w==keyboardGeneration&&ShellHotkeyBindings.Command((nuint)l) is {} hotkey)Dispatch(hotkey);return 0;}
        if(m==0x8003){while(notifications.TryDequeue(out var text)){if(!tray)continue;data.Info=text;data.Title="Pane";data.Flags=0x10;Shell_NotifyIconW(1,ref data);data.Flags=7;}return 0;}
        if(m==0x8002){try{if(pending is {} s)ApplyCore(s);}catch(Exception ex){lock(diagnostics)diagnostics.Add($"apply-settings=failed;code={ex.HResult:X8}");}return 0;}
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
        stopping=true;CancelTrayRetry();
        while(barriers.TryDequeue(out var done))done.TrySetException(new IOException(Strings.Common_HotkeyHostStopped));
        keyboardGeneration++;StopKeyboardHook();
        while(winVRequests.TryDequeue(out var request))request.Done.TrySetException(new IOException(Strings.Common_HotkeyHostStopped));
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
    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd,nuint id,uint delay,nint callback);
    [DllImport("user32.dll")] static extern bool KillTimer(nint hwnd,nuint id);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint m,ref NotifyIcon data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint LoadImageW(nint h,string file,uint type,int x,int y,uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern int GetMessageW(out Message msg,nint h,uint min,uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref Message msg);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("Pane.Native.dll",CallingConvention=CallingConvention.Cdecl)] static extern int PaneKeyboardStart(nint window,uint message,nuint generation,uint launcher,uint clipboard,uint words,uint memo,int pauseWhenFull);
    [DllImport("Pane.Native.dll",CallingConvention=CallingConvention.Cdecl)] static extern void PaneKeyboardStop();
    [DllImport("Pane.Native.dll",CallingConvention=CallingConvention.Cdecl)] static extern int PaneKeyboardActive();
}
