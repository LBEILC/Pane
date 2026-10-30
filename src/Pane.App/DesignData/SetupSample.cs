// 安装 / 卸载界面的假服务（设计负责人，截图和看效果用）。不碰任何文件、注册表和快捷方式。
// 由 `Pane.App.exe --setup-preview <状态>` 使用；状态：fresh 新装、update 升级（正在运行）、working 停在一半、
// done 很快装完、failed 装到一半失败；卸载：u-ask、u-working、u-done、u-failed。
// 环境变量 PANE_DESIGN_SETUP_RUN=1 时界面一出来就自动点主按钮（截安装中 / 装好了 / 失败用）。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class SetupSample(string state) : ISetupService, IUninstallService
{
    const string Default = @"C:\Users\你\AppData\Local\Programs\Pane";
    bool Update => state == "update";

    public SetupInfo Info => new("0.1.112", Update ? "0.1.110" : null, Update, Default, 81_901_540, true);

    UninstallInfo IUninstallService.Info => new("0.1.112", true, @"C:\Users\你\AppData\Roaming\Pane", 37_748_736);

    public InstallPathCheck CheckInstallPath(string folder)
    {
        var path = string.Equals(Path.GetFileName(folder.TrimEnd('\\')), "Pane", StringComparison.OrdinalIgnoreCase) ? folder : Path.Combine(folder, "Pane");
        return path.Contains("Program Files", StringComparison.OrdinalIgnoreCase) ? new(path, "这里要管理员权限，换一个位置") : new(path, null);
    }

    public Task InstallAsync(SetupOptions options, IProgress<SetupProgress> progress) =>
        Steps(progress, ["正在解压", Update ? "正在关闭正在运行的 Pane" : "正在复制文件", "正在复制文件", "正在建快捷方式", "正在登记到「已安装的应用」"],
            state == "failed" ? "Pane 正在运行，没能让它退出。请先从托盘图标退出 Pane 再试" : null);

    public Task UninstallAsync(bool removeData, IProgress<SetupProgress> progress) =>
        Steps(progress, ["正在关闭正在运行的 Pane", "正在删开机启动项和快捷方式", "正在删程序文件", removeData ? "正在删数据" : "正在收尾"],
            state == "u-failed" ? @"有文件正被别的程序占用：C:\Users\你\AppData\Local\Programs\Pane\app\Pane.App.exe。关掉占用它的程序后再试一次" : null);

    // 按步骤推进；working / u-working 停在一半不动（截「进行中」）；带 error 的走到一半抛出
    async Task Steps(IProgress<SetupProgress> progress, string[] steps, string? error)
    {
        bool hold = state is "working" or "u-working";
        int fast = state is "done" or "u-done" ? 120 : 600;
        for (int i = 0; i < steps.Length; i++)
        {
            progress.Report(new((i + 0.5) / steps.Length, steps[i]));
            if (hold && i == steps.Length / 2) await Task.Delay(Timeout.Infinite);
            if (error != null && i == 2) throw new SetupException(error);
            await Task.Delay(fast);
        }
        progress.Report(new(1, "完成"));
    }

    public void LaunchInstalled() { }
}
