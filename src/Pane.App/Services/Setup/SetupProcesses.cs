using System.Diagnostics;
using Pane.Contracts;
namespace Pane.Services.Setup;

internal sealed class SetupProcesses(SetupPaths paths, bool isolated) : ISetupProcesses
{
    public void SetPaths(SetupPaths value) => paths = value;
    public bool Running
    {
        get
        {
            var processes = Process.GetProcessesByName("Pane.App");
            try { return processes.Any(p => { try { return p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, paths.Exe, StringComparison.OrdinalIgnoreCase); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; } }); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public void Stop()
    {
        if (!Running) return;
        using var process = Start(paths.Exe, ["--exit"]);
        // 发送者返回时原进程可能还在清理服务；等实际退出再换目录。
        var watch = Stopwatch.StartNew();
        if (!process.WaitForExit(12000))
            throw new SetupException("Pane 正在运行，没能让它退出，请先从托盘退出再试");
        while (Running && watch.ElapsedMilliseconds < 15000) Thread.Sleep(50);
        if (Running)
            throw new SetupException("Pane 正在运行，没能让它退出，请先从托盘退出再试");
    }
    public void Launch(bool background = false)
    {
        // 安装自测只启动验证入口，避免实际注册全局服务。
        using var process = Start(paths.Exe, isolated ? ["--setup-launch-probe", IsolatedRoot()] : background ? ["--autostart"] : []);
        if (isolated && (!process.WaitForExit(15000) || process.ExitCode != 0)) throw new IOException("隔离新版启动失败");
    }
    string IsolatedRoot()
    {
        // 隔离数据目录始终是测试根目录的 data，安装位置可以是其子目录。
        return SetupPaths.Isolated(Path.GetDirectoryName(paths.Data)!).Root;
    }
    Process Start(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (string name in new[] { "PANE_DATA_DIR", "PANE_INSTANCE", "PANE_RESULT", "PANE_DEMO", "PANE_OBSERVE", "PANE_VERIFY", "PANE_DEV_HOTKEYS", "PANE_BIN_DIR" }) info.Environment.Remove(name);
        if (isolated)
        {
            info.Environment["PANE_DATA_DIR"] = paths.Data; info.Environment["PANE_INSTANCE"] = paths.Root;
            info.Environment["PANE_OBSERVE"] = "1"; info.Environment["PANE_DEV_HOTKEYS"] = "launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C";
        }
        return Process.Start(info) ?? throw new IOException("无法启动 Pane");
    }
}
