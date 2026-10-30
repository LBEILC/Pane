// 快捷键提示行（原型 .panel-foot / .ai-foot）：键帽 + 说明为一组，按组整体换行（放进 WrapPanel）。
// 不用 RichTextBlock 的段落 / 内嵌控件：在 NativeAOT 下往 Blocks 里加段落会抛 InvalidCastException。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Pane.Views.Controls;

public static class KeyHints
{
    /// <param name="items">每组的按键（"+" 表示组合键之间的加号）和说明。</param>
    /// <param name="labelProto">XAML 里隐藏的样板说明文字（取字体、字号、颜色），避免在代码里按名字查资源再强转。</param>
    /// <param name="kbdProto">XAML 里隐藏的样板键帽（Border + 里面的 TextBlock，取样式）。</param>
    public static void Fill(WrapPanel target, IEnumerable<(string[] keys, string label)> items, TextBlock labelProto, Border kbdProto)
    {
        var kbdBorder = kbdProto.Style;
        var kbdText = ((TextBlock)kbdProto.Child).Style;
        target.Children.Clear();
        foreach (var (keys, label) in items)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var k in keys)
            {
                if (k == "+") { group.Children.Add(Label("+", labelProto)); continue; }
                group.Children.Add(new Border { Style = kbdBorder, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Style = kbdText, Text = k } });
            }
            var text = Label(label, labelProto);
            text.Margin = new Thickness(3, 0, 0, 0);
            group.Children.Add(text);
            target.Children.Add(group);
        }
    }

    static TextBlock Label(string text, TextBlock proto) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = proto.FontFamily,
        FontSize = proto.FontSize,
        Foreground = proto.Foreground,
    };
}
