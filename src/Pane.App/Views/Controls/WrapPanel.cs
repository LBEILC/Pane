// 自动换行面板（原型 flex-wrap）：子元素从左到右排，放不下就换行。WinUI 没有自带的 WrapPanel。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pane.Views.Controls;

public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;
    public double VerticalSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > available.Width) { y += row + VerticalSpacing; x = 0; row = 0; }
            x += s.Width + HorizontalSpacing;
            row = Math.Max(row, s.Height);
            width = Math.Max(width, x - HorizontalSpacing);
        }
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, y + row);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += row + VerticalSpacing; x = 0; row = 0; }
            child.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + HorizontalSpacing;
            row = Math.Max(row, s.Height);
        }
        return final;
    }
}
