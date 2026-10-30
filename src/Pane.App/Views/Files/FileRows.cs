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
        FileCategory.All => "全部", FileCategory.Folder => "文件夹", FileCategory.Document => "文档", FileCategory.Spreadsheet => "表格",
        FileCategory.Presentation => "演示", FileCategory.Pdf => "PDF", FileCategory.Text => "文本", FileCategory.Image => "图片",
        FileCategory.Video => "视频", FileCategory.Audio => "音频", FileCategory.Archive => "压缩包", FileCategory.Program => "程序", _ => "文件",
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
        if (local.Date == today) return "今天 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today.AddDays(-1)) return "昨天 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Year == today.Year ? $"{local.Month} 月 {local.Day} 日" : $"{local.Year} 年 {local.Month} 月 {local.Day} 日";
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
    public double TextOpacity => selected ? 1 : 0.78;   // 未选中用次要文字的观感（同一支画刷降不透明度）
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
    public string Size => Hit.Category == FileCategory.Folder ? "文件夹" : FileKinds.Size(Hit.Size);
    public string Glyph => FileKinds.Glyph(Hit.Category);
    public Brush Tile => FileKinds.Brush(Hit.Category);
    bool selected;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; Changed(nameof(SelectedVisibility)); } }
    public Visibility SelectedVisibility => selected ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
