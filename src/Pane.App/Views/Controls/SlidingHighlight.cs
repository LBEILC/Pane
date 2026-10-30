// 选中高亮平滑滑动（原型 makeHl / moveHl）：圆角矩形 + 1px 描边，画在列表下面的一层（Composition），
// 位置和尺寸用 --dur-normal + --ease-glide 过渡；第一次出现直接定位，不从别处滑过来。
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Pane.Hosting;
using Windows.Foundation;

namespace Pane.Views.Controls;

public sealed class SlidingHighlight
{
    readonly FrameworkElement layer;
    readonly Compositor compositor;
    readonly CompositionRoundedRectangleGeometry geometry;
    readonly CompositionSpriteShape shape;
    readonly ShapeVisual visual;
    readonly Func<Size, double> radius;
    bool visible;

    /// <param name="layer">放在列表下面、和列表同一坐标系的空元素（通常是 Canvas）。</param>
    /// <param name="radius">按高亮尺寸给圆角；不给就用令牌 PaneRadiusItem。</param>
    public SlidingHighlight(FrameworkElement layer, Func<Size, double>? radius = null)
    {
        this.layer = layer;
        this.radius = radius ?? (_ => ((CornerRadius)Application.Current.Resources["PaneRadiusItem"]).TopLeft);
        compositor = ElementCompositionPreview.GetElementVisual(layer).Compositor;
        geometry = compositor.CreateRoundedRectangleGeometry();
        shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeThickness = 1;
        visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(4096, 8192);
        visual.Shapes.Add(shape);
        visual.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(layer, visual);
        layer.ActualThemeChanged += OnThemeChanged;
        layer.Loaded += OnLoaded;
        ApplyColors();
    }

    void OnThemeChanged(FrameworkElement sender, object args) => ApplyColors();
    void OnLoaded(object sender, RoutedEventArgs e) => ApplyColors();

    /// <summary>元素要永久丢弃时调用（设置窗口关闭、分段选择被重建）：摘掉附着的 Composition 视觉并释放。</summary>
    bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        layer.ActualThemeChanged -= OnThemeChanged;
        layer.Loaded -= OnLoaded;
        ElementCompositionPreview.SetElementChildVisual(layer, null);
        visual.Shapes.Clear();
        shape.FillBrush?.Dispose(); shape.StrokeBrush?.Dispose();
        shape.Dispose(); geometry.Dispose(); visual.Dispose();
    }

    public void MoveTo(FrameworkElement target, bool animate = true)
    {
        if (disposed) return;
        var p = target.TransformToVisual(layer).TransformPoint(new Point(0, 0));
        var size = new Size(target.ActualWidth, target.ActualHeight);
        var offset = new Vector2((float)p.X, (float)p.Y);
        var extent = new Vector2((float)size.Width, (float)size.Height);
        geometry.CornerRadius = new Vector2((float)Math.Min(radius(size), Math.Min(size.Width, size.Height) / 2));
        if (!visible || !animate)
        {
            geometry.Offset = offset;
            geometry.Size = extent;
            visual.Opacity = 1;
            visible = true;
            return;
        }
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 1.05f), new Vector2(0.35f, 1f));
        var duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurNormal"));
        var move = compositor.CreateVector2KeyFrameAnimation();
        move.InsertKeyFrame(1, offset, ease);
        move.Duration = duration;
        var grow = compositor.CreateVector2KeyFrameAnimation();
        grow.InsertKeyFrame(1, extent, ease);
        grow.Duration = duration;
        geometry.StartAnimation("Offset", move);
        geometry.StartAnimation("Size", grow);
    }

    public void Hide()
    {
        visual.Opacity = 0;
        visible = false;
    }

    void ApplyColors()
    {
        var theme = (ResourceDictionary)GlassParameters.Tokens.ThemeDictionaries[layer.ActualTheme == ElementTheme.Dark ? "Dark" : "Light"];
        shape.FillBrush = compositor.CreateColorBrush((Windows.UI.Color)theme["PaneFillSelectedColor"]);
        shape.StrokeBrush = compositor.CreateColorBrush((Windows.UI.Color)theme["PaneStrokeColor"]);
    }
}
