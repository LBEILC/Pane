using Pane.Contracts;
using Pane.Localization;

namespace Pane.Views;

// 定时关机的显示文字（设计负责人，058）：定时窗口、提醒、托盘共用，写法照 docs/design.md「定时关机」
static class PowerTimerText
{
    public static string Name(PowerAction a) => a switch
    {
        PowerAction.Shutdown => Strings.PowerTimer_Shutdown,
        PowerAction.Restart => Strings.PowerTimer_Restart,
        _ => Strings.PowerTimer_Sleep,
    };

    public static string Glyph(PowerAction a) => a switch { PowerAction.Shutdown => "power", PowerAction.Restart => "restart", _ => "moon" };

    /// <summary>「23:00」，不是今天的写「明天 07:30」。</summary>
    public static string Clock(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var hm = local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return local.Date == DateTime.Today ? hm : string.Format(Strings.PowerTimer_Tomorrow, (object?)hm);
    }

    /// <summary>「42 分钟」「1 小时 30 分钟」，不足一分钟按一分钟。</summary>
    public static string Span(TimeSpan left)
    {
        int m = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        if (m < 60) return string.Format(Strings.PowerTimer_Minutes, (object?)m);
        int h = m / 60, r = m % 60;
        return r == 0 ? string.Format(Strings.PowerTimer_Hours, (object?)h) : string.Format(Strings.PowerTimer_HoursMinutes, (object?)h, (object?)r);
    }

    /// <summary>「23:00 关机」。</summary>
    public static string State(PowerTimerState s) => string.Format(Strings.PowerTimer_AtTitle, (object?)Clock(s.At), (object?)Name(s.Action));

    /// <summary>倒计时大字：「42:18」，一小时以上「1:05:09」。</summary>
    public static string Countdown(TimeSpan left)
    {
        int s = Math.Max(0, (int)Math.Round(left.TotalSeconds));
        int h = s / 3600, m = s % 3600 / 60, sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }
}
