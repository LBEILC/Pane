using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

internal static class FailureFixture
{
    public static async Task<int> Run(string package, string payload, string evidence)
    {
        int checks = 0; var rows = new List<string>(); var children = new List<Process>(); var temporaries = new HashSet<string>();
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-061-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string At(string name) => Path.Combine(root, name);
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
        Process Start(string exe, params string[] args)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            var process = Process.Start(info)!; children.Add(process); return process;
        }
        async Task Wait(Process child, int expected = 0)
        {
            using var limit = new CancellationTokenSource(60000);
            await child.WaitForExitAsync(limit.Token); Check(child.ExitCode == expected, $"退出码={child.ExitCode}, expected={expected}");
        }
        async Task<string> WaitFile(string path)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 20000)
            {
                try { if (File.Exists(path) && new FileInfo(path).Length > 0) return File.ReadAllText(path).Trim(); } catch (IOException) { }
                await Task.Delay(20);
            }
            throw new TimeoutException(Path.GetFileName(path));
        }
        string Registration()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(At("uninstall.json")));
            return json.RootElement.GetProperty("InstallLocation").GetString()!;
        }
        string Log() => File.ReadAllText(Path.Combine(Path.GetTempPath(), "Pane-uninstall.log"));
        string NewTemporary()
        {
            string path = Path.Combine(Path.GetTempPath(), "Pane-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); File.WriteAllText(path + ".lease", root); temporaries.Add(path); return path;
        }
        try
        {
            File.WriteAllText(At("ai-tools.test"), "test");
            File.WriteAllText(At("scan.test"), "test"); File.WriteAllText(At("crash-once.test"), "test");
            var wrapper = Start(package, "--self-test-root", root);
            string extracted = await WaitFile(At("extracted.txt")); temporaries.Add(extracted);
            var locks = new List<FileStream>();
            try
            {
                foreach (string name in new[] { "resources.pri", "Microsoft.UI.Xaml.dll", "Pane.Native.dll" })
                    locks.Add(new FileStream(Path.Combine(extracted, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                File.WriteAllText(At("scan-ready.test"), "ready");
                await Task.Delay(2200);
                Check(File.Exists(At("crash-once.test")), "文件占用期间尚未拉起worker");
                Check(!File.Exists(At("installed.txt")), "文件占用期间尚未安装");
            }
            finally { foreach (var handle in locks) handle.Dispose(); }
            await Wait(wrapper);
            Check(File.Exists(At("installed.txt")), "等待及一次早退重试后安装成功");
            string log = Log();
            Check(log.Contains("step=wait-readable\tresult=waiting") && log.Contains("step=worker-retry"), "等待与重试日志");
            Check(log.Contains("attempt=1") && log.Contains("attempt=2") && log.Contains("elapsedMs=") && log.Contains("afterEntry="), "两次worker完整退出诊断");
            string installed = Registration(); Check(installed == At(@"Ai tools\Pane"), "空格安装路径");
            Check(!Directory.Exists(extracted), "成功后临时目录清理");
            rows.Add("scan: locked resources.pri + Microsoft.UI.Xaml.dll + Pane.Native.dll for 2200ms; start delayed; injected early exit retried once; installation succeeded");
            File.Delete(At("scan.test")); File.Delete(At("scan-ready.test")); File.Delete(At("extracted.txt"));

            File.WriteAllText(At("cleanup-hold.test"), "test");
            var cleanupWrapper = Start(package, "--self-test-root", root);
            string cleanupApp = (await WaitFile(At("worker-completed.txt"))).TrimEnd('\\'); temporaries.Add(cleanupApp);
            using (var hold = new FileStream(Path.Combine(cleanupApp, "resources.pri"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                File.WriteAllText(At("worker-release.test"), "release");
                await Wait(cleanupWrapper);
                Check(Directory.Exists(cleanupApp) && !File.Exists(At("cleanup-error.txt")) && !File.Exists(At("wrapper-error.txt")), "cleanup-app 超时只保留，不报安装失败");
            }
            File.Delete(At("cleanup-hold.test")); File.Delete(At("worker-completed.txt")); File.Delete(At("worker-release.test"));

            string stale = NewTemporary(); string blocked = Path.Combine(stale, "busy.dll"); File.WriteAllText(blocked, "test");
            using (var hold = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Wait(Start(package, "--self-test-root", root));
                Check(Directory.Exists(stale), "锁住残留被保留");
                Check(!Directory.Exists(cleanupApp), "下一次启动清掉 cleanup-app 残留");
                Check(!File.Exists(At("wrapper-error.txt")) && !File.Exists(At("cleanup-error.txt")), "残留删除失败不弹窗不失败");
                Check(File.Exists(Path.Combine(installed, "app.old", "Pane.App.exe")), "空格路径升级完成");
            }
            string deniedPath = NewTemporary(); var deniedDirectory = new DirectoryInfo(deniedPath);
            var original = deniedDirectory.GetAccessControl(); var denied = deniedDirectory.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            try
            {
                deniedDirectory.SetAccessControl(denied);
                await Wait(Start(package, "--self-test-root", root));
                Check(!Directory.Exists(stale), "下一次启动清掉已释放的残留");
                Check(Directory.Exists(deniedPath) && !File.Exists(At("wrapper-error.txt")), "无法访问的临时项不阻止安装");
            }
            finally { denied.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access); deniedDirectory.SetAccessControl(denied); }
            rows.Add("cleanup: sharing violation retained without dialog; next launch removed it; denied enumeration did not fail installation");

            File.Delete(At("helper.txt"));
            string uninstallStale = NewTemporary(); string uninstallLock = Path.Combine(uninstallStale, "busy.dll"); File.WriteAllText(uninstallLock, "test");
            using (var hold = new FileStream(uninstallLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Wait(Start(Path.Combine(installed, "Uninstall Pane.exe"), "--self-test-root", root));
                Check(!File.Exists(At("wrapper-error.txt")), "卸载助手的窗口等待预算覆盖残留清理退避");
            }
            string[] helper = (await WaitFile(At("helper.txt"))).Split('\n');
            temporaries.Add(helper[0].Trim());
            try { var process = Process.GetProcessById(int.Parse(helper[1])); children.Add(process); await Wait(process); } catch (ArgumentException) { }
            Check(!Directory.Exists(installed), "空格路径卸载完成");

            // 真正的对照实验：直接运行被锁的 payload，不走可读等待，不注入退出码。
            string direct = NewTemporary();
            foreach (string file in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
            { string target = Path.Combine(direct, Path.GetRelativePath(payload, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
            File.WriteAllText(At("show-window.test"), "hidden only");
            foreach (string[] names in new[] { new[] { "resources.pri", "Microsoft.UI.Xaml.dll", "Pane.Native.dll" }, new[] { "resources.pri" }, Array.Empty<string>() })
            {
                var directLocks = new List<FileStream>(); Process? actual = null;
                string shownName = @"Local\Pane-Uninstall-" + Guid.NewGuid().ToString("N");
                using var shown = new EventWaitHandle(false, EventResetMode.ManualReset, shownName);
                try
                {
                    foreach (string name in names) directLocks.Add(new FileStream(Path.Combine(direct, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                    actual = Start(Path.Combine(direct, "Pane.App.exe"), "--setup", "--self-test-root", root, "--shown-event", shownName);
                    await Task.Delay(4000);
                    rows.Add($"direct locks=[{string.Join(',', names)}] at 4s: shown={shown.WaitOne(0)}; exited={actual.HasExited}; code={(actual.HasExited ? $"0x{actual.ExitCode:X8}" : "running")}");
                    if (names.Length == 0) Check(shown.WaitOne(0), "相同目录解锁后正常显示真实安装窗口");
                }
                finally { foreach (var handle in directLocks) handle.Dispose(); }
                if (actual != null && !actual.HasExited)
                {
                    // 未显示窗口时先观察自然退出，不能向初始化中的隐藏窗口发关闭消息污染复现结果。
                    var observation = Stopwatch.StartNew();
                    while (!actual.HasExited && !shown.WaitOne(0) && observation.ElapsedMilliseconds < 5000) await Task.Delay(20);
                    bool closing = !actual.HasExited && shown.WaitOne(0);
                    rows.Add($"direct after release: exited={actual.HasExited}; shown={shown.WaitOne(0)}; fixtureCloseSent={closing}");
                    if (closing) EnumWindows((window, _) => { GetWindowThreadProcessId(window, out uint pid); if (pid == actual.Id && IsWindowVisible(window)) PostMessageW(window, 0x10, 0, 0); return true; }, 0);
                    using var limit = new CancellationTokenSource(5000);
                    try { await actual.WaitForExitAsync(limit.Token); } catch (OperationCanceledException) { actual.Kill(true); await actual.WaitForExitAsync(); rows.Add("direct worker required fixture termination after locks released"); }
                }
                rows.Add($"direct final code=0x{actual!.ExitCode:X8}");
            }
            File.WriteAllText(evidence + ".installer.log", Log());
            rows.Add($"PASS setup061-hidden assertions={checks}; realRegistry=0; realData=0; dialogsOnUserDesktop=0");
            return 0;
        }
        catch (Exception error)
        {
            rows.Add(error.ToString());
            foreach (string file in Directory.GetFiles(root, "*error.txt")) rows.Add(Path.GetFileName(file) + ": " + File.ReadAllText(file));
            File.WriteAllText(evidence + ".installer.log", Log());
            return 1;
        }
        finally
        {
            foreach (var child in children)
            { try { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } } catch (InvalidOperationException) { } child.Dispose(); }
            foreach (string path in temporaries)
            {
                string full = Path.GetFullPath(path);
                if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd('\\') || !(Path.GetFileName(full).StartsWith("Pane-Setup-") || Path.GetFileName(full).StartsWith("Pane-Cleanup-"))) throw new IOException("Unsafe temporary cleanup");
                if (Directory.Exists(full)) Directory.Delete(full, true); else File.Delete(full);
                File.Delete(full + ".lease");
            }
            if (!Path.GetFullPath(root).StartsWith(Path.GetTempPath()) || !Path.GetFileName(root).StartsWith("Pane-018-061-")) throw new IOException("Unsafe root");
            Directory.Delete(root, true); File.WriteAllLines(evidence, rows);
        }
    }
    delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint window, uint message, nint w, nint l);
}
