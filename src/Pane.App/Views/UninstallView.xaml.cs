// 卸载界面逻辑（设计负责人）。卸载逻辑在 IUninstallService（工程，018）。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class UninstallView : UserControl, IPanelView
{
    readonly IUninstallService uninstall;
    readonly PaneSwitch removeData;
    bool working;

    public UninstallView(IUninstallService uninstall)
    {
        this.uninstall = uninstall;
        InitializeComponent();
        var info = uninstall.Info;
        Logo.Source = WorkingLogo.Source = SetupArt.Logo();
        AskTitle.Text = $"卸载 Pane {info.InstalledVersion}？";
        DataText.Text = $"设置、剪贴板历史、手势和 AI 登录信息，共 {SetupArt.Size(info.DataBytes)}。不删的话，以后重新安装会接着用";
        removeData = new PaneSwitch(false, FillProto.Background, FillProto.BorderBrush, AccentProto.Background);
        DataHost.Content = removeData;
        if (info.PaneRunning) RunningText.Visibility = Visibility.Visible;
        KeyDown += OnKeyDown;
        // 设计预览：一出来就点主按钮，截进行中 / 完成 / 失败（只在 --setup-preview 的假服务上有意义）
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_SETUP_RUN") == "1") Loaded += (_, _) => OnUninstall(this, new RoutedEventArgs());
        Loaded += (_, _) => UninstallButton.Focus(FocusState.Programmatic);
    }

    async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (working) return;
        working = true;
        bool data = removeData.IsOn;
        ProgressFill.Width = 0;
        StepText.Text = "准备中";
        SetupArt.Show(this, Working);
        CloseButton.IsEnabled = false;
        var progress = new Progress<SetupProgress>(p =>
        {
            ProgressFill.Width = 300 * Math.Clamp(p.Fraction, 0, 1);
            StepText.Text = p.Step;
        });
        try
        {
            await uninstall.UninstallAsync(data, progress);
            DoneText.Text = data ? "数据也一起删掉了。" : $"你的数据还留在 {uninstall.Info.DataPath}，以后重新安装会接着用；不需要的话可以直接删掉这个文件夹。";
            SetupArt.Show(this, Done);
        }
        catch (SetupException ex)
        {
            ErrorText.Text = ex.Message;
            SetupArt.Show(this, Failed);
        }
        finally
        {
            working = false;
            CloseButton.IsEnabled = true;
        }
    }

    void OnClose(object sender, RoutedEventArgs e) { if (!working) CloseRequested?.Invoke(this, EventArgs.Empty); }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !working) { CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    }

    public Size PreferredSize => new(520, 480);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
}
