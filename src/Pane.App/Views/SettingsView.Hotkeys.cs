// 设置窗口：快捷键总览（设计负责人，2026-11-23，原型 prototype/settings.html#hotkeys）。
// 全部热键集中一页，行和各分区里的同一行共用（改哪边都一样）；停用的能力不列。
// 应用快捷键（计划 B，2026-11-25，任务 122）：按下启动应用、已运行就切到它；也能从启动器 Ctrl+K「设置快捷键」加进来。
using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        if (ModuleGate.On(ModuleIds.Memo)) global.Add(MemoHotkeyRow(s.Memo.Hotkey, s.Launcher.Hotkey, Strings.Memo_Title));
        if (ModuleGate.On(ModuleIds.Gestures))
        {
            try { global.Add(PauseHotkeyRow(app.GestureConfig.Settings.PauseHotkey)); }
            catch (Exception) { }   // 手势配置读不出来时这一行不列，手势分区里有说明
        }
        // 全屏程序中不响应连按两下（issue 80，任务 154）：只管连按两下修饰键，有启动器或剪贴板时列在全局组末尾
        if (ModuleGate.On(ModuleIds.Launcher) || ModuleGate.On(ModuleIds.Clipboard))
            global.Add(Row(Strings.Settings_Hotkeys_DoubleTapFullscreen, Strings.Settings_Hotkeys_DoubleTapFullscreenDescription,
                Switch(s.General.DoubleTapPauseInFullscreen, v => SaveGeneral(g => g with { DoubleTapPauseInFullscreen = v }))));
        if (global.Count > 0) Group(Strings.Settings_Hotkeys_Activation, global.ToArray(), Strings.Settings_Hotkeys_ActivationNote);
        if (ModuleGate.On(ModuleIds.FolderJump)) Group(Strings.Settings_Hotkeys_Dialog, FolderJumpHotkeyRows(), Strings.Settings_Hotkeys_DialogNote);
        if (ModuleGate.On(ModuleIds.Launcher)) Group(Strings.Settings_Hotkeys_Apps, AppHotkeyRows(), Strings.Settings_Hotkeys_AppsNote);
    }

    /// <summary>启动器 Ctrl+K「设置快捷键」加进来的那一项：打开快捷键页时把它的热键框设为正在录入。</summary>
    internal static string? FocusAppHotkey { get; set; }
    string? appHotkeyQuery;   // 非 null = 正在挑要添加的应用

    FrameworkElement[] AppHotkeyRows()
    {
        var launcher = app.Store.Current.Launcher;
        var apps = app.AppIcons.Apps().ToDictionary(a => a.Id);
        var rows = new List<FrameworkElement>();
        // 设计预览：PANE_DESIGN_APPHOTKEYS=1 时拿应用目录前两个做样例（第一个生效、第二个被占用），只显示不保存
        var list = launcher.AppHotkeys;
        var demo = launcher.AppHotkeys.Length == 0 && Environment.GetEnvironmentVariable("PANE_DESIGN_APPHOTKEYS") == "1";
        if (demo) list = apps.Values.Take(2).Select((a, i) => new AppHotkey(a.Id, a.Name, new(KeyModifiers.Ctrl | KeyModifiers.Alt, i == 0 ? 'W' : 'C'))).ToArray();
        foreach (var h in list)
        {
            apps.TryGetValue(h.AppId, out var info);
            var state = demo ? (h == list[0] ? AppHotkeyState.Active : AppHotkeyState.Occupied) : app.AppHotkeys?.State(h.AppId) ?? AppHotkeyState.Off;
            var sub = state switch
            {
                AppHotkeyState.Active => Strings.Settings_Hotkeys_AppSub,
                AppHotkeyState.Occupied => Strings.Settings_Hotkeys_AppOccupied,
                AppHotkeyState.AppMissing => Strings.Settings_Hotkeys_AppMissing,
                _ => Strings.Settings_Hotkeys_AppOff,
            };
            var entry = h;
            var box = Hotkey(h.Hotkey, launcher.Hotkey, k => SaveLauncher(x => x with { AppHotkeys = x.AppHotkeys.Select(a => a.AppId == entry.AppId ? a with { Hotkey = k } : a).ToArray() }));
            if (FocusAppHotkey == h.AppId) { FocusAppHotkey = null; box.Loaded += (_, _) => box.Focus(FocusState.Programmatic); }
            var remove = IconButton("close", Strings.Common_Remove, () => { SaveLauncher(x => x with { AppHotkeys = x.AppHotkeys.Where(a => a.AppId != entry.AppId).ToArray() }); Render(); });
            var label = Text(info?.Name ?? h.Name, LabelProto);
            rows.Add(state == AppHotkeyState.Occupied
                ? ListRow(AppIcon(info?.Icon), label, Strings.Settings_Hotkeys_AppSub, false, Status(false, sub), box, remove)
                : ListRow(AppIcon(info?.Icon), label, sub, false, box, remove));
        }
        rows.Add(appHotkeyQuery == null
            ? AddRow(Button(Strings.Settings_Hotkeys_AddApp, "plus", () => { appHotkeyQuery = ""; revealKey = "apphotkey"; Render(); }))
            : Reveal("apphotkey", [AppHotkeyPicker(apps.Values, launcher)]));
        return rows.ToArray();
    }

    // 添加应用：搜索框 + 最多 6 个候选（同 外观 → 应用图标 的挑选），点一个就加上，热键先给一个没被占用的 Ctrl+Alt+字母，框直接进入录入
    FrameworkElement AppHotkeyPicker(IEnumerable<AppIconInfo> all, LauncherSettings launcher)
    {
        var taken = launcher.AppHotkeys.Select(a => a.AppId).ToHashSet();
        var candidates = all.Where(a => !taken.Contains(a.Id)).ToList();
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(14, 10, 14, 12) };
        var box = Field(Strings.Settings_Hotkeys_SearchApp, 300);
        box.Text = appHotkeyQuery ?? "";
        var results = new StackPanel { Spacing = 2 };
        void Fill()
        {
            results.Children.Clear();
            var q = (appHotkeyQuery ?? "").Trim();
            if (q.Length == 0) return;
            foreach (var a in candidates.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(6))
            {
                var pick = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                pick.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = AppIcon(a.Icon);
                icon.Width = icon.Height = 24;
                pick.Children.Add(icon);
                var name = Text(a.Name, LabelProto);
                Grid.SetColumn(name, 1);
                pick.Children.Add(name);
                pick.PointerEntered += (_, _) => pick.Background = SelectedProto.Background;
                pick.PointerExited += (_, _) => pick.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                pick.Tapped += (_, _) => { appHotkeyQuery = null; AddAppHotkey(a.Id, a.Name); Render(); };
                results.Children.Add(pick);
            }
            if (results.Children.Count == 0) results.Children.Add(Text(Strings.Common_NotFound, DescProto));
        }
        box.TextChanged += (_, _) => { appHotkeyQuery = box.Text; Fill(); };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Collapse("apphotkey", () => { appHotkeyQuery = null; Render(); }); } };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var cancel = Button(Strings.Common_Cancel, null, () => Collapse("apphotkey", () => { appHotkeyQuery = null; Render(); }));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, cancel } };
        panel.Children.Add(line);
        panel.Children.Add(results);
        Fill();
        return panel;
    }

    /// <summary>加一项应用快捷键（设置页「添加应用」、启动器 Ctrl+K「设置快捷键」共用）；已有的不重复加。打开快捷键页时它的热键框进入录入。</summary>
    void AddAppHotkey(string appId, string name)
    {
        FocusAppHotkey = appId;
        if (app.Store.Current.Launcher.AppHotkeys.Any(a => a.AppId == appId)) return;
        SaveLauncher(x => x with { AppHotkeys = [.. x.AppHotkeys, new AppHotkey(appId, name, SuggestAppHotkey(name, app.Store.Current))] });
    }

    /// <summary>给新加的应用先配一个没被占用的 Ctrl+Alt+字母：优先名字里第一个英文字母或数字，否则从 A 往后找。</summary>
    internal static Settings.Hotkey SuggestAppHotkey(string name, PaneSettings s)
    {
        var used = s.Launcher.AppHotkeys.Select(a => a.Hotkey).Append(s.Launcher.Hotkey).Append(s.Clipboard.Hotkey).ToHashSet();
        if (s.Words.Hotkey is { } w) used.Add(w);
        var first = name.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).Take(1);
        foreach (var c in first.Concat("ABCDEFGHIJKLMNOPQRSTUVWXYZ"))
        {
            var key = new Settings.Hotkey(KeyModifiers.Ctrl | KeyModifiers.Alt, c);
            if (!used.Contains(key)) return key;
        }
        return new(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, 'A');
    }

    void OnAppHotkeysChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "hotkeys") Later(0); });
}
