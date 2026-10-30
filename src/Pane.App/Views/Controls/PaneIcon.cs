// Lucide 描边图标（24×24 画布按控件尺寸缩放，线宽随之缩放，和网页 SVG 一致）。
// 用法：<c:PaneIcon Glyph="search" Width="22" Height="22" Foreground="{ThemeResource PaneText2Brush}"/>
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Pane.Views.Controls;

public sealed partial class PaneIcon : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(PaneIcon), new PropertyMetadata(null, (d, _) => ((PaneIcon)d).UpdateGeometry()));

    // 默认取令牌 PaneIconStroke（1.75）；彩色应用图块里的白色图形略粗（1.9）
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(PaneIcon), new PropertyMetadata(1.75, (d, _) => ((PaneIcon)d).path.StrokeThickness = ((PaneIcon)d).StrokeWidth));

    // Lucide 只有线性版：「已固定」等开启状态把图形内部填满（原型 .icon-btn.on svg { fill: currentColor }）
    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(PaneIcon), new PropertyMetadata(false, (d, _) => ((PaneIcon)d).UpdateFill()));

    public bool IsFilled { get => (bool)GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }
    void UpdateFill() => path.Fill = IsFilled ? Foreground : null;

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    readonly Path path = new()
    {
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeThickness = 1.75,
    };

    public PaneIcon()
    {
        IsTabStop = false;
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
        path.Stroke = Foreground;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => { path.Stroke = Foreground; UpdateFill(); });
    }

    void UpdateGeometry()
    {
        // Geometry 不能在多个元素间共享，每个图标各自解析一份
        path.Data = Glyph is { } g && LucideIcons.Data.TryGetValue(g, out var data)
            ? (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data)
            : null;
    }
}
