// 滑杆（原型 input[type=range]）：4px 轨道，已填充部分用主题色；16px 白色圆钮，按住时放大到 1.15（弹簧）。
// 拖动时连续触发 ValueChanged（玻璃参数实时生效），松开时触发 Committed（再存盘）。
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Windows.System;

namespace Pane.Views.Controls;

public sealed partial class PaneSlider : UserControl
{
    const double TrackWidth = 180, Thumb = 16;
    readonly double min, max, step;
    readonly Border fillPart;
    readonly Border thumb = new()
    {
        Width = Thumb, Height = Thumb, CornerRadius = new CornerRadius(Thumb / 2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0, 0, 0)), BorderThickness = new Thickness(0.5),
    };
    double value;
    bool dragging;

    public PaneSlider(double min, double max, double step, double value, Brush fill, Brush stroke, Brush accent)
    {
        this.min = min; this.max = max; this.step = step; this.value = Math.Clamp(value, min, max);
        IsTabStop = true;
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        fillPart = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var root = new Grid { Width = TrackWidth, Height = 18, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(track); root.Children.Add(fillPart); root.Children.Add(thumb);
        Content = root;
        FocusRing.Attach(this, root, 9);
        var visual = ElementCompositionPreview.GetElementVisual(thumb);
        visual.CenterPoint = new Vector3((float)Thumb / 2, (float)Thumb / 2, 0);
        Place();
        root.PointerPressed += (_, e) => { if (!IsEnabled) return; dragging = true; root.CapturePointer(e.Pointer); Grow(true); Drag(e.GetCurrentPoint(root).Position.X); Focus(FocusState.Pointer); e.Handled = true; };
        root.PointerMoved += (_, e) => { if (dragging) Drag(e.GetCurrentPoint(root).Position.X); };
        root.PointerReleased += (_, e) => { if (!dragging) return; dragging = false; root.ReleasePointerCapture(e.Pointer); Grow(false); Committed?.Invoke(this, this.value); e.Handled = true; };
        root.PointerCaptureLost += (_, _) => { if (!dragging) return; dragging = false; Grow(false); Committed?.Invoke(this, this.value); };
        KeyDown += (_, e) =>
        {
            int d = e.Key is VirtualKey.Left or VirtualKey.Down ? -1 : e.Key is VirtualKey.Right or VirtualKey.Up ? 1 : 0;
            if (d == 0) return;
            Set(this.value + d * step); Committed?.Invoke(this, this.value); e.Handled = true;
        };
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
    }

    public double Value { get => value; set { this.value = Math.Clamp(value, min, max); Place(); } }
    public event EventHandler<double>? ValueChanged;
    public event EventHandler<double>? Committed;

    void Drag(double x) => Set(min + Math.Clamp((x - Thumb / 2) / (TrackWidth - Thumb), 0, 1) * (max - min));

    void Set(double v)
    {
        v = Math.Clamp(Math.Round((v - min) / step) * step + min, min, max);
        v = Math.Round(v, 4);
        if (v == value) return;
        value = v; Place(); ValueChanged?.Invoke(this, value);
    }

    void Place()
    {
        double p = max > min ? (value - min) / (max - min) : 0;
        thumb.Margin = new Thickness(p * (TrackWidth - Thumb), 0, 0, 0);
        fillPart.Width = Math.Max(4, Thumb / 2 + p * (TrackWidth - Thumb));
    }

    void Grow(bool down)
    {
        var visual = ElementCompositionPreview.GetElementVisual(thumb);
        var c = visual.Compositor;
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, down ? new Vector3(1.15f, 1.15f, 1) : Vector3.One, PaneSwitch.Ease(c, "PaneEaseSpring"));
        scale.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast") * ((App)Application.Current).Glass.Current.MotionSpeed);
        visual.StartAnimation("Scale", scale);
    }
}
