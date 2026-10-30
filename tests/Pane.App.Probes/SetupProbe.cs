using Pane.Services.Setup;
using Pane.Settings;
namespace Pane.Diagnostics;

internal static class SetupProbe
{
    public static void Run(SetupPaths paths, bool uninstall)
    {
        // 自测假数据；不依赖 XAML，也不读取真实用户设置。
        var glass = new GlassThemeParameters(.2, .2, 1, 1, 1, 0);
        var defaults = new PaneSettings(1, new(false, true, ThemeMode.System, "#FF5874C9", 20, 1, glass, glass),
            new(new(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, 76)),
            new(new(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, 67)));
        string isolated = Pane.Hosting.SetupEntry.IsolatedRoot!;
        var integration = new SetupIntegration(paths, defaults, isolated);
        var service = new SetupService(paths, AppContext.BaseDirectory, SetupFiles.VersionOf(Environment.ProcessPath!) ?? "0.1.0", integration, new SetupProcesses(paths, true), autoUpdate: Pane.Hosting.SetupEntry.AutoUpdate);
        File.AppendAllText(Path.Combine(isolated, "update-timeline.log"), $"{DateTimeOffset.UtcNow:O} 安装入口 autoUpdate={service.AutoUpdate}; installed={service.Info.InstalledVersion}\n");
        var progress = new Progress<Pane.Contracts.SetupProgress>();
        if (uninstall) service.UninstallAsync(false, progress).GetAwaiter().GetResult();
        else service.InstallAsync(new(false, true, service.Info.InstalledVersion == null
            ? service.CheckInstallPath(Path.Combine(isolated, "custom tools")).Path : service.Info.InstallPath), progress).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(isolated, uninstall ? "uninstalled.txt" : "installed.txt"), AppContext.BaseDirectory);
        if (!uninstall && service.AutoUpdate) service.LaunchInstalled();
    }
}
