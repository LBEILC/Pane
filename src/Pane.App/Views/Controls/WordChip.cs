// 词块（设计负责人，093）：分词窗口里拆开的一块文字。照组件库 .wordchip（prototype/components.css「词块」）：
// 普通词带控件底 + 细描边；标点只显示字、不带底；网址、邮箱、电话、整块数字前面带 13 的小图标；
// 选中是选择状态，用主题色淡底（28%）+ 主题色描边（60%）；键盘光标是外圈 2px 主题色（70%）。悬停时淡淡带一点主题色（9%）。
// 不是 Control：点选、拖选由分词窗口统一处理（拖动要跨块），这里只管样子。
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using System.Numerics;
using Windows.UI;

namespace Pane.Views.Controls;

internal sealed partial class WordChip : Grid
{
    /// <summary>同一窗口里的词块共用的画刷（主题色由设置决定，窗口打开时取一次）。</summary>
    internal sealed record Palette(Brush Fill, Brush Stroke, Brush Hover, Brush PunctHover, Brush Selected, Brush SelectedStroke, Brush PunctSelected,
        Brush Text1, Brush Text2, Brush Text3, Brush Focus, FontFamily Font, double FontSize)
    {
        internal static Palette From(Color accent, Brush fill, Brush stroke, Brush hover, Brush text1, Brush text2, Brush text3, FontFamily font, double size)
        {
            SolidColorBrush A(double alpha) => new(Color.FromArgb((byte)Math.Round(alpha * 255), accent.R, accent.G, accent.B));
            return new(fill, stroke, A(0.09), hover, A(0.28), A(0.60), A(0.14), text1, text2, text3, A(0.70), font, size);
        }
    }

    static readonly Dictionary<WordKind, string> Glyphs = new() { [WordKind.Link] = "link", [WordKind.Email] = "mail", [WordKind.Phone] = "phone", [WordKind.Number] = "hash" };

    readonly Border chip, ring;
    readonly TextBlock text;
    readonly PaneIcon? icon;
    readonly Palette palette;
    bool selected, hover, focused;

    public WordPiece Piece { get; }
    public int Index { get; }
    public bool IsPunctuation => Piece.Kind == WordKind.Punctuation;

    public WordChip(WordPiece piece, int index, Palette palette)
    {
        Piece = piece; Index = index; this.palette = palette;
        text = new TextBlock { Text = piece.Text, FontFamily = palette.Font, FontSize = palette.FontSize, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (Glyphs.TryGetValue(piece.Kind, out var glyph))
        {
            icon = new PaneIcon { Glyph = glyph, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(icon);
        }
        content.Children.Add(text);
        chip = new Border
        {
            Height = 30, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(IsPunctuation ? 0 : 1),
            Padding = IsPunctuation ? new Thickness(3, 0, 3, 0) : new Thickness(10, 0, 10, 0), MinWidth = IsPunctuation ? 20 : 0, Child = content,
        };
        ring = new Border { Margin = new Thickness(-3), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), BorderBrush = palette.Focus, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Children.Add(chip); Children.Add(ring);
        PointerEntered += (_, _) => { hover = true; Paint(); };
        PointerExited += (_, _) => { hover = false; Paint(); };
        Paint();
    }

    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Paint(); } }
    public bool Focused { get => focused; set { if (focused == value) return; focused = value; ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }

    void Paint()
    {
        if (IsPunctuation)
        {
            chip.Background = selected ? palette.PunctSelected : hover ? palette.PunctHover : null;
            text.Foreground = selected ? palette.Text1 : palette.Text3;
            return;
        }
        chip.Background = selected ? palette.Selected : hover ? palette.Hover : palette.Fill;
        chip.BorderBrush = selected ? palette.SelectedStroke : palette.Stroke;
        text.Foreground = palette.Text1;
        if (icon != null) icon.Foreground = selected ? palette.Text2 : palette.Text3;
    }

    /// <summary>点选时轻轻弹一下（原型：0.92 → 1，回弹曲线）。</summary>
    public void Pop(double motionSpeed)
    {
        var visual = ElementCompositionPreview.GetElementVisual(chip);
        var c = visual.Compositor;
        visual.CenterPoint = new Vector3((float)chip.ActualWidth / 2, (float)chip.ActualHeight / 2, 0);
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, new Vector3(0.92f, 0.92f, 1));
        a.InsertKeyFrame(1, Vector3.One, c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.36f), new Vector2(0.64f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(Math.Max(1, 220 * motionSpeed));
        visual.StartAnimation("Scale", a);
    }
}
