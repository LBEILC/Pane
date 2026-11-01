using System.Text.Json;
using System.Text.Json.Serialization;
namespace Pane.Settings;
[JsonSerializable(typeof(PaneSettings))]
[JsonSerializable(typeof(GlassCommand))]
[JsonSerializable(typeof(RenameSettings))]
[JsonSourceGenerationOptions(WriteIndented=true, UseStringEnumConverter=true)]
internal partial class SettingsJson : JsonSerializerContext { }
public sealed class SettingsStore
{
    public static string ProductionDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pane");
    public static string DevelopmentDirectory {
        get {
            var configured=Environment.GetEnvironmentVariable("PANE_DATA_DIR");if(!string.IsNullOrWhiteSpace(configured))return Path.GetFullPath(configured);
            for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory!=null;directory=directory.Parent)
                if(File.Exists(Path.Combine(directory.FullName,"Pane.App.csproj")))return Path.Combine(directory.FullName,"bin","data");
            return Path.Combine(AppContext.BaseDirectory,"data");
        }
    }
    public string DirectoryPath { get; }
    public PaneSettings Current { get; private set; }
    readonly PaneSettings defaults;
    public event EventHandler? Changed;
    public event EventHandler<string>? CorruptFile;
    public SettingsStore(string directory, PaneSettings defaults) { DirectoryPath=directory; this.defaults=defaults; Current=defaults; }
    public void Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path=Path.Combine(DirectoryPath,"settings.json");
        if(!File.Exists(path)) return;
        try { var value=JsonSerializer.Deserialize(File.ReadAllText(path),SettingsJson.Default.PaneSettings) ?? throw new JsonException(); value=Normalize(value); Validate(value); Current=value; }
        catch(Exception e) when(e is JsonException or ArgumentException or NotSupportedException) {
            var backup=path+".bad-"+Guid.NewGuid().ToString("N"); File.Move(path,backup); Current=defaults; CorruptFile?.Invoke(this,backup);
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public void Save(PaneSettings value)
    {
        value=Normalize(value); Validate(value); Directory.CreateDirectory(DirectoryPath);
        var path=Path.Combine(DirectoryPath,"settings.json"); var temp=path+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {
            JsonSerializer.Serialize(stream,value,SettingsJson.Default.PaneSettings); stream.Flush(true);
        }
        File.Move(temp,path,true); Current=value; Changed?.Invoke(this,EventArgs.Empty);
    }
    static PaneSettings Normalize(PaneSettings value) => value with { FolderJump=NormalizeFolderJump(value.FolderJump) };
    internal static FolderJumpSettings NormalizeFolderJump(FolderJumpSettings value)
    {
        const KeyModifiers mask=KeyModifiers.Ctrl|KeyModifiers.Alt|KeyModifiers.Shift|KeyModifiers.Win;
        static bool ValidKey(Hotkey? key) => key is null || ((key.Modifiers & mask)!=0 && (key.Modifiers & ~mask)==0 && key.VirtualKey is >0 and <=255);
        return value with { JumpHotkey=ValidKey(value.JumpHotkey)?value.JumpHotkey:null, MenuHotkey=ValidKey(value.MenuHotkey)?value.MenuHotkey:null };
    }
    static void Validate(PaneSettings s)
    {
        if(s.Rename.Presets.Length>30||!ValidRules(s.Rename.LastRules)||s.Rename.Presets.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Name)||!ValidRules(p.Rules)))throw new ArgumentException("重命名设置无效");
        if(s.Ai==null)throw new ArgumentException("AI 设置无效");
        s.Ai.Validate();
        if(s.Version!=1||s.General==null||s.Launcher?.Hotkey==null||s.Clipboard?.Hotkey==null) throw new ArgumentException("设置版本或结构无效");
        if(s.SuperPanel is null || !Enum.IsDefined(s.SuperPanel.TriggerButton) || s.SuperPanel.HoldMilliseconds<=0 || s.SuperPanel.DisabledPrograms is null) throw new ArgumentException("超级面板设置无效");
        if(s.Icons.Overrides.Any(p=>string.IsNullOrWhiteSpace(p.Key)||!Enum.IsDefined(p.Value))||s.Icons.CustomIcons.Any(p=>string.IsNullOrWhiteSpace(p.Key)||string.IsNullOrWhiteSpace(p.Value)))throw new ArgumentException("图标设置无效"); var g=s.General;
        var c=s.Clipboard;
        if((!c.RecordText&&!c.RecordImages&&!c.RecordFiles)||c.Limit is not (200 or 500 or 1000 or 5000)||c.ExcludedPrograms.Any(p=>string.IsNullOrWhiteSpace(p)||p.IndexOfAny(['/', '\\', '\r', '\n'])>=0))throw new ArgumentException("剪贴板设置无效");
        var l=s.Launcher;
        if(l.ActionRow==null||l.ActionRow.Any(string.IsNullOrWhiteSpace)||!Enum.IsDefined(l.KeepInput)||l.AutoAttachSeconds is not (0 or 1 or 3 or 5)||l.FileResultCount is not (5 or 8 or 12)||l.Favorites==null||l.SearchEngines==null||l.PinnedApps==null||l.SearchEngines.Length==0)throw new ArgumentException("启动器设置无效");
        if(l.Favorites.Any(f=>f==null||string.IsNullOrWhiteSpace(f.Path))||l.PinnedApps.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("启动器入口无效");
        if(l.SearchEngines.Any(e=>e==null||string.IsNullOrWhiteSpace(e.Name)||string.IsNullOrWhiteSpace(e.Keyword)||e.Keyword.Any(char.IsWhiteSpace)||e.UrlTemplate==null||!e.UrlTemplate.Contains("{query}",StringComparison.Ordinal)||!Uri.TryCreate(e.UrlTemplate.Replace("{query}","test"),UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))||l.SearchEngines.Select(e=>e.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=l.SearchEngines.Length||!l.SearchEngines.Any(e=>string.Equals(e.Keyword,l.DefaultEngine,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("搜索引擎设置无效");
        if(s.Launcher.Position is {} p && (string.IsNullOrWhiteSpace(p.MonitorId)||!double.IsFinite(p.X)||!double.IsFinite(p.Y))) throw new ArgumentException("启动器位置无效");
        if(!Enum.IsDefined(g.Theme)||!double.IsFinite(g.PanelRadius)||g.PanelRadius<0||g.PanelRadius>100||!double.IsFinite(g.MotionSpeed)||g.MotionSpeed<=0||g.MotionSpeed>20) throw new ArgumentException("设置数值无效");
        if(g.AccentColor==null||g.AccentColor.Length!=9||g.AccentColor[0]!='#'||!uint.TryParse(g.AccentColor.AsSpan(1),System.Globalization.NumberStyles.HexNumber,null,out _)) throw new ArgumentException("主题色无效");
        foreach(var v in new[]{g.GlassLight,g.GlassDark}) if(v==null||!Valid(v.TintOpacity,1)||!Valid(v.LuminosityOpacity,1)||!Valid(v.Saturation,2)||!Valid(v.Highlight,1)||!Valid(v.Shadow,1)||!Valid(v.ExtraBlur,250)) throw new ArgumentException("玻璃参数无效");
    }
    static bool ValidRules(Pane.Contracts.RenameRule[]? rules)=>rules!=null&&rules.Length<=20&&rules.All(r=>r!=null&&Enum.IsDefined(r.Kind)&&Enum.IsDefined(r.Position)&&Enum.IsDefined(r.Case)&&Enum.IsDefined(r.DateSource)&&r.Find!=null&&r.Text!=null&&r.Separator!=null&&r.DateFormat!=null);
    static bool Valid(double v,double max)=>double.IsFinite(v)&&v>=0&&v<=max;
}
