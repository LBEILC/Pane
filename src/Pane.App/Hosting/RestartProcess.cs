using Pane.Localization;
using System.Diagnostics;
using System.Globalization;
namespace Pane.Hosting;

internal static class RestartProcess
{
    const string ParentVariable = "PANE_RESTART_PARENT";
    public static bool Requested { get; set; }
    public static void WaitForParent()
    {
        var value = Environment.GetEnvironmentVariable(ParentVariable);
        Environment.SetEnvironmentVariable(ParentVariable, null);
        if (value == null) return;
        var parts = value.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long ticks))
            throw new InvalidDataException("重启进程标识无效"); // 不翻译：异常
        Process parent;
        try { parent = Process.GetProcessById(pid); } catch (ArgumentException) { return; }
        using (parent) {
            try {
                if (parent.StartTime.ToUniversalTime().Ticks != ticks) return;
                if (!parent.WaitForExit(30000)) throw new TimeoutException("等待旧进程退出超时"); // 不翻译：异常
            } catch (InvalidOperationException) when (parent.HasExited) {
                // 查询启动时间前旧进程已退出，也可以继续获取单实例锁。
            }
        }
    }
    public static void Start()
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(arg);
        start.Environment[ParentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        using var child = Process.Start(start) ?? throw new IOException(Strings.Common_RestartFailed);
    }
}
