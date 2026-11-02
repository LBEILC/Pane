using Pane.Localization;
// 手势的轨迹和提示框（设计负责人）：何时出现、何时收起、放在哪里。
// 宿主（工程接线）把 Pane.Gestures 的事件翻译过来：
//   TrailSample.Begin / Move / End → TrailBegin / TrailMove / TrailEnd（物理像素）
//   Progress → Hint(Drawing)；Finished → Hint(Executed / NotRecognized / Failed)，ReplayedClick（没移动，补发右键）→ Dismiss
//   暂停热键 → Hint(Notice, "手势已暂停" / "手势已恢复")
// 设置里的「显示轨迹 / 轨迹淡出 / 显示命令名」对应 ShowTrail / FadeTrail / ShowHint。
using Microsoft.UI.Xaml;
using Pane.Contracts;
using Pane.Hosting;
using Windows.UI;

namespace Pane.Views;

public sealed class GestureOverlay : IDisposable
{
    readonly GlassParameters glass;
    readonly GestureHintView view = new();
    readonly GestureTrail trail = new();
    internal readonly GlassPanel Panel;
    (int X, int Y)? anchor;
    bool freshGesture;
    long hideAt;

    public GestureOverlay(GlassParameters glass)
    {
        this.glass = glass;
        // 不可交互：输入窗也是分层 + 鼠标穿透 + 不激活，提示框完全不挡鼠标、不抢焦点
        Panel = new GlassPanel(Strings.Gestures_HintTitle, view, view, glass, interactive: false) { Placement = PanelPlacement.Center };
        Panel.TickRequested += () => TickRequested?.Invoke();
        Panel.Prepare();
    }

    public bool ShowTrail { get; set; } = true;
    public bool FadeTrail { get; set; } = true;
    public bool ShowHint { get; set; } = true;
    internal bool Settled => Panel.Settled && trail.Settled;
    internal bool Active => Panel.Active || trail.Active || hideAt != 0;
    internal event Action? TickRequested;

    /// <param name="color">按触发键设的轨迹颜色；null = 主题色。</param>
    public void TrailBegin(int x, int y, Color? color = null)
    {
        anchor = (x, y);
        freshGesture = true;
        if (!ShowTrail) return;
        trail.MotionSpeed = glass.Current.MotionSpeed;
        trail.Begin(x, y, color ?? Accent());
        TickRequested?.Invoke();
    }

    public void TrailMove(int x, int y)
    {
        anchor = (x, y);
        if (ShowTrail) trail.Move(x, y);
    }

    public void TrailEnd()
    {
        trail.End(FadeTrail);
        // 松开后还停在「画的过程中」的提示（取消、超时、按点击补发等没有结果提示的路径）：兜底收起。
        // 有结果提示时它会带着自己的停留时间覆盖掉这个
        if (Panel.Shown && hideAt == 0) hideAt = Environment.TickCount64 + (long)(600 * glass.Current.MotionSpeed);
        TickRequested?.Invoke();
    }

    /// <summary>这一笔被当成普通点击补发了：提示立刻收起。</summary>
    public void Dismiss()
    {
        hideAt = 0;
        if (Panel.Shown) Panel.Hide();
    }

    public void Hint(GestureHint hint)
    {
        if (!ShowHint) return;
        // 上一笔的提示还没收起时又开始画：清空箭头，从头逐个弹出
        if (freshGesture) { freshGesture = false; view.Reset(); }
        var size = view.Apply(hint);
        if (!Panel.Shown)
        {
            Panel.Width = size.Width; Panel.Height = size.Height;
            // 放在手势所在显示器的工作区正中（Placement.Center）
            if (anchor is { } p) Panel.Show(true, p.X, p.Y, false);
            else Panel.Show(activate: false);
        }
        double delay = hint.Kind switch
        {
            GestureHintKind.Executed => 520,
            GestureHintKind.NotRecognized => 420,
            GestureHintKind.Cancelled => 700,
            GestureHintKind.Failed => 2400,   // 带一句原因，留够读的时间
            GestureHintKind.Notice => 1200,
            _ => 0,
        };
        hideAt = delay == 0 ? 0 : Environment.TickCount64 + (long)(delay * glass.Current.MotionSpeed);
    }

    // 演示截图用：让当前提示一直留着
    internal void HoldHint() => hideAt = 0;

    public void HideAll()
    {
        hideAt = 0;
        trail.End(false);
        Panel.Hide();
    }

    /// <summary>宿主的界面定时器里调用。</summary>
    public void Tick()
    {
        trail.Tick();
        if (hideAt != 0 && Environment.TickCount64 >= hideAt) { hideAt = 0; Panel.Hide(); }
        Panel.Tick();
    }

    Color Accent()
    {
        uint argb = Convert.ToUInt32(glass.Current.AccentColor[1..], 16);
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    public void Dispose() { trail.Dispose(); Panel.Dispose(); }
}
