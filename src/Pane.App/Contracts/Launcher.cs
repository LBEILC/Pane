// 启动器：界面和搜索服务之间的接口。归设计负责人定义，工程照着实现真实服务。
// 界面开发期间用 DesignData/LauncherSampleSearch 的假数据。
namespace Pane.Contracts;

public enum LauncherItemKind { App, File, Folder, Command, Calculation, Web, Ai, Action }

/// <summary>标题里要高亮的一段（按 UTF-16 下标）。搜索命中时加粗加下划线。</summary>
public readonly record struct TextSpan(int Start, int Length);

/// <summary>
/// 图标。真实应用图标由服务给出处理好的位图路径（按 docs/design.md「应用图标的显示」做过遮罩）；
/// 没有位图时用 Lucide 图标名 <see cref="Glyph"/>；GradientFrom/To 只给界面假数据的彩色图块用。
/// </summary>
public sealed record LauncherIcon(string Glyph, string? ImagePath = null, string? GradientFrom = null, string? GradientTo = null);

/// <summary>
/// 一条结果。AI 类条目的 Id 写成 "ai:动作Id"（如 "ai:translate"），单轮提问是 "ai:ask"；界面据此打开 AI 结果卡片。
/// </summary>
public sealed record LauncherItem(
    string Id,
    LauncherItemKind Kind,
    string Group,                         // 稳定分组标识：使用 LauncherGroups；同组条目相邻给出
    string Title,
    string? Subtitle,
    LauncherIcon Icon,
    IReadOnlyList<TextSpan>? Highlights = null,
    string? Hotkey = null);               // 右侧显示的快捷键提示，例如 "Tab"、"Alt 1"

/// <param name="IsHome">输入为空时的「最近使用 / 已固定」，按图块显示。</param>
/// <param name="FileTotal">
/// 文件一共命中多少个（Everything 的总数，不只是 Items 里给出的几个）。启动器只列前 3 个，
/// 超过 3 个时显示「在文件窗口里查看全部 N 个」。0 表示不知道，界面就按 Items 里的文件个数算。
/// </param>
/// <remarks>
/// 2026-10-29 新排法（docs/design.md「启动器排法与二级窗口」）：应用、命令排成格子（最多两行 14 个），
/// 常用文件 / 文件夹和文件排成列表，算式是卡片；AI 和网页搜索改由界面的「动作行」给出，服务不再返回 AI / 网页两组（返回了界面也忽略）。
/// </remarks>
public sealed record LauncherResult(IReadOnlyList<LauncherItem> Items, bool IsHome, int FileTotal = 0);

public interface ILauncherSearch
{
    /// <summary>取消即放弃这次结果。空字符串返回首页图块。</summary>
    Task<LauncherResult> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>
    /// 分批给结果（2026-10-30，026 补测：完整输入后约 140 ms 结果区才开始变化，慢于 uTools）。
    /// 界面每次按键都直接调用、不再防抖，新的一次会取消上一次。
    /// 本地来源（应用、命令、算式、常用文件）一齐好了就调用一次 <paramref name="partial"/>，不等文件搜索这类慢来源；
    /// 返回值仍是全部来源的完整结果（慢来源超时就不含它）。慢来源比本地来源先回来、或者没有慢来源时，可以不调用 partial。
    /// partial 可在任意线程调用，界面自己切回界面线程；取消之后不要再调用。默认实现不分批。
    /// </summary>
    Task<LauncherResult> SearchAsync(string query, Action<LauncherResult> partial, CancellationToken cancellationToken)
        => SearchAsync(query, cancellationToken);

    /// <summary>执行一项（打开应用 / 文件、运行命令、复制计算结果……）。AI 类条目由界面自己处理，不走这里。</summary>
    Task ExecuteAsync(LauncherItem item, CancellationToken cancellationToken);

    /// <summary>
    /// 以管理员身份执行（037，2026-11-01 设计加）：终端项和应用都支持，按 Ctrl+Shift+Enter 时调用，走系统 UAC，
    /// 用户在 UAC 里点「否」静默（错误码 1223）。其他项没有管理员方式，照常执行。
    /// </summary>
    Task ExecuteElevatedAsync(LauncherItem item, CancellationToken cancellationToken) => ExecuteAsync(item, cancellationToken);
}

