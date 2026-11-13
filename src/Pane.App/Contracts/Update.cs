// 软件内更新：界面（设置 → 关于、托盘提示）和更新服务之间的接口。归设计负责人定义，工程照着实现（docs/specs/023）。
// 2026-10-30 用户定：做完整的自动更新——从公开仓库的 GitHub Releases 取最新版，后台下载、核对 SHA-256，提示后重启安装。
// 界面开发期间用 DesignData/UpdateSample（PANE_DESIGN_UPDATE 选状态）。
namespace Pane.Contracts;

/// <summary>
/// Idle：还没查过；Checking：正在查；UpToDate：已是最新；Available：有新版、还没下载（关了自动更新，或下载失败后）；
/// Downloading：正在下载；Ready：已下载并核对好，等用户「重启并更新」；Failed：这一步失败了（原因在 Error）。
/// </summary>
public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>失败在哪一步（State 为 Failed 时有意义）：界面据此决定「重试」是重新检查还是重新下载。</summary>
public enum UpdateStep { Check, Download, Install }

/// <param name="Version">新版本号，如 0.1.150（和 CurrentVersion 同一种写法，按数字逐段比较）。</param>
/// <param name="Notes">更新说明（Release 正文的纯文本，去掉 Markdown 标记，最多约 2000 字）。</param>
/// <param name="PublishedAt">发布时间；Mirror酱 不返回，从那边查到时为 null（界面不显示日期）。</param>
/// <param name="SizeBytes">安装包大小；不知道时为 0（Mirror酱 不带 CDK 时不返回，界面不显示大小）。</param>
/// <param name="PageUrl">这个版本的 Release 网页（「查看详情」打开它）；从 Mirror酱 查到时拼成 GitHub 的 releases/tag/v&lt;版本&gt;。</param>
/// <param name="Details">从 Release 正文解析出的结构化更新说明（## 新增 / ## 改进 / ## 修复，062）；正文不是这个格式时为 null，界面退回显示 Notes。Mirror酱 的 release_note 就是同一份正文。</param>
public sealed record UpdateRelease(string Version, string Notes, DateTimeOffset? PublishedAt, long SizeBytes, string PageUrl, ReleaseNotes? Details = null);

/// <summary>
/// 更新源（2026-11-12 用户定，照 MAA / StrongholdAssistant 接入 Mirror酱，见 docs/design.md「Mirror酱 更新源」）。
/// GitHub：检查和下载都走 GitHub Releases（原来的做法）。
/// MirrorChyan：检查走 Mirror酱；CDK 有效时也从 Mirror酱 下载，没填或出错时下载退回 GitHub（规则见 MirrorCdkStatus）。
/// 存在 UpdateSettings.Source；界面改了设置之后调 CheckAsync。
/// </summary>
public enum UpdateSource { GitHub, MirrorChyan }

/// <summary>
/// Mirror酱 CDK 的状态（更新源是 MirrorChyan 时有意义）。
/// None：没填。Unknown：填了，但还没校验过、或这次连不上 Mirror酱 没能校验（有上次的到期日照样显示）。Checking：正在带它检查。
/// Valid：有效（CdkExpires 是到期时间）。
/// Expired（7001）、QuotaExhausted（7003）：后台检查到新版时自动改从 GitHub 下载（用户 2026-11-12 定），7003 当天不再带 CDK 重试下载。
/// Invalid（7002）、Mismatched（7004）、Blocked（7005）：不自动下载，等用户改 CDK、点「下载更新」（走 GitHub）或「改用 GitHub」。
/// </summary>
public enum MirrorCdkStatus { None, Unknown, Checking, Valid, Expired, Invalid, QuotaExhausted, Mismatched, Blocked }

/// <summary>Latest 从哪里下载（Available / Downloading / Ready 时有意义）。</summary>
public enum UpdateOrigin { GitHub, MirrorChyan }

