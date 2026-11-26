// 滚动区边缘渐隐 + 细滑块（设计负责人，2026-11-26，用户定）。原型 ui/scroll-fade.css、scrollbar.js。
// 1. 渐隐：哪一边还有内容没露出来，哪一边最后 24 px 渐隐成透明；滚到顶 / 底、内容不够长时那一边不渐隐。
//    WinUI 3 没有 OpacityMask：内容照常布局和命中测试，但它的合成层由一张 VisualSurface 画出来，
//    外面套一个渐变遮罩（CompositionMaskBrush）；原内容所在的那层透明度设成 0，只负责输入。
// 2. 细滑块：系统滚动条隐藏（鼠标移上去会展开成带箭头的宽条，和玻璃不搭），右侧画一条宽 4 的细条，
//    滚动或鼠标进来时出现，0.9 秒没动静淡出；移到细条上变宽 6，可以拖、点轨道跳转、滚轮照常。同文件窗口的 ScrollThumb。
//    细条贴右边、离边 2 画在留白里，不压选中高亮（2026-11-26）；可拖的范围仍是 12 宽，透明地伸进内容一点。
// 用法：<c:ScrollFade Grid.Row="1"><ScrollViewer …>…</ScrollViewer></c:ScrollFade>；Thumb="False" 只要渐隐。
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;

namespace Pane.Views.Controls;

