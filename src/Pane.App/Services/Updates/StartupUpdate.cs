using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Settings;

namespace Pane.Services.Updates;

internal sealed record CachedUpdate(UpdateRelease Release, string Hash);
internal static class StartupUpdate
{
    // 先写失败闩锁再交出进程：崩溃、安装失败或新版没启动都不会反复自动装。
    // 只有目标版本或更高版本启动，才说明更新已落地，可以清除闩锁。
    internal static bool TryInstall(UpdateService service, SettingsStore store)
    {
        var settings = store.Current.Update;
        if (settings.AutoInstallFailedVersion is {} failed)
        {
            try
            {
                if (SetupFiles.CompareVersions(service.CurrentVersion, failed) >= 0)
                {
                    store.Update(s => s with { Update = s.Update with { AutoInstallFailedVersion = null } });
                    settings = store.Current.Update;
                }
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { }
        }
        if (!settings.AutoUpdate || !settings.InstallOnStartup || service.State != UpdateState.Ready || service.Latest is not {} release) return false;
        if (settings.AutoInstallFailedVersion is {} blocked)
        {
            try { if (SetupFiles.CompareVersions(release.Version, blocked) == 0) return false; }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { }
        }
        store.Update(s => s with { Update = s.Update with { AutoInstallFailedVersion = release.Version } });
        service.InstallAndRestart();
        // 启动失败时继续正常启动，重新读取缓存后显示已下载提示。
        return service.State != UpdateState.Failed;
    }
}
