using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Pane.Settings;
using Pane.Services.Launcher;
namespace Pane.Services.Icons;

internal static class IconTheme
{
    public static bool IsDark(ThemeMode mode){
        if(mode!=ThemeMode.System)return mode==ThemeMode.Dark;
        // 只读系统应用主题；不写注册表。
        using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light&&light==0;
    }
}
internal sealed record IconMeasurement(string Id,string Name,string? Original,string Kind,string? Dark,string? Light,bool Cached,double Milliseconds,string? Error);
internal static class IconProcessing
{
    public const string Version="2";
    public static string SettingsKey(IconSettings settings)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(settings,SettingsJson.Default.IconSettings))));
    public static IconMode Mode(IconSettings settings,string id)=>!settings.MaskByDefault?IconMode.Raw:settings.Overrides.GetValueOrDefault(id);
    public static string Key(string original,IconMode mode,bool dark,string version=Version){
        // 013 的无元数据虚拟项仍每次提取；内容没变时复用遮罩，不因临时文件名重算。
        string fingerprint;
        if(string.Equals(Path.GetFileName(Path.GetDirectoryName(original)),"launcher-icons",StringComparison.OrdinalIgnoreCase)){
            using var stream=File.OpenRead(original);
            fingerprint=Path.GetFileNameWithoutExtension(original).Split('-')[0]+":"+Convert.ToHexString(SHA256.HashData(stream));
        }else fingerprint=IconCache.Key("custom",original);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"mask:{version}:{mode}:{dark}:{fingerprint}")));
    }
    public static PersistedIndex Apply(PersistedIndex index,string directory,IconSettings settings,CancellationToken token,Action<IconMeasurement>? measured=null){
        var cache=Path.Combine(directory,"masked-icons");Directory.CreateDirectory(cache);var rows=new List<IndexedApp>();var measurements=new List<IconMeasurement>();
        foreach(var app in index.Snapshot.Apps){
            token.ThrowIfCancellationRequested();var watch=Stopwatch.StartNew();string? source=app.Icon,dark=null,light=null,error=null;string kind="Unavailable";bool hit=false;
            try {
                // TODO(design): 设置页确定自定义图片的格式说明及读取失败提示；当前使用 WIC 位图解码，缺失或无法解码时回退系统原图。
                if(settings.CustomIcons.TryGetValue(app.Id,out var custom)&&File.Exists(custom))source=Path.GetFullPath(custom);
                if(source!=null){
                    var mode=Mode(settings,app.Id);dark=Path.Combine(cache,Key(source,mode,true)+".png");light=Path.Combine(cache,Key(source,mode,false)+".png");
                    string meta=dark+".kind";hit=File.Exists(dark)&&File.Exists(light)&&File.Exists(meta);
                    if(hit)kind=File.ReadAllText(meta);
                    else {var a=IconRenderer.Load(source);var verdict=IconRenderer.Decide(a,mode);kind=a.Empty?"Empty":verdict.ToString();foreach(var pair in new[]{(dark,true),(light,false)}){token.ThrowIfCancellationRequested();IconRenderer.Save(pair.Item1+".tmp",IconRenderer.Render(a,verdict,pair.Item2));File.Move(pair.Item1+".tmp",pair.Item1,true);}File.WriteAllText(meta,kind);}
                }
            }catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){error=e.GetType().Name;dark=null;light=null;}
            rows.Add(app with{DarkIcon=dark,LightIcon=light});var m=new IconMeasurement(app.Id,app.Name.Name,source,kind,dark,light,hit,watch.Elapsed.TotalMilliseconds,error);measurements.Add(m);measured?.Invoke(m);
        }
        // 测量清单只在子进程写入；主进程不反序列化像素和测量数据。
        File.WriteAllText(Path.Combine(directory,"icon-measurements.json"),System.Text.Json.JsonSerializer.Serialize(measurements,IconMetricsJson.Default.ListIconMeasurement));
        return index with{Snapshot=index.Snapshot with{Apps=rows.ToArray(),IconSettingsKey=SettingsKey(settings)}};
    }
}
[System.Text.Json.Serialization.JsonSerializable(typeof(List<IconMeasurement>))]
internal partial class IconMetricsJson:System.Text.Json.Serialization.JsonSerializerContext { }
