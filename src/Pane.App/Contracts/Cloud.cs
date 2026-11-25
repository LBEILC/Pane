// Pane 云（2026-11-24 设计定义）：界面（设置「账号」分区、「数据」→「同步」组、启动器胶囊头像、剪贴板来源设备）和
// 账号服务、同步服务之间的接口。工程照着实现。
// 规则：docs/cloud.md；服务端接口：docs/cloud-api.md（账号、订阅、设备）、docs/cloud-sync-api.md（同步）。
// 这些服务只在「Pane 云」能力（ModuleIds.Cloud）启用时创建；停用时不发任何请求（docs/cloud.md「总体」）。
// 界面原型：prototype/settings.html#account。
namespace Pane.Contracts;

// ================= 账号 =================

/// <summary>
/// SignedOut：没登录（本机没有凭据）。
/// SignedIn：已登录，Profile 有值。
/// NeedsSignIn：本机有过登录，但会话失效了（在别处改了密码或邮箱、这台设备被移除、忘记密码重设过）。
///   界面提示「登录已失效，请重新登录」，邮箱预填。本机的数据密钥还在时，重新登录后同步接着进行。
/// </summary>
public enum AccountState { SignedOut, SignedIn, NeedsSignIn }

public enum SubscriptionStatus { None, Trial, Active, Expired }
public enum SubscriptionTier { Trial, Month, Year, Lifetime }

/// <summary>每月流量（同步下载和从 Pane 云下载更新共用）。ResetsAt：下个月 1 日北京时间零点。</summary>
public sealed record CloudQuota(long LimitBytes, long UsedBytes, DateTimeOffset ResetsAt);

/// <summary>云端原图存储（各档 1 GB）。</summary>
public sealed record CloudStorage(long LimitBytes, long UsedBytes);

/// <param name="Tier">现在落在的那一段的档位；None、Expired 时为 null。</param>
/// <param name="ExpiresAt">所有时段的最后结束时间；永久为 null。</param>
/// <param name="Quota">没有有效订阅时为 null。</param>
/// <param name="TrialAvailable">账号还能不能领试用（电脑和 IP 领的时候才查）。</param>
/// <param name="RetainUntil">已到期时云端数据保留到哪天（到期后 90 天）。</param>
public sealed record CloudSubscription(
    SubscriptionStatus Status,
    SubscriptionTier? Tier,
    DateTimeOffset? ExpiresAt,
    bool Lifetime,
    CloudQuota? Quota,
    CloudStorage? Storage,
    bool TrialAvailable,
    DateTimeOffset? RetainUntil);

/// <summary>铭牌。目前只有 Id = "founder"（创始会员），Number 1～100。</summary>
public sealed record CloudBadge(string Id, int? Number, DateTimeOffset GrantedAt, bool Public = false, bool Hidden = false);

/// <summary>会员 QQ 群（docs/cloud-api.md「GET /member/group」）。Url 可能为 null。Code 是本人的入群码「XXXX-XXXX」。</summary>
public sealed record CloudMemberGroup(string Number, string? Url, string Code);

/// <param name="AvatarPath">本机缓存的头像文件（256 × 256 JPEG）；没上传头像时为 null，界面画「昵称首字 + 主题色底」。</param>
/// <param name="RecoveryCreatedAt">设了恢复码时的生成时间；没设为 null。</param>
public sealed record CloudProfile(
    string Id,
    string Email,
    string Nickname,
    string? AvatarPath,
    IReadOnlyList<CloudBadge> Badges,
    DateTimeOffset CreatedAt,
    CloudSubscription Subscription,
    int DeviceCount,
    int DeviceLimit,
    DateTimeOffset? RecoveryCreatedAt);

