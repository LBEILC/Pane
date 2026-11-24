using Pane.Localization;
// 剪贴板条目在界面上的形态。
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Windows.Foundation;

namespace Pane.Views.Clipboard;

/// <summary>一段要显示的文字和搜索词（命中处加粗 + 中性浅底）。</summary>
public sealed record ClipText(string Text, string Query);

public sealed partial class ClipRow : INotifyPropertyChanged
{
    readonly IClipRowActions actions;

    /// <param name="index">在可选中序列里的位置（含已固定区的展开行）。</param>
    /// <param name="number">Alt+数字的编号，1～9；0 = 不编号（已固定区的条目，见 docs/design.md「剪贴板已固定区可折叠」）。</param>
    public ClipRow(ClipEntry entry, int index, int number, string query, IClipRowActions actions)
    {
        Entry = entry;
        Index = index;
        Number = number;
        this.actions = actions;
        Body = new ClipText(entry.Text ?? string.Empty, query);
        if (entry.Kind == ClipKind.Image && entry.SourceDevice is not null && entry.Thumbnail is null) actions.EnsureThumbnail(entry.Id);
        FileLines = (entry.Files ?? []).Select(f => new ClipText(System.IO.Path.GetFileName(f), query)).ToList();
        // 有本机位图就用 Image 显示（issue 21：ImageBrush 画在背景上时，图解码好了也常常要等别的东西触发重绘才出来，看着就是空白很久）；
        // 没有路径的（演示数据）用渐变底
        if (entry.Thumbnail is { Path: { } path }) ThumbPicture = new BitmapImage(new Uri(path));
        else if (entry.Thumbnail is { } t) Thumb = Gradient(t.GradientFrom ?? "#808080", t.GradientTo ?? "#404040");
        if (entry.Kind == ClipKind.Color && TryColor(entry.Text, out var c)) Swatch = new SolidColorBrush(c);
        SourcePicture = LoadIcon(entry.SourceIcon?.ImagePath);
        if (SourcePicture is null && entry.SourceIcon is { GradientFrom: { } from, GradientTo: { } to }) SourceBrush = Gradient(from, to);
        var dims = entry.Kind == ClipKind.Image && entry.ImageWidth is { } w && entry.ImageHeight is { } h ? $"{entry.Text} {w}×{h} · " : "";
        // Pane 云：别的电脑同步来的写「微信 · 笔记本 · 3 分钟前」（docs/cloud.md「剪贴板同步 → 区分设备」）
        MetaText = $"{entry.Source} · {(entry.SourceDevice is { Length: > 0 } device ? device + " · " : "")}{dims}{Ago(entry.CopiedAt)}";
        OriginalNote = entry.Original switch
        {
            ClipOriginal.Downloading => string.Format(Strings.Clipboard_OriginalDownloading, actions.OriginalSize(entry.Id) is { } size ? SizeText(size) : ""),
            ClipOriginal.Expired => Strings.Clipboard_OriginalExpired,
            ClipOriginal.NotSynced => string.Format(Strings.Clipboard_OriginalNotSynced, entry.SourceDevice ?? Strings.Clipboard_OtherDevice),
            ClipOriginal.QuotaExceeded => Strings.Clipboard_OriginalQuota,
            _ => null,
        };
        // 「带格式」和标签小片（2026-11-23）：标签最多露 2 个，其余写 +N
        if (entry.Rich) MetaChips.Add(ClipTagChip.Rich());
        foreach (var name in entry.Tags.Take(2)) MetaChips.Add(ClipTagChip.Tag(name, actions.TagDot(name)));
        if (entry.Tags.Count > 2) MetaChips.Add(ClipTagChip.More(entry.Tags.Count - 2));
    }

    /// <summary>元信息行上的「带格式」和标签小片。</summary>
    public List<ClipTagChip> MetaChips { get; } = [];

