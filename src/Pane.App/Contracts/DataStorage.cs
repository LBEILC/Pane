// 设置「数据」分区和服务之间的接口（设计负责人定义，2026-11-08；082 / 083 由工程实现）。
// 规则见 docs/design.md「数据分区」。数据目录 = %APPDATA%\Pane；「大文件」= 剪贴板图片（以后加截图、备忘录附件），位置可以改。
namespace Pane.Contracts;

/// <summary>存储占用的分类。</summary>
public enum StorageCategory
{
    /// <summary>settings.json、gestures.json</summary>
    Settings,
    /// <summary>启动器使用次数、终端历史</summary>
    Usage,
    /// <summary>剪贴板历史和图片（图片在大文件位置时也算在这里）</summary>
    Clipboard,
    /// <summary>启动器索引、应用图标、遮罩图标、文件图标、下载的更新包；可清理，清后自动重新生成</summary>
    Cache,
    /// <summary>logs 文件夹；可清空</summary>
    Logs,
}

/// <param name="Folder">「打开文件夹」打开的位置；没有就是 null。</param>
public sealed record StorageUsage(StorageCategory Category, long Bytes, int Files, string? Folder);

public sealed record StorageReport(IReadOnlyList<StorageUsage> Items)
{
    public long Total => Items.Sum(i => i.Bytes);
}

/// <summary>大文件位置的状态。</summary>
/// <param name="Path">正在使用的位置（完整路径）。</param>
/// <param name="IsDefault">是不是默认位置（数据目录下）。</param>
/// <param name="Configured">用户选的位置；没改过就是 null。</param>
/// <param name="Unavailable">用户选的位置现在不可用，正在用默认位置代替（Path 是默认位置）。</param>
public sealed record LargeFilesLocation(string Path, bool IsDefault, string? Configured, bool Unavailable);

/// <summary>失败时给用户看的一句话（进资源）。</summary>
public sealed class DataStorageException(string message, Exception? inner = null) : Exception(message, inner);

public interface IDataStorage
{
    /// <summary>统计各类占用。在后台线程做，可能要一两秒；取消即放弃。</summary>
    Task<StorageReport> MeasureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 清理一类：只接受 Cache 和 Logs，其他抛 ArgumentException。
    /// 正在使用、删不掉的文件跳过；清完缓存要让启动器索引和图标在后台重新生成。返回清掉的字节数。
    /// </summary>
    Task<long> ClearAsync(StorageCategory category, CancellationToken cancellationToken);

    /// <summary>当前大文件位置。</summary>
    LargeFilesLocation LargeFiles { get; }

    /// <summary>
    /// 把大文件搬到 folder（null = 搬回默认位置）。先复制、逐个核对（大小 + 哈希），全部成功才切换并删除旧文件；
    /// 任何一步失败都留在原处、清掉复制了一半的文件，抛 DataStorageException。progress 报 0～1。
    /// folder 必须是本机固定磁盘上能写的文件夹，不能在安装目录、系统目录里，不能是数据目录本身，否则抛 DataStorageException。
    /// 搬的过程中新复制的图片要么等搬完再存，要么跟着搬过去，不能丢。
    /// </summary>
    Task MoveLargeFilesAsync(string? folder, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Unavailable 时，用户点「重新使用」：用户选的位置又能用了，就把默认位置里临时存的文件合并过去，再切回。</summary>
    Task ReuseConfiguredAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>占用、大文件位置有变化（搬完、清完、新存了图片不用通知）。可能在后台线程触发。</summary>
    event EventHandler? Changed;
}

// ---------------- 导入导出（083）----------------
// 实现 IDataStorage 的同一个对象也实现 IDataTransfer，界面用 `Data as IDataTransfer` 取。

/// <summary>剪贴板导出到什么程度。</summary>
public enum ClipboardExport
{
    /// <summary>不带剪贴板</summary>
    None,
    /// <summary>只带文字类条目（文字、代码、链接、颜色），不带图片和文件条目</summary>
    TextOnly,
    /// <summary>文字类条目加图片条目（连同图片文件）；文件条目（只是路径）不带</summary>
    WithImages,
}

/// <param name="Settings">settings.json 和 gestures.json（去掉本机专属的项，见任务 083）</param>
/// <param name="Usage">启动器使用次数、终端历史</param>
public sealed record DataExportOptions(bool Settings, bool Usage, ClipboardExport Clipboard);

public sealed record DataExportResult(string Path, long Bytes);

/// <summary>导入文件里有什么。</summary>
/// <param name="AppVersion">导出时 Pane 的版本，例如 0.1.443。</param>
/// <param name="ClipboardEntries">剪贴板条目数，没带剪贴板时为 0。</param>
/// <param name="ClipboardImages">其中图片条目数。</param>
public sealed record DataImportPreview(string AppVersion, DateTimeOffset ExportedAt, bool HasSettings, bool HasUsage, int ClipboardEntries, int ClipboardImages);

public enum DataImportMode
{
    /// <summary>合并：使用记录累加，剪贴板只加入现在没有的条目</summary>
    Merge,
    /// <summary>覆盖：用文件里的内容替换现有的</summary>
    Replace,
}

/// <param name="Mode">只影响使用记录和剪贴板；设置一律用文件里的替换（本机专属的项保留）。</param>
public sealed record DataImportOptions(bool Settings, bool Usage, bool Clipboard, DataImportMode Mode);

public interface IDataTransfer
{
    /// <summary>导出成一个 zip 到 path（已存在就覆盖）。失败抛 DataStorageException，不留下半个文件。progress 报 0～1。</summary>
    Task<DataExportResult> ExportAsync(string path, DataExportOptions options, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>读出导入文件里有什么，不改任何数据。不是 Pane 的导出文件、已损坏、格式比当前版本新时抛 DataStorageException。</summary>
    Task<DataImportPreview> InspectAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 校验并把要导入的内容放进待导入区，不改现有数据；返回后界面重启 Pane，
    /// 启动早期（各服务读数据之前）先备份当前数据，再应用待导入的内容。
    /// 失败抛 DataStorageException，待导入区清空，现有数据不受影响。
    /// </summary>
    Task StageImportAsync(string path, DataImportOptions options, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// 上次启动时应用导入的结果：null 表示上次没有导入；否则给出备份文件位置，失败时带原因（这时数据已恢复原样）。
    /// 界面读到后提示一次，然后调 AcknowledgeImport。
    /// </summary>
    DataImportOutcome? LastImport { get; }

    /// <summary>提示过了，清掉 LastImport。</summary>
    void AcknowledgeImport();
}

/// <param name="Backup">导入前备份的 zip 完整路径。</param>
/// <param name="Error">失败原因（给用户看的一句话）；成功时为 null。</param>
public sealed record DataImportOutcome(string Backup, string? Error);
