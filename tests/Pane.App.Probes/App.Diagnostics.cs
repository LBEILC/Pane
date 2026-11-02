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
        if(Take("service-status",out _)){File.WriteAllText(output+".service-status-result",$"hotkeys={shell?.RegisteredCount??0};pause={gestureService?.RegisteredHotkeyCount??0};state={gestureService?.State};hookError={gestureService?.Statistics().Error}");return true;}
        if(Take("settings-gc",out _)){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();File.WriteAllText(output+".settings-gc-result",$"views={retiredSettings.Count(w=>w.IsAlive)};total={retiredSettings.Count};managed={GC.GetTotalMemory(false)};hosts={retiredHosts.Count(w=>w.IsAlive)};unloaded={settingsUnloaded};hotkeySubscribers={HotkeyOccupied?.GetInvocationList().Count(d=>d.Target is SettingsView)??0}");File.WriteAllLines(output+".settings-children-result",retiredChildren.GroupBy(v=>v.Type).Select(g=>$"{g.Key};alive={g.Count(v=>v.Reference.IsAlive)};observations={g.Count()}"));return true;}
        return false;
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
