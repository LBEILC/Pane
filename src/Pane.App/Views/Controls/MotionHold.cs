namespace Pane.Views.Controls;

// 控件自带的过渡动画（开关滑块、分段高亮）正在播放的截止时间。
// 整页重建的视图（设置页 Render）重建前先看这里，没播完就推迟，免得刚拨的控件被换掉、动画截成跳变。
// 2026-11-08 「自动更新」开关又被截断（2026-10-31 AI 页修过一次同类问题），改成在重建入口统一拦。
internal static class MotionHold
{
    static long until;

    /// <summary>登记一段动画（毫秒，已乘动效速度）。</summary>
    public static void Hold(double ms) => until = Math.Max(until, Environment.TickCount64 + (long)Math.Ceiling(ms));

    /// <summary>还剩多少毫秒播完；没有在播的返回 0。</summary>
    public static double Remaining => Math.Max(0, until - Environment.TickCount64);
}
