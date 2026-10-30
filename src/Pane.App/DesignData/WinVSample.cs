// 用 Pane 替换 Win+V 的假服务（设计负责人，演示和看效果用）。不碰注册表、不重启资源管理器，只在内存里切状态。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class WinVSample : IWinVReplacement
{
    public WinVState State { get; private set; } =
        Environment.GetEnvironmentVariable("PANE_DESIGN_WINV") is { } v && Enum.TryParse<WinVState>(v, true, out var s) ? s : WinVState.Off;
    public bool SystemHistoryDisabled { get; private set; }
    public string? Error { get; private set; }
    public event EventHandler? Changed;

    public async Task EnableAsync(bool restartExplorer, CancellationToken cancellationToken)
    {
        await Task.Delay(restartExplorer ? 900 : 200, cancellationToken);   // 假装重启资源管理器
        State = restartExplorer ? WinVState.On : WinVState.Pending;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisableAsync(bool restartExplorer, CancellationToken cancellationToken)
    {
        await Task.Delay(restartExplorer ? 900 : 200, cancellationToken);
        State = restartExplorer || State == WinVState.Pending ? WinVState.Off : WinVState.RestorePending;
        SystemHistoryDisabled = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SetSystemHistoryDisabledAsync(bool disabled, CancellationToken cancellationToken)
    {
        SystemHistoryDisabled = disabled;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
