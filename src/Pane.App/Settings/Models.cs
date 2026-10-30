namespace Pane.Settings;
[Flags] public enum KeyModifiers { None=0, Ctrl=1, Alt=2, Shift=4, Win=8 }
public enum ThemeMode { System, Light, Dark }
public sealed record PaneSettings(int Version, GeneralSettings General, LauncherSettings Launcher, ClipboardSettings Clipboard) { AiSettings ai=new(); public AiSettings Ai { get=>ai; init=>ai=value??new(); } IconSettings icons=new(); public IconSettings Icons { get=>icons; init=>icons=value??new(); } SuperPanelSettings superPanel=new(); public SuperPanelSettings SuperPanel { get=>superPanel; init=>superPanel=value??new(); } UpdateSettings update=new(); public UpdateSettings Update { get=>update; init=>update=value??new(); } }
// 软件内更新（设计加，2026-10-30，023）：AutoUpdate 开着就自动检查并在后台下载，下载好后提示重启更新
public sealed record UpdateSettings(bool AutoUpdate=true);
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
public sealed record GeneralSettings(bool StartWithWindows, bool ShowTrayIcon, ThemeMode Theme, string AccentColor, double PanelRadius, double MotionSpeed, GlassThemeParameters GlassLight, GlassThemeParameters GlassDark) { /* 界面字体（设计加，2026-10-30）：本机字体的字族名；null = 默认（MiSans VF，没装时微软雅黑）。重启后生效 */ public string? UiFont { get; init; } }
public sealed record Hotkey(KeyModifiers Modifiers, ushort VirtualKey);
public enum KeepInputMode { Clear, OneMinute, Always }
public sealed record FavoriteEntry(string Path, bool IsFolder);
public sealed record SearchEngine(string Name, string Keyword, string UrlTemplate);
public sealed record LauncherSettings(Hotkey Hotkey, LauncherPosition? Position=null,
    bool HideOnBlur=true, KeepInputMode KeepInput=KeepInputMode.Clear,
    int AutoAttachSeconds=3, int FileResultCount=8)
{
    FavoriteEntry[] favorites=[];
    public FavoriteEntry[] Favorites { get=>favorites; init=>favorites=value??[]; }
    SearchEngine[] searchEngines=DefaultSearchEngines();
    public SearchEngine[] SearchEngines { get=>searchEngines; init=>searchEngines=value??DefaultSearchEngines(); }
    static SearchEngine[] DefaultSearchEngines()=>[
        new("必应", "bing", "https://www.bing.com/search?q={query}"),
        new("百度", "bd", "https://www.baidu.com/s?wd={query}"),
        new("GitHub", "gh", "https://github.com/search?q={query}"),
        new("哔哩哔哩", "b", "https://search.bilibili.com/all?keyword={query}")];
    string defaultEngine="bing";
    public string DefaultEngine { get=>defaultEngine; init=>defaultEngine=value??"bing"; }
    string[] pinnedApps=[];
    public string[] PinnedApps { get=>pinnedApps; init=>pinnedApps=value??[]; }
    // 启动器的动作行（设计加，2026-10-29）：顺序就是显示顺序，不在里面的不显示。
    // Id：ai:translate、ai:ask、files、ai:<预设动作 Id>、web:<搜索站点的关键字>；认不出的 Id 界面跳过
    string[] actionRow=DefaultActionRow();
    public string[] ActionRow { get=>actionRow; init=>actionRow=value??DefaultActionRow(); }
    internal static string[] DefaultActionRow()=>["ai:translate","ai:ask","files","ai:summary","web:bing","web:bd"];
}
public sealed record LauncherPosition(string MonitorId, double X, double Y);
public sealed record ClipboardSettings(Hotkey Hotkey,
    bool RecordText=true,bool RecordImages=true,bool RecordFiles=true,int Limit=500,
    bool Dedupe=true,bool MoveToTopAfterPaste=true,bool HonorExcludeMarker=true,
    // 设计加（2026-10-31，033）：整个剪贴板功能的开关。关掉 = 不监听、不记录、不注册呼出热键、面板打不开、Win+V 替换先恢复；已有记录保留
    bool Enabled=true)
{
    string[] excludedPrograms=[];
    public string[] ExcludedPrograms {get=>excludedPrograms;init=>excludedPrograms=value??[];}
}
public sealed record GlassCommand(ThemeMode Theme, GlassThemeParameters Parameters);

public enum IconMode { Auto, ForceMask, Raw }
public sealed record IconSettings
{
    public bool MaskByDefault { get; init; }=true;
    Dictionary<string,IconMode> overrides=[];
    public Dictionary<string,IconMode> Overrides { get=>overrides; init=>overrides=value??[]; }
    Dictionary<string,string> customIcons=[];
    public Dictionary<string,string> CustomIcons { get=>customIcons; init=>customIcons=value??[]; }
}
