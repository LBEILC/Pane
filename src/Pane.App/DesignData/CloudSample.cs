using Pane.Contracts;

namespace Pane.DesignData;

public sealed class CloudSample : IAccountService, ISyncService, IAsyncDisposable
{
    const long MB = 1048576;
    const string Recovery = "7GYK-WFT0-8514-6H25-8S3M-GJAA-9D64-TKJF";
    static readonly CloudPlans Prices = new([
        new("pane_month", SubscriptionTier.Month, "9.90", 31, 1024 * MB),
        new("pane_year", SubscriptionTier.Year, "69.90", 366, 2048 * MB),
        new("pane_life", SubscriptionTier.Lifetime, "299.00", 0, 2048 * MB)], 7, 200 * MB, 100, 93);
    readonly List<CloudDevice> devices = [
        new("desktop", "书房台式机", true, new(2026,10,15,0,0,0,TimeSpan.Zero), DateTimeOffset.Now),
        new("laptop", "笔记本", false, new(2026,11,12,0,0,0,TimeSpan.Zero), DateTimeOffset.Now.AddDays(-3))];
    readonly Dictionary<string,CloudOrder> orders = [];
    readonly CancellationTokenSource lifetime = new();
    async Task Delay(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await Task.Delay(160, linked.Token);
    }
    public async ValueTask DisposeAsync() => await lifetime.CancelAsync();

    // 同步的演示状态（设计预览）：PANE_DESIGN_SYNC = off（默认）/ idle / error / expired / needssignin / merge；不是 off 时同步和剪贴板同步都开着
    static readonly string syncMode = Environment.GetEnvironmentVariable("PANE_DESIGN_SYNC") ?? "off";
    SyncState syncState = syncMode switch { "idle" => SyncState.Idle, "error" => SyncState.Error, "expired" or "needssignin" or "merge" => SyncState.Paused, _ => SyncState.Off };
    SyncState ISyncService.State => Options.Enabled ? syncState == SyncState.Off ? SyncState.Idle : syncState : SyncState.Off;
    public SyncPauseReason? PauseReason => syncMode switch { "expired" => SyncPauseReason.NoSubscription, "needssignin" => SyncPauseReason.NeedsSignIn, "merge" => SyncPauseReason.NeedsMergeChoice, _ => null };
    public string? ErrorCode => syncMode == "error" ? "network" : null;
    public DateTimeOffset? LastSuccessAt { get; private set; } = DateTimeOffset.Now.AddMinutes(-2);
    public SyncOptions Options { get; private set; } = syncMode == "off" ? new() : new(Enabled: true, Clipboard: true, ExcludedApps: ["KeePassXC.exe"]);
    public IReadOnlyList<CloudDevice> OtherDevices => State == AccountState.SignedIn ? devices.Where(d => !d.Current).ToArray() : [];
    public async Task ApplyAsync(SyncOptions options, CancellationToken ct) => await Update(ct, () => Options = options);
    public async Task SyncNowAsync(CancellationToken ct) => await Update(ct, () => { syncState = SyncState.Idle; LastSuccessAt = DateTimeOffset.Now; });
    public async Task ResolveMergeAsync(SyncMergeChoice choice, CancellationToken ct) => await Update(ct, () => syncState = SyncState.Idle);
    public AccountState State { get; private set; }
    public CloudProfile? Profile { get; private set; }
    public string? LastEmail { get; private set; }
    public bool HasLocalKey => State != AccountState.SignedOut;
    public string AccountCenterUrl => "https://pane.lubeiluchen.com/account/#profile";
    public event EventHandler? Changed;

