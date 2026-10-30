// 手势轨迹和提示框的演示（截图用，设计负责人）。只在演示模式下由控制文件触发，不装钩子、不发输入。
// 控制文件 .gesture 的内容：「状态;方向;修饰;命令名;路点」，例如
//   Drawing;4,2;None;关闭标签页;-200,-150 -200,40 40,40
// 可选第 6 段是失败原因；路点为空时不画轨迹，直接显示提示（预览面板动作失败：Failed;;None;粘贴;;原来的窗口已经关了）
// 方向是 GestureArrow 的数值（逗号分隔，可空）；路点是相对主显示器工作区中心的 DIP 偏移，空格分隔。
// 状态为 Drawing 时轨迹保持不松开；其他状态先画完、再松开（轨迹开始淡出）后显示该状态；Released 是松开了但没有结果提示。
using System.Globalization;
using System.Runtime.InteropServices;
using Pane.Contracts;
using Pane.Hosting;

namespace Pane;

public sealed partial class App
{
    void DemoGesture(string spec)
    {
        var parts = spec.Trim().Split(';');
        // Grow;方向;间隔毫秒：不画轨迹，按间隔一个个加箭头（快速画长手势），验证提示框跟得上、内容不晃
        if (parts[0].Equals("Grow", StringComparison.OrdinalIgnoreCase))
        {
            var all = parts[1].Split(',').Select(a => (GestureArrow)int.Parse(a, CultureInfo.InvariantCulture)).ToArray();
            int n = 0;
            gestures!.HideAll();
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(int.Parse(parts[2], CultureInfo.InvariantCulture));
            timer.Tick += (_, _) => { n++; gestures!.Hint(new(GestureHintKind.Drawing, all[..n], GestureHintModifier.None, null)); if (n == all.Length) { timer.Stop(); gestures.HoldHint(); } };
            gestures.Hint(new(GestureHintKind.Drawing, [], GestureHintModifier.None, null));
            timer.Start();
            return;
        }
        // Released：画一小段就松开、没有任何结果提示（取消 / 按点击补发的情形），验证「…」会自己收起
        bool released = parts[0].Equals("Released", StringComparison.OrdinalIgnoreCase);
        var kind = released ? GestureHintKind.Drawing : Enum.Parse<GestureHintKind>(parts[0], true);
        var arrows = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => (GestureArrow)int.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        var modifier = Enum.Parse<GestureHintModifier>(parts[2], true);
        string? name = parts[3].Length == 0 ? null : parts[3];
        var waypoints = parts[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(','))
            .Select(p => (X: double.Parse(p[0], CultureInfo.InvariantCulture), Y: double.Parse(p[1], CultureInfo.InvariantCulture))).ToArray();

        var monitor = GlassPanel.MonitorFromPoint(new GlassPanel.Point(), 2);
        var info = new GlassPanel.MonitorInfo { Size = Marshal.SizeOf<GlassPanel.MonitorInfo>() };
        GlassPanel.GetMonitorInfoW(monitor, ref info);
        GlassPanel.GetDpiForMonitor(monitor, 0, out var dx, out _);
        double scale = (dx == 0 ? 96 : dx) / 96.0;
        double cx = (info.Work.Left + info.Work.Right) / 2.0, cy = (info.Work.Top + info.Work.Bottom) / 2.0;
        (int, int) At(double x, double y) => ((int)Math.Round(cx + x * scale), (int)Math.Round(cy + y * scale));

        var g = gestures!;
        g.HideAll();
        string? detail = parts.Length > 5 && parts[5].Length > 0 ? parts[5] : kind == GestureHintKind.Failed ? "目标窗口已关闭" : null;
        if (waypoints.Length == 0) { g.Hint(new(kind, arrows, modifier, name, detail)); g.HoldHint(); return; }
        var (sx, sy) = At(waypoints[0].X, waypoints[0].Y);
        g.TrailBegin(sx, sy);
        for (int i = 1; i < waypoints.Length; i++)
        {
            var a = waypoints[i - 1]; var b = waypoints[i];
            int steps = Math.Max(1, (int)(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / 4));
            for (int s = 1; s <= steps; s++)
            {
                double t = s / (double)steps;
                // 手画的线不会笔直：加一点点抖动
                double wobble = Math.Sin((i * 31 + s) * 0.35) * 1.2;
                var (x, y) = At(a.X + (b.X - a.X) * t + wobble, a.Y + (b.Y - a.Y) * t - wobble);
                g.TrailMove(x, y);
            }
        }
        if (released) { g.Hint(new(kind, arrows, modifier, name)); g.TrailEnd(); return; }
        if (kind == GestureHintKind.Drawing) { g.Hint(new(kind, arrows, modifier, name)); return; }
        g.Hint(new(GestureHintKind.Drawing, arrows, modifier, name));
        g.TrailEnd();
        g.Hint(new(kind, arrows, modifier, name, detail));
        g.HoldHint();
    }
}
