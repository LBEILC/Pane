// 无边框输入框的字形余量（issue 44）：j、f 这类字母的笔画会伸到文字起点左边（或末尾右边），
// TextBox 内部滚动区按自己的视口裁剪，内边距在视口外面，所以加内边距也救不回来，第一个 j 的勾被切掉一截。
// 做法：输入框左右各留 Bleed 的内边距、外边距用 -Bleed 抵消（字的位置不变）；内部滚动区允许内容画出视口；
// 再把输入框自身裁到自己的大小——伸出去的笔画只在这几像素的余量里可见，长文字横向滚动时也不会画到旁边的图标上。
// 用法：<TextBox c:TextBleed.Bleed="4" …/>（不要再另写 Padding 的左右和 Margin 的左右）。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pane.Views.Controls;

public static class TextBleed
{
    public static readonly DependencyProperty BleedProperty =
        DependencyProperty.RegisterAttached("Bleed", typeof(double), typeof(TextBleed), new PropertyMetadata(0.0, OnBleed));

    public static double GetBleed(TextBox target) => (double)target.GetValue(BleedProperty);
    public static void SetBleed(TextBox target, double value) => target.SetValue(BleedProperty, value);

    static void OnBleed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not double bleed || bleed <= 0) return;
        var p = box.Padding;
        box.Padding = new Thickness(bleed, p.Top, bleed, p.Bottom);
        var m = box.Margin;
        box.Margin = new Thickness(-bleed, m.Top, -bleed, m.Bottom);
        box.Loaded -= OnLoaded;
        box.Loaded += OnLoaded;
        box.SizeChanged -= OnSizeChanged;
        box.SizeChanged += OnSizeChanged;
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (Find<ScrollViewer>(box, "ContentElement") is { } viewer) viewer.CanContentRenderOutsideBounds = true;
        Clip(box);
    }

    static void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (sender is TextBox box) Clip(box); }

    // 左右只放开到输入框自己的边；上下仍裁在原来的视口（多行输入框滚动时不露出半行字）
    static void Clip(TextBox box)
    {
        var p = box.Padding;
        var b = box.BorderThickness;
        double top = p.Top + b.Top, height = box.ActualHeight - top - p.Bottom - b.Bottom;
        box.Clip = new RectangleGeometry { Rect = new Rect(0, top, Math.Max(0, box.ActualWidth), Math.Max(0, height)) };
    }

    static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            if (Find<T>(child, name) is { } found) return found;
        }
        return null;
    }
}
