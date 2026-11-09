using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Modules;
using Pane.Views;
namespace Pane;

public sealed partial class App
{
    public IModuleCatalog Modules { get; private set; } = null!;
    readonly ClipboardBridge clipboardBridge = new();
    bool gestureModule, superModule;
    readonly List<(string Id,WeakReference View,WeakReference Host)> retiredModules=[];
    LauncherView? launcherView;
    public string? LastModuleCommandResult { get; private set; }
    string ModuleDiagnostics()
    {
        int folder = (folderJump as Pane.Services.FolderJumpService)?.DiagnosticBits ?? 0;
        var lines = Modules.Modules.Select(m => { var s = Modules.StateOf(m.Id); return $"module.{m.Id}={s.Status};enabled={s.Enabled};problem={s.Problem}"; });
        return string.Join("\n", lines) + $"\nretiredViews={retiredModules.Count(x=>x.View.IsAlive)};retiredHosts={retiredModules.Count(x=>x.Host.IsAlive)};mousehook={(gestureService?.HookActive==true?1:0)};keyhook={(shell?.KeyboardHookActive==true?1:0)};folderhook={folder & 3};foldertimer={(folder & 4)!=0};folderkeys={folder >> 3};recognition={gestureService?.RecognitionEnabled==true};hotkeys={shell?.RegisteredCount??0};pause={gestureService?.RegisteredHotkeyCount??0};gestureHotkeys={gestureService?.DiagnosticHotkeys};clipboard={clipboardSource?.Registered==true};panels={string.Join(',',panels.Keys)};overlay={gestures!=null};secondary={string.Join(',',secondaries.Keys)};powerTimer={PowerTimer != null};powerTimers={(PowerTimer as Pane.Services.PowerTimerService)?.ArmedTimers ?? 0};powerListeners={shell?.PowerTimeListeners ?? 0};powerWakeups={(PowerTimer as Pane.Services.PowerTimerService)?.Wakeups ?? 0};index={launcherSearch?.ProcessIndex?.Outcome??"none"}\n" + (shell==null?"":string.Join("\n",shell.Diagnostics));
    }
    bool moduleControlBusy;
    bool ModuleControls()
    {
        if(moduleControlBusy)return true;
        if(Take("module-status",out _)){File.WriteAllText(output+".module-status-result",ModuleDiagnostics());return true;}
        if(Take("module-open",out var owner))
        {
            if(owner.Trim()==ModuleIds.Ai){ShowTranslate(null);ShowChat(null,null);}
            else if(owner.Trim()==ModuleIds.Rename)ShowRename([],0);
            else if(owner.Trim()==ModuleIds.Launcher)ShowFiles("Pane055-no-match-"+Guid.NewGuid().ToString("N"),null);
            File.WriteAllText(output+".module-open-result",ModuleDiagnostics());return true;
        }
        if(Take("module-command",out var command)){Command(command.Trim());File.WriteAllText(output+".module-command-result",LastModuleCommandResult??"none");return true;}
        if(!Take("module-set",out var value))return false;
        _=ApplyModuleControl(value);return true;
    }
    async Task ApplyModuleControl(string value)
    {
        moduleControlBusy=true;
        try
        {
            var parts=value.Trim().Split('=');
            await Modules.ApplyAsync(Modules.Preview(parts[0],parts[1]=="1"),cancellation.Token);
            // 外壳热键在独立消息线程完成重绑。
            await Task.Delay(100,cancellation.Token);
            File.WriteAllText(output+".module-set-result",ModuleDiagnostics());
        }
        catch(Exception e){File.WriteAllText(output+".module-set-result","error="+e);}
        finally{moduleControlBusy=false;}
    }
    Task OnUi(Func<Task> work)
    {
        async Task Run(){await work();if(shell!=null)await shell.FlushAsync();}
        if (queue.HasThreadAccess) return Run();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () => { try { await Run(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }))
            done.SetException(new InvalidOperationException("界面线程已退出")); // 不翻译：内部异常
        return done.Task;
    }
    async Task StartModules()
    {
        ClipboardHistory = clipboardBridge;
        secondaryActions = demo ? new Pane.DesignData.SelectionActionsSample() : new Pane.Services.SelectionActions();
        FileSelection = demo ? new Pane.DesignData.RenameSample() : new Pane.Services.FileSelection();
        selectionReader = new Pane.Services.SelectionReader(new Pane.Services.WindowsSelectionPlatform(),
            () => Store.Current.SuperPanel.ClipboardFallback, files: FileSelection as Pane.Services.FileSelection);
        // 新能力只需新增一个类，在此登记；显示顺序由这个列表决定。
        var host = new ModuleHost([new LauncherModule(this), new ClipboardModule(this), new GesturesModule(this),
            new SuperModule(this), new AiModule(this), new FolderJumpModule(this), new RenameModule(this), new PowerTimerModule(this)], Store,
            () => WinVService?.State is WinVState.On or WinVState.Pending, OnUi);
        Modules = host;
        host.Changed += (_, _) => { shell?.Apply(ModuleShellSettings()); WakeFrames(); };
        await host.InitializeAsync(cancellation.Token);
    }
    Pane.Settings.PaneSettings ModuleShellSettings()
    {
        var s = Store.Current;
        if (Modules == null) return s with { Modules = new() { [ModuleIds.Launcher] = false, [ModuleIds.Clipboard] = false } };
        return s with { Modules = new(s.Modules ?? []) { [ModuleIds.Launcher] = Modules.IsActive(ModuleIds.Launcher),
            [ModuleIds.Clipboard] = Modules.IsActive(ModuleIds.Clipboard) },
            Clipboard = s.Clipboard with { Enabled = Modules.IsActive(ModuleIds.Clipboard) } };
    }
    bool AllowModule(string id)
    {
        if (Modules == null || Modules.IsActive(id)) { LastModuleCommandResult = "accepted:" + id; return true; }
        LastModuleCommandResult = "disabled:" + id;
        string name = id switch { ModuleIds.Launcher => Strings.Common_Launcher, ModuleIds.Clipboard => Strings.Common_Clipboard,
            ModuleIds.SuperPanel => Strings.Common_SuperPanel, ModuleIds.Ai => "AI", ModuleIds.Rename => Strings.Common_Rename,
            ModuleIds.Gestures => Strings.Module_Gestures_Name, _ => Strings.Module_FolderJump_Name };
        var text = string.Format(Strings.Module_NotEnabled, (object?)name);
        gestures?.Hint(new(GestureHintKind.Notice, [], CommandName: text));
        shell?.NotifyOnce(text);
        return false;
    }
    void AddModulePanel(string id, string title, Microsoft.UI.Xaml.Controls.UserControl view, IPanelView model, PanelPlacement placement = PanelPlacement.UpperCenter)
    {
        var panel = new GlassPanel(title, view, model, Glass) { Placement = placement };
        panels.Add(id, panel); WatchFrames(panel); panel.Prepare();
    }
    void RemoveModulePanel(string id)
    {
        if (!panels.Remove(id, out var panel)) return;
        if(controlsEnabled)retiredModules.Add((id,new WeakReference(panel.View),new WeakReference(panel)));
        if (current == panel) current = null;
        panel.View.OnHidden(); panel.Dispose(); ReclaimModuleViews();
    }
    void RemoveSecondary(params SecondaryId[] ids)
    {
        bool removed=false;
        foreach (var id in ids) if (secondaries.Remove(id, out var panel))
        {
            removed=true;
            if(controlsEnabled)retiredModules.Add((id.ToString(),new WeakReference(panel.View),new WeakReference(panel)));
            secondaryBounds[id] = (panel.Origin.X, panel.Origin.Y, panel.Width, panel.Height);
            if (secondaryRelease.Remove(id, out var release)) release();   // 设计加：退订视图上指向宿主的事件，否则宿主回收不了
            if (current == panel) current = null;
            panel.View.OnHidden(); panel.Dispose();
        }
        if(removed)ReclaimModuleViews();
    }
    // 停止方法返回后再回收：此刻模块字段和局部变量仍可能持有视图，立即 GC 无法释放它们。
    void ReclaimModuleViews() => queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ReclaimAfterSettings);
    async Task UpdateMouse()
    {
        if (!gestureModule && !superModule)
        {
            var old = gestureService; gestureService = null;
            if (old != null) await old.DisposeAsync();
            // 监听器捕获自己的服务与令牌，不会在重建后读到下一代对象。
            trailCancellation?.Cancel();
            if (trailListener != null) await trailListener;
            trailCancellation?.Dispose(); trailCancellation = null; trailListener = null;
            return;
        }
        if (gestureService == null && !demo) StartGestures(Store.DirectoryPath);
        gestureService?.SetRecognitionEnabled(gestureModule);
        ApplySuperSettings(); ApplyGestureDisplay();
        if (gestureService != null)
        {
            try { await gestureService.Ready; }
            catch (InvalidOperationException e) { throw new InvalidOperationException(Strings.Module_MouseHookFailed, e); }
        }
    }
    abstract class Capability(App app, string id, ModuleCategory category, string glyph, params string[] optional) : ICapabilityModule
    {
        protected App App { get; } = app;
        public ModuleInfo Info { get; } = new(id, category, glyph, true, optional.Select(d => new ModuleDependency(d, DependencyKind.Optional)).ToArray());
        public abstract Task StartAsync(CancellationToken token);
        public abstract Task StopAsync(CancellationToken token);
    }
    sealed class LauncherModule(App app) : Capability(app, ModuleIds.Launcher, ModuleCategory.Search, "search", ModuleIds.Ai, ModuleIds.Clipboard, ModuleIds.Rename)
    {
        public override Task StartAsync(CancellationToken token)
        {
            var a = App; a.FileIcons=a.demo?new Pane.DesignData.FileIconsSample(a.Store.DirectoryPath):new Pane.Services.Icons.FileIcons(a.Store.DirectoryPath);
            var everything = new Pane.Services.Launcher.EverythingSearch(); a.FileSearchStatus = everything;
            if (!a.demo)
            {
                a.launcherSearch = new(a.Store, new Pane.Services.Launcher.WindowsAppCatalog(a.Store.DirectoryPath), everything, new Pane.Services.Launcher.WindowsLauncherExecutor(c => a.queue.TryEnqueue(() => a.Command(c))));
                a.launcherSearch.PowerTimerProvider = () => a.PowerTimer; a.WebSuggestions = new Pane.Services.Launcher.WebSuggestions(() => a.Store.Current.Launcher.WebSuggestions);
                a.TerminalHistory = a.launcherSearch.TerminalHistory;
                a.WebSearchHistory = a.launcherSearch.WebSearchHistory;
                SettingsView.PortableApps = a.launcherSearch;
                SettingsView.AppAliases = a.launcherSearch;
            }
            if(a.demo)a.WebSearchHistory=new Pane.DesignData.WebSearchHistorySample(()=>a.Store.Current.Launcher.RememberWebSearches);
            a.EngineIcons=new Pane.Services.Launcher.EngineIcons(a.Store.DirectoryPath,!a.demo);
            a.Files = a.demo ? new Pane.DesignData.FileSearchSample() : new Pane.Services.Launcher.FileSearch(new Pane.Services.Launcher.EverythingIpc(), new Pane.Services.Launcher.WindowsLauncherExecutor(c => a.queue.TryEnqueue(() => a.Command(c))), a.launcherSearch!.OpenFileAsync);
            var view = new LauncherView((ILauncherSearch?)a.launcherSearch ?? new Pane.DesignData.LauncherSampleSearch { PinnedApps = () => a.Store.Current.Launcher.PinnedApps }, a.Ai, a.secondaryActions!,
                a.demo ? new Pane.DesignData.LauncherAttachmentActionsSample() : new Pane.Services.Clipboard.ClipboardAttachmentActions(a.launcherSearch!),
                new Pane.Services.Clipboard.LauncherClipboardHistory(a.ClipboardHistory, a.Store));
            a.launcherView = view; a.AddModulePanel("launcher", Strings.Common_Launcher, view, view);
            a.panels["launcher"].PositionStore = a.Store; a.panels["launcher"].HideOnBlur = () => a.Store.Current.Launcher.HideOnBlur && !a.clipboardOverLauncher;   // 剪贴板叠在上面时不算失焦
            a.WireSecondary(view, a.secondaryActions!);
            return Task.CompletedTask;
        }
        public override async Task StopAsync(CancellationToken token)
        {
            var a = App; a.RemoveSecondary(SecondaryId.Files); a.RemoveModulePanel("launcher"); a.launcherView = null;
            SettingsView.AppAliases = null;
            SettingsView.PortableApps = null; a.launcherSearch?.Dispose(); a.launcherSearch = null; (a.WebSuggestions as IDisposable)?.Dispose(); a.WebSuggestions = null;
            a.TerminalHistory = null; a.Files = null; a.FileSearchStatus=null!;
            a.WebSearchHistory=null;
            if(a.EngineIcons is Pane.Services.Launcher.EngineIcons engines){engines.Dispose();await engines.Completion;}
            a.EngineIcons=null;
            var icons=a.FileIcons;a.FileIcons=new Pane.Services.Icons.EmptyFileIcons();(icons as IDisposable)?.Dispose();
            if(icons is Pane.Services.Icons.FileIcons real)await real.Completion;
        }
    }
    sealed class ClipboardModule(App app) : Capability(app, ModuleIds.Clipboard, ModuleCategory.Clipboard, "clipboard", ModuleIds.Launcher, ModuleIds.Ai)
    {
        public override async Task StartAsync(CancellationToken token)
        {
            var a = App;
            IClipboardHistory history;
            if (a.demo) { history = new Pane.DesignData.ClipboardSample(); a.clipboardBridge.Attach(history); }
            else
            {
                a.clipboardSource = new(a.Store, Environment.GetEnvironmentVariable("PANE_OBSERVE") == "1" || Environment.GetEnvironmentVariable("PANE_VERIFY") == "1");
                a.clipboardService = new(a.Store, a.clipboardSource, new Pane.Services.Clipboard.WindowsClipboardWriter(), new Pane.Services.Clipboard.WindowsClipboardImages());
                await a.clipboardService.Ready; history = a.clipboardService; a.clipboardBridge.Attach(history);
                a.EnsurePanelKeys();   // 和超级面板共用（079）
            }
            var view = new ClipboardView(history);
            view.SendToLauncher += (_, entry) => { if (a.AllowModule(ModuleIds.Launcher)) { a.launcherView?.AttachNext(entry); a.Command("show-launcher"); } };
            a.AddModulePanel("clipboard", Strings.Common_Clipboard, view, view, PanelPlacement.Pointer);
            a.WireClipboardLauncher(a.panels["clipboard"], view);
        }
        public override async Task StopAsync(CancellationToken token)
        {
            var a = App;
            if (a.WinVService is { State: WinVState.On or WinVState.Pending } winv)
            {
                await winv.DisableAsync(false, token);
                if (winv.Error != null) throw new InvalidOperationException(winv.Error);
            }
            a.RemoveModulePanel("clipboard"); a.clipboardBridge.Attach(null);
            a.ReleasePanelKeys(false, a.superModule);
            try { a.clipboardService?.Dispose(); }
            finally { a.clipboardService = null; a.clipboardSource = null; }
        }
    }
    sealed class GesturesModule(App app) : Capability(app, ModuleIds.Gestures, ModuleCategory.Mouse, "mouse-pointer-2")
    {
        public override async Task StartAsync(CancellationToken token)
        {
            var a = App; a.gestureModule = true; a.gestures = new(a.Glass); a.gestures.TickRequested += a.WakeFrames;
            await a.UpdateMouse();
        }
        public override async Task StopAsync(CancellationToken token)
        {
            var a = App; a.gestureModule = false; a.gestureService?.SetRecognitionEnabled(false);
            bool reclaim=a.gestures!=null;
            a.gestures?.Dispose(); a.gestures = null; await a.UpdateMouse(); if(reclaim)a.ReclaimModuleViews();
        }
    }
    sealed class SuperModule(App app) : Capability(app, ModuleIds.SuperPanel, ModuleCategory.Mouse, "circle-dot", ModuleIds.Ai, ModuleIds.Rename)
    {
        public override async Task StartAsync(CancellationToken token)
        {
            var a = App; var view = new SuperView(a.Ai, a.secondaryActions!); a.superView = view; a.WireSuper(view); a.EnsurePanelKeys();
            a.AddModulePanel("super", Strings.Common_SuperPanel, view, view, PanelPlacement.Pointer); a.superModule = true; await a.UpdateMouse();
        }
        public override async Task StopAsync(CancellationToken token)
        {
            var a = App; a.superModule = false; a.selectionCancellation?.Cancel(); a.RemoveModulePanel("super"); a.superView = null; a.ReleasePanelKeys(a.panels.ContainsKey("clipboard"), false); await a.UpdateMouse();
        }
    }
    sealed class AiModule(App app) : Capability(app, ModuleIds.Ai, ModuleCategory.Ai, "sparkles")
    {
        public override Task StartAsync(CancellationToken token) { App.aiService?.SetModuleEnabled(true); return Task.CompletedTask; }
        public override Task StopAsync(CancellationToken token) { App.aiService?.SetModuleEnabled(false); App.RemoveSecondary(SecondaryId.Translate, SecondaryId.Chat); return Task.CompletedTask; }
    }
    sealed class FolderJumpModule(App app) : Capability(app, ModuleIds.FolderJump, ModuleCategory.Files, "folder-input")
    {
        public override Task StartAsync(CancellationToken token) { if (!App.demo) App.FolderJump = new Pane.Services.FolderJumpService(); return Task.CompletedTask; }
        public override Task StopAsync(CancellationToken token)
        {
            (App.folderJump as IDisposable)?.Dispose(); App.FolderJump = null;
            App.RemoveModulePanel("folder-jump"); App.jumpPanel = null; App.jumpView = null; return Task.CompletedTask;
        }
    }
    sealed class PowerTimerModule(App app) : Capability(app, ModuleIds.PowerTimer, ModuleCategory.System, "timer", ModuleIds.Launcher)
    {
        void Finished(object? sender, PowerTimerResult result)
        {
            string action = Pane.Services.Launcher.PowerTimerSource.Name(result.Action);
            if (result.Outcome == PowerTimerOutcome.Missed) App.shell?.NotifyOnce(string.Format(Strings.PowerTimer_Missed, action));
            else if (result.Outcome == PowerTimerOutcome.Failed) App.shell?.NotifyOnce(string.Format(Strings.PowerTimer_Failed, action, result.Detail));
        }
        public override Task StartAsync(CancellationToken token)
        {
            if (App.demo) App.PowerTimer = new Pane.DesignData.PowerTimerSample();
            else
            {
                var executor = new Pane.Services.Launcher.WindowsLauncherExecutor(_ => { });
                var service = new Pane.Services.PowerTimerService(TimeProvider.System,
                    action => executor.CommandAsync(action switch { PowerAction.Shutdown => "shutdown", PowerAction.Restart => "restart", _ => "sleep" }, CancellationToken.None));
                App.PowerTimer = service;
                if (App.shell != null) App.shell.PowerTimeChanged += service.SystemTimeChanged;
            }
            App.PowerTimer.Finished += Finished;
            return Task.CompletedTask;
        }
        public override Task StopAsync(CancellationToken token)
        {
            var service = App.PowerTimer;
            if (service != null) service.Finished -= Finished;
            if (service is Pane.Services.PowerTimerService real && App.shell != null) App.shell.PowerTimeChanged -= real.SystemTimeChanged;
            service?.Cancel(); (service as IDisposable)?.Dispose(); App.PowerTimer = null;
            return Task.CompletedTask;
        }
    }
    sealed class RenameModule(App app) : Capability(app, ModuleIds.Rename, ModuleCategory.Files, "pencil")
    {
        public override Task StartAsync(CancellationToken token) { App.Renamer = App.demo ? new Pane.DesignData.RenameSample() : new Pane.Services.Rename.BatchRenamer(); return Task.CompletedTask; }
        public override Task StopAsync(CancellationToken token) { App.RemoveSecondary(SecondaryId.Rename); App.Renamer = null; return Task.CompletedTask; }
    }
}
