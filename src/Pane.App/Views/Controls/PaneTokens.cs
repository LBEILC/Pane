// 代码里取设计令牌（2026-10-31，组件库立规矩时加）：和 Themes/Tokens.xaml 一一对应。
// 在代码里建控件时用这里的值，不写死数字和颜色；对应原型 prototype/tokens.css。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views.Controls;

internal static class PaneTokens
{
    static double D(string key) => (double)Application.Current.Resources[key];

    /// <summary>控件高度四档：xs 小标签、行内小按钮；sm 文字按钮、图标按钮；md 输入框、下拉框、窗口按钮；lg 引导页大按钮。</summary>
    public static double ControlXs => D("PaneControlXs");
    public static double ControlSm => D("PaneControlSm");
    public static double ControlMd => D("PaneControlMd");
    public static double ControlLg => D("PaneControlLg");

    /// <summary>状态色：只用于出错 / 成功 / 留意的小字、小标签和危险操作。</summary>
    public static Brush Danger => (Brush)Application.Current.Resources["PaneDangerBrush"];
    public static Brush Success => (Brush)Application.Current.Resources["PaneSuccessBrush"];
    public static Brush Warning => (Brush)Application.Current.Resources["PaneWarningBrush"];
}
