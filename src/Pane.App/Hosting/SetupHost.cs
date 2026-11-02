using Pane.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Pane.Services.Setup;
using Pane.Contracts;
using Pane.Views;
namespace Pane.Hosting;

// 仅连接设计提供的视图和既有玻璃宿主，不初始化日常运行的后台服务。
internal sealed class SetupHost : IDisposable
{
    readonly WindowsXamlManager manager;
    readonly GlassParameters glass;
    readonly GlassPanel panel;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    bool closed;
    public SetupHost(Application app)
    {
        GlassParameters.InitializeResources(app.Resources);
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        queue.EnsureSystemDispatcherQueue();
        manager = WindowsXamlManager.InitializeForCurrentThread();
        var defaults = GlassParameters.Defaults();
        var installed = SetupIntegration.ReadSettings(SetupEntry.Paths.Data, defaults);
        // 只继承主题、强调色；玻璃材质参数仍采用默认令牌。
        try { glass = new(defaults.General with { Theme = installed.General.Theme, AccentColor = installed.General.AccentColor }); }
        catch (Exception e) when (e is ArgumentException or FormatException or NullReferenceException) { glass = new(defaults.General); }
        ((App)app).Glass = glass;   // 控件（焦点环等）从 App.Glass 读主题色
        IPanelView view;
        Task Operation() => Task.CompletedTask;
        Func<Task> operation = Operation;
        if (SetupEntry.Preview is { } state)
        {
            ShowPreviewBackground();
            // 设计预览：假服务，不碰文件和注册表
            var sample = new Pane.DesignData.SetupSample(state);
            view = SetupEntry.Uninstall ? new UninstallView(sample) : new SetupView(sample);
        }
        else
        {
            var integration = new SetupIntegration(SetupEntry.Paths, defaults);
            var service = new SetupService(SetupEntry.Paths, AppContext.BaseDirectory,
                SetupFiles.VersionOf(Environment.ProcessPath!) ?? "0.1.0", integration, new SetupProcesses(SetupEntry.Paths, false), autoUpdate: SetupEntry.AutoUpdate,
                restoreWinV: directory => new Pane.Services.WinV.WinVReplacement(directory, new Pane.Services.WinV.WindowsWinVRegistry(),
                    new Pane.Services.WinV.ExplorerRestart(new Pane.Services.WinV.WindowsExplorerPlatform()), new Pane.Services.WinV.NoWinVHotkey()).RestoreForUninstallAsync());
            view = SetupEntry.Uninstall ? new UninstallView(service) : new SetupView(service);
            operation = () => service.Operation;
        }
        panel = new(SetupEntry.Uninstall ? Strings.Setup_UninstallTitle : Strings.Setup_InstallTitle, (UIElement)view, view, glass, true);
        panel.Hidden += async (_, _) =>
        {
            // 标题栏关闭或 Esc 也必须等事务完成，不能让后台复制随进程一起中断。
            try { await operation(); } catch (SetupException) { }
            Dispose(); app.Exit();
        };
        panel.Prepare(); panel.Show();
        timer = queue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(8); timer.Tick += (_, _) => panel.Tick(); timer.Start();
    }
    // 设计预览截图用：PANE_WALLPAPER 指定的原型壁纸铺满主屏，放在安装窗口后面，截图里不会出现用户自己的屏幕内容
    Window? background;
    void ShowPreviewBackground()
    {
        if (Environment.GetEnvironmentVariable("PANE_WALLPAPER") is not { Length: > 0 } wallpaper) return;
        background = new Window { Title = "Pane 安装预览背景" }; // 不翻译：演示夹具
        background.Content = new Microsoft.UI.Xaml.Controls.Image { Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(wallpaper)), Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        background.AppWindow.SetPresenter(presenter);
        var monitor = GlassPanel.MonitorFromPoint(new GlassPanel.Point(), 2);
        var info = new GlassPanel.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>() };
        GlassPanel.GetMonitorInfoW(monitor, ref info);
        var r = info.Monitor;
        background.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        background.AppWindow.Show(false);
    }

    public void Dispose()
    {
        background?.Close();
        if (closed) return; closed = true;
        timer?.Stop(); panel.Dispose(); glass.Dispose(); manager.Dispose();
    }
}
