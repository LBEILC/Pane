// 设置窗口：备忘录（设计负责人，2026-11-25，原型 prototype/settings.html#memo，docs/design.md「备忘录」）：
// 呼出热键（默认不设，同分词）、从启动器存入的说明、存放位置和「打开文件夹」。
using Pane.Localization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Pane.Settings;
using Pane.Contracts;

namespace Pane.Views;

public sealed partial class SettingsView
{
    void RenderMemo()
    {
        var s = app.Store.Current;
        var rows = new List<FrameworkElement> { MemoHotkeyRow(s.Memo.Hotkey, s.Launcher.Hotkey) };
        if (ModuleGate.On(ModuleIds.Launcher))
            rows.Add(Row(Strings.Settings_Memo_FromLauncher, string.Format(Strings.Settings_Memo_FromLauncherDescription, (object?)(s.Launcher.MemoKeywords.FirstOrDefault() ?? "bw"))));
        Group(Strings.Settings_Clipboard_Activation, rows.ToArray());
        var folder = app.Notes?.Folder;
        var open = Button(Strings.Settings_Memo_OpenFolder, "folder-open", () => { if (folder != null) { try { Directory.CreateDirectory(folder); } catch { } OpenUrl(folder); } });
        Enable(open, folder != null);
        Group(Strings.Settings_Memo_Files, [Row(Strings.Settings_Memo_Folder, folder ?? "", open)], Strings.Settings_Memo_FilesDescription);
        RenderMemoTags();
    }

    // 标签（144，docs/design.md「备忘录标签」）：行同 设置 → 剪贴板 →「标签」（色点格、名字框、条数、6 个色点 + 无、删除）。和剪贴板标签是两套
    string? memoTagError;
    void RenderMemoTags()
    {
        if (app.Notes is not { } notes) return;
        var tags = notes.Tags;
        var counts = notes.All.SelectMany(n => n.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<FrameworkElement>();
        if (memoTagError != null) rows.Add(Row(Strings.Settings_Memo_Tags, null, Status(false, memoTagError)));
        foreach (var t in tags)
        {
            var name = t.Name;
            var box = Field(name, 150);
            box.Text = name;
            void Commit()
            {
                var v = box.Text.Trim();
                if (v == name) return;
                if (v.Length == 0) { memoTagError = Strings.Settings_Clipboard_TagInvalid; Later(0); return; }
                try { notes.UpdateTag(name, v, t.Color); memoTagError = null; }
                catch (ArgumentException e) { memoTagError = e.Message; }
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
                    try { notes.UpdateTag(name, name, color); memoTagError = null; } catch (ArgumentException e) { memoTagError = e.Message; }
                    Later(0);
                }));
            }
            var lead = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
                Child = Clipboard.TagColors.Dot(t.Color) is { } brush ? new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush }
                    : new Controls.PaneIcon { Glyph = "tag", Width = 15, Height = 15, Foreground = IconProto.Foreground } };
            var field = Wrap(box);
            field.HorizontalAlignment = HorizontalAlignment.Left;
            rows.Add(ListRow(lead, field, string.Format(Strings.Clipboard_TagCount, counts.GetValueOrDefault(name)), false,
                dots, IconButton("trash", Strings.Common_Delete, () => { notes.DeleteTag(name); memoTagError = null; Render(); })));
        }
        if (tags.Count == 0) rows.Add(Row(Strings.Settings_Memo_TagsEmpty, null));
        Group(Strings.Settings_Memo_Tags, rows.ToArray(), Strings.Settings_Memo_TagsNote);
    }

    // 热键可以不设（默认）；设了之后能改、能清除（照分词热键）
    FrameworkElement MemoHotkeyRow(Settings.Hotkey? hotkey, Settings.Hotkey launcher, string? title = null)
    {
        var label = title ?? Strings.Settings_Hotkey;
        if (hotkey is null)
            return Row(label, Strings.Settings_Gestures_HotkeyUnset, Button(Strings.Common_Settings, "plus", () => { SaveMemo(x => x with { Hotkey = new(KeyModifiers.Alt, (ushort)'N') }); Render(); }));
        var box = Hotkey(hotkey, launcher, h => SaveMemo(x => x with { Hotkey = h }), doubleTap: true);
        var clear = IconButton("close", Strings.Common_ClearValue, () => { SaveMemo(x => x with { Hotkey = null }); Render(); });
        var conflict = app.HotkeyConflicts.FirstOrDefault(x => x.Target == "memo");
        return conflict != null
            ? Row(label, Strings.Settings_Hotkey_ConflictDescription, Status(false, ConflictText(conflict)), box, clear)
            : Row(label, hotkey.DoubleTap ? Strings.Settings_Hotkey_DoubleTapHint : Strings.Settings_Memo_HotkeyDescription, box, clear);
    }

    void SaveMemo(Func<MemoSettings, MemoSettings> change) =>
        app.Store.Save(app.Store.Current with { Memo = change(app.Store.Current.Memo) });
}
