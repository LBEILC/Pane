// 放置区（设计负责人，2026-11-04，057）：能把文件拖进来添加的列表。照原型 components.css 的 .drop-zone：
// 拖着文件经过时虚线描边 + 悬停底，原内容变淡，中间写「松开添加」；不用主题色。
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Pane.Hosting;
using Pane.Localization;
using Windows.ApplicationModel.DataTransfer;

namespace Pane.Views.Controls;

internal static class DropZone
{
    /// <summary>
    /// 把 <paramref name="content"/>（一组列表的外框）包成放置区。松开时把拖进来的完整路径交给 <paramref name="drop"/>；
    /// 只认文件和文件夹（StorageItems），别的拖拽不响应。
    /// </summary>
    public static Grid Wrap(FrameworkElement content, CornerRadius radius, Action<IReadOnlyList<string>> drop)
    {
        var host = new Grid { AllowDrop = true };
        host.Children.Add(content);
        var outline = new Rectangle { StrokeThickness = 1.5, StrokeDashArray = [3, 2], RadiusX = radius.TopLeft, RadiusY = radius.TopLeft };
        var hint = new TextBlock
        {
            Text = Strings.Settings_DropToAdd, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.Resources["PaneFontUi"], FontSize = (double)Application.Current.Resources["PaneTextMd"], FontWeight = FontWeights.SemiBold,
        };
        var overlay = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        overlay.Children.Add(outline);
        overlay.Children.Add(hint);
        host.Children.Add(overlay);

        void Show(bool on)
        {
            if (on)
            {
                // 按控件当前深浅色取令牌（换主题后再拖也对）
                var theme = (ResourceDictionary)GlassParameters.Tokens.ThemeDictionaries[host.ActualTheme == ElementTheme.Dark ? "Dark" : "Light"];
                SolidColorBrush Brush(string key) => new((Windows.UI.Color)theme[key]);
                outline.Fill = Brush("PaneFillHoverColor");
                outline.Stroke = Brush("PaneText3Color");
                hint.Foreground = Brush("PaneText1Color");
            }
            overlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            content.Opacity = on ? 0.3 : 1;
        }

        host.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Strings.Settings_DropToAdd;
            Show(true);
        };
        host.DragLeave += (_, _) => Show(false);
        host.Drop += async (_, e) =>
        {
            Show(false);
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0) drop(paths);
        };
        return host;
    }
}
