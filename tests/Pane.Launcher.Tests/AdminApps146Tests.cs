using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static async Task AdminApps146Tests()
    {
        string root = TestResults.NewDirectory("146-");
        Directory.CreateDirectory(root);
        var defaults = Defaults();
        var store = new SettingsStore(root, defaults);
        var file = Path.Combine(root, "settings.json");
        var old = JsonSerializer.SerializeToNode(defaults, SettingsJson.Default.PaneSettings)!.AsObject();
        old["Launcher"]!.AsObject().Remove("AdminApps");
        File.WriteAllText(file, old.ToJsonString());
        store.Load();
        Check(store.Current.Launcher.AdminApps.Length == 0, "旧设置默认空数组");

        var table = new PinyinTable();
        var app = new IndexedApp("app:desk", table.Create("Desk"), "desk.lnk", false, null);
        var packaged = new IndexedApp("app:term", table.Create("Term"), "Terminal_family!App", true, null);
        var plain = new IndexedApp("app:plain", table.Create("Plain"), "plain.exe", false, null);
        var entries = new[] { app.Id, packaged.Id, "app:removed" };
        store.Save(defaults with { Launcher = defaults.Launcher with { AdminApps = entries, PinnedApps = [app.Id] } });
        store.Load();
        Check(store.Current.Launcher.AdminApps.SequenceEqual(entries), "设置读写保留应用 id");
        var transfer = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        LocalSettingsFields.Apply(transfer, null);
        LocalSettingsFields.Apply(transfer, old);
        Check(transfer.Deserialize(SettingsJson.Default.PaneSettings)!.Launcher.AdminApps.SequenceEqual(entries),
            "导出导入的本机字段处理保留 AdminApps");

        var platform = new ActionPlatform();
        var executor = new FakeExecutor();
        var process = new Process037();
        using var search = new LauncherSearch(store, new Index076([app, packaged, plain]), new FakeFiles(),
            executor, process: process, actionPlatform: platform);
        await search.Indexing;
        async Task<LauncherItem> Row(string query, string group) =>
            (await search.SearchAsync(query, default)).Items.Single(i => i.Id == app.Id && i.Group == group);
        async Task Elevated(LauncherItem row)
        {
            int before = process.Requests.Count;
            await search.ExecuteAsync(row, default);
            Check(process.Requests.Count == before + 1 && process.Requests[^1].Elevated &&
                executor.Calls.Count == 0, "提权打开 " + row.Group);
        }

        var first = await Row("desk", LauncherGroups.Applications);
        process.Error = new Win32Exception(1223);
        await search.ExecuteAsync(first, default);
        Check(!(await search.SearchAsync("", default)).Items.Any(i => i.Group == LauncherGroups.Recent)
            && search.SearchMemory.Ranked("desk").Length == 0, "拒绝 UAC 不记最近和搜索学习");
        process.Error = null;
        await Elevated(first);
        await Elevated(await Row("desk", LauncherGroups.Best));
        await Elevated(await Row("", LauncherGroups.Pinned));
        await Elevated(await Row("", LauncherGroups.Recent));

        int launches = process.Requests.Count;
        nint activated = 0;
        Task Hotkey(List<nint> windows) => search.PressHotkeyAsync(app, (_, launch) =>
            AppHotkeyWindows.SwitchOrLaunch(windows, 0, _ => false, _ => {}, w => activated = w, launch));
        await Hotkey([]);
        await Hotkey([17]);
        Check(process.Requests.Count == launches + 1 && process.Requests[^1].Elevated && activated == 17,
            "快捷键只在没有窗口时提权启动");

        var term = ApplicationSource.Item(packaged, LauncherGroups.Applications);
        Check(!(await search.GetActionsAsync(term, default)).Contains(LauncherItemAction.RunAsAdmin), "无别名隐藏提权");
        const string alias = @"C:\Fixture\WindowsApps\Terminal_family\wt.exe";
        platform.ExecutionAliases[packaged.Launch] = alias;
        Check((await search.GetActionsAsync(term, default)).Contains(LauncherItemAction.RunAsAdmin), "有别名提供提权");
        await search.ExecuteAsync(term, default);
        Check(process.Requests[^1] is { Elevated: true, FileName: alias }, "商店应用提权使用所属别名");
        await search.ExecuteAsync(ApplicationSource.Item(plain, LauncherGroups.Applications), default);
        Check(executor.Calls.SequenceEqual(["open:plain.exe:False"]), "普通打开");
        store.Save(store.Current with { Launcher = store.Current.Launcher with { AdminApps = [] } });
        int beforeNormal = process.Requests.Count;
        foreach (var row in new[] { first, await Row("desk", LauncherGroups.Best),
            await Row("", LauncherGroups.Pinned), await Row("", LauncherGroups.Recent) })
        {
            int before = executor.Calls.Count;
            await search.ExecuteAsync(row, default);
            Check(executor.Calls.Count == before + 1 && executor.Calls[^1] == "open:desk.lnk:False" &&
                process.Requests.Count == beforeNormal, "取消设置后普通打开 " + row.Group);
        }
        Console.WriteLine($"PASS 146 assertions={checks};realExecutions=0");
    }
}
