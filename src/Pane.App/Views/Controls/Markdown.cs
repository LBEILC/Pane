// AI 回答的 Markdown 渲染（设计负责人，2026-11-13，用户群反馈「适配标准 Markdown 和 LaTeX」）。
// 启动器 / 超级面板的 AI 卡片和 AI 对话窗口共用，代替原来对话窗口里的小子集。
// 支持：标题、段落（单个换行照原样换行）、加粗 / 斜体 / 删除线、行内代码、链接（只认 http / https）、裸网址、
// 无序 / 有序 / 任务列表（可嵌套）、引用、代码块、表格（对齐）、分割线、公式（$…$、\(…\)、$$…$$、\[…\]）。
// 公式先转成能读的文字（希腊字母、上下标、√、分数线等，见 TexText）；转不了的（矩阵、环境）原样用等宽字体显示。
// 流式生成时整段重排：没写完的代码块按代码块显示，没闭合的 ** 和 $ 照原样显示，写完自然变对。
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views.Controls;

/// <summary>渲染用的字体和画刷（NativeAOT 下不按名字查资源，由各视图从样板元素取）。</summary>
public sealed class MarkdownStyle
{
    public required FontFamily Font { get; init; }
    public required FontFamily Mono { get; init; }
    public required FontFamily MathFont { get; init; }
    public required double Size { get; init; }
    public required double LineHeight { get; init; }
    public required Brush Text1 { get; init; }
    public required Brush Text2 { get; init; }
    public required Brush Text3 { get; init; }
    public required Brush Stroke { get; init; }
    public required Brush CodeFill { get; init; }
    public required SolidColorBrush Accent { get; init; }
    /// <summary>代码块、表格的字号。</summary>
    public double MonoSize => Math.Round(Size * 0.86 * 2) / 2;
    public MarkdownStyle With(Brush text1) => new() { Font = Font, Mono = Mono, MathFont = MathFont, Size = Size, LineHeight = LineHeight, Text1 = text1, Text2 = Text2, Text3 = Text3, Stroke = Stroke, CodeFill = CodeFill, Accent = Accent };
}

public static partial class MarkdownRender
{
    /// <summary>
    /// 把 Markdown 排成一列元素（调用方放进 StackPanel）。<paramref name="tail"/> 不为 null 时（流式生成的光标），
    /// 接在最后一段文字的末尾；最后一块不是文字（代码块、表格）就单独放一行。<paramref name="tailHost"/> 是装光标的容器，
    /// 下次重排前调用方要把它的 Child 设回 null，光标才能挂到新的段落上。
    /// </summary>
    public static List<FrameworkElement> Build(string text, MarkdownStyle st, UIElement? tail, out InlineUIContainer? tailHost)
    {
        var output = new List<FrameworkElement>();
        RichTextBlock? lastText = null;
        void Add(FrameworkElement e, RichTextBlock? textBlock) { output.Add(e); lastText = textBlock; }
        Blocks(text.Replace("\r\n", "\n").Split('\n'), st, Add);
        tailHost = null;
        if (tail is not null)
        {
            tailHost = new InlineUIContainer { Child = tail };
            if (lastText?.Blocks.LastOrDefault() is Paragraph p) p.Inlines.Add(tailHost);
            else
            {
                var r = Text(st);
                var para = new Paragraph();
                para.Inlines.Add(tailHost);
                r.Blocks.Add(para);
                output.Add(r);
            }
        }
        return output;
    }

    // ---------------- 块 ----------------

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")] private static partial Regex HeadingPattern();
    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")] private static partial Regex RulePattern();
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")] private static partial Regex ListPattern();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableRulePattern();

    static void Blocks(string[] lines, MarkdownStyle st, Action<FrameworkElement, RichTextBlock?> add)
    {
        var para = new List<string>();
        var listIndents = new List<int>();   // 嵌套列表：每层的缩进列
        void Flush()
        {
            if (para.Count == 0) return;
            var r = Paragraph(string.Join("\n", para), st);
            para.Clear();
            add(r, r);
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // 代码块：``` 或 ~~~，没写完就到结尾
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush(); listIndents.Clear();
                var fence = trimmed[..3];
                var lang = trimmed[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal); i++) code.Add(lines[i]);
                add(CodeBlock(string.Join("\n", code), lang, st), null);
                continue;
            }

