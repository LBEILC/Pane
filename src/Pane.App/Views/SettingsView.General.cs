// 设置窗口：通用（语言、字体、外观、玻璃参数、开机启动）（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 通用 =================

    void RenderGeneral()
    {
        var g = app.Store.Current.General;
        Group(Strings.Settings_Startup,
            AutostartRow(),
            Row(Strings.Settings_ShowTrayIcon, Strings.Settings_ShowTrayIcon_Description,
                Switch(g.ShowTrayIcon, v => SaveGeneral(x => x with { ShowTrayIcon = v }))));
        RenderLanguage(g);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (color, name) in new[] { ("#FF5874C9", Strings.Settings_Accent_MistBlue), ("#FF17958B", Strings.Settings_Accent_Teal), ("#FF5F8F4E", Strings.Settings_Accent_Moss), ("#FF4A505C", Strings.Settings_Accent_Graphite) })
            swatches.Children.Add(Swatch(color, name, string.Equals(g.AccentColor, color, StringComparison.OrdinalIgnoreCase)));
        Group(Strings.Settings_Appearance,
            Row(Strings.Settings_Theme, null, Seg([("System", Strings.Settings_FollowSystem), ("Light", Strings.Settings_Theme_Light), ("Dark", Strings.Settings_Theme_Dark)], g.Theme.ToString(),
                v => SaveGeneral(x => x with { Theme = Enum.Parse<ThemeMode>(v) }))),
            Row(Strings.Settings_AccentColor, Strings.Settings_AccentColor_Description, swatches),
            Row(Strings.Settings_PanelRadius, Strings.Settings_PanelRadius_Description, Seg([("8", Strings.Settings_PanelRadius_Small), ("20", Strings.Settings_PanelRadius_Large)], g.PanelRadius <= 8 ? "8" : "20",
                v => SaveGeneral(x => x with { PanelRadius = double.Parse(v) }))),
            Row(Strings.Settings_GlassOpacity, Strings.Settings_GlassOpacity_Description,
                GlassSlider(0, 1, 0.01, p => p.TintOpacity, (p, v) => p with { TintOpacity = v }, null)),
            Row(Strings.Settings_Motion, Strings.Settings_Motion_Description, Seg([("normal", Strings.Settings_Motion_Normal), ("reduced", Strings.Settings_Motion_Reduced), ("off", Strings.Settings_Motion_Off)],
                g.MotionSpeed <= 0.05 ? "off" : g.MotionSpeed < 1 ? "reduced" : "normal",
                // 「关闭」用 0.01 倍速：各处动画 1～4 毫秒播完，等于直接出现，又不用给每处动画单独加判断
                v => SaveGeneral(x => x with { MotionSpeed = v switch { "reduced" => 0.5, "off" => 0.01, _ => 1 } }))));
        RenderFonts(g);
        RenderGlassAdvanced();
    }

    // 界面语言（2026-11-02，048）：语言名用各自的写法；跟随系统时按系统语言换算后和这次启动用的比较，不同才提示重启
    void RenderLanguage(GeneralSettings g)
    {
        bool pending = App.ResolveUiLanguage(g.UiLanguage) != app.UiLanguage;
        var seg = Seg([("system", Strings.Settings_FollowSystem), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文")], g.UiLanguage ?? "system", // 不翻译：语言名用各自的写法，选错语言也认得回来
            v => { SaveGeneral(x => x with { UiLanguage = v == "system" ? null : v }); Later(); });
        Group(Strings.Settings_Language, pending
            ? Row(Strings.Settings_UiLanguage, Strings.Settings_RestartRequired, seg, Button(Strings.Common_RestartNow, "refresh", app.Restart))
            : Row(Strings.Settings_UiLanguage, Strings.Settings_UiLanguage_Description, seg));
    }

    // 界面字体（2026-10-30）：从本机字体里选，能写中文的排前面；每个字体用它自己显示一行预览。换了字体重启 Pane 后生效
    IReadOnlyList<FontFamilyInfo>? fonts;
    string fontQuery = "";
    bool fontsLoading, fontsOpen;
    void RenderFonts(GeneralSettings g)
    {
        var chosen = g.UiFont;
        var shown = chosen ?? string.Format(Strings.Settings_Font_Default, (object?)(app.DefaultFont));
        var fallback = app.UiLanguage == "zh-Hant" ? Strings.Settings_Font_JhengHei : Strings.Settings_Font_YaHei;
        bool pending = chosen != app.AppliedUiFont;
        void ToggleFonts()
        {
            if (fontsOpen) Collapse("fonts", () => { fontsOpen = false; Render(); });
            else { fontsOpen = true; revealKey = "fonts"; Render(); }
        }
        var toggle = Button(fontsOpen ? Strings.Common_Collapse : Strings.Settings_Font_Choose, fontsOpen ? "chev-d" : "chev-r", ToggleFonts);
        designToggles["fonts"] = ToggleFonts;
        var rows = new List<FrameworkElement>
        {
            pending
                ? Row(Strings.Settings_Font_Ui, Strings.Settings_RestartRequired, Chip(shown), Button(Strings.Common_RestartNow, "refresh", app.Restart), toggle)
                : Row(Strings.Settings_Font_Ui, string.Format(Strings.Settings_Font_FallbackDescription, (object?)(fallback)), Chip(shown), toggle),
        };
        if (fontsOpen)
        {
            var more = new List<FrameworkElement>();
            if (app.Fonts is null)
            {
                // 读不到本机字体列表时（024 之前的正式版）：手填字族名
                var box = Field(Strings.Settings_Font_FamilyPlaceholder, 260);
                box.Text = chosen ?? "";
                void Commit() { var v = box.Text.Trim(); SaveGeneral(x => x with { UiFont = v.Length == 0 ? null : v }); Render(); }
                box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
                more.Add(Row(Strings.Settings_Font_Family, Strings.Settings_Font_FamilyDescription, Wrap(box), Button(Strings.Common_Save, "check", Commit)));
            }
            else if (fonts is null)
            {
                if (!fontsLoading)
                {
                    fontsLoading = true;
                    _ = Load();
                    async Task Load()
                    {
                        try { fonts = await app.Fonts.GetFamiliesAsync(CancellationToken.None); }
                        catch (Exception) { fonts = []; }
                        fontsLoading = false;
                        Later(0);
                    }
                }
                more.Add(Row(Strings.Settings_Font_Loading, null));
            }
            else
            {
                var search = Field(Strings.Settings_Font_Search, 220);
                search.Text = fontQuery;
                var list = new StackPanel();
                void Fill()
                {
                    list.Children.Clear();
                    var q = search.Text.Trim();
                    list.Children.Add(FontRow(null, app.UiLanguage == "zh-Hant" ? string.Format(Strings.Settings_Font_Default, (object?)(app.DefaultFont)) : string.Format(Strings.Settings_Font_DefaultFallback, (object?)(app.DefaultFont), (object?)(fallback)), Strings.Settings_Font_Sample, chosen is null));
                    var hits = fonts.Where(f => q.Length == 0 || f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.HasChinese).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(40).ToList();
                    foreach (var f in hits)
                        list.Children.Add(FontRow(f.Name, f.DisplayName, f.HasChinese ? Strings.Settings_Font_SampleChinese : Strings.Settings_Font_SampleLatin, f.Name == chosen));
                    if (hits.Count == 0) list.Children.Add(Row(string.Format(Strings.Settings_Font_NotFound, (object?)(q)), null));
                }
                search.TextChanged += (_, _) => { fontQuery = search.Text; Fill(); };
                Fill();
                more.Add(Row(Strings.Settings_Font_Local, string.Format(Strings.Settings_Font_LocalDescription, (object?)(fonts.Count)), Wrap(search)));
                more.Add(list);
            }
            rows.Add(Reveal("fonts", more));
        }
        Group(Strings.Settings_Font, rows.ToArray(), Strings.Settings_Font_InstallDescription);

        FrameworkElement FontRow(string? name, string label, string sample, bool on)
        {
            var title = Text(label, LabelProto);
            var preview = Text(sample, SmallProto);
            if (name is not null) { title.FontFamily = new FontFamily(name); preview.FontFamily = new FontFamily(name); }
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(preview);
            var grid = new Grid { Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 12, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            if (on) { var check = new PaneIcon { Glyph = "check", Width = 16, Height = 16, Foreground = Accent(), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(check, 1); grid.Children.Add(check); }
            grid.Tapped += (_, _) => { SaveGeneral(x => x with { UiFont = name }); Render(); };
            return grid;
        }
    }

    // 「玻璃参数（高级）」：默认折叠；改的是当前主题那一套，拖动时所有玻璃窗口实时变化，松手才存盘
    void RenderGlassAdvanced()
    {
        bool opening = glassOpen && revealKey == "glass";
        var chevron = new PaneIcon { Glyph = "chev-d", Width = 16, Height = 16, Foreground = SmallProto.Foreground, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = glassOpen && !opening ? 180 : 0 } };
        if (opening) chevron.Loaded += (_, _) => SpinChevron(chevron, 180);
        var header = Row(Strings.Settings_Glass_Advanced, glassOpen ? string.Format(Strings.Settings_Glass_CurrentTheme, (object?)((app.Glass.Dark ? Strings.Settings_Glass_Dark : Strings.Settings_Glass_Light))) : Strings.Settings_Glass_Description, chevron);
        header.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        void Toggle()
        {
            if (glassOpen) { SpinChevron(chevron, 0); Collapse("glass", () => { glassOpen = false; Render(); }); }
            else { glassOpen = true; revealKey = "glass"; Render(); }
        }
        header.Tapped += (_, _) => Toggle();
        designToggles["glass"] = Toggle;
        var rows = new List<FrameworkElement> { header };
        if (glassOpen)
        {
            var d = app.Glass.Dark ? GlassParameters.Defaults().General.GlassDark : GlassParameters.Defaults().General.GlassLight;
            rows.Add(Reveal("glass", [
                GlassRow(Strings.Settings_Glass_Tint, Strings.Settings_Glass_TintDescription, 0, 1, 0.01, p => p.TintOpacity, (p, v) => p with { TintOpacity = v }, d.TintOpacity),
                GlassRow(Strings.Settings_Glass_Luminosity, Strings.Settings_Glass_LuminosityDescription, 0, 1, 0.01, p => p.LuminosityOpacity, (p, v) => p with { LuminosityOpacity = v }, d.LuminosityOpacity),
                GlassRow(Strings.Settings_Glass_Saturation, Strings.Settings_Glass_SaturationDescription, 1, 2, 0.05, p => p.Saturation, (p, v) => p with { Saturation = v }, d.Saturation),
                GlassRow(Strings.Settings_Glass_Highlight, Strings.Settings_Glass_HighlightDescription, 0, 1, 0.01, p => p.Highlight, (p, v) => p with { Highlight = v }, d.Highlight),
                GlassRow(Strings.Settings_Glass_Shadow, Strings.Settings_Glass_ShadowDescription, 0, 1, 0.01, p => p.Shadow, (p, v) => p with { Shadow = v }, d.Shadow),
                GlassRow(Strings.Settings_Glass_Blur, Strings.Settings_Glass_BlurDescription, 0, 40, 1, p => p.ExtraBlur, (p, v) => p with { ExtraBlur = v }, d.ExtraBlur),
                Row("", null, Button(Strings.Settings_Glass_ResetAll, "refresh", () => SaveGlass(_ => d))),
            ]));
        }
        Group(null, rows.ToArray());
    }

    FrameworkElement GlassRow(string label, string desc, double min, double max, double step,
        Func<GlassThemeParameters, double> get, Func<GlassThemeParameters, double, GlassThemeParameters> set, double fallback)
    {
        var value = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = MonoProto.FontFamily, FontSize = 12, Foreground = SmallProto.Foreground };
        var slider = GlassSlider(min, max, step, get, set, value);
        var reset = IconButton("refresh", Strings.Common_ResetDefaults, () => SaveGlass(p => set(p, fallback)));
        return Row(label, desc, slider, value, reset);
    }

    PaneSlider GlassSlider(double min, double max, double step, Func<GlassThemeParameters, double> get,
        Func<GlassThemeParameters, double, GlassThemeParameters> set, TextBlock? readout)
    {
        var slider = new PaneSlider(min, max, step, get(ActiveGlass(app.Store.Current.General)), FillProto.Background, FillProto.BorderBrush, Accent());
        void Readout(double v) { if (readout != null) readout.Text = step >= 1 ? v.ToString("0") : v.ToString("0.00"); }
        Readout(slider.Value);
        slider.ValueChanged += (_, v) =>
        {
            Readout(v);
            live = WithGlass(live ?? app.Store.Current.General, p => set(p, v));
            app.Glass.Apply(live);
        };
        slider.Committed += (_, _) => { if (live is { } g) { live = null; app.Store.Save(app.Store.Current with { General = g }); } };
        return slider;
    }

    GlassThemeParameters ActiveGlass(GeneralSettings g) => app.Glass.Dark ? g.GlassDark : g.GlassLight;
    GeneralSettings WithGlass(GeneralSettings g, Func<GlassThemeParameters, GlassThemeParameters> change) =>
        app.Glass.Dark ? g with { GlassDark = change(g.GlassDark) } : g with { GlassLight = change(g.GlassLight) };
    void SaveGlass(Func<GlassThemeParameters, GlassThemeParameters> change) { SaveGeneral(g => WithGlass(g, change)); Render(); }
    void SaveGeneral(Func<GeneralSettings, GeneralSettings> change) =>
        app.Store.Save(app.Store.Current with { General = change(app.Store.Current.General) });

    // 开机启动按注册表的实际情况显示（IAutostart.State），不是设置文件里记的意图
    string? autostartError;

    FrameworkElement AutostartRow()
    {
        var label = Strings.Settings_Autostart;
        var auto = app.Autostart;
        var state = auto.State;
        var toggle = Switch(state is AutostartState.On or AutostartState.DisabledInTaskManager or AutostartState.ElevationRequired, v =>
        {
            try { auto.Set(v); autostartError = null; }
            catch (Exception e) { autostartError = App.FailureReason(e) ?? Strings.Settings_Autostart_ChangeFailed; }
            Later(0);
        });
        if (state == AutostartState.Unavailable)
        {
            Enable(toggle, false);
            return Row(label, string.Format(Strings.Settings_Autostart_InstallHint, auto.UnavailableReason ?? Strings.Settings_Autostart_Unavailable), toggle);
        }
        if (autostartError != null) return Row(label, null, Status(false, string.Format(Strings.Settings_Autostart_Error, autostartError)), toggle);
        if (state == AutostartState.DisabledInTaskManager)
            return Row(label, Strings.Settings_Autostart_EnableInTaskManager, Status(false, Strings.Settings_Autostart_DisabledInTaskManager), toggle);
        // 099 / issue 48：exe 设了「以管理员身份运行」，开机时 Windows 直接跳过，不替用户改兼容性设置
        if (state == AutostartState.ElevationRequired)
            return Row(label, Strings.Settings_Autostart_ElevationHint, Status(false, Strings.Settings_Autostart_ElevationRequired), toggle);
        return Row(label, Strings.Settings_Autostart_Description, toggle);
    }
}