    public CloudSample()
    {
        var mode = Environment.GetEnvironmentVariable("PANE_DESIGN_ACCOUNT");
        if (mode is "year" or "lifetime" or "new" or "expired")
        {
            Login(mode == "new" ? "new@example.com" : "demo@example.com", mode == "new" ? "新用户" : "示例用户");
            if (mode is "year" or "lifetime")
            {
                Activate(mode == "year" ? SubscriptionTier.Year : SubscriptionTier.Lifetime);
                Profile = Profile! with { RecoveryCreatedAt = new DateTimeOffset(2026,11,2,0,0,0,TimeSpan.FromHours(8)) };
            }
            if (mode == "expired") Profile = Profile! with { Subscription = Profile.Subscription with {
                Status = SubscriptionStatus.Expired, TrialAvailable = false, RetainUntil = DateTimeOffset.Now.AddDays(88) } };
        }
        if (mode == "needssignin") { LastEmail = "demo@example.com"; State = AccountState.NeedsSignIn; }
    }
    void Login(string email, string name)
    {
        LastEmail = email;
        State = AccountState.SignedIn;
        Profile = new("demo", email, name, null, [], DateTimeOffset.Now.AddMonths(-1),
            new(SubscriptionStatus.None, null, null, false, null, null, true, null), devices.Count, 5, null);
    }
    void Activate(SubscriptionTier tier)
    {
        if (Profile == null) return;
        var now = DateTimeOffset.Now;
        bool life = tier == SubscriptionTier.Lifetime, trial = tier == SubscriptionTier.Trial;
        var plan = Prices.Plans.FirstOrDefault(p => p.Tier == tier);
        var nextMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.FromHours(8)).AddMonths(1);
        Profile = Profile with { Subscription = new(trial ? SubscriptionStatus.Trial : SubscriptionStatus.Active, tier,
            life ? null : now.AddDays(plan?.Days ?? 7), life, new(plan?.QuotaBytes ?? Prices.TrialQuotaBytes, 186*MB, nextMonth),
            new(1024*MB,64*MB), false, null), Badges = tier is SubscriptionTier.Lifetime or SubscriptionTier.Year ? [new("founder",7,now)] : [] };
    }
    async Task<CloudResult> Update(CancellationToken ct, Action action)
    {
        await Delay(ct);
        action();
        Changed?.Invoke(this, EventArgs.Empty);
        return CloudResult.Success;
    }
    public async Task<CloudResult> SendCodeAsync(string email, CodePurpose purpose, CancellationToken ct)
    { await Delay(ct); return new(RetryAfter:60); }
    public async Task<CloudResult<DeviceLimitHit>> RegisterAsync(string email,string code,string password,string? nickname,CloudConsents consents,CancellationToken ct)
    {
        await Delay(ct);
        if (code != "123456") return new(null,"bad_code",AttemptsLeft:4);
        if (!consents.CrossBorder) return new(null,"consent_required");
        Login(email,nickname ?? "新用户"); Changed?.Invoke(this,EventArgs.Empty); return new(null);
    }
    public async Task<CloudResult<DeviceLimitHit>> SignInAsync(string email,string password,CancellationToken ct)
    {
        await Update(ct, () => { Login(email,"示例用户"); Activate(SubscriptionTier.Year); });
        return new(null);
    }
    public Task<CloudResult> RemoveDeviceAndSignInAsync(string deviceId,CancellationToken ct) => Update(ct, () => Login(LastEmail ?? "demo@example.com","示例用户"));
    public Task<CloudResult> SignOutAsync(CancellationToken ct) => Update(ct, () => { Profile=null; State=AccountState.SignedOut; orders.Clear(); });
    public async Task<CloudResult> SetFounderPublicAsync(bool isPublic, CancellationToken ct)
    {
        if (Profile?.Badges.Any(b => b.Id == "founder") != true) return new("not_founder");
        return await Update(ct, () => Profile = Profile! with { Badges = Profile.Badges.Select(b =>
            b.Id == "founder" ? b with { Public = isPublic } : b).ToArray() });
    }
    public async Task<CloudResult<CloudMemberGroup>> GetMemberGroupAsync(CancellationToken ct)
    {
        await Delay(ct);
        return Profile?.Subscription.Status == SubscriptionStatus.Active
            ? new(new("100000000", null, "K7M2-9QXA")) : new(null, "subscription_required");
    }
    public Task<CloudResult> RefreshAsync(CancellationToken ct) => Update(ct, () => {});
    public async Task<CloudResult<DeviceLimitHit>> ResetPasswordAsync(string email,string code,string newPassword,ResetMode mode,string? recoveryCode,CancellationToken ct)
    {
        await Delay(ct);
        if (code!="123456") return new(null,"bad_code",AttemptsLeft:4);
        if (mode==ResetMode.LocalKey && !HasLocalKey) return new(null,"key_mismatch");
        if (mode==ResetMode.RecoveryCode && !string.Equals(recoveryCode,Recovery,StringComparison.OrdinalIgnoreCase)) return new(null,"bad_recovery");
        await Update(ct, () => Login(email,"示例用户"));
        return new(null);
    }
    public Task<CloudResult> ChangePasswordAsync(string currentPassword,string newPassword,CancellationToken ct) => Update(ct, () => {});
    public async Task<CloudResult<string>> CreateRecoveryCodeAsync(CancellationToken ct)
    {
        await Update(ct, () => Profile=Profile! with { RecoveryCreatedAt=DateTimeOffset.Now });
        return new(Recovery);
    }
    public Task<CloudResult> DeleteRecoveryCodeAsync(CancellationToken ct) => Update(ct, () => Profile=Profile! with { RecoveryCreatedAt=null });
    public async Task<CloudResult<IReadOnlyList<CloudDevice>>> GetDevicesAsync(CancellationToken ct)
    { await Delay(ct); return new(devices.ToArray()); }
    public Task<CloudResult> RenameDeviceAsync(string deviceId,string name,CancellationToken ct) => Update(ct, () =>
    { int i=devices.FindIndex(d=>d.Id==deviceId); if(i>=0) devices[i]=devices[i] with { Name=name }; });
    public Task<CloudResult> RemoveDeviceAsync(string deviceId,CancellationToken ct) => Update(ct, () =>
    { devices.RemoveAll(d=>d.Id==deviceId); Profile=Profile! with { DeviceCount=devices.Count }; });
    public async Task<CloudResult<CloudPlans>> GetPlansAsync(CancellationToken ct)
    { await Delay(ct); return new(Prices); }
    public async Task<CloudResult<CloudOrder>> CreateOrderAsync(string planId,CancellationToken ct)
    {
        await Delay(ct);
        var plan = Prices.Plans.FirstOrDefault(p=>p.Id==planId);
        if (plan==null) return new(null,"not_found");
        var now=DateTimeOffset.UtcNow;
        var order=new CloudOrder(Guid.NewGuid().ToString("N"),planId,CloudOrderStatus.Pending,plan.Price,now,null,
            // 演示：长度和真实的爱发电付款链接差不多（商品、规格、订单号都是假的），二维码的疏密照真实的看
            $"https://ifdian.net/order/create?product_type=1&plan_id=0123456789abcdef0123456789abcdef&sku=%5B%7B%22sku_id%22%3A%22fedcba9876543210fedcba9876543210%22%2C%22count%22%3A1%7D%5D&custom_order_id=P{now:yyyyMMddHHmmss}DEMO",now.AddHours(1));
        orders.Add(order.OrderNo,order); return new(order);
    }
    public async Task<CloudResult<CloudOrder>> GetOrderAsync(string orderNo,CancellationToken ct)
    {
        await Delay(ct);
        if (!orders.TryGetValue(orderNo,out var order)) return new(null,"not_found");
        if (order.Status==CloudOrderStatus.Pending && DateTimeOffset.UtcNow-order.CreatedAt>=TimeSpan.FromSeconds(6))
        {
            orders[orderNo]=order=order with { Status=CloudOrderStatus.Paid, PaidAt=DateTimeOffset.UtcNow };
            Activate(Prices.Plans.First(p=>p.Id==order.PlanId).Tier); Changed?.Invoke(this,EventArgs.Empty);
        }
        return new(order);
    }
    public Task<CloudResult> RedeemAsync(string code,CancellationToken ct) => Update(ct, () => Activate(SubscriptionTier.Year));
    public Task<CloudResult> ClaimTrialAsync(CancellationToken ct) => Update(ct, () => Activate(SubscriptionTier.Trial));
    public Task<CloudResult> DeleteAccountAsync(string password,CancellationToken ct) => SignOutAsync(ct);
}
