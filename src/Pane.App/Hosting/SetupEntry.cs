using Pane.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pane.Services.Setup;
namespace Pane.Hosting;

internal static class SetupEntry
{
    public static bool Active { get; private set; }
    public static bool AutoUpdate { get; private set; }
    public static bool Uninstall { get; private set; }
    public static SetupPaths Paths { get; private set; } = null!;
    public static string? IsolatedRoot { get; private set; }
    /// <summary>设计预览（--setup-preview / --uninstall-preview &lt;状态&gt;）：界面接假服务，什么都不装。</summary>
    public static string? Preview { get; private set; }
    public static bool TryRun(string[] args, Action runApplication)
    {
        if (args is ["--restore-winv", var dataDirectory])
        {
            try
            {
                var service = new Pane.Services.WinV.WinVReplacement(Path.GetFullPath(dataDirectory),
                    new Pane.Services.WinV.WindowsWinVRegistry(),
                    new Pane.Services.WinV.ExplorerRestart(new Pane.Services.WinV.WindowsExplorerPlatform()), new Pane.Services.WinV.NoWinVHotkey());
                service.RestoreForUninstallAsync().GetAwaiter().GetResult();
            }
            catch { Environment.ExitCode = 1; }
            return true;
        }
        if (args is ["--setup-launch-probe", var probeRoot])
        {
            // 只验证隔离安装的新进程入口，不启动热键、钩子、剪贴板及窗口。
            var probePaths = SetupPaths.Isolated(probeRoot).ResolveInstalled(probeRoot);
            if (!SetupFiles.Full(Environment.ProcessPath!).Equals(SetupFiles.Full(probePaths.Exe), StringComparison.OrdinalIgnoreCase))
                throw new IOException("自测启动位置无效"); // 不翻译：诊断异常
            File.WriteAllText(Path.Combine(probeRoot, "current-directory.txt"), Environment.CurrentDirectory);
            File.WriteAllText(Path.Combine(probeRoot, "launched.txt"), SetupFiles.VersionOf(Environment.ProcessPath!) ?? "unknown");
            File.AppendAllText(Path.Combine(probeRoot, "update-timeline.log"), $"{DateTimeOffset.UtcNow:O} 新版进程启动并退出 {SetupFiles.VersionOf(Environment.ProcessPath!)}\n"); // 不翻译：诊断日志
            return true;
        }
        if (args is ["--setup-preview" or "--uninstall-preview", var state])
        {
            Preview = state; Uninstall = args[0] == "--uninstall-preview"; Paths = SetupPaths.Production; Active = true;
            runApplication();
            return true;
        }
        if (args.FirstOrDefault() is not ("--setup" or "--uninstall" or "--uninstall-worker")) return false;
        try
        {
            AutoUpdate = args[0] == "--setup" && args.Contains("--auto-update");
            args = args.Where(a => a != "--auto-update").ToArray();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--package" or "--temp-dir" or "--self-test-root")) throw new ArgumentException("安装参数无效"); // 不翻译：异常
                options.Add(args[i], args[i + 1]);
            }
            IsolatedRoot = options.GetValueOrDefault("--self-test-root");
            Paths = IsolatedRoot == null ? SetupPaths.Production : SetupPaths.Isolated(IsolatedRoot);
            IsolatedRoot = IsolatedRoot == null ? null : Paths.Root;
            Paths = Paths.ResolveInstalled(IsolatedRoot);
            Uninstall = args[0] != "--setup";
            if (args[0] == "--uninstall")
            {
                // 先启动独立小助手，再退出原进程；助手等本进程退出后才复制并运行临时卸载器。
                string helper = Path.Combine(Path.GetTempPath(), "Pane-Cleanup-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Pane.Maintenance.exe"), helper);
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string value in new[] { "--relocate", Paths.App, Environment.ProcessId.ToString() }) start.ArgumentList.Add(value);
                if (IsolatedRoot != null) { start.ArgumentList.Add("--self-test-root"); start.ArgumentList.Add(IsolatedRoot); }
                using var process = Process.Start(start) ?? throw new IOException(Strings.Setup_UninstallerLaunchFailed);
                if(IsolatedRoot != null) File.WriteAllLines(Path.Combine(IsolatedRoot,"helper.txt"), [helper, process.Id.ToString()]);
                return true;
            }
            if (args[0] == "--uninstall-worker" && SetupFiles.Full(AppContext.BaseDirectory).Equals(SetupFiles.Full(Paths.App), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("卸载程序必须先移到临时目录"); // 不翻译：异常
            Active = true;
            if (IsolatedRoot != null)
            {
                // 只在验证过的临时根目录中使用假注册表、假快捷方式并跳过窗口。
                Pane.Diagnostics.SetupProbe.Run(Paths, Uninstall);
            }
            else runApplication();
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            if (IsolatedRoot != null) { if (Paths != null) { Directory.CreateDirectory(Paths.Root); File.WriteAllText(Path.Combine(Paths.Root, "setup-error.txt"), error.ToString()); } }
            else MessageBoxW(0, error is Pane.Contracts.SetupException ? error.Message : Strings.Setup_UnexpectedFailure, "Pane", 0x10);
        }
        return true;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint window, string text, string caption, uint flags);
}