/// <param name="Current">就是这台电脑。</param>
public sealed record CloudDevice(string Id, string Name, bool Current, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

public sealed record CloudPlan(string Id, SubscriptionTier Tier, string Price, int Days, long QuotaBytes);

/// <param name="FounderRemaining">创始会员还剩几个名额。</param>
public sealed record CloudPlans(IReadOnlyList<CloudPlan> Plans, int TrialDays, long TrialQuotaBytes, int FounderTotal, int FounderRemaining);

public enum CloudOrderStatus { Pending, Paid, Underpaid, Expired, Refunded }

/// <param name="PayUrl">爱发电付款页（界面画成二维码，手机扫码付）；只有刚下单时有。</param>
public sealed record CloudOrder(string OrderNo, string PlanId, CloudOrderStatus Status, string Amount, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, string? PayUrl, DateTimeOffset? ExpiresAt);

/// <summary>
/// 账号接口的结果。Error 是服务端的错误代码原样（docs/cloud-api.md、cloud-sync-api.md 的表），再加客户端自己的：
/// "network"（连不上）、"bad_recovery"（恢复码格式不对或解不开）。界面按代码取文案（Strings.resx），服务端不给文案。
/// </summary>
public record CloudResult(string? Error = null, int? RetryAfter = null, int? AttemptsLeft = null)
{
    public bool Ok => Error is null;
    public static readonly CloudResult Success = new();
}
public sealed record CloudResult<T>(T? Value, string? Error = null, int? RetryAfter = null, int? AttemptsLeft = null) : CloudResult(Error, RetryAfter, AttemptsLeft);

public enum CodePurpose { Register, Reset }

/// <summary>注册时同意的协议、隐私政策版本（就是它们的生效日期），出境同意必须单独勾选。</summary>
public sealed record CloudConsents(string Terms, string Privacy, bool CrossBorder);

/// <summary>
/// 登录或注册时设备已满（5 台）：列出已有设备，用户选一台移除后用 RemoveDeviceAndSignInAsync 接着登录。
/// 服务端给的临时凭据（pending_token）服务自己保存，界面不碰。
/// </summary>
public sealed record DeviceLimitHit(IReadOnlyList<CloudDevice> Devices);

/// <summary>
/// 忘记密码时怎么保住云端数据（docs/cloud.md「账号」→ 忘记密码）：
/// LocalKey：用本机存着的数据密钥（这台 Pane 登录过、没退出），云端数据保留；
/// RecoveryCode：用恢复码解开数据密钥，云端数据保留；
/// Fresh：都没有，生成新的数据密钥，云端同步数据清空（界面先二次确认）。
/// </summary>
public enum ResetMode { LocalKey, RecoveryCode, Fresh }

public interface IAccountService
{
    AccountState State { get; }
    /// <summary>已登录时最近一次取到的账号信息；没登录为 null。</summary>
    CloudProfile? Profile { get; }
    /// <summary>NeedsSignIn 时预填的邮箱。</summary>
    string? LastEmail { get; }
    /// <summary>本机存着数据密钥：忘记密码时可以用 ResetMode.LocalKey。退出登录会清掉。</summary>
    bool HasLocalKey { get; }
    /// <summary>网页账号中心（换头像、改邮箱、购买记录）。界面用浏览器打开。</summary>
    string AccountCenterUrl { get; }
    /// <summary>上面任何一项变了。可能在后台线程触发，界面自己切回界面线程。</summary>
    event EventHandler? Changed;

    Task<CloudResult> SendCodeAsync(string email, CodePurpose purpose, CancellationToken cancellationToken);
    /// <summary>注册并登录。密码规则（8～64 个字符、不能和邮箱相同）界面先查。</summary>
    Task<CloudResult<DeviceLimitHit>> RegisterAsync(string email, string code, string password, string? nickname, CloudConsents consents, CancellationToken cancellationToken);
    /// <summary>成功时 Value 为 null；设备已满时 Error 为 "device_limit"、Value 是设备列表。</summary>
    Task<CloudResult<DeviceLimitHit>> SignInAsync(string email, string password, CancellationToken cancellationToken);
    Task<CloudResult> RemoveDeviceAndSignInAsync(string deviceId, CancellationToken cancellationToken);
    /// <summary>清掉本机的会话凭据和数据密钥，同步停止。本机的设置、剪贴板不动。</summary>
    Task<CloudResult> SignOutAsync(CancellationToken cancellationToken);

    /// <summary>创始会员是否在官网创始会员页公开昵称（PATCH /me founder_public）。不是创始会员时 Error 为 "not_founder"。成功后 Profile 跟着变。</summary>
    Task<CloudResult> SetFounderPublicAsync(bool isPublic, CancellationToken cancellationToken);
    /// <summary>会员 QQ 群。没配群号 "not_configured"（界面整块不显示）；不是正式会员 "subscription_required"。</summary>
    Task<CloudResult<CloudMemberGroup>> GetMemberGroupAsync(CancellationToken cancellationToken);
    /// <summary>重取账号信息（打开「账号」分区、付款到账、领试用后）。</summary>
    Task<CloudResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>忘记密码：先 SendCodeAsync(Reset)，再调这个。recoveryCode 只在 RecoveryCode 时用（用户输入的原样，服务自己规范化）。成功后已登录。</summary>
    Task<CloudResult<DeviceLimitHit>> ResetPasswordAsync(string email, string code, string newPassword, ResetMode mode, string? recoveryCode, CancellationToken cancellationToken);
    /// <summary>知道当前密码时改密码；其他设备要重新登录，云端数据不动。</summary>
    Task<CloudResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>生成恢复码（覆盖旧的）。Value 是给用户看的 8 组 4 位，只显示这一次，服务不保存。要求本机有数据密钥。</summary>
    Task<CloudResult<string>> CreateRecoveryCodeAsync(CancellationToken cancellationToken);
    Task<CloudResult> DeleteRecoveryCodeAsync(CancellationToken cancellationToken);

    Task<CloudResult<IReadOnlyList<CloudDevice>>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<CloudResult> RenameDeviceAsync(string deviceId, string name, CancellationToken cancellationToken);
    /// <summary>移除别的设备（那台退出登录）。移除本机等于退出登录，界面不提供。</summary>
    Task<CloudResult> RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken);

    Task<CloudResult<CloudPlans>> GetPlansAsync(CancellationToken cancellationToken);
    /// <summary>下单，拿到爱发电付款链接（界面画成二维码）。之后用 GetOrderAsync 每 3 秒查一次，最多 10 分钟；到账后服务自己 RefreshAsync。</summary>
    Task<CloudResult<CloudOrder>> CreateOrderAsync(string planId, CancellationToken cancellationToken);
    Task<CloudResult<CloudOrder>> GetOrderAsync(string orderNo, CancellationToken cancellationToken);
    Task<CloudResult> RedeemAsync(string code, CancellationToken cancellationToken);
    /// <summary>领 7 天试用：服务自己算两个硬件哈希（docs/cloud-api.md「POST /trial」）。</summary>
    Task<CloudResult> ClaimTrialAsync(CancellationToken cancellationToken);

    /// <summary>注销：账号、云端数据、头像立即删除，然后和 SignOutAsync 一样清本机凭据。界面先二次确认。</summary>
    Task<CloudResult> DeleteAccountAsync(string password, CancellationToken cancellationToken);
}

