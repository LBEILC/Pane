// 定时关机的假服务（设计负责人，058）。演示模式用：照真实服务的规则计时、到点前 60 秒进入提醒，但到点只结束、不真的关机。
// 设计预览：PANE_DESIGN_TIMER=分钟数 先定一个这么久以后的关机；=remind 先定一个 60 秒后的关机（直接进入提醒）
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class PowerTimerSample : IPowerTimer, IDisposable
{
    readonly Lock sync = new();
    readonly Timer timer;
    PowerTimerState? current;

    public PowerTimerSample()
    {
        timer = new Timer(_ => Tick());
        var preview = Environment.GetEnvironmentVariable("PANE_DESIGN_TIMER");
        if (preview == "remind") Set(new(PowerAction.Shutdown, DateTimeOffset.Now.AddSeconds(60), false));
        else if (int.TryParse(preview, out var minutes) && minutes > 0) Set(new(PowerAction.Shutdown, DateTimeOffset.Now.AddMinutes(minutes), false));
    }

    public PowerTimerState? Current { get { lock (sync) return current; } }
    public event EventHandler? Changed;
    public event EventHandler<PowerTimerResult>? Finished;

    public void Schedule(PowerAction action, DateTimeOffset at)
    {
        var left = at - DateTimeOffset.Now;
        if (left < TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1) || left > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(at));
        Set(new(action, at, false));
    }

    public void Cancel() => Set(null);

    public void Postpone(TimeSpan by) { if (Current is { } c) Set(new(c.Action, DateTimeOffset.Now + by, false)); }

    void Set(PowerTimerState? value)
    {
        lock (sync) { current = value; Arm(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // 一次性计时：下一个时刻是「进入提醒」或「到点」，不轮询
    void Arm()
    {
        if (current is not { } c) { timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
        var next = c.Reminding ? c.At : c.At.AddSeconds(-60);
        timer.Change(Math.Max(0, (long)(next - DateTimeOffset.Now).TotalMilliseconds), Timeout.Infinite);
    }

    void Tick()
    {
        PowerTimerResult? done = null;
        lock (sync)
        {
            if (current is not { } c) return;
            if (DateTimeOffset.Now >= c.At) { current = null; done = new(c.Action, PowerTimerOutcome.Executed); }
            else if (!c.Reminding) current = c with { Reminding = true };
            Arm();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (done is not null) Finished?.Invoke(this, done);
    }

    public void Dispose() => timer.Dispose();
}
