using System.Text.Json;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Settings;

internal static partial class SetupTests
{
    internal static async Task Registration157(PaneSettings defaults)
    {
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-157-" + Guid.NewGuid().ToString("N"));
        var initial = SetupPaths.Isolated(root);
        var paths = initial with { Root = Path.Combine(root, "custom tools", "Pane") };
        string source = Path.Combine(root, "source");
        string current = SetupRegistration.FileName(root, SetupRegistration.ProductCode);
        string legacy = SetupRegistration.FileName(root, SetupRegistration.Legacy);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Pane.App.exe"), "157");
        File.WriteAllText(Path.Combine(source, "Uninstall Pane.exe"), "157");
        var integration = new SetupIntegration(paths, defaults, root);
        var options = new SetupOptions(false, true, paths.Root);
        void Check(bool ok, string name) { if (!ok) throw new Exception("157: " + name); }
        UninstallChain.IsolatedLogRoot = root;
        try
        {
            integration.Apply("1.0.0", 1024, options);
            var entry = JsonNode.Parse(File.ReadAllText(current))!.AsObject();
            Check(!File.Exists(legacy) && (string?)entry["Publisher"] == "LBEILC", "全新安装");
            File.Delete(current);
            entry["Publisher"] = "Pane";
            entry["DisplayVersion"] = "0.1.814";
            string old = entry.ToJsonString();
            File.WriteAllText(legacy, old);
            Check(initial.ResolveInstalled(root).Root == paths.Root, "旧登记自选位置");
            var update = new SetupService(initial, source, "1.0.0", new SetupIntegration(initial, defaults, root), new FakeProcesses(), autoUpdate: true);
            Check(update.AutoUpdate && update.Info.InstalledVersion == "0.1.814" && update.Info.InstallPath == paths.Root, "旧版识别为升级");
            await update.InstallAsync(options, new Steps());
            Check(!File.Exists(legacy) && integration.InstalledVersion == "1.0.0" && integration.InstallLocation == paths.Root, "升级迁移");
            string upgraded = File.ReadAllText(current);
            Check((string?)JsonNode.Parse(upgraded)!["Publisher"] == "LBEILC", "迁移后的发布者");
            Console.WriteLine($"0.1.814 -> 1.0.0: {SetupRegistration.Key(SetupRegistration.ProductCode)} {upgraded}");

            File.WriteAllText(legacy, old);
            var failing = new SetupService(paths, source, "1.0.1", new FailingIntegration(integration), new FakeProcesses());
            try { await failing.InstallAsync(options, new Steps()); throw new Exception("未触发回滚"); }
            catch (SetupException) { }
            Check(JsonNode.DeepEquals(JsonNode.Parse(upgraded), JsonNode.Parse(File.ReadAllText(current))) &&
                JsonNode.DeepEquals(JsonNode.Parse(old), JsonNode.Parse(File.ReadAllText(legacy))), "登记后失败还原两个键");

            entry.Remove("InstallLocation");
            File.WriteAllText(legacy, entry.ToJsonString());
            integration.Remove();
            Check(!File.Exists(current) && !File.Exists(legacy), "按卸载命令匹配并卸载");
            entry["InstallLocation"] = Path.Combine(root, "other");
            entry["UninstallString"] = Path.Combine(root, "other", "remove.exe");
            string foreign = entry.ToJsonString();
            File.WriteAllText(legacy, foreign);
            Check(initial.ResolveInstalled(root) == initial && integration.InstalledVersion == null, "忽略其他同名程序");
            integration.Apply("1.0.0", 1024, options);
            integration.Remove();
            Check(File.ReadAllText(legacy) == foreign, "安装和卸载保留其他程序登记");
            Console.WriteLine("PASS --157");
        }
        finally { UninstallChain.IsolatedLogRoot = null; SetupFiles.Delete(root); }
    }
}
