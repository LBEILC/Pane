using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        var desktop = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, 512, out _) || !desktop.ToString().StartsWith("Pane027-")) return 2;
        if (args[0] == "--launch")
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
            File.WriteAllText(args[1], $"launcher={Environment.ProcessId};foreground={foreground};background={foreground != Environment.ProcessId}\n");
            using var child = Start(args[2], args.Skip(3).ToArray());
            if (!child.WaitForExit(20000)) { child.Kill(true); return 3; }
            return child.ExitCode;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new Fixture(args));
        return Environment.ExitCode;
    }
    static Process Start(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    sealed class Fixture(string[] args) : Form
    {
        readonly List<string> rows = [];
        readonly List<Process> children = [];
        readonly HashSet<string> helpers = [];
        int checks;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        async Task Wait(Process process, int timeout = 60000, int expected = 0)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancel.Token); Check(process.ExitCode == expected, "子进程退出码 " + process.ExitCode + "; expected=" + expected); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
        async Task Run(string exe, params string[] values)
        { using var child = Start(exe, values); children.Add(child); await Wait(child); children.Remove(child); }
        async Task WaitFile(string path)
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(path) && watch.ElapsedMilliseconds < 15000) await Task.Delay(20);
            Check(File.Exists(path), "未出现 " + Path.GetFileName(path));
        }
        async Task WaitHelper(string root, int expected = 0)
        {
            string info = Path.Combine(root, "helper.txt"); await WaitFile(info);
            string[] lines = File.ReadAllLines(info); helpers.Add(lines[0]);
            try { using var child = Process.GetProcessById(int.Parse(lines[1])); children.Add(child); await Wait(child, expected: expected); children.Remove(child); }
            catch (ArgumentException) { }
        }
        static string InstallRoot(string root)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "uninstall.json")));
            return json.RootElement.GetProperty("InstallLocation").GetString()!;
        }
        static string Registered(string root)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "uninstall.json")));
            return json.RootElement.GetProperty("UninstallString").GetString()!;
        }
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            string root = Path.Combine(Path.GetTempPath(), "Pane-018-051-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                // 0.1.294 真包：完整执行其隔离安装与登记卸载链路，不读真实登记或数据。
                await Run(args[1], "--self-test-root", root);
                string installed = InstallRoot(root), command = Registered(root);
                Check(command == $"\"{Path.Combine(installed, "app", "Pane.App.exe")}\" --uninstall", "旧版登记");
                await Run(Environment.ProcessPath!, "--launch", Path.Combine(root, "background.txt"), Path.Combine(installed, "app", "Pane.App.exe"), "--uninstall", "--self-test-root", root);
                await WaitHelper(root);
                Check(!Directory.Exists(installed), "旧版隔离卸载完成");
                Check(File.ReadAllText(Path.Combine(root, "background.txt")).Contains("background=True"), "旧版从非前台进程启动");
                rows.Add("baseline 0.1.294: registered app -> temporary helper -> temporary worker -> uninstall completed; background=True; headless isolation bypasses window by design");
                // 从真实旧版布局升级，并保留自测数据。
                await Run(args[1], "--self-test-root", root);
                installed = InstallRoot(root);
                Check(!File.Exists(Path.Combine(installed, "Uninstall Pane.exe")), "旧布局无卸载程序");
                await Run(args[2], "--auto-update", "--self-test-root", root);
                string uninstaller = Path.Combine(installed, "Uninstall Pane.exe");
                Check(File.Exists(uninstaller), "升级补齐卸载程序");
                Check(Registered(root) == $"\"{uninstaller}\"", "升级改写登记");
                var version = FileVersionInfo.GetVersionInfo(uninstaller);
                Check(version.FileDescription == "Pane 卸载程序" && version.FileVersion == FileVersionInfo.GetVersionInfo(args[2]).FileVersion, "卸载器文件说明及版本");
                using (var actualIcon = Icon.ExtractAssociatedIcon(uninstaller))
                using (var appIcon = Icon.ExtractAssociatedIcon(Path.Combine(installed, "app", "Pane.App.exe")))
                using (var actual = actualIcon!.ToBitmap())
                using (var expected = appIcon!.ToBitmap())
                    Check(actual.Size == expected.Size && Enumerable.Range(0, actual.Width * actual.Height).All(i => actual.GetPixel(i % actual.Width, i / actual.Width) == expected.GetPixel(i % actual.Width, i / actual.Width)), "卸载器使用 Pane 图标");
                rows.Add($"upgrade: uninstaller={version.FileVersion}; bytes={new FileInfo(uninstaller).Length}");
                File.WriteAllText(Path.Combine(root, "show-window.test"), "hidden desktop only");
                foreach (string route in new[] { "double-click", "registration", "legacy" })
                {
                    File.Delete(Path.Combine(root, "window.txt")); File.Delete(Path.Combine(root, "helper.txt"));
                    Activate(); SetForegroundWindow(Handle);
                    bool foregroundAvailable = GetForegroundWindow() != 0;
                    string exe = route == "legacy" ? Path.Combine(installed, "app", "Pane.App.exe") : uninstaller;
                    var values = new List<string> { "--launch", Path.Combine(root, "background.txt"), exe };
                    if (route == "legacy") values.Add("--uninstall");
                    values.AddRange(["--self-test-root", root]);
                    var watch = Stopwatch.StartNew();
                    using var launcher = Start(Environment.ProcessPath!, values.ToArray()); children.Add(launcher);
                    await WaitFile(Path.Combine(root, "window.txt"));
                    long elapsed = watch.ElapsedMilliseconds;
                    string[] state = File.ReadAllLines(Path.Combine(root, "window.txt"));
                    nint window = (nint)long.Parse(state[1]);
                    Check(IsWindowVisible(window), "卸载窗口可见");
                    if (foregroundAvailable) Check(GetForegroundWindow() == window, "卸载窗口在前台");
                    Check(File.ReadAllText(Path.Combine(root, "background.txt")).Contains("background=True"), "发起进程不在前台");
                    Check(elapsed < 2500, "卸载窗口约两秒内出现");
                    rows.Add($"route={route}; visible=True; foreground={(foregroundAvailable ? (GetForegroundWindow() == window).ToString() : "UNAVAILABLE-hidden-desktop")}; elapsedMs={elapsed}; background=True");
                    PostMessageW(window, 0x10, 0, 0); // 仅关闭本测试的卸载窗口，模拟取消。
                    await Wait(launcher); children.Remove(launcher); await WaitHelper(root);
                    Check(Directory.Exists(installed), "取消不卸载");
                    Check(Directory.GetFiles(root, "*error.txt").Length == 0, "取消无错误提示");
                }
                File.Delete(Path.Combine(root, "show-window.test")); File.Delete(Path.Combine(root, "uninstalled.txt"));
                await Run(uninstaller, "--self-test-root", root); await WaitHelper(root);
                Check(!Directory.Exists(installed), "卸载后安装根目录不存在");
                string temporary = File.ReadAllText(Path.Combine(root, "uninstalled.txt"));
                Check(!Directory.Exists(temporary), "临时应用目录清理");
                string lastHelper = File.ReadAllLines(Path.Combine(root, "helper.txt"))[0];
                Check(File.Exists(lastHelper), "正在执行的助手保留供下次清理");
                await Run(args[2], "--self-test-root", root);
                Check(!File.Exists(lastHelper) && !File.Exists(lastHelper + ".lease"), "下次安装清理上次助手");
                installed = InstallRoot(root);
                Check(File.Exists(Path.Combine(installed, "Uninstall Pane.exe")), "新装带卸载程序");
                Check(Registered(root) == $"\"{Path.Combine(installed, "Uninstall Pane.exe")}\"", "新装登记");
                uninstaller = Path.Combine(installed, "Uninstall Pane.exe");
                // 用真实拒绝共享的假文件阻断复制；提示写入隔离输出，不显示消息框。
                string blocked = Path.Combine(installed, "app", "blocked.test"); File.WriteAllText(blocked, "fake");
                using (var locked = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                { await Run(uninstaller, "--self-test-root", root); await WaitHelper(root, 1); }
                string failurePath = Path.Combine(root, "wrapper-error.txt");
                Check(File.ReadAllText(failurePath).Contains("copy-app") && File.ReadAllText(failurePath).Contains("Pane-uninstall.log"), "复制失败真实提示含步骤和日志");
                File.Delete(failurePath); File.Delete(blocked);
                // 模拟安全软件阻断下一个可执行文件：复制成功、启动返回无效映像错误。
                string worker = Path.Combine(installed, "app", "Pane.App.exe"), backup = worker + ".test-backup";
                File.Move(worker, backup);
                try
                {
                    File.WriteAllText(worker, "blocked executable fixture");
                    await Run(uninstaller, "--self-test-root", root); await WaitHelper(root, 1);
                    string message = File.ReadAllText(failurePath);
                    Check(message.Contains("start-worker") && message.Contains("安全软件") && message.Contains("Pane-uninstall.log"), "启动失败真实提示含安全软件及日志");
                    File.Delete(failurePath);
                }
                finally { File.Delete(worker); File.Move(backup, worker); }
                using (var invalid = Start(uninstaller, "--invalid", "--self-test-root", root)) await Wait(invalid, expected: 1);
                Check(File.ReadAllText(failurePath).Contains("arguments") && File.ReadAllText(failurePath).Contains("Pane-uninstall.log"), "无效参数提示被截获");
                File.Delete(failurePath);
                rows.Add("failure paths: real copy lock + invalid worker image intercepted; dialogs=0; installation retained");
                rows.Add($"PASS setup051 assertions={checks}; realRegistry=0; realData=0; globalHooks=0");
            }
            catch (Exception error) { rows.Add(error.ToString()); Environment.ExitCode = 1; }
            finally
            {
                foreach (var process in children) { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { } }
                if (File.Exists(Path.Combine(root, "helper.txt"))) helpers.Add(File.ReadAllLines(Path.Combine(root, "helper.txt"))[0]);
                foreach (string helper in helpers)
                {
                    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(helper)))
                    {
                        using (process)
                        { try { if (string.Equals(process.MainModule?.FileName, helper, StringComparison.OrdinalIgnoreCase)) { process.Kill(true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { } }
                    }
                }
                foreach (string file in Directory.GetFiles(root, "*error.txt")) rows.Add(Path.GetFileName(file) + ": " + File.ReadAllText(file));
                foreach (string helper in helpers)
                {
                    string full = Path.GetFullPath(helper);
                    if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd('\\') || !Path.GetFileName(full).StartsWith("Pane-Cleanup-")) throw new IOException("Unsafe helper cleanup");
                    try { File.Delete(full); File.Delete(full + ".lease"); } catch (IOException) { }
                }
                // 根目录固定由本测试生成，拒绝重解析点后才递归删除。
                if (!Path.GetFullPath(root).StartsWith(Path.GetTempPath()) || !Path.GetFileName(root).StartsWith("Pane-018-051-")) throw new IOException("Unsafe root");
                foreach (string item in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected link");
                Directory.Delete(root, true);
                File.WriteAllLines(args[0], rows); Close();
            }
        }
    }
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(nint handle, int kind, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint window, uint message, nint w, nint l);
}
