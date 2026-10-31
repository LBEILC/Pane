// 组合键键帽（组件库 .kcombo，2026-10-31）：把 "Alt 1"、"Ctrl+Enter" 这样的一个字符串画成「每个键一个键帽、中间用 + 连接」。
// 用法：<StackPanel c:KeyCombo.Text="{x:Bind Hotkey}"/>。底部提示那种「键帽 + 说明」成组的用 KeyHints。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Pane.Views.Controls;

public static class KeyCombo
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(KeyCombo), new PropertyMetadata(null, OnText));

    public static string? GetText(StackPanel target) => (string?)target.GetValue(TextProperty);
    public static void SetText(StackPanel target, string? value) => target.SetValue(TextProperty, value);

    static void OnText(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackPanel panel) return;
        panel.Orientation = Orientation.Horizontal;
        panel.Spacing = 2;
        panel.Children.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(text)) return;
        var res = Application.Current.Resources;
        var keys = text.Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = "+", VerticalAlignment = VerticalAlignment.Center,
                    FontSize = (double)res["PaneTextXs"], Foreground = (Microsoft.UI.Xaml.Media.Brush)res["PaneText3Brush"],
                });
            panel.Children.Add(new Border
            {
                Style = (Style)res["PaneKbdBorder"], VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Style = (Style)res["PaneKbdText"], Text = keys[i] },
            });
        }
    }
}