public interface IUpdateService
{
    /// <summary>正在运行的版本（开发版为 "开发版"，开发版不自动检查）。</summary>
    string CurrentVersion { get; }
    UpdateState State { get; }
    /// <summary>State 为 Failed 时，失败的是哪一步；其余状态为 null。</summary>
    UpdateStep? FailedStep { get; }
    /// <summary>查到的最新版本；还没查过或已是最新时可以为 null。</summary>
    UpdateRelease? Latest { get; }
    /// <summary>下载进度 0～1（Downloading 时有意义）。</summary>
    double DownloadProgress { get; }
    /// <summary>Failed 时给用户看的一句话（网络不通、校验不对、磁盘写不进……）。</summary>
    string? Error { get; }
    DateTimeOffset? LastChecked { get; }
    /// <summary>上面任何一项变化。可能在后台线程触发，界面自己切回 UI 线程。</summary>
    event EventHandler? Changed;

    // ---- Mirror酱（2026-11-12）：先给默认实现让现有服务照常编译，工程实现时在 UpdateService 里全部覆盖 ----

    /// <summary>
    /// 这次检查实际从哪里查到的：更新源是 MirrorChyan 但 Mirror酱 连不上、返回非 JSON、8001 或 code&lt;0 时，服务自动改查 GitHub 一次，这里为 GitHub。
    /// 界面据此在更新源那一行说一句「这次从 GitHub 检查」。还没查过为 null。
    /// </summary>
    UpdateOrigin? CheckedFrom => null;
    /// <summary>Latest 会从 / 正在从 / 已经从哪里下载；没有 Latest 时为 null。</summary>
    UpdateOrigin? DownloadFrom => Latest == null ? null : UpdateOrigin.GitHub;
    /// <summary>
    /// 保存的 CDK 明文（输入框默认遮住，点眼睛才显示）；没填为 ""。
    /// 用当前用户 DPAPI 单独存一个文件，不进 settings.json、数据导出和日志。
    /// </summary>
    string MirrorCdk => "";
    MirrorCdkStatus CdkStatus => MirrorCdk.Length == 0 ? MirrorCdkStatus.None : MirrorCdkStatus.Unknown;
    /// <summary>CDK 到期时间（接口的 cdk_expired_time）；和 CDK 存在一起，下次启动还能显示；换了 CDK 或校验出错就清掉。</summary>
    DateTimeOffset? CdkExpires => null;
    /// <summary>
    /// 保存 CDK（去掉首尾空白；"" = 删除）。不联网；界面接着调 CheckAsync，让新 CDK 立刻被带着检查一次
    /// （分成规则：新 CDK 第一个带它检查的 app 拿分成，所以填完失焦或回车就查，不等下次启动）。
    /// </summary>
    void SaveMirrorCdk(string cdk) { }

    /// <summary>
    /// 检查更新：「检查更新」按钮、切换更新源、保存 CDK 后都调它。设置里开着自动更新时，查到新版会接着自动下载（CDK 出错时的例外见 MirrorCdkStatus）。
    /// 正在检查时再调：取消正在进行的那次，按最新的更新源和 CDK 重新查（不能吞掉，否则刚填的 CDK 可能没被带上）；正在下载时不打断下载，下载完再查。
    /// 自动检查由服务自己安排（正式版启动后约 1 分钟一次，之后每 24 小时一次；关掉自动更新就不自动查），计时器碰上正在检查就跳过。
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 下载 Latest（「下载更新」按钮，或自动更新时服务自己调），从 DownloadFrom 那边下。完成后核对 SHA-256（GitHub 用 SHA256SUMS.txt，Mirror酱 用接口返回的 sha256），进入 Ready。
    /// 用户手动点的下载即使 CDK 出错也会走 GitHub。
    /// </summary>
    Task DownloadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 只在 Ready 时可用：以「自动更新」方式启动下载好的安装包（安装窗口自动开始、装完自动打开新版），然后 Pane 正常退出。
    /// 用户正在用的东西（设置窗口里没保存的输入等）由界面在调用前处理。
    /// </summary>
    void InstallAndRestart();
}
