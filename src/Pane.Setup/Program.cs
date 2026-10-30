using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Pane.Services.Setup;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        string? temporary = null;
        string? isolated = null;
        bool relay = args.FirstOrDefault() == "--relocate";
        try
        {
            // 外壳等待期间不能持有待替换目录；卸载中转也走此入口。
            Environment.CurrentDirectory = Path.GetTempPath();
            bool autoUpdate = !relay && args.Contains("--auto-update");
            args = args.Where(a => a != "--auto-update").ToArray();
            int offset = relay ? 3 : 0;
            if (args.Length != offset)
            {
                if (args.Length != offset + 2 || args[offset] != "--self-test-root") throw new ArgumentException("安装参数无效");
                isolated = SetupPaths.Isolated(args[offset + 1]).Root;
            }
            if (relay)
            {
                if (args.Length < 3 || !int.TryParse(args[2], out int parent)) throw new ArgumentException("卸载参数无效");
                try { using var owner = Process.GetProcessById(parent); if (!owner.WaitForExit(15000)) throw new IOException("原卸载进程尚未退出"); } catch (ArgumentException) { }
                string expected = (isolated == null ? SetupPaths.Production : SetupPaths.Isolated(isolated)).ResolveInstalled(isolated).App;
                if (!SetupFiles.Full(args[1]).Equals(SetupFiles.Full(expected), StringComparison.OrdinalIgnoreCase)) throw new IOException("卸载目录无效");
            }
            temporary = Path.Combine(Path.GetTempPath(), "Pane-Setup-" + Guid.NewGuid().ToString("N"));
            if (relay) SetupFiles.Copy(args[1], temporary);
            else
            {
                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("安装包缺少程序文件");
                using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
                zip.ExtractToDirectory(temporary);
            }
            var start = new ProcessStartInfo(Path.Combine(temporary, "Pane.App.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporary };
            foreach (string value in new[] { relay ? "--uninstall-worker" : "--setup", "--package", Environment.ProcessPath!, "--temp-dir", temporary }) start.ArgumentList.Add(value);
            if (autoUpdate) start.ArgumentList.Add("--auto-update");
            if (isolated != null) { start.ArgumentList.Add("--self-test-root"); start.ArgumentList.Add(isolated); }
            using var process = Process.Start(start) ?? throw new IOException("无法启动安装程序");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception error)
        {
            if (isolated != null) { Directory.CreateDirectory(isolated); File.WriteAllText(Path.Combine(isolated, "wrapper-error.txt"), error.ToString()); }
            else MessageBoxW(0, (error.HResult & 0xffff) is 112 or 39 ? "磁盘空间不够，无法解压安装程序。" : "无法解压或启动安装程序，请检查磁盘空间、目录权限和安全软件的拦截记录。", "Pane 安装程序", 0x10);
            return 1;
        }
        finally
        {
            if (temporary != null)
            {
                try { SetupFiles.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (isolated == null) MessageBoxW(0, "临时文件未能清理，请在关闭占用它的程序后删除：" + temporary, "Pane 安装程序", 0x30);
                    else File.WriteAllText(Path.Combine(isolated, "cleanup-error.txt"), temporary);
                }
            }
            if (relay) { try { File.Delete(Environment.ProcessPath!); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Windows 锁住正在执行的助手；残留位置在工程报告中说明。 */ } }
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint window, string text, string caption, uint flags);
}

// 外壳不引用应用设置程序集，只需要正式数据路径常量。
namespace Pane.Settings { internal static class SettingsStore { public static string ProductionDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pane"); } }
