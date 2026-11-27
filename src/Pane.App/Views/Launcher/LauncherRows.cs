// 启动器结果在界面上的形态：分组（格子 / 列表 / 算式）和每一项。
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.Views.Controls;
using Windows.Foundation;

namespace Pane.Views.Launcher;

/// <summary>每一项在界面上是什么：决定排法、底部提示栏的说明和按键。</summary>
/// <remarks>Menu：Ctrl+K / 右键之后就地列出的附加操作（050）。</remarks>
public enum LauncherCell { Calc, App, Command, Action, File, More, Attachment, Menu }

public sealed partial class LauncherRow : INotifyPropertyChanged
{
    public LauncherRow(LauncherItem item, int index, LauncherCell cell, string? number = null)
    {
        Item = item;
        Index = index;
        Cell = cell;
        Number = number;
        Match = new MatchedText(item.Title, item.Highlights);
        picture = Load(item.Icon.ImagePath);
        if (item.Icon.GradientFrom is { } from && item.Icon.GradientTo is { } to)
        {
            // 原型 linear-gradient(135deg, from, to)
            Gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = { new GradientStop { Color = Hex(from), Offset = 0 }, new GradientStop { Color = Hex(to), Offset = 1 } },
            };
        }
    }

    public LauncherItem Item { get; }
    public int Index { get; }
    public LauncherCell Cell { get; }
    public MatchedText Match { get; }
    string? warning;
    /// <summary>副标题；需要确认的命令（关机、重启）第一次执行时换成确认提示，执行失败时换成原因。</summary>
    public string? Subtitle => warning ?? Item.Subtitle;
    public void SetWarning(string? text) { if (warning == text) return; warning = text; Changed(nameof(Subtitle)); Changed(nameof(SubtitleVisibility)); }
    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>文件的路径用等宽字体（原型 .row .s）；其余用界面字体。</summary>
    public FontFamily SubtitleFont => (Cell == LauncherCell.File || Item.Group == LauncherGroups.Terminal && Item.Id != "terminal:hint") && warning is null ? Mono : Pane.Hosting.GlassParameters.UiFont;   // 文件路径、终端命令用等宽
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    public string Glyph => Item.Icon.Glyph;
    public string? Hotkey => Item.Hotkey;
    /// <summary>文件、文件夹行尾的类型标签（issue 82）：大写扩展名或「文件夹」。</summary>
    public string? TypeTag => Cell == LauncherCell.File && Item.Kind is LauncherItemKind.File or LauncherItemKind.Folder
        ? Pane.Views.Files.FileKinds.TypeTag(Item.Title, Item.Kind == LauncherItemKind.Folder) : null;
    public Visibility TypeTagVisibility => TypeTag is null ? Visibility.Collapsed : Visibility.Visible;
    public string Title => Item.Title;
    public string CalcText => "= " + Item.Title;
    public Brush? Gradient { get; }
    /// <summary>真实应用图标（服务给的位图路径）。有它就不画 Lucide 图块。</summary>
    public ImageSource? Picture => picture;
    ImageSource? picture;
    /// <summary>文件行换成系统图标（036）：取到了就补上，图块换成图片。</summary>
    public void SetSystemIcon(string? png)
    {
        if (Pane.Views.Files.SystemIcons.Load(png) is not { } image || ReferenceEquals(image, picture)) return;
        picture = image;
        Changed(nameof(Picture)); Changed(nameof(PictureVisibility)); Changed(nameof(ColoredVisibility)); Changed(nameof(PlainVisibility)); Changed(nameof(PlainGlyphVisibility));
    }

    /// <summary>网页搜索历史行（issue 39）：搜过的词。选中（含鼠标悬停）时行尾出 ×，Shift+Delete 也能删。</summary>
    public string? HistoryText { get; init; }
    public Action<LauncherRow>? Remove { get; init; }
    bool showRemove;
    public bool ShowRemove { get => showRemove; set { value &= HistoryText is not null; if (showRemove == value) return; showRemove = value; Changed(nameof(RemoveVisibility)); } }
    public Visibility RemoveVisibility => showRemove ? Visibility.Visible : Visibility.Collapsed;
    public void OnRemoveClick(object sender, RoutedEventArgs e) => Remove?.Invoke(this);

    public Visibility PictureVisibility => Picture is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ColoredVisibility => Picture is null && Gradient is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainVisibility => Picture is null && Gradient is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 动作行里的网页搜索站点（issue 56）：中性底图块里放站点自己的图标，不再一排都是地球。
    /// 图形和搜索框的引擎徽章同一处理（<see cref="EngineBadgeImages"/>）：一般图标按笔画范围居中，自带底色的整张铺满图块。取不到就保留地球。
    /// </summary>
    internal void SetEngine(EngineBadgeImages.Glyph? glyph)
    {
        engineGlyph = glyph is { FullBleed: false } ? glyph.Source : null;
        engineFull = glyph is { FullBleed: true } ? glyph.Source : null;
        Changed(nameof(SiteGlyph)); Changed(nameof(SiteFull));
        Changed(nameof(SiteGlyphVisibility)); Changed(nameof(SiteFullVisibility)); Changed(nameof(PlainGlyphVisibility));
    }
    ImageSource? engineGlyph, engineFull;
    public ImageSource? SiteGlyph => engineGlyph;
    public ImageSource? SiteFull => engineFull;
    public Visibility SiteGlyphVisibility => engineGlyph is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SiteFullVisibility => engineFull is null ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>中性图块上的线条图标：有站点图标时让位。</summary>
    public Visibility PlainGlyphVisibility => Picture is null && Gradient is null && engineGlyph is null && engineFull is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HotkeyVisibility => string.IsNullOrEmpty(Item.Hotkey) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>动作格子的序号（1～7）：按住 Alt 时显示，Alt+数字直接触发。</summary>
    public string? Number { get; }
    // 左上角的序号徽章：按住 Alt 时动作格子显示 Number；按住 Ctrl 时前 9 个结果显示 1～9（issue 75，Ctrl+数字直接打开）
    string? badge;
    public string? Badge => badge;
    public bool ShowNumber { get => badge is not null && badge == Number; set => SetBadge(value ? Number : null); }
    public void SetBadge(string? text) { if (badge == text) return; badge = text; Changed(nameof(Badge)); Changed(nameof(NumberVisibility)); }
    public Visibility NumberVisibility => badge is null ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>算在「结果」里、可以用 Ctrl+数字打开的行：应用、命令、文件（动作格子归 Alt；算式卡片没地方放徽章，「查看全部」和附件也不算）。</summary>
    public bool IsResult => Cell is LauncherCell.App or LauncherCell.Command or LauncherCell.File;

    // 同一个图标只解码一次；按 2 倍显示尺寸解码（行 32、格子 40 DIP），图标缓存是 256px，不按原尺寸解码省内存
    static readonly Dictionary<string, BitmapImage> pictures = new(StringComparer.OrdinalIgnoreCase);
    static ImageSource? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (pictures.TryGetValue(path, out var cached)) return cached;
        if (pictures.Count > 300) pictures.Clear();
        var image = new BitmapImage { DecodePixelWidth = 80, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(path) };
        pictures[path] = image;
        return image;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    static Windows.UI.Color Hex(string s)
    {
        var v = Convert.ToUInt32(s.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

public enum LauncherSectionKind { List, Tiles, Calc }

public sealed class LauncherSection
{
    public LauncherSection(string label, LauncherSectionKind kind, List<LauncherRow> rows) { Label = label; Kind = kind; Rows = rows; }
    public string Label { get; }
    public string? GroupId { get; init; }
    public LauncherSectionKind Kind { get; }
    public List<LauncherRow> Rows { get; }
}

public sealed partial class LauncherSectionSelector : DataTemplateSelector
{
    public DataTemplate? List { get; set; }
    public DataTemplate? Tiles { get; set; }
    public DataTemplate? Calc { get; set; }
    protected override DataTemplate? SelectTemplateCore(object item) => item is LauncherSection s
        ? s.Kind switch { LauncherSectionKind.Tiles => Tiles, LauncherSectionKind.Calc => Calc, _ => List } : List;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed partial class LauncherRowSelector : DataTemplateSelector
{
    public DataTemplate? Row { get; set; }
    public DataTemplate? More { get; set; }
    protected override DataTemplate? SelectTemplateCore(object item) => item is LauncherRow { Cell: LauncherCell.More } ? More : Row;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
