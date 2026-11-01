// 剪贴板历史：界面和剪贴板服务之间的接口。归设计负责人定义，工程照着实现真实服务。
// 规则依据 docs/requirements.md「剪贴板」和 docs/design.md「技术决策」（图片、文件列表保存完整内容，能再次粘贴）。
// 界面开发期间用 DesignData/ClipboardSample 的假数据。
namespace Pane.Contracts;

/// <summary>条目类型。Code / Link / Color 是文本的细分，由服务按内容判断；界面按类型选图标和排版。</summary>
public enum ClipKind { Text, Code, Link, Color, Image, Files }

/// <summary>
/// 一张图：真实数据给本机位图路径（缩略图由服务生成好，不在界面里解码原图）；
/// 界面假数据没有位图时用渐变色块代替。
/// </summary>
public sealed record ClipPicture(string? Path, string? GradientFrom = null, string? GradientTo = null);

public sealed record ClipEntry(
    string Id,
    ClipKind Kind,
    string? Text,                         // 文本类的正文；Image 时是说明（如「截图」）；Files 时为 null
    IReadOnlyList<string>? Files,         // Files：完整路径，界面只显示文件名
    ClipPicture? Thumbnail,               // Image：缩略图；Files：复制的文件里有图片时，第一张图片的缩略图（045），ImageWidth / ImageHeight 同时给原图尺寸
    int? ImageWidth,
    int? ImageHeight,
    string Source,                        // 来源程序的显示名（如「微信」）
    LauncherIcon? SourceIcon,             // 来源程序的小图标；没有就不显示
    DateTimeOffset CopiedAt,
    bool Pinned,
    string? ContentPath = null);           // Image：完整图片（PNG）的本机路径，粘贴、另存为、复制图片用；界面显示只用 Thumbnail

public interface IClipboardHistory
{
    /// <summary>当前全部条目。排序：已固定在前，其余按复制时间从新到旧。</summary>
    IReadOnlyList<ClipEntry> Snapshot();

    /// <summary>历史变化（新复制、删除、固定、粘贴后挪到最前）。可能在后台线程触发，界面自己切回 UI 线程。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 粘贴：把条目写回剪贴板（带「Pane 自己写的」标记，不再进历史），恢复 <paramref name="targetWindow"/> 为前台，
    /// 等用户松开修饰键后发送 Ctrl+V。<paramref name="plainText"/> 为 true 时只写纯文本。
    /// 界面先收起面板再调用。失败抛带类型的异常（目标窗口已关闭、权限受限、超时）。
    /// </summary>
    Task PasteAsync(string id, bool plainText, nint targetWindow, CancellationToken cancellationToken);

    void SetPinned(string id, bool pinned);
    void Delete(string id);
    /// <summary>清空未固定的条目（设置页「清空历史」）。已固定的保留。</summary>
    void ClearUnpinned();

    /// <summary>
    /// 历史存不进硬盘时的原因（045）：一句给用户看的中文，如「保存失败：文件被占用（已重试 12 次）」；正常时为 null。
    /// 设置页剪贴板分区显示它；变化时触发 <see cref="Changed"/>。
    /// </summary>
    string? StorageProblem => null;
}
