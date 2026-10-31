// 分段选择（原型 .seg）：中性底 + 细描边，内边距 2；每段等宽、高 28；
// 选中段文字加粗、正文色，选中底色平滑滑过去（共用 SlidingHighlight）。
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Pane.Views.Controls;

public sealed partial class PaneSegmented : UserControl
{
    readonly (string Value, string Label)[] options;
    readonly List<Border> items = [];
    readonly SlidingHighlight highlight;
    readonly TextBlock strongProto, dimProto;
    int selected;

    /// <param name="strongProto">选中文字的样板（正文色）；<paramref name="dimProto"/> 未选中（次要色）。字体、字号取自 strongProto。</param>
    public PaneSegmented(IEnumerable<(string Value, string Label)> options, string value, TextBlock strongProto, TextBlock dimProto, Brush fill, Brush stroke)
    {
        this.options = options.ToArray();
        this.strongProto = strongProto; this.dimProto = dimProto;
        selected = Math.Max(0, Array.FindIndex(this.options, o => o.Value == value));
        IsTabStop = true;
        var radius = ((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft;
        var layer = new Canvas { IsHitTestVisible = false };
        var row = new Grid();
        for (int i = 0; i < this.options.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = new TextBlock { Text = this.options[i].Label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = strongProto.FontFamily, FontSize = strongProto.FontSize };
            var item = new Border { Height = PaneTokens.ControlMd - 4, Padding = new Thickness(10, 0, 10, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = text };
            Grid.SetColumn(item, i);
            int index = i;
            item.Tapped += (_, _) => Select(index, true);
            items.Add(item); row.Children.Add(item);
        }
        var grid = new Grid();
        grid.Children.Add(layer); grid.Children.Add(row);
        var shell = new Grid();
        shell.Children.Add(new Border { Padding = new Thickness(2), CornerRadius = new CornerRadius(radius), Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1), Child = grid });
        Content = shell;
        FocusRing.Attach(this, shell, radius);
        highlight = new SlidingHighlight(layer, _ => Math.Max(3, radius - 2));
        UpdateText();
        // 第一次直接定位，不从别处滑过来
        items[selected].SizeChanged += First;
        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Left && selected > 0) { Select(selected - 1, true); e.Handled = true; }
            if (e.Key == VirtualKey.Right && selected < items.Count - 1) { Select(selected + 1, true); e.Handled = true; }
        };
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        // 设置页每次重建分区都会丢弃旧的分段选择，附着的高亮视觉要跟着释放
        Unloaded += (_, _) => highlight.Dispose();
    }

    void First(object sender, SizeChangedEventArgs e) { ((Border)sender).SizeChanged -= First; highlight.MoveTo(items[selected], false); }

    public string Value => options[selected].Value;
    public event EventHandler<string>? Changed;

    void Select(int index, bool notify)
    {
        if (!IsEnabled || index == selected) return;
        selected = index;
        highlight.MoveTo(items[selected]);
        UpdateText();
        if (notify) Changed?.Invoke(this, Value);
    }

    void UpdateText()
    {
        for (int i = 0; i < items.Count; i++)
        {
            var t = (TextBlock)items[i].Child;
            t.Foreground = i == selected ? strongProto.Foreground : dimProto.Foreground;
            t.FontWeight = i == selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
