// 自动换行面板（原型 flex-wrap）：子元素从左到右排，放不下就换行。WinUI 没有自带的 WrapPanel。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pane.Views.Controls;

public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;
    public double VerticalSpacing { get; set; } = 4;
    // 换行判断的余量（issue 29）：125%、150% 缩放下宽度按物理像素取整，量出来的总宽和分到的宽度会差一点点浮点误差，
    // 不留余量时最后一组会被误判成放不下、折到第二行（单行高的提示栏里就被切掉）。小于半个 DIP 的差别不换行。
    const double Slack = 0.5;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            if (child is WrapBreak br) { br.Measure(new Size(0, 0)); if (x > 0 || row > 0) { y += row + VerticalSpacing + br.Extra; x = 0; row = 0; } continue; }
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > available.Width + Slack) { y += row + VerticalSpacing; x = 0; row = 0; }
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
            if (child is WrapBreak br) { if (x > 0 || row > 0) { y += row + VerticalSpacing + br.Extra; x = 0; row = 0; } child.Arrange(new Rect(0, y, 0, 0)); continue; }
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > final.Width + Slack) { y += row + VerticalSpacing; x = 0; row = 0; }
            child.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + HorizontalSpacing;
            row = Math.Max(row, s.Height);
        }
        return final;
    }
}

/// <summary>放进 WrapPanel 里强制换行（原型 .words .br：原文里的换行）；Extra = 换行处比普通行距多空出的高度。</summary>
public sealed partial class WrapBreak : FrameworkElement
{
    public double Extra { get; set; } = 2;
    protected override Size MeasureOverride(Size availableSize) => new(0, 0);
}
