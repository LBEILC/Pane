using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Settings;
using System.Diagnostics;

internal static class SetupLinkTests
{
    public static async Task<int> Run(PaneSettings defaults)
    {
        int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("075: " + name); }
        Check(SetupFiles.IsNameSurrogate(0xA0000003), "junction 是名称代理");
        Check(SetupFiles.IsNameSurrogate(0xA000000C), "symlink 是名称代理");
        Check(!SetupFiles.IsNameSurrogate(0x9000001A), "cloud 不是名称代理");
        Check(!SetupFiles.IsNameSurrogate(0x8000001B), "appexeclink 不是名称代理");
        for (uint variant = 1; variant <= 15; variant++)
            Check(!SetupFiles.IsNameSurrogate(0x9000001A | (variant << 12)), $"cloud_{variant:X} 不是名称代理");
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-links-" + Guid.NewGuid().ToString("N"));
        var links = new List<string>();
        Directory.CreateDirectory(root);
        string Join(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
        void Junction(string link, string target)
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "create-junction.ps1"), "-Link", link, "-Target", target })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("创建测试 junction 失败");
            links.Add(link);
        }
        void Refused(Action action, string path, string reason)
        {
            try { action(); throw new Exception("应拒绝链接：" + path); }
            catch (IOException error) { Check(error.Message.Contains(path) && error.Message.Contains(reason), "异常含具体路径和链接位置"); }
        }
        try
        {
            string real = Join("real"), alias = Join("alias");
            Directory.CreateDirectory(real); Junction(alias, real);
            string isolated = Path.Combine(alias, "Pane-018-isolated");
            var paths = SetupPaths.Isolated(isolated) with { Root = Path.Combine(isolated, "install", "Pane") };
            string source = Path.Combine(isolated, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Pane.App.exe"), "app");
            File.WriteAllText(Path.Combine(source, "Uninstall Pane.exe"), "uninstaller");
            Directory.CreateDirectory(paths.Data);
            new SettingsStore(paths.Data, defaults).Save(defaults);
            Directory.CreateDirectory(Path.Combine(isolated, "menu"));
            Directory.CreateDirectory(Path.Combine(isolated, "desktop"));
            File.WriteAllText(Path.Combine(isolated, "menu", "Pane.lnk"), "before");
            File.WriteAllText(Path.Combine(isolated, "desktop", "Pane.lnk"), "before");
            var integration = new SetupIntegration(paths, defaults, isolated);
            var service = new SetupService(paths, source, "0.1.1", integration, new Processes());
            Check(service.Info.SizeBytes == 14, "Info 经过 junction 统计源目录");
            var snapshot = integration.Capture();
            integration.Restore(snapshot);
            Check(File.ReadAllText(Path.Combine(isolated, "menu", "Pane.lnk")) == "before", "Capture/Restore 经过 junction");
            await service.InstallAsync(new(true, true, paths.Root), new Progress());
            Check(File.ReadAllText(paths.Exe) == "app", "junction 祖先下安装成功");
            Check(File.Exists(Path.Combine(isolated, "desktop", "Pane.lnk")) && File.Exists(Path.Combine(paths.Data, "settings.json")), "快捷方式和设置写入成功");
            await service.UninstallAsync(true, new Progress());
            Check(!Directory.Exists(paths.Root) && !Directory.Exists(paths.Data), "junction 祖先下卸载成功");
            string external = Join("external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.txt"), "untouched");
            string treeLink = Path.Combine(source, "linked");
            Junction(treeLink, external);
            Check(SetupFiles.Size(source) == 14, "Size 跳过树内 junction");
            Refused(() => SetupFiles.Delete(source), treeLink, "树里有链接");
            Refused(() => SetupFiles.Copy(source, Join("copy")), treeLink, "树里有链接");
            Check(!Directory.Exists(Join("copy")) && File.Exists(Path.Combine(source, "Pane.App.exe")), "整树预检先于复制删除");
            Check(Directory.GetFiles(external).Length == 1 && File.ReadAllText(Path.Combine(external, "keep.txt")) == "untouched", "链接目标所有文件不变");
            string installLink = Join("install-link"); Junction(installLink, external);
            Refused(() => SetupFiles.Check(installLink), installLink, "本身是链接");
            Check(SetupInstallPath.Check(installLink, false).Error != null, "安装根本身为链接拒绝");
            string fileLink = Path.Combine(isolated, "menu", "Pane.lnk"); Junction(fileLink, external);
            Refused(integration.Remove, fileLink, "本身是链接");
            Refused(() => integration.Restore(snapshot), fileLink, "本身是链接");
            string settingsLink = Path.Combine(paths.Data, "settings.json");
            Directory.CreateDirectory(paths.Data); Junction(settingsLink, external);
            Refused(() => integration.Apply("0.1.1", 14, new(false, false, paths.Root)), settingsLink, "本身是链接");
            string system = Join("system"), systemAlias = Join("system-alias");
            Directory.CreateDirectory(system); Junction(systemAlias, system);
            Check(SetupInstallPath.Check(Path.Combine(systemAlias, "missing", "Pane"), false, systemDirectories: [system]).Error != null, "真实祖先阻止绕入系统目录");
            Check(!Directory.EnumerateFileSystemEntries(system).Any(), "拒绝之前未写模拟系统目录");
            Check(SetupInstallPath.Check(Path.Combine(system, "Pane"), false, systemDirectories: [system]).Error != null, "原字符串防线保留");
            Directory.CreateDirectory(Join("system-other"));
            Check(SetupInstallPath.Check(Join("system-other", "Pane"), false, systemDirectories: [system]).Error == null, "相同前缀的其他目录允许");
        }
        finally
        {
            // 只删除本测试登记的链接本身，再清理已确认位于临时根内的真实目录。
            foreach (string link in links.AsEnumerable().Reverse())
            {
                if (!Path.GetFullPath(link).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试清理路径越界");
                if (Directory.Exists(link)) Directory.Delete(link, false);
            }
            SetupFiles.Delete(root);
        }
        Console.WriteLine($"PASS setup links assertions={count}; realRegistry=0; realShortcuts=0");
        return count;
    }
    sealed class Processes : ISetupProcesses
    {
        public bool Running => false;
        public void SetPaths(SetupPaths paths) { }
        public void Stop() { }
        public void Launch(bool background = false) { }
    }
    sealed class Progress : IProgress<SetupProgress> { public void Report(SetupProgress value) { } }
}
