// 键盘焦点光环（原型 :focus-visible：外圈 2px 主题色 45%）。只在用 Tab / 方向键移到控件上时出现，
// 鼠标点出来的焦点不画；替代系统默认的黑白焦点框。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views.Controls;

internal static class FocusRing
{
    /// <param name="host">控件内容的根 Grid，光环叠在它上面，向外扩 3px。</param>
    /// <param name="radius">控件本身的圆角；光环圆角 = radius + 3。</param>
    public static void Attach(Control owner, Grid host, double radius)
    {
        owner.UseSystemFocusVisuals = false;
        uint argb = Convert.ToUInt32(((App)Application.Current).Glass.Current.AccentColor[1..], 16);
        var ring = new Border
        {
            Margin = new Thickness(-3), CornerRadius = new CornerRadius(radius + 3), BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x73, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        host.Children.Add(ring);
        owner.GotFocus += (_, _) => ring.Visibility = owner.FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed;
        owner.LostFocus += (_, _) => ring.Visibility = Visibility.Collapsed;
    }
}
