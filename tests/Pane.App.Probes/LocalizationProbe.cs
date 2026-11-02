using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views;

public sealed partial class TrayMenuView
{
    // 只由开发控制通道调用；读取真实 XAML 菜单的绑定结果，无需占用前台。
    internal string[] LocalizationProbe()
    {
        Apply(new("1.2.3", [], [], true, false, [], true));
        Bindings.Update();
        var text = new List<string>();
        void Read(DependencyObject node)
        {
            if (node is TextBlock block && block.Text.Length > 0) text.Add(block.Text);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Read(VisualTreeHelper.GetChild(node, i));
        }
        Read(Root);
        return text.ToArray();
    }
}
