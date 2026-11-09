// 设置窗口：分词（设计负责人，093）。docs/design.md「分词」：呼出热键（默认不设，可清除）、从剪贴板打开的说明、复制后关闭窗口、拖选时包含标点。
// 拼接方式不在这里设，分词窗口里选了就记住。
using Pane.Localization;
using Microsoft.UI.Xaml;
using Pane.Settings;
using Pane.Contracts;

namespace Pane.Views;

public sealed partial class SettingsView
{
    void RenderWords()
    {
        var s = app.Store.Current;
        var w = s.Words;
        var rows = new List<FrameworkElement> { WordsHotkeyRow(w.Hotkey, s.Launcher.Hotkey) };
        if (ModuleGate.On(ModuleIds.Clipboard)) rows.Add(Row(Strings.Settings_Words_FromClipboard, Strings.Settings_Words_FromClipboardDescription));
        Group(Strings.Settings_Clipboard_Activation, rows.ToArray());
        Group(Strings.Settings_Words_Window,
            Row(Strings.Settings_Words_CloseAfterCopy, Strings.Settings_Words_CloseAfterCopyDescription, Switch(w.CloseAfterCopy, v => SaveWords(x => x with { CloseAfterCopy = v }))),
            Row(Strings.Settings_Words_DragPunctuation, Strings.Settings_Words_DragPunctuationDescription, Switch(w.DragPunctuation, v => SaveWords(x => x with { DragPunctuation = v }))));
    }

    // 热键可以不设（默认）；设了之后能改、能清除。照手势的暂停热键
    FrameworkElement WordsHotkeyRow(Settings.Hotkey? hotkey, Settings.Hotkey launcher)
    {
        var label = Strings.Settings_Hotkey;
        if (hotkey is null)
            return Row(label, Strings.Settings_Gestures_HotkeyUnset, Button(Strings.Common_Settings, "plus", () => { SaveWords(x => x with { Hotkey = new(KeyModifiers.Alt, (ushort)'Q') }); Render(); }));
        var box = Hotkey(hotkey, launcher, h => SaveWords(x => x with { Hotkey = h }), doubleTap: true);
        var clear = IconButton("close", Strings.Common_ClearValue, () => { SaveWords(x => x with { Hotkey = null }); Render(); });
        var conflict = app.HotkeyConflicts.FirstOrDefault(x => x.Target == "words");
        return conflict != null
            ? Row(label, Strings.Settings_Hotkey_ConflictDescription, Status(false, ConflictText(conflict)), box, clear)
            : Row(label, hotkey.DoubleTap ? Strings.Settings_Hotkey_DoubleTapHint : Strings.Settings_Words_HotkeyDescription, box, clear);
    }

    void SaveWords(Func<WordsSettings, WordsSettings> change) =>
        app.Store.Save(app.Store.Current with { Words = change(app.Store.Current.Words) });
}
