using System.Diagnostics;
using Pane.Services.Setup;

internal static class UninstallTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = SetupPaths.Isolated(root);
        string? step = null; Exception? failure = null;
        void Capture(string value, Exception error) { step = value; failure = error; }
        try
        {
            string childResult = Path.Combine(root, "child arguments.json");
            var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = root };
            foreach (string value in new[] { "--uninstall-child-fixture", childResult, "空格 \" 引号", @"C:\trailing\" }) childInfo.ArgumentList.Add(value);
            using (var child = UninstallChain.Start(childInfo))
            {
                Check(child.WaitForExit(10000) && child.ExitCode == 7, "挂起创建后授权再运行，保留退出码");
                Check(System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(childResult))!.SequenceEqual(new[] { "空格 \" 引号", @"C:\trailing\" }), "原生参数数组保留引号与末尾反斜线");
            }
            UninstallChain.Launch(paths, paths.Uninstaller, root, Capture,
                copy: (_, _) => throw new UnauthorizedAccessException());
            Check(step == "copy-helper" && failure is UnauthorizedAccessException, "复制失败被提示边界截获");
            UninstallChain.Launch(paths, paths.Uninstaller, root, Capture, copy: (_, _) => { },
                start: _ => throw new System.ComponentModel.Win32Exception(5));
            Check(step == "start-helper" && failure is System.ComponentModel.Win32Exception, "助手被拦被提示边界截获");
            var watch = Stopwatch.StartNew();
            UninstallChain.Launch(paths, paths.Uninstaller, root, Capture, copy: (_, _) => { },
                start: _ => Process.GetCurrentProcess());
            Check(step == "wait-window" && failure is TimeoutException, "无窗口回执触发提示");
            Check(watch.ElapsedMilliseconds is >= 9900 and < 12000, "兜底约十秒");
            step = null; failure = null;
            UninstallChain.Launch(paths, paths.Uninstaller, root, Capture, copy: (_, _) => { }, start: info =>
            {
                int index = info.ArgumentList.IndexOf("--ready-event");
                string name = info.ArgumentList[index + 1];
                Check(UninstallChain.ValidateEvent(name) == name, "事件名称有效");
                UninstallChain.Signal(name); return Process.GetCurrentProcess();
            });
            Check(step == null && failure == null, "窗口已出现后取消不算失败");
            string log = File.ReadAllText(UninstallChain.LogPath);
            Check(log.Contains("step=copy-helper\tresult=failed") && log.Contains("exception=System.UnauthorizedAccessException"), "复制失败日志");
            Check(log.Contains("step=start-helper\tresult=failed") && log.Contains("code=0x80004005\tnative=5"), "启动失败日志有 HRESULT 及原生错误码");
            Check(log.Contains("step=wait-window\tresult=failed") && log.Contains("exception=System.TimeoutException"), "超时日志");
            for (int i = 0; i < 80; i++) UninstallChain.Log("bounded-test", path: new string('x', 1024));
            Check(new FileInfo(UninstallChain.LogPath).Length <= 65536, "日志上限64KiB");
            string helper = Path.Combine(Path.GetTempPath(), "Pane-Cleanup-" + Guid.NewGuid().ToString("N") + ".exe");
            string temporary = Path.Combine(Path.GetTempPath(), "Pane-Setup-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(helper, "fake"); Directory.CreateDirectory(temporary);
            using (UninstallChain.Lease(helper, root))
            {
                using (UninstallChain.Lease(temporary, root)) { }
                UninstallChain.Cleanup(root);
                Check(File.Exists(helper), "活动助手保留");
                Check(!Directory.Exists(temporary) && !File.Exists(temporary + ".lease"), "旧临时应用目录及lease清理");
            }
            UninstallChain.Cleanup(root);
            Check(!File.Exists(helper) && !File.Exists(helper + ".lease"), "退出的助手下次清理");
        }
        finally { SetupFiles.Delete(root); }
        Console.WriteLine($"PASS uninstall assertions={checks}; realRegistry=0; dialogs=0");
        return checks;
    }
}
