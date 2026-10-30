// 用 Pane 替换 Win+V：设置 → 剪贴板 和替换服务之间的接口。归设计负责人定义，工程照着实现（030）。
// 原型 prototype/settings.js「用 Pane 替换 Win+V」。需求：默认关闭，在设置里打开；关闭时把系统设置原样恢复。
namespace Pane.Contracts;

public enum WinVState
{
    /// <summary>没替换，Win+V 归系统。</summary>
    Off,
    /// <summary>注册表已改，资源管理器还没重启（等用户重新登录或稍后重启资源管理器）；这期间 Win+V 仍是系统的。</summary>
    Pending,
    /// <summary>已生效：Win+V 打开 Pane 的剪贴板。</summary>
    On,
    /// <summary>关掉了，但资源管理器还没重启，系统还没把 Win+V 拿回去（这期间 Win+V 没反应）。</summary>
    RestorePending,
}

public interface IWinVReplacement
{
    WinVState State { get; }

    /// <summary>「同时关闭 Windows 自带的剪贴板历史」是否由 Pane 关掉了（关闭替换时一起恢复）。</summary>
    bool SystemHistoryDisabled { get; }

    /// <summary>上一次操作失败的原因（一句话，给用户看）；成功后清空。</summary>
    string? Error { get; }

    /// <summary>状态变化（可能在后台线程触发）。</summary>
    event EventHandler? Changed;

    /// <summary>
    /// 替换：记下原值后改当前用户的注册表（不需要管理员权限），让资源管理器放开 Win+V。
    /// <paramref name="restartExplorer"/> 为 true 时正常退出再启动资源管理器（不强制结束），完成后 Win+V 打开 Pane 的剪贴板；
    /// 为 false 时状态停在 <see cref="WinVState.Pending"/>，下次登录生效。
    /// </summary>
    Task EnableAsync(bool restartExplorer, CancellationToken cancellationToken);

    /// <summary>
    /// 恢复：把注册表原样恢复（含「Windows 自带的剪贴板历史」），Pane 不再占 Win+V。重启资源管理器的选项同上：
    /// 不重启时停在 <see cref="WinVState.RestorePending"/>；从 <see cref="WinVState.Pending"/>（资源管理器还没放开）关闭时不用重启，直接回到 Off。
    /// </summary>
    Task DisableAsync(bool restartExplorer, CancellationToken cancellationToken);

    /// <summary>只在已替换（On / Pending）时可用；关闭替换时一并恢复成原值。</summary>
    Task SetSystemHistoryDisabledAsync(bool disabled, CancellationToken cancellationToken);
}
