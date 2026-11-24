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
    string? ContentPath = null)            // Image：完整图片（PNG）的本机路径，粘贴、另存为、复制图片用；界面显示只用 Thumbnail
{
    /// <summary>标签名（2026-11-23，106）：顺序 = 打上的先后。有标签的条目长期保存：不受条数上限、不被「清空历史」清掉，和固定一样。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>带格式（106）：复制时同时存下了 HTML 或 RTF。Enter 粘贴时一并写回，Shift+Enter 只写文字。</summary>
    public bool Rich { get; init; }
    /// <summary>
    /// 别的设备同步来的条目（Pane 云，Contracts/Cloud.cs）：那台设备的名字，说明行写「微信 · 💻 书房台式机 · 3 分钟前」。
    /// 本机复制的为 null。设备改名后跟着变（由同步服务按设备 id 查当前名字）。
    /// </summary>
    public string? SourceDevice { get; init; }
    /// <summary>别的设备来的图片：原图在不在本机（docs/cloud.md「剪贴板同步」→ 图片）。本机复制的永远是 Local。</summary>
    public ClipOriginal Original { get; init; } = ClipOriginal.Local;
}

/// <summary>
/// Local：原图在本机（ContentPath 可用）。Remote：只有缩略图，粘贴、打开、另存时才下载。
/// Downloading：正在下载（界面写「正在下载原图 1.2 MB」，大小在 IClipboardCloud.OriginalSize）。
/// Expired：云端原图已过期（不保留的 7 天后），只剩缩略图，不能粘贴原图。
/// NotSynced：原图没有同步到云端（超过 10 MB，或复制时云端空间已满），只剩缩略图；只能在复制它的那台电脑上粘贴原图。
/// QuotaExceeded：本月流量用完，下月 1 日后才能下载。
/// </summary>
public enum ClipOriginal { Local, Remote, Downloading, Expired, QuotaExceeded, NotSynced }   // 按数字存进本机索引：只往后加，不插队（2026-11-24 NotSynced 放最后）

/// <summary>标签定义（106）。Color：1～6 对应令牌 PaneTag1～6，null 不带色点。名字不分大小写唯一，顺序就是筛选条上的顺序（新建的排最后）。</summary>
public sealed record ClipTag(string Name, int? Color);

/// <summary>操作列表里的「粘贴为…」（106）。都写成文字（ImageFile 除外），写法同 PasteAsync（自身标记、切回原窗口、等松开修饰键再 Ctrl+V）。</summary>
public enum ClipPasteAs
{
    /// <summary>Files：完整路径，多个按行（CRLF）分开。</summary>
    Paths,
    /// <summary>Files：文件名（含扩展名），多个按行分开。</summary>
    FileNames,
    /// <summary>Image：把完整图片存成临时目录里的 PNG 文件（文件名照 Strings.Clipboard_ImageFileName），按文件（CF_HDROP）粘贴。</summary>
    ImageFile,
    /// <summary>Color：「#RRGGBB」大写。</summary>
    Hex,
    /// <summary>Color：「rgb(88, 116, 201)」。</summary>
    Rgb,
}

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
    /// <summary>清空未固定、也没有标签的条目（设置页「清空历史」）。已固定的、有标签的保留。</summary>
    void ClearUnpinned();

    // ---------------- 2026-11-23（106）：操作列表、标签、编辑，规则见 docs/design.md「剪贴板操作列表、富文本、标签、编辑、高度」 ----------------

    /// <summary>按指定方式粘贴（见 <see cref="ClipPasteAs"/>）。条目类型不适用时抛 NotSupportedException。成功后同 PasteAsync 处理「粘贴后移到最前」。</summary>
    Task PasteAsAsync(string id, ClipPasteAs kind, nint targetWindow, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// 编辑文字类条目（Text / Code / Link / Color）：换成新文字，按内容重新判断类型，去掉保存的格式（Rich 变 false）；
    /// 复制时间、固定、标签不变。新文字为空时什么也不做。其他类型抛 NotSupportedException。
    /// </summary>
    void UpdateText(string id, string text) { }

    /// <summary>全部标签定义。</summary>
    IReadOnlyList<ClipTag> Tags() => [];

    /// <summary>
    /// 整体替换这一条的标签。名字两端去空白、去重（不分大小写，以定义里的写法为准）；还没有定义的名字自动新建（Color = null，排最后）。
    /// 去掉最后一个标签后变回普通条目：面板开着时不立刻淘汰，下次有新复制或面板收起后再按上限处理。
    /// </summary>
    void SetTags(string id, IReadOnlyList<string> tags) { }

    /// <summary>改名、改色点（设置 → 剪贴板 →「标签」）。新名字和别的标签重名时抛 ArgumentException（Message 给用户看）；条目上的名字跟着改。</summary>
    void UpdateTag(string name, string newName, int? color) { }

    /// <summary>删除标签：只从所有条目上去掉这个名字，内容不删（之后按普通条目处理）。</summary>
    void DeleteTag(string name) { }

    /// <summary>
    /// 历史存不进硬盘时的原因（045）：一句给用户看的中文，如「保存失败：文件被占用（已重试 12 次）」；正常时为 null。
    /// 设置页剪贴板分区显示它；变化时触发 <see cref="Changed"/>。
    /// </summary>
    string? StorageProblem => null;
}
