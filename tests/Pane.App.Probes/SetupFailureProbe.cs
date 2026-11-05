using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace Pane.Diagnostics;

// 仅供 061 隔离测试同步解压后的文件锁；所有入口都要求独立根和隐藏桌面。
internal static class SetupFailureProbe
{
    static void RequireHidden()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("Pane027-"))
            throw new InvalidOperationException("Failure probe requires hidden desktop");
    }
    public static void Extracted(string? root, string temporary)
    {
        if (root == null || !File.Exists(Path.Combine(root, "scan.test"))) return;
        RequireHidden();
        File.WriteAllText(Path.Combine(root, "extracted.txt"), temporary);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "scan-ready.test")))
        { if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Scan fixture not ready"); Thread.Sleep(20); }
    }
    public static void Worker(string? root)
    {
        if (root == null || !File.Exists(Path.Combine(root, "crash-once.test"))) return;
        RequireHidden();
        File.Delete(Path.Combine(root, "crash-once.test"));
        // 确定性模拟早退，仅验证重试策略；不把这一结果算作真实 XAML 崩溃复现。
        Environment.Exit(unchecked((int)0xC000027B));
    }
    public static void Completed(string root)
    {
        if (!File.Exists(Path.Combine(root, "cleanup-hold.test"))) return;
        RequireHidden();
        File.WriteAllText(Path.Combine(root, "worker-completed.txt"), AppContext.BaseDirectory);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "worker-release.test")))
        { if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Cleanup fixture not ready"); Thread.Sleep(20); }
    }
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(nint handle, int kind, StringBuilder value, int length, out int needed);
}
