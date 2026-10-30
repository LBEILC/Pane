using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Pane.Gestures.Tests")]

namespace Pane.Gestures;

// 仅在托管服务线程与命令线程之间使用；原生钩子不进入此锁。
internal sealed class CommandQueue
{
    readonly object gate = new();
    readonly LinkedList<CommandRequest> pending = new();
    LinkedListNode<CommandRequest>? wheel;
    bool completed;

    internal void Add(CommandRequest request)
    {
        lock (gate)
        {
            if (completed) throw new InvalidOperationException("命令队列已结束");
            bool replaceable = request.Modifier is GestureModifier.WheelUp or GestureModifier.WheelDown
                && (request.Phase == CommandPhase.ModifyAware
                    || request.Phase == CommandPhase.Execute && request.Binding.ExecuteOnModifier);
            if (replaceable)
            {
                // 删除旧等待项，再按新请求的到达位置入队，不能越过 End/Begin 或点击。
                if (wheel != null) { pending.Remove(wheel); wheel.Value = request; }
                else wheel = new(request);
                pending.AddLast(wheel);
            }
            else pending.AddLast(request);
            Monitor.Pulse(gate);
        }
    }

    internal IEnumerable<CommandRequest> GetConsumingEnumerable()
    {
        while (true)
        {
            CommandRequest request;
            lock (gate)
            {
                while (pending.Count == 0 && !completed) Monitor.Wait(gate);
                if (pending.First is not {} first) yield break;
                request = first.Value;
                if (first == wheel) wheel = null;
                pending.RemoveFirst();
            }
            yield return request;
        }
    }

    internal void CompleteAdding() { lock (gate) { completed = true; Monitor.PulseAll(gate); } }
}
