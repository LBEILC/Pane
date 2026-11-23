// 对话框搜索（2026-11-23 用户定，issue 81，Listary 的同类功能）。归设计负责人定义；由工程实现（107）。
// 交互（docs/design.md「对话框搜索」，原型 prototype/dialog-search.html）：
// - 038 能识别的标准「打开 / 另存为」对话框在前台时，Pane 在对话框外的下边缘贴一条搜索框（放不下贴到上边缘外），跟着对话框移动、缩放；
// - 搜索框不激活出现；点击或在对话框前台时按启动器热键才聚焦；
// - 空输入列「已打开的资源管理器窗口」「最近使用」（IFolderJump.Entries）；打字后先文件夹、后文件（Everything）；
// - 选中后对话框跳过去；「打开」对话框里的文件默认直接打开（设置可改为仅填入文件名）；
// - 设置：FolderJumpSettings.SearchBar（总开关）、OpenOnEnter、FilterByType，经 IFolderJump.Apply 传给服务。
namespace Pane.Contracts;

/// <summary>
/// 一个标准文件对话框的当前状态。
/// Left / Top / Right / Bottom：对话框窗口的矩形（物理像素、屏幕坐标，不含系统阴影）。
/// Foreground：对话框是不是前台窗口。Minimized：最小化了。
/// Save：另存为对话框（否则是打开）。
/// Extensions：对话框当前选中的文件类型限定的扩展名（小写、不带点，如 png）；「所有文件」、读不到或 FilterByType 关着时为 null。
/// FilterName：那个文件类型的名字（如「图像文件」），读不到为 null。
/// </summary>
public sealed record FileDialogInfo(nint Dialog, int Left, int Top, int Right, int Bottom, bool Foreground, bool Minimized, bool Save,
    IReadOnlyList<string>? Extensions, string? FilterName);

public interface IDialogSearch
{
    /// <summary>
    /// 对话框出现、移动、缩放、前台变化、最小化 / 还原、文件类型变化时触发，参数是最新状态；对话框关闭时参数为 null。
    /// 同一时刻只跟一个对话框（最近一个成为前台的）。移动时最多约 60 次 / 秒。可能在后台线程触发。
    /// SearchBar 关着时不触发、也不监听位置。
    /// </summary>
    event EventHandler<FileDialogInfo?>? DialogChanged;

    /// <summary>当前跟着的对话框；没有为 null。</summary>
    FileDialogInfo? Current { get; }

    /// <summary>
    /// 搜索：文件夹在前（最多 8 个），文件在后（最多 maxFiles 个），各自按 Everything 的相关度。text 为空返回空。
    /// extensions 不为 null 时文件只给这些扩展名，文件夹照常。总耗时有上限（约 1 秒），超时返回已经拿到的部分。
    /// Everything 不可用抛 <see cref="FileSearchUnavailableException"/>。
    /// </summary>
    Task<IReadOnlyList<FileHit>> SearchAsync(string text, IReadOnlyList<string>? extensions, int maxFiles, CancellationToken cancellationToken);

    /// <summary>
    /// 让对话框跳到 folder，跳完把对话框设回前台。
    /// fileName 不为 null：跳完把文件名框写成它；为 null：文件名框保持原来的文字。
    /// confirm：写好文件名后确认（打开对话框里就是打开这个文件；另存为由对话框自己提示是否覆盖）。
    /// 对话框已经关了就什么也不做。
    /// </summary>
    Task NavigateAsync(nint dialog, string folder, string? fileName, bool confirm, CancellationToken cancellationToken);
}