[ContentProperty(Name = nameof(Content))]
public sealed partial class ScrollFade : Grid
{
    const float Edge = 24;
    readonly Grid hidden = new(), source = new(), surfaceHost = new() { IsHitTestVisible = false };
    readonly Border track = new() { Width = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = Visibility.Collapsed };
    readonly Border thumb = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(2) };
    UIElement? content;
    ScrollViewer? scroller;
    Compositor? compositor;
    CompositionVisualSurface? surface;
    SpriteVisual? sprite;
    CompositionColorGradientStop? top0, top1, bottom0, bottom1;
    bool topOn, bottomOn, overThumb, dragging, maskFailed;
    double thumbOpacity, dragStartY, dragStartOffset;
    DispatcherQueueTimer? fadeTimer;

    /// <summary>要包的滚动区（通常就是一个 ScrollViewer）。</summary>
    public UIElement? Content
    {
        get => content;
        set
        {
            if (ReferenceEquals(content, value)) return;
            if (content != null) source.Children.Remove(content);
            content = value;
            if (value != null) source.Children.Add(value);
            if (IsLoaded) Hook();
        }
    }

    /// <summary>要不要右侧的细滑块（默认要）。已经自己画了滑块的地方（文件窗口）设 false。</summary>
    public bool Thumb { get; set; } = true;

    public ScrollFade()
    {
        hidden.Children.Add(source);
        Children.Add(hidden);
        Children.Add(surfaceHost);
        track.Child = thumb;
        Children.Add(track);
        track.PointerEntered += (_, _) => { overThumb = true; Wide(true); Fade(1); };
        track.PointerExited += (_, _) => { overThumb = false; if (!dragging) { Wide(false); RestartFade(); } };
        track.PointerPressed += OnTrackPressed;
        track.PointerMoved += OnTrackMoved;
        track.PointerReleased += (_, e) => { EndDrag(); track.ReleasePointerCapture(e.Pointer); };
        track.PointerCaptureLost += (_, _) => EndDrag();
        track.PointerWheelChanged += (_, e) =>
        {
            if (scroller is { } sv) sv.ChangeView(null, sv.VerticalOffset - e.GetCurrentPoint(track).Properties.MouseWheelDelta, null);
            e.Handled = true;
        };
        PointerEntered += (_, _) => ShowThumb();
        PointerExited += (_, _) => RestartFade();
        Loaded += (_, _) => { ElementCompositionPreview.GetElementVisual(thumb).Opacity = (float)thumbOpacity; Hook(); EnsureMask(); };
        Unloaded += (_, _) => fadeTimer?.Stop();
        SizeChanged += (_, _) => { Resize(); Update(); };
    }

    void Hook()
    {
        var sv = content as ScrollViewer ?? Find(content);
        if (ReferenceEquals(sv, scroller)) return;
        if (scroller != null) scroller.ViewChanged -= OnViewChanged;
        scroller = sv;
        if (sv == null) return;
        sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;   // 细滑块代替系统滚动条
        sv.ViewChanged += OnViewChanged;
        sv.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => Update());
        sv.SizeChanged += (_, _) => Update();
        thumb.Background = Application.Current.Resources.TryGetValue("PaneText3Brush", out var b) ? (Brush)b : null;
        Update();
    }

    static ScrollViewer? Find(DependencyObject? root)
    {
        if (root == null) return null;
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (Find(child) is { } found) return found;
        }
        return null;
    }

    void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) { Update(); ShowThumb(); }

    // ---------------- 渐隐 ----------------

    void EnsureMask()
    {
        if (sprite != null || maskFailed) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(source);
            var c = compositor = visual.Compositor;
            surface = c.CreateVisualSurface();
            surface.SourceVisual = visual;
            var gradient = c.CreateLinearGradientBrush();
            gradient.StartPoint = Vector2.Zero; gradient.EndPoint = new Vector2(0, 1);
            top0 = c.CreateColorGradientStop(0, Opaque); top1 = c.CreateColorGradientStop(0.1f, Opaque);
            bottom1 = c.CreateColorGradientStop(0.9f, Opaque); bottom0 = c.CreateColorGradientStop(1, Opaque);
            gradient.ColorStops.Add(top0); gradient.ColorStops.Add(top1); gradient.ColorStops.Add(bottom1); gradient.ColorStops.Add(bottom0);
            var mask = c.CreateMaskBrush();
            mask.Source = c.CreateSurfaceBrush(surface);
            mask.Mask = gradient;
            sprite = c.CreateSpriteVisual();
            sprite.Brush = mask;
            ElementCompositionPreview.SetElementChildVisual(surfaceHost, sprite);
            // 原内容只负责布局和输入；画面由遮罩后的那张图出
            ElementCompositionPreview.GetElementVisual(hidden).Opacity = 0;
            Resize();
            Update();
        }
        catch (Exception)
        {
            // 合成不可用（极少见）：退回不渐隐，内容照常显示
            maskFailed = true;
            try { ElementCompositionPreview.GetElementVisual(hidden).Opacity = 1; } catch (Exception) { }
        }
    }

    static readonly Windows.UI.Color Opaque = Windows.UI.Color.FromArgb(255, 255, 255, 255), Clear = Windows.UI.Color.FromArgb(0, 255, 255, 255);

    void Resize()
    {
        if (surface == null || sprite == null) return;
        var size = new Vector2((float)source.ActualWidth, (float)source.ActualHeight);
        surface.SourceSize = size;
        sprite.Size = size;
        float f = size.Y > 0 ? Math.Min(0.5f, Edge / size.Y) : 0.1f;
        top1!.Offset = f;
        bottom1!.Offset = 1 - f;
    }

    void Update()
    {
        UpdateThumb();
        if (scroller is not { } sv || top0 == null) return;
        bool top = sv.VerticalOffset > 0.5, bottom = sv.VerticalOffset < sv.ScrollableHeight - 0.5;
        if (top != topOn) { topOn = top; AnimateStop(top0, top); }
        if (bottom != bottomOn) { bottomOn = bottom; AnimateStop(bottom0!, bottom); }
    }

    void AnimateStop(CompositionColorGradientStop stop, bool fade)
    {
        var c = compositor!;
        var animation = c.CreateColorKeyFrameAnimation();
        animation.InsertKeyFrame(1, fade ? Clear : Opaque);
        animation.Duration = TimeSpan.FromMilliseconds(120);
        stop.StartAnimation("Color", animation);
    }

    // ---------------- 细滑块 ----------------

    void UpdateThumb()
    {
        if (!Thumb || scroller is not { } sv) { track.Visibility = Visibility.Collapsed; return; }
        double view = sv.ViewportHeight, extent = sv.ExtentHeight;
        if (view <= 0 || extent <= view + 1) { track.Visibility = Visibility.Collapsed; return; }
        track.Visibility = Visibility.Visible;
        double length = track.ActualHeight > 0 ? track.ActualHeight : view;
        double size = Math.Max(28, length * view / extent);
        thumb.Height = size;
        thumb.Margin = new Thickness(0, (length - size) * sv.VerticalOffset / (extent - view), 2, 0);   // 贴右边、离边 2，画在留白里不压内容
    }

    void ShowThumb()
    {
        if (track.Visibility != Visibility.Visible) return;
        Fade(1);
        RestartFade();
    }

    void RestartFade()
    {
        fadeTimer ??= CreateTimer();
        fadeTimer.Stop();
        fadeTimer.Start();
    }

    DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(900);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (!overThumb && !dragging) Fade(0); };
        return timer;
    }

    void Fade(double to)
    {
        if (Math.Abs(thumbOpacity - to) < 0.01) return;
        thumbOpacity = to;
        var visual = ElementCompositionPreview.GetElementVisual(thumb);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, (float)to, PaneSwitch.Ease(visual.Compositor, "PaneEaseSmooth"));
        fade.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast") * ((App)Application.Current).Glass.Current.MotionSpeed + 1);
        visual.StartAnimation("Opacity", fade);
    }

    void Wide(bool wide) { thumb.Width = wide ? 6 : 4; thumb.CornerRadius = new CornerRadius(wide ? 3 : 2); }

    void OnTrackPressed(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(track).Position.Y;
        double top = thumb.Margin.Top, size = thumb.Height;
        if (y < top || y > top + size) ScrollToThumb(y - size / 2);   // 点在细条外：细条中心跳到点击处，再从这里拖
        dragging = true;
        dragStartY = y;
        dragStartOffset = scroller?.VerticalOffset ?? 0;
        track.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void OnTrackMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging || scroller is not { } sv) return;
        double range = track.ActualHeight - thumb.Height;
        if (range <= 0) return;
        var y = e.GetCurrentPoint(track).Position.Y;
        sv.ChangeView(null, dragStartOffset + (y - dragStartY) * sv.ScrollableHeight / range, null, true);
    }

    void ScrollToThumb(double thumbTop)
    {
        if (scroller is not { } sv) return;
        double range = track.ActualHeight - thumb.Height;
        if (range <= 0) return;
        sv.ChangeView(null, Math.Clamp(thumbTop, 0, range) * sv.ScrollableHeight / range, null, true);
    }

    void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        if (!overThumb) { Wide(false); RestartFade(); }
    }
}
