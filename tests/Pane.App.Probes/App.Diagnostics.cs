using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    readonly List<WeakReference> retiredSettings=[],retiredHosts=[];
    readonly List<(string Type,WeakReference Reference)> retiredChildren=[];
    void TrackSettingsChildren(Microsoft.UI.Xaml.DependencyObject element){
        retiredChildren.Add((element.GetType().Name,new WeakReference(element)));
        int count=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for(int i=0;i<count;i++)TrackSettingsChildren(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element,i));
    }
    int settingsUnloaded;
    int MemoryStartupLevel(){
        if(!controlsEnabled||Environment.GetEnvironmentVariable("PANE_OBSERVE")!="1")return 6;
        var text=Environment.GetEnvironmentVariable("PANE_MEMORY_STARTUP_LEVEL");
        if(string.IsNullOrEmpty(text))return 6;
        int level=int.Parse(text);return level is >=0 and <=6?level:throw new ArgumentOutOfRangeException(nameof(level));
    }
    Microsoft.UI.Xaml.Hosting.DesktopWindowXamlSource? emptyIsland;
    nint emptyWindow;
    readonly Native.Proc emptyProc=Native.DefWindowProcW;
    void OpenEmptyIsland(){
        var wc=new Native.WndClass{size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WndClass>(),proc=emptyProc,name="PaneMemoryEmptyIsland",instance=Native.GetModuleHandleW(null)};
        if(Native.RegisterClassExW(ref wc)==0)throw new System.ComponentModel.Win32Exception();
        emptyWindow=Native.CreateWindowExW(0x08000080,wc.name!,"Pane 空内容岛测量",0x80000000,0,0,760,520,0,0,wc.instance,0);
        emptyIsland=new();emptyIsland.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(emptyWindow));
        emptyIsland.Content=new Microsoft.UI.Xaml.Controls.Grid();emptyIsland.SiteBridge.MoveAndResize(new Windows.Graphics.RectInt32(0,0,760,520));emptyIsland.SiteBridge.Show();Native.ShowWindow(emptyWindow,4);
    }
    void CloseEmptyIsland(){
        if(emptyIsland==null)return;
        emptyIsland.Content=null;emptyIsland.Dispose();emptyIsland=null;Native.DestroyWindow(emptyWindow);emptyWindow=0;Native.UnregisterClassW("PaneMemoryEmptyIsland",Native.GetModuleHandleW(null));
    }
    void TrackSettings(SettingsView view)=>view.Unloaded+=(_,_)=>settingsUnloaded++;
    void SelectSettingsSection(int index){
        if(settings==null)throw new InvalidOperationException("settings closed");
        var view=(SettingsView)settings.View;
        if(Environment.GetEnvironmentVariable("PANE_MEMORY_TRACK_CHILDREN")=="1")TrackSettingsChildren(view);
        var sections=(IList)typeof(SettingsView).GetField("sections",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(SettingsView).GetMethod("Show",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[sections[index]]);
    }
    sealed class FixedReader : Pane.Contracts.ISelectionReader { public Task<Pane.Contracts.SelectionResult> ReadAsync(CancellationToken token)=>Task.FromResult(new Pane.Contracts.SelectionResult("fixture",0,Pane.Contracts.SelectionFailure.None)); }
    async Task SuperProbe(){var original=selectionReader;try{selectionReader=new FixedReader();await ShowSuper(new(100,100,Pane.Gestures.GestureButton.X1));Ack("super-tested");}finally{selectionReader=original;}}
    // 027：控制通道本身保活，所以主动停表后逐条验证真实入口能重新叫醒它。
    async Task FramesProbe(){
        try{
            int checks=0;
            async Task Rest(){Command("hide");long end=Environment.TickCount64+5000;while(VisualFrames){if(Environment.TickCount64>end)throw new InvalidOperationException("收起后仍有逐帧工作");await Task.Delay(15);}checks++;}
            void Wake(Action action){timer!.Stop();action();if(!timer.IsRunning)throw new InvalidOperationException("入口没有叫醒界面计时器");checks++;}
            await Rest();
            foreach(var name in new[]{"launcher","clipboard","super","settings"}){
                Wake(()=>Command("show-"+name));if(!VisualFrames)throw new InvalidOperationException("显示中的窗口未保活");checks++;await Rest();
            }
            Wake(()=>panels["launcher"].RequestSize(new(panels["launcher"].Width,panels["launcher"].Height)));await Rest();
            Wake(()=>Glass.Apply(Store.Current.General));await Rest();
            Wake(()=>ShowFiles("fixture",null));await Rest();
            Wake(()=>ShowTranslate(null));await Rest();
            Wake(()=>ShowChat(null,null));await Rest();
            Wake(()=>gestures!.TrailBegin(0,0));if(!VisualFrames)throw new InvalidOperationException("手势开始未保活");checks++;
            Wake(()=>gestures!.TrailEnd());await Rest();
            File.WriteAllText(output+".frames-probed",$"checks={checks};visualIdle={!VisualFrames};input=0");
        }catch(Exception e){File.WriteAllText(output+".error",e.ToString());}
        finally{WakeFrames();}
    }
    bool DiagnosticCommand(){
        if(Take("panelkeys-probe",out _)){_ = PanelKeysProbe();return true;}
        if(Take("panelkeys-end",out _)){PanelKeys?.End();File.WriteAllText(output+".panelkeys-end-result",$"panelkeys={(Pane.Services.PanelKeyRouter.HookInstalled?1:0)}");return true;}
        if(Take("panelkeys-begin",out _)){bool ok=PanelKeys?.Begin(panels["clipboard"].Input,true)==true;File.WriteAllText(output+".panelkeys-begin-result",$"begin={ok};panelkeys={(Pane.Services.PanelKeyRouter.HookInstalled?1:0)}");return true;}
        if(Take("doubletap-probe",out _)){_ = DoubleTapProbe();return true;}
        if(Take("hotkey-probe",out _)){_ = HotkeyProbe();return true;}
        if(Take("words-probe",out _)){_ = WordsHotkeyProbe();return true;}
        if(Take("localization-probe",out _)){File.WriteAllLines(output+".localization-result",new[]{UiLanguage}.Concat(new TrayMenuView().LocalizationProbe()));return true;}
        if(Take("frames-probe",out _)){_ = FramesProbe();return true;}
        if(Take("restart",out _)){Restart();return true;}
        if(Take("fonts-test",out _)){_ = FontsProbe();return true;}
        if(Take("ai-test",out var aiUrl)){_ = AiProbe(aiUrl);return true;}
        if(Take("clipboard-status",out _)){File.WriteAllText(output+".clipboard-status-result",$"count={ClipboardHistory.Snapshot().Count};images={ClipboardHistory.Snapshot().Count(e=>e.Kind==Pane.Contracts.ClipKind.Image)};registered={clipboardSource?.Registered};failures={clipboardSource?.Failures};storageFailures={clipboardService?.StorageFailures}");return true;}
        if(Take("launcher-index",out _)){_ = LauncherIndexProbe();return true;}
        if(Take("launcher-cached",out _)){_ = LauncherCachedProbe();return true;}
        if(Take("empty-island-open",out _)){OpenEmptyIsland();Ack("empty-island-opened");return true;}
        if(Take("empty-island-close",out _)){CloseEmptyIsland();Ack("empty-island-closed");return true;}
        if(Take("memory-managed",out _)){
            var managed=GC.GetTotalMemory(false);var gc=GC.GetGCMemoryInfo();var native=NativeHeapProbe.Read();
            File.WriteAllText(output+".memory-managed-result",managed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(output+".memory-heaps-result",$"{{\"ManagedBytes\":{managed},\"GcCommittedBytesAtLastCollection\":{gc.TotalCommittedBytes},\"NativeBusyBytes\":{native.Busy},\"NativeRegionCommittedBytes\":{native.Committed},\"HeapErrors\":{native.Errors},\"HeapCount\":{native.Count}}}");return true;
        }
        if(Take("super-test",out _)){_ = SuperProbe();return true;}
        if(Take("settings-section",out var section)){SelectSettingsSection(int.Parse(section));pendingAck="section-shown";return true;}
        if(Take("hotkeys-suspend",out var suspend)){SetHotkeysSuspended(bool.Parse(suspend));Ack("hotkeys-requested");return true;}
        if(Take("service-status",out _)){File.WriteAllText(output+".service-status-result",$"hotkeys={shell?.RegisteredCount??0};keyhook={(shell?.KeyboardHookActive==true?1:0)};panelkeys={(Pane.Services.PanelKeyRouter.HookInstalled?1:0)};pause={gestureService?.RegisteredHotkeyCount??0};state={gestureService?.State};hookError={gestureService?.Statistics().Error}");return true;}
        if(Take("settings-gc",out _)){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();File.WriteAllText(output+".settings-gc-result",$"views={retiredSettings.Count(w=>w.IsAlive)};total={retiredSettings.Count};managed={GC.GetTotalMemory(false)};hosts={retiredHosts.Count(w=>w.IsAlive)};unloaded={settingsUnloaded};hotkeySubscribers={HotkeyOccupied?.GetInvocationList().Count(d=>d.Target is SettingsView)??0}");File.WriteAllLines(output+".settings-children-result",retiredChildren.GroupBy(v=>v.Type).Select(g=>$"{g.Key};alive={g.Count(v=>v.Reference.IsAlive)};observations={g.Count()}"));return true;}
        return false;
    }
    async Task DoubleTapProbe(){
        try{
            if(!demo||shell!=null||clipboardSource!=null||!controlsEnabled||string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_INSTANCE"))||string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PANE_DATA_DIR")))throw new InvalidOperationException("双击探针只允许隔离演示实例");
            int checks=0;void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks++;}
            var chord=new Pane.Settings.Hotkey(Pane.Settings.KeyModifiers.Ctrl|Pane.Settings.KeyModifiers.Alt|Pane.Settings.KeyModifiers.Shift,0x86);
            var clipChord=chord with{VirtualKey=0x87};
            var left=Pane.Hosting.ShellHotkeyBindings.ParseOverride("2xLCtrl");
            var right=Pane.Hosting.ShellHotkeyBindings.ParseOverride("2xRCtrl");
            var settings=Store.Current with{Modules=null,General=Store.Current.General with{ShowTrayIcon=false},Launcher=Store.Current.Launcher with{Hotkey=chord},Clipboard=Store.Current.Clipboard with{Enabled=true,Hotkey=clipChord}};
            shell=new Pane.Hosting.ShellHost(_=>{},settings);
            Check(!shell.KeyboardHookActive&&shell.RegisteredCount==2,"组合键不挂键盘钩子："+string.Join(";",shell.Diagnostics));
            async Task Applied(Pane.Settings.PaneSettings next,bool hook,int registered){
                shell.Apply(next);
                // Win+V 请求在同一宿主消息队列中充当设置应用屏障。
                await shell.SetAsync(false);
                Check(shell.KeyboardHookActive==hook&&shell.RegisteredCount==registered,"设置变更立即应用");
            }
            settings=settings with{Launcher=settings.Launcher with{Hotkey=left}};await Applied(settings,true,1);
            shell.SetHotkeysSuspended(true);await shell.SetAsync(false);Check(!shell.KeyboardHookActive&&shell.RegisteredCount==0,"录入卸载钩子");
            shell.SetHotkeysSuspended(false);await shell.SetAsync(false);Check(shell.KeyboardHookActive&&shell.RegisteredCount==1,"录入结束恢复");
            settings=settings with{Launcher=settings.Launcher with{Hotkey=chord},Clipboard=settings.Clipboard with{Hotkey=right}};await Applied(settings,true,1);
            settings=settings with{Clipboard=settings.Clipboard with{Enabled=false}};await Applied(settings,false,1);
            settings=settings with{Clipboard=settings.Clipboard with{Enabled=true,Hotkey=clipChord}};await Applied(settings,false,2);
            settings=settings with{Launcher=settings.Launcher with{Hotkey=left},Clipboard=settings.Clipboard with{Hotkey=left}};await Applied(settings,true,0);Check(shell.Conflicts.Count==0,"重复双击只给启动器");
            bool winV=await shell.SetAsync(true);
            Check(winV||shell.Conflicts.Any(c=>c.Target=="winv"&&c.Error==1409),"Win+V 独立注册或现有占用");Check(shell.RegisteredCount==(winV?1:0)&&shell.KeyboardHookActive,"Win+V 不影响双击");await shell.SetAsync(false);
            settings=settings with{Launcher=settings.Launcher with{Hotkey=left with{VirtualKey=65}},Clipboard=settings.Clipboard with{Enabled=false}};await Applied(settings,false,0);
            Check(shell.Conflicts.Count==1&&shell.Conflicts[0].Error==87,"非法双击错误码 87");
            settings=settings with{Launcher=settings.Launcher with{Hotkey=left}};await Applied(settings,true,0);
            await Applied(settings with{Modules=new(){["launcher"]=false,["clipboard"]=false}},false,0);
            await Applied(settings with{Modules=new(){["launcher"]=false,["clipboard"]=true},Clipboard=settings.Clipboard with{Enabled=true,Hotkey=left}},true,0);
            await Applied(settings with{Modules=new(){["launcher"]=false,["clipboard"]=false}},false,0);
            await Applied(settings,true,0);
            File.WriteAllText(output+".doubletap-result",$"PASS assertions={checks};keyhook=1;gestures=0;clipboard=0;awaitingExit=1");
        }catch(Exception e){File.WriteAllText(output+".error",e.ToString());}
    }
    async Task HotkeyProbe(){
        try{
            if(!demo||shell!=null||clipboardSource!=null)throw new InvalidOperationException("热键探针只能在无全局服务的演示宿主运行");
            int checks=0;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            async Task Settled(Pane.Hosting.GlassPanel panel,bool shown){
                long end=Environment.TickCount64+5000;
                while(!panel.Settled){if(Environment.TickCount64>end)throw new TimeoutException("动画未完成");await Task.Delay(10);}
                Check(panel.Shown==shown,"动画最终目标错误");
                Check(panel.WindowDiagnostics().Contains(shown?"opacity=1;":"opacity=0;"),"动画停在半透明");
            }
            foreach(nuint id in new nuint[]{1,2}){
                string command=Pane.Hosting.ShellHotkeyBindings.Command(id)!;
                var panel=panels[id==1?"launcher":"clipboard"];
                Command(command,0);await Settled(panel,true);
                Command(command,panel.Input);await Settled(panel,false);
                Command(command,0);await Settled(panel,true);
                Command(command,0);Check(panel.Shown,"后台面板应被拉回前台");await Settled(panel,true);
                Command(id==1?"show-launcher":"show-clipboard",panel.Input);Check(panel.Shown,"普通显示不能收起");
                // 模拟 RegisterHotKey 的 MOD_NOREPEAT：一次按下只投递第一条消息。
                Check((Pane.Hosting.ShellHotkeyBindings.NoRepeat&0x4000)!=0,"必须注册 MOD_NOREPEAT");
                bool held=false;int delivered=0;
                for(int repeat=0;repeat<10;repeat++){if(held)continue;held=true;delivered++;Command(command,panel.Input);}
                Check(delivered==1&&!panel.Shown,"按住只收起一次");await Settled(panel,false);
                held=false;
                Command(command,0);await Task.Delay(20);Command(command,panel.Input);await Task.Delay(20);
                Command(command,panel.Input);await Settled(panel,true);
                Command(command,panel.Input);await Task.Delay(20);Command(command,panel.Input);await Task.Delay(20);
                Command(command,panel.Input);await Settled(panel,false);
            }
            Check(Pane.Hosting.ShellHotkeyBindings.Command(3)=="hotkey-clipboard"&&Pane.Hosting.ShellHotkeyBindings.Command(99)==null,"热键编号映射");
            File.WriteAllText(output+".hotkey-result",$"PASS hotkey assertions={checks}; realHotkeys=0; input=0; clipboard=0; finalOpacity=0");
        }catch(Exception e){File.WriteAllText(output+".error",e.ToString());}
    }
    async Task FontsProbe(){
        var watch=System.Diagnostics.Stopwatch.StartNew();var fonts=await Fonts!.GetFamiliesAsync(default);double first=watch.Elapsed.TotalMilliseconds;watch.Restart();await Fonts.GetFamiliesAsync(default);double cached=watch.Elapsed.TotalMilliseconds;
        File.WriteAllText(output+".fonts-result",$"count={fonts.Count};chinese={fonts.Count(f=>f.HasChinese)};firstMs={first:F3};cachedMs={cached:F3}");
        (Fonts as Pane.Services.FontCatalog)?.ClearCache();ReclaimAfterSettings();
    }
    async Task AiProbe(string url){
        try{
            if(IsProduction||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="http"||uri.Host!="127.0.0.1")throw new InvalidOperationException("AI 探针只允许开发实例和回环假服务");
            Store.Save(Store.Current with{Ai=Store.Current.Ai with{Provider=Pane.Settings.AiProvider.Api,ApiBaseUrl=url,ApiModel="fixture-model"}});
            AiSetup.SaveApiKey("fixture-key-015-private");await AiSetup.TestAsync(cancellation.Token);
            File.WriteAllText(output+".ai-test-result","success");
        }catch(Exception){File.WriteAllText(output+".ai-test-result","failed");}
    }
    async Task LauncherCachedProbe(){
        if(launcherSearch?.CachedQuery is not {} query){File.WriteAllText(output+".launcher-cached-result","cache=0");return;}
        var result=await launcherSearch.SearchAsync(query,default);
        using var process=System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(output+".launcher-cached-result",$"apps={result.Items.Count(i=>i.Kind==Pane.Contracts.LauncherItemKind.App)};indexPending={!launcherSearch.Indexing.IsCompleted};startupToSearchMs={(DateTime.UtcNow-process.StartTime.ToUniversalTime()).TotalMilliseconds:F3};cacheReadMs={launcherSearch.CachedReadyMilliseconds:F3};pinyinLoads={Pane.Services.Launcher.PinyinTable.Loads}");
    }
    async Task LauncherIndexProbe(){
        if(launcherSearch==null){File.WriteAllText(output+".launcher-index-result","disabled");return;}
        await launcherSearch.Indexing;
        var m=launcherSearch.Metrics;
        File.WriteAllText(output+".launcher-index-result",m==null?"error="+launcherSearch.IndexError:$"total={m.Total};appsFolder={m.AppsFolder};shortcuts={m.Shortcuts};filtered={m.Filtered};duplicates={m.Duplicates};icons={m.Icons};indexMs={m.Milliseconds:F2}");
        var child=launcherSearch.ProcessIndex;
        File.WriteAllText(output+".launcher-process-result",$"pinyinLoads={Pane.Services.Launcher.PinyinTable.Loads};childPid={child?.LastProcessId};childExit={child?.LastExitCode};childOutcome={child?.Outcome};childMs={child?.LastMilliseconds:F2};childPeakMiB={child?.PeakWorkingSet/1048576.0:F3};cachedApps={launcherSearch.CachedApps};cachedReadyMs={launcherSearch.CachedReadyMilliseconds:F3}");
    }
}

