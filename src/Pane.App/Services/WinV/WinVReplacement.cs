using Pane.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;

namespace Pane.Services.WinV;

// 工程内部边界；测试不得实例化 Windows 实现。
internal sealed record RegistryValue(bool Exists = false, string? Text = null, int? Number = null);
internal interface IWinVRegistry
{
    RegistryValue Read(bool history);
    void Write(bool history, RegistryValue value);
}
internal interface IWinVHotkey
{
    Task<bool> SetAsync(bool enabled);
}
internal interface IWinVExplorer
{
    string Identity { get; }
    Task RestartAsync(CancellationToken cancellationToken);
}
internal sealed record WinVJournal
{
    public int Version { get; set; }
    public bool Enabled { get; set; }
    public bool Restoring { get; set; }
    public bool HotkeysTouched { get; set; }
    public bool HistoryTouched { get; set; }
    public bool MayHaveReleased { get; set; }
    public string Explorer { get; set; } = "";
    public RegistryValue Hotkeys { get; set; } = new();
    public RegistryValue History { get; set; } = new();
}
[JsonSerializable(typeof(WinVJournal))]
internal partial class WinVJson : JsonSerializerContext { }

internal sealed class WinVReplacement : IWinVReplacement
{
    readonly IWinVRegistry registry;
    readonly IWinVExplorer explorer;
    readonly IWinVHotkey hotkey;
    readonly string file;
    readonly SemaphoreSlim gate = new(1);
    WinVJournal journal = new();
    bool loaded, historyDisabled;
    public WinVState State { get; private set; }
    public bool SystemHistoryDisabled => historyDisabled && !journal.Restoring;
    public string? Error { get; private set; }
    public event EventHandler? Changed;
    public void RegistrationChanged(bool registered)
    {
        if (!journal.Enabled || journal.Restoring) return;
        if (registered && !journal.MayHaveReleased)
        {
            journal.MayHaveReleased = true;
            _ = Task.Run(PersistAcquisitionAsync);
        }
        State = registered ? WinVState.On : WinVState.Pending;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    async Task PersistAcquisitionAsync()
    {
        // 后台宿主重新注册成功也记录下来；不在热键线程等待文件 IO。
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (journal.Enabled && !journal.Restoring) Save(); }
        catch (Exception e) { Error = string.Format(Strings.Settings_WinV_StateSaveError, e.Message); }
        finally { gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public WinVReplacement(string directory, IWinVRegistry registry, IWinVExplorer explorer, IWinVHotkey hotkey)
    { file = Path.Combine(directory, "winv-original.json"); this.registry = registry; this.explorer = explorer; this.hotkey = hotkey; }
    void Load()
    {
        if (loaded) return;
        if (File.Exists(file))
        {
            journal = JsonSerializer.Deserialize(File.ReadAllText(file), WinVJson.Default.WinVJournal) ?? throw new IOException(Strings.Settings_WinV_InvalidRecord);
            if (journal.Version != 1 || journal.Hotkeys == null || journal.History == null) throw new IOException(Strings.Settings_WinV_InvalidRecordVersion);
        }
        loaded = true;
    }
    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, journal, WinVJson.Default.WinVJournal); stream.Flush(true); }
        File.Move(file + ".tmp", file, true);
    }
    async Task Run(Func<Task> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { Error = null; Load(); token.ThrowIfCancellationRequested(); await action(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // TODO(design): 请复核系统操作失败的文案。
            Error = string.Format(Strings.Settings_WinV_SetupError, e.Message);
        }
        finally { gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public Task InitializeAsync(CancellationToken token = default) => Run(async () =>
    {
        if (journal.Restoring)
        {
            State = WinVState.RestorePending;
            RestoreValues();
            State = journal.Explorer != explorer.Identity ? WinVState.Off : WinVState.RestorePending;
            if (State == WinVState.Off) { journal.Restoring = false; Save(); }
        }
        else if (journal.Enabled)
        {
            State = WinVState.Pending;
            ApplyHotkeys();
            if (journal.HistoryTouched && registry.Read(true) == journal.History)
            { Save(); registry.Write(true, new(true, Number: 0)); }
            historyDisabled = journal.HistoryTouched && registry.Read(true) == new RegistryValue(true, Number: 0);
            if (journal.Explorer != explorer.Identity) { journal.MayHaveReleased = true; Save(); }
            await Register();
        }
    }, token);
    async Task Register()
    {
        bool registered = await hotkey.SetAsync(true);
        State = registered ? WinVState.On : WinVState.Pending;
        if (registered) { journal.MayHaveReleased = true; Save(); }
        else Error = Strings.Settings_WinV_Occupied;
    }
    public Task EnableAsync(bool restartExplorer, CancellationToken cancellationToken) => Run(async () =>
    {
        if (journal.Restoring) throw new IOException(Strings.Settings_WinV_RestoreFirst);
        if (State == WinVState.On) return;
        if (!journal.Enabled)
        {
            var original = registry.Read(false);
            if (original.Exists && original.Text == null) throw new IOException(Strings.Settings_WinV_InvalidHotkeys);
            journal = new() { Version = 1, Enabled = true, Hotkeys = original, Explorer = explorer.Identity,
                HotkeysTouched = !(original.Text ?? "").Contains('V', StringComparison.OrdinalIgnoreCase) };
            Save(); // 先落盘，再触碰系统；崩溃后可以重复恢复。
        }
        State = WinVState.Pending;
        ApplyHotkeys();
        if (restartExplorer)
        {
            journal.MayHaveReleased = true; Save();
            await explorer.RestartAsync(cancellationToken);
            await Register();
        }
    }, cancellationToken);
    void ApplyHotkeys()
    {
        // 只补完写前日志；重试不能覆盖替换期间用户新增的字符。
        if (journal.HotkeysTouched && registry.Read(false) == journal.Hotkeys)
        { Save(); registry.Write(false, new(true, (journal.Hotkeys.Text ?? "") + "V")); }
    }
    void RestoreValues()
    {
        if (journal.HotkeysTouched)
        {
            var current = registry.Read(false);
            string applied = (journal.Hotkeys.Text ?? "") + "V";
            if (current.Text == applied)
            {
                // 崩溃后才补做恢复时，当前桌面仍可能读的是修改值，不能误报 Off。
                journal.Explorer = explorer.Identity; Save();
                registry.Write(false, journal.Hotkeys);
            }
            else if (current.Text is {} text)
            {
                // 保留替换期间新增的其他字符，只移除最后一个 Pane 添加的 V。
                int index = text.LastIndexOf('V');
                if (index >= 0)
                { journal.Explorer = explorer.Identity; Save(); registry.Write(false, current with { Text = text.Remove(index, 1) }); }
            }
            journal.HotkeysTouched = false; Save();
        }
        if (journal.HistoryTouched)
        { registry.Write(true, journal.History); historyDisabled = false; journal.HistoryTouched = false; Save(); }
    }
    public Task DisableAsync(bool restartExplorer, CancellationToken cancellationToken) => Run(async () =>
    {
        if (!journal.Enabled && !journal.Restoring) return;
        bool needsRestart = journal.MayHaveReleased || journal.Explorer != explorer.Identity || journal.Restoring;
        journal.Enabled = false; journal.Restoring = true; journal.Explorer = explorer.Identity; Save();
        await hotkey.SetAsync(false);
        State = WinVState.RestorePending;
        RestoreValues();
        if (needsRestart && restartExplorer) await explorer.RestartAsync(cancellationToken);
        if (!needsRestart || restartExplorer) { State = WinVState.Off; journal.Restoring = false; Save(); }
    }, cancellationToken);
    public Task SetSystemHistoryDisabledAsync(bool disabled, CancellationToken cancellationToken) => Run(() =>
    {
        if (!journal.Enabled || journal.Restoring) throw new IOException(Strings.Settings_WinV_EnableRequired);
        if (disabled && !journal.HistoryTouched)
        { journal.History = registry.Read(true); journal.HistoryTouched = true; Save(); }
        if (disabled) { Save(); registry.Write(true, new(true, Number: 0)); historyDisabled = true; }
        else if (journal.HistoryTouched)
        { registry.Write(true, journal.History); historyDisabled = false; journal.HistoryTouched = false; Save(); }
        return Task.CompletedTask;
    }, cancellationToken);
    public async Task RestoreForUninstallAsync()
    {
        await DisableAsync(false, default);
        if (Error != null) throw new IOException(Error);
    }
}