    public ClipEntry Entry { get; }
    public int Index { get; }
    public int Number { get; }
    public ClipText Body { get; }
    public List<ClipText> FileLines { get; }
    /// <summary>缩略图框的底：只有演示数据（没有位图路径）用渐变。</summary>
    public Brush? Thumb { get; }
    /// <summary>缩略图位图（服务生成好的本机 PNG）。</summary>
    public ImageSource? ThumbPicture { get; }
    bool HasThumb => Entry.Thumbnail is not null;
    public Brush? Swatch { get; }
    public Brush? SourceBrush { get; }
    /// <summary>来源程序的真实小图标（服务缓存的 PNG）。有它就不画渐变图块。</summary>
    public ImageSource? SourcePicture { get; }
    public string MetaText { get; }
    /// <summary>别的电脑来的图片：原图不在本机时缩略图底部的一句状态；Remote（粘贴时才下载）和本机的为 null。</summary>
    public string? OriginalNote { get; }
    public Visibility OriginalVisibility => V(OriginalNote is not null);
    public double ThumbDim => Entry.Original is ClipOriginal.Expired or ClipOriginal.NotSynced ? 0.72 : 1;
    static string SizeText(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.#} MB" : $"{Math.Max(1, b >> 10)} KB";

    public string TypeGlyph => Entry.Kind switch
    {
        ClipKind.Code => "code",
        ClipKind.Link => "link",
        ClipKind.Color => "drop",
        ClipKind.Image => "image",
        // 文件：复制的是图片（服务给了缩略图）用图片图标；看着都是文件夹（没有扩展名）用文件夹；其余用文档（原来一律是文件夹）
        ClipKind.Files => HasThumb ? "image" : (Entry.Files ?? []).All(f => !System.IO.Path.HasExtension(f)) ? "folder" : "doc",
        _ => "text",
    };
    public string SourceGlyph => Entry.SourceIcon?.Glyph ?? "app";
    public string Hotkey => Number > 0 ? $"Alt {Number}" : "";

    static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => V(Entry.Kind is ClipKind.Text or ClipKind.Link);
    public Visibility CodeVisibility => V(Entry.Kind == ClipKind.Code);
    public Visibility ColorVisibility => V(Entry.Kind == ClipKind.Color);
    public Visibility ThumbVisibility => V(HasThumb);

    // 缩略图框：按原图比例。行内容宽约 290；宽图铺满一行（高按比例，最矮 56，太扁的裁两边），
    // 不够宽的图限高 ThumbMaxHeight、宽按比例靠左
    const double ThumbRowWidth = 290, ThumbMaxHeight = 140, ThumbMinHeight = 56;
    double Ratio => Entry.ImageWidth is int w && w > 0 && Entry.ImageHeight is int h && h > 0 ? (double)w / h : 16.0 / 9;
    bool ThumbFillsRow => Ratio >= ThumbRowWidth / ThumbMaxHeight;
    public double ThumbHeight => ThumbFillsRow ? Math.Max(ThumbMinHeight, Math.Round(ThumbRowWidth / Ratio)) : ThumbMaxHeight;
    public double ThumbWidth => ThumbFillsRow ? double.NaN : Math.Max(ThumbMinHeight, Math.Round(ThumbMaxHeight * Ratio));
    public HorizontalAlignment ThumbAlign => ThumbFillsRow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    public Visibility FilesVisibility => V(Entry.Kind == ClipKind.Files);
    public Visibility PinVisibility => V(Entry.Pinned);
    public Visibility SourceVisibility => V(SourceBrush is not null);
    public Visibility SourcePictureVisibility => V(SourcePicture is not null);
    public Visibility HotkeyVisibility => V(Number > 0);
    public Visibility SendVisibility => V(ModuleGate.On(Contracts.ModuleIds.Launcher));   // 启动器停用时没有「交给启动器」（055）
    public Visibility WordsVisibility => V(CanSplit);   // 分词（093）：只给普通文字条目，分词能力开着时
    public bool CanSplit => Entry.Kind == ClipKind.Text && !string.IsNullOrWhiteSpace(Entry.Text) && ModuleGate.On(Contracts.ModuleIds.Words);
    public bool Pinned => Entry.Pinned;
    public string PinTip => Entry.Pinned ? Strings.Clipboard_UnpinHint : Strings.Clipboard_PinHint;

    // 右下角：平时显示 Alt+数字，选中或悬停时换成操作按钮（原型 .clip-end）
    bool selected;
    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            Changed(nameof(HotkeyOpacity)); Changed(nameof(ActionsOpacity)); Changed(nameof(ActionsHitTest));
        }
    }
    public double HotkeyOpacity => selected ? 0 : 0.85;
    public double ActionsOpacity => selected ? 1 : 0;
    public bool ActionsHitTest => selected;

    public void Send() => actions.Send(this);
    public void Words() => actions.Words(this);
    public void TogglePin() => actions.TogglePin(this);
    public void Delete() => actions.Delete(this);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- 小工具 ----

    public static string Ago(DateTimeOffset at)
    {
        var min = (int)(DateTimeOffset.Now - at).TotalMinutes;
        if (min < 1) return Strings.Clipboard_JustNow;
        if (min < 60) return string.Format(Strings.Clipboard_MinutesAgo, (object?)(min));
        if (min < 1440) return string.Format(Strings.Clipboard_HoursAgo, (object?)(min / 60));
        if (min < 2880) return Strings.Clipboard_Yesterday;
        return string.Format(Strings.Clipboard_DaysAgo, (object?)(min / 1440));
    }

    // 来源图标只有几十个程序，按路径缓存；14px 显示，按逻辑像素解码
    static readonly Dictionary<string, ImageSource> icons = new(StringComparer.OrdinalIgnoreCase);

    static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
        if (icons.TryGetValue(path, out var cached)) return cached;
        if (icons.Count > 100) icons.Clear();
        var image = new BitmapImage { DecodePixelWidth = 14, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(path) };
        icons[path] = image;
        return image;
    }

    static LinearGradientBrush Gradient(string from, string to) => new()
    {
        // 原型 linear-gradient(160deg, …)
        StartPoint = new Point(0.329, 0.03),
        EndPoint = new Point(0.671, 0.97),
        GradientStops = { new GradientStop { Color = Hex(from), Offset = 0 }, new GradientStop { Color = Hex(to), Offset = 1 } },
    };

    static Windows.UI.Color Hex(string s) => TryHex(s, out var c) ? c : Windows.UI.Color.FromArgb(255, 128, 128, 128);

    static bool TryHex(string? s, out Windows.UI.Color color)
    {
        color = default;
        if (s is null || s.Length != 7 || s[0] != '#' || !uint.TryParse(s.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>色块：服务认的几种写法都要能画（#RGB、#RRGGBB、#AARRGGBB、rgb(r,g,b)、rgba(r,g,b,a)）。</summary>
    static bool TryColor(string? text, out Windows.UI.Color color)
    {
        color = default;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s[0] == '#')
        {
            var hex = s[1..];
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, inv, out var v)) return false;
            switch (hex.Length)
            {
                case 3:
                    color = Windows.UI.Color.FromArgb(255, (byte)((v >> 8 & 0xF) * 17), (byte)((v >> 4 & 0xF) * 17), (byte)((v & 0xF) * 17));
                    return true;
                case 6:
                    color = Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                    return true;
                case 8:
                    color = Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                    return true;
            }
            return false;
        }
        var open = s.IndexOf('(');
        if (open < 0 || !s.EndsWith(')')) return false;
        var name = s[..open].Trim().ToLowerInvariant();
        var parts = s[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (!(name == "rgb" && parts.Length == 3 || name == "rgba" && parts.Length == 4)) return false;
        var rgb = new byte[3];
        for (var i = 0; i < 3; i++)
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.Integer, inv, out rgb[i])) return false;
        double alpha = 1;
        if (parts.Length == 4 && (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out alpha) || alpha is < 0 or > 1)) return false;
        color = Windows.UI.Color.FromArgb((byte)Math.Round(alpha * 255), rgb[0], rgb[1], rgb[2]);
        return true;
    }
}

