using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Modules;

// 模块只持有宿主；宿主负责跨能力转发和共享资源。
internal interface ICapabilityModule
{
    ModuleInfo Info { get; }
    Task StartAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
}

internal sealed class ModuleHost : IModuleCatalog
{
    readonly Dictionary<string, ICapabilityModule> modules;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModuleState> states = new();
    readonly SettingsStore store;
    readonly Func<bool> restores;
    readonly Func<Func<Task>, Task> dispatch;
    readonly SemaphoreSlim serial = new(1);
    public IReadOnlyList<ModuleInfo> Modules { get; }
    public event EventHandler<string>? Changed;
    public ModuleHost(IEnumerable<ICapabilityModule> entries, SettingsStore store, Func<bool> restores,
        Func<Func<Task>, Task>? dispatch = null)
    {
        modules = entries.ToDictionary(m => m.Info.Id, StringComparer.Ordinal);
        Modules = modules.Values.Select(m => m.Info).ToArray();
        this.store = store; this.restores = restores; this.dispatch = dispatch ?? (work => work());
        foreach (var m in Modules) states[m.Id] = new(m.Id, false, ModuleStatus.Disabled);
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Validate(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ArgumentException("能力硬依赖成环"); // 不翻译：登记错误
            foreach (var dependency in modules[id].Info.Dependencies.Where(d => d.Kind == DependencyKind.Required)) Validate(dependency.ModuleId);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var m in Modules) Validate(m.Id);
    }
    public ModuleState StateOf(string id) => states[id];
    public bool IsActive(string id) => states.TryGetValue(id, out var s) && s.Status == ModuleStatus.Running;
    bool Enabled(string id) => store.Current.Modules?.GetValueOrDefault(id, modules[id].Info.DefaultEnabled) ?? modules[id].Info.DefaultEnabled;
    public ModuleChange Preview(string id, bool enable)
    {
        if (!modules.ContainsKey(id)) throw new ArgumentException("未知能力", nameof(id)); // 不翻译：调用错误
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var related = new List<string>();
        void Visit(string item)
        {
            var next = enable
                ? modules[item].Info.Dependencies.Where(d => d.Kind == DependencyKind.Required).Select(d => d.ModuleId)
                : Modules.Where(m => m.Dependencies.Any(d => d.Kind == DependencyKind.Required && d.ModuleId == item)).Select(m => m.Id);
            foreach (var n in next) if (seen.Add(n)) { Visit(n); if (Enabled(n) != enable) related.Add(n); }
        }
        Visit(id);
        return new(id, enable, enable ? related : [], enable ? [] : related, false,
            !enable && (id == ModuleIds.Clipboard || related.Contains(ModuleIds.Clipboard)) && restores());
    }
    public Task InitializeAsync(CancellationToken token) => dispatch(async () =>
    {
        ModuleSettings.Initialize(store, Modules);
        var attempted = new HashSet<string>();
        async Task Start(string id)
        {
            if (!attempted.Add(id)) return;
            foreach (var d in modules[id].Info.Dependencies.Where(d => d.Kind == DependencyKind.Required)) await Start(d.ModuleId);
            if (!Enabled(id)) SaveChoice(id, true);
            await Change(id, true, token, false);
        }
        foreach (var m in Modules)
            if (Enabled(m.Id)) await Start(m.Id);
            else await Change(m.Id, false, token, false);
    });
    public Task ApplyAsync(ModuleChange change, CancellationToken cancellationToken) => dispatch(async () =>
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            // 重新计算，不能信任过时预览或调用者构造的连带列表。
            var plan = Preview(change.Id, change.Enable);
            foreach (var id in plan.Enable ? plan.AlsoEnable : plan.AlsoDisable)
                if (!await Change(id, plan.Enable, cancellationToken)) return;
            await Change(plan.Id, plan.Enable, cancellationToken);
        }
        finally { serial.Release(); }
    });
    async Task<bool> Change(string id, bool enable, CancellationToken token, bool persist = true)
    {
        token.ThrowIfCancellationRequested();
        var module = modules[id];
        try
        {
            if (enable && persist) SaveChoice(id, true);
            if (enable && module.Info.Dependencies.Any(d => d.Kind == DependencyKind.Required && !IsActive(d.ModuleId)))
                throw new InvalidOperationException(Pane.Localization.Strings.Module_DependencyNotRunning);
            if (enable && !IsActive(id)) await module.StartAsync(token);
            if (!enable && (!persist || StateOf(id).Status != ModuleStatus.Disabled)) await module.StopAsync(token);
            if (!enable && persist) SaveChoice(id, false);
            states[id] = new(id, enable, enable ? ModuleStatus.Running : ModuleStatus.Disabled);
        }
        catch (Exception e)
        {
            if (enable) { try { await module.StopAsync(CancellationToken.None); } catch { } }
            states[id] = new(id, Enabled(id), ModuleStatus.Failed, e.Message.Split('\n')[0]);
            Changed?.Invoke(this, id); return false;
        }
        Changed?.Invoke(this, id); return true;
    }
    void SaveChoice(string id, bool enable)
    {
        var values = new Dictionary<string, bool>(store.Current.Modules ?? []) { [id] = enable };
        store.Save(ModuleSettings.Normalize(store.Current with { Modules = values }, Modules));
    }
}

internal static class ModuleSettings
{
    public static void Initialize(SettingsStore store, IReadOnlyList<ModuleInfo> infos)
    {
        var path = Path.Combine(store.DirectoryPath, "gestures.json");
        var config = Pane.Gestures.GestureConfigStore.LoadOrDefaults(path);
        bool repair = config.Settings.EnabledButtons == Pane.Gestures.GestureButton.None;
        bool migrate = store.Current.Modules == null;
        var value = Normalize(store.Current, infos);
        if (migrate && repair) value.Modules![ModuleIds.Gestures] = false;
        // 先保存停用选择；即使手势文件写入中断，下次启动也不会意外启用。
        store.Save(value);
        if (repair) Pane.Gestures.GestureConfigStore.Save(path, config with {
            Settings = config.Settings with { EnabledButtons = Pane.Gestures.GestureButton.Right } });
    }

    public static PaneSettings Normalize(PaneSettings value, IReadOnlyList<ModuleInfo> infos)
    {
        var values = new Dictionary<string, bool>(value.Modules ?? [], StringComparer.Ordinal);
        foreach (var info in infos)
            if (!values.ContainsKey(info.Id)) values[info.Id] = value.Modules == null ? info.Id switch
            {
                ModuleIds.Clipboard => value.Clipboard.Enabled,
                ModuleIds.SuperPanel => value.SuperPanel.Enabled,
                ModuleIds.FolderJump => value.FolderJump.Enabled,
                ModuleIds.Ai => !value.Ai.Disabled,
                _ => info.DefaultEnabled
            } : info.DefaultEnabled;
        return value with { Modules = values,
            Ai = value.Ai with { Disabled = !values.GetValueOrDefault(ModuleIds.Ai, true) },
            Clipboard = value.Clipboard with { Enabled = values.GetValueOrDefault(ModuleIds.Clipboard, true) },
            SuperPanel = value.SuperPanel with { Enabled = values.GetValueOrDefault(ModuleIds.SuperPanel, true) },
            FolderJump = value.FolderJump with { Enabled = values.GetValueOrDefault(ModuleIds.FolderJump, true) } };
    }
}
