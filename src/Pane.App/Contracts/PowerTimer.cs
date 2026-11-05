// 定时关机（2026-11-05 设计定义，058）：界面（定时窗口、到点前提醒、托盘、启动器结果行）和计时服务之间的接口。
// 规则见 docs/design.md「定时关机」；工程照着实现服务和启动器里的说法解析。
namespace Pane.Contracts;

/// <summary>只做这三种（用户定）。</summary>
public enum PowerAction { Shutdown, Restart, Sleep }

/// <param name="At">到点的本地时间。</param>
/// <param name="Reminding">已进入到点前 60 秒的提醒阶段（提醒面板开着）。</param>
public sealed record PowerTimerState(PowerAction Action, DateTimeOffset At, bool Reminding);

/// <summary>一次定时的结局，界面据此给提示。</summary>
public enum PowerTimerOutcome
{
    /// <summary>到点执行了（关机 / 重启已发起，睡眠已醒来）。</summary>
    Executed,
    /// <summary>电脑睡着错过了点，醒来时已过点超过 1 分钟，没有执行。</summary>
    Missed,
    /// <summary>执行失败（系统拒绝等）；Detail 给一句原因。</summary>
    Failed,
}

public sealed record PowerTimerResult(PowerAction Action, PowerTimerOutcome Outcome, string? Detail = null);

public interface IPowerTimer
{
    /// <summary>当前的定时；没有为 null。</summary>
    PowerTimerState? Current { get; }

    /// <summary>
    /// 定一个；已有的被替换。<paramref name="at"/> 离现在不到 1 分钟或超过 24 小时抛 ArgumentOutOfRangeException（界面先挡住）。
    /// 离现在不到 60 秒以内的不会出现（最短 1 分钟），所以提醒总能完整倒数。
    /// </summary>
    void Schedule(PowerAction action, DateTimeOffset at);

    /// <summary>取消当前定时（没有就什么都不做）；提醒开着就收起。</summary>
    void Cancel();

    /// <summary>推迟：新的到点 = 现在 + <paramref name="by"/>（提醒里的「推迟 10 分钟」），动作不变，之后照常再提醒。</summary>
    void Postpone(TimeSpan by);

    /// <summary>Current 变了（定好、替换、取消、推迟、进入提醒、结束）。可能在后台线程触发，界面自己切回界面线程。</summary>
    event EventHandler? Changed;

    /// <summary>一次定时结束（执行、错过、失败）；取消不算。可能在后台线程触发。</summary>
    event EventHandler<PowerTimerResult>? Finished;
}
