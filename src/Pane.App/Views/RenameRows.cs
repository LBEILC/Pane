// 批量重命名窗口的预览行（设计负责人，040）。照原型 prototype/rename.html .prow：
// 新名里改动的部分加粗 + 中性浅底（主题色不用于文字），没改的部分用次要文字色；不变的写「不变」；有问题的整段用警示色，右端给问题标签。
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Views.Controls;
using Pane.Views.Files;

namespace Pane.Views;

public enum RenameDiffStyle { Changed, Same, Bad }

/// <param name="Start">改动部分在 Text 里的起点；Length = 0 表示没有可标的改动（比如只删了字）。</param>
public sealed record RenameDiffText(string Text, int Start, int Length, RenameDiffStyle Style)
{
    // 找出新名和原名不同的那一段：去掉共同的开头和结尾，剩下的就是改动
    public static RenameDiffText Of(string oldName, string newName)
    {
        int p = 0;
        while (p < oldName.Length && p < newName.Length && oldName[p] == newName[p]) p++;
        int s = 0;
        while (s < oldName.Length - p && s < newName.Length - p && oldName[^(s + 1)] == newName[^(s + 1)]) s++;
        return new(newName, p, newName.Length - p - s, RenameDiffStyle.Changed);
    }
}

public static class RenameDiff
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(object), typeof(RenameDiff), new PropertyMetadata(null, Changed));

    public static object? GetSource(TextBlock target) => target.GetValue(SourceProperty);
    public static void SetSource(TextBlock target, object? value) => target.SetValue(SourceProperty, value);

    static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(RenameDiff), new PropertyMetadata(false));

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (TextBlock)d;
        // 颜色要按控件实际的深浅色取：没进界面前 ActualTheme 不准，加载好和换主题时再画一遍
        if (!(bool)block.GetValue(HookedProperty))
        {
            block.SetValue(HookedProperty, true);
            block.Loaded += (_, _) => Render(block);
            block.ActualThemeChanged += (_, _) => Render(block);
        }
        Render(block);
    }

    static void Render(TextBlock block)
    {
        block.Inlines.Clear();
        block.TextHighlighters.Clear();
        if (GetSource(block) is not RenameDiffText t) return;
        block.Foreground = t.Style switch
        {
            RenameDiffStyle.Bad => PaneTokens.Danger,
            RenameDiffStyle.Same => Theme(block, "PaneText3Brush"),
            _ => Theme(block, "PaneText2Brush"),
        };
        if (t.Style != RenameDiffStyle.Changed || t.Length <= 0) { block.Inlines.Add(new Run { Text = t.Text }); return; }
        if (t.Start > 0) block.Inlines.Add(new Run { Text = t.Text[..t.Start] });
        block.Inlines.Add(new Run { Text = t.Text.Substring(t.Start, t.Length), FontWeight = FontWeights.SemiBold, Foreground = Theme(block, "PaneText1Brush") });
        if (t.Start + t.Length < t.Text.Length) block.Inlines.Add(new Run { Text = t.Text[(t.Start + t.Length)..] });
        var highlighter = new TextHighlighter { Background = Theme(block, "PaneMatchBackgroundBrush"), Foreground = Theme(block, "PaneText1Brush") };
        highlighter.Ranges.Add(new TextRange { StartIndex = t.Start, Length = t.Length });
        block.TextHighlighters.Add(highlighter);
    }

    // 按元素当前的深浅色取主题画刷（代码里建的 Run 拿不到 ThemeResource）
    internal static Brush Theme(FrameworkElement element, string key)
    {
        var dark = element.ActualTheme == ElementTheme.Dark;
        // 文字色在令牌字典里（Tokens.xaml，和 SlidingHighlight 同一处）；命中底色在 Controls.xaml
        if (key.EndsWith("Brush", StringComparison.Ordinal)
            && ((ResourceDictionary)Hosting.GlassParameters.Tokens.ThemeDictionaries[dark ? "Dark" : "Light"]).TryGetValue(key[..^5] + "Color", out var color))
            return new SolidColorBrush((Windows.UI.Color)color);
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
            if (dict.ThemeDictionaries.TryGetValue(dark ? "Dark" : "Light", out var theme) && ((ResourceDictionary)theme).TryGetValue(key, out var brush))
                return (Brush)brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(160, 128, 128, 128));
    }
}

