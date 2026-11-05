// 软件内更新的假服务：不联网、不下载、不启动安装包。
// 环境变量 PANE_DESIGN_UPDATE=uptodate|available|downloading|ready|failed 选一开始的状态（默认 idle）；
// 「检查更新」走一遍 Checking → Available（开着自动更新时接着模拟下载到 Ready）。
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
        Set(UpdateState.Checking);
        await Task.Delay(900, cancellationToken);
        Latest = Release;
        LastChecked = DateTimeOffset.Now;
        Set(UpdateState.Available);
        if (((App)Microsoft.UI.Xaml.Application.Current).Store.Current.Update.AutoUpdate) await DownloadAsync(cancellationToken);
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