// ================= 同步 =================

/// <summary>
/// Off：没开同步（或没登录）。Idle：开着，最近一次成功，正在等新变化。Syncing：正在上传或下载。
/// Paused：开着但暂时不能同步，原因在 PauseReason。Error：上次失败了，正在按退避间隔重试，原因在 ErrorCode。
/// 同步永远不阻塞界面，本机照常可用（docs/cloud.md「断网与故障」）。
/// </summary>
public enum SyncState { Off, Idle, Syncing, Paused, Error }

/// <summary>
/// NoSubscription：没有有效订阅（到期了：界面写云端数据保留到哪天，给续费入口）。
/// NeedsSignIn：会话失效。ClientOutdated：服务端要求更新 Pane。
/// NeedsMergeChoice：换了账号、本机和云端都有数据，等用户选怎么合并（ResolveMergeAsync）。
/// </summary>
public enum SyncPauseReason { NoSubscription, NeedsSignIn, ClientOutdated, NeedsMergeChoice }

/// <summary>换账号登录、本机和云端都有数据时的选择（docs/cloud.md「设备」），说法同导入的「合并 / 覆盖」。</summary>
public enum SyncMergeChoice { Merge, UseCloud, UseLocal }

/// <summary>剪贴板同步范围。打开剪贴板同步时默认 TextAndImages。</summary>
public enum ClipSyncScope { Text, TextAndImages }