public sealed partial class RenameRow : INotifyPropertyChanged
{
    static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);

    public RenameRow(string path)
    {
        Path = path;
        var folder = System.IO.Directory.Exists(path);
        var category = Services.Launcher.FileSearch.Category(System.IO.Path.GetFileName(path), folder);
        Glyph = FileKinds.Glyph(category);
        Tile = FileKinds.Brush(category);
        oldName = System.IO.Path.GetFileName(path);
        diff = new(oldName, 0, 0, RenameDiffStyle.Same);
    }

    /// <summary>这一项现在在磁盘上的路径（执行后换成新路径，撤销后换回来）。</summary>
    public string Path { get; set; }
    public string Glyph { get; }
    public Brush Tile { get; }

    string oldName;
    public string OldName { get => oldName; set { if (oldName == value) return; oldName = value; Changed(nameof(OldName)); } }

    string newName = "";
    public string NewName => newName;
    RenameDiffText diff;
    public RenameDiffText Diff => diff;
    public RenameIssue Issue { get; private set; } = RenameIssue.Unchanged;

    /// <summary>按一份预览更新新名和问题标签。</summary>
    public void Show(RenamePreview p)
    {
        Issue = p.Issue;
        newName = p.NewName;
        diff = p.Issue switch
        {
            RenameIssue.Unchanged => new("不变", 0, 0, RenameDiffStyle.Same),
            RenameIssue.BadPattern => new(oldName, 0, 0, RenameDiffStyle.Same),
            RenameIssue.None => RenameDiffText.Of(oldName, p.NewName),
            _ => new(p.NewName.Length > 0 ? p.NewName : "（空）", 0, 0, RenameDiffStyle.Bad),
        };
        Changed(nameof(NewName)); Changed(nameof(Diff));
        SetTag(IssueText(p.Issue), null, danger: true);
    }

    /// <summary>执行之后：这一行停在「原名 → 新名」，右端写结果。</summary>
    public void ShowResult(bool ok, string? reason) => SetTag(ok ? "已改名" : "没改成", reason, danger: !ok);

    public static string? IssueText(RenameIssue issue) => issue switch
    {
        RenameIssue.Empty => "名字是空的",
        RenameIssue.Invalid => "含不能用的字符",
        RenameIssue.TooLong => "路径太长",
        RenameIssue.Duplicate => "和别的重名",
        RenameIssue.Exists => "已有同名文件",
        RenameIssue.Missing => "文件已经不在了",
        _ => null,
    };

    public string Tag { get; private set; } = "";
    public string? TagTip { get; private set; }
    public Visibility TagVisibility => Tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush TagForeground { get; private set; } = Clear;
    public Brush TagBackground { get; private set; } = Clear;

    void SetTag(string? text, string? tip, bool danger)
    {
        Tag = text ?? "";
        TagTip = tip;
        TagForeground = danger ? PaneTokens.Danger : PaneTokens.Success;
        TagBackground = Tint(danger ? "PaneDangerColor" : "PaneSuccessColor", danger ? 0.13 : 0.14);
        Changed(nameof(Tag)); Changed(nameof(TagTip)); Changed(nameof(TagVisibility)); Changed(nameof(TagForeground)); Changed(nameof(TagBackground));
    }

    // 状态色的淡底：原型 color-mix(状态色 13%, transparent)
    static readonly Dictionary<string, SolidColorBrush> tints = [];
    static SolidColorBrush Tint(string key, double alpha)
    {
        if (tints.TryGetValue(key, out var b)) return b;
        var c = (Windows.UI.Color)Application.Current.Resources[key];
        return tints[key] = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
    }

    bool hovered;
    public bool Hovered { get => hovered; set { if (hovered == value) return; hovered = value; Changed(nameof(DropOpacity)); } }
    public double DropOpacity => hovered ? 1 : 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