/// <summary>条目上三个按钮的去处（由视图实现）。</summary>
public interface IClipRowActions
{
    void Send(ClipRow row);
    void Words(ClipRow row);
    void TogglePin(ClipRow row);
    void Delete(ClipRow row);
    /// <summary>标签的色点；不带色点为 null。</summary>
    Brush? TagDot(string name);
    /// <summary>Pane 云：别的电脑来的图片原图大小（「正在下载原图 1.2 MB」）；不知道为 null。</summary>
    long? OriginalSize(string clipId) => null;
    /// <summary>Pane 云：别的电脑来的图片显示出来了，缩略图还没下载就排队下载（IClipboardCloud.EnsureThumbnail）。</summary>
    void EnsureThumbnail(string clipId) { }
}

/// <summary>标签色点（令牌 PaneTag1～6）。</summary>
public static class TagColors
{
    public static Brush? Dot(int? color) => color is >= 1 and <= 6 && Application.Current.Resources.TryGetValue($"PaneTag{color}Brush", out var b) ? (Brush)b : null;
}

/// <summary>元信息行上的一个小片：标签（可带色点）、「带格式」（透明底、次要色）、「+N」。</summary>
public sealed partial class ClipTagChip
{
    ClipTagChip(string name, Brush? dot, bool filled, double opacity) { Label = name; Dot = dot; Fill = filled ? Res("PaneFillControlBrush") : null; Stroke = filled ? Res("PaneStrokeBrush") : null; TextOpacity = opacity; }
    public static ClipTagChip Tag(string name, Brush? dot) => new(name, dot, true, 1);
    public static ClipTagChip Rich() => new(Strings.Clipboard_RichMark, null, false, 0.68);
    public static ClipTagChip More(int n) => new("+" + n, null, true, 0.68);
    public string Label { get; }
    public Brush? Dot { get; }
    public double TextOpacity { get; }
    public Visibility DotVisibility => Dot is null ? Visibility.Collapsed : Visibility.Visible;
    public Brush? Fill { get; }
    public Brush? Stroke { get; }
    static Brush? Res(string key) => Application.Current.Resources.TryGetValue(key, out var b) ? b as Brush : null;
}

