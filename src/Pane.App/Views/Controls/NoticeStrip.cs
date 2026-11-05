using Pane.Localization;
// 提示条（设计负责人，issue 25、062；组件库 .notice-strip）：启动器、剪贴板面板最上面的一条，高 40。
// 两种内容：新版本已下载好（重启更新 / 忽略）、更新后（查看 / ×）。只能用鼠标点：按钮都不进 Tab 顺序，Enter 照旧给列表。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views.Controls;

public enum PanelNoticeKind { UpdateReady, Updated }

/// <param name="Detail">标题后面的次要文字：「新增 3 项、修复 2 项」或第一条重点。</param>
public sealed record PanelNotice(PanelNoticeKind Kind, string Version, string Detail);

public sealed partial class NoticeStrip : UserControl
{
    public const double StripHeight = 40;
    readonly PaneIcon icon = new() { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    readonly TextBlock detail = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button primary = new(), quiet = new(), close = new();

    public PanelNotice? Notice { get; private set; }
    /// <summary>重启更新 / 查看。</summary>
    public event EventHandler<PanelNotice>? Primary;
    /// <summary>忽略 / ×。</summary>
    public event EventHandler<PanelNotice>? Dismiss;

    public NoticeStrip()
    {
        Height = StripHeight;
        Visibility = Visibility.Collapsed;
        var res = Application.Current.Resources;
        var font = (FontFamily)res["PaneFontUi"];
        double sm = (double)res["PaneTextSm"], xs = (double)res["PaneTextXs"], small = (double)res["PaneControlXs"];
        title.FontFamily = detail.FontFamily = font;
        title.FontSize = detail.FontSize = sm;
        title.Foreground = icon.Foreground = (Brush)res["PaneText1Brush"];
        detail.Foreground = (Brush)res["PaneText3Brush"];

        primary.Style = (Style)res["PanePrimaryButton"];
        primary.Height = primary.MinHeight = small; primary.Padding = new Thickness(10, 0, 10, 0); primary.FontSize = xs; primary.CornerRadius = new CornerRadius(small / 2);
        quiet.Style = (Style)res["PaneTextButton"];
        quiet.Height = quiet.MinHeight = small; quiet.Padding = new Thickness(8, 0, 8, 0); quiet.FontSize = xs;
        close.Style = (Style)res["PaneIconButton"];
        close.Width = close.Height = small;
        close.Content = new PaneIcon { Glyph = "close", Width = 14, Height = 14 };
        ToolTipService.SetToolTip(close, Strings.Notice_Dismiss);
        foreach (var b in new[] { primary, quiet, close }) { b.IsTabStop = false; b.VerticalAlignment = VerticalAlignment.Center; }
        primary.Click += (_, _) => { if (Notice is { } n) Primary?.Invoke(this, n); };
        quiet.Click += (_, _) => { if (Notice is { } n) { if (n.Kind == PanelNoticeKind.Updated) Primary?.Invoke(this, n); else Dismiss?.Invoke(this, n); } };
        close.Click += (_, _) => { if (Notice is { } n) Dismiss?.Invoke(this, n); };

        // 标题不缩，次要文字占剩下的宽度、放不下就省略
        var text = new Grid { VerticalAlignment = VerticalAlignment.Center };
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        text.Children.Add(title); Grid.SetColumn(detail, 1); text.Children.Add(detail);
        var grid = new Grid { Padding = new Thickness(14, 0, 8, 0), ColumnSpacing = 10 };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(primary, 2); grid.Children.Add(primary);
        Grid.SetColumn(quiet, 3); grid.Children.Add(quiet);
        Grid.SetColumn(close, 4); grid.Children.Add(close);
        Content = new Border { BorderBrush = (Brush)res["PaneStrokeBrush"], BorderThickness = new Thickness(0, 0, 0, 1), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = grid };
    }

    /// <summary>显示一条提示；null 收起。返回这条提示占的高度（0 或 40）。</summary>
    public double Show(PanelNotice? notice)
    {
        Notice = notice;
        if (notice is null) { Visibility = Visibility.Collapsed; return 0; }
        bool ready = notice.Kind == PanelNoticeKind.UpdateReady;
        icon.Glyph = ready ? "download" : "news";
        title.Text = string.Format(ready ? Strings.Notice_UpdateReady : Strings.Notice_Updated, notice.Version);
        detail.Text = notice.Detail;
        primary.Content = Strings.Update_RestartAndUpdate;
        primary.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        quiet.Content = ready ? Strings.Notice_Ignore : Strings.Notice_View;
        close.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        return StripHeight;
    }
}
