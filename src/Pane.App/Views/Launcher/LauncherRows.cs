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
public enum LauncherCell { Calc, App, Command, Action, File, More, Attachment }

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
    public FontFamily SubtitleFont => (Cell == LauncherCell.File || Item.Group == "终端" && Item.Id != "terminal:hint") && warning is null ? Mono : Pane.Hosting.GlassParameters.UiFont;   // 文件路径、终端命令用等宽
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    public string Glyph => Item.Icon.Glyph;
    public string? Hotkey => Item.Hotkey;
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
        Changed(nameof(Picture)); Changed(nameof(PictureVisibility)); Changed(nameof(ColoredVisibility)); Changed(nameof(PlainVisibility));
    }

    public Visibility PictureVisibility => Picture is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ColoredVisibility => Picture is null && Gradient is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainVisibility => Picture is null && Gradient is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HotkeyVisibility => string.IsNullOrEmpty(Item.Hotkey) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>动作格子的序号（1～7）：按住 Alt 时显示，Alt+数字直接触发。</summary>
    public string? Number { get; }
    bool showNumber;
    public bool ShowNumber { get => showNumber; set { if (showNumber == value || Number is null) return; showNumber = value; Changed(nameof(NumberVisibility)); } }
    public Visibility NumberVisibility => showNumber ? Visibility.Visible : Visibility.Collapsed;

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