/// <summary>
/// 就地操作行（2026-11-23，原型 .clip-op）：操作列表的一项，或标签选择的一个标签 / 「新建标签」。
/// Id：操作 Id（paste、paste-path…），标签行为 null。
/// </summary>
public sealed partial class ClipOp : INotifyPropertyChanged
{
    public ClipOp(string? id, string title, string glyph, string keyText = "", int number = 0, bool danger = false)
    {
        Id = id; Title = title; Glyph = glyph; KeyText = keyText; Danger = danger;
        Number = number is >= 1 and <= 9 ? number.ToString() : "";
    }
    public string? Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string KeyText { get; }
    public string Number { get; }
    public bool Danger { get; }
    /// <summary>标签行：标签名；「新建标签」行：要新建的名字。</summary>
    public string? Tag { get; set; }
    public bool Create { get; set; }
    public string Sub { get; set; } = "";
    public Brush? Dot { get; set; }
    public Visibility DotVisibility => Dot is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Dot is null ? Visibility.Visible : Visibility.Collapsed;
    public Brush? Foreground => Danger && Application.Current.Resources.TryGetValue("PaneDangerBrush", out var d) ? (Brush)d
        : Application.Current.Resources.TryGetValue("PaneText1Brush", out var t) ? (Brush)t : null;
    bool on;
    /// <summary>标签行：这一条已经打上这个标签（右侧打勾、文字加粗）。</summary>
    public bool On
    {
        get => on;
        set { if (on == value) return; on = value; PropertyChanged?.Invoke(this, new(nameof(CheckVisibility))); PropertyChanged?.Invoke(this, new(nameof(Weight))); }
    }
    public Visibility CheckVisibility => on ? Visibility.Visible : Visibility.Collapsed;
    public Windows.UI.Text.FontWeight Weight => on ? FontWeights.SemiBold : FontWeights.Normal;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 已固定区底下的展开行（原型 .more-row）：收起时「还有 N 条」，展开后「收起」，箭头转 180°。
/// 和条目一样能被选中，没有编号；选中 / 悬停时文字从次要色变正文色。
/// </summary>
public sealed partial class ClipMoreRow : INotifyPropertyChanged
{
    public ClipMoreRow(int index, int hidden, bool expanded)
    {
        Index = index;
        Label = expanded ? Strings.Common_Collapse : string.Format(Strings.Clipboard_MorePinned, (object?)hidden);
        Angle = expanded ? 180 : 0;
    }
    public int Index { get; }
    public string Label { get; }
    public double Angle { get; }
    bool selected;
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextOpacity))); }
    }
    // 次要色近似：正文色 × 透明度（同筛选小标签）
    public double TextOpacity => selected ? 1 : 0.68;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ClipSection
{
    public ClipSection(string label, ClipMoreRow? more = null) { Label = label; More = more; }
    public string Label { get; }
    /// <summary>已固定区折叠时的展开行；其余分组为 null。</summary>
    public ClipMoreRow? More { get; }
    public Visibility MoreVisibility => More is null ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>可追加：列表往下滚时分批补条目，不重建已有的行。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ClipRow> Rows { get; } = [];
}

