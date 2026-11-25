// 设置窗口：备忘录（设计负责人，2026-11-25，原型 prototype/settings.html#memo，docs/design.md「备忘录」）：
// 呼出热键（默认不设，同分词）、从启动器存入的说明、存放位置和「打开文件夹」。
using Pane.Localization;
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
