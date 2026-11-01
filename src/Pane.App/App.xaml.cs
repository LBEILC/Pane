using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views;
using System.Text.Json;
using Windows.Foundation;
namespace Pane;
public sealed partial class App : Application
{
    public bool IsProduction => RuntimeMode.Current.IsProduction;
    public Pane.Contracts.IAutostart Autostart {get;private set;}=null!;
    readonly bool controlsEnabled=!RuntimeMode.Current.IsProduction&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_RESULT"));
    readonly bool demo=Environment.GetEnvironmentVariable("PANE_DEMO")=="1";
    readonly string output=Environment.GetEnvironmentVariable("PANE_RESULT")??Path.Combine(AppContext.BaseDirectory,"pane");
    readonly Microsoft.UI.Dispatching.DispatcherQueue queue=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    readonly CancellationTokenSource cancellation=new();
    Task? listener;
    WindowsXamlManager? manager;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? timer;
    public Pane.Contracts.IAiService Ai {get;private set;}=null!;
    public Pane.Contracts.IAiSetup AiSetup {get;private set;}=null!;
    Pane.Services.Ai.AiService? aiService;
    public SettingsStore Store {get;private set;}=null!;
    public GlassParameters Glass {get;internal set;}=null!;   // 安装模式由 SetupHost 设成它自己的那份
    public Pane.Contracts.IFileSearchStatus FileSearchStatus {get;private set;}=null!;
    public Pane.Contracts.IFileIcons FileIcons {get;private set;}=new Pane.Services.Icons.EmptyFileIcons();
    Pane.Services.Launcher.LauncherSearch? launcherSearch;
    public Pane.Contracts.IClipboardHistory ClipboardHistory {get;private set;}=null!;
    Pane.Services.Clipboard.ClipboardHistory? clipboardService;
    Pane.Services.Clipboard.WindowsClipboardSource? clipboardSource;
    ShellHost? shell;
    public event EventHandler<HotkeyConflict>? HotkeyOccupied;
    public IReadOnlyList<HotkeyConflict> HotkeyConflicts=>shell?.Conflicts??Array.Empty<HotkeyConflict>();
    readonly Dictionary<string,GlassPanel> panels=[];
    GlassPanel? settings,current;
    GestureOverlay? gestures;
    bool closing,ready;
    string? pendingAck;
    int cycleIndex=-1;
    bool cycleHiding,cycleRecorded;
    public App(){InitializeComponent();}
    /// <summary>设置页录热键期间暂停业务热键，应急退出仍可使用。</summary>
    // 设计（012 补丁跟进）：设置窗口销毁后托管堆很小、GC 迟迟不跑，XAML 元素背后的大块原生内存就一直不释放，
    // 每开关一次涨几 MiB。关闭后主动回收两次（第二次收掉终结器释放后才变成垃圾的对象）。
    // 不在界面线程上 WaitForPendingFinalizers：XAML 对象的释放要回到界面线程，等它可能死锁。
    void ReclaimAfterSettings(){
        GC.Collect(2,GCCollectionMode.Forced,true);
        var again=queue.CreateTimer();again.Interval=TimeSpan.FromSeconds(2);again.IsRepeating=false;
        again.Tick+=(t,_)=>{t.Stop();GC.Collect(2,GCCollectionMode.Forced,true);};again.Start();
    }
    public void SetHotkeysSuspended(bool suspended){if(closing)return;shell?.SetHotkeysSuspended(suspended);gestureService?.SetHotkeysSuspended(suspended);}
    protected override void OnLaunched(LaunchActivatedEventArgs args){
        if(SetupEntry.Active){
            // 设计预览排查用：PANE_RESULT 设了就把界面异常写进 .error（正式安装模式不写文件）
            var previewLog=SetupEntry.Preview!=null?Environment.GetEnvironmentVariable("PANE_RESULT"):null;
            if(previewLog!=null)UnhandledException+=(_,e)=>File.WriteAllText(previewLog+".error",e.Exception.ToString());
            try{setupHost=new SetupHost(this);}catch(Exception e) when(previewLog!=null){File.WriteAllText(previewLog+".error",e.ToString());throw;}
            return;}
        if(controlsEnabled)Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception error)ReportCrash(error);if(queue.HasThreadAccess)Shutdown();else {var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);if(queue.TryEnqueue(()=>{try{Shutdown();}finally{done.TrySetResult();}}))done.Task.Wait(TimeSpan.FromSeconds(8));}};
        UnhandledException+=(_,e)=>{ReportCrash(e.Exception);Shutdown();e.Handled=true;};
        try {
            GlassParameters.InitializeResources(Resources);queue.EnsureSystemDispatcherQueue();manager=WindowsXamlManager.InitializeForCurrentThread();
            string data=RuntimeMode.Current.DataDirectory;
            FileIcons=demo?new Pane.DesignData.FileIconsSample(data):new Pane.Services.Icons.FileIcons(data);   /* 设计：演示模式按扩展名取真实系统图标 */
            Store=new SettingsStore(data,GlassParameters.Defaults());Store.CorruptFile+=(_,_)=>Ack("settings-recovered");Store.Load();if(!demo)Fonts=new Pane.Services.FontCatalog();ApplyUiFont();/* 设计：界面字体 */
            var autostart=new Pane.Services.AutostartService(IsProduction,Environment.ProcessPath!,Store,CreateStartupRegistry());Autostart=autostart;autostart.Align();
            aiService=new Pane.Services.Ai.AiService(Store);AiSetup=aiService;Ai=demo?new Pane.DesignData.AiSample():aiService;
            Glass=new GlassParameters(Store.Current.General);
            Store.Changed+=(_,_)=>{Glass.Apply(Store.Current.General);shell?.Apply(Store.Current);ApplySuperSettings();folderJump?.Apply(Store.Current.FolderJump);};
            if(Enum.TryParse<ThemeMode>(Environment.GetEnvironmentVariable("PANE_THEME"),true,out var theme))Glass.Apply(Glass.Current with {Theme=theme});
            if(!demo){FileSelection=new Pane.Services.FileSelection();Renamer=new Pane.Services.Rename.BatchRenamer();}
            selectionReader=new Pane.Services.SelectionReader(new Pane.Services.WindowsSelectionPlatform(),()=>Store.Current.SuperPanel.ClipboardFallback,files:FileSelection as Pane.Services.FileSelection);
            Pane.Contracts.ISelectionActions actions=demo?new Pane.DesignData.SelectionActionsSample():new Pane.Services.SelectionActions();
            int startupLevel=MemoryStartupLevel();LauncherView? launcher=null;
            if(!demo&&startupLevel>=5){shell=new ShellHost(Command,Store.Current);shell.HotkeyOccupied+=(_,conflict)=>{if(controlsEnabled)File.AppendAllText(output+".hotkey-conflicts",$"{conflict.Target};{conflict.Error}\n");HotkeyOccupied?.Invoke(this,conflict);};}
            StartWinV();
            if(!demo&&startupLevel>=5)FolderJump=new Pane.Services.FolderJumpService();
            hotkeysReadyMs=(DateTime.UtcNow-System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
            if(!demo&&startupLevel>=6)StartGestures(data);
            if(demo)ClipboardHistory=new Pane.DesignData.ClipboardSample();
            else {clipboardSource=new(Store,Environment.GetEnvironmentVariable("PANE_OBSERVE")=="1"||Environment.GetEnvironmentVariable("PANE_VERIFY")=="1");clipboardService=new(Store,clipboardSource,new Pane.Services.Clipboard.WindowsClipboardWriter(),new Pane.Services.Clipboard.WindowsClipboardImages());ClipboardHistory=clipboardService;}
            var everything=new Pane.Services.Launcher.EverythingSearch();FileSearchStatus=everything;
            if(startupLevel>=1){
            Files=demo?new Pane.DesignData.FileSearchSample():new Pane.Services.Launcher.FileSearch(new Pane.Services.Launcher.EverythingIpc(),new Pane.Services.Launcher.WindowsLauncherExecutor(command=>queue.TryEnqueue(()=>Command(command))));
            if(!demo)launcherSearch=new(Store,new Pane.Services.Launcher.WindowsAppCatalog(data),everything,new Pane.Services.Launcher.WindowsLauncherExecutor(command=>queue.TryEnqueue(()=>Command(command))));
            if(!demo){WebSuggestions=new Pane.Services.Launcher.WebSuggestions(()=>Store.Current.Launcher.WebSuggestions);TerminalHistory=launcherSearch!.TerminalHistory;}
            launcher=new LauncherView((Pane.Contracts.ILauncherSearch?)launcherSearch??new Pane.DesignData.LauncherSampleSearch(),Ai,actions,demo?new Pane.DesignData.LauncherAttachmentActionsSample():new Pane.Services.Clipboard.ClipboardAttachmentActions(launcherSearch!),new Pane.Services.Clipboard.LauncherClipboardHistory(ClipboardHistory,Store));panels.Add("launcher",new("启动器",launcher,launcher,Glass));
            panels["launcher"].PositionStore=Store;panels["launcher"].HideOnBlur=()=>Store.Current.Launcher.HideOnBlur;WireSecondary(launcher,actions);/* 设计接线：二级窗口 */}
            if(startupLevel>=2){var clipboard=new ClipboardView(ClipboardHistory);clipboard.SendToLauncher+=(_,entry)=>{launcher!.AttachNext(entry);Command("show-launcher");};/* 设计接线：剪贴板「交给启动器」 */panels.Add("clipboard",new("剪贴板",clipboard,clipboard,Glass){Placement=PanelPlacement.Pointer});}
            if(startupLevel>=3){var super=new SuperView(Ai,actions);superView=super;secondaryActions??=actions;WireSuper(super);panels.Add("super",new("超级面板",super,super,Glass){Placement=PanelPlacement.Pointer});}
            foreach(var panel in panels.Values){WatchFrames(panel);panel.Prepare();}
            if(startupLevel>=4){gestures=new GestureOverlay(Glass);gestures.TickRequested+=WakeFrames;}/* 设计：手势轨迹与提示框，手势服务的接线由工程做 */
            listener=SingleInstance.Listen(command=>queue.TryEnqueue(()=>{Command(command);Ack("forwarded");}),cancellation.Token);
            if(IsProduction&&!demo)Updates=new Pane.Services.Updates.UpdateService(Pane.Services.Setup.SetupFiles.VersionOf(Environment.ProcessPath!)??"0.1.0",data,()=>Store.Current.Update.AutoUpdate,path=>{
                var start=new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(path))!};start.ArgumentList.Add("--auto-update");
                using var installer=System.Diagnostics.Process.Start(start)??throw new IOException("无法启动安装程序");Shutdown();
            });
            StartUpdates();/* 设计接线：软件内更新 */StartBackground();StartReleaseProbe();
            timer=queue.CreateTimer();timer.Interval=TimeSpan.FromMilliseconds(8);timer.Tick+=(_,_)=>{try{Tick();}finally{if(!NeedsFrames)timer.Stop();}};timer.Start();
        }catch(Exception e){ReportCrash(e);Shutdown();}
    }
    SetupHost? setupHost;
    // 显示、动画和诊断控制仍逐帧推进；完全收起后等事件重新叫醒。
    bool VisualFrames=>panels.Values.Any(p=>p.Active)||(settings?.Active??false)||secondaries.Values.Any(p=>p.Active)||(gestures?.Active??false);
    bool NeedsFrames=>!closing&&(!ready||controlsEnabled||pendingAck!=null||cycleIndex>=0||VisualFrames);
    void WatchFrames(GlassPanel panel)=>panel.TickRequested+=WakeFrames;
    void WakeFrames(){if(!queue.HasThreadAccess){queue.TryEnqueue(WakeFrames);return;}if(!closing&&timer is {IsRunning:false})timer.Start();}
    void Command(string command){
        if(!Pane.Services.Clipboard.ClipboardAccess.Allow(command,Store.Current.Clipboard.Enabled,text=>shell?.NotifyOnce(text)))return;
        WakeFrames();
        if(command.StartsWith("tray-menu ",StringComparison.Ordinal)){ShowTrayMenu(command[10..]);return;}/* 设计：托盘菜单 */
        if(command=="show-launcher"&&!demo){launcherSearch?.RefreshIndex();_ = FileSearchStatus.RefreshAsync(cancellation.Token);}
        if(command=="pause-gestures"){if(gestureService!=null)gestureService.SetPaused(!gestureService.IsPaused);return;}
        if(command=="exit"){Shutdown();return;}
        if(command=="hide"){foreach(var p in panels.Values)p.Hide();settings?.Hide();HideSecondaries();gestures?.HideAll();return;}
        if(command=="show-settings"){
            if(settings==null){var view=new SettingsView();if(controlsEnabled)TrackSettings(view);settings=new("设置",view,view,Glass,true);WatchFrames(settings);settings.Hidden+=(_,_)=>{var old=settings;if(controlsEnabled&&old!=null){retiredSettings.Add(new WeakReference(old.View));retiredHosts.Add(new WeakReference(old));}settings=null;if(current==old)current=null;old?.Dispose();(Fonts as Pane.Services.FontCatalog)?.ClearCache();ReclaimAfterSettings();};settings.Prepare();}current=settings;
        }else if(command.StartsWith("show-")&&panels.TryGetValue(command[5..],out var panel)){foreach(var other in panels.Values)if(other!=panel)other.Hide();current=panel;}
        else return;
        current.Show(activate:!demo&&Environment.GetEnvironmentVariable("PANE_VERIFY")!="1");
    }
    readonly Dictionary<string,string> controlPaths=[];
    static readonly (string Name,string Command)[] showCommands=[("launcher","show-launcher"),("clipboard","show-clipboard"),("super","show-super"),("settings","show-settings")];
    // 控制文件每 8ms 轮询，路径在实例生命周期内不变，不必每次分配新字符串。
    bool Take(string command,out string text){if(!controlPaths.TryGetValue(command,out var path)){path=output+"."+command;controlPaths.Add(command,path);}text="";if(!File.Exists(path))return false;try{text=File.ReadAllText(path);File.Delete(path);return true;}catch(IOException e) when((e.HResult&0xffff) is 32 or 33){return false;}}
    void Ack(string suffix){if(controlsEnabled)File.WriteAllText(output+"."+suffix,"done");}
    bool Settled=>panels.Values.All(p=>p.Settled)&&(settings?.Settled??true)&&SecondariesSettled&&(gestures?.Settled??true);
    void Tick(){
        if(closing)return;
        if(clipboardService?.Ready.IsFaulted==true)throw new InvalidOperationException("剪贴板历史初始化失败");
        if(controlsEnabled&&Take("exit",out _)){Shutdown();return;}
        DrainTrail();
        foreach(var panel in panels.Values)panel.Tick();settings?.Tick();TickSecondaries();gestures?.Tick();
        if(!ready&&panels.Values.All(p=>p.Prepared)&&(gestures?.Panel.Prepared??true)&&wallpaperReady&&(clipboardService?.Ready.IsCompletedSuccessfully??true)){ready=true;if(!demo&&Environment.GetEnvironmentVariable("PANE_VERIFY")!="1"&&Environment.GetEnvironmentVariable("PANE_OBSERVE")!="1"&&LaunchArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray())==LaunchCommand.Show)Command("show-launcher");if(IsProduction&&shell!=null){var log=new CrashLog(Store.DirectoryPath);if(log.Pending() is {} crash){shell.NotifyOnce("Pane 上次意外退出，记录在 "+crash);log.MarkNotified(crash);}}if(controlsEnabled)File.WriteAllText(output+".ready",$"pid={Environment.ProcessId};panels={panels.Count};prepared=1;demo={(demo?1:0)};hotkeys={shell?.RegisteredCount??0};tray={(shell?.TrayCreated==true?1:0)};theme={(Glass.Dark?"Dark":"Light")}\n"+(shell==null?"":string.Join("\n",shell.Diagnostics)));}
        if(ready)DesignOpen();
        if(!ready||!controlsEnabled)return;
        if(pendingAck!=null){if(Settled){Ack(pendingAck);pendingAck=null;}return;}
        if(cycleIndex>=0){
            if(!Settled)return;
            if(cycleHiding){cycleIndex++;cycleHiding=false;cycleRecorded=false;if(cycleIndex==60){cycleIndex=-1;Ack("cycled");return;}}
            else if(current!=null&&current.Shown){
                bool valid=current.MaterialAttached;if(!cycleRecorded)File.AppendAllText(output+".cycle-diagnostics",$"{cycleIndex+1};material={valid};clip={valid};shadow={valid};visible={valid}\n");
                if(!valid)throw new InvalidOperationException("合成树未完整挂载");
                cycleRecorded=true;Ack("cycle-"+(cycleIndex+1));
                if(Environment.GetEnvironmentVariable("PANE_CYCLE_CAPTURE")=="1"&&!Take("cycle-next",out _))return;
                current.Hide();cycleHiding=true;return;
            }
            Command("show-"+Pane.Services.Clipboard.ClipboardAccess.Cycle(cycleIndex,Store.Current.Clipboard.Enabled));return;
        }
        foreach(var (name,command) in showCommands)if(Take(command,out var point)){
            Command(command);if(name=="super"&&!string.IsNullOrWhiteSpace(point)){var xy=Pair(point);current!.Show(true,(int)xy.Width,(int)xy.Height,false);}pendingAck="shown-"+name;return;
        }
        if(DiagnosticCommand())return;
        if(Take("import-wgestures",out var source)){ImportGestures(source);return;}
        if(Take("hide",out _)){Command("hide");pendingAck="hidden";return;}
        if(Take("gesture",out var gesture)){DemoGesture(gesture);pendingAck="gesture-shown";return;}
        if(Take("drag",out var delta)){var pair=Pair(delta);current!.SimulateDrag((int)pair.Width,(int)pair.Height);pendingAck="dragged";return;}
        if(Take("reset-position",out _)){current!.ResetPosition();pendingAck="position-reset";return;}
        if(Take("show-at-pointer",out var pointer)){var pair=Pair(pointer);if(current?.Placement!=PanelPlacement.Pointer)throw new InvalidOperationException("当前面板不跟随鼠标");current.Show(true,(int)pair.Width,(int)pair.Height,false);pendingAck="shown-at-pointer";return;}
        if(Take("position-info",out _)){File.WriteAllText(output+".position-info-result",current!.PositionDiagnostics());return;}
        if(Take("theme",out var theme)){Store.Save(Store.Current with {General=Store.Current.General with {Theme=Enum.Parse<ThemeMode>(theme.Trim(),true)}});pendingAck="theme-applied";return;}
        if(Take("glass",out var json)){var command=JsonSerializer.Deserialize(json,SettingsJson.Default.GlassCommand)!;var g=Store.Current.General;Store.Save(Store.Current with {General=(command.Theme==ThemeMode.Dark||command.Theme==ThemeMode.System&&Glass.Dark)?g with {GlassDark=command.Parameters}:g with {GlassLight=command.Parameters}});pendingAck="glass-applied";return;}
        if(Take("radius",out var radius)){Store.Save(Store.Current with {General=Store.Current.General with {PanelRadius=double.Parse(radius,System.Globalization.CultureInfo.InvariantCulture)}});pendingAck="radius-applied";return;}
        if(Take("resize",out var size)){if(current==null)throw new InvalidOperationException("没有当前面板");current.RequestSize(Pair(size));pendingAck="resized";return;}
        if(Take("cycle",out _)){Command("hide");current=null;cycleIndex=0;cycleHiding=false;cycleRecorded=false;return;}
        if(Take("save-settings",out _)){Store.Save(Store.Current);Ack("settings-saved");}
    }
    static Size Pair(string text){var parts=text.Trim().Split(',');return new(double.Parse(parts[0],System.Globalization.CultureInfo.InvariantCulture),double.Parse(parts[1],System.Globalization.CultureInfo.InvariantCulture));}
    void ReportCrash(Exception exception){Environment.ExitCode=1;if(IsProduction)new CrashLog(RuntimeMode.Current.DataDirectory).Write(exception);else if(controlsEnabled)File.WriteAllText(output+".error",exception.ToString());}
    void Shutdown(){
        if(closing)return;closing=true;
        // 某个服务清理失败也继续释放其余钩子、热键与窗口。
        void Clean(Action action){try{action();}catch(Exception error){Environment.ExitCode=1;ReportCrash(error);}}
        Clean(()=>(Updates as IDisposable)?.Dispose());
        Clean(()=>timer?.Stop());cancellation.Cancel();
        Clean(()=>trailListener?.GetAwaiter().GetResult());
        Clean(()=>releaseProbe?.GetAwaiter().GetResult());Clean(()=>listener?.GetAwaiter().GetResult());
        Clean(()=>selectionCancellation?.Cancel());Clean(()=>gestureService?.DisposeAsync().AsTask().GetAwaiter().GetResult());
        if(controlsEnabled&&gestureService!=null)Clean(()=>File.WriteAllText(output+".gesture-stopped",$"unhook={gestureService.Statistics().UnhookSucceeded};pause={gestureService.RegisteredHotkeyCount}"));
        Clean(()=>shell?.Dispose());
        Clean(()=>(FolderJump as IDisposable)?.Dispose());
        if(shell?.CleanupSucceeded==false||ReleaseProbeEnabled&&gestureService?.Statistics().UnhookSucceeded!=1)Environment.ExitCode=1;
        Clean(()=>clipboardService?.Dispose());if(clipboardSource?.Removed==false||clipboardService?.StorageFailures>0)Environment.ExitCode=1;
        if(controlsEnabled&&clipboardSource!=null)Clean(()=>File.WriteAllText(output+".clipboard-stopped",$"registered={clipboardSource.Registered};removed={clipboardSource.Removed};notifications={clipboardSource.Notifications};failures={clipboardSource.Failures};storageFailures={clipboardService?.StorageFailures}"));
        Clean(()=>(FileIcons as IDisposable)?.Dispose());
        Clean(()=>(WebSuggestions as IDisposable)?.Dispose());Clean(()=>aiService?.Dispose());Clean(()=>launcherSearch?.Dispose());Clean(CloseEmptyIsland);Clean(()=>settings?.Dispose());foreach(var w in secondaries.Values.ToArray())Clean(w.Dispose);
        foreach(var panel in panels.Values)Clean(panel.Dispose);
        Clean(()=>gestures?.Dispose());Clean(()=>background?.Close());Clean(()=>Glass?.Dispose());Clean(()=>manager?.Dispose());
        if(controlsEnabled)Clean(()=>File.WriteAllText(output+".stopped",Environment.ExitCode!=0?"cleanup-failed":"hotkeys=0;tray=0;panels=0"));Exit();
    }
}
