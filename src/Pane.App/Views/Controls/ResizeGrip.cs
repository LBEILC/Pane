// 拖动边缘调整高度（设计负责人，2026-11-23，原型 ui/clipboard.css .clip-grip）：8 高的拖动区，光标上下箭头；
// 靠近或拖动时浮出一条居中细线（36 × 3，次要文字色 70%）。面板自己处理拖动，不走系统缩放、不激活面板。
// Dragged 报累计位移（屏幕物理像素，往下为正；按 DPI 换算由使用方做），Completed 在松手时，Reset 在双击时。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views.Controls;

public sealed partial class ResizeGrip : Grid
{
    readonly Border line;
    bool dragging, hovering;

    public ResizeGrip()
    {
        Height = 8;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // 透明也要命中
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        line = new Border { Width = 36, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
        line.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        line.SetValue(Border.BackgroundProperty, Application.Current.Resources.TryGetValue("PaneText3Brush", out var b) ? b : null);
        Children.Add(line);
        Loaded += (_, _) => line.VerticalAlignment = FromTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        line.Margin = new Thickness(0, 3, 0, 3);
        PointerEntered += (_, _) => { hovering = true; Sync(); };
        PointerExited += (_, _) => { hovering = false; Sync(); };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e);
        PointerCaptureLost += (_, e) => End(e);
        DoubleTapped += (_, e) => { e.Handled = true; Reset?.Invoke(this, EventArgs.Empty); };
    }

    /// <summary>上边缘：往上拖是变高，面板下边不动。</summary>
    public bool FromTop { get; set; }

    public event EventHandler<double>? Dragged;
    public event EventHandler? Started;
    public event EventHandler? Completed;
    public event EventHandler? Reset;

    void Sync() => line.Opacity = hovering || dragging ? 0.7 : 0;

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (!CapturePointer(e.Pointer)) return;
        dragging = true;
        screenStart = ScreenY();
        Sync();
        Started?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        // 窗口跟着变高 / 上移时，窗口内坐标也跟着动；用屏幕上的光标位置算位移才稳
        Dragged?.Invoke(this, ScreenY() - screenStart);
        e.Handled = true;
    }

    double screenStart;
    static double ScreenY() { global::Native.GetCursorPos(out var p); return p.Y; }

    void End(PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleasePointerCaptures();
        Sync();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
