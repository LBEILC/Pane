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

    public ClipRow(ClipEntry entry, int index, string query, IClipRowActions actions)
    {
        Entry = entry;
        Index = index;
        this.actions = actions;
        Body = new ClipText(entry.Text ?? string.Empty, query);
        FileLines = (entry.Files ?? []).Select(f => new ClipText(System.IO.Path.GetFileName(f), query)).ToList();
        if (entry.Thumbnail is { } t) Thumb = PictureBrush(t);
        if (entry.Kind == ClipKind.Color && TryColor(entry.Text, out var c)) Swatch = new SolidColorBrush(c);
        SourcePicture = LoadIcon(entry.SourceIcon?.ImagePath);
        if (SourcePicture is null && entry.SourceIcon is { GradientFrom: { } from, GradientTo: { } to }) SourceBrush = Gradient(from, to);
        var dims = entry.Kind == ClipKind.Image && entry.ImageWidth is { } w && entry.ImageHeight is { } h ? $"{entry.Text} {w}×{h} · " : "";
        MetaText = $"{entry.Source} · {dims}{Ago(entry.CopiedAt)}";
    }

    public ClipEntry Entry { get; }
    public int Index { get; }
    public ClipText Body { get; }
    public List<ClipText> FileLines { get; }
    public Brush? Thumb { get; }
    public Brush? Swatch { get; }
    public Brush? SourceBrush { get; }
    /// <summary>来源程序的真实小图标（服务缓存的 PNG）。有它就不画渐变图块。</summary>
    public ImageSource? SourcePicture { get; }
    public string MetaText { get; }

    public string TypeGlyph => Entry.Kind switch
    {
        ClipKind.Code => "code",
        ClipKind.Link => "link",
        ClipKind.Color => "drop",
        ClipKind.Image => "image",
        ClipKind.Files => "folder",
        _ => "text",
    };
    public string SourceGlyph => Entry.SourceIcon?.Glyph ?? "app";
    public string Hotkey => Index < 9 ? $"Alt {Index + 1}" : "";

    static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => V(Entry.Kind is ClipKind.Text or ClipKind.Link);
    public Visibility CodeVisibility => V(Entry.Kind == ClipKind.Code);
    public Visibility ColorVisibility => V(Entry.Kind == ClipKind.Color);
    public Visibility ImageVisibility => V(Entry.Kind == ClipKind.Image);
    public Visibility FilesVisibility => V(Entry.Kind == ClipKind.Files);
    public Visibility PinVisibility => V(Entry.Pinned);
    public Visibility SourceVisibility => V(SourceBrush is not null);
    public Visibility SourcePictureVisibility => V(SourcePicture is not null);
    public Visibility HotkeyVisibility => V(Index < 9);
    public bool Pinned => Entry.Pinned;
    public string PinTip => Entry.Pinned ? "取消固定（Ctrl+P）" : "固定（Ctrl+P）";

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
    public void TogglePin() => actions.TogglePin(this);
    public void Delete() => actions.Delete(this);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- 小工具 ----

    public static string Ago(DateTimeOffset at)
    {
        var min = (int)(DateTimeOffset.Now - at).TotalMinutes;
        if (min < 1) return "刚刚";
        if (min < 60) return $"{min} 分钟前";
        if (min < 1440) return $"{min / 60} 小时前";
        if (min < 2880) return "昨天";
        return $"{min / 1440} 天前";
    }

    static Brush PictureBrush(ClipPicture p)
    {
        if (p.Path is { } path) return new ImageBrush { ImageSource = new BitmapImage(new Uri(path)), Stretch = Stretch.UniformToFill };
        return Gradient(p.GradientFrom ?? "#808080", p.GradientTo ?? "#404040");
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
    void TogglePin(ClipRow row);
    void Delete(ClipRow row);
}

public sealed partial class ClipSection
{
    public ClipSection(string label) { Label = label; }
    public string Label { get; }
    /// <summary>可追加：列表往下滚时分批补条目，不重建已有的行。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ClipRow> Rows { get; } = [];
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
        var highlighter = new TextHighlighter { Background = MatchBrush(block) };
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

/// <summary>类型筛选的小标签。</summary>
public sealed partial class ChipItem : INotifyPropertyChanged
{
    public ChipItem(string key, string label) { Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
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
