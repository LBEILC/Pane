// 自绘输入光标（设计加，2026-10-31，用户反馈浅色模式光标是白的）：WinUI 的光标按它底下的像素反色，
// Pane 的输入框底下是透明的（玻璃在另一扇窗口），反出来总是白色；TextBox 又没有改光标颜色的 API。
// 这里在系统光标正上方盖一条稍宽的文字色竖条把它遮住。系统光标会闪，熄灭那一下会露白，所以这条常亮；
// 也不做位移动画（系统光标是瞬移的，动画途中会露出白的）。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Pane.Views.Controls;

internal sealed class PaneCaret
{
    const double BarWidth = 2;
    readonly TextBox box;
    readonly Canvas layer = new() { IsHitTestVisible = false };
    readonly Rectangle bar = new() { Width = BarWidth, RadiusX = 1, RadiusY = 1, Visibility = Visibility.Collapsed };
    ScrollViewer? scroller;
    FrameworkElement? placeholder;
    double lineHeight;
    bool pending;

    PaneCaret(TextBox box)
    {
        this.box = box;
        layer.Children.Add(bar);
        box.Loaded += (_, _) => Mount();
        box.GotFocus += (_, _) => Update();
        box.LostFocus += (_, _) => Update();
        box.SelectionChanged += (_, _) => Update();
        box.TextChanged += (_, _) => Update();
        box.TextCompositionChanged += (_, _) => Update();
        box.TextCompositionEnded += (_, _) => Update();
        box.SizeChanged += (_, _) => Update();
        box.ActualThemeChanged += (_, _) => bar.Fill = box.Foreground;
        box.RegisterPropertyChangedCallback(Control.ForegroundProperty, (_, _) => bar.Fill = box.Foreground);
    }

    public static void Attach(TextBox box) => _ = new PaneCaret(box);

    // 叠在模板根 Grid 的最上层；Loaded 每次显示都会来，只挂一次
    void Mount()
    {
        if (layer.Parent is null && VisualTreeHelper.GetChildrenCount(box) > 0 && VisualTreeHelper.GetChild(box, 0) is Grid root)
        {
            Grid.SetRowSpan(layer, 16); Grid.SetColumnSpan(layer, 16);
            root.Children.Add(layer);
            scroller = Find(root, "ContentElement") as ScrollViewer;
            placeholder = Find(root, "PlaceholderTextContentPresenter");
            if (scroller is not null) scroller.ViewChanged += (_, _) => Update();
        }
        bar.Fill = box.Foreground;
        Update();
    }

    static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement f && f.Name == name) return f;
            if (Find(child, name) is { } found) return found;
        }
        return null;
    }

    void Update()
    {
        Place();
        // 文字刚变时排版可能还没更新到位，下一帧再对一次
        if (!pending) { pending = true; CompositionTarget.Rendering += Recheck; }
    }

    void Recheck(object? sender, object e)
    {
        CompositionTarget.Rendering -= Recheck;
        pending = false;
        Place();
    }

    void Place()
    {
        if (layer.Parent is null || box.FocusState == FocusState.Unfocused || box.SelectionLength > 0 || !box.IsEnabled)
        {
            bar.Visibility = Visibility.Collapsed;
            return;
        }
        Rect r;
        try
        {
            int i = box.SelectionStart, n = box.Text.Length;
            r = n == 0 ? Empty()
                : i < n ? box.GetRectFromCharacterIndex(i, false)
                : box.GetRectFromCharacterIndex(n - 1, true);
        }
        catch { bar.Visibility = Visibility.Collapsed; return; }
        if (r.Height <= 0) { bar.Visibility = Visibility.Collapsed; return; }
        if (box.Text.Length > 0) lineHeight = r.Height;
        // 可见区域外（多行滚走了）就不画
        if (scroller is not null && (r.Bottom < 0 || r.Top > box.ActualHeight)) { bar.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(bar, r.X - BarWidth / 2 + 0.5);
        Canvas.SetTop(bar, r.Y);
        bar.Height = r.Height;
        bar.Visibility = Visibility.Visible;
    }

    // 没有文字时取不到字符位置：光标在第一行行首，就是占位文字开始的地方
    Rect Empty()
    {
        FrameworkElement? anchor = placeholder is { Visibility: Visibility.Visible, ActualHeight: > 0 } ? placeholder : scroller;
        if (anchor is null) return Rect.Empty;
        var origin = anchor.TransformToVisual(layer).TransformPoint(default);
        if (anchor == scroller) origin = new Point(origin.X + scroller!.Padding.Left, origin.Y + scroller.Padding.Top);
        double height = lineHeight > 0 ? lineHeight : anchor == placeholder ? placeholder!.ActualHeight : box.FontSize * 1.33;
        if (anchor == placeholder && lineHeight > 0) origin.Y += (placeholder!.ActualHeight - lineHeight) / 2;
        return new Rect(origin.X, origin.Y, 0, height);
    }
}
