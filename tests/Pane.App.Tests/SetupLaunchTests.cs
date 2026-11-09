using System.Diagnostics;
using Pane.Services.Setup;

internal static class SetupLaunchTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
        var paths = SetupPaths.Isolated(Path.Combine(Path.GetTempPath(), "Pane-018-launch-088"));
        foreach (bool background in new[] { false, true })
        {
            var calls = new List<string>();
            var processes = new SetupProcesses(paths, false,
                (exe, args, foreground) => {
                    calls.Add("shell"); Check(exe == paths.Exe, "外壳目标");
                    Check(args.SequenceEqual(background ? ["--autostart"] : Array.Empty<string>()), "外壳参数");
                    Check(foreground == !background, "前台权限请求"); return 0;
                }, (_, _) => calls.Add("direct"), _ => calls.Add("log"));
            processes.Launch(background);
            Check(calls.SequenceEqual(["shell"]), "正式模式只走外壳");

            calls.Clear();
            processes = new SetupProcesses(paths, false, (_, _, _) => { calls.Add("shell"); return unchecked((int)0x80004005); },
                (info, wait) => {
                    calls.Add("direct"); Check(!wait, "回退不等待常驻进程");
                    Check(info.FileName == paths.Exe && info.ArgumentList.SequenceEqual(background ? ["--autostart"] : Array.Empty<string>()), "回退目标及参数");
                    Check(!info.UseShellExecute && info.CreateNoWindow && info.WorkingDirectory == paths.App, "回退进程选项");
                    Check(new[] { "PANE_DATA_DIR", "PANE_INSTANCE", "PANE_RESULT", "PANE_DEMO", "PANE_OBSERVE", "PANE_VERIFY", "PANE_DEV_HOTKEYS", "PANE_BIN_DIR" }.All(n => !info.Environment.ContainsKey(n)), "回退环境清理");
                }, code => { calls.Add("log"); Check(code == unchecked((int)0x80004005), "记录原始 HRESULT"); });
            processes.Launch(background);
            Check(calls.SequenceEqual(["shell", "log", "direct"]), "失败先记日志再回退一次");

            calls.Clear();
            processes = new SetupProcesses(paths, true, (_, _, _) => throw new Exception("隔离不能调用外壳"),
                (info, wait) => {
                    calls.Add("direct"); Check(wait, "隔离等待探针结束");
                    Check(info.ArgumentList.SequenceEqual(["--setup-launch-probe", paths.Root]), "隔离探针参数");
                    Check(info.Environment["PANE_DATA_DIR"] == paths.Data && info.Environment["PANE_INSTANCE"] == paths.Root, "隔离环境");
                }, _ => throw new Exception("隔离不能记回退"));
            processes.Launch(background);
            Check(calls.SequenceEqual(["direct"]), "隔离只走直接启动");
        }
        Console.WriteLine($"PASS desktop launch routing assertions={checks}");
        return checks;
    }

    public static void Probe(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (Path.GetFileName(executable) != "Pane.DesktopLaunch.Probe.exe") throw new ArgumentException("只允许仓库构建的探针");
        string result = Path.Combine(Path.GetDirectoryName(executable)!, "探针 结果.txt");
        string[] values = ["--autostart", @"D:\图片 文件\末尾\", "空格 \"引号\"", "", @"a\\\""b"];
        File.Delete(result);
        try
        {
            int hr = DesktopShellLaunch.Launch(executable, [result, .. values], true);
            if (hr < 0) throw new Exception($"Shell HRESULT=0x{hr:X8}");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(result) && timer.ElapsedMilliseconds < 15000) Thread.Sleep(20);
            string[] lines = File.ReadAllLines(result);
            using (var child = TryProcess(int.Parse(lines[0])))
            {
                if (child != null && !child.WaitForExit(5000)) { child.Kill(); throw new Exception("探针未及时退出"); }
            }
            if (lines[2] != "explorer" || lines[3] != "console=False" || !lines.Skip(4).Select(s => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s))).SequenceEqual(values))
                throw new Exception("探针结果不匹配");
            Console.WriteLine($"PASS desktop probe assertions=4; parent={lines[2]}.exe; parentPid={lines[1]}; arguments=5 exact; console=False; childExited=True");
        }
        finally { File.Delete(result); File.Delete(result + ".tmp"); }
    }
    static Process? TryProcess(int pid) { try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }
}
