using Pane.Services.Setup;
using Pane.Settings;
using Pane.Contracts;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class SetupFailureTests
{
    public static async Task<int> Run(PaneSettings defaults)
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-061-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string temp = Path.Combine(Path.GetTempPath(), "Pane-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using (UninstallChain.Lease(temp, root)) { }
        string locked = Path.Combine(temp, "resources.pri"); File.WriteAllText(locked, "fake");
        try
        {
            var watch = Stopwatch.StartNew();
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var waiting = Task.Run(() => UninstallChain.WaitReadable(temp));
                await Task.Delay(600); Check(!waiting.IsCompleted, "资源锁释放前不继续");
                hold.Dispose(); await waiting;
            }
            Check(watch.ElapsedMilliseconds >= 600, "实际等待文件锁");
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                watch.Restart(); UninstallChain.WaitReadable(temp, 250);
                Check(watch.ElapsedMilliseconds < 1500, "等待有上限并继续");
                Check(!UninstallChain.DeleteTemporary(temp, "cleanup-app", 250), "锁持续时保留临时目录");
            }
            UninstallChain.Cleanup(root);
            Check(!Directory.Exists(temp) && !File.Exists(temp + ".lease"), "下次清理删除有 lease 的新残留，无需一天");

            Directory.CreateDirectory(temp); using (UninstallChain.Lease(temp, root)) { }
            var directory = new DirectoryInfo(temp); var original = directory.GetAccessControl();
            var denied = directory.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            try
            {
                directory.SetAccessControl(denied);
                UninstallChain.Cleanup(root);
                Check(Directory.Exists(temp), "无法枚举的残留保留，清理不抛错");
            }
            finally { denied.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access); directory.SetAccessControl(denied); }
            UninstallChain.Cleanup(root); Check(!Directory.Exists(temp), "恢复访问后下次可清理");

            var paths = SetupPaths.Isolated(root) with { Root = Path.Combine(root, "Ai tools", "Pane") };
            var integration = new SetupIntegration(paths, defaults, root);
            Directory.CreateDirectory(paths.Data);
            string settings = Path.Combine(paths.Data, "settings.json"); File.WriteAllText(settings, "private-fixture-value");
            string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Pane.App.exe"), "test");
            File.WriteAllText(Path.Combine(source, "Uninstall Pane.exe"), "test");
            using (var hold = new FileStream(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { integration.Capture(); throw new Exception("应捕获共享冲突"); }
                catch (IOException e) { Check((uint)e.HResult == 0x80070020, "备份文件共享冲突 HRESULT"); }
                var service = new SetupService(paths, source, "0.1.1", integration, new QuietProcesses());
                try { await service.InstallAsync(new(false, false, paths.Root), new Progress<SetupProgress>()); throw new Exception("应安装失败"); }
                catch (SetupException failure)
                {
                    Check((string?)failure.Data["Step"] == "备份登记", "现有异常携带失败步骤");
                    Check(((string?)failure.Data["Reason"])?.Contains("0x80070020") == true && failure.InnerException is IOException, "现有异常携带原始消息、错误码和异常");
                }
            }
            string log = File.ReadAllText(UninstallChain.LogPath);
            Check(log.Contains("step=capture-file\tresult=failed") && log.Contains("settings.json") && log.Contains("0x80070020"), "定位备份登记具体文件和错误");
            Check(!log.Contains("private-fixture-value"), "不记录设置内容");
            Exception error;
            try { throw new IOException("outer\r\nline\tfield", new UnauthorizedAccessException("inner\nmessage")); }
            catch (Exception e) { error = e; }
            UninstallChain.Log("061-log", "failed", error);
            string row = File.ReadLines(UninstallChain.LogPath).Last();
            Check(row.Contains("message=outer\\r\\nline\\tfield") && row.Contains("System.UnauthorizedAccessException:0x80070005:inner\\nmessage"), "消息与内部异常单行转义");
            Check(row.Contains("SetupFailureTests") && !row.EndsWith("stack=-"), "失败日志保留堆栈");
            UninstallChain.Log("061-stack", "failed", new LongStackException());
            Check(File.ReadLines(UninstallChain.LogPath).Last().Split("\tstack=")[1].Split('\t')[0].Length == 2048, "堆栈截断至2048字符");
            for (int i = 0; i < 100; i++) UninstallChain.Log("061-roll", "failed", error, details: new string('x', 2048));
            Check(new FileInfo(UninstallChain.LogPath).Length <= 65536 && File.ReadLines(UninstallChain.LogPath).All(x => x.Contains("\tpid=")), "64KB按完整行滚动");
            foreach (uint code in new uint[] { 0x80070005, 0x80070020, 0x80070021, 0x80070070 })
                Check(UninstallChain.Reason(new IOException("reason", unchecked((int)code))).Contains($"0x{code:X8}"), "提示包含错误码");
        }
        finally { SetupFiles.Delete(temp); File.Delete(temp + ".lease"); SetupFiles.Delete(root); }
        Console.WriteLine($"PASS setup061-unit assertions={checks}"); return checks;
    }
    sealed class LongStackException : Exception { public override string StackTrace => new('s', 4096); }
    sealed class QuietProcesses : ISetupProcesses
    {
        public bool Running => false;
        public void SetPaths(SetupPaths paths) { }
        public void Stop() { }
        public void Launch(bool background = false) { }
    }
}
