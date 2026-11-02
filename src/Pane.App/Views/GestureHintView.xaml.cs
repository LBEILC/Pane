using Pane.Localization;
// 手势提示框逻辑（设计负责人）。照原型 app.js「showHint」：
// 箭头只追加新增的那几个（逐个弹出），命令名随识别结果更新；执行时整块弹一下（40% 处放大到 1.04）。
// 何时显示、何时收起由 GestureOverlay 决定；这里只管内容和尺寸。
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class GestureHintView : UserControl, IPanelView
{
    // 小键盘位置记号，和原型、LucideIcons 的箭头名一致
    static readonly string[] ArrowGlyphs = ["U", "9", "R", "3", "D", "1", "L", "7"];

    readonly List<GestureArrow> arrows = [];
    GestureHintModifier modifier;
    FrameworkElement? chip, chipSeparator;
    bool shown;

    public GestureHintView()
    {
        InitializeComponent();
    }

    static double Speed => ((App)Application.Current).Glass.Current.MotionSpeed;

    /// <summary>更新内容，返回提示框需要的尺寸（DIP）。</summary>
    public Size Apply(GestureHint hint)
    {
        // 没有方向的提示（通知、面板动作失败）只有一行字
        bool notice = hint.Kind == GestureHintKind.Notice || hint.Arrows.Count == 0 && hint.Modifier == GestureHintModifier.None && hint.Kind == GestureHintKind.Failed;
        Arrows.Visibility = notice ? Visibility.Collapsed : Visibility.Visible;
        if (!notice) UpdateArrows(hint.Arrows, hint.Modifier);

        bool hasInput = hint.Arrows.Count > 0 || hint.Modifier != GestureHintModifier.None;
        var (text, strong) = hint.Kind switch
        {
            GestureHintKind.Drawing => hint.CommandName is { } n ? (n, true) : (hasInput ? Strings.Gestures_Unknown : "…", false),
            GestureHintKind.Executed => (hint.CommandName ?? Strings.Gestures_Executed, true),
            GestureHintKind.NotRecognized => (Strings.Gestures_Unknown, false),
            GestureHintKind.Failed => (string.Format(Strings.Gestures_ActionFailed, (object?)(hint.CommandName ?? Strings.Gestures_Command)), true),
            GestureHintKind.Cancelled => (Strings.Gestures_TimedOut, false),
            _ => (hint.CommandName ?? "", true),
        };
        Label.Text = text;
        // 失败原因另起一行，小一号、次要文字色，太长就折行（最多两行）
        if (hint.Kind == GestureHintKind.Failed && hint.Detail is { Length: > 0 } detail)
        {
            Label.Inlines.Add(new LineBreak());
            Label.Inlines.Add(new Run { Text = detail, FontSize = DetailProto.FontSize, FontWeight = FontWeights.Normal, Foreground = DetailProto.Foreground });
        }
        Label.Foreground = strong ? StrongProto.Foreground : DimProto.Foreground;
        Label.FontWeight = strong && hint.Kind != GestureHintKind.Failed ? FontWeights.SemiBold : FontWeights.Normal;
        Label.Margin = new Thickness(0, notice ? 0 : 8, 0, 0);

        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new Size(Math.Ceiling(Root.DesiredSize.Width), Math.Ceiling(Root.DesiredSize.Height));
        if (shown) SizeRequested?.Invoke(this, size);
        if (hint.Kind == GestureHintKind.Executed) Pulse();
        return size;
    }

    void UpdateArrows(IReadOnlyList<GestureArrow> next, GestureHintModifier nextModifier)
    {
        bool prefix = next.Count >= arrows.Count && arrows.SequenceEqual(next.Take(arrows.Count));
        if (!prefix) { Arrows.Children.Clear(); arrows.Clear(); chip = chipSeparator = null; modifier = GestureHintModifier.None; }
        bool modifierChanged = nextModifier != modifier;
        // 胶囊永远在最后：先拿掉，追加箭头后再放回
        if (chip != null) { Arrows.Children.Remove(chip); Arrows.Children.Remove(chipSeparator); }
        for (int i = arrows.Count; i < next.Count; i++)
        {
            var icon = new PaneIcon { Glyph = ArrowGlyphs[(int)next[i]], Width = 40, Height = 40, StrokeWidth = 2.2, Foreground = StrongProto.Foreground };
            Arrows.Children.Add(icon);
            PopIn(icon, new Vector3(20, 20, 0));
            arrows.Add(next[i]);
        }
        if (modifierChanged) { chip = nextModifier == GestureHintModifier.None ? null : MakeChip(nextModifier); chipSeparator = null; }
        if (chip != null)
        {
            if (arrows.Count > 0)
            {
                chipSeparator ??= new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 2), FontFamily = ChipTextProto.FontFamily, FontSize = 18, Foreground = DimProto.Foreground };
                Arrows.Children.Add(chipSeparator);
            }
            Arrows.Children.Add(chip);
            if (modifierChanged) PopIn(chip, null);
        }
        modifier = nextModifier;
    }

    FrameworkElement MakeChip(GestureHintModifier m)
    {
        var (glyph, text) = m switch
        {
            GestureHintModifier.WheelUp => ("U", Strings.Gestures_WheelUp),
            GestureHintModifier.WheelDown => ("D", Strings.Gestures_WheelDown),
            GestureHintModifier.LeftClick => ("gesture", Strings.Gestures_LeftButton),
            GestureHintModifier.MiddleClick => ("gesture", Strings.Gestures_MiddleButton),
            GestureHintModifier.X1Click => ("gesture", Strings.Gestures_SideButtonOne),
            _ => ("gesture", Strings.Gestures_SideButtonTwo),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new PaneIcon { Glyph = glyph, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = StrongProto.Foreground });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontFamily = ChipTextProto.FontFamily, FontSize = ChipTextProto.FontSize, Foreground = ChipTextProto.Foreground });
        return new Border
        {
            Height = ChipProto.Height, CornerRadius = ChipProto.CornerRadius, Padding = ChipProto.Padding,
            Background = ChipProto.Background, VerticalAlignment = VerticalAlignment.Center, Child = row,
        };
    }

    // 原型 @keyframes arrow-in：0.6 → 1、淡入，--dur-slow + --ease-spring
    void PopIn(FrameworkElement element, Vector3? center)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        if (center is { } p) visual.CenterPoint = p;
        else element.SizeChanged += (_, e) => visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        var duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurSlow") * Speed);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.6f, 0.6f, 1));
        scale.InsertKeyFrame(1, Vector3.One, Spring(c));
        scale.Duration = duration;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Spring(c));
        fade.Duration = duration;
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", fade);
    }

    // 原型 .is-done：40% 处 1.04
    void Pulse()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var c = visual.Compositor;
        visual.CenterPoint = new Vector3((float)Root.DesiredSize.Width / 2, (float)Root.DesiredSize.Height / 2, 0);
        var pulse = c.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0, Vector3.One);
        pulse.InsertKeyFrame(0.4f, new Vector3(1.04f, 1.04f, 1), Spring(c));
        pulse.InsertKeyFrame(1, Vector3.One, Spring(c));
        pulse.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurSlow") * Speed);
        visual.StartAnimation("Scale", pulse);
    }

    static CubicBezierEasingFunction Spring(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.36f), new Vector2(0.64f, 1));

    public Size PreferredSize => new(200, 108);
    public event EventHandler<Size>? SizeRequested;
    // 提示框不会自己要求关闭
    public event EventHandler? CloseRequested { add { } remove { } }
    public void OnShowing(PanelShowContext context) => shown = true;
    public void OnShown() { }
    public void OnHidden() { shown = false; Reset(); }
    public UIElement? DragRegion => null;

    /// <summary>新的一笔手势开始时清空，箭头从头逐个弹出。</summary>
    public void Reset()
    {
        Arrows.Children.Clear(); arrows.Clear(); chip = chipSeparator = null; modifier = GestureHintModifier.None;
    }
}
