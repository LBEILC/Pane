using Pane.Localization;
// 到点前提醒（设计负责人，058）。只在提醒阶段存在：宿主进入提醒时建、退出提醒（执行 / 取消 / 推迟）时收起并释放。
// 倒数每 250 毫秒刷新一次，只在面板开着的这一分钟里跑。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Contracts;
using Pane.Hosting;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class PowerReminderView : UserControl, IPanelView, IDisposable
{
    readonly IPowerTimer timer;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer clock;

    public PowerReminderView(IPowerTimer timer)
    {
        InitializeComponent();
        this.timer = timer;
        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromMilliseconds(250);
        clock.Tick += OnClock;
        Refresh();
    }

    void OnClock(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => Refresh();

    void Refresh()
    {
        if (timer.Current is not { } c) return;
        ActionIcon.Glyph = PowerTimerText.Glyph(c.Action);
        TitleText.Text = string.Format(Strings.Reminder_Title, (object?)PowerTimerText.Name(c.Action));
        CountText.Text = PowerTimerText.Countdown(c.At - DateTimeOffset.Now);
    }

    void OnPostpone(object sender, RoutedEventArgs e) => timer.Postpone(TimeSpan.FromMinutes(10));
    void OnCancel(object sender, RoutedEventArgs e) => timer.Cancel();

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(320, 132);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested { add { } remove { } }
    public UIElement? DragRegion => null;
    public void OnShowing(PanelShowContext context) { Refresh(); clock.Start(); }
    public void OnShown() { }
    public void OnHidden() => Dispose();
    // 计时器不参与 XAML 引用跟踪，不退订 Tick 会把视图留住；宿主销毁面板时也会调用
    public void Dispose() { clock.Stop(); clock.Tick -= OnClock; }
}
