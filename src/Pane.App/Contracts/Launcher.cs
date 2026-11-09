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
/// 网页搜索历史（issue 39，2026-11-09 设计定）：用关键字搜网页时记下搜过的词，按站点分开（以 <c>UrlTemplate</c> 区分，改关键字不丢历史），只存本机。
/// 界面在关键字模式下混排：只输入了关键字时列最近的词；打字后历史里匹配的词在上（最多 3 条），站点的联想词在下。
/// 设置 → 启动器 →「记住网页搜索」关掉后不再记录，<see cref="Recent"/> 也返回空（已有记录保留，「清空」才删）。
/// 所有方法都在界面线程调用，不得阻塞（内存里查，写盘放后台）。
/// </summary>
public interface IWebSearchHistory
{
    /// <summary>
    /// 记一次搜索：去掉首尾空白；空白、以空白开头（照终端命令的规矩，前面加空格就不记）、超过 200 字的不记。
    /// 同一站点里不分大小写去重，重复的移到最前；每个站点最多 50 条，超出删最旧的。
    /// </summary>
    void Record(Pane.Settings.SearchEngine engine, string text);

    /// <summary>
    /// 最近的记录，新的在前。<paramref name="prefix"/> 为空时返回最近 <paramref name="max"/> 条；
    /// 否则返回包含它的（不分大小写），以它开头的排在前面，同类按时间新的在前；和 <paramref name="prefix"/> 完全相同的不返回。
    /// </summary>
    IReadOnlyList<string> Recent(Pane.Settings.SearchEngine engine, string prefix, int max);

    /// <summary>删掉一条（界面上按 Shift+Delete 或点行尾的 ×）。</summary>
    void Remove(Pane.Settings.SearchEngine engine, string text);

    /// <summary>清空全部站点的记录（设置里的「清空」）。</summary>
    void Clear();

    /// <summary>全部站点的记录总数，设置里显示用。</summary>
    int Count { get; }
}

/// <summary>引擎图标的种类：彩色原样显示；单色由界面按主题上色（有 <see cref="EngineIcon.Tint"/> 时用这个品牌色，深色主题下界面再提亮）。</summary>
public enum EngineIconKind { Color, Monochrome }

/// <summary>引擎图标：透明底 PNG（长边 128 px 以内，界面按实际笔画范围缩放）。</summary>
public sealed record EngineIcon(string Path, EngineIconKind Kind, string? Tint = null);

/// <summary>
/// 关键字搜网页时搜索框左边的「引擎徽章」用的图标（issue 43，2026-11-09 设计定，见 docs/design.md「网页搜索的引擎徽章」）。
/// 默认的五个站点按地址的域名对上内置图标（随程序发布，总是有）；用户自己加的站点运行时从网站取一次、缓存到本机。
/// 界面线程调用，不得阻塞。
/// </summary>
public interface IEngineIcons
{
    /// <summary>
    /// 有就立刻返回（内置的、已缓存的）；没有就返回 null，并在后台去取，取好了发 <see cref="Ready"/>。
    /// 取不到（网站没有图标、断网、格式不支持）返回 null，界面用首字兜底；失败结果也缓存一段时间，不反复联网。
    /// </summary>
    EngineIcon? TryGet(Pane.Settings.SearchEngine engine);

    /// <summary>某个站点的图标取好了，参数是它的 <c>UrlTemplate</c>。可能在后台线程触发。</summary>
    event EventHandler<string>? Ready;
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
    public const string Timer = "timer";   // 定时关机（058）：「30 分钟后关机」这类说法的结果，排在最前，列表样式
}

/// <summary>
/// 启动器结果的附加操作（050，issue 10）。顺序就是菜单里的顺序。
/// RemoveFromApps（057，issue 19）：只给单独添加的便携程序和记住的程序，从 LauncherSettings.Portable 里去掉（不删文件），之后重建索引；
/// 文件夹扫描出来的、安装的软件都不给。
/// </summary>
public enum LauncherItemAction { RunAsAdmin, OpenLocation, CopyPath, Properties, Uninstall, RemoveFromApps, EditAliases, RemoveFromRecent }
// EditAliases 由界面打开编辑，不传给 RunAsync；仅对已索引应用提供。
// RemoveFromRecent（设计加，2026-11-09，用户要求）：「从最近使用中移除」。只对有使用记录的应用提供（首页「最近使用」里的、搜索里用过的都算）；
// RunAsync 删掉这个应用的使用记录（次数和最近使用时间），它就不再出现在「最近使用」里，搜索排序也不再因用过而靠前。
// 不动「已固定」，不动应用本身和索引；记录是 launcher-usage.json 里的那一条，删除后照常写盘。

/// <summary>应用的搜索别名（078）。appId 就是应用行的 LauncherItem.Id。</summary>
public interface IAppAliases
{
    /// <summary>某个应用的别名，没有就返回空列表。</summary>
    IReadOnlyList<string> Get(string appId);
    /// <summary>
    /// 设置别名，传空列表就是清除。去掉首尾空白、空项，不区分大小写去重并保留首次写法。
    /// 去重后超过 8 个、单个超过 32 个字符或含空白字符时抛 ArgumentException。
    /// 保存后下一次搜索立即生效。
    /// </summary>
    void Set(string appId, IReadOnlyList<string> aliases);
    /// <summary>按应用名排序；失去索引的应用使用保存时的名字，Missing=true。</summary>
    IReadOnlyList<AppAliasInfo> All();
    /// <summary>别名或索引重建后的名字、图标、存在状态变化；可能在后台线程触发。</summary>
    event EventHandler? Changed;
}
/// <param name="IconPath">和 IAppIconCatalog 给的图标相同；没有则为 null。</param>
public sealed record AppAliasInfo(string AppId, string Name, string? IconPath, IReadOnlyList<string> Aliases, bool Missing);

/// <summary>便携软件某一项的扫描状态（057）。</summary>
public enum PortableScanState { Scanning, Ready, Missing }

/// <param name="Programs">文件夹：扫到几个程序（去掉辅助程序和与已安装软件重复的之后）；程序：1。Scanning / Missing 时为 0。</param>
public sealed record PortableStatus(PortableScanState State, int Programs);

/// <summary>
/// 设置页「便携软件」每一行的副标题用（057）。键是 PortableEntry.Path（不分大小写）。
/// 索引重建完成、文件夹或程序被删时触发 Changed（可在后台线程，界面自己切回）；没扫过的路径返回 Scanning。
/// </summary>
public interface IPortableAppsStatus
{
    PortableStatus Get(string path);
    event EventHandler? Changed;
}

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