/// <summary>
/// 终端命令（037）：输入以 > 开头时，SearchAsync 先给「在终端里运行」，后面跟最多 6 条补全（最近运行过的命令、PATH 里的程序名），
/// 补全项 Id 写成 "complete:&lt;完整命令&gt;"、Group 为 LauncherGroups.Completion；界面按 Tab 把它填进输入框，Enter 直接运行它（走 ExecuteAsync）。
/// 「在终端里运行」那一项 Group 为 LauncherGroups.Terminal、副标题是命令本身；输入只有 > 时给一条提示项，Id 写成 "terminal:hint"、Group 为 LauncherGroups.Terminal、
/// 标题「输入要在终端里运行的命令」，界面不执行它（2026-11-01 设计定）。系统设置和组件的 Group 为 LauncherGroups.Settings，界面排成列表。
/// </summary>
public interface ITerminalHistory
{
    /// <summary>清空记住的命令（设置 → 启动器 → 记住终端命令 旁的「清空」）。</summary>
    void Clear();
}

/// <summary>网页搜索建议（037）：只在用户用搜索关键字搜网页时调用（界面已防抖）；取不到、超时（1 秒）或站点不支持都返回空，不抛异常。</summary>
public interface IWebSuggestions
{
    /// <summary>最多 8 条，按站点给的顺序。不支持的站点（没有公开建议接口）直接返回空。</summary>
    Task<IReadOnlyList<string>> SuggestAsync(Pane.Settings.SearchEngine engine, string query, CancellationToken cancellationToken);
}

/// <summary>
/// 启动器的附件（复制后 3 秒内呼出自动带入，或从剪贴板「交给启动器」）上的非 AI 动作。
/// 动作 Id：文件 open / reveal / copy-path；图片 save-image / copy-image；文字 web-search。
/// 附件就是剪贴板条目（<see cref="ClipEntry"/>）；带入附件不清空系统剪贴板。
/// </summary>
public interface ILauncherAttachmentActions
{
    Task RunAsync(string actionId, ClipEntry attachment, CancellationToken cancellationToken);
}

/// <summary>文件搜索（Everything）的状态。设置页「启动器 → 文件搜索」显示它，「重新检测」调用 <see cref="Refresh"/>。</summary>
public enum FileSearchState { Ready, NotRunning, Unsupported }

public interface IFileSearchStatus
{
    FileSearchState State { get; }
    /// <summary>Everything 的版本（如 "1.4.1.1026"）；没连上为 null。</summary>
    string? Version { get; }
    /// <summary>状态变化。可能在后台线程触发，界面自己切回 UI 线程。</summary>
    event EventHandler? Changed;
    /// <summary>立即重新检测（设置页按钮）。</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>启动器分组的稳定标识；显示标题由界面资源提供。</summary>
public static class LauncherGroups
{
    public const string Applications = "applications";
    public const string Files = "files";
    public const string Commands = "commands";
    public const string Calculation = "calculation";
    public const string Ai = "ai";
    public const string Web = "web";
    public const string Settings = "settings";
    public const string Terminal = "terminal";
    public const string Recent = "recent";
    public const string Pinned = "pinned";
    public const string Completion = "completion";
    public const string Favorites = "favorites";
    public const string Images = "images";
    public const string Other = "other";
    public const string Actions = "actions";
}

/// <summary>启动器结果的附加操作（050，issue 10）。顺序就是菜单里的顺序。</summary>
public enum LauncherItemAction { RunAsAdmin, OpenLocation, CopyPath, Properties, Uninstall }

/// <summary>
/// 右键 / Ctrl+K 菜单的数据。LauncherSearch 和 DesignData/LauncherSampleSearch 都实现它。
/// </summary>
public interface ILauncherItemActions
{
    /// <summary>
    /// 这一项能用哪些附加操作，按枚举顺序给出，「打开」不在里面（界面自己放在第一项）。
    /// 可以稍慢（解析快捷方式、读卸载登记），在后台线程做，界面最多等 300 ms，超时就不弹菜单；取消即放弃。
    /// 不支持的项不要给，例如商店应用没有「以管理员身份运行」「打开所在位置」。
    /// </summary>
    Task<IReadOnlyList<LauncherItemAction>> GetActionsAsync(LauncherItem item, CancellationToken cancellationToken);

    /// <summary>
    /// 执行一项。失败抛 LauncherActionException（Message 是给用户看的一句话，进资源）；UAC 里点「否」静默返回（1223）。
    /// RunAsAdmin 和 ExecuteElevatedAsync 走同一个实现。
    /// </summary>
    Task RunAsync(LauncherItem item, LauncherItemAction action, CancellationToken cancellationToken);
}

public sealed class LauncherActionException(string message) : Exception(message);