/// <summary>别的设备来的条目怎么显示：Mixed 混在同一条时间线（默认）；FilterOnly 只在设备筛选里显示。</summary>
public enum ClipDeviceView { Mixed, FilterOnly }

/// <summary>
/// 同步设置（存在 settings.json，本身不同步）。界面（「数据」→「同步」组）改完调 ISyncService.ApplyAsync。
/// Enabled：同步总开关，默认关。Settings / Usage：同步设置、使用记录，打开总开关时默认都开。
/// Notes（2026-11-25，任务 124）：同步备忘录，打开总开关时默认开；两台电脑都改过同一条时两份都留（docs/cloud-sync-api.md「notes」）。
/// Clipboard：剪贴板同步，单独开关，默认关；打开时界面说清会上传哪些内容（加密后上传）。
/// ExcludedApps：这些程序的内容照常在本机记录，只是不上传（程序 exe 名）。
/// WriteToSystemClipboard：「其他设备复制时，同时放进本机剪贴板」，默认关；打开后这条不再被本机记录、上传。
/// </summary>
public sealed record SyncOptions(
    bool Enabled = false,
    bool Settings = true,
    bool Usage = true,
    bool Clipboard = false,
    ClipSyncScope ClipScope = ClipSyncScope.TextAndImages,
    IReadOnlyList<string>? ExcludedApps = null,
    bool WriteToSystemClipboard = false,
    ClipDeviceView DeviceView = ClipDeviceView.Mixed,
    bool Notes = true);

public interface ISyncService
{
    SyncState State { get; }
    SyncPauseReason? PauseReason { get; }
    /// <summary>Error 时的错误代码（同 CloudResult.Error），界面按代码取文案。</summary>
    string? ErrorCode { get; }
    DateTimeOffset? LastSuccessAt { get; }
    SyncOptions Options { get; }
    /// <summary>
    /// 同一账号的其他设备（剪贴板筛选条的设备组、说明行的设备名用）。不含本机。
    /// 两台以上设备（含本机）时，剪贴板筛选条才出现设备组。
    /// </summary>
    IReadOnlyList<CloudDevice> OtherDevices { get; }
    event EventHandler? Changed;

    /// <summary>保存同步设置并按新设置开始 / 停止。关掉剪贴板同步不删云端已有的条目。</summary>
    Task ApplyAsync(SyncOptions options, CancellationToken cancellationToken);
    /// <summary>「立即同步」：不等长轮询，马上同步一次。</summary>
    Task SyncNowAsync(CancellationToken cancellationToken);
    Task ResolveMergeAsync(SyncMergeChoice choice, CancellationToken cancellationToken);
}

/// <summary>
/// 剪贴板和同步之间（剪贴板界面用）：别的设备来的图片原图按需下载。
/// 条目上的 ClipEntry.SourceDevice / Original 由剪贴板历史服务填，数据从同步服务来。
/// </summary>
public interface IClipboardCloud
{
    /// <summary>
    /// 界面把一条别的设备来的图片条目显示出来时调用（面板里滚动到它）：缩略图还没下载就排队下载，下完后条目的 Thumbnail 有值、触发 IClipboardHistory.Changed。
    /// 已在本机、或不是别的设备来的图片时什么也不做。可以频繁调用，服务自己去重、限并发。
    /// </summary>
    void EnsureThumbnail(string clipId);

    /// <summary>原图大小（字节），界面写「正在下载原图 1.2 MB」；不知道时为 null。</summary>
    long? OriginalSize(string clipId);
    /// <summary>
    /// 下载原图到本机，完成后条目变成 Local、ContentPath 可用。粘贴、打开、另存时调；已在本机直接返回成功。
    /// 失败的 Error："blob_expired"（条目变 Expired）、"quota_exceeded"（变 QuotaExceeded）、"network"。
    /// </summary>
    Task<CloudResult> FetchOriginalAsync(string clipId, CancellationToken cancellationToken);
}
