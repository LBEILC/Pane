using Pane.Localization;
// 软件内更新的界面接线（设计负责人）：设置 → 关于里的「更新」组读 Updates；下载好时托盘提示一次（每个版本一次）。
// 真实服务由工程在 023 里赋给 Updates（在 StartUpdates 之前）；演示模式用 DesignData/UpdateSample。
using Pane.Contracts;
namespace Pane;
public sealed partial class App
{
    /// <summary>软件内更新服务；023 之前正式版为 null（关于页只显示版本）。</summary>
    public IUpdateService? Updates {get;set;}
    /// <summary>随安装包带着的历代更新说明（062）：关于页的更新日志、「更新后」提示条用。工程在启动时赋值（演示模式也用真实的打包数据）；为 null 时这些地方不显示。</summary>
    public IReleaseNotesCatalog? ReleaseHistory {get;set;}=new Pane.Services.Updates.ReleaseNotesCatalog(
        message=>new Pane.Services.SelectionLog(Pane.Hosting.RuntimeMode.Current.DataDirectory).Write(message));
    string? notifiedUpdate;
    internal static bool TryStartupUpdate()
    {
        var mode=Pane.Hosting.RuntimeMode.Current;
        // 隔离根和开发、演示宿主沿用原有行为，测试直接注入假安装回调。
        if(!mode.IsProduction||Environment.GetEnvironmentVariable("PANE_DEMO")=="1"||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PANE_DATA_DIR")))return false;
        string settingsPath=System.IO.Path.Combine(mode.DataDirectory,"settings.json");
        if(!System.IO.File.Exists(settingsPath))return false;
        Pane.Settings.SettingsStore store;
        try{
            var settings=System.Text.Json.JsonSerializer.Deserialize(System.IO.File.ReadAllText(settingsPath),Pane.Settings.SettingsJson.Default.PaneSettings);
            if(settings==null)return false;
            Pane.Settings.SettingsStore.Validate(settings);
            store=new(mode.DataDirectory,settings);store.Load();
        }catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NullReferenceException){
            new Pane.Services.SelectionLog(mode.DataDirectory).Write("startup-update skipped: "+e.GetType().Name);return false;
        }
        using var service=new Pane.Services.Updates.UpdateService(Pane.Services.Setup.SetupFiles.VersionOf(Environment.ProcessPath!)??"0.1.0",
            mode.DataDirectory,()=>false,path=>{
                var start=new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=System.IO.Path.GetDirectoryName(path)!};
                start.ArgumentList.Add("--auto-update");
                using var installer=System.Diagnostics.Process.Start(start)??throw new System.IO.IOException(Strings.Setup_InstallerLaunchFailed);
            });
        try{return Pane.Services.Updates.StartupUpdate.TryInstall(service,store);}
        catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException){
            new Pane.Services.SelectionLog(mode.DataDirectory).Write("startup-update skipped: "+e.GetType().Name);return false;
        }
    }
    void StartUpdates()
    {
        if(demo)Updates??=new Pane.DesignData.UpdateSample();
        if(Updates is not {} u)return;
        u.Changed+=(_,_)=>queue.TryEnqueue(()=>{
            if(closing||u.State!=UpdateState.Ready||u.Latest is not {} r||notifiedUpdate==r.Version)return;
            notifiedUpdate=r.Version;
            shell?.NotifyOnce(string.Format(Strings.Update_ReadyNotification, (object?)(r.Version)));
        });
    }
}
