// 软件内更新的假服务：不联网、不下载、不启动安装包。
// 环境变量 PANE_DESIGN_UPDATE=uptodate|available|downloading|ready|failed 选一开始的状态（默认 idle）；
// 「检查更新」走一遍 Checking → Available（开着自动更新时接着模拟下载到 Ready）。
// Mirror酱（2026-11-13）：更新源读真实设置；PANE_DESIGN_CDK=valid|soon|expired|invalid|quota|mismatch|blocked|offline 预填一个演示 CDK 和对应状态。
// 在设置里填 CDK 时照原型的演示规则：不少于 8 位算有效（含 soon 5 天后到期），含 expired / quota / mismatch / blocked 出对应错误，
// 少于 8 位算填错，含 offline 算连不上 Mirror酱（这次改查 GitHub）。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class UpdateSample : IUpdateService
{
    static readonly UpdateRelease Release = new("0.1.150", "启动器：应用和动作改成格子，文件改成列表，底部提示栏写明按键\n新增文件、翻译、AI 对话三个窗口\n超级面板：选中外文自动翻译\n修复：按住右键快速滚轮切标签后停不下来",
        DateTimeOffset.Now.AddDays(-1), 34_416_640, "https://github.com/",
        // 062：结构化说明，关于页按 新增 / 改进 / 修复 分组显示
        new("0.1.150", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), [
            new(ReleaseNoteKind.New, "设置 → 关于里能直接看到这一版和以前每个版本的更新内容。", true),
            new(ReleaseNoteKind.New, "新版本下载好后，呼出启动器或剪贴板时最上面会提示，可以直接重启更新，或者忽略这个版本。"),
            new(ReleaseNoteKind.Improved, "启动器可以调宽度，以及最多显示几行应用、几个文件。"),
            new(ReleaseNoteKind.Fixed, "安装程序清理旧临时文件出错时会让安装失败。"),
            new(ReleaseNoteKind.Fixed, "安装失败时只说「没有装上」，看不出是哪一步出的错。"),
        ]));

    public UpdateSample()
    {
        State = Environment.GetEnvironmentVariable("PANE_DESIGN_UPDATE") switch
        {
            "uptodate" => UpdateState.UpToDate,
            "available" => UpdateState.Available,
            "downloading" => UpdateState.Downloading,
            "ready" => UpdateState.Ready,
            "failed" => UpdateState.Failed,
            _ => UpdateState.Idle,
        };
        if (State is not (UpdateState.Idle or UpdateState.UpToDate)) Latest = Release;
        if (State == UpdateState.Downloading) DownloadProgress = 0.42;
        if (State == UpdateState.Failed) Error = "连不上 GitHub，稍后再试";
        if (State != UpdateState.Idle) LastChecked = DateTimeOffset.Now.AddMinutes(-3);
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_CDK") is { Length: > 0 } preset)
        {
            // 截图脚本每次用新的演示数据目录：预设 CDK 时顺带把更新源切到 Mirror酱，关于页才会展开 CDK 一行
            var store = ((App)Microsoft.UI.Xaml.Application.Current).Store;
            if (store.Current.Update.Source != UpdateSource.MirrorChyan)
                store.Save(store.Current with { Update = store.Current.Update with { Source = UpdateSource.MirrorChyan } });
            MirrorCdk = "DEMO-CDK-" + preset.ToUpperInvariant();
            Judge(Mirror);
            CheckedFrom = Mirror && preset != "offline" ? UpdateOrigin.MirrorChyan : UpdateOrigin.GitHub;
        }
    }

    static bool Mirror => ((App)Microsoft.UI.Xaml.Application.Current).Store.Current.Update.Source == UpdateSource.MirrorChyan;
    public UpdateOrigin? CheckedFrom { get; private set; }
    public UpdateOrigin? DownloadFrom => Latest == null ? null
        : Mirror && CdkStatus == MirrorCdkStatus.Valid ? UpdateOrigin.MirrorChyan : UpdateOrigin.GitHub;
    public string MirrorCdk { get; private set; } = "";
    public MirrorCdkStatus CdkStatus { get; private set; }
    public DateTimeOffset? CdkExpires { get; private set; }

    public void SaveMirrorCdk(string cdk)
    {
        cdk = cdk.Trim();
        if (cdk == MirrorCdk) return;
        MirrorCdk = cdk; CdkExpires = null;
        CdkStatus = cdk.Length == 0 ? MirrorCdkStatus.None : MirrorCdkStatus.Unknown;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // 演示规则：按 CDK 的文字决定校验结果
    void Judge(bool mirror)
    {
        if (!mirror || MirrorCdk.Length == 0) { if (MirrorCdk.Length == 0) CdkStatus = MirrorCdkStatus.None; return; }
        string c = MirrorCdk.ToLowerInvariant();
        if (c.Contains("offline")) { CdkStatus = MirrorCdkStatus.Unknown; return; }
        CdkStatus = c.Contains("expired") ? MirrorCdkStatus.Expired : c.Contains("quota") ? MirrorCdkStatus.QuotaExhausted
            : c.Contains("mismatch") ? MirrorCdkStatus.Mismatched : c.Contains("blocked") ? MirrorCdkStatus.Blocked
            : c.Contains("invalid") || MirrorCdk.Length < 8 ? MirrorCdkStatus.Invalid : MirrorCdkStatus.Valid;
        CdkExpires = CdkStatus == MirrorCdkStatus.Valid ? DateTimeOffset.Now.AddDays(c.Contains("soon") ? 5 : 120) : null;
    }

    public string CurrentVersion => "0.1.138";
    public UpdateState State { get; private set; }
    public UpdateStep? FailedStep => State == UpdateState.Failed ? UpdateStep.Check : null;
    public UpdateRelease? Latest { get; private set; }
    public double DownloadProgress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public event EventHandler? Changed;

    void Set(UpdateState state) { State = state; Changed?.Invoke(this, EventArgs.Empty); }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        Error = null;
        bool mirror = Mirror;
        if (mirror && MirrorCdk.Length > 0) CdkStatus = MirrorCdkStatus.Checking;
        Set(UpdateState.Checking);
        await Task.Delay(900, cancellationToken);
        Judge(mirror);
        CheckedFrom = mirror && !MirrorCdk.Contains("offline", StringComparison.OrdinalIgnoreCase) ? UpdateOrigin.MirrorChyan : UpdateOrigin.GitHub;
        Latest = Release;
        LastChecked = DateTimeOffset.Now;
        Set(UpdateState.Available);
        // CDK 填错、类型不符、被封禁时不自动下载（用户 2026-11-12 定）
        bool hold = mirror && CdkStatus is MirrorCdkStatus.Invalid or MirrorCdkStatus.Mismatched or MirrorCdkStatus.Blocked;
        if (((App)Microsoft.UI.Xaml.Application.Current).Store.Current.Update.AutoUpdate && !hold) await DownloadAsync(cancellationToken);
    }

    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        Set(UpdateState.Downloading);
        for (int i = 1; i <= 20; i++)
        {
            await Task.Delay(120, cancellationToken);
            DownloadProgress = i / 20.0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        Set(UpdateState.Ready);
    }

    public void InstallAndRestart() { }
}