// 只在显式诊断命令下遍历；锁内不分配托管对象，也不写日志。
internal static class NativeHeapProbe
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit,Size=40)]
    struct Entry {
        [System.Runtime.InteropServices.FieldOffset(0)] public nint Data;
        [System.Runtime.InteropServices.FieldOffset(8)] public uint Size;
        [System.Runtime.InteropServices.FieldOffset(14)] public ushort Flags;
        [System.Runtime.InteropServices.FieldOffset(16)] public uint Committed;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern unsafe uint GetProcessHeaps(uint count,nint* heaps);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool HeapLock(nint heap);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool HeapUnlock(nint heap);
    [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool HeapWalk(nint heap,ref Entry entry);
    internal static unsafe (ulong Busy,ulong Committed,int Errors,uint Count) Read(){
        nint* heaps=stackalloc nint[512];uint count=GetProcessHeaps(512,heaps);
        if(count>512)throw new InvalidOperationException("原生堆数量超出探针容量");
        ulong busy=0,committed=0;int errors=0;
        for(int i=0;i<count;i++){
            if(!HeapLock(heaps[i])){errors++;continue;}
            try{var entry=new Entry();while(HeapWalk(heaps[i],ref entry)){
                if((entry.Flags&4)!=0)busy+=entry.Size;
                if((entry.Flags&1)!=0)committed+=entry.Committed;
            }if(System.Runtime.InteropServices.Marshal.GetLastWin32Error()!=259)errors++;}
            finally{HeapUnlock(heaps[i]);}
        }
        return(busy,committed,errors,count);
    }
}