/// <summary>
/// 元信息行上的小片（2026-11-23）：用附加属性在代码里生成，不在条目模板里再套一层 DataTemplate
/// （嵌套模板里放 ItemsPanel 时 XAML 编译器会内部出错）。16 高、圆角 8、左右 6；色点 5；字 10.5 次要文字色。
/// </summary>
public static class ClipChips
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(object), typeof(ClipChips), new PropertyMetadata(null, Changed));
    public static object? GetSource(StackPanel target) => target.GetValue(SourceProperty);
    public static void SetSource(StackPanel target, object? value) => target.SetValue(SourceProperty, value);

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (StackPanel)d;
        panel.Children.Clear();
        if (e.NewValue is not IEnumerable<ClipTagChip> chips) return;
        var text2 = Application.Current.Resources.TryGetValue("PaneText2Brush", out var t) ? (Brush)t : null;
        var font = (FontFamily)Application.Current.Resources["PaneFontUi"];
        foreach (var c in chips)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (c.Dot is { } dot) row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = c.Label, FontFamily = font, FontSize = 10.5, LineHeight = 14, Foreground = text2, Opacity = c.TextOpacity, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new Border { Height = 16, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(8), Background = c.Fill, BorderBrush = c.Stroke,
                BorderThickness = new Thickness(c.Stroke is null ? 0 : 1), Child = row });
        }
    }
}

/// <summary>剪贴板的搜索命中：加粗 + 中性浅底（docs/design.md「主题色」：命中不用主题色）。</summary>
public static class ClipMatch
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(object), typeof(ClipMatch), new PropertyMetadata(null, Changed));

    public static object? GetSource(TextBlock target) => target.GetValue(SourceProperty);
    public static void SetSource(TextBlock target, object? value) => target.SetValue(SourceProperty, value);

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (TextBlock)d;
        block.Inlines.Clear();
        block.TextHighlighters.Clear();
        if (e.NewValue is not ClipText t) return;
        var text = t.Text;
        var q = t.Query;
        if (q.Length == 0) { block.Inlines.Add(new Run { Text = text }); return; }
        var highlighter = new TextHighlighter { Background = MatchBrush(block), Foreground = block.Foreground };   // 不给前景色时高亮段用系统默认（黑色），深色下看不见
        int at = 0;
        while (true)
        {
            var i = text.IndexOf(q, at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;
            if (i > at) block.Inlines.Add(new Run { Text = text[at..i] });
            block.Inlines.Add(new Run { Text = text.Substring(i, q.Length), FontWeight = FontWeights.SemiBold });
            highlighter.Ranges.Add(new TextRange { StartIndex = i, Length = q.Length });
            at = i + q.Length;
        }
        if (at < text.Length) block.Inlines.Add(new Run { Text = text[at..] });
        if (highlighter.Ranges.Count > 0) block.TextHighlighters.Add(highlighter);
    }

    static Brush MatchBrush(FrameworkElement element)
    {
        var dark = element.ActualTheme == ElementTheme.Dark;
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
            if (dict.ThemeDictionaries.TryGetValue(dark ? "Dark" : "Light", out var theme)
                && ((ResourceDictionary)theme).TryGetValue("PaneMatchBackgroundBrush", out var brush))
                return (Brush)brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(36, 128, 128, 128));
    }
}

/// <summary>类型筛选的小标签；2026-11-23 起也放标签（Key 为「tag:名字」，可带色点）和类型与标签之间的分隔（Key 为「sep」）。</summary>
public sealed partial class ChipItem : INotifyPropertyChanged
{
    public ChipItem(string key, string label, Brush? dot = null) { Key = key; Label = label; Dot = dot; }
    public string Key { get; }
    public string Label { get; }
    public Brush? Dot { get; }
    bool Separator => Key == "sep";
    public Visibility DotVisibility => Dot is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SeparatorVisibility => Separator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelVisibility => Separator ? Visibility.Collapsed : Visibility.Visible;
    public Thickness ChipPadding => Separator ? new Thickness(6, 0, 6, 0) : new Thickness(12, 0, 12, 0);
    bool selected;
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Changed(nameof(Weight)); Changed(nameof(TextOpacity)); }
    }
    public Windows.UI.Text.FontWeight Weight => selected ? FontWeights.SemiBold : FontWeights.Normal;
    public double TextOpacity => selected ? 1 : 0.68;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
