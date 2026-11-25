using Pane.Contracts;
using Pane.Services.Modules;
using Pane.Settings;
using Pane.Services.Updates;

namespace Pane.Services.Cloud;

internal sealed class CloudModule(Func<IAccountService> create, SettingsStore store) : ICapabilityModule
{
    // 2026-11-24 开卖：放出到「能力」页，默认启用（启用本身不联网，登录后才请求，docs/cloud.md「总体」）
    public ModuleInfo Info { get; } = new(ModuleIds.Cloud, ModuleCategory.Sync, "cloud", true,
        [new(ModuleIds.Clipboard, DependencyKind.Optional), new(ModuleIds.Launcher, DependencyKind.Optional)]);
    readonly object gate = new();
    ISyncService? sync;
    Memo.NoteStore? notes;
    internal void SetNotes(Memo.NoteStore? value)
    {
        lock (gate)
        {
            notes = value;
            (sync as SyncService)?.SetNotes(value);
        }
    }
    public IAccountService? Account { get; private set; }
    public ISyncService? Sync
    {
        get
        {
            lock(gate) return sync ??= Account is AccountService account ? new SyncService(store,account,notes:notes) : Account as ISyncService;
        }
    }
    void SettingsChanged(object? sender,EventArgs args)
    {
        if (store.Current.SyncOptions.Enabled) _ = Sync;
    }
    public Task StartAsync(CancellationToken token)
    {
        lock(gate) Account = create();
        if (Account is AccountService account)
        {
            UpdateService.ActiveCloudAccount = account;
            account.Changed += AccountChanged;
            AccountChanged(account, EventArgs.Empty);
        }
        store.Changed += SettingsChanged;
        SettingsChanged(this,EventArgs.Empty);
        return Task.CompletedTask;
    }
    void AccountChanged(object? sender, EventArgs args)
    {
        if (sender is AccountService account && account == Account)
            UpdateService.ApplyMemberDefault(store, account);
    }
    public async Task StopAsync(CancellationToken token)
    {
        store.Changed -= SettingsChanged;
        IAccountService? service;
        ISyncService? previous;
        lock(gate)
        {
            service = Account; Account = null;
            previous = sync; sync = null;
        }
        if (service is AccountService account)
        {
            account.Changed -= AccountChanged;
            Interlocked.CompareExchange(ref UpdateService.ActiveCloudAccount, null, account);
        }
        if (previous is SyncService engine) await engine.DisposeAsync();
        if (service is IAsyncDisposable disposable) await disposable.DisposeAsync();
    }
}
