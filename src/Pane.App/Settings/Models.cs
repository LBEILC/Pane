namespace Pane.Settings;
[Flags] public enum KeyModifiers { None=0, Ctrl=1, Alt=2, Shift=4, Win=8 }
public enum ThemeMode { System, Light, Dark }
public sealed record PaneSettings(int Version, GeneralSettings General, LauncherSettings Launcher, ClipboardSettings Clipboard) { AiSettings ai=new(); public AiSettings Ai { get=>ai; init=>ai=value??new(); } IconSettings icons=new(); public IconSettings Icons { get=>icons; init=>icons=value??new(); } SuperPanelSettings superPanel=new(); public SuperPanelSettings SuperPanel { get=>superPanel; init=>superPanel=value??new(); } UpdateSettings update=new(); public UpdateSettings Update { get=>update; init=>update=value??new(); } RenameSettings rename=new(); public RenameSettings Rename { get=>rename; init=>rename=value??new(); } FolderJumpSettings folderJump=new(); public FolderJumpSettings FolderJump { get=>folderJump; init=>folderJump=value??new(); }
    StorageSettings storage=new(); public StorageSettings Storage { get=>storage; init=>storage=value??new(); }
    // 能力模块的启用状态（设计加，2026-11-04，055）：键是 Pane.Contracts.ModuleIds，没写的用 ModuleInfo.DefaultEnabled。
    // 旧设置文件没有这项时，按旧的总开关迁移（Clipboard / SuperPanel / FolderJump 的 Enabled），见 docs/design.md「能力模块」
    Dictionary<string,bool>? modules; public Dictionary<string,bool>? Modules { get=>modules; init=>modules=value; } }
// 文件对话框跳转（设计加，2026-11-01，046）：用户要能在设置里调。Ctrl+G / Ctrl+Shift+G 可改（null = 默认组合）；
// AutoJump = 对话框刚打开时自动跳到最近用过的资源管理器文件夹（只跳一次）；ShowRecent = 菜单里列出最近用过的文件夹；MenuWhenMany（047）= 开着多个资源管理器时 Ctrl+G 改为弹菜单让用户选
public sealed record FolderJumpSettings(bool Enabled=true, Hotkey? JumpHotkey=null, Hotkey? MenuHotkey=null, bool AutoJump=false, bool ShowRecent=true, bool MenuWhenMany=false)
{
    public static readonly Hotkey DefaultJump=new(KeyModifiers.Ctrl,(ushort)'G'), DefaultMenu=new(KeyModifiers.Ctrl|KeyModifiers.Shift,(ushort)'G');
    [System.Text.Json.Serialization.JsonIgnore] public Hotkey Jump=>JumpHotkey??DefaultJump;
    [System.Text.Json.Serialization.JsonIgnore] public Hotkey Menu=>MenuHotkey??DefaultMenu;
}
// 软件内更新（设计加，2026-10-30，023）：AutoUpdate 开着就自动检查并在后台下载，下载好后提示重启更新
// 设计加（2026-11-05，issue 25、062）：InstallOnStartup = 下次启动时自动安装已下载好的更新（默认关）；
// IgnoredVersion = 提示条上点了「忽略」的版本；SeenVersion = 看过「更新后」提示的版本，NewsShown = 这一版的提示已经出现过几次（最多 3 次）；
// AutoInstallFailedVersion = 启动时自动安装失败过的版本，同一版本不再自动装（服务写）
public sealed record UpdateSettings(bool AutoUpdate=true, bool InstallOnStartup=false, string? IgnoredVersion=null,
    string? SeenVersion=null, int NewsShown=0, string? AutoInstallFailedVersion=null);
