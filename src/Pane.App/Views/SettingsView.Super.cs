// 设置窗口：超级面板、文件对话框（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
using Pane.Localization;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.Foundation;
using G = Pane.Gestures;

namespace Pane.Views;

public sealed partial class SettingsView
{
    // ================= 超级面板 =================

    void RenderSuper()
    {
        var p = app.Store.Current.SuperPanel;
        var trigger = Seg([("X1", Strings.Settings_Super_BackButton), ("X2", Strings.Settings_Super_ForwardButton), ("Middle", Strings.Settings_Super_MiddleButton)], p.TriggerButton.ToString(),
            v => SaveSuper(x => x with { TriggerButton = Enum.Parse<SuperPanelButton>(v) }));
        // 2026-10-29 用户要求：改滑杆，0.1～1 秒自己调（原来是 0.3 / 0.4 / 0.6 三档）。拖动时只改读数，松手才存
        var holdValue = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = MonoProto.FontFamily, FontSize = 12, Foreground = SmallProto.Foreground };
        var hold = new PaneSlider(100, 1000, 50, Math.Clamp(p.HoldMilliseconds, 100, 1000), FillProto.Background, FillProto.BorderBrush, Accent());
        void HoldReadout(double v) => holdValue.Text = string.Format(Strings.Settings_Super_Seconds, (v / 1000).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture));
        HoldReadout(hold.Value);
        hold.ValueChanged += (_, v) => HoldReadout(v);
        hold.Committed += (_, _) => { int ms = (int)Math.Round(hold.Value); if (ms != app.Store.Current.SuperPanel.HoldMilliseconds) SaveSuper(x => x with { HoldMilliseconds = ms }); };
        var fullscreen = Switch(p.PauseInFullscreen, v => SaveSuper(x => x with { PauseInFullscreen = v }));
        var borrow = Switch(p.ClipboardFallback, v => SaveSuper(x => x with { ClipboardFallback = v }));
        Group(Strings.Settings_Super_Basic,
            Row(Strings.Settings_Super_Trigger, Strings.Settings_Super_TriggerDescription, trigger),
            Row(Strings.Settings_Super_HoldDuration, Strings.Settings_Super_HoldDurationDescription, hold, holdValue),
            Row(Strings.Settings_Super_DisableFullscreen, Strings.Settings_Super_DisableFullscreenDescription, fullscreen),
            Row(Strings.Settings_Super_BorrowClipboard, Strings.Settings_Super_BorrowClipboardDescription, borrow));

        var programs = p.DisabledPrograms.Select(path => ListRow("app", Path.GetFileNameWithoutExtension(path), path, false,
            IconButton("close", Strings.Common_Remove, () => { SaveSuper(x => x with { DisabledPrograms = x.DisabledPrograms.Where(v => !string.Equals(v, path, StringComparison.OrdinalIgnoreCase)).ToArray() }); Render(); }))).ToList();
        programs.Add(AddRow(Button(Strings.Settings_Super_AddProgram, "plus", async () =>
        {
            if (await PickProgram() is not { } exe) return;
            SaveSuper(x => x with { DisabledPrograms = x.DisabledPrograms.Any(v => string.Equals(v, exe, StringComparison.OrdinalIgnoreCase)) ? x.DisabledPrograms : [.. x.DisabledPrograms, exe] });
            Render();
        })));
        Group(Strings.Settings_Super_ExcludedPrograms, programs.ToArray(), Strings.Settings_Super_ExcludedProgramsDescription);
        RenderSuperActions(p);
    }

    // 面板里的动作（2026-10-30，原型 settings.js「面板里的动作」）：照启动器动作行，显示的在前、按顺序，可上移；关掉的排在后面。
    // 一排放 6 个，开满了其余的开关变灰。候选：AI 预设动作、搜索、复制
    void RenderSuperActions(SuperPanelSettings p)
    {
        var all = app.Store.Current.Ai.Actions.Select(a => ("ai:" + a.Id, a.Name, a.Glyph))
            .Append(("search", Strings.Common_Search, "globe")).Append(("copy", Strings.Common_Copy, "clipboard")).ToList();
        List<string> Shown(SuperPanelSettings s) =>
            (s.Actions ?? SuperPanelSettings.DefaultActions(app.Store.Current.Ai.Actions.Select(a => a.Id))).Where(id => all.Any(a => a.Item1 == id)).Distinct().ToList();
        var shown = Shown(p);
        var ordered = shown.Select(id => all.First(a => a.Item1 == id)).Concat(all.Where(a => !shown.Contains(a.Item1))).ToList();
        bool full = shown.Count >= SuperPanelSettings.MaxActions;
        var rows = new List<FrameworkElement>();
        foreach (var (id, name, glyph) in ordered)
        {
            bool on = shown.Contains(id);
            int pos = shown.IndexOf(id);
            var tools = new List<FrameworkElement>();
            if (on && pos > 0)
                tools.Add(IconButton("U", Strings.Common_MoveUp, () => { SaveSuper(x => { var list = Shown(x); (list[pos - 1], list[pos]) = (list[pos], list[pos - 1]); return x with { Actions = list.ToArray() }; }); Render(); }));
            var toggle = Switch(on, v =>
            {
                SaveSuper(x => { var list = Shown(x).Where(a => a != id).ToList(); if (v) list.Add(id); return x with { Actions = list.ToArray() }; });
                Render();
            });
            Enable(toggle, on || !full);
            tools.Add(toggle);
            rows.Add(ListRow(glyph, name, on ? string.Format(Strings.Settings_Super_ActionPosition, (object?)(pos + 1)) : full ? Strings.Settings_Super_ActionsFull : Strings.Settings_Super_ActionHidden, false, tools.ToArray()));
        }
        Group(Strings.Settings_Super_Actions, rows.ToArray(), Strings.Settings_Super_ActionsDescription);
    }

    void SaveSuper(Func<SuperPanelSettings, SuperPanelSettings> change) =>
        app.Store.Save(app.Store.Current with { SuperPanel = change(app.Store.Current.SuperPanel) });

    // 文件对话框跳转（2026-11-01，046，用户：Ctrl+G 也要能在设置里调）。两个组合键只在「打开 / 另存为」对话框在前台时注册，
    // 所以不和启动器、剪贴板的全局热键比较，只防两个互相重复；被占用的提示来自服务上次注册的结果。关掉总开关时其余各项变灰（同超级面板）
    void RenderFolderJump()
    {
        var f = app.Store.Current.FolderJump;
        var jump = Hotkey(f.Jump, f.Menu, h => SaveFolderJump(x => x with { JumpHotkey = h == FolderJumpSettings.DefaultJump ? null : h }));
        var menu = Hotkey(f.Menu, f.Jump, h => SaveFolderJump(x => x with { MenuHotkey = h == FolderJumpSettings.DefaultMenu ? null : h }));
        var auto = Switch(f.AutoJump, v => SaveFolderJump(x => x with { AutoJump = v }));
        var recent = Switch(f.ShowRecent, v => SaveFolderJump(x => x with { ShowRecent = v }));
        // 047（用户：开了三个资源管理器，Ctrl+G 只跳其中一个）：默认跳最近用过的那个（同 Listary），想自己挑的改成弹菜单
        var many = Seg([("recent", Strings.Settings_FolderJump_Recent), ("menu", Strings.Settings_FolderJump_Menu)], f.MenuWhenMany ? "menu" : "recent", v => SaveFolderJump(x => x with { MenuWhenMany = v == "menu" }));
        var problem = app.FolderJump?.HotkeyProblem;   // 总开关（046）挪到「能力」页（055）
        Group(Strings.Settings_FolderJump_Hotkeys,
            problem != null ? Row(Strings.Settings_FolderJump_CurrentFolder, Strings.Settings_Hotkey_ConflictDescription, Status(false, problem), jump)
                            : Row(Strings.Settings_FolderJump_CurrentFolder, Strings.Settings_FolderJump_CurrentFolderDescription, jump),
            Row(Strings.Common_ChooseFolder, Strings.Settings_FolderJump_ChooseFolderDescription, menu));
        Group(Strings.Settings_FolderJump_Behavior,
            Row(Strings.Settings_FolderJump_MultipleWindows, f.MenuWhenMany ? Strings.Settings_FolderJump_MultipleMenuDescription : Strings.Settings_FolderJump_MultipleRecentDescription, many),
            Row(Strings.Settings_FolderJump_Automatic, Strings.Settings_FolderJump_AutomaticDescription, auto),
            Row(Strings.Settings_FolderJump_ShowRecent, Strings.Settings_FolderJump_ShowRecentDescription, recent));
    }

    void SaveFolderJump(Func<FolderJumpSettings, FolderJumpSettings> change) =>
        app.Store.Save(app.Store.Current with { FolderJump = change(app.Store.Current.FolderJump) });

    // 选一个程序（.exe），返回完整路径（引擎按完整路径匹配）
    async Task<string?> PickProgram()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
