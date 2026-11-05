using Pane.Localization;
// 定时窗口（设计负责人，058）。照原型 prototype/timer.html：选动作、选多久以后或指定时刻，摘要一行，「定好」交给计时服务。
// 窗口开着时每秒刷新一次（状态卡的倒计时、摘要里的到点时刻）；收起就停，不在后台空转。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class TimerView : UserControl, ISecondaryView, IDisposable
{
    static readonly int[] PickMinutes = [15, 30, 60, 120];
    static readonly TimeSpan Shortest = TimeSpan.FromMinutes(1), Longest = TimeSpan.FromHours(24);

    readonly IPowerTimer timer;
    readonly PaneSegmented actionSeg, modeSeg;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer clock;
    PowerAction action = PowerAction.Shutdown;
    bool atMode;

    public TimerView(IPowerTimer timer)
    {
        InitializeComponent();
        this.timer = timer;
        actionSeg = new PaneSegmented([("shutdown", Strings.PowerTimer_Shutdown), ("restart", Strings.PowerTimer_Restart), ("sleep", Strings.PowerTimer_Sleep)], "shutdown",
            SmallStrongProto, SmallProto, FillProto.Background, FillProto.BorderBrush);
        actionSeg.Changed += (_, v) => { action = v switch { "restart" => PowerAction.Restart, "sleep" => PowerAction.Sleep, _ => PowerAction.Shutdown }; Refresh(); };
        ActionHost.Child = actionSeg;
        modeSeg = new PaneSegmented([("after", Strings.Timer_After), ("at", Strings.Timer_At)], "after", SmallStrongProto, SmallProto, FillProto.Background, FillProto.BorderBrush);
        modeSeg.Changed += (_, v) => { atMode = v == "at"; Refresh(); };
        ModeHost.Child = modeSeg;
        MinutesBox.Text = "30";
        TimeBox.Text = DefaultTime();
        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromSeconds(1);
        clock.Tick += OnClock;
        timer.Changed += OnTimerChanged;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnKeyDown;
        Refresh();
    }

    // 指定时刻默认填下一个整点之后半小时以内的整点，比如现在 22:41 → 23:00
    static string DefaultTime() => DateTime.Now.AddMinutes(60 - DateTime.Now.Minute).ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture);

    void OnClock(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => Refresh();
    // 可能在后台线程触发
    void OnTimerChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    // 永久销毁时由宿主调用（Unloaded 不一定来）：计时器不参与 XAML 引用跟踪，不退订 Tick 会把视图留住
    public void Dispose()
    {
        Unloaded -= OnUnloaded;
        timer.Changed -= OnTimerChanged;
        clock.Stop();
        clock.Tick -= OnClock;
    }

    // 想定的到点时刻；不合法时给原因
    (DateTimeOffset? At, string? Error) Target()
    {
        var now = DateTimeOffset.Now;
        if (!atMode)
        {
            if (!int.TryParse(MinutesBox.Text.Trim(), out var m)) return (null, Strings.Timer_ErrorNumber);
            var span = TimeSpan.FromMinutes(m);
            if (span < Shortest) return (null, Strings.Timer_ErrorMin);
            if (span > Longest) return (null, Strings.Timer_ErrorMax);
            return (now + span, null);
        }
        var text = TimeBox.Text.Trim().Replace('：', ':');
        if (!TimeOnly.TryParseExact(text, ["H:mm", "HH:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t))
            return (null, Strings.Timer_ErrorTime);
        var at = new DateTimeOffset(DateTime.Today.Add(t.ToTimeSpan()));
        if (at <= now) at = at.AddDays(1);
        if (at - now < Shortest) return (null, Strings.Timer_ErrorMin);
        return (at, null);
    }

    void Refresh()
    {
        var current = timer.Current;
        StateCard.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        if (current is not null)
        {
            LeftText.Text = PowerTimerText.Countdown(current.At - DateTimeOffset.Now);
            AtText.Text = PowerTimerText.State(current);
        }
        AfterRow.Visibility = atMode ? Visibility.Collapsed : Visibility.Visible;
        AtRow.Visibility = atMode ? Visibility.Visible : Visibility.Collapsed;
        var (at, error) = Target();
        TomorrowText.Text = atMode && at is { } a && a.ToLocalTime().Date != DateTime.Today ? Strings.Timer_Tomorrow : "";
        WarnDot.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Foreground = error is null ? SmallStrongProto.Foreground : SmallProto.Foreground;
        SummaryText.Text = error ?? string.Format(Strings.Timer_Summary, (object?)PowerTimerText.Clock(at!.Value), (object?)PowerTimerText.Name(action), (object?)PowerTimerText.Span(at.Value - DateTimeOffset.Now));
        GoButton.Content = current is null ? Strings.Timer_Go : Strings.Timer_Change;
        GoButton.IsEnabled = error is null;
        BuildPicks();
        // TextBox 的清除按钮在模板里，得等它真正显示过才找得到；每次刷新顺手再藏一次（树很小）
        HideClearButton(MinutesBox);
        HideClearButton(TimeBox);
    }

    // 常用时长（原型 .tog）：选中的那个用主题色描边和选中底
    void BuildPicks()
    {
        var chosen = atMode ? -1 : int.TryParse(MinutesBox.Text.Trim(), out var m) ? m : -1;
        Picks.Children.Clear();
        foreach (var minutes in PickMinutes)
        {
            bool on = minutes == chosen;
            var label = minutes < 60 ? string.Format(Strings.PowerTimer_Minutes, (object?)minutes) : string.Format(Strings.PowerTimer_Hours, (object?)(minutes / 60));
            var chip = new Border
            {
                Height = PaneTokens.ControlXs, CornerRadius = new CornerRadius(PaneTokens.ControlXs / 2), Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1),
                Background = on ? SelectedProto.Background : new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderBrush = on ? AccentProto.Background : FillProto.BorderBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontFamily = XsProto.FontFamily, FontSize = XsProto.FontSize, Foreground = (on ? SmallStrongProto : XsProto).Foreground },
            };
            chip.Tapped += (_, _) => { MinutesBox.Text = minutes.ToString(); Refresh(); };
            Picks.Children.Add(chip);
        }
    }

    // 框窄，TextBox 自带的清除按钮会把后面的字挤走（同剪贴板搜索框的做法）
    static void HideClearButton(TextBox box)
    {
        if (Find(box) is { } b) { b.MinWidth = 0; b.Width = 0; b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false; }   // 模板给了最小宽度，只设 Width 不够
        static Button? Find(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Button { Name: "DeleteButton" } b) return b;
                if (Find(child) is { } hit) return hit;
            }
            return null;
        }
    }

    void OnMinutesChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }
    void OnTimeChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }

    void OnGo(object sender, RoutedEventArgs e) => Go();

    void Go()
    {
        var (at, error) = Target();
        if (error is not null || at is null) return;
        try { timer.Schedule(action, at.Value); }
        catch (ArgumentOutOfRangeException) { SummaryText.Text = Strings.Timer_ErrorMin; return; }
        Refresh();
    }

    void OnCancel(object sender, RoutedEventArgs e) => timer.Cancel();

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter: if (GoButton.IsEnabled) Go(); e.Handled = true; break;
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
        }
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(420, 400);
    public Size MinimumSize => new(420, 400);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged { add { } remove { } }
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { Refresh(); clock.Start(); }
    public void OnShown() => MinutesBox.Focus(FocusState.Programmatic);
    public void OnHidden() => clock.Stop();

    /// <summary>从启动器、托盘打开（已开着就提到前面）：保留上次选的动作和时间。</summary>
    public void Open() => Refresh();
}
