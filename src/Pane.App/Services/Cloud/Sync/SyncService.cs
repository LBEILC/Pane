using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Cloud;

internal sealed partial class SyncService : ISyncService, IClipboardCloud, IAsyncDisposable
{
    readonly SettingsStore store;
    readonly AccountService account;
    readonly string file;
    readonly object gate = new();
    Channel<bool>? wake;
    Timer? settingsTimer, usageTimer;
    readonly System.Collections.Concurrent.ConcurrentQueue<(PaneSettings Before,PaneSettings After,long At)> edits = new();
    CancellationTokenSource? run;
    Task worker = Task.CompletedTask;
    bool disposed;
    string? activeToken;
    SyncOptions? activeOptions;
    SyncMergeChoice? choice;
    readonly Dictionary<string,long> editTimes = [];
    PaneSettings? observed;
    long usageDue;
    TaskCompletionSource? immediate;
    public SyncState State { get; private set; }
    public SyncPauseReason? PauseReason { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public SyncOptions Options => store.Current.SyncOptions;
    public IReadOnlyList<CloudDevice> OtherDevices { get; private set; } = [];
    public event EventHandler? Changed;

    internal SyncService(SettingsStore store, AccountService account, IEnumerable<ISyncCategory>? extensions = null)
    {
        this.store = store;
        this.account = account;
        clipboard = Pane.Services.Clipboard.ClipboardCloudLink.For(store);
        categories = (extensions ?? []).Concat(new[]{"clip","cliptags","control"}.Select(c=>new ClipboardCategory(this,c))).ToDictionary(c => c.Name);
        clipboard.Owner=()=>activeToken==null?null:account.SyncIdentity().Owner;
        clipboard.DeviceName=id=>OtherDevices.FirstOrDefault(d=>d.Id==id)?.Name;
        clipboard.Wake=()=>{if(Options.Clipboard)Wake();};
        file = Path.Combine(store.DirectoryPath, "cloud", "sync.json");
        store.Changed += SettingsChanged;
        store.UsageChanged += UsageChanged;
        account.Changed += AccountChanged;
        Restart();
    }
    void Wake() => wake?.Writer.TryWrite(true);
    void SettingsChanged(object? sender, EventArgs args)
    {
        Restart();
        lock(gate) if (run != null && Options.Settings)
        {
            var current = store.Current;
            if (observed != null && !ReferenceEquals(observed,current))
            {
                edits.Enqueue((observed,current,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                settingsTimer!.Change(2000, Timeout.Infinite);
            }
            observed = current;
        }
    }
    void UsageChanged()
    {
        lock(gate) if (run != null && Options.Usage)
        {
            Interlocked.Exchange(ref usageDue,Environment.TickCount64 + 30000);
            usageTimer!.Change(30000, Timeout.Infinite);
        }
    }
    void AccountChanged(object? sender, EventArgs args) => Restart(State == SyncState.Paused && PauseReason == SyncPauseReason.NoSubscription);
    void Status(SyncState state, SyncPauseReason? pause = null, string? error = null)
    {
        State = state;
        PauseReason = pause;
        ErrorCode = error;
        Changed?.Invoke(this,EventArgs.Empty);
    }
    void Restart(bool force = false)
    {
        lock(gate)
        {
            if (disposed) return;
            var options = Options;
            var identity = account.SyncIdentity();
            string? token = options.Enabled && store.Current.Modules?.GetValueOrDefault("cloud") == true ? identity.Token : null;
            if (!force && activeToken == token && Equals(activeOptions,options) && (token != null || State != SyncState.Paused && account.State != AccountState.NeedsSignIn)) return;
            run?.Cancel();
            run = null;
            immediate?.TrySetCanceled();
            immediate = null;
            if (activeToken != token) choice = null;
            activeToken = token;
            activeOptions = options;
            observed = store.Current;
            edits.Clear();
            store.OtherCounts = new Dictionary<string,int>();
            store.OtherCommands = [];
            OtherDevices = [];
            if (token == null)
            {
                bool expired = options.Enabled && account.State == AccountState.NeedsSignIn;
                Status(expired ? SyncState.Paused : SyncState.Off, expired ? SyncPauseReason.NeedsSignIn : null);
                return;
            }
            wake ??= Channel.CreateBounded<bool>(1);
            settingsTimer ??= new(_ => Wake()); usageTimer ??= new(_ => { Interlocked.Exchange(ref usageDue,0); Wake(); });
            var lifetime = run = new();
            var previous = worker;
            worker = Task.Run(async () =>
            {
                try
                {
                    await previous;
                    lifetime.Token.ThrowIfCancellationRequested();
                    editTimes.Clear();
                    await Run(token,identity.Key!,identity.Owner!,options,lifetime.Token);
                }
                catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
                finally { lock(gate) { if (ReferenceEquals(run,lifetime)) run = null; lifetime.Dispose(); } }
            });
        }
    }
    public Task ApplyAsync(SyncOptions options, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        store.UpdateFromSync(s => s with { SyncOptions = options });
        Restart();
    },ct);
    public Task SyncNowAsync(CancellationToken ct)
    {
        lock(gate)
        {
            if (run == null || PauseReason == SyncPauseReason.NeedsMergeChoice) return Task.CompletedTask;
            immediate ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Wake();
            return immediate.Task.WaitAsync(ct);
        }
    }
    public Task ResolveMergeAsync(SyncMergeChoice selected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(gate) if (PauseReason == SyncPauseReason.NeedsMergeChoice)
        {
            choice = selected;
            Restart(true);
        }
        return Task.CompletedTask;
    }
    readonly Dictionary<string,ISyncCategory> categories;
    string clipboardDevice="";

    async Task Run(string token, string encodedKey, string owner, SyncOptions options, CancellationToken ct)
    {
        var device = clipboardDevice = account.DeviceId;
        var key = CloudKeys.Decode(encodedKey);
        var keyId = CloudKeys.Check(key);
        var delay = 5;
        bool fetchDevices = true;
        SyncJournal? journal = null;
        TaskCompletionSource? pending = null;
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    lock(gate)
                    {
                        while(wake!.Reader.TryRead(out _)) { }
                        if (pending == null) { pending = immediate; immediate = null; }
                    }
                    Status(SyncState.Syncing);
                    journal ??= SyncJournal.Read(file);
                    // 同一账号以 Fresh 重设密码会清空云端并更换密钥，旧游标和派生 id 都不能再用。
                    if (journal.Account == owner && journal.KeyId != keyId) journal = new();
                    LastSuccessAt = journal.LastSuccess;
                    bool initial = !journal.Initialized || journal.Account != owner || journal.SettingsEnabled != options.Settings || journal.UsageEnabled != options.Usage || journal.ClipboardEnabled != ClipboardEnabled(options);
                    if (initial)
                    {
                        if (!await Initial(journal, owner, token, key, device, options, ct)) return;
                    }
                    await Exchange(journal, token, key, device, options, initial || pending != null, ct);
                    if (fetchDevices || options.Clipboard && pending != null)
                    {
                        var devices = await account.GetDevicesAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        OtherDevices = devices.Value?.Where(d => !d.Current).ToArray() ?? [];
                        fetchDevices = !devices.Ok;
                        clipboard.History?.StorageNotify();
                    }
                    journal.LastSuccess = LastSuccessAt = DateTimeOffset.UtcNow;
                    journal.Save(file);
                    delay = 5;
                    Status(SyncState.Idle);
                    pending?.TrySetResult(); pending = null;
                    lock(gate) if (immediate != null) continue;
                    await Wait(journal.Cursor, token, ct);
                }
                catch (AccountService.Failure e) when(e.Status == 410 && e.Code == "cursor_expired")
                { journal!.Initialized = false; journal.Save(file); }
                catch (AccountService.Failure e) when(e.Status == 401)
                {
                    await account.ExpireAsync(token,ct);
                    return;
                }
                catch (AccountService.Failure e) when(e.Status == 426 || e.Code == "subscription_required")
                {
                    Status(SyncState.Paused,e.Status == 426 ? SyncPauseReason.ClientOutdated : SyncPauseReason.NoSubscription);
                    return;
                }
                catch(Exception e) when(!ct.IsCancellationRequested)
                {
                    var f = e as AccountService.Failure;
                    Status(SyncState.Error,error:f?.Code ?? (e is IOException or HttpRequestException or OperationCanceledException ? "network" : "sync_data"));
                    journal = null;
                    await Task.Delay(TimeSpan.FromSeconds((int?)f?.Detail?["retry_after"] ?? delay),ct);
                    delay = Math.Min(300,delay*2);
                }
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            pending?.TrySetResult();
            lock(gate) if (!ct.IsCancellationRequested) { immediate?.TrySetResult(); immediate = null; }
        }
    }
    async Task Wait(long cursor, string token, CancellationToken ct)
    {
        while(true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var local = wake!.Reader.WaitToReadAsync(wait.Token).AsTask();
            var remote = account.SyncRequest("sync/wait?cursor=" + cursor,null,token,wait.Token);
            var first = await Task.WhenAny(local,remote);
            await wait.CancelAsync();
            try
            {
                var reply = await remote;
                if ((long)reply["seq"]! > cursor) return;
                // 设备改名不推进记录游标，空闲长轮询到期时更新名称。
                if(ClipboardEnabled(Options))
                {
                    var devices=await account.GetDevicesAsync(ct);
                    if(devices.Ok)
                    {
                        var other=devices.Value!.Where(d=>!d.Current).ToArray();
                        if(!OtherDevices.SequenceEqual(other)){OtherDevices=other;clipboard.History?.StorageNotify();Changed?.Invoke(this,EventArgs.Empty);}
                    }
                }
            }
            catch(OperationCanceledException) when(local.IsCompletedSuccessfully) { return; }
            if(first == local) return;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate)
        {
            disposed = true;
            clipboard.Owner=null; clipboard.Wake=null; clipboard.DeviceName=null;
            immediate?.TrySetCanceled(); immediate = null;
            run?.Cancel();
            account.Changed -= AccountChanged;
            store.Changed -= SettingsChanged;
            store.UsageChanged -= UsageChanged;
            settingsTimer?.Dispose(); usageTimer?.Dispose();
        }
        await worker;
        Task[] transfers;
        lock(gate)transfers=fetching.Values.ToArray();
        await Task.WhenAll(transfers);
        store.OtherCounts = new Dictionary<string,int>();
        store.OtherCommands = [];
    }
}