            // 独立公式：$$…$$ 或 \[…\]，可以跨行
            if (trimmed.StartsWith("$$", StringComparison.Ordinal) || trimmed.StartsWith(@"\[", StringComparison.Ordinal))
            {
                var close = trimmed.StartsWith("$$", StringComparison.Ordinal) ? "$$" : @"\]";
                var body = trimmed[2..];
                int end = body.IndexOf(close, StringComparison.Ordinal);
                if (end >= 0 && body[(end + 2)..].Trim().Length == 0) { Flush(); listIndents.Clear(); add(MathBlock(body[..end], st), null); continue; }
                if (end < 0)
                {
                    var tex = new StringBuilder(body);
                    int j = i + 1;
                    for (; j < lines.Length; j++)
                    {
                        int k = lines[j].IndexOf(close, StringComparison.Ordinal);
                        if (k >= 0) { tex.Append('\n').Append(lines[j][..k]); break; }
                        tex.Append('\n').Append(lines[j]);
                    }
                    if (j < lines.Length) { Flush(); listIndents.Clear(); add(MathBlock(tex.ToString(), st), null); i = j; continue; }
                    // 还没闭合（正在生成）：先照原文显示
                }
            }

            if (trimmed.Length == 0) { Flush(); continue; }

            if (HeadingPattern().Match(trimmed) is { Success: true } h && line.Length - line.TrimStart().Length < 4)
            {
                Flush(); listIndents.Clear();
                var r = Heading(h.Groups[2].Value, h.Groups[1].Length, st);
                add(r, r);
                continue;
            }

            if (RulePattern().IsMatch(line) && para.Count == 0)
            {
                listIndents.Clear();
                add(new Border { Height = 1, Background = st.Stroke, Margin = new Thickness(0, 6, 0, 6) }, null);
                continue;
            }
            if (RulePattern().IsMatch(line) && trimmed.StartsWith('-')) { para.Clear(); continue; }   // 「文字 + ---」（标题下划线写法）：当分割线吃掉

            // 表格：这一行有 |，下一行是分隔行
            if (line.Contains('|') && i + 1 < lines.Length && TableRulePattern().IsMatch(lines[i + 1]))
            {
                Flush(); listIndents.Clear();
                var rows = new List<string> { line };
                var rule = lines[i + 1];
                for (i += 2; i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0; i++) rows.Add(lines[i]);
                i--;
                add(Table(rows, rule, st), null);
                continue;
            }

