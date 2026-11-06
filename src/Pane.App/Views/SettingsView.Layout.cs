// 设置窗口：拼装（分组、行、展开动画）（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
using Pane.Localization;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.Foundation;
using G = Pane.Gestures;

namespace Pane.Views;

public sealed partial class SettingsView
{
    // ================= 拼装（原型 group / row / lrow） =================

    void Group(string? title, params FrameworkElement[] rows) => Group(title, rows, null);

    void Group(string? title, FrameworkElement[] rows, string? note) => Group(title, rows, note, null);

    // drop：这一组能把文件拖进来添加（原型 .drop-zone），松开时拿到完整路径
    void Group(string? title, FrameworkElement[] rows, string? note, Action<IReadOnlyList<string>>? drop)
    {
        var group = new StackPanel { Margin = new Thickness(0, Body.Children.Count == 0 ? 8 : 20, 0, 0) };
        if (title != null) group.Children.Add(new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 8), FontFamily = GroupTitleProto.FontFamily, FontSize = GroupTitleProto.FontSize, FontWeight = GroupTitleProto.FontWeight, Foreground = GroupTitleProto.Foreground });
        var list = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
            list.Children.Add(new Border { BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0), Child = rows[i] });
        var box = new Border { CornerRadius = RowsProto.CornerRadius, Background = RowsProto.Background, BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(1), Child = list };
        group.Children.Add(drop is null ? box : DropZone.Wrap(box, RowsProto.CornerRadius, drop));
        if (note != null) group.Children.Add(new TextBlock { Text = note, Margin = new Thickness(2, 6, 2, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = DescProto.Foreground });
        Body.Children.Add(group);
    }

    // ---------------- 就地展开 / 收起的动效（设计改 2026-10-30，用户提：「玻璃参数（高级）」展开没有动效）----------------
    // 页面每次整页重建，展开的几行原来一下子出现、下面的内容直接跳下去。改为：展开的行放进一个容器，
    // 容器高度从 0 长到内容高度（超出部分裁掉）、内容同时淡入，下面的分组被平滑推开；收起反过来，播完才真的去掉。
    // 用法：Group(..., [header, Reveal("key", 展开的行...)])；打开前设 revealKey = "key"；收起用 Collapse("key", 真正收起)。
    string? revealKey;
    readonly Dictionary<string, (Grid Host, FrameworkElement List)> reveals = [];
    readonly Dictionary<string, Action> designToggles = [];   // 演示预览 PANE_DESIGN_REVEAL 用：各展开开关的点击动作

    FrameworkElement Reveal(string key, IEnumerable<FrameworkElement> rows)
    {
        var list = new StackPanel();
        int i = 0;
        foreach (var r in rows) list.Children.Add(new Border { BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(0, i++ == 0 ? 0 : 1, 0, 0), Child = r });
        var host = new Grid();
        host.Children.Add(list);
        reveals[key] = (host, list);
        if (revealKey == key)
        {
            revealKey = null;
            host.Height = 0;   // 第一帧就从 0 开始，不先露一下全高
            list.Opacity = 0;
            // 排到页面恢复滚动位置之后再开始（Render 末尾把原来的滚动位置排进了队列）
            void Once(object s, RoutedEventArgs e) { host.Loaded -= Once; DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => PlayReveal(host, list, true, null)); }
            host.Loaded += Once;
        }
        return host;
    }

    void Collapse(string key, Action done)
    {
        if (reveals.TryGetValue(key, out var r) && r.Host.IsLoaded && r.Host.ActualHeight > 0) PlayReveal(r.Host, r.List, false, done);
        else done();
    }

    double RevealMs => GlassParameters.Token("PaneDurNormal") * app.Glass.Current.MotionSpeed;

    void PlayReveal(Grid host, FrameworkElement list, bool open, Action? done)
    {
        double width = host.ActualWidth > 0 ? host.ActualWidth : double.PositiveInfinity;
        list.Measure(new Size(width, double.PositiveInfinity));
        double full = open ? list.DesiredSize.Height : host.ActualHeight;
        double ms = RevealMs;
        if (ms < 1 || full <= 0)
        {
            host.ClearValue(HeightProperty); list.Opacity = 1;
            done?.Invoke();
            return;
        }
        var clip = new RectangleGeometry { Rect = new Rect(0, 0, 100000, open ? 0 : full) };
        host.Clip = clip;
        void Resized(object s, SizeChangedEventArgs e) => clip.Rect = new Rect(0, 0, 100000, e.NewSize.Height);
        host.SizeChanged += Resized;
        // 在页面底部点开时，展开的内容会长在可视区外面：页面跟着往上滚，让展开的部分露出来（标题行留在视野里）
        double follow = 0;
        if (open)
        {
            double top = host.TransformToVisual(Scroller).TransformPoint(new Point(0, 0)).Y;
            follow = Math.Max(0, Math.Min(top + full + 16 - Scroller.ViewportHeight, top - 72));
        }
        double startOffset = Scroller.VerticalOffset;
        void Follow(object? s, object e) => Scroller.ChangeView(null, Math.Min(startOffset + follow, Scroller.ScrollableHeight), null, true);
        if (follow > 0) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Follow;
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(Ease(host, "Height", open ? 0 : full, open ? full : 0, ms, dependent: true));
        // 淡入比长高短一点、淡出比收起快一点，避免文字被裁一半时还很实
        board.Children.Add(Ease(list, "Opacity", open ? 0 : 1, open ? 1 : 0, open ? ms : ms * 0.6, dependent: false));
        board.Completed += (_, _) =>
        {
            host.SizeChanged -= Resized;
            if (follow > 0) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Follow; Follow(null, EventArgs.Empty); }
            if (open) { host.ClearValue(HeightProperty); host.Clip = null; list.Opacity = 1; }
            done?.Invoke();
        };
        board.Begin();
    }

    // 照令牌 PaneEaseSmooth（0.2,0,0,1）的关键帧动画
    static Microsoft.UI.Xaml.Media.Animation.Timeline Ease(DependencyObject target, string property, double from, double to, double ms, bool dependent)
    {
        var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames { EnableDependentAnimation = dependent };
        a.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        a.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(ms), Value = to,
            KeySpline = new Microsoft.UI.Xaml.Media.Animation.KeySpline { ControlPoint1 = new Point(0.2, 0), ControlPoint2 = new Point(0, 1) },
        });
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, property);
        return a;
    }

    // 展开箭头：跟着转（0° 收起、180° 展开）
    void SpinChevron(PaneIcon chevron, double to)
    {
        if (chevron.RenderTransform is not RotateTransform rotate) return;
        if (RevealMs < 1) { rotate.Angle = to; return; }
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(Ease(rotate, "Angle", rotate.Angle, to, RevealMs, dependent: false));
        board.Begin();
    }

    Grid Row(string label, string? desc, params FrameworkElement[] controls) => Row(label, desc, (IEnumerable<FrameworkElement>)controls);

    Grid Row(string label, string? desc, IEnumerable<FrameworkElement> controls)
    {
        var row = new Grid { MinHeight = 56, Padding = new Thickness(14, 10, 14, 10), ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (label.Length > 0) text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontFamily = LabelProto.FontFamily, FontSize = LabelProto.FontSize, Foreground = LabelProto.Foreground });
        if (desc != null) text.Children.Add(new TextBlock { Text = desc, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = DescProto.Foreground });
        row.Children.Add(text);
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls) { c.VerticalAlignment = VerticalAlignment.Center; ctl.Children.Add(c); }
        Grid.SetColumn(ctl, 1);
        row.Children.Add(ctl);
        return row;
    }

    FrameworkElement ListRow(string glyph, string label, string sub, bool mono, params FrameworkElement[] tail) => ListRow(glyph, Text(label, LabelProto), sub, mono, tail);

    FrameworkElement ListRow(string glyph, FrameworkElement label, string sub, bool mono, params FrameworkElement[] tail) =>
        ListRow(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new PaneIcon { Glyph = glyph, Width = 16, Height = 16, Foreground = IconProto.Foreground },
        }, label, sub, mono, tail);

    // lead：行首的图块；手势列表换成方向箭头（原型 .arrows）
    FrameworkElement ListRow(FrameworkElement lead, FrameworkElement label, string sub, bool mono, params FrameworkElement[] tail)
    {
        var row = new Grid { MinHeight = 48, Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lead.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(lead);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(label);
        var subText = Text(sub, mono ? MonoProto : DescProto);
        subText.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(subText);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in tail) ctl.Children.Add(c);
        Grid.SetColumn(ctl, 2);
        row.Children.Add(ctl);
        return row;
    }

    FrameworkElement AddRow(params FrameworkElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(14, 10, 14, 10) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}
