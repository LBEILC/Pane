using System.ComponentModel;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static async Task SuperPanel148Tests()
    {
        string root = TestResults.NewDirectory("148-");
        Directory.CreateDirectory(root);
        var defaults = Defaults();
        string[] ids = Enumerable.Range(1, 10).Select(i => $"app:{i}").ToArray();
        var legacy = defaults with { Launcher = defaults.Launcher with {
            PinnedApps = ["file:example", .. ids], AdminApps = [ids[1]] } };
        var json = JsonSerializer.SerializeToNode(legacy, SettingsJson.Default.PaneSettings)!;
        json["SuperPanel"]!.AsObject().Remove("Apps");
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, json.ToJsonString());
        var store = new SettingsStore(root, defaults);
        store.Load();
        Check(store.Current.SuperPanel.Apps!.SequenceEqual(ids[..8]), "按原顺序迁移应用");
        var saved = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.PaneSettings)!;
        Check(saved.SuperPanel.Apps!.SequenceEqual(ids[..8]), "首次读取即写回");
        var transfer = JsonSerializer.SerializeToNode(saved, SettingsJson.Default.PaneSettings)!.AsObject();
        LocalSettingsFields.Apply(transfer, null);
        LocalSettingsFields.Apply(transfer, json.AsObject());
        Check(transfer.Deserialize(SettingsJson.Default.PaneSettings)!.SuperPanel.Apps!.SequenceEqual(ids[..8]), "导出导入保留常用应用");
        store.Save(saved with { Launcher = saved.Launcher with { PinnedApps = [] } });
        store.Load();
        Check(store.Current.SuperPanel.Apps!.SequenceEqual(ids[..8]), "已有列表独立");
        store.Save(saved with { SuperPanel = saved.SuperPanel with { Apps = ids.Reverse().ToArray() } });
        Check(store.Current.SuperPanel.Apps!.SequenceEqual(ids.Reverse().Take(8)), "保留显式列表前八项");
        store.Save(saved with { SuperPanel = saved.SuperPanel with { Apps = [] } });
        Check(store.Current.SuperPanel.Apps is { Length: 0 }, "显式空列表不迁移");

        var table = new PinyinTable();
        var plain = new IndexedApp(ids[0], table.Create("Plain"), "plain.exe", false, null);
        var admin = new IndexedApp(ids[1], table.Create("Admin"), "admin.exe", false, null);
        var process = new Process037();
        var executor = new FakeExecutor();
        using var search = new LauncherSearch(store, new Index076([plain, admin]), new FakeFiles(),
            executor, process: process, actionPlatform: new ActionPlatform());
        await search.Indexing;
        IAppLauncher launcher = search;
        await launcher.LaunchAsync(plain.Id, default);
        Check(executor.Calls.SequenceEqual(["open:plain.exe:False"]), "按 id 普通打开");
        process.Error = new Win32Exception(1223);
        await launcher.LaunchAsync(admin.Id, default);
        var usagePath = Path.Combine(root, "launcher-usage.json");
        Dictionary<string,AppUse> Usage() => JsonSerializer.Deserialize(File.ReadAllText(usagePath),
            LauncherJson.Default.DictionaryStringAppUse)!;
        Check(Usage()[plain.Id].Count == 1 && !Usage().ContainsKey(admin.Id), "普通打开计数，拒绝提权不计数");
        process.Error = null;
        await launcher.LaunchAsync(admin.Id, default);
        Check(process.Requests[^1].Elevated && Usage()[admin.Id].Count == 1, "成功提权后计数");
        try { await launcher.LaunchAsync("app:missing", default); throw new Exception("缺少异常"); }
        catch (LauncherActionException error) { Check(error.Message == Pane.Localization.Strings.SuperPanel_AppMissing, "缺失应用提示"); }
        Console.WriteLine($"PASS 148 launcher/settings assertions={checks}");
    }
}
