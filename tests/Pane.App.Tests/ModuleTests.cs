using Pane.Contracts;
using Pane.Hosting;
using Pane.Services.Modules;
using Pane.Settings;

static class ModuleTests
{
    sealed class Fake(string id, bool enabled = false, params ModuleDependency[] dependencies) : ICapabilityModule
    {
        public ModuleInfo Info { get; } = new(id, ModuleCategory.Files, "test", enabled, dependencies);
        public bool Fail, FailStop;
        public int Starts, Stops;
        public Task StartAsync(CancellationToken token) { Starts++; if (Fail) throw new IOException("fixture failure"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Stops++; if (FailStop) throw new IOException("restore failure"); return Task.CompletedTask; }
    }
    internal static async Task<int> Run(PaneSettings defaults)
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        string root = Path.Combine(AppContext.BaseDirectory, "results", "modules-" + Guid.NewGuid().ToString("N"));
        var infos = new[] { "launcher", "clipboard", "gestures", "super-panel", "ai", "folder-jump", "rename" }
            .Select(id => new ModuleInfo(id, ModuleCategory.Files, "test", true, [])).ToArray();
        for (int bits = 0; bits < 8; bits++)
        {
            var legacy = defaults with { Modules = null, Clipboard = defaults.Clipboard with { Enabled = (bits & 1) != 0 },
                SuperPanel = defaults.SuperPanel with { Enabled = (bits & 2) != 0 }, FolderJump = defaults.FolderJump with { Enabled = (bits & 4) != 0 } };
            var migrated = ModuleSettings.Normalize(legacy, infos);
            Check(migrated.Modules!.Count == 7, "migration contains all modules");
            Check(migrated.Modules["clipboard"] == legacy.Clipboard.Enabled && migrated.Modules["super-panel"] == legacy.SuperPanel.Enabled && migrated.Modules["folder-jump"] == legacy.FolderJump.Enabled, "legacy combinations");
            Check(new[] { "launcher", "gestures", "ai", "rename" }.All(id => migrated.Modules[id]), "new module defaults");
            var store = new SettingsStore(Path.Combine(root, bits.ToString()), defaults); store.Save(migrated); store.Load();
            Check(store.Current.Modules!["clipboard"] == legacy.Clipboard.Enabled, "migration persisted");
        }
        var unknown = ModuleSettings.Normalize(defaults with { Modules = new() { ["future"] = false, ["clipboard"] = false } }, infos);
        int migrationStart = checks;
        foreach (bool disabled in new[] { true, false })
        {
            var aiStore = new SettingsStore(Path.Combine(root, "ai-" + disabled), defaults);
            aiStore.Save(defaults with { Modules = null, Ai = defaults.Ai with { Disabled = disabled } });
            aiStore.Load();
            var aiHost = new ModuleHost([new Fake("ai", true)], aiStore, () => false);
            await aiHost.InitializeAsync(default);
            Check(aiHost.StateOf("ai").Enabled == !disabled, "AI legacy choice migrated");
            Check(aiStore.Current.Modules!["ai"] == !disabled && aiStore.Current.Ai.Enabled == !disabled, "AI fields agree");
            await aiHost.ApplyAsync(aiHost.Preview("ai", true), default);
            aiStore.Load();
            Check(!aiStore.Current.Ai.Disabled, "AI re-enabled and persisted");
            aiStore.Save(aiStore.Current with { Modules = new() { ["ai"] = false }, Ai = defaults.Ai with { Disabled = false } });
            aiStore.Load();
            Check(aiStore.Current.Ai.Disabled, "settings store independently synchronizes AI");
        }
        foreach (bool? enabled in new bool?[] { null, true, false })
        {
            var gestureStore = new SettingsStore(Path.Combine(root, "gesture-" + enabled), defaults);
            gestureStore.Save(defaults with { Modules = enabled is null ? null : new() { ["gestures"] = enabled.Value, ["future"] = false } });
            string path = Path.Combine(gestureStore.DirectoryPath, "gestures.json");
            var config = Pane.Gestures.GestureConfigStore.Defaults;
            Pane.Gestures.GestureConfigStore.Save(path, config with { Settings = config.Settings with { EnabledButtons = Pane.Gestures.GestureButton.None } });
            ModuleSettings.Initialize(gestureStore, infos);
            gestureStore.Load();
            Check(gestureStore.Current.Modules!["gestures"] == (enabled ?? false), "gesture choice migrated or retained");
            var saved = Pane.Gestures.GestureConfigStore.Load(path);
            Check(saved.Settings.EnabledButtons == Pane.Gestures.GestureButton.Right, "gesture trigger repaired on disk");
            Check(saved.Global.Count == config.Global.Count && saved.Settings == config.Settings with { TrailColors = saved.Settings.TrailColors }, "gesture bindings and settings retained");
            if (enabled != null) Check(!gestureStore.Current.Modules["future"], "unknown choice retained during repair");
        }
        Console.WriteLine($"PASS legacy module migration assertions={checks - migrationStart}");
        Check(unknown.Modules!["future"] == false && unknown.Modules["launcher"], "unknown retained and missing default");
        Check(!unknown.Clipboard.Enabled && unknown.SuperPanel.Enabled && unknown.FolderJump.Enabled, "legacy follows modules");
        var storage = new SettingsStore(Path.Combine(root, "catalog"), defaults);
        storage.Save(defaults with { Modules = new() { ["future"] = true } });
        var parent = new Fake("parent");
        var child = new Fake("child", false, new ModuleDependency("parent", DependencyKind.Required));
        var leaf = new Fake("leaf", false, new ModuleDependency("child", DependencyKind.Required));
        var optional = new Fake("optional", false, new ModuleDependency("parent", DependencyKind.Optional));
        var failing = new Fake("failing") { Fail = true };
        var clip = new Fake("clipboard");
        var host = new ModuleHost([parent, child, leaf, optional, failing, clip], storage, () => true);
        int changed = 0; host.Changed += (_, _) => changed++;
        await host.InitializeAsync(default);
        var preview = host.Preview("leaf", true);
        Check(preview.AlsoEnable.SequenceEqual(["parent", "child"]) && preview.AlsoDisable.Count == 0, "hard dependencies topological order");
        Check(parent.Starts == 0 && !host.IsActive("leaf"), "preview has no side effects");
        await host.ApplyAsync(preview, default);
        Check(new[] { "parent", "child", "leaf" }.All(host.IsActive), "enable hard dependency closure");
        Check(!host.IsActive("optional"), "optional not enabled by dependency");
        await host.ApplyAsync(host.Preview("optional", true), default);
        preview = host.Preview("parent", false);
        Check(preview.AlsoDisable.SequenceEqual(["leaf", "child"]), "disable dependent closure");
        await host.ApplyAsync(preview, default);
        Check(new[] { "parent", "child", "leaf" }.All(id => host.StateOf(id).Status == ModuleStatus.Disabled), "closure disabled");
        Check(host.IsActive("optional"), "optional remains running");
        await host.ApplyAsync(host.Preview("failing", true), default);
        Check(host.StateOf("failing") is { Enabled: true, Status: ModuleStatus.Failed, Problem: "fixture failure" }, "failure recorded");
        Check(failing.Stops == 2 && host.IsActive("optional"), "partial start cleaned and others unaffected");
        failing.Fail = false; await host.ApplyAsync(host.Preview("failing", true), default);
        Check(host.IsActive("failing"), "failed module can retry");
        Check(storage.Current.Modules!["future"], "future retained through apply");
        Check(host.Preview("clipboard", false).RestoresSystemSetting, "restore preview");
        await host.ApplyAsync(host.Preview("clipboard", true), default);
        clip.FailStop = true; await host.ApplyAsync(host.Preview("clipboard", false), default);
        Check(storage.Current.Modules["clipboard"] && host.StateOf("clipboard").Enabled && host.StateOf("clipboard").Status == ModuleStatus.Failed, "restore failure does not persist disabled");
        clip.FailStop = false; await host.ApplyAsync(host.Preview("clipboard", false), default);
        Check(!storage.Current.Clipboard.Enabled && host.StateOf("clipboard").Status == ModuleStatus.Disabled, "restore retry");
        Check(changed >= 10, "change events emitted");
        var key = new Hotkey(KeyModifiers.None, 162) { DoubleTap = true };
        Check(ShellHotkeyBindings.DoubleTapKeys(key, key, true, false, false) == (0u, 162u), "clipboard owns duplicate when launcher disabled");
        List<int> registered = [1, 2], removed = [], requested = [];
        ShellHotkeyBindings.Rebind(registered, removed.Add, (id, _, _) => requested.Add(id), new(KeyModifiers.Alt, 32), new(KeyModifiers.Ctrl, 86), false, false, false);
        Check(removed.Count == 2 && requested.Count == 0, "all disabled unregisters every chord");
        return checks;
    }
}
