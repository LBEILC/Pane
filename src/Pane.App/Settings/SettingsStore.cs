using Pane.Localization;
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
        if(!File.Exists(path)) { Save(Current with{Update=Current.Update with{SeenVersion=CurrentVersion()}}); return; }
        try {
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var value=document.RootElement.Deserialize(SettingsJson.Default.PaneSettings)??throw new JsonException();
            if(!document.RootElement.TryGetProperty("Update",out var update)||!update.TryGetProperty("SeenVersion",out _))
                value=value with{Update=value.Update with{SeenVersion="0"}};
            // 源生成反序列化会为缺失的 init 布尔属性传入 false，显式保留旧文件默认开启。
            if(document.RootElement.TryGetProperty("Launcher",out var launcher)&&!launcher.TryGetProperty("RememberOpenedPrograms",out _))
                value=value with{Launcher=value.Launcher with{RememberOpenedPrograms=true}};
            value=Normalize(value);Validate(value);Current=value;
        }
        catch(Exception e) when(e is JsonException or ArgumentException or NotSupportedException) {
            var backup=path+".bad-"+Guid.NewGuid().ToString("N"); File.Move(path,backup); Current=defaults; CorruptFile?.Invoke(this,backup);
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    readonly object writeLock=new();
    internal static string CurrentVersion()=>typeof(SettingsStore).Assembly.GetName().Version is {} v?$"{v.Major}.{v.Minor}.{Math.Max(0,v.Build)}":"0.1.0";
    public void Save(PaneSettings value)
    {
        lock(writeLock)SaveCore(value);
        Changed?.Invoke(this,EventArgs.Empty);
    }
    internal void Update(Func<PaneSettings,PaneSettings> change)
    {
        lock(writeLock){var value=change(Current);if(ReferenceEquals(value,Current))return;SaveCore(value);}
        Changed?.Invoke(this,EventArgs.Empty);
    }
    void SaveCore(PaneSettings value)
    {
        value=Normalize(value); Validate(value); Directory.CreateDirectory(DirectoryPath);
        var path=Path.Combine(DirectoryPath,"settings.json"); var temp=path+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {
            JsonSerializer.Serialize(stream,value,SettingsJson.Default.PaneSettings); stream.Flush(true);
        }
        File.Move(temp,path,true); Current=value;
    }
    static PaneSettings Normalize(PaneSettings value) => value with {
        Ai=value.Ai with {Disabled=value.Modules is null?value.Ai.Disabled:!value.Modules.GetValueOrDefault("ai",true)},
        Launcher=value.Launcher with {Portable=value.Launcher.Portable.DistinctBy(e=>e.Path,StringComparer.OrdinalIgnoreCase).ToArray()},
        Clipboard=value.Clipboard with {Enabled=value.Modules?.GetValueOrDefault("clipboard",true)??value.Clipboard.Enabled},
        SuperPanel=value.SuperPanel with {Enabled=value.Modules?.GetValueOrDefault("super-panel",true)??value.SuperPanel.Enabled},
        FolderJump=NormalizeFolderJump(value.FolderJump) with {Enabled=value.Modules?.GetValueOrDefault("folder-jump",true)??value.FolderJump.Enabled} };
    internal static FolderJumpSettings NormalizeFolderJump(FolderJumpSettings value)
    {
        const KeyModifiers mask=KeyModifiers.Ctrl|KeyModifiers.Alt|KeyModifiers.Shift|KeyModifiers.Win;
        static bool ValidKey(Hotkey? key) => key is null || ((key.Modifiers & mask)!=0 && (key.Modifiers & ~mask)==0 && key.VirtualKey is >0 and <=255);
        return value with { JumpHotkey=ValidKey(value.JumpHotkey)?value.JumpHotkey:null, MenuHotkey=ValidKey(value.MenuHotkey)?value.MenuHotkey:null };
    }
    internal static void Validate(PaneSettings s)
    {
        if(s.Rename.Presets.Length>30||!ValidRules(s.Rename.LastRules)||s.Rename.Presets.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Name)||!ValidRules(p.Rules)))throw new ArgumentException(Strings.Settings_Rename_Invalid);
        if(s.Ai==null)throw new ArgumentException(Strings.Settings_Ai_Invalid);
        s.Ai.Validate();
        if(s.Version!=1||s.General==null||s.Launcher?.Hotkey==null||s.Clipboard?.Hotkey==null) throw new ArgumentException(Strings.Settings_InvalidVersion);
        if(s.SuperPanel is null || !Enum.IsDefined(s.SuperPanel.TriggerButton) || s.SuperPanel.HoldMilliseconds<=0 || s.SuperPanel.DisabledPrograms is null) throw new ArgumentException(Strings.Settings_Super_Invalid);
        if(s.Icons.Overrides.Any(p=>string.IsNullOrWhiteSpace(p.Key)||!Enum.IsDefined(p.Value))||s.Icons.CustomIcons.Any(p=>string.IsNullOrWhiteSpace(p.Key)||string.IsNullOrWhiteSpace(p.Value)))throw new ArgumentException(Strings.Settings_Icons_Invalid); var g=s.General;
        var c=s.Clipboard;
        if((!c.RecordText&&!c.RecordImages&&!c.RecordFiles)||c.Limit is not (200 or 500 or 1000 or 5000)||c.ExcludedPrograms.Any(p=>string.IsNullOrWhiteSpace(p)||p.IndexOfAny(['/', '\\', '\r', '\n'])>=0))throw new ArgumentException(Strings.Settings_Clipboard_Invalid);
        var l=s.Launcher;
        if(!Enum.IsDefined(l.Width)||l.AppRows is <1 or >4||l.FileRows is not (3 or 5 or 8 or 10))throw new ArgumentException(Strings.Settings_Launcher_Invalid);
        if(s.Update.NewsShown<0)throw new ArgumentException(Strings.Settings_InvalidNumber);
        if(l.ActionRow==null||l.ActionRow.Any(string.IsNullOrWhiteSpace)||!Enum.IsDefined(l.KeepInput)||l.AutoAttachSeconds is not (0 or 1 or 3 or 5)||l.FileResultCount is not (5 or 8 or 12)||l.Favorites==null||l.SearchEngines==null||l.PinnedApps==null||l.SearchEngines.Length==0)throw new ArgumentException(Strings.Settings_Launcher_Invalid);
        if(l.Favorites.Any(f=>f==null||string.IsNullOrWhiteSpace(f.Path))||l.PinnedApps.Any(string.IsNullOrWhiteSpace))throw new ArgumentException(Strings.Settings_LauncherEntry_Invalid);
        if(l.SearchEngines.Any(e=>e==null||string.IsNullOrWhiteSpace(e.Name)||string.IsNullOrWhiteSpace(e.Keyword)||e.Keyword.Any(char.IsWhiteSpace)||e.UrlTemplate==null||!e.UrlTemplate.Contains("{query}",StringComparison.Ordinal)||!Uri.TryCreate(e.UrlTemplate.Replace("{query}","test"),UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))||l.SearchEngines.Select(e=>e.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=l.SearchEngines.Length||!l.SearchEngines.Any(e=>string.Equals(e.Keyword,l.DefaultEngine,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException(Strings.Settings_SearchEngine_Invalid);
        if(s.Launcher.Position is {} p && (string.IsNullOrWhiteSpace(p.MonitorId)||!double.IsFinite(p.X)||!double.IsFinite(p.Y))) throw new ArgumentException(Strings.Settings_LauncherPosition_Invalid);
        if(!Enum.IsDefined(g.Theme)||!double.IsFinite(g.PanelRadius)||g.PanelRadius<0||g.PanelRadius>100||!double.IsFinite(g.MotionSpeed)||g.MotionSpeed<=0||g.MotionSpeed>20) throw new ArgumentException(Strings.Settings_InvalidNumber);
        if(g.AccentColor==null||g.AccentColor.Length!=9||g.AccentColor[0]!='#'||!uint.TryParse(g.AccentColor.AsSpan(1),System.Globalization.NumberStyles.HexNumber,null,out _)) throw new ArgumentException(Strings.Settings_InvalidAccent);
        foreach(var v in new[]{g.GlassLight,g.GlassDark}) if(v==null||!Valid(v.TintOpacity,1)||!Valid(v.LuminosityOpacity,1)||!Valid(v.Saturation,2)||!Valid(v.Highlight,1)||!Valid(v.Shadow,1)||!Valid(v.ExtraBlur,250)) throw new ArgumentException(Strings.Settings_InvalidGlass);
    }
    static bool ValidRules(Pane.Contracts.RenameRule[]? rules)=>rules!=null&&rules.Length<=20&&rules.All(r=>r!=null&&Enum.IsDefined(r.Kind)&&Enum.IsDefined(r.Position)&&Enum.IsDefined(r.Case)&&Enum.IsDefined(r.DateSource)&&r.Find!=null&&r.Text!=null&&r.Separator!=null&&r.DateFormat!=null);
    static bool Valid(double v,double max)=>double.IsFinite(v)&&v>=0&&v<=max;
}
