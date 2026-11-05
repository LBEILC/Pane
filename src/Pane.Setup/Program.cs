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
        string? readyEvent = null;
        string step = "arguments";
        string? launcherPid = null;
        FileStream? helperLease = null, temporaryLease = null;
        bool relay = args.FirstOrDefault() == "--relocate";
        try
        {
            // 外壳等待期间不能持有待替换目录；卸载中转也走此入口。
            Environment.CurrentDirectory = Path.GetTempPath();
            UninstallChain.Log("wrapper-entry", path: Environment.ProcessPath);
            int isolatedIndex = Array.IndexOf(args, "--self-test-root");
            if (isolatedIndex >= 0 && isolatedIndex + 1 < args.Length) isolated = SetupPaths.Isolated(args[isolatedIndex + 1]).Root;
            var values = args.ToList();
            int launcherIndex = values.IndexOf("--launcher-pid");
            if (launcherIndex >= 0)
            {
                if (launcherIndex + 1 >= values.Count || !int.TryParse(values[launcherIndex + 1], out int pid) || pid <= 0) throw new ArgumentException("Invalid launcher");
                launcherPid = values[launcherIndex + 1]; values.RemoveRange(launcherIndex, 2);
            }
            int eventIndex = values.IndexOf("--ready-event");
            if (eventIndex >= 0)
            {
                if (eventIndex + 1 >= values.Count) throw new ArgumentException("Invalid event");
                readyEvent = UninstallChain.ValidateEvent(values[eventIndex + 1]);
                values.RemoveRange(eventIndex, 2);
            }
            args = values.ToArray();
            bool autoUpdate = !relay && args.Contains("--auto-update");
            args = args.Where(a => a != "--auto-update").ToArray();
            int offset = relay ? 3 : 0;
            if (args.Length != offset)
            {
                if (args.Length != offset + 2 || args[offset] != "--self-test-root") throw new ArgumentException("安装参数无效");
                isolated = SetupPaths.Isolated(args[offset + 1]).Root;
            }
            if (!relay && string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "Uninstall Pane", StringComparison.OrdinalIgnoreCase))
            {
                var paths = (isolated == null ? SetupPaths.Production : SetupPaths.Isolated(isolated)).ResolveInstalled(isolated);
                UninstallChain.Launch(paths, Environment.ProcessPath!, isolated, (where, error) => { step = where; throw error; });
                return 0;
            }
            if (relay) helperLease = UninstallChain.Lease(Environment.ProcessPath!, isolated);
            step = "cleanup-stale";
            UninstallChain.Cleanup(isolated);
            if (relay)
            {
                if (args.Length < 3 || !int.TryParse(args[2], out int parent)) throw new ArgumentException("卸载参数无效");
                try { using var owner = Process.GetProcessById(parent); if (!owner.WaitForExit(15000)) throw new IOException("原卸载进程尚未退出"); } catch (ArgumentException) { }
                string expected = (isolated == null ? SetupPaths.Production : SetupPaths.Isolated(isolated)).ResolveInstalled(isolated).App;
                if (!SetupFiles.Full(args[1]).Equals(SetupFiles.Full(expected), StringComparison.OrdinalIgnoreCase)) throw new IOException("卸载目录无效");
            }
            temporary = Path.Combine(Path.GetTempPath(), "Pane-Setup-" + Guid.NewGuid().ToString("N"));
            temporaryLease = UninstallChain.Lease(temporary, isolated);
            step = relay ? "copy-app" : "extract-package";
            UninstallChain.Log(step, "begin", path: temporary);
            if (relay) SetupFiles.Copy(args[1], temporary);
            else
            {
                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("安装包缺少程序文件");
                using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
                zip.ExtractToDirectory(temporary);
            }
            UninstallChain.Log(step, path: temporary);
            Pane.Diagnostics.SetupFailureProbe.Extracted(isolated, temporary);
            UninstallChain.WaitReadable(temporary);
            var start = new ProcessStartInfo(Path.Combine(temporary, "Pane.App.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporary };
            foreach (string value in new[] { relay ? "--uninstall-worker" : "--setup", "--package", Environment.ProcessPath!, "--temp-dir", temporary }) start.ArgumentList.Add(value);
            if (autoUpdate) start.ArgumentList.Add("--auto-update");
            if (readyEvent != null) { start.ArgumentList.Add("--ready-event"); start.ArgumentList.Add(readyEvent); }
            if (launcherPid != null) { start.ArgumentList.Add("--launcher-pid"); start.ArgumentList.Add(launcherPid); }
            if (isolated != null) { start.ArgumentList.Add("--self-test-root"); start.ArgumentList.Add(isolated); }
            string shownName = @"Local\Pane-Uninstall-" + Guid.NewGuid().ToString("N");
            using var shown = new EventWaitHandle(false, EventResetMode.ManualReset, shownName);
            start.ArgumentList.Add("--shown-event"); start.ArgumentList.Add(shownName);
            for (int attempt = 1; ; attempt++)
            {
                shown.Reset(); step = "start-worker";
                var started = DateTimeOffset.UtcNow;
                var watch = Stopwatch.StartNew();
                using var process = UninstallChain.Start(start);
                process.WaitForExit();
                step = "worker-exit";
                bool visible = shown.WaitOne(0);
                bool? progressed = null;
                try
                {
                    foreach (string line in File.ReadLines(UninstallChain.LogPath))
                    {
                        if (!line.Contains($"\tpid={process.Id}\t") || !DateTimeOffset.TryParse(line.Split('\t')[0], out var time) || time < started) continue;
                        if (line.Contains("\tstep=application-entry\t")) progressed ??= false;
                        else if (progressed.HasValue) progressed = true;
                    }
                }
                catch (Exception error) { UninstallChain.Log("worker-progress", "unavailable", error); }
                var failure = process.ExitCode == 0 ? null : new ExternalException("安装界面意外退出", process.ExitCode); // 不翻译：外壳没有资源
                UninstallChain.Log(step, failure == null ? "ok" : "failed", failure, details: $"attempt={attempt}; workerPid={process.Id}; elapsedMs={watch.ElapsedMilliseconds}; windowShown={visible}; afterEntry={progressed?.ToString() ?? "unknown"}");
                if (process.ExitCode is 0 or 1) return process.ExitCode;
                if (visible || attempt == 2) throw failure!;
                UninstallChain.Log("worker-retry", "waiting", details: "delayMs=1000");
                Thread.Sleep(1000); UninstallChain.WaitReadable(temporary);
            }
        }
        catch (Exception error)
        {
            UninstallChain.Log(step, "failed", error);
            UninstallChain.Signal(readyEvent);
            string description = step switch { "extract-package" => "解压安装文件", "cleanup-stale" => "清理旧的临时文件", "start-worker" => "启动安装界面", "worker-exit" => "安装界面意外退出", _ => step }; // 不翻译：安装外壳没有资源
            string message = $"没有装上：{description}出错。\n{UninstallChain.Reason(error)}\n日志：{UninstallChain.LogPath}"; // 不翻译：安装外壳没有资源
            if (isolated != null) { Directory.CreateDirectory(isolated); File.WriteAllText(Path.Combine(isolated, "wrapper-error.txt"), message + "\n" + error); }
            else MessageBoxW(0, message, "Pane 安装程序", 0x10); // 不翻译：安装外壳没有资源
            return 1;
        }
        finally
        {
            if (temporary != null)
            {
                UninstallChain.DeleteTemporary(temporary, "cleanup-app");
            }
            temporaryLease?.Dispose();
            try { if (temporary != null && !Directory.Exists(temporary)) File.Delete(temporary + ".lease"); }
            catch (Exception error) { UninstallChain.Log("cleanup-lease", "retained", error, temporary); }
            if (relay) { try { File.Delete(Environment.ProcessPath!); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { UninstallChain.Log("cleanup-self", "retained", e, Environment.ProcessPath); } }
            helperLease?.Dispose();
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint window, string text, string caption, uint flags);
}

// 外壳不引用应用设置程序集，只需要正式数据路径常量。
namespace Pane.Settings { internal static class SettingsStore { public static string ProductionDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pane"); } }
