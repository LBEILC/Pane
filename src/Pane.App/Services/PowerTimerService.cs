using Pane.Contracts;
using Pane.Localization;
namespace Pane.Services;

// 不持久化、不轮询；时钟及执行器可替换，测试不会调用系统电源操作。
internal sealed class PowerTimerService(TimeProvider clock, Func<PowerAction, Task> execute) : IPowerTimer, IDisposable
{
    readonly object gate = new();
    ITimer? reminder, deadline;
    PowerTimerState? current;
    long generation;
    bool disposed;
    public PowerTimerState? Current { get { lock (gate) return current; } }
    internal int ArmedTimers { get { lock (gate) return (reminder != null ? 1 : 0) + (deadline != null ? 1 : 0); } }
    internal long Wakeups { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<PowerTimerResult>? Finished;
    public void Schedule(PowerAction action, DateTimeOffset at)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = clock.GetLocalNow(); Validate(at - now);
            if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
            ClearTimers(); current = new(action, at, false); Arm(now); Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    static void Validate(TimeSpan delay)
    {
        if (delay < TimeSpan.FromMinutes(1) || delay > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(delay));
    }
    public void Cancel()
    {
        lock (gate) { ClearTimers(); if (current == null) return; current = null; Changed?.Invoke(this, EventArgs.Empty); }
    }
    public void Postpone(TimeSpan by)
    {
        lock (gate) { if (current == null) return; Schedule(current.Action, clock.GetLocalNow() + by); }
    }
    void ClearTimers() { generation++; reminder?.Dispose(); deadline?.Dispose(); reminder = deadline = null; }
    void Arm(DateTimeOffset now)
    {
        var id = generation;
        var left = current!.At - now;
        if (!current.Reminding)
            reminder = clock.CreateTimer(_ => Remind(id), null, left - TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        deadline = clock.CreateTimer(_ => { _ = Run(id); }, null, left, Timeout.InfiniteTimeSpan);
    }
    void Remind(long id)
    {
        lock (gate)
        {
            if (disposed || id != generation || current == null) return;
            Wakeups++; reminder?.Dispose(); reminder = null;
            current = current with { Reminding = true }; Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    async Task Run(long id)
    {
        PowerAction action; Task operation;
        lock (gate)
        {
            if (disposed || id != generation || current == null) return;
            Wakeups++; action = current.Action; ClearTimers(); current = null;
            // 先取走这次定时，睡眠恢复广播不会再次排入它。执行器调用和取消通过同一把锁排序。
            try { operation = execute(action); } catch { operation = Task.FromException(new InvalidOperationException()); }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        PowerTimerResult result;
        try { await operation.ConfigureAwait(false); result = new(action, PowerTimerOutcome.Executed); }
        catch { result = new(action, PowerTimerOutcome.Failed, Strings.Common_SystemOperationFailed); }
        lock (gate) { if (!disposed) Finished?.Invoke(this, result); }
    }
    internal void SystemTimeChanged()
    {
        lock (gate)
        {
            if (disposed || current == null) return;
            var now = clock.GetLocalNow(); var left = current.At - now; ClearTimers();
            if (left < -TimeSpan.FromMinutes(1))
            {
                var action = current.Action; current = null; Changed?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke(this, new(action, PowerTimerOutcome.Missed)); return;
            }
            current = left < TimeSpan.FromSeconds(60)
                ? current with { At = now.AddSeconds(60), Reminding = true }
                : current with { Reminding = false };
            Arm(now); Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; Cancel(); disposed = true; Changed = null; Finished = null; } }
}
