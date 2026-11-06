// 设置窗口：鼠标手势（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 鼠标手势 =================

    static readonly string[] ArrowGlyphs = ["U", "9", "R", "3", "D", "1", "L", "7"]; // 顺序同 GestureDirection
    G.ImportPreview? importPreview;
    string? importStatus;
    bool importOk;
    string? gestureError;

    void RenderGesture()
    {
        G.GestureConfig config;
        try { config = app.GestureConfig; }
        catch (Exception)
        {
            Group(null, Row(Strings.Settings_Gestures_LoadFailed, Strings.Settings_Gestures_LoadFailedDescription, Status(false, Strings.Settings_Gestures_InvalidConfiguration)));
            RenderImport();
            return;
        }
        var st = config.Settings;

        var state = app.GestureState switch
        {
            G.GestureServiceState.WGesturesRunning => Strings.Settings_Gestures_Conflict,
            G.GestureServiceState.HookFailed => Strings.Settings_Gestures_HookFailed,
            G.GestureServiceState.Paused => Strings.Settings_Gestures_Paused,
            _ => null,
        };

        int buttons = System.Numerics.BitOperations.PopCount((uint)st.EnabledButtons);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (b, name) in new[] { (G.GestureButton.Right, Strings.Settings_Gestures_RightButton), (G.GestureButton.Middle, Strings.Settings_Gestures_MiddleButton), (G.GestureButton.X1, Strings.Settings_Gestures_BackButton), (G.GestureButton.X2, Strings.Settings_Gestures_ForwardButton) })
        {
            bool has = st.EnabledButtons.HasFlag(b);
            chips.Children.Add(ToggleChip(name, null, has, buttons == 1, v => SaveGestureSettings(x => x with { EnabledButtons = v ? x.EnabledButtons | b : x.EnabledButtons & ~b }, render: true)));
        }

        var trail = Switch(st.ShowTrail, v => SaveGestureSettings(x => x with { ShowTrail = v }, render: true, delay: true));
        var fade = Switch(st.FadeTrail, v => SaveGestureSettings(x => x with { FadeTrail = v }));
        var hint = Switch(st.ShowCommandName, v => SaveGestureSettings(x => x with { ShowCommandName = v }));
        var fullscreen = Switch(st.PauseInFullscreen, v => SaveGestureSettings(x => x with { PauseInFullscreen = v }));
        Enable(fade, st.ShowTrail);

        var rows = new List<FrameworkElement>();
        if (state != null) rows.Add(Row(Strings.Settings_Gestures_Status, null, Status(false, state)));
        rows.Add(Row(Strings.Settings_Gestures_Buttons, Strings.Settings_Gestures_ButtonsDescription, chips));
        rows.Add(Row(Strings.Settings_Gestures_ShowTrail, null, trail));
        rows.Add(Row(Strings.Settings_Gestures_TrailFade, Strings.Settings_Gestures_TrailFadeDescription, fade));
        rows.Add(Row(Strings.Settings_Gestures_ShowHint, Strings.Settings_Gestures_ShowHintDescription, hint));
        rows.Add(Row(Strings.Settings_Gestures_PauseFullscreen, Strings.Settings_Gestures_PauseFullscreenDescription, fullscreen));
        rows.Add(PauseHotkeyRow(st.PauseHotkey));
        if (gestureError != null) rows.Add(Row(Strings.Common_NotSaved, null, Status(false, gestureError)));
        Group(Strings.Settings_Gestures_Basic, rows.ToArray());

        // 编辑中的那一行换成编辑器；新加的编辑器放在最后
        var list = config.Global.Select((g, i) => draft is { App: < 0 } d && d.Index == i ? Reveal("gesture", [GestureEditor(d, config)])
            : ListRow(Arrows(g), Text(g.Name, LabelProto), GestureSub(g), false,
                IconButton("chev-r", Strings.Common_Edit, () => EditGesture(i)),
                IconButton("close", Strings.Settings_Gestures_Delete, () => { draft = null; SaveGestures(x => x with { Global = x.Global.Where((_, j) => j != i).ToArray() }); }))).ToList();
        if (draft is { App: < 0, Index: < 0 } fresh) list.Add(Reveal("gesture", [GestureEditor(fresh, config)]));
        else if (draft is not { App: < 0 }) list.Add(AddRow(Button(Strings.Settings_Gestures_Add, "plus", () => EditGesture(-1))));
        Group(string.Format(Strings.Settings_Gestures_Count, (object?)(config.Global.Count)), list.ToArray(),
            Strings.Settings_Gestures_PatternDescription);

        RenderGestureApps(config);
        RenderImport();
    }

    // 按程序设置（2026-10-30，用户提：每个软件单独配置）：每个程序选启用 / 禁用；启用的可以展开，
    // 就地增删改这个程序专用的手势（编辑器同全局）。和全局一样的手势允许，这个程序里用专用的，列表里注明覆盖了哪个
    int openApp = -1;
    void RenderGestureApps(G.GestureConfig config)
    {
        if (openApp >= config.Apps.Count || openApp >= 0 && config.Apps[openApp].Mode == G.AppRuleMode.Disabled) openApp = -1;
        var rows = new List<FrameworkElement>();
        for (int i = 0; i < config.Apps.Count; i++)
        {
            var a = config.Apps[i];
            int at = i;
            var name = Path.GetFileNameWithoutExtension(a.ExePath) + (a.WindowClass is { } wc ? $"（{wc}）" : "");
            var sub = a.Mode == G.AppRuleMode.Disabled ? Strings.Settings_Gestures_ProgramDisabled
                : a.Bindings.Count > 0 ? string.Format(Strings.Settings_Gestures_ProgramCount, (object?)(a.Bindings.Count)) : Strings.Settings_Gestures_ProgramInherit;
            var mode = Seg([("on", Strings.Common_Enable), ("off", Strings.Common_Disable)], a.Mode == G.AppRuleMode.Disabled ? "off" : "on",
                v => SaveGestures(x => x with { Apps = x.Apps.Select((r, j) => j == at ? r with { Mode = v == "off" ? G.AppRuleMode.Disabled : G.AppRuleMode.InheritAndOverride } : r).ToArray() }, render: true, delay: true));
            mode.MinWidth = 140;
            bool open = openApp == i;
            var tail = new List<FrameworkElement> { mode };
            if (a.Mode != G.AppRuleMode.Disabled)
            {
                bool opening = open && revealKey == "app";
                var chevron = new PaneIcon { Glyph = "chev-d", Width = 15, Height = 15, Foreground = IconProto.Foreground, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = open && !opening ? 180 : 0 } };
                if (opening) chevron.Loaded += (_, _) => SpinChevron(chevron, 180);
                var expand = new Button { Style = IconButtonProto.Style, Content = chevron };
                ToolTipService.SetToolTip(expand, open ? Strings.Common_Collapse : Strings.Settings_Gestures_ProgramBindings);
                void ToggleApp()
                {
                    if (open) { SpinChevron(chevron, 0); Collapse("app", () => { openApp = -1; if (draft is { App: >= 0 }) draft = null; Render(); }); }
                    else { openApp = at; if (draft is { App: >= 0 }) draft = null; revealKey = "app"; Render(); }
                }
                expand.Click += (_, _) => ToggleApp();
                if (!designToggles.ContainsKey("app") || open) designToggles["app"] = ToggleApp;   // 演示：第一个启用的程序，展开后换成它自己的收起
                tail.Add(expand);
            }
            tail.Add(IconButton("close", Strings.Common_Remove, () =>
            {
                openApp = -1;
                if (draft is { App: >= 0 }) draft = null;
                SaveGestures(x => x with { Apps = x.Apps.Where((_, j) => j != at).ToArray() });
            }));
            rows.Add(ListRow("app", name, sub, false, tail.ToArray()));
            if (open) rows.Add(Reveal("app", AppBindingRows(config, at)));
        }
        rows.Add(AddRow(Button(Strings.Settings_Gestures_AddProgram, "plus", async () =>
        {
            if (await PickProgram() is not { } exe) return;
            SaveGestures(x => x.Apps.Any(r => string.Equals(r.ExePath, exe, StringComparison.OrdinalIgnoreCase) && r.WindowClass == null) ? x
                : x with { Apps = [.. x.Apps, new G.AppRule(exe, null, G.AppRuleMode.Disabled, [])] });
        })));
        Group(Strings.Settings_Gestures_Programs, rows.ToArray(), Strings.Settings_Gestures_ProgramsDescription);
    }

    // 展开的程序：它的专用手势（缩进一格，和程序行区分），编辑中的那一行换成编辑器，末尾「添加专用手势」
    List<FrameworkElement> AppBindingRows(G.GestureConfig config, int appIndex)
    {
        var rule = config.Apps[appIndex];
        var rows = new List<FrameworkElement>();
        for (int k = 0; k < rule.Bindings.Count; k++)
        {
            var b = rule.Bindings[k];
            int at = k;
            if (draft is { } d && d.App == appIndex && d.Index == k) { rows.Add(Reveal("gesture", [GestureEditor(d, config)])); continue; }
            var covered = config.Global.FirstOrDefault(g => G.GestureMatcher.Same(g.Pattern, b.Pattern));
            var sub = covered != null ? string.Format(Strings.Settings_Gestures_OverrideDescription, GestureSub(b), covered.Name) : GestureSub(b);
            var row = ListRow(Arrows(b), Text(b.Name, LabelProto), sub, false,
                IconButton("chev-r", Strings.Common_Edit, () => EditGesture(at, appIndex)),
                IconButton("close", Strings.Settings_Gestures_DeleteProgramBinding, () =>
                {
                    draft = null;
                    SaveGestures(x => x with { Apps = x.Apps.Select((r, j) => j == appIndex ? r with { Bindings = r.Bindings.Where((_, n) => n != at).ToArray() } : r).ToArray() });
                }));
            row.Margin = new Thickness(28, 0, 0, 0);
            rows.Add(row);
        }
        if (draft is { Index: < 0 } fresh && fresh.App == appIndex) rows.Add(Reveal("gesture", [GestureEditor(fresh, config)]));
        else if (draft is not { App: >= 0 })
        {
            var add = AddRow(Button(Strings.Settings_Gestures_AddProgramBinding, "plus", () => EditGesture(-1, appIndex)));
            add.Margin = new Thickness(28, 0, 0, 0);
            rows.Add(add);
        }
        return rows;
    }

    // 暂停热键可以不设；设了之后能改、能清除
    FrameworkElement PauseHotkeyRow(G.KeyChord? chord)
    {
        var label = Strings.Settings_Gestures_PauseHotkey;
        if (chord is not { } k)
            return Row(label, Strings.Settings_Gestures_HotkeyUnset, Button(Strings.Common_Settings, "plus", () => SaveGestureSettings(x => x with { PauseHotkey = new G.KeyChord(G.KeyModifiers.Ctrl | G.KeyModifiers.Alt, 'G') }, render: true)));
        var box = Hotkey(new Settings.Hotkey((KeyModifiers)(int)k.Modifiers, k.VirtualKey), app.Store.Current.Launcher.Hotkey,
            h => SaveGestureSettings(x => x with { PauseHotkey = new G.KeyChord((G.KeyModifiers)(int)h.Modifiers, h.VirtualKey) }));
        return Row(label, null, box, IconButton("close", Strings.Common_ClearValue, () => SaveGestureSettings(x => x with { PauseHotkey = null }, render: true)));
    }

    // 方向箭头列：定宽，各行的名字对齐（最多 4 段，WGestures 导入的老手势可能更长，超出就挤）
    FrameworkElement Arrows(G.GestureBinding g)
    {
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Width = 92 };
        foreach (var d in g.Pattern.Directions)
            arrows.Children.Add(new PaneIcon { Glyph = ArrowGlyphs[(int)d], Width = 16, Height = 16, StrokeWidth = 2.2, Foreground = LabelProto.Foreground });
        if (g.Pattern.Directions.Count == 0) arrows.Children.Add(Text(Strings.Settings_Gestures_NoDirection, DescProto));
        return arrows;
    }

    static string GestureSub(G.GestureBinding g)
    {
        var parts = new List<string>();
        if (g.Pattern.Button != G.GestureButton.Right)
            parts.Add(g.Pattern.Button switch { G.GestureButton.Middle => Strings.Settings_Gestures_MiddleButton, G.GestureButton.X1 => Strings.Settings_Gestures_BackButton, G.GestureButton.X2 => Strings.Settings_Gestures_ForwardButton, _ => g.Pattern.Button.ToString() });
        if (g.Pattern.Modifier != G.GestureModifier.None)
            parts.Add(g.Pattern.Modifier switch
            {
                G.GestureModifier.WheelUp => Strings.Settings_Gestures_WheelUp, G.GestureModifier.WheelDown => Strings.Settings_Gestures_WheelDown,
                G.GestureModifier.LeftClick => Strings.Settings_Gestures_LeftClick, G.GestureModifier.MiddleClick => Strings.Settings_Gestures_MiddleClick,
                G.GestureModifier.X1Click => Strings.Settings_Gestures_BackClick, G.GestureModifier.X2Click => Strings.Settings_Gestures_ForwardClick, _ => "",
            });
        parts.Add(CommandText(g.Command));
        return string.Join(" · ", parts);
    }

    static string CommandText(G.GestureCommand c) => c switch
    {
        G.HotkeyCommand h => string.Format(Strings.Settings_Gestures_HotkeyDescription, string.Join(" ", h.Chords.Select(k => HotkeyBox.Text(new Settings.Hotkey((KeyModifiers)(int)k.Modifiers, k.VirtualKey))))),
        G.SendTextCommand => Strings.Settings_Gestures_SendText,
        G.WebSearchCommand => Strings.Settings_Gestures_WebSearch,
        G.RunCommand => Strings.Settings_Gestures_Run,
        G.WindowCommand w => string.Format(Strings.Settings_Gestures_WindowAction, w.Action switch
        {
            G.WindowAction.MaximizeRestore => Strings.Settings_Gestures_Maximize, G.WindowAction.Minimize => Strings.Settings_Gestures_Minimize, G.WindowAction.Close => Strings.Settings_Gestures_Close,
            G.WindowAction.ToggleTopmost => Strings.Settings_Gestures_Topmost, G.WindowAction.SnapLeft => Strings.Settings_Gestures_SnapLeft, _ => Strings.Settings_Gestures_SnapRight,
        }),
        G.VolumeCommand v => v.Action switch
        {
            G.VolumeAction.Up => Strings.Settings_Gestures_VolumeUp, G.VolumeAction.Down => Strings.Settings_Gestures_VolumeDown, G.VolumeAction.ToggleMute => Strings.Settings_Gestures_Mute, _ => Strings.Settings_Gestures_WheelVolume,
        },
        G.TaskSwitchCommand => Strings.Settings_Gestures_SwitchWindow,
        G.OpenCommand o => string.Format(Strings.Settings_Gestures_OpenTarget, Path.GetFileName(o.Target) is { Length: > 0 } f ? f : o.Target),
        G.PauseGesturesCommand => Strings.Settings_Gestures_Pause,
        _ => Strings.Settings_Gestures_NoOp,
    };

    IReadOnlyList<string> importSearched = [];
    // 从 WGestures 导入：先预览（只读它的配置文件），确认后整套替换
    void RenderImport()
    {
        FrameworkElement row;
        if (importPreview is { } p)
        {
            var desc = string.Format(p.Unsupported.Count > 0 ? Strings.Settings_Gestures_ImportSummaryUnsupported : Strings.Settings_Gestures_ImportSummary, p.SourceVersion, p.GlobalCount, p.AppRuleCount, p.DisabledAppCount, p.Unsupported.Count);
            row = Row(Strings.Settings_Gestures_ImportTitle, desc,
                Button(Strings.Common_Cancel, null, () => { importPreview = null; Render(); }),
                Button(Strings.Common_Import, "check", () =>
                {
                    try { app.ImportWGestures(p); importStatus = string.Format(Strings.Settings_Gestures_Imported, (object?)(p.GlobalCount)); importOk = true; }
                    catch (Exception e) { importStatus = string.Format(Strings.Settings_Gestures_ImportError, e.Message); importOk = false; }
                    importPreview = null;
                    Render();
                }));
        }
        else
        {
            void Load(string? path)
            {
                try
                {
                    importPreview = app.PreviewWGestures(path);
                    if (importPreview == null) { importStatus = Strings.Settings_Gestures_ImportNotFound; importOk = false; importSearched = app.WGesturesSearched; }
                    else { importStatus = null; importSearched = []; }
                }
                catch (Exception e) { importStatus = string.Format(Strings.Settings_Gestures_ImportReadError, e.Message); importOk = false; importSearched = []; }
                Render();
            }
            var find = Button(Strings.Settings_Gestures_FindConfig, "search", () => Load(null));
            // 找不到时手动选（043）：WGestures 数据目录里的 gestures.wg2 / gestures.wg，或在 WGestures 里「导出」的 .wgb
            var pick = Button(Strings.Settings_Gestures_ChooseConfig, "folder", async () =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    foreach (var ext in new[] { ".wg2", ".wg", ".wgb" }) picker.FileTypeFilter.Add(ext);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                    if (await picker.PickSingleFileAsync() is { } file) Load(file.Path);
                }
                catch (Exception e) { importStatus = string.Format(Strings.Settings_Gestures_PickConfigError, App.FailureReason(e) ?? e.Message); importOk = false; Render(); }
            });
            row = importStatus != null
                ? Row(Strings.Settings_Gestures_ImportTitle, null, Status(importOk, importStatus), find, pick)
                : Row(Strings.Settings_Gestures_ImportTitle, Strings.Settings_Gestures_ImportDescription, find, pick);
            if (importSearched.Count > 0)
                row = new StackPanel { Children = { row, Row(Strings.Settings_Gestures_SearchedLocations, string.Format(Strings.Settings_Gestures_ExportHelp, string.Join("\n", importSearched.Take(6)) + (importSearched.Count > 6 ? "\n…" : ""))) } };
        }
        // 恢复默认：点一下先问一句，再点「恢复」才替换
        var reset = resetConfirm
            ? Row(Strings.Settings_Gestures_RestoreTitle, Strings.Settings_Gestures_RestoreWarning,
                Button(Strings.Common_Cancel, null, () => { resetConfirm = false; Render(); }),
                Button(Strings.Common_Restore, "check", () =>
                {
                    resetConfirm = false;
                    try { app.ResetGestures(); gestureError = null; }
                    catch (Exception e) { gestureError = e.Message; }
                    Render();
                }))
            : Row(Strings.Settings_Gestures_RestoreTitle, Strings.Settings_Gestures_DefaultDescription, Button(Strings.Common_ResetDefaults, "refresh", () => { resetConfirm = true; Render(); }));
        Group(Strings.Settings_Gestures_Migration, row, reset);
    }
    bool resetConfirm;

    void SaveGestureSettings(Func<G.GestureSettings, G.GestureSettings> change, bool render = false, bool delay = false) =>
        SaveGestures(x => x with { Settings = change(x.Settings) }, render, delay);

    void SaveGestures(Func<G.GestureConfig, G.GestureConfig> change, bool render = true, bool delay = false)
    {
        try { app.SaveGestures(change(app.GestureConfig)); gestureError = null; }
        catch (Exception e) { gestureError = e.Message; render = true; }
        if (!render) return;
        if (delay) Later(); else Render();
    }
}
