// 开机启动：设置页「通用 → 开机时启动 Pane」和服务之间的接口。归设计负责人定义，工程照着实现（017）。
namespace Pane.Contracts;

public enum AutostartState
{
    Off,
    On,
    /// <summary>注册了，但用户在任务管理器「启动应用」里禁用了。设置页照实显示，不替用户改回来。</summary>
    DisabledInTaskManager,
    /// <summary>当前不能注册（开发版、从构建目录运行等），原因见 <see cref="IAutostart.UnavailableReason"/>。</summary>
    Unavailable,
}

public interface IAutostart
{
    /// <summary>按注册表实际情况算出的状态（不是设置文件里记的意图）。</summary>
    AutostartState State { get; }

    /// <summary>State 为 Unavailable 时给用户看的一句话，如「开发版不注册开机启动」。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 打开 / 关闭开机启动：写或删当前用户的启动项，并同步设置里的 StartWithWindows。
    /// 失败抛异常（界面显示「没有改成功」）；Unavailable 时调用抛 InvalidOperationException。
    /// </summary>
    void Set(bool enabled);
}
