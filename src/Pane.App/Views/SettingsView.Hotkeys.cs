// 设置窗口：快捷键总览（设计负责人，2026-11-23，原型 prototype/settings.html#hotkeys）。
// 全部热键集中一页，行和各分区里的同一行共用（改哪边都一样）；停用的能力不列。应用快捷键（计划 B）做好后再加一组。
using Pane.Contracts;
using Pane.Localization;
using Microsoft.UI.Xaml;

namespace Pane.Views;

public sealed partial class SettingsView
{
    void RenderHotkeys()
    {
        var s = app.Store.Current;
        var global = new List<FrameworkElement>();
        if (ModuleGate.On(ModuleIds.Launcher)) global.Add(LauncherHotkeyRow(Strings.Common_Launcher));
        if (ModuleGate.On(ModuleIds.Clipboard)) global.Add(ClipboardHotkeyRow(Strings.Common_Clipboard));
        if (ModuleGate.On(ModuleIds.Words)) global.Add(WordsHotkeyRow(s.Words.Hotkey, s.Launcher.Hotkey, Strings.Module_Words_Name));
        if (ModuleGate.On(ModuleIds.Gestures))
        {
            try { global.Add(PauseHotkeyRow(app.GestureConfig.Settings.PauseHotkey)); }
            catch (Exception) { }   // 手势配置读不出来时这一行不列，手势分区里有说明
        }
        if (global.Count > 0) Group(Strings.Settings_Hotkeys_Activation, global.ToArray(), Strings.Settings_Hotkeys_ActivationNote);
        if (ModuleGate.On(ModuleIds.FolderJump)) Group(Strings.Settings_Hotkeys_Dialog, FolderJumpHotkeyRows(), Strings.Settings_Hotkeys_DialogNote);
    }
}
