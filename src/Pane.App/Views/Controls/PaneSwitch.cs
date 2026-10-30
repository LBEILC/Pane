// 开关（原型 .switch）：38×22 轨道，16px 白色滑块弹簧移动（--dur-normal + --ease-spring）；
// 按住时滑块拉长到 21px，松开回弹；主题色只出现在「开」的轨道上。
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Hosting;
using Windows.System;

namespace Pane.Views.Controls;

public sealed partial class PaneSwitch : UserControl
{
    readonly Border track = new() { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1) };
    readonly Border on = new() { Width = 38, Height = 22, CornerRadius = new CornerRadius(11) };
    readonly Border knob = new()
    {
        Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(3, 0, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0, 0, 0)), BorderThickness = new Thickness(0.5),
    };
    readonly Visual knobVisual, onVisual;
    bool isOn, pressed;

    /// <param name="fill">轨道底色（--fill-control）；<paramref name="stroke"/> 轨道描边（--stroke）；<paramref name="accent"/> 开启时的轨道色。</param>
    public PaneSwitch(bool value, Brush fill, Brush stroke, Brush accent)
    {
        isOn = value;
        IsTabStop = true;
        track.Background = fill; track.BorderBrush = stroke; on.Background = accent;
        var root = new Grid { Width = 38, Height = 22, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(track); root.Children.Add(on); root.Children.Add(knob);
        Content = root;
        FocusRing.Attach(this, root, 11);
        ElementCompositionPreview.SetIsTranslationEnabled(knob, true);
        knobVisual = ElementCompositionPreview.GetElementVisual(knob);
        onVisual = ElementCompositionPreview.GetElementVisual(on);
        knobVisual.Properties.InsertVector3("Translation", new Vector3(isOn ? 16 : 0, 0, 0));
        onVisual.Opacity = isOn ? 1 : 0;
        PointerPressed += (_, e) => { if (!IsEnabled) return; pressed = true; CapturePointer(e.Pointer); Stretch(true); e.Handled = true; };
        PointerReleased += (_, e) => { if (!pressed) return; pressed = false; ReleasePointerCapture(e.Pointer); Stretch(false); Toggle(); e.Handled = true; };
        PointerCaptureLost += (_, _) => { if (pressed) { pressed = false; Stretch(false); } };
        KeyDown += (_, e) => { if (e.Key is VirtualKey.Space or VirtualKey.Enter) { Toggle(); e.Handled = true; } };
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
    }

    public bool IsOn { get => isOn; set { if (isOn != value) { isOn = value; Animate(); } } }
    public event EventHandler<bool>? Toggled;

    void Toggle() { isOn = !isOn; Animate(); Toggled?.Invoke(this, isOn); }

    void Animate()
    {
        var c = knobVisual.Compositor;
        double speed = ((App)Application.Current).Glass.Current.MotionSpeed;
        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1, new Vector3(isOn ? (pressed ? 11 : 16) : 0, 0, 0), Ease(c, "PaneEaseSpring"));
        move.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurNormal") * speed);
        knobVisual.StartAnimation("Translation", move);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, isOn ? 1 : 0, Ease(c, "PaneEaseSmooth"));
        fade.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast") * speed);
        onVisual.StartAnimation("Opacity", fade);
    }

    // 按住拉长：关时向右长、开时向左长（原型 width 21px + translateX 11px）
    void Stretch(bool down)
    {
        var c = knobVisual.Compositor;
        double speed = ((App)Application.Current).Glass.Current.MotionSpeed;
        knobVisual.CenterPoint = new Vector3(isOn ? 16 : 0, 8, 0);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, new Vector3(down ? 21f / 16 : 1, 1, 1), Ease(c, "PaneEaseSmooth"));
        scale.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast") * speed);
        knobVisual.StartAnimation("Scale", scale);
    }

    internal static CubicBezierEasingFunction Ease(Compositor c, string token)
    {
        var p = ((string)GlassParameters.Resource(token)).Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return c.CreateCubicBezierEasingFunction(new Vector2(p[0], p[1]), new Vector2(p[2], p[3]));
    }
}
