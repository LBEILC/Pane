// 搜索命中的文字：加粗 + 下划线（docs/design.md「主题色」：命中不用主题色，靠字重和下划线表达）。
// 用法：<TextBlock c:MatchText.Source="{x:Bind Match}"/>，Source 是 (文字, 命中区间)。
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Pane.Contracts;
using Windows.UI.Text;

namespace Pane.Views.Controls;

public sealed record MatchedText(string Text, IReadOnlyList<TextSpan>? Spans);

public static class MatchText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(object), typeof(MatchText), new PropertyMetadata(null, Changed));

    public static object? GetSource(TextBlock target) => target.GetValue(SourceProperty);
    public static void SetSource(TextBlock target, object? value) => target.SetValue(SourceProperty, value);

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (TextBlock)d;
        block.Inlines.Clear();
        if (e.NewValue is not MatchedText m) return;
        int at = 0;
        foreach (var s in (m.Spans ?? []).Where(s => s.Length > 0 && s.Start >= 0 && s.Start + s.Length <= m.Text.Length).OrderBy(s => s.Start))
        {
            if (s.Start < at) continue;
            if (s.Start > at) block.Inlines.Add(new Run { Text = m.Text[at..s.Start] });
            block.Inlines.Add(new Run
            {
                Text = m.Text.Substring(s.Start, s.Length),
                FontWeight = FontWeights.Bold,
                TextDecorations = TextDecorations.Underline,
            });
            at = s.Start + s.Length;
        }
        if (at < m.Text.Length) block.Inlines.Add(new Run { Text = m.Text[at..] });
    }
}
