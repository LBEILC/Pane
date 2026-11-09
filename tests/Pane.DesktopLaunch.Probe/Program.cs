using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// 无窗口、无全局服务；只把父进程和参数写入测试目录，随后立即退出。
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        if (NtQueryInformationProcess(GetCurrentProcess(), 0, out var basic, Marshal.SizeOf<BasicInformation>(), out _) != 0) return 3;
        using var parent = Process.GetProcessById((int)basic.Parent);
        string[] lines = [Environment.ProcessId.ToString(), basic.Parent.ToString(), parent.ProcessName,
            $"console={GetConsoleWindow() != 0}", .. args.Skip(1).Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)))];
        File.WriteAllLines(args[0] + ".tmp", lines);
        File.Move(args[0] + ".tmp", args[0], true);
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicInformation { public nint Reserved, Peb, Reserved2, Reserved3, Pid, Parent; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int kind, out BasicInformation information, int size, out int returned);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern nint GetConsoleWindow();
}