// 批量重命名（设计加，2026-10-31，040）：存下来的规则组合（预设）；LastRules = 上次执行时的规则，下次打开窗口接着用
public sealed record RenamePreset(string Name, Pane.Contracts.RenameRule[] Rules);
public sealed record RenameSettings
{
    RenamePreset[] presets=[];
    public RenamePreset[] Presets { get=>presets; init=>presets=value??[]; }
    Pane.Contracts.RenameRule[] lastRules=[];
    public Pane.Contracts.RenameRule[] LastRules { get=>lastRules; init=>lastRules=value??[]; }
}
public enum SuperPanelButton { Middle=2, X1=4, X2=8 }
public sealed record SuperPanelSettings(bool Enabled=true, SuperPanelButton TriggerButton=SuperPanelButton.X1, int HoldMilliseconds=400, bool PauseInFullscreen=true, bool ClipboardFallback=true)
{
    string[] disabledPrograms=[];
    public string[] DisabledPrograms { get=>disabledPrograms; init=>disabledPrograms=value??[]; }
    /// <summary>
    /// 面板里的动作（设计加，2026-10-30），按显示顺序；最多 <see cref="MaxActions"/> 个。
    /// Id：AI 预设动作 "ai:&lt;动作 Id&gt;"、"search"（默认站点搜索）、"copy"（复制）。认不出的 Id 跳过。
    /// null = 默认：全部 AI 预设动作，再加搜索、复制，取前 6 个。自动翻译时的那一排是固定的，不受这项影响。
    /// </summary>
    public string[]? Actions { get; init; }
    public const int MaxActions=6;
    public static string[] DefaultActions(IEnumerable<string> aiActionIds)=>[.. aiActionIds.Select(id=>"ai:"+id).Append("search").Append("copy").Take(MaxActions)];
}
public sealed record GlassThemeParameters(double TintOpacity, double LuminosityOpacity, double Saturation, double Highlight, double Shadow, double ExtraBlur);
public sealed record GeneralSettings(bool StartWithWindows, bool ShowTrayIcon, ThemeMode Theme, string AccentColor, double PanelRadius, double MotionSpeed, GlassThemeParameters GlassLight, GlassThemeParameters GlassDark) { /* 界面字体（设计加，2026-10-30）：本机字体的字族名；null = 默认（MiSans VF，没装时微软雅黑）。重启后生效 */ public string? UiFont { get; init; } /* 界面语言（设计加，2026-11-02，任务 048）：null = 跟随系统（繁体系统用繁体，其余用简体）；"zh-Hans" / "zh-Hant"，以后加 "en"。重启后生效 */ public string? UiLanguage { get; init; } }
// DoubleTap（设计加，2026-11-04，054，issue 18）：true = 连按两下单个修饰键呼出，这时 Modifiers = None，
// VirtualKey 只能是左右区分的修饰键 VK_LSHIFT / VK_RSHIFT / VK_LCONTROL / VK_RCONTROL / VK_LMENU / VK_RMENU（0xA0～0xA5）。
// 只有启动器和剪贴板的热键允许这样设；不能用 RegisterHotKey 注册，宿主改用键盘钩子识别，没选时不挂钩子
public sealed record Hotkey(KeyModifiers Modifiers, ushort VirtualKey)
{
    public bool DoubleTap { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsValidDoubleTap=>DoubleTap&&Modifiers==KeyModifiers.None&&VirtualKey is >= 0xA0 and <= 0xA5;
}
public enum KeepInputMode { Clear, OneMinute, Always }
public sealed record FavoriteEntry(string Path, bool IsFolder);
public sealed record AppAliasEntry(string AppId, string Name, string[] Aliases);
public sealed record SearchEngine(string Name, string Keyword, string UrlTemplate);
// 空启动器显示什么（设计加，2026-10-31，用户反馈要极简）：最近和固定 / 只显示固定 / 什么都不显示（只剩搜索栏）
public enum LauncherHome { RecentAndPinned, PinnedOnly, Nothing }
public enum LauncherWidth { Compact, Standard, Wide }
public sealed record LauncherSettings(Hotkey Hotkey, LauncherPosition? Position=null,
    bool HideOnBlur=true, KeepInputMode KeepInput=KeepInputMode.Clear,
    int AutoAttachSeconds=3, int FileResultCount=8, LauncherHome Home=LauncherHome.RecentAndPinned,
    // 设计加（2026-10-31，037）：RememberCommands = 记住 > 运行过的最近 20 条命令用来补全（空格开头的不记）；
    // WebSuggestions = 用搜索关键字搜网页时（如「bd 天气」）向那个站点取搜索建议，平时打字不联网
    bool RememberCommands=true, bool WebSuggestions=true,
    // 设计加（2026-11-05，issue 24、062）：宽度三档（510 / 592 / 756，一行 6 / 7 / 9 个应用）、应用最多几行（1～4）、文件最多几个（3 / 5 / 8 / 10）
    LauncherWidth Width=LauncherWidth.Standard, int AppRows=2, int FileRows=3)
{
    // 设计加（2026-11-09，issue 38）：呼出时先只出搜索栏，再向下展开首页内容；默认关。「未输入时显示」为「都不显示」时不起作用
    public bool ExpandOnOpen { get; init; }
    FavoriteEntry[] favorites=[];
    public FavoriteEntry[] Favorites { get=>favorites; init=>favorites=value??[]; }
    AppAliasEntry[] appAliases=[];
    public AppAliasEntry[] AppAliases { get=>appAliases; init=>appAliases=value??[]; }
    SearchEngine[] searchEngines=DefaultSearchEngines();
    // 设计改（2026-11-01，issue 8）：默认加上 Google。存过的列表如果和旧默认完全一样（没改过），读进来时换成新默认；改过的不动
    public SearchEngine[] SearchEngines { get=>searchEngines; init=>searchEngines=value==null||IsOldDefault(value)?DefaultSearchEngines():value; }
    static bool IsOldDefault(SearchEngine[] v)=>v.Length==4&&v[0]==new SearchEngine("必应","bing","https://www.bing.com/search?q={query}") // 不翻译：用户数据
        &&v[1]==new SearchEngine("百度","bd","https://www.baidu.com/s?wd={query}")&&v[2]==new SearchEngine("GitHub","gh","https://github.com/search?q={query}") // 不翻译：用户数据
        &&v[3]==new SearchEngine("哔哩哔哩","b","https://search.bilibili.com/all?keyword={query}"); // 不翻译：用户数据
    static SearchEngine[] DefaultSearchEngines()=>[
        new("必应", "bing", "https://www.bing.com/search?q={query}"), // 不翻译：用户数据
        new("Google", "g", "https://www.google.com/search?q={query}"),
        new("百度", "bd", "https://www.baidu.com/s?wd={query}"), // 不翻译：用户数据
        new("GitHub", "gh", "https://github.com/search?q={query}"),
        new("哔哩哔哩", "b", "https://search.bilibili.com/all?keyword={query}")]; // 不翻译：用户数据
    string defaultEngine="bing";
    public string DefaultEngine { get=>defaultEngine; init=>defaultEngine=value??"bing"; }
    string[] pinnedApps=[];
    public string[] PinnedApps { get=>pinnedApps; init=>pinnedApps=value??[]; }
    // 启动器的动作行（设计加，2026-10-29）：顺序就是显示顺序，不在里面的不显示。
    // Id：ai:translate、ai:ask、files、ai:<预设动作 Id>、web:<搜索站点的关键字>；认不出的 Id 界面跳过
    string[] actionRow=DefaultActionRow();
    public string[] ActionRow { get=>actionRow; init=>actionRow=value??DefaultActionRow(); }
    internal static string[] DefaultActionRow()=>["ai:translate","ai:ask","files","ai:summary","web:bing","web:bd"];
    // 关键字直达（设计加，2026-11-03，052，issue 11 / 12）：启动器里「关键字 空格 内容」直接翻译、问 AI、进文件窗口。
    // 关键字不含空格、不分大小写；空数组 = 不用；和搜索站点的关键字重复时站点优先
    string[] translateKeywords=DefaultTranslateKeywords();
    public string[] TranslateKeywords { get=>translateKeywords; init=>translateKeywords=value??DefaultTranslateKeywords(); }
    internal static string[] DefaultTranslateKeywords()=>["fy","翻译"]; // 不翻译：默认关键字，用户数据
    string[] askKeywords=["ai"];
    public string[] AskKeywords { get=>askKeywords; init=>askKeywords=value??["ai"]; }
    string[] filesKeywords=["f"];
    public string[] FilesKeywords { get=>filesKeywords; init=>filesKeywords=value??["f"]; }
    // 便携软件（设计加，2026-11-04，057，issue 19，见 docs/design.md「便携软件」）：文件夹（扫描本身和下一层）、单独添加的程序、
    // 用 Pane 打开过而记住的程序（最多 100 个）。三种都进「应用」组。路径不分大小写去重，顺序就是设置页列表的顺序
    PortableEntry[] portable=[];
    public PortableEntry[] Portable { get=>portable; init=>portable=value??[]; }
    // 在 Pane 的文件结果里打开不在应用列表里的 .exe 时记住它；关掉后不再记新的，已记住的保留
    public bool RememberOpenedPrograms { get; init; } = true;
}
public enum PortableKind { Folder, Program, Learned }
/// <param name="AddedAt">加入时间；记住的程序满 100 个时，按启动器使用记录去掉最久没用的，没用过的按这个时间。</param>
public sealed record PortableEntry(string Path, PortableKind Kind, DateTimeOffset AddedAt);
public sealed record LauncherPosition(string MonitorId, double X, double Y);
// 本机专属位置；083 导出时跳过，不能随配置迁移到其他电脑。
public sealed record StorageSettings(string? LargeFilesDirectory=null);
public sealed record ClipboardSettings(Hotkey Hotkey,
    bool RecordText=true,bool RecordImages=true,bool RecordFiles=true,int Limit=500,
    bool Dedupe=true,bool MoveToTopAfterPaste=true,bool HonorExcludeMarker=true,
    // 设计加（2026-10-31，033）：整个剪贴板功能的开关。关掉 = 不监听、不记录、不注册呼出热键、面板打不开、Win+V 替换先恢复；已有记录保留
    bool Enabled=true,long ImageLimitMegabytes=0,bool KeepHotkeyWithWinV=true)
{
    string[] excludedPrograms=[];
    public string[] ExcludedPrograms {get=>excludedPrograms;init=>excludedPrograms=value??[];}
}
public sealed record GlassCommand(ThemeMode Theme, GlassThemeParameters Parameters);

public enum IconMode { Auto, ForceMask, Raw }
public sealed record IconSettings
{
    public bool MaskByDefault { get; init; }=true;
    // 文件和文件夹用系统图标（设计加，2026-10-31，036）：false = Pane 的彩色类型图标（默认），true = 跟资源管理器一样
    public bool SystemFileIcons { get; init; }
    Dictionary<string,IconMode> overrides=[];
    public Dictionary<string,IconMode> Overrides { get=>overrides; init=>overrides=value??[]; }
    Dictionary<string,string> customIcons=[];
    public Dictionary<string,string> CustomIcons { get=>customIcons; init=>customIcons=value??[]; }
}
