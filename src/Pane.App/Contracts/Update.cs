// 软件内更新：界面（设置 → 关于、托盘提示）和更新服务之间的接口。归设计负责人定义，工程照着实现（docs/specs/023）。
// 2026-10-30 用户定：做完整的自动更新——从公开仓库的 GitHub Releases 取最新版，后台下载、核对 SHA-256，提示后重启安装。
// 界面开发期间用 DesignData/UpdateSample（PANE_DESIGN_UPDATE 选状态）。
namespace Pane.Contracts;

/// <summary>
/// Idle：还没查过；Checking：正在查；UpToDate：已是最新；Available：有新版、还没下载（关了自动更新，或下载失败后）；
/// Downloading：正在下载；Ready：已下载并核对好，等用户「重启并更新」；Failed：这一步失败了（原因在 Error）。
/// </summary>
public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <param name="Version">新版本号，如 0.1.150（和 CurrentVersion 同一种写法，按数字逐段比较）。</param>
/// <param name="Notes">更新说明（Release 正文的纯文本，去掉 Markdown 标记，最多约 2000 字）。</param>
/// <param name="SizeBytes">安装包大小。</param>
/// <param name="PageUrl">这个版本的 Release 网页（「查看详情」打开它）。</param>
public sealed record UpdateRelease(string Version, string Notes, DateTimeOffset PublishedAt, long SizeBytes, string PageUrl);

public interface IUpdateService
{
    /// <summary>正在运行的版本（开发版为 "开发版"，开发版不自动检查）。</summary>
    string CurrentVersion { get; }
    UpdateState State { get; }
    /// <summary>查到的最新版本；还没查过或已是最新时可以为 null。</summary>
    UpdateRelease? Latest { get; }
    /// <summary>下载进度 0～1（Downloading 时有意义）。</summary>
    double DownloadProgress { get; }
    /// <summary>Failed 时给用户看的一句话（网络不通、校验不对、磁盘写不进……）。</summary>
    string? Error { get; }
    DateTimeOffset? LastChecked { get; }
    /// <summary>上面任何一项变化。可能在后台线程触发，界面自己切回 UI 线程。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 手动检查（「检查更新」按钮）。设置里开着自动更新时，查到新版会接着自动下载。
    /// 自动检查由服务自己安排（正式版启动后约 1 分钟一次，之后每 24 小时一次；关掉自动更新就不自动查）。
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken);

    /// <summary>下载 Latest（「下载更新」按钮，或自动更新时服务自己调）。完成后核对 SHA-256，进入 Ready。</summary>
    Task DownloadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 只在 Ready 时可用：以「自动更新」方式启动下载好的安装包（安装窗口自动开始、装完自动打开新版），然后 Pane 正常退出。
    /// 用户正在用的东西（设置窗口里没保存的输入等）由界面在调用前处理。
    /// </summary>
    void InstallAndRestart();
}
