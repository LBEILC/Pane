// 设置窗口：剪贴板的「外观」和「标签」两组（设计负责人，2026-11-23）。docs/design.md「剪贴板操作列表、富文本、标签、编辑、高度」，原型 prototype/settings.html#clipboard。
// 外观：按键提示三档（默认收起为按钮）、面板高度（拖动面板边缘改，这里显示当前值和恢复默认）。
// 标签：改名（输入框，回车或离开时保存）、色点（无 + 6 个低饱和色，选中的外面一圈主题色环）、删除（只去掉标签，不删内容）。
using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;
using Pane.Views.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views;

public sealed partial class SettingsView
{
    string? tagError;

    void RenderClipboardLook(ClipboardSettings c)
    {
        var hints = Seg([("Always", Strings.Settings_Clipboard_HintsAlways), ("Button", Strings.Settings_Clipboard_HintsButton), ("Off", Strings.Settings_Clipboard_HintsOff)],
            c.KeyHints.ToString(), v => SaveClipboard(x => x with { KeyHints = Enum.Parse<ClipKeyHints>(v) }));
        var heightRow = c.PanelHeight > 0
            ? Row(Strings.Settings_Clipboard_PanelHeight, string.Format(Strings.Settings_Clipboard_PanelHeightDescription, Math.Round(c.PanelHeight)),
                Button(Strings.Common_ResetDefaults, "refresh", () => { SaveClipboard(x => x with { PanelHeight = 0 }); Render(); }))
            : Row(Strings.Settings_Clipboard_PanelHeight, Strings.Settings_Clipboard_PanelHeightDefault);
        Group(Strings.Settings_Appearance, Row(Strings.Settings_Clipboard_KeyHints, Strings.Settings_Clipboard_KeyHintsDescription, hints), heightRow);
    }

    void RenderClipboardTags()
    {
        var history = app.ClipboardHistory;
        var tags = history.Tags();
        var counts = history.Snapshot().SelectMany(e => e.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<FrameworkElement>();
        if (tagError != null) rows.Add(Row(Strings.Settings_Clipboard_Tags, null, Status(false, tagError)));
        foreach (var t in tags)
        {
            var name = t.Name;
            var box = Field(name, 150);
            box.Text = name;
            void Commit()
            {
                var v = box.Text.Trim();
                if (v == name) return;
                if (v.Length == 0) { tagError = Strings.Settings_Clipboard_TagInvalid; Later(0); return; }
                try { history.UpdateTag(name, v, t.Color); tagError = null; }
                catch (ArgumentException e) { tagError = e.Message; }
                Later(0);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i <= 6; i++)
            {
                int? color = i == 0 ? null : i;
                dots.Children.Add(TagDotButton(color, t.Color == color, () =>
                {
                    try { history.UpdateTag(name, name, color); tagError = null; } catch (ArgumentException e) { tagError = e.Message; }
                    Later(0);
                }));
            }
            var lead = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
                Child = TagColors.Dot(t.Color) is { } brush ? new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush }
                    : new Controls.PaneIcon { Glyph = "tag", Width = 15, Height = 15, Foreground = IconProto.Foreground } };
            var field = Wrap(box);
            field.HorizontalAlignment = HorizontalAlignment.Left;
            rows.Add(ListRow(lead, field, string.Format(Strings.Clipboard_TagCount, counts.GetValueOrDefault(name)), false,
                dots, IconButton("trash", Strings.Common_Delete, () => { history.DeleteTag(name); tagError = null; Render(); })));
        }
        if (tags.Count == 0) rows.Add(Row(Strings.Settings_Clipboard_TagsEmpty, null));
        Group(Strings.Settings_Clipboard_Tags, rows.ToArray(), Strings.Settings_Clipboard_TagsNote);
    }

    // 色点按钮：20 见方；「无」是一圈细描边的空心圆；选中的外面隔 2 再套一圈 2 的主题色环（同「选中式小标签」用主题色描边）
    Border TagDotButton(int? color, bool selected, Action pick)
    {
        var fill = TagColors.Dot(color);
        var inner = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = fill, BorderBrush = fill is null ? IconProto.Foreground : null, BorderThickness = new Thickness(fill is null ? 1 : 0) };
        var ring = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Child = inner,
            BorderBrush = selected ? (Brush)Application.Current.Resources["PaneAccentBrush"] : null, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(ring, color is null ? Strings.Settings_Clipboard_TagNoColor : $"{Strings.Settings_Clipboard_TagColor} {color}");
        ring.Tapped += (_, _) => pick();
        return ring;
    }
}