            // 引用：连续的 > 行，里面再按块排
            if (trimmed.StartsWith('>'))
            {
                Flush(); listIndents.Clear();
                var inner = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    var t = lines[i].TrimStart()[1..];
                    inner.Add(t.StartsWith(' ') ? t[1..] : t);
                }
                i--;
                var panel = new StackPanel { Spacing = 6 };
                Blocks([.. inner], st.With(st.Text2), (e, _) => panel.Children.Add(e));
                add(new Border { BorderBrush = st.Stroke, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Child = panel }, null);
                continue;
            }

            if (ListPattern().Match(line) is { Success: true } m)
            {
                Flush();
                int indent = m.Groups[1].Value.Replace("\t", "    ").Length;
                while (listIndents.Count > 0 && listIndents[^1] > indent) listIndents.RemoveAt(listIndents.Count - 1);
                if (listIndents.Count == 0 || listIndents[^1] < indent) listIndents.Add(indent);
                int level = Math.Min(listIndents.Count - 1, 4);
                var marker = m.Groups[2].Value;
                var body = new StringBuilder(m.Groups[3].Value);
                // 接续行：比标记缩进更深、又不是新的列表项
                for (; i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && lines[i + 1].Length - lines[i + 1].TrimStart().Length > indent
                       && !ListPattern().IsMatch(lines[i + 1]) && !lines[i + 1].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                    body.Append('\n').Append(lines[i + 1].Trim());
                var (item, textBlock) = ListItem(marker, body.ToString(), level, st);
                add(item, textBlock);
                continue;
            }

            // 段落里夹着缩进的接续行（列表项之后）：并到上一段
            listIndents.Clear();
            para.Add(line.TrimEnd());
        }
        Flush();
    }

    static RichTextBlock Text(MarkdownStyle st, double? size = null, double? lineHeight = null) => new()
    {
        FontFamily = st.Font,
        FontSize = size ?? st.Size,
        LineHeight = lineHeight ?? st.LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Foreground = st.Text1,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        SelectionHighlightColor = st.Accent,
    };

    static RichTextBlock Paragraph(string text, MarkdownStyle st)
    {
        var r = Text(st);
        var p = new Paragraph();
        Inline(p.Inlines, text, st);
        r.Blocks.Add(p);
        return r;
    }

    static RichTextBlock Heading(string text, int level, MarkdownStyle st)
    {
        double scale = level switch { 1 => 1.32, 2 => 1.2, 3 => 1.08, _ => 1 };
        var size = Math.Round(st.Size * scale);
        var r = Text(st, size, Math.Round(size * 1.45));
        r.FontWeight = FontWeights.SemiBold;
        r.Margin = new Thickness(0, level <= 2 ? 6 : 2, 0, 0);
        var p = new Paragraph();
        Inline(p.Inlines, text, st);
        r.Blocks.Add(p);
        return r;
    }

    static (FrameworkElement, RichTextBlock) ListItem(string marker, string text, int level, MarkdownStyle st)
    {
        string bullet;
        if (char.IsAsciiDigit(marker[0])) bullet = marker[..^1] + ".";
        else bullet = level switch { 0 => "•", 1 => "◦", _ => "▪" };
        // 任务列表：- [ ] / - [x]
        if (!char.IsAsciiDigit(marker[0]) && text.Length >= 3 && text[0] == '[' && text[2] == ']' && (text.Length == 3 || text[3] == ' '))
        {
            bool done = text[1] is 'x' or 'X';
            if (done || text[1] == ' ') { bullet = done ? "☑" : "☐"; text = text.Length > 3 ? text[4..] : ""; }
        }
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(4 + level * 20, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = bullet, FontFamily = st.Font, FontSize = st.Size, LineHeight = st.LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = st.Text3, MinWidth = char.IsAsciiDigit(bullet[0]) ? st.Size * 1.1 : 0,
        });
        var body = Paragraph(text, st);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return (grid, body);
    }

    static Border CodeBlock(string code, string lang, MarkdownStyle st)
    {
        var panel = new StackPanel { Spacing = 4 };
        if (lang.Length > 0 && lang.Length <= 20)
            panel.Children.Add(new TextBlock { Text = lang, FontFamily = st.Font, FontSize = 11, Foreground = st.Text3 });
        var size = st.MonoSize;
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
            Content = new TextBlock { Text = code, FontFamily = st.Mono, FontSize = size, LineHeight = Math.Round(size * 1.55), Foreground = st.Text1, IsTextSelectionEnabled = true, SelectionHighlightColor = st.Accent },
        });
        return new Border { Padding = new Thickness(12, 9, 12, 10), CornerRadius = new CornerRadius(10), Background = st.CodeFill, BorderBrush = st.Stroke, BorderThickness = new Thickness(1), Child = panel };
    }

    static FrameworkElement MathBlock(string tex, MarkdownStyle st)
    {
        var (text, ok) = TexText.Convert(tex.Trim());
        if (!ok) return CodeBlock(tex.Trim(), "LaTeX", st);
        var size = Math.Round(st.Size * 1.12);
        var r = Text(st, size, Math.Round(size * 1.6));
        r.FontFamily = st.MathFont;
        r.TextAlignment = TextAlignment.Center;
        r.Margin = new Thickness(0, 2, 0, 2);
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        r.Blocks.Add(p);
        return r;
    }

    static FrameworkElement Table(List<string> rows, string rule, MarkdownStyle st)
    {
        var aligns = Cells(rule).Select(c => c.StartsWith(':') && c.EndsWith(':') ? TextAlignment.Center : c.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left).ToList();
        var cells = rows.Select(Cells).ToList();
        int columns = Math.Max(aligns.Count, cells.Max(c => c.Count));
        var grid = new Grid();
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var size = Math.Round(st.Size * 0.93);
        for (int r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < columns; c++)
            {
                var block = Text(st, size, Math.Round(size * 1.55));
                block.MaxWidth = 320;
                block.TextAlignment = c < aligns.Count ? aligns[c] : TextAlignment.Left;
                if (r == 0) block.FontWeight = FontWeights.SemiBold;
                var p = new Paragraph();
                Inline(p.Inlines, c < cells[r].Count ? cells[r][c] : "", st);
                block.Blocks.Add(p);
                var cell = new Border
                {
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderBrush = st.Stroke,
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, 0, 0, r == cells.Count - 1 ? 0 : 1),
                    Background = r == 0 ? st.CodeFill : null,
                    Child = block,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            BorderBrush = st.Stroke,
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                IsTabStop = false,
                Content = grid,
            },
        };
    }

    static List<string> Cells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith(@"\|", StringComparison.Ordinal)) t = t[..^1];
        var list = new List<string>();
        var cell = new StringBuilder();
        bool code = false;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '`') code = !code;
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (t[i] == '|' && !code) { list.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(t[i]);
        }
        list.Add(cell.ToString().Trim());
        return list;
    }

    // ---------------- 行内 ----------------

    [GeneratedRegex(@"https?://[^\s<>()（）\[\]，。、；：！？""'`]+[^\s<>()（）\[\]，。、；：！？""'`.,;:!?]")] private static partial Regex UrlPattern();

    static void Inline(InlineCollection into, string text, MarkdownStyle st)
    {
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            Plain(into, plain.ToString(), st);
            plain.Clear();
        }
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            // 换行：段落里的单个换行照原样换行
            if (ch == '\n') { FlushPlain(); into.Add(new LineBreak()); i++; continue; }
            // 行内公式 \( … \)
            if (ch == '\\' && i + 1 < text.Length && text[i + 1] == '(')
            {
                int end = text.IndexOf(@"\)", i + 2, StringComparison.Ordinal);
                if (end > i + 2) { FlushPlain(); MathRun(into, text[(i + 2)..end], st); i = end + 2; continue; }
            }
            // 转义：\* \_ \` \$ 等照字面
            if (ch == '\\' && i + 1 < text.Length && "\\`*_{}[]()#+-.!|~$<>".Contains(text[i + 1])) { plain.Append(text[i + 1]); i += 2; continue; }
            // 行内代码：`…`（几个反引号开头就几个结尾）
            if (ch == '`')
            {
                int n = 1;
                while (i + n < text.Length && text[i + n] == '`') n++;
                var fence = new string('`', n);
                int end = text.IndexOf(fence, i + n, StringComparison.Ordinal);
                if (end > i + n)
                {
                    FlushPlain();
                    into.Add(new Run { Text = text[(i + n)..end].Trim(), FontFamily = st.Mono, FontSize = st.MonoSize, Foreground = st.Text2 });
                    i = end + n;
                    continue;
                }
                plain.Append(fence); i += n; continue;
            }
            // 行内公式 $…$：开头的 $ 后面不是空白，结尾的 $ 前面不是空白、后面不是数字（避开「$5 和 $10」这种金额）
            if (ch == '$' && i + 1 < text.Length && text[i + 1] != '$' && !char.IsWhiteSpace(text[i + 1]))
            {
                int end = i + 1;
                while ((end = text.IndexOf('$', end)) > 0 && (char.IsWhiteSpace(text[end - 1]) || text[end - 1] == '\\')) end++;
                if (end > i + 1 && (end + 1 >= text.Length || !char.IsAsciiDigit(text[end + 1])) && !text[(i + 1)..end].Contains('\n'))
                {
                    FlushPlain(); MathRun(into, text[(i + 1)..end], st); i = end + 1; continue;
                }
            }
            // 加粗 ** / __、删除线 ~~
            if ((ch == '*' || ch == '_' || ch == '~') && i + 1 < text.Length && text[i + 1] == ch && (ch != '_' || WordBoundaryBefore(text, i)))
            {
                var mark = new string(ch, 2);
                int end = text.IndexOf(mark, i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[i + 2]) && !char.IsWhiteSpace(text[end - 1]))
                {
                    FlushPlain();
                    var span = ch == '~' ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough } : new Bold();
                    Inline(span.Inlines, text[(i + 2)..end], st);
                    into.Add(span);
                    i = end + 2;
                    continue;
                }
            }
            // 斜体 * / _（_ 只认词边界，避开 snake_case）
            if ((ch == '*' || ch == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != ch && (ch == '*' || WordBoundaryBefore(text, i)))
            {
                int end = i + 1;
                while ((end = text.IndexOf(ch, end)) > 0 && (char.IsWhiteSpace(text[end - 1]) || end + 1 < text.Length && text[end + 1] == ch)) end += 2;
                if (end > i + 1 && end < text.Length && (ch == '*' || end + 1 >= text.Length || !char.IsLetterOrDigit(text[end + 1])) && !text[(i + 1)..end].Contains('\n'))
                {
                    FlushPlain();
                    var span = new Italic();
                    Inline(span.Inlines, text[(i + 1)..end], st);
                    into.Add(span);
                    i = end + 1;
                    continue;
                }
            }
            // 链接 [文字](网址)
            if (ch == '[')
            {
                int close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                int paren = close < 0 ? -1 : text.IndexOf(')', close + 2);
                if (close > i && paren > close && !text[i..paren].Contains('\n'))
                {
                    var label = text[(i + 1)..close];
                    var url = text[(close + 2)..paren].Trim().Split(' ')[0].Trim('<', '>');
                    FlushPlain();
                    if (WebUri(url) is { } uri)
                    {
                        var link = new Hyperlink { NavigateUri = uri, Foreground = st.Accent, UnderlineStyle = UnderlineStyle.None };
                        Inline(link.Inlines, label, st);
                        ToolTipService.SetToolTip(link, url);
                        into.Add(link);
                    }
                    else Inline(into, label, st);
                    i = paren + 1;
                    continue;
                }
            }
            // <https://…>
            if (ch == '<')
            {
                int close = text.IndexOf('>', i + 1);
                if (close > i && WebUri(text[(i + 1)..close]) is { } uri)
                {
                    FlushPlain();
                    into.Add(Link(uri, text[(i + 1)..close], st));
                    i = close + 1;
                    continue;
                }
            }
            plain.Append(ch);
            i++;
        }
        FlushPlain();
    }

    static bool WordBoundaryBefore(string text, int i) => i == 0 || !char.IsLetterOrDigit(text[i - 1]);

    // 普通文字：裸网址变成链接
    static void Plain(InlineCollection into, string text, MarkdownStyle st)
    {
        int at = 0;
        foreach (Match m in UrlPattern().Matches(text))
        {
            if (WebUri(m.Value) is not { } uri) continue;
            if (m.Index > at) into.Add(new Run { Text = text[at..m.Index] });
            into.Add(Link(uri, m.Value, st));
            at = m.Index + m.Length;
        }
        if (at < text.Length) into.Add(new Run { Text = text[at..] });
    }

    static Hyperlink Link(Uri uri, string label, MarkdownStyle st)
    {
        var link = new Hyperlink { NavigateUri = uri, Foreground = st.Accent, UnderlineStyle = UnderlineStyle.None };
        link.Inlines.Add(new Run { Text = label });
        return link;
    }

    static Uri? WebUri(string s) => Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri : null;

    static void MathRun(InlineCollection into, string tex, MarkdownStyle st)
    {
        var (text, ok) = TexText.Convert(tex);
        if (ok) into.Add(new Run { Text = text, FontFamily = st.MathFont });
        else into.Add(new Run { Text = tex, FontFamily = st.Mono, FontSize = st.MonoSize, Foreground = st.Text2 });
    }
}
