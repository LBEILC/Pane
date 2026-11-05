using Pane.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

/// <summary>托盘菜单每次弹出时的状态（App 填）。热键为空就不显示键帽。</summary>
public sealed record TrayMenuState(string? UpdateVersion, IReadOnlyList<string> LauncherKeys, IReadOnlyList<string> ClipboardKeys,
    bool GesturesAvailable, bool GesturesPaused, IReadOnlyList<string> PauseKeys, bool ClipboardAvailable = true,
    bool LauncherAvailable = true, bool RenameAvailable = false,
    bool TimerAvailable = false, string? TimerStatus = null);   // 058：定时关机能力在运行时列出；有定时时 TimerStatus =「23:00 关机 · 还剩 42 分钟」   // 055：停用的能力不列；RenameAvailable = 批量重命名启用、但启动器和超级面板都停用了

public sealed partial class TrayMenuView : UserControl, IPanelView
{
    /// <summary>选了哪一项：update / launcher / clipboard / rename / timer / timer-cancel / settings / pause / exit。菜单先收起，App 再执行。</summary>
    public event EventHandler<string>? Invoked;
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => null;
    readonly List<Grid> items;
    int selected = -1;

    public TrayMenuView()
    {
        InitializeComponent();
        items = Root.Children.OfType<Grid>().ToList();
        foreach (var item in items)
        {
            item.PointerEntered += (_, _) => Select(items.IndexOf(item));
            item.PointerExited += (_, _) => { if (selected == items.IndexOf(item)) Select(-1); };
            item.Tapped += (_, _) => Run((string)item.Tag);
        }
        KeyDown += OnKeyDown;
    }

    /// <summary>弹出前按当前状态填好；宿主据此量尺寸、算位置。</summary>
    public void Apply(TrayMenuState state)
    {
        var update = state.UpdateVersion is { Length: > 0 } v;
        UpdateHead.Text = update ? string.Format(Strings.Tray_UpdateReady, state.UpdateVersion) : "";
        UpdateHead.Visibility = UpdateItem.Visibility = UpdateSeparator.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        Keys(LauncherKeys, state.LauncherKeys);
        Keys(ClipboardKeys, state.ClipboardKeys);
        ClipboardItem.Visibility = state.ClipboardAvailable ? Visibility.Visible : Visibility.Collapsed;   // 剪贴板关了就不列这一项（033）
        LauncherItem.Visibility = state.LauncherAvailable ? Visibility.Visible : Visibility.Collapsed;
        RenameItem.Visibility = state.RenameAvailable ? Visibility.Visible : Visibility.Collapsed;
        TimerItem.Visibility = state.TimerAvailable ? Visibility.Visible : Visibility.Collapsed;
        TimerText.Text = state.TimerStatus ?? Strings.Tray_TimerOpen;
        TimerCancelItem.Visibility = state.TimerAvailable && state.TimerStatus is not null ? Visibility.Visible : Visibility.Collapsed;
        PauseSeparator.Visibility = PauseItem.Visibility = state.GesturesAvailable ? Visibility.Visible : Visibility.Collapsed;
        PauseIcon.Glyph = state.GesturesPaused ? "check" : "gesture";
        PauseIcon.Foreground = state.GesturesPaused ? AccentProto.Background : IconProto.Background;
        Keys(PauseKeys, state.PauseKeys);
        // 宽度默认 264；热键很长（如 Ctrl + Alt + Shift + W）时按内容放宽，免得名字被挤成「暂…」（issue 11），最宽 400
        Root.Width = double.NaN;
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        width = Math.Clamp(Math.Ceiling(Root.DesiredSize.Width), 264, 400);
        Root.Width = width;
        SizeRequested?.Invoke(this, PreferredSize);
    }

    // 高度按显示的行算：内边距 6 × 2，每项 34，分隔线 11，更新标题 26
    public Size PreferredSize
    {
        get
        {
            double h = 12;
            foreach (var child in Root.Children)
            {
                if (child.Visibility != Visibility.Visible) continue;
                h += child switch { Grid => 34, Border => 11, _ => 26 };
            }
            return new Size(width, h);
        }
    }

    double width = 264;

    void Keys(StackPanel target, IReadOnlyList<string> keys)
    {
        target.Children.Clear();
        var text = ((TextBlock)KbdProto.Child).Style;
        for (int i = 0; i < keys.Count; i++)
        {
            // 双击热键：「左 Ctrl」键帽后跟普通文字「×2」
            if (keys[i] == Controls.HotkeyBox.TwiceMark) { target.Children.Add(new TextBlock { Text = keys[i], VerticalAlignment = VerticalAlignment.Center, FontFamily = PlusProto.FontFamily, FontSize = PlusProto.FontSize, Foreground = PlusProto.Foreground }); continue; }
            if (i > 0) target.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, FontFamily = PlusProto.FontFamily, FontSize = PlusProto.FontSize, Foreground = PlusProto.Foreground });
            target.Children.Add(new Border { Style = KbdProto.Style, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Style = text, Text = keys[i] } });
        }
    }

    List<Grid> Visible => items.Where(i => i.Visibility == Visibility.Visible).ToList();

    void Select(int index)
    {
        selected = index;
        for (int i = 0; i < items.Count; i++)
            items[i].Background = i == index ? HoverProto.Background : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    void Move(int step)
    {
        var visible = Visible;
        if (visible.Count == 0) return;
        int at = selected >= 0 ? visible.IndexOf(items[selected]) : (step > 0 ? -1 : 0);
        at = ((at + step) % visible.Count + visible.Count) % visible.Count;
        Select(items.IndexOf(visible[at]));
    }

    void Run(string id)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        Invoked?.Invoke(this, id);
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down: Move(1); break;
            case VirtualKey.Up: Move(-1); break;
            case VirtualKey.Enter when selected >= 0: Run((string)items[selected].Tag); break;
            case VirtualKey.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }

    public void OnShowing(PanelShowContext context) => Select(-1);
    public void OnShown() => Focus(FocusState.Programmatic);
    public void OnHidden() => Select(-1);
}
