using Pane.Localization;
// 文件窗口的界面数据：左侧分类、中间结果行。颜色和图标照原型 prototype/launcher.html 的 TYPES。
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Views.Controls;

namespace Pane.Views.Files;

public static class FileKinds
{
    public static string Name(FileCategory c) => c switch
    {
        FileCategory.All => Strings.Common_All, FileCategory.Folder => Strings.Files_CategoryFolder, FileCategory.Document => Strings.Files_CategoryDocument, FileCategory.Spreadsheet => Strings.Files_CategorySpreadsheet,
        FileCategory.Presentation => Strings.Files_CategoryPresentation, FileCategory.Pdf => "PDF", FileCategory.Text => Strings.Common_Text, FileCategory.Image => Strings.Common_Image,
        FileCategory.Video => Strings.Files_CategoryVideo, FileCategory.Audio => Strings.Files_CategoryAudio, FileCategory.Archive => Strings.Files_CategoryArchive, FileCategory.Program => Strings.Files_CategoryProgram, _ => Strings.Common_File,
    };

    public static string Glyph(FileCategory c) => c switch
    {
        FileCategory.Folder => "folder", FileCategory.Text => "text", FileCategory.Image => "image", FileCategory.Video => "video",
        FileCategory.Audio => "music", FileCategory.Archive or FileCategory.Program => "app", _ => "doc",
    };

    // 类型色块：只在图标底上用，不用于文字（docs/design.md「主题色」）
    public static string Color(FileCategory c) => c switch
    {
        FileCategory.Folder => "#e8a93a", FileCategory.Document => "#3d7be0", FileCategory.Spreadsheet => "#2f9e5b", FileCategory.Presentation => "#e06a3d",
        FileCategory.Pdf => "#d64545", FileCategory.Text => "#6b7385", FileCategory.Image => "#8a5be0", FileCategory.Video => "#d0447c",
        FileCategory.Audio => "#1fa3a3", FileCategory.Archive => "#8d6e4f", _ => "#4a5263",
    };

    static readonly Dictionary<FileCategory, SolidColorBrush> brushes = [];
    public static SolidColorBrush Brush(FileCategory c)
    {
        if (brushes.TryGetValue(c, out var b)) return b;
        var v = Convert.ToUInt32(Color(c)[1..], 16);
        return brushes[c] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v));
    }

    // 行尾类型标签（issue 60 / 82，原型 launcher.html 的 typeTag）：文件是大写扩展名（最多 8 个字母，再长或没有就不显示），
    // 文件夹是「文件夹」。启动器的文件行和文件窗口共用
    public static string? TypeTag(string name, bool folder)
    {
        if (folder) return Strings.Files_CategoryFolder;
        var ext = System.IO.Path.GetExtension(name);
        return ext.Length is >= 2 and <= 9 ? ext[1..].ToUpperInvariant() : null;
    }

    public static string Size(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(".0 ", " "),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    // 今天 10:42 / 昨天 18:05 / 10 月 21 日 / 2025 年 12 月 30 日（原型的写法）
    public static string When(DateTimeOffset? t)
    {
        if (t is not { } v) return "";
        var local = v.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return string.Format(Strings.Files_TodayTime, local.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (local.Date == today.AddDays(-1)) return string.Format(Strings.Files_YesterdayTime, local.ToString("HH:mm", CultureInfo.InvariantCulture));
        return local.Year == today.Year ? string.Format(Strings.Files_MonthDay, (object?)(local.Month), (object?)(local.Day)) : string.Format(Strings.Files_YearMonthDay, (object?)(local.Year), (object?)(local.Month), (object?)(local.Day));
    }
}

/// <summary>系统文件图标（036）：服务给的 64 px PNG，同一张只解码一次。</summary>
public static class SystemIcons
{
    static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> cache = new(StringComparer.OrdinalIgnoreCase);
    public static bool Enabled => ((App)Application.Current).Store.Current.Icons.SystemFileIcons;
    public static ImageSource? Load(string? png)
    {
        if (string.IsNullOrEmpty(png)) return null;
        if (cache.TryGetValue(png, out var hit)) return hit;
        if (cache.Count > 500) cache.Clear();
        return cache[png] = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 64, DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical, UriSource = new Uri(png) };
    }
}

