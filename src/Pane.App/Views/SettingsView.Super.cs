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
        RenderSuperApps(p);
        RenderSuperActions(p);
    }

    // 常用应用（148，issue 88，原型 settings.js spAppsGroup）：没选中内容时常用面板列出的应用，最多 8 个，和启动器首页固定分开。
    // 列表（上移、移除）→「添加应用」就地搜索 →「从启动器首页添加」小片（首页固定了、这里还没有的）
    string? superAppQuery;   // null = 没在添加
    StackPanel? superAppResults;

    void RenderSuperApps(SuperPanelSettings p)
    {
        var catalog = app.AppIcons.Apps().GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var ids = (p.Apps ?? []).ToList();
        bool full = ids.Count >= SuperPanelSettings.MaxApps;
        var rows = new List<FrameworkElement>();
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            int pos = i;
            catalog.TryGetValue(id, out var info);
            var tools = new List<FrameworkElement>();
            if (pos > 0) tools.Add(IconButton("U", Strings.Common_MoveUp, () => { SaveSuper(x => { var list = (x.Apps ?? []).ToList(); if (pos < list.Count) (list[pos - 1], list[pos]) = (list[pos], list[pos - 1]); return x with { Apps = list.ToArray() }; }); Render(); }));
            tools.Add(IconButton("close", Strings.Common_Remove, () => { SaveSuper(x => x with { Apps = (x.Apps ?? []).Where(a => a != id).ToArray() }); Render(); }));
            rows.Add(ListRow(AppIcon(info?.Icon), Text(info?.Name ?? id, LabelProto), info == null ? Strings.SuperPanel_AppMissing : string.Format(Strings.Settings_Super_AppKey, (object?)(pos + 1)), false, tools.ToArray()));
        }
        if (full) rows.Add(AddRow(Text(Strings.Settings_Super_AppsFull, DescProto)));
        else rows.Add(superAppQuery == null
            ? AddRow(Button(Strings.Settings_Super_AddApp, "plus", () => { superAppQuery = ""; Render(); }))
            : SuperAppSearch(catalog.Values, ids));
        // 从启动器首页添加：首页固定里的应用（app:），这里还没有的
        var home = app.Store.Current.Launcher.PinnedApps.Where(id => id.StartsWith("app:", StringComparison.Ordinal) && !ids.Contains(id) && catalog.ContainsKey(id)).ToList();
        if (home.Count > 0)
        {
            var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
            foreach (var id in home)
            {
                var info = catalog[id];
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                var icon = AppIcon(info.Icon);
                icon.Width = icon.Height = 18;
                content.Children.Add(icon);
                content.Children.Add(Text(info.Name, SmallProto));
                var chip = new Border
                {
                    Height = PaneTokens.ControlSm, CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2), Padding = new Thickness(8, 0, 12, 0), BorderThickness = new Thickness(1),
                    Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, Opacity = full ? 0.4 : 1, Child = content,
                };
                if (!full) chip.Tapped += (_, _) => { SaveSuper(x => (x.Apps ?? []).Contains(id) || (x.Apps?.Length ?? 0) >= SuperPanelSettings.MaxApps ? x : x with { Apps = [.. x.Apps ?? [], id] }); Render(); };
                chips.Children.Add(chip);
            }
            rows.Add(Row(Strings.Settings_Super_FromHome, Strings.Settings_Super_FromHomeDescription, chips));
        }
        Group(Strings.Settings_Super_Apps, rows.ToArray(), Strings.Settings_Super_AppsDescription);
    }

    // 「添加应用」：同图标分区的就地搜索（只刷新结果，不重建整页，否则输入框会丢焦点）
    FrameworkElement SuperAppSearch(IEnumerable<AppIconInfo> all, List<string> taken)
    {
        var box = Field(Strings.Settings_Super_AppSearchPlaceholder, 260);
        box.Text = superAppQuery ?? "";
        superAppResults = new StackPanel { Spacing = 2 };
        var candidates = all.Where(a => !taken.Contains(a.Id)).ToList();
        void Fill()
        {
            superAppResults!.Children.Clear();
            var q = (superAppQuery ?? "").Trim();
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
                pick.Tapped += (_, _) =>
                {
                    SaveSuper(x => (x.Apps ?? []).Contains(a.Id) || (x.Apps?.Length ?? 0) >= SuperPanelSettings.MaxApps ? x : x with { Apps = [.. x.Apps ?? [], a.Id] });
                    superAppQuery = (app.Store.Current.SuperPanel.Apps?.Length ?? 0) >= SuperPanelSettings.MaxApps ? null : "";
                    Render();
                };
                superAppResults.Children.Add(pick);
            }
            if (superAppResults.Children.Count == 0) superAppResults.Children.Add(Text(Strings.Common_NotFound, DescProto));
        }
        box.TextChanged += (_, _) => { superAppQuery = box.Text; Fill(); };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { superAppQuery = null; Render(); e.Handled = true; } };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.StartBringIntoView(); };
        Fill();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Wrap(box));
        head.Children.Add(Button(Strings.Common_Cancel, null, () => { superAppQuery = null; Render(); }));
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(14, 10, 14, 10) };
        panel.Children.Add(head);
        panel.Children.Add(superAppResults);
        return panel;
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
    // 两个快捷键行：文件对话框分区和快捷键页共用。总开关（046）挪到「能力」页（055）
    FrameworkElement[] FolderJumpHotkeyRows()
    {
        var f = app.Store.Current.FolderJump;
        var jump = Hotkey(f.Jump, f.Menu, h => SaveFolderJump(x => x with { JumpHotkey = h == FolderJumpSettings.DefaultJump ? null : h }));
        var menu = Hotkey(f.Menu, f.Jump, h => SaveFolderJump(x => x with { MenuHotkey = h == FolderJumpSettings.DefaultMenu ? null : h }));
        var problem = app.FolderJump?.HotkeyProblem;
        return [problem != null ? Row(Strings.Settings_FolderJump_CurrentFolder, Strings.Settings_Hotkey_ConflictDescription, Status(false, problem), jump)
                                : Row(Strings.Settings_FolderJump_CurrentFolder, Strings.Settings_FolderJump_CurrentFolderDescription, jump),
                Row(Strings.Common_ChooseFolder, Strings.Settings_FolderJump_ChooseFolderDescription, menu)];
    }

    void RenderFolderJump()
    {
        var f = app.Store.Current.FolderJump;
        var auto = Switch(f.AutoJump, v => SaveFolderJump(x => x with { AutoJump = v }));
        var recent = Switch(f.ShowRecent, v => SaveFolderJump(x => x with { ShowRecent = v }));
        // 047（用户：开了三个资源管理器，Ctrl+G 只跳其中一个）：默认跳最近用过的那个（同 Listary），想自己挑的改成弹菜单
        var many = Seg([("recent", Strings.Settings_FolderJump_Recent), ("menu", Strings.Settings_FolderJump_Menu)], f.MenuWhenMany ? "menu" : "recent", v => SaveFolderJump(x => x with { MenuWhenMany = v == "menu" }));
        Group(Strings.Settings_FolderJump_Hotkeys, FolderJumpHotkeyRows());
        // 对话框搜索（2026-11-23，issue 81）：总开关关掉时下面两行变灰
        var bar = Switch(f.SearchBar, v => { SaveFolderJump(x => x with { SearchBar = v }); Later(); });
        var enter = Seg([("open", Strings.Settings_DialogSearch_EnterOpen), ("fill", Strings.Settings_DialogSearch_EnterFill)], f.OpenOnEnter ? "open" : "fill", v => SaveFolderJump(x => x with { OpenOnEnter = v == "open" }));
        var byType = Switch(f.FilterByType, v => SaveFolderJump(x => x with { FilterByType = v }));
        Enable(enter, f.SearchBar); Enable(byType, f.SearchBar);
        Group(Strings.Settings_DialogSearch,
            Row(Strings.Settings_DialogSearch_Show, Strings.Settings_DialogSearch_ShowDescription, bar),
            Row(Strings.Settings_DialogSearch_Enter, Strings.Settings_DialogSearch_EnterDescription, enter),
            Row(Strings.Settings_DialogSearch_Filter, Strings.Settings_DialogSearch_FilterDescription, byType));
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
        try { return await FolderPicker.PickFileAsync(SettingsWindow, [(Strings.Common_FilterPrograms, "*.exe")]); }   // 原生选择框（146）
        catch (Exception e) { app.ReportFailure(Strings.Common_ChooseFile, e); return null; }
    }
}
