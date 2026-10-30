using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Settings;
using System.Text.Json;
internal static class SetupTests
{
    public static async Task<int> Run(PaneSettings defaults)
    {
        int count = 0;
        void Check(bool ok, string message) { count++; if (!ok) throw new Exception("018: " + message); }
        var root = Path.Combine(Path.GetTempPath(), "Pane-018-unit-" + Guid.NewGuid().ToString("N"));
        var paths = SetupPaths.Isolated(root) with { Root = Path.Combine(root, "custom tools", "Pane") };
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Pane.App.exe"), "new");
        File.WriteAllText(Path.Combine(source, "library.dll"), "payload");
        var integration = new SetupIntegration(paths, defaults, root);
        var process = new FakeProcesses();
        var progress = new Steps();
        SetupService Service(Action<string,string>? copy = null, ISetupIntegration? alternate = null) => new(paths, source, "0.1.123", alternate ?? integration, process, copy);
        async Task Failure(Func<Task> operation)
        {
            try { await operation(); throw new Exception("应失败"); }
            catch (SetupException) { count++; }
        }
        string AppText() => File.ReadAllText(paths.Exe);
        try
        {
            string parent = Path.GetDirectoryName(paths.Root)!;
            Check(Service().CheckInstallPath(parent).Path == paths.Root, "普通文件夹补 Pane");
            Check(Service().CheckInstallPath(paths.Root + "\\").Path == paths.Root, "Pane 不重复添加");
            Check(Service().CheckInstallPath(paths.Root.ToUpperInvariant()).Path == paths.Root.ToUpperInvariant(), "Pane 大小写不敏感");
            Check(!Directory.Exists(parent), "检查不创建目录");
            Check(Service().CheckInstallPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).Error == "这里要管理员权限，换一个位置", "系统目录拒绝");
            Check(Service().CheckInstallPath(@"\\server\share").Error == "请选本机的固定磁盘", "网络路径拒绝");
            Check(Service().CheckInstallPath(Path.Combine(root, new string('a', 181))).Error == "路径太长，换一个短一点的位置", "过长路径拒绝");
            Directory.CreateDirectory(paths.Root);
            string foreign = Path.Combine(paths.Root, "用户文件.txt");
            File.WriteAllText(foreign, "保留");
            Check(Service().CheckInstallPath(paths.Root).Error == "这个文件夹里已经有别的文件", "占用目录拒绝");
            File.Delete(foreign);
            Check(Service().CheckInstallPath(paths.Root).Error == null && !Directory.EnumerateFileSystemEntries(paths.Root).Any(), "空目录可用且探测文件已清理");
            Check(Service().Info.InstalledVersion == null && Service().Info.StartWithWindows, "新装默认自启");
            Check(!new SetupService(paths, source, "0.1.123", integration, process, autoUpdate: true).AutoUpdate, "新装忽略自动更新参数");
            await Service().InstallAsync(new(true, true, paths.Root), progress);
            Check(new SetupService(paths, source, "0.1.124", integration, process, autoUpdate: true).AutoUpdate, "升级接受自动更新参数");
            Check(AppText() == "new", "新装程序文件");
            Check(File.ReadAllText(Path.Combine(root, "run.txt")) == $"\"{paths.Exe}\" --autostart", "假 Run 项");
            Check(File.Exists(Path.Combine(root, "menu/Pane.lnk")) && File.Exists(Path.Combine(root, "desktop/Pane.lnk")), "两种假快捷方式");
            Check(progress.Values.First().Fraction > 0 && progress.Values.Last().Fraction == 1 && progress.Values.Select(v => v.Fraction).SequenceEqual(progress.Values.Select(v => v.Fraction).Order()), "有序进度");
            using (var registration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "uninstall.json"))))
            {
                var fields = registration.RootElement;
                Check(fields.GetProperty("DisplayName").GetString() == "Pane" && fields.GetProperty("DisplayVersion").GetString() == "0.1.123", "名称版本");
                Check(fields.GetProperty("Publisher").GetString() == "Pane" && fields.GetProperty("DisplayIcon").GetString() == paths.Exe, "发布者图标");
                Check(fields.GetProperty("InstallLocation").GetString() == paths.Root && fields.GetProperty("EstimatedSize").GetInt32() == 1, "安装位置及 KiB 大小");
                Check(fields.GetProperty("UninstallString").GetString() == $"\"{paths.Exe}\" --uninstall", "卸载命令引号");
                Check(fields.GetProperty("NoModify").GetInt32() == 1 && fields.GetProperty("NoRepair").GetInt32() == 1, "禁用修改修复");
            }
            Check(Service().Info.InstalledVersion == "0.1.123" && Service().Info.StartWithWindows, "读取已装信息");
            var fallback = SetupPaths.Isolated(root);
            var discovered = new SetupService(fallback, source, "0.1.124", new SetupIntegration(fallback, defaults, root), process);
            Check(discovered.Info.InstallPath == paths.Root, "卸载登记发现自定义位置");
            await Failure(() => discovered.InstallAsync(new(false, false, Path.Combine(root, "other", "Pane")), progress));
            Check(AppText() == "new" && process.Stops == 1, "升级位置不一致在退出进程前拒绝");
            // 模拟 017 安装：只有 app，无卸载登记。
            File.Delete(Path.Combine(root, "uninstall.json")); File.WriteAllText(paths.Exe, "017");
            process.Running = true;
            await Service().InstallAsync(new(false, false, paths.Root), progress);
            Check(AppText() == "new" && File.ReadAllText(Path.Combine(paths.Root, "app.old/Pane.App.exe")) == "017", "原地升级保留旧版");
            Check(!process.Running && process.Stops == 2, "替换前退出");
            Check(File.Exists(Path.Combine(root, "uninstall.json")) && !File.Exists(Path.Combine(root, "run.txt")) && !File.Exists(Path.Combine(root, "desktop/Pane.lnk")), "补齐卸载登记并应用选项");
            var oldSettings = File.ReadAllBytes(Path.Combine(paths.Data, "settings.json"));
            await Failure(() => Service((s, d) => { Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "partial"), "x"); throw new IOException("复制失败"); }).InstallAsync(new(true, true, paths.Root), progress));
            Check(AppText() == "new" && !Directory.Exists(Path.Combine(paths.Root, "app.new")), "复制失败清理暂存且不动已装程序");
            Check(File.ReadAllText(Path.Combine(paths.Root, "app.old/Pane.App.exe")) == "017", "复制失败保留历史版本");
            process.Running = true; process.Refuse = true;
            await Failure(() => Service().InstallAsync(new(true, true, paths.Root), progress));
            Check(process.Running && AppText() == "new" && !Directory.Exists(Path.Combine(paths.Root, "app.new")), "旧版退不掉不切换文件");
            process.Refuse = false;
            var failing = new FailingIntegration(integration);
            await Failure(() => Service(alternate: failing).InstallAsync(new(true, true, paths.Root), progress));
            Check(AppText() == "new" && File.ReadAllText(Path.Combine(paths.Root, "app.old/Pane.App.exe")) == "017", "登记失败逆序恢复两代目录");
            Check(process.Running && process.Launches == 1, "登记失败恢复旧进程运行");
            Check(oldSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(paths.Data, "settings.json"))) && !File.Exists(Path.Combine(root, "run.txt")) && !File.Exists(Path.Combine(root, "desktop/Pane.lnk")), "登记失败恢复设置启动项快捷方式");
            // 031：假目录移动边界，真实文件回滚；不等待真实 2.5 秒。
            foreach (int code in new[] { 5, 32, 33 })
            {
                int calls = 0, waited = 0;
                var transient = new SetupService(paths, source, "0.1.124", integration, process,
                    move: (a, b) => { if (a == paths.App && ++calls < 3) throw new IOException("短暂占用", unchecked((int)0x80070000) | code); Directory.Move(a, b); },
                    delay: ms => waited += ms);
                await transient.InstallAsync(new(false, false, paths.Root), progress);
                Check(calls == 3 && waited == 1000 && AppText() == "new", "短暂占用重试成功 " + code);
            }
            int attempts = 0, wait = 0;
            var blocked = new SetupService(paths, source, "0.1.125", integration, process,
                move: (a, b) => { if (a == paths.App) { attempts++; throw new IOException("持续占用", unchecked((int)0x80070020)); } Directory.Move(a, b); },
                delay: ms => wait += ms);
            try { await blocked.InstallAsync(new(false, false, paths.Root), progress); throw new Exception("应被占用阻止"); }
            catch (SetupException error) { Check(error.Message == "有程序正在使用 Pane 的安装目录，关掉打开着这个目录的窗口或程序后再试。", "占用提示精确匹配"); }
            Check(attempts == 6 && wait == 2500, "重试有界");
            Check(AppText() == "new" && !Directory.Exists(Path.Combine(paths.Root, "app.new")) && !Directory.Exists(Path.Combine(paths.Root, "app.previous")), "持续占用回滚干净");
            string logDirectory = Path.Combine(paths.Data, "logs");
            string latest = Directory.GetFiles(logDirectory, "setup-*.txt").OrderByDescending(Path.GetFileName).First();
            string detail = File.ReadAllText(latest);
            Check(detail.Contains("System.IO.IOException") && detail.Contains("0x80070020") && detail.Contains("移动目录") && detail.Contains(paths.App) && detail.Contains(Path.Combine(paths.Root, "app.old")), "错误类型数值步骤及双路径入日志");
            foreach (int code in new[] { 2, 112 })
            {
                attempts = 0; wait = 0;
                var permanent = new SetupService(paths, source, "0.1.125", integration, process,
                    move: (a, b) => { if (a == paths.App) { attempts++; throw new IOException("非占用", unchecked((int)0x80070000) | code); } Directory.Move(a, b); }, delay: ms => wait += ms);
                try { await permanent.InstallAsync(new(false, false, paths.Root), progress); throw new Exception("应失败"); }
                catch (SetupException error) { Check(error.Message == (code == 112 ? "磁盘空间不够" : "操作未完成，请检查目录权限和文件是否被其他程序占用后重试。"), "非占用文案 " + code); }
                Check(attempts == 1 && wait == 0, "非占用不重试 " + code);
            }
            Check(SetupDiagnostics.Busy(new UnauthorizedAccessException()), "拒绝访问可重试");
            File.WriteAllText(Path.Combine(logDirectory, "crash-keep.txt"), "保留");
            for (int i = 0; i < 12; i++) SetupDiagnostics.Write(paths.Data, new IOException("日志测试"), "测试 " + i, source, paths.App);
            Check(Directory.GetFiles(logDirectory, "setup-*.txt").Length == 10, "安装日志最多十份");
            Check(File.Exists(Path.Combine(logDirectory, "crash-keep.txt")), "不删除其他日志");
            string notDirectory = Path.Combine(root, "log-file"); File.WriteAllText(notDirectory, "x");
            SetupDiagnostics.Write(notDirectory, new IOException("原始异常"), "日志不可写", source);
            Check(File.ReadAllText(notDirectory) == "x", "日志不可写不掩盖失败");
            // 由其他应用接管 Run\Pane 后，卸载不能误删。
            File.WriteAllText(Path.Combine(root, "run.txt"), "\"C:\\Other\\tool.exe\" --autostart");
            File.WriteAllText(foreign, "保留");
            await Service().UninstallAsync(false, progress);
            Check(File.ReadAllText(foreign) == "保留" && Directory.Exists(parent), "卸载保留用户文件和父目录");
            Check(!Directory.Exists(paths.App) && !Directory.Exists(Path.Combine(paths.Root, "app.old")), "卸载删除程序和历史版本");
            Check(File.Exists(Path.Combine(paths.Data, "settings.json")), "卸载保留数据");
            Check(File.Exists(Path.Combine(root, "run.txt")), "不删除其他位置的 Run 项");
            Check(!File.Exists(Path.Combine(root, "uninstall.json")) && !File.Exists(Path.Combine(root, "menu/Pane.lnk")), "卸载移除登记快捷方式");
            File.Delete(Path.Combine(root, "run.txt"));
            File.Delete(foreign);
            await Service().InstallAsync(new(true, false, paths.Root), progress);
            await Service().UninstallAsync(true, progress);
            Check(!Directory.Exists(paths.Root) && Directory.Exists(parent), "空安装目录删除且父目录保留");
            Console.WriteLine("PASS custom lifecycle: install -> upgrade -> uninstall; parent preserved; foreign file preserved; empty root removed");
            Check(!Directory.Exists(paths.Data) && !File.Exists(Path.Combine(root, "run.txt")), "删除数据及指向本安装的 Run 项");
            Check(SetupFiles.CompareVersions("0.1.9", "0.1.10") < 0, "数字版本比较");
            Check(SetupFiles.CompareVersions("1.2.0", "1.2") == 0 && SetupFiles.CompareVersions("1.2.0.0", "1.2.0+abc") == 0, "规范化版本比较");
            Check(SetupFiles.CompareVersions("2.0.0", "1.99.99") > 0, "主版本比较");
        }
        finally { SetupFiles.Delete(root); }
        Console.WriteLine($"PASS setup assertions={count}; realRegistry=0; realShortcuts=0");
        return count;
    }
    sealed class FakeProcesses : ISetupProcesses
    {
        public void SetPaths(SetupPaths paths) { }
        public bool Running { get; set; }
        public bool Refuse;
        public int Stops, Launches;
        public void Stop() { Stops++; if (Refuse) throw new SetupException("Pane 正在运行，没能让它退出，请先从托盘退出再试"); Running = false; }
        public void Launch(bool background = false) { Launches++; Running = true; }
    }
    sealed class Steps : IProgress<SetupProgress> { public List<SetupProgress> Values = []; public void Report(SetupProgress value) => Values.Add(value); }
    sealed class FailingIntegration(ISetupIntegration inner) : ISetupIntegration
    {
        public string? InstalledVersion => inner.InstalledVersion;
        public string? InstallLocation => inner.InstallLocation;
        public void SetPaths(SetupPaths paths) => inner.SetPaths(paths);
        public bool StartWithWindows => inner.StartWithWindows;
        public object Capture() => inner.Capture();
        public void Restore(object state) => inner.Restore(state);
        public void Apply(string version, long bytes, SetupOptions options) { inner.Apply(version, bytes, options); throw new IOException("登记写入后失败"); }
        public void Remove() => inner.Remove();
    }
}
