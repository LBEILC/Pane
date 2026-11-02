using Pane.Localization;
// 安装界面逻辑（设计负责人）。安装逻辑在 ISetupService（工程，018）；这里只管状态切换和文案。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class SetupView : UserControl, IPanelView
{
    readonly ISetupService setup;
    readonly PaneSwitch start, desktop;
    bool working;
    string installPath;

    public SetupView(ISetupService setup)
    {
        this.setup = setup;
        InitializeComponent();
        var info = setup.Info;
        Logo.Source = WorkingLogo.Source = SetupArt.Logo();
        bool update = info.InstalledVersion is not null;
        VersionText.Text = update ? $"{info.InstalledVersion} → {info.Version}" : string.Format(Strings.Setup_Version, (object?)(info.Version));
        InstallButton.Content = update ? string.Format(Strings.Setup_UpdateVersion, (object?)(info.Version)) : Strings.Setup_InstallNow;
        start = new PaneSwitch(info.StartWithWindows, FillProto.Background, FillProto.BorderBrush, AccentProto.Background);
        desktop = new PaneSwitch(false, FillProto.Background, FillProto.BorderBrush, AccentProto.Background);
        StartHost.Content = start;
        DesktopHost.Content = desktop;
        // 升级、正在运行：提前说一句会发生什么
        var notes = new List<string>();
        // 升级时用户已经知道 Pane 是什么：去掉介绍，换成「会发生什么」
        if (update) { Tagline.Visibility = Visibility.Collapsed; notes.Add(Strings.Setup_KeepData); }
        if (info.PaneRunning) notes.Add(Strings.Setup_StopRunningHint);
        if (notes.Count > 0) { NoteText.Text = string.Join("；", notes); NoteText.Visibility = Visibility.Visible; }
        installPath = info.InstallPath;
        PathText.Text = update ? string.Format(Strings.Setup_UpdatePath, (object?)(info.InstallPath)) : info.InstallPath;
        ChangePath.IsEnabled = !update;
        ChangePath.Opacity = update ? 0.4 : 1;
        WhereText.Text = string.Format(Strings.Setup_InstallSize, (object?)(SetupArt.Size(info.SizeBytes)));
        KeyDown += OnKeyDown;
        // 设计预览：一出来就点主按钮，截进行中 / 完成 / 失败（只在 --setup-preview 的假服务上有意义）
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_SETUP_RUN") == "1") Loaded += (_, _) => OnInstall(this, new RoutedEventArgs());
        // 软件内更新启动的：直接开始，装好后自己打开新版（2026-10-30，023）
        if (setup.AutoUpdate && update) Loaded += (_, _) => OnInstall(this, new RoutedEventArgs());
        Loaded += (_, _) => InstallButton.Focus(FocusState.Programmatic);
    }

    async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (working) return;
        working = true;
        bool update = setup.Info.InstalledVersion is not null;
        WorkingTitle.Text = update ? Strings.Setup_Updating : Strings.Setup_Installing;
        ProgressFill.Width = 0;
        StepText.Text = Strings.Setup_Preparing;
        SetupArt.Show(this, Working);
        CloseButton.IsEnabled = false;
        var progress = new Progress<SetupProgress>(p =>
        {
            ProgressFill.Width = 300 * Math.Clamp(p.Fraction, 0, 1);
            StepText.Text = p.Step;
        });
        try
        {
            await setup.InstallAsync(new(start.IsOn, desktop.IsOn, installPath), progress);
            DoneTitle.Text = update ? Strings.Setup_UpdateComplete : Strings.Setup_InstalledHeading;
            SetupArt.Show(this, Done);
            if (setup.AutoUpdate && update)
            {
                DoneTitle.Text = Strings.Setup_OpeningUpdated;
                await Task.Delay(1200);
                OnLaunch(this, new RoutedEventArgs());
            }
        }
        catch (SetupException ex)
        {
            FailedTitle.Text = update ? Strings.Setup_UpdateFailed : Strings.Setup_InstallFailed;
            ErrorText.Text = ex.Message;
            KeptText.Text = update ? Strings.Setup_ExistingIntact : Strings.Setup_NoPartialFiles;
            SetupArt.Show(this, Failed);
        }
        finally
        {
            working = false;
            CloseButton.IsEnabled = true;
        }
    }

    // 选文件夹：服务负责补上 \Pane、检查能不能写（要管理员权限的、已有别的文件的都拒绝），返回最终路径或原因
    async void OnChangePath(object sender, RoutedEventArgs e)
    {
        var owner = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (await FolderPicker.PickFolderAsync(owner, null, null) is not { } folder) return;
        var check = setup.CheckInstallPath(folder);
        if (check.Error is { } error)
        {
            PathText.Text = $"{check.Path}：{error}";
            PathText.Foreground = PaneTokens.Danger;
            InstallButton.IsEnabled = false;
            return;
        }
        installPath = check.Path;
        PathText.Text = installPath;
        PathText.Foreground = DimProto.Foreground;
        InstallButton.IsEnabled = true;
    }

    void OnLaunch(object sender, RoutedEventArgs e)
    {
        setup.LaunchInstalled();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    void OnClose(object sender, RoutedEventArgs e) { if (!working) CloseRequested?.Invoke(this, EventArgs.Empty); }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !working) { CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    }

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(520, 480);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
}

/// <summary>安装、卸载界面共用的小工具。</summary>
internal static class SetupArt
{
    public static ImageSource? Logo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "pane-app.svg");
        return File.Exists(path) ? new SvgImageSource(new Uri(path)) : null;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => string.Format(Strings.Setup_Bytes, (object?)(bytes)),
    };

    // 切换状态：别的收起，这一页从 0 淡入（OpacityTransition 用令牌的时长）
    public static void Show(FrameworkElement owner, FrameworkElement page)
    {
        foreach (var child in ((Panel)page.Parent).Children)
            if (child is StackPanel s && s != page && s.Visibility == Visibility.Visible) s.Visibility = Visibility.Collapsed;
        page.OpacityTransition ??= new ScalarTransition { Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurNormal")) };
        page.Opacity = 0;
        page.Visibility = Visibility.Visible;
        owner.DispatcherQueue.TryEnqueue(() => page.Opacity = 1);
    }
}