// 注意：x:Bind 的属性名不要和所在页面上元素的 x:Name 重名（如 CountText），否则 XAML 编译器内部崩溃（WMC9999，错误文本还丢失）
public sealed partial class CategoryItem : INotifyPropertyChanged
{
    public CategoryItem(FileCategory category) { Category = category; Name = FileKinds.Name(category); }
    public FileCategory Category { get; }
    public string Name { get; }
    int? count;
    public string CountText => count is > 0 ? count.Value.ToString("N0", CultureInfo.InvariantCulture) : "";
    public void SetCount(int? value) { if (count == value) return; count = value; Changed(nameof(CountText)); }
    bool selected;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; Changed(nameof(SelectedVisibility)); Changed(nameof(Weight)); Changed(nameof(TextOpacity)); } }
    public Visibility SelectedVisibility => selected ? Visibility.Visible : Visibility.Collapsed;
    public Windows.UI.Text.FontWeight Weight => selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    public double TextOpacity => !enabled ? 0.4 : selected ? 1 : 0.78;   // 未选中用次要文字的观感（同一支画刷降不透明度）；不可用的再淡
    bool enabled = true;
    /// <summary>为「选择文件夹」对话框选时，文件夹以外的分类不可用（任务 125）：变淡、点了不切、Tab 跳过。</summary>
    public bool Enabled { get => enabled; set { if (enabled == value) return; enabled = value; Changed(nameof(TextOpacity)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed partial class FileRow : INotifyPropertyChanged
{
    public FileRow(FileHit hit, int index) { Hit = hit; Index = index; Match = new(hit.Name, hit.Highlights); }
    public FileHit Hit { get; }
    public int Index { get; }
    public MatchedText Match { get; }
    public string Folder => System.IO.Path.GetDirectoryName(Hit.Path) ?? Hit.Path;
    public string Modified => FileKinds.When(Hit.Modified);
    /// <summary>今天改过的文件，时间加粗（issue 60）。</summary>
    public Windows.UI.Text.FontWeight ModifiedWeight => Hit.Modified is { } m && m.ToLocalTime().Date == DateTime.Today ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    // 文件夹的类型已经写在行尾标签里，大小这一行不再写「文件夹」
    public string Size => FileKinds.Size(Hit.Size);
    public Visibility SizeVisibility => Hit.Category == FileCategory.Folder ? Visibility.Collapsed : Visibility.Visible;
    public string? TypeTag => FileKinds.TypeTag(Hit.Name, Hit.Category == FileCategory.Folder);
    public Visibility TypeTagVisibility => TypeTag is null ? Visibility.Collapsed : Visibility.Visible;
    public string Glyph => FileKinds.Glyph(Hit.Category);
    public Brush Tile => FileKinds.Brush(Hit.Category);
    /// <summary>系统图标（设置 → 外观 → 文件图标：系统，036）；还没取到时为 null，先显示彩色类型图块。</summary>
    public ImageSource? SystemIcon { get; private set; }
    public void SetSystemIcon(string? png)
    {
        var image = SystemIcons.Load(png);
        if (ReferenceEquals(image, SystemIcon)) return;
        SystemIcon = image;
        Changed(nameof(SystemIcon)); Changed(nameof(SystemIconVisibility)); Changed(nameof(TileVisibility));
    }
    public Visibility SystemIconVisibility => SystemIcon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TileVisibility => SystemIcon is null ? Visibility.Visible : Visibility.Collapsed;
    bool selected;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; Changed(nameof(SelectedVisibility)); } }
    public Visibility SelectedVisibility => selected ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
