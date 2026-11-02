using Pane.Localization;
// 设置窗口逻辑（设计负责人）。结构照原型 settings.js：SECTIONS → group() / row() 拼成分组列表。
// 已有设置模型的项（主题、主题色、圆角、动效、玻璃参数、托盘、热键、启动器位置）直接读写 SettingsStore，改了立即生效；
// 演示模式没有真实服务，状态用 DesignData 里的假数据。
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

public sealed partial class SettingsView : UserControl, IPanelView
{
    sealed record Section(string Id, string Name, string Glyph, string Desc, Action Render);

    readonly App app = (App)Application.Current;
    readonly List<Section> sections;
    readonly List<Grid> navItems = [];
    readonly SlidingHighlight navHighlight;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer rerender;
    Section current;
    string lookKey = "";
    bool glassOpen, navPlaced;
    GeneralSettings? live;   // 拖动玻璃滑杆时还没存盘的值

    public SettingsView()
    {
        InitializeComponent();
        sections =
        [
            new("general", Strings.Settings_General, "gear", Strings.Settings_General_Description, RenderGeneral),
            new("launcher", Strings.Settings_Launcher, "search", Strings.Settings_Launcher_Description, RenderLauncher),
            new("clipboard", Strings.Settings_Clipboard, "clipboard", Strings.Settings_Clipboard_Description, RenderClipboard),
            new("gesture", Strings.Settings_Gestures, "gesture", Strings.Settings_Gestures_Description, RenderGesture),
            new("superpanel", Strings.Settings_SuperPanel, "pointer", Strings.Settings_SuperPanel_Description, RenderSuper),
            new("dialog", Strings.Settings_FolderJump, "folder", Strings.Settings_FolderJump_Description, RenderFolderJump),
            new("ai", "AI", "ai", Strings.Settings_Ai_Description, RenderAi),
            new("icons", Strings.Settings_Icons, "app", Strings.Settings_Icons_Description, RenderIcons),
            new("about", Strings.Settings_About, "info", Strings.Settings_About_Description, RenderAbout),
        ];
        current = sections[0];
        // 设计预览：PANE_DESIGN_SETTINGS=分区 Id（可加 @像素 先滚下去，如 gesture@700），或 glass（通用页展开「玻璃参数（高级）」并滚到底）
        var design = Environment.GetEnvironmentVariable("PANE_DESIGN_SETTINGS");
        if (design?.Split('@') is [var id, var px] && double.TryParse(px, out var y))
        {
            design = id;
            Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, y, null, true));
        }
        // 设计预览（只在演示模式）：PANE_DESIGN_ICONS=list 预置几条按应用设置；search:关键字 同时打开「添加应用」搜索框
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_ICONS") is { Length: > 0 } iconPreview)
        {
            SaveIcons(x => x with { Overrides = new() { ["app:photoshop"] = IconMode.ForceMask, ["app:obs"] = IconMode.Auto, ["app:steam"] = IconMode.Raw }, CustomIcons = new() { ["app:obs"] = "obs.png" } });
            if (iconPreview.StartsWith("search:", StringComparison.Ordinal)) iconQuery = iconPreview[7..];
        }
        // 设计预览（只在演示模式）：PANE_DESIGN_GESTURE_EDIT=new 或手势序号，直接打开手势编辑器
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_GESTURE_EDIT") is { Length: > 0 } edit)
            draft = NewDraft(int.TryParse(edit, out var gi) ? gi : -1);
        // 设计预览（只在演示模式）：PANE_DESIGN_REVEAL=glass / fonts / app，打开约 1.2 秒后自动展开、再过 1.2 秒自动收起（录展开动效用）
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_REVEAL") is { Length: > 0 } reveal)
        {
            async void Play(object s, RoutedEventArgs e)
            {
                Loaded -= Play;
                for (int n = 0; n < 2; n++) { await Task.Delay(1200); if (designToggles.TryGetValue(reveal, out var t)) t(); }
            }
            Loaded += Play;
        }
        // 设计预览（只在演示模式）：PANE_DESIGN_WINV_CONFIRM=1 直接展开「用 Pane 替换 Win+V」的确认
        winvConfirm = Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_WINV_CONFIRM") == "1";
        // 设计预览（只在演示模式）：ai-chatgpt = AI 分区切到「ChatGPT 账号」
        if (design == "ai-chatgpt" && Environment.GetEnvironmentVariable("PANE_DEMO") == "1") { SaveAi(x => x with { Provider = AiProvider.ChatGpt }); design = "ai"; }
        if (design == "glass") { glassOpen = true; Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true)); }
        else if (sections.Find(s => s.Id == design) is { } preview) current = preview;
        navHighlight = new SlidingHighlight(NavLayer);
        foreach (var s in sections) Nav.Children.Add(NavItem(s));
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "pane-app.svg");
        if (File.Exists(logo)) Logo.Source = new SvgImageSource(new Uri(logo));

        rerender = DispatcherQueue.CreateTimer();
        rerender.IsRepeating = false;
        rerender.Tick += OnRerender;
        // 主题、主题色、圆角变了要整页重建（代码建的元素拿的是旧主题的画刷）；
        // 等分段 / 开关的动画播完再建（原型 rerenderAfterAnim）
        lookKey = LookKey();
        app.Glass.Changed += OnGlassChanged;
        app.HotkeyOccupied += OnHotkeyOccupied;
        if (app.FileSearchStatus is { } fs) fs.Changed += OnFileSearchChanged;
        app.AppIcons.Changed += OnIconsChanged;
        app.AiSetup.Changed += OnAiChanged;
        if (app.Updates is { } up) up.Changed += OnUpdateChanged;
        if (app.WinV is { } wv) wv.Changed += OnWinVChanged;
        Unloaded += OnUnloaded;
        Show(current);
    }

    void OnRerender(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => Render();

    // 设置窗口关闭时宿主销毁视图：退订应用级事件、停掉计时器、释放附着的合成视觉，让视图能被回收
    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        app.Glass.Changed -= OnGlassChanged;
        app.HotkeyOccupied -= OnHotkeyOccupied;
        if (app.FileSearchStatus is { } fs) fs.Changed -= OnFileSearchChanged;
        app.AppIcons.Changed -= OnIconsChanged;
        app.AiSetup.Changed -= OnAiChanged;
        if (app.Updates is { } up) up.Changed -= OnUpdateChanged;
        if (app.WinV is { } wv) wv.Changed -= OnWinVChanged;
        signIn?.Cancel();
        app.SetHotkeysSuspended(false);
        rerender.Stop();
        rerender.Tick -= OnRerender;
        navHighlight.Dispose();
        Body.Children.Clear();
        Nav.Children.Clear();
        Logo.Source = null;
    }

    string LookKey() => $"{app.Glass.Dark}|{app.Glass.Current.AccentColor}|{app.Glass.Current.PanelRadius}";
    void OnGlassChanged(object? sender, EventArgs e) { var key = LookKey(); if (key != lookKey) { lookKey = key; Later(); } }
    void OnHotkeyOccupied(object? sender, HotkeyConflict e) => Later();
    // 可能在后台线程触发
    void OnUpdateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "about") Later(0); });
    void OnFileSearchChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "launcher") Later(0); });
    void Later(double ms = 320) { rerender.Stop(); rerender.Interval = TimeSpan.FromMilliseconds(ms * app.Glass.Current.MotionSpeed); rerender.Start(); }

    // ================= 外壳 =================

    Grid NavItem(Section s)
    {
        var text = new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center, FontFamily = LabelProto.FontFamily, FontSize = LabelProto.FontSize };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new PaneIcon { Glyph = s.Glyph, Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(text);
        var item = new Grid { Height = 36, Padding = new Thickness(10, 0, 10, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Tag = s };
        item.Children.Add(row);
        item.Tapped += (_, _) => Show(s);
        item.SizeChanged += (_, _) => { if (!navPlaced && ReferenceEquals(item.Tag, current)) { navPlaced = true; navHighlight.MoveTo(item, false); } };
        navItems.Add(item);
        return item;
    }

    void Show(Section s)
    {
        bool changed = s != current;
        current = s;
        if (navPlaced) navHighlight.MoveTo(navItems[sections.IndexOf(s)]);
        Render(resetScroll: changed);
    }

    void Render() => Render(false);

    void Render(bool resetScroll)
    {
        rerender.Stop();
        double offset = Scroller.VerticalOffset;
        foreach (var item in navItems)
        {
            bool on = ReferenceEquals(item.Tag, current);
            var row = (StackPanel)item.Children[0];
            ((PaneIcon)row.Children[0]).Foreground = on ? LabelProto.Foreground : SmallProto.Foreground;
            var text = (TextBlock)row.Children[1];
            text.Foreground = on ? LabelProto.Foreground : SmallProto.Foreground;
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        SectionTitle.Text = current.Name;
        SectionDesc.Text = current.Desc;
        Body.Children.Clear();
        current.Render();
        if (resetScroll) Scroller.ChangeView(null, 0, null, true);
        else DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, offset, null, true));
    }

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

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
            Row(Strings.Settings_Motion, Strings.Settings_Motion_Description, Seg([("normal", Strings.Settings_Motion_Normal), ("reduced", Strings.Settings_Motion_Reduced)], g.MotionSpeed < 1 ? "reduced" : "normal",
                v => SaveGeneral(x => x with { MotionSpeed = v == "reduced" ? 0.5 : 1 }))));
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
        var toggle = Switch(state is AutostartState.On or AutostartState.DisabledInTaskManager, v =>
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
        return Row(label, Strings.Settings_Autostart_Description, toggle);
    }

    // ================= 启动器 =================

    void RenderLauncher()
    {
        var s = app.Store.Current;
        var conflict = app.HotkeyConflicts.FirstOrDefault(c => c.Target == "launcher");
        var hotkey = Hotkey(s.Launcher.Hotkey, s.Clipboard.Hotkey, h => app.Store.Save(app.Store.Current with { Launcher = app.Store.Current.Launcher with { Hotkey = h } }));
        string? hotkeyDesc = conflict != null ? null : DevHotkey("launcher");
        var hotkeyRow = conflict != null
            ? Row(Strings.Settings_Hotkey, Strings.Settings_Hotkey_ConflictDescription, Status(false, ConflictText(conflict)), hotkey)
            : Row(Strings.Settings_Hotkey, hotkeyDesc, hotkey);
        bool moved = s.Launcher.Position != null;
        var l = s.Launcher;
        var reset = Button(Strings.Settings_Launcher_ResetPosition, "refresh", () => { SaveLauncher(x => x with { Position = null }); Render(); });
        Enable(reset, moved);
        Group(Strings.Settings_Launcher_Activation,
            hotkeyRow,
            Row(Strings.Settings_Launcher_Position, moved ? Strings.Settings_Launcher_PositionSaved : Strings.Settings_Launcher_PositionDefault, reset),
            Row(Strings.Settings_Launcher_HideOnBlur, Strings.Settings_Launcher_HideOnBlurDescription, Switch(l.HideOnBlur, v => SaveLauncher(x => x with { HideOnBlur = v }))),
            Row(Strings.Settings_Launcher_KeepInput, Strings.Settings_Launcher_KeepInputDescription,
                Seg([("Clear", Strings.Common_Clear), ("OneMinute", Strings.Settings_Launcher_KeepOneMinute), ("Always", Strings.Settings_Launcher_KeepAlways)], l.KeepInput.ToString(),
                    v => SaveLauncher(x => x with { KeepInput = Enum.Parse<KeepInputMode>(v) }))),
            Row(Strings.Settings_Launcher_Home, l.Home == LauncherHome.Nothing ? Strings.Settings_Launcher_HomeNone : Strings.Settings_Launcher_HomeRecentDescription,
                Seg([("RecentAndPinned", Strings.Settings_Launcher_HomeRecent), ("PinnedOnly", Strings.Settings_Launcher_HomePinned), ("Nothing", Strings.Settings_Launcher_HomeHidden)], l.Home.ToString(),
                    v => { SaveLauncher(x => x with { Home = Enum.Parse<LauncherHome>(v) }); Later(); })),
            Row(Strings.Settings_Launcher_AutoAttach, Strings.Settings_Launcher_AutoAttachDescription,
                Seg([("0", Strings.Common_Off), ("1", Strings.Settings_Launcher_AttachOneSecond), ("3", Strings.Settings_Launcher_AttachThreeSeconds), ("5", Strings.Settings_Launcher_AttachFiveSeconds)], l.AutoAttachSeconds.ToString(),
                    v => SaveLauncher(x => x with { AutoAttachSeconds = int.Parse(v) }))));

        // 演示模式没有真实服务（FileSearchStatus 为 null），状态用假数据
        var fs = app.FileSearchStatus;
        // 状态对象要检测过一次才准（默认是「没有运行」）：每次打开设置窗口、第一次进这个分区时检测一次，结果通过 Changed 刷新
        if (fs != null && !everythingChecked) { everythingChecked = true; _ = fs.RefreshAsync(CancellationToken.None); }
        bool ready = fs == null ? SettingsSample.EverythingRunning : fs.State == FileSearchState.Ready;
        string status = fs == null ? (ready ? Strings.Settings_Everything_ConnectedLegacy : Strings.Settings_Everything_NotRunning)
            : fs.State switch
            {
                FileSearchState.Ready => string.Format(Strings.Settings_Everything_Connected, (object?)(fs.Version)),
                FileSearchState.Unsupported => Strings.Settings_Everything_Unavailable,
                _ => Strings.Settings_Everything_FilesUnavailable,
            };
        var check = Button(Strings.Settings_Everything_Refresh, "refresh", async () =>
        {
            if (fs == null) { SettingsSample.EverythingRunning = !SettingsSample.EverythingRunning; Render(); return; }
            try { await fs.RefreshAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            Render();
        });
        Group(Strings.Settings_Launcher_FileSearch,
            // 2026-10-29 起启动器固定列前 3 个文件，其余在文件窗口里看，「每次显示的文件结果」一行去掉
            Row("Everything", Strings.Settings_Launcher_FileSearchDescription, Status(ready, status), check));
        RenderActionRow(l);

        var favorites = l.Favorites.Select(f => ListRow(f.IsFolder ? "folder" : "doc", FavoriteTitle(f.Path), f.Path, false,
            IconButton("close", Strings.Common_Remove, () => { SaveLauncher(x => x with { Favorites = x.Favorites.Where(e => e != f).ToArray() }); Render(); }))).ToList();
        favorites.Add(AddRow(
            Button(Strings.Settings_Launcher_AddFile, "plus", async () => { if (await PickFavorite(false) is { } e) AddFavorite(e); }),
            Button(Strings.Settings_Launcher_AddFolder, "folder-plus", async () => { if (await PickFavorite(true) is { } e) AddFavorite(e); })));
        Group(Strings.Settings_Launcher_Favorites, favorites.ToArray(), Strings.Settings_Launcher_FavoritesDescription);

        var engines = l.SearchEngines.Select(e =>
        {
            bool def = e.Keyword == l.DefaultEngine;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(Text(e.Name, LabelProto));
            label.Children.Add(Chip(e.Keyword));
            if (def) label.Children.Add(Chip(Strings.Common_Default));
            var tail = new List<FrameworkElement>();
            if (!def) tail.Add(Button(Strings.Settings_Launcher_SetDefault, null, () => { SaveLauncher(x => x with { DefaultEngine = e.Keyword }); Render(); }));
            var del = IconButton("close", def ? Strings.Settings_Launcher_DefaultEngineCannotDelete : Strings.Common_Delete, () =>
            {
                if (e.Keyword == app.Store.Current.Launcher.DefaultEngine) return;
                SaveLauncher(x => x with { SearchEngines = x.SearchEngines.Where(v => v != e).ToArray() });
                Render();
            });
            Enable(del, !def);
            tail.Add(del);
            return ListRow("globe", label, e.UrlTemplate, true, tail.ToArray());
        }).ToList();
        engines.Add(addingEngine ? Reveal("engine", [EngineForm()]) : AddRow(Button(Strings.Settings_Launcher_AddEngine, "plus", () => { addingEngine = true; revealKey = "engine"; Render(); })));
        engines.Insert(0, Row(Strings.Settings_Launcher_WebSuggestions, Strings.Settings_Launcher_WebSuggestionsDescription,
            Switch(l.WebSuggestions, v => SaveLauncher(x => x with { WebSuggestions = v }))));
        Group(Strings.Settings_Launcher_WebSearch, engines.ToArray(), Strings.Settings_Launcher_WebSearchDescription);

        // 终端命令（037）：> 开头在终端里运行；记住最近 20 条用来补全
        var clearHistory = Button(Strings.Common_Clear, "trash", () => { app.TerminalHistory?.Clear(); terminalCleared = true; Render(); });
        Enable(clearHistory, !terminalCleared);
        Group(Strings.Settings_Launcher_Terminal, [Row(Strings.Settings_Launcher_RememberCommands, terminalCleared ? Strings.Settings_Launcher_CommandsCleared : Strings.Settings_Launcher_CommandsDescription,
                Switch(l.RememberCommands, v => SaveLauncher(x => x with { RememberCommands = v })), clearHistory)],
            Strings.Settings_Launcher_TerminalDescription);
    }
    bool terminalCleared;

    void SaveLauncher(Func<LauncherSettings, LauncherSettings> change) =>
        app.Store.Save(app.Store.Current with { Launcher = change(app.Store.Current.Launcher) });

    static string FavoriteTitle(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : path;

    void AddFavorite(FavoriteEntry entry)
    {
        SaveLauncher(x => x with { Favorites = x.Favorites.Any(f => string.Equals(f.Path, entry.Path, StringComparison.OrdinalIgnoreCase)) ? x.Favorites : [.. x.Favorites, entry] });
        Render();
    }

    // 系统的文件 / 文件夹选择框，以设置窗口为所有者
    async Task<FavoriteEntry?> PickFavorite(bool folder)
    {
        var owner = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (folder)
        {
            return await FolderPicker.PickFolderAsync(owner, null, null) is { } path ? new FavoriteEntry(path, true) : null;
        }
        var open = new Windows.Storage.Pickers.FileOpenPicker();
        open.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(open, owner);
        return await open.PickSingleFileAsync() is { } f ? new FavoriteEntry(f.Path, false) : null;
    }

    // 「添加搜索站点」：就地展开一行表单（名称、关键字、地址），保存前校验
    bool addingEngine;
    bool everythingChecked;

    // 设置页里的输入框：控件底色 + 细描边的圆角框（网页搜索表单、添加应用搜索共用）
    TextBox Field(string placeholder, double width) => new()
    {
        PlaceholderText = placeholder, Width = width, Height = PaneTokens.ControlMd, Padding = new Thickness(10, 6, 10, 0),
        FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize,
    };

    Border Wrap(TextBox box) => new()
    {
        Child = box, CornerRadius = new CornerRadius(((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft),
        Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
    };

    FrameworkElement EngineForm()
    {
        var name = Field(Strings.Common_Name, 96);
        var key = Field(Strings.Settings_Launcher_EngineKeyword, 72);
        var url = Field(Strings.Settings_Launcher_EngineAddress, 260);
        var error = Text("", DescProto);
        var save = Button(Strings.Common_Save, "check", () =>
        {
            var current = app.Store.Current.Launcher;
            string n = name.Text.Trim(), k = key.Text.Trim(), u = url.Text.Trim();
            string? problem = n.Length == 0 || k.Length == 0 || u.Length == 0 ? Strings.Settings_Launcher_EngineRequired
                : k.Contains(' ') ? Strings.Settings_Launcher_EngineKeywordSpace
                : current.SearchEngines.Any(e => string.Equals(e.Keyword, k, StringComparison.OrdinalIgnoreCase)) ? string.Format(Strings.Settings_Launcher_EngineKeywordDuplicate, (object?)(k))
                : !u.Contains("{query}") ? Strings.Settings_Launcher_EngineQueryRequired
                : !(u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ? Strings.Settings_Launcher_EngineSchemeRequired
                : null;
            if (problem != null) { error.Text = problem; return; }
            SaveLauncher(x => x with { SearchEngines = [.. x.SearchEngines, new SearchEngine(n, k, u)] });
            addingEngine = false;
            Render();
        });
        var cancel = Button(Strings.Common_Cancel, null, () => Collapse("engine", () => { addingEngine = false; Render(); }));
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fields.Children.Add(Wrap(name)); fields.Children.Add(Wrap(key)); fields.Children.Add(Wrap(url)); fields.Children.Add(save); fields.Children.Add(cancel);
        var form = new StackPanel { Spacing = 6, Padding = new Thickness(14, 10, 14, 10) };
        form.Children.Add(fields);
        form.Children.Add(error);
        form.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        return form;
    }

    // ================= 剪贴板 =================

    void RenderClipboard()
    {
        var s = app.Store.Current;
        var c = s.Clipboard;
        // 总开关（2026-10-31，033）：用别的剪贴板工具的人可以整个关掉。关着时只留这一行和「数据」（旧记录还能清）
        // Win+V 替换着时不让直接关：先在下面把 Win+V 还给系统（要不要重启资源管理器得用户选），免得 Win+V 两头都不管
        var winvOn = app.WinV is { State: not WinVState.Off };
        var master = Switch(c.Enabled, v => { SaveClipboard(x => x with { Enabled = v }); Later(); });
        if (c.Enabled && winvOn) Enable(master, false);
        Group(Strings.Settings_Clipboard_Basic, Row(Strings.Settings_Clipboard_Enable, c.Enabled
                ? (winvOn ? Strings.Settings_Clipboard_DisableWinVFirst : Strings.Settings_Clipboard_EnableDescription)
                : Strings.Settings_Clipboard_Disabled,
            master));
        if (!c.Enabled) { RenderClipboardData(); return; }
        var conflict = app.HotkeyConflicts.FirstOrDefault(x => x.Target == "clipboard");
        var hotkey = Hotkey(c.Hotkey, s.Launcher.Hotkey, h => SaveClipboard(x => x with { Hotkey = h }));
        var winv = app.WinV;
        var call = new List<FrameworkElement>
        {
            conflict != null ? Row(Strings.Settings_Hotkey, Strings.Settings_Hotkey_ConflictDescription, Status(false, ConflictText(conflict)), hotkey)
                             : Row(Strings.Settings_Hotkey, DevHotkey("clipboard") ?? (winv?.State == WinVState.On ? Strings.Settings_Clipboard_WinVActive : null), hotkey),
        };
        if (winv != null) call.AddRange(WinVRows(winv));
        Group(Strings.Settings_Clipboard_Activation, call.ToArray());

        // 三种类型至少留一种（设置模型要求）：只剩一种时点它不响应，说明里写清楚
        int kinds = (c.RecordText ? 1 : 0) + (c.RecordImages ? 1 : 0) + (c.RecordFiles ? 1 : 0);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(ToggleChip(Strings.Common_Text, "text", c.RecordText, kinds == 1, v => SaveClipboard(x => x with { RecordText = v })));
        chips.Children.Add(ToggleChip(Strings.Common_Image, "image", c.RecordImages, kinds == 1, v => SaveClipboard(x => x with { RecordImages = v })));
        chips.Children.Add(ToggleChip(Strings.Common_File, "folder", c.RecordFiles, kinds == 1, v => SaveClipboard(x => x with { RecordFiles = v })));
        Group(Strings.Settings_Clipboard_Recording,
            Row(Strings.Settings_Clipboard_Formats, kinds == 1 ? Strings.Settings_Clipboard_OneFormatRequired : Strings.Settings_Clipboard_FormatsDescription, chips),
            Row(Strings.Settings_Clipboard_MaxItems, Strings.Settings_Clipboard_MaxItemsDescription,
                Seg([("200", Strings.Settings_Clipboard_LimitTwoHundred), ("500", Strings.Settings_Clipboard_LimitFiveHundred), ("1000", Strings.Settings_Clipboard_LimitThousand), ("5000", Strings.Settings_Clipboard_LimitFiveThousand)], c.Limit.ToString(),
                    v => SaveClipboard(x => x with { Limit = int.Parse(v) }))),
            Row(Strings.Settings_Clipboard_Deduplicate, Strings.Settings_Clipboard_DeduplicateDescription, Switch(c.Dedupe, v => SaveClipboard(x => x with { Dedupe = v }))),
            Row(Strings.Settings_Clipboard_MovePastedFirst, Strings.Settings_Clipboard_MovePastedFirstDescription, Switch(c.MoveToTopAfterPaste, v => SaveClipboard(x => x with { MoveToTopAfterPaste = v }))));

        var excluded = c.ExcludedPrograms.Select(name => ListRow("shield", name, Strings.Settings_Clipboard_ExcludedProgram, false,
            IconButton("close", Strings.Common_Remove, () => { SaveClipboard(x => x with { ExcludedPrograms = x.ExcludedPrograms.Where(v => !string.Equals(v, name, StringComparison.OrdinalIgnoreCase)).ToArray() }); Render(); }))).ToList();
        excluded.Insert(0, Row(Strings.Settings_Clipboard_RespectHistoryFlag, Strings.Settings_Clipboard_RespectHistoryFlagDescription, Switch(c.HonorExcludeMarker, v => SaveClipboard(x => x with { HonorExcludeMarker = v }))));
        excluded.Add(AddRow(Button(Strings.Settings_Clipboard_AddExcludedProgram, "plus", async () =>
        {
            // 服务按程序文件名匹配（不区分大小写），只存文件名
            if (await PickProgram() is not { } exe) return;
            var name = Path.GetFileName(exe);
            SaveClipboard(x => x with { ExcludedPrograms = x.ExcludedPrograms.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) ? x.ExcludedPrograms : [.. x.ExcludedPrograms, name] });
            Render();
        })));
        Group(Strings.Settings_Clipboard_Privacy, excluded.ToArray());
        RenderClipboardData();
    }

    void RenderClipboardData()
    {
        // 清空要点两次：第一次按钮变成「再点一次清空」，3 秒内不点就复原（同启动器里关机 / 重启的确认）
        var clear = Button(clearArmed ? Strings.Settings_Clipboard_ConfirmClear : Strings.Common_Clear, "trash", () =>
        {
            if (!clearArmed) { clearArmed = true; Render(); _ = DisarmClear(); return; }
            clearArmed = false;
            app.ClipboardHistory.ClearUnpinned();
            clearedAt = DateTime.Now;
            Render();
        });
        var cleared = (DateTime.Now - clearedAt).TotalSeconds < 4;
        var rows = new List<FrameworkElement>
        {
            cleared ? Row(Strings.Settings_Clipboard_ClearHistory, null, Status(true, Strings.Settings_Clipboard_Cleared), clear)
                    : Row(Strings.Settings_Clipboard_ClearHistory, clearArmed ? Strings.Settings_Clipboard_ClearWarning : Strings.Settings_Clipboard_PinnedRemain, clear),
        };
        // 历史存不进硬盘时（045，用户 2026-11-01 遇到：重启后历史退回早上）在这里说清楚，不要等重启丢了才发现
        if (app.ClipboardHistory.StorageProblem is { } problem)
            rows.Insert(0, Row(Strings.Settings_Clipboard_StorageFailed, Strings.Settings_Clipboard_StorageFailedDescription, Status(false, problem)));
        Group(Strings.Settings_Clipboard_Data, rows.ToArray());
    }

    // 用 Pane 替换 Win+V（2026-10-30，原型 settings.js winvDialog）：打开 / 关闭都先就地说明要改什么、要不要现在重启资源管理器，
    // 再点按钮才动手。下面跟一行「同时关闭 Windows 自带的剪贴板历史」，只在已替换时可用
    bool winvConfirm, winvBusy;
    IEnumerable<FrameworkElement> WinVRows(IWinVReplacement w)
    {
        var state = w.State;
        bool replaced = state is WinVState.On or WinVState.Pending;
        void Run(Func<CancellationToken, Task> work)
        {
            winvConfirm = false; winvBusy = true; Render();
            _ = Go();
            async Task Go()
            {
                try { await work(CancellationToken.None); } catch (Exception) { }   // 失败原因由服务放进 Error
                winvBusy = false; Render();
            }
        }
        var label = Strings.Settings_WinV_Replace;
        if (winvBusy)
        {
            yield return Row(label, null, Status(true, Strings.Settings_WinV_Working));
            yield break;
        }
        if (winvConfirm)
        {
            // 说明一行占满宽度，问题和按钮另起一行（挤在一行时说明被压成很窄的一列）
            yield return Row(replaced ? Strings.Settings_WinV_Restore : label, replaced
                ? Strings.Settings_WinV_RestoreDescription
                : Strings.Settings_WinV_RegistryDescription);
            yield return Row(Strings.Settings_WinV_RestartPrompt, Strings.Settings_WinV_RestartDescription,
                Button(Strings.Common_Cancel, null, () => { winvConfirm = false; Render(); }),
                Button(Strings.Settings_WinV_NextLogin, null, () => Run(t => replaced ? w.DisableAsync(false, t) : w.EnableAsync(false, t))),
                Button(Strings.Settings_WinV_RestartNow, "refresh", () => Run(t => replaced ? w.DisableAsync(true, t) : w.EnableAsync(true, t))));
            yield break;
        }
        // 关闭 Pending（资源管理器还没放开 Win+V）不用重启，直接恢复；其余先就地确认
        var toggle = Switch(replaced, _ => { if (state == WinVState.Pending) Run(t => w.DisableAsync(false, t)); else { winvConfirm = true; Render(); } });
        FrameworkElement? status = w.Error is { } err ? Status(false, err) : state switch
        {
            WinVState.On => Status(true, Strings.Settings_WinV_Active),
            WinVState.Pending => Status(false, Strings.Settings_WinV_PendingEnable),
            WinVState.RestorePending => Status(false, Strings.Settings_WinV_PendingRestore),
            _ => null,
        };
        var now = state is WinVState.Pending or WinVState.RestorePending
            ? Button(Strings.Settings_WinV_RestartExplorer, "refresh", () => Run(t => state == WinVState.Pending ? w.EnableAsync(true, t) : w.DisableAsync(true, t)))
            : null;
        yield return Row(label, Strings.Settings_WinV_ReplaceDescription,
            new[] { status, now, toggle }.OfType<FrameworkElement>());
        var history = Switch(w.SystemHistoryDisabled, v => Run(t => w.SetSystemHistoryDisabledAsync(v, t)));
        Enable(history, replaced);
        yield return Row(Strings.Settings_WinV_DisableSystemHistory, replaced ? Strings.Settings_WinV_DisableSystemHistoryDescription : Strings.Settings_WinV_EnableFirst, history);
    }

    void OnWinVChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "clipboard" && !winvBusy) Later(0); });

    bool clearArmed;
    DateTime clearedAt;

    async Task DisarmClear()
    {
        await Task.Delay(3000);
        if (!clearArmed) return;
        clearArmed = false;
        if (current.Id == "clipboard") Render();
    }

    // 可多选的小标签（原型 .chipc）：选中是选中底色 + 主题色描边。onlyOne：至少要留一个时，最后一个点了不响应
    Border ToggleChip(string text, string? glyph, bool on, bool onlyOne, Action<bool> changed)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null) content.Children.Add(new PaneIcon { Glyph = glyph, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = (on ? SmallStrongProto : SmallProto).Foreground });
        content.Children.Add(Text(text, on ? SmallStrongProto : SmallProto));
        var chip = new Border
        {
            Height = PaneTokens.ControlSm, CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2), Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(1),
            Background = on ? SelectedProto.Background : FillProto.Background, BorderBrush = on ? Accent() : FillProto.BorderBrush,
            Opacity = on ? 1 : 0.7, Child = content,
        };
        chip.Tapped += (_, _) =>
        {
            if (on && onlyOne) return;
            changed(!on);
            Render();
        };
        return chip;
    }

    void SaveClipboard(Func<ClipboardSettings, ClipboardSettings> change) =>
        app.Store.Save(app.Store.Current with { Clipboard = change(app.Store.Current.Clipboard) });

    // ================= 鼠标手势 =================

    static readonly string[] ArrowGlyphs = ["U", "9", "R", "3", "D", "1", "L", "7"]; // 顺序同 GestureDirection
    G.GestureButton gestureButtonsBeforeOff = G.GestureButton.Right;
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
        bool on = st.EnabledButtons != G.GestureButton.None;

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
        if (!on) { chips.Opacity = 0.4; chips.IsHitTestVisible = false; }

        var trail = Switch(st.ShowTrail, v => SaveGestureSettings(x => x with { ShowTrail = v }, render: true, delay: true));
        var fade = Switch(st.FadeTrail, v => SaveGestureSettings(x => x with { FadeTrail = v }));
        var hint = Switch(st.ShowCommandName, v => SaveGestureSettings(x => x with { ShowCommandName = v }));
        var fullscreen = Switch(st.PauseInFullscreen, v => SaveGestureSettings(x => x with { PauseInFullscreen = v }));
        foreach (var c in new Control[] { trail, hint, fullscreen }) Enable(c, on);
        Enable(fade, on && st.ShowTrail);

        var rows = new List<FrameworkElement>();
        if (state != null) rows.Add(Row(Strings.Settings_Gestures_Status, null, Status(false, state)));
        rows.Add(Row(Strings.Settings_Gestures_Enable, null, Switch(on, v =>
        {
            if (!v) gestureButtonsBeforeOff = app.GestureConfig.Settings.EnabledButtons;
            SaveGestureSettings(x => x with { EnabledButtons = v ? (gestureButtonsBeforeOff == G.GestureButton.None ? G.GestureButton.Right : gestureButtonsBeforeOff) : G.GestureButton.None }, render: true, delay: true);
        })));
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

    // ================= 应用图标 =================

    string? iconQuery;                        // null = 没在添加；否则是「添加应用」搜索框里的文字
    StackPanel? iconResults;

    void RenderIcons()
    {
        var s = app.Store.Current.Icons;
        var catalog = app.AppIcons;
        var rows = new List<FrameworkElement>
        {
            Row(Strings.Settings_Icons_DefaultMask, Strings.Settings_Icons_DefaultMaskDescription,
                Switch(s.MaskByDefault, v => { SaveIcons(x => x with { MaskByDefault = v }); Later(); })),
        };
        if (catalog.Rebuilding) rows.Add(Row(Strings.Settings_Icons_Rebuilding, Strings.Settings_Icons_RebuildingDescription, Status(true, Strings.Settings_Icons_Generating)));
        Group(Strings.Settings_Icons_Mask, rows.ToArray());

        // 文件图标（036，评论区建议）：默认仍是 Pane 的彩色类型图标；选「系统」和资源管理器一样。只影响文件窗口和启动器里的文件行
        Group(Strings.Settings_Icons_FileIcons, Row(Strings.Settings_Icons_Files, s.SystemFileIcons ? Strings.Settings_Icons_SystemDescription : Strings.Settings_Icons_PaneDescription,
            Seg([("pane", Strings.Settings_Icons_Pane), ("system", Strings.Settings_Icons_System)], s.SystemFileIcons ? "system" : "pane", v => { SaveIcons(x => x with { SystemFileIcons = v == "system" }); Later(); })));

        var apps = catalog.Apps().ToDictionary(a => a.Id);
        var ids = s.Overrides.Keys.Union(s.CustomIcons.Keys)
            .OrderBy(id => apps.TryGetValue(id, out var a) ? a.Name : id, StringComparer.CurrentCultureIgnoreCase).ToList();
        var list = new List<FrameworkElement>();
        foreach (var id in ids)
        {
            apps.TryGetValue(id, out var info);
            var mode = s.Overrides.GetValueOrDefault(id);
            bool custom = s.CustomIcons.ContainsKey(id);
            var sub = info == null ? Strings.Settings_Icons_AppMissing : custom ? string.Format(Strings.Settings_Icons_CustomDescription, KindText(s.MaskByDefault ? mode : IconMode.Raw, info.Kind)) : KindText(s.MaskByDefault ? mode : IconMode.Raw, info.Kind);
            var seg = Seg([("Auto", Strings.Common_Auto), ("ForceMask", Strings.Settings_Icons_ForceMask), ("Raw", Strings.Settings_Icons_Raw)], mode.ToString(),
                v => { SaveIcons(x => x with { Overrides = new(x.Overrides) { [id] = Enum.Parse<IconMode>(v) } }); Later(); });
            seg.MinWidth = 220;
            Enable(seg, s.MaskByDefault);
            list.Add(ListRow(AppIcon(info?.Icon), Text(info?.Name ?? id, LabelProto), sub, false, seg,
                IconButton("image", Strings.Settings_Icons_ChangeImage, async () => { if (await PickIcon(id)) Render(); }),
                IconButton("close", Strings.Common_ResetDefaults, () => { ResetIcon(id); Render(); })));
        }
        list.Add(iconQuery == null
            ? AddRow(Button(Strings.Settings_Icons_AddApp, "plus", () => { iconQuery = ""; Render(); }))
            : IconSearch(apps.Values, ids));
        Group(Strings.Settings_Icons_PerApp, list.ToArray(), Strings.Settings_Icons_PerAppDescription);
    }

    static string KindText(IconMode mode, AppIconKind kind)
    {
        var text = mode == IconMode.Raw ? Strings.Settings_Icons_Original : kind switch
    {
        AppIconKind.Full => Strings.Settings_Icons_FullBleed,
        AppIconKind.Plate => Strings.Settings_Icons_ColoredPlate,
        AppIconKind.Neutral => Strings.Settings_Icons_NeutralPlate,
        AppIconKind.Raw => Strings.Settings_Icons_Original,
        _ => Strings.Settings_Icons_NotGenerated,
    };
        return mode == IconMode.ForceMask ? string.Format(Strings.Settings_Icons_ForcedDescription, text) : text;
    }

    // 行首：应用现在的图标（和启动器里一样大小比例）；没有图就用占位图块
    FrameworkElement AppIcon(string? path)
    {
        if (path != null && File.Exists(path))
            return new Image { Width = 30, Height = 30, Stretch = Stretch.Uniform, Source = new BitmapImage { DecodePixelWidth = 30, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(path) } };
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new PaneIcon { Glyph = "app", Width = 16, Height = 16, Foreground = IconProto.Foreground },
        };
    }

    // 「添加应用」：就地搜索框 + 最多 6 个结果；只刷新结果，不重建整页（否则输入框会丢焦点）
    FrameworkElement IconSearch(IEnumerable<AppIconInfo> all, List<string> taken)
    {
        var box = Field(Strings.Settings_Icons_SearchPlaceholder, 260);
        box.Text = iconQuery ?? "";
        iconResults = new StackPanel { Spacing = 2 };
        var candidates = all.Where(a => !taken.Contains(a.Id)).ToList();
        void Fill()
        {
            iconResults!.Children.Clear();
            var q = (iconQuery ?? "").Trim();
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
                    iconQuery = null;
                    SaveIcons(x => x with { Overrides = new(x.Overrides) { [a.Id] = IconMode.Auto } });
                    Render();
                };
                iconResults.Children.Add(pick);
            }
            if (iconResults.Children.Count == 0) iconResults.Children.Add(Text(Strings.Common_NotFound, DescProto));
        }
        box.TextChanged += (_, _) => { iconQuery = box.Text; Fill(); };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { iconQuery = null; Render(); e.Handled = true; } };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        Fill();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Wrap(box));
        head.Children.Add(Button(Strings.Common_Cancel, null, () => { iconQuery = null; Render(); }));
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(14, 10, 14, 10) };
        panel.Children.Add(head);
        panel.Children.Add(iconResults);
        return panel;
    }

    // 换图标：复制一份到数据目录（原图移走、删掉也不影响），文件名带时间，内容变了缓存键跟着变
    async Task<bool> PickIcon(string id)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        if ((await picker.PickSingleFileAsync())?.Path is not { } source) return false;
        var dir = Path.Combine(app.Store.DirectoryPath, "custom-icons");
        Directory.CreateDirectory(dir);
        var stem = IconFileStem(id);
        var target = Path.Combine(dir, $"{stem}-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(source).ToLowerInvariant()}");
        File.Copy(source, target, true);
        DeleteCustomIcons(stem, keep: target);
        SaveIcons(x => x with { CustomIcons = new(x.CustomIcons) { [id] = target } });
        return true;
    }

    void ResetIcon(string id)
    {
        SaveIcons(x =>
        {
            var overrides = new Dictionary<string, IconMode>(x.Overrides);
            var customs = new Dictionary<string, string>(x.CustomIcons);
            overrides.Remove(id);
            customs.Remove(id);
            return x with { Overrides = overrides, CustomIcons = customs };
        });
        DeleteCustomIcons(IconFileStem(id), keep: null);
    }

    static string IconFileStem(string id) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];

    // 只删 Pane 自己复制进数据目录的那几份
    void DeleteCustomIcons(string stem, string? keep)
    {
        var dir = Path.Combine(app.Store.DirectoryPath, "custom-icons");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, stem + "-*"))
            if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void SaveIcons(Func<IconSettings, IconSettings> change) =>
        app.Store.Save(app.Store.Current with { Icons = change(app.Store.Current.Icons) });

    void OnIconsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "icons" && iconQuery == null) Later(0); });

    // ================= AI =================

    // 联网操作（拉模型、测试、登录）的状态：只在本次打开设置窗口期间保留
    IReadOnlyList<AiModel>? aiModels;
    string? aiBusy;                           // "models" / "test" / "signin" / "proxy"
    string? aiLast;                           // aiResult 是哪一项的结果（测试代理的结果只显示在代理那一行）
    bool proxyCustomPending;                  // 选了「自定义」还没填地址
    (bool Ok, string Text)? aiResult;
    string? aiFieldError;
    CancellationTokenSource? signIn;

    void RenderAi()
    {
        var a = app.Store.Current.Ai;
        // AI 总开关（issue 8，照原型 settings.js）：关掉后只留这一行
        Group(Strings.Settings_Ai_Basic, Row(Strings.Settings_Ai_Enable, a.Enabled ? Strings.Settings_Ai_EnableDescription
                : Strings.Settings_Ai_Disabled,
            Switch(a.Enabled, v => { SaveAi(x => x with { Disabled = !v }); Later(); })));
        if (!a.Enabled) return;
        var setup = app.AiSetup;
        bool api = a.Provider == AiProvider.Api;
        var rows = new List<FrameworkElement>
        {
            Row(Strings.Settings_Ai_Provider, null, Seg([("Api", Strings.Settings_Ai_ApiProvider), ("ChatGpt", Strings.Settings_Ai_ChatGptProvider)], a.Provider.ToString(), v =>
            {
                SaveAi(x => x with { Provider = Enum.Parse<AiProvider>(v) });
                aiModels = null; aiResult = null;
                Later(0);
            })),
        };
        if (api)
        {
            rows.Add(Row(Strings.Settings_Ai_Endpoint, Strings.Settings_Ai_EndpointDescription, CommitField("https://…/v1", 300, a.ApiBaseUrl, v =>
                Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? Try(() => SaveAi(x => x with { ApiBaseUrl = v })) : Strings.Settings_Ai_EndpointInvalid)));
            rows.Add(KeyRow(setup));
        }
        else rows.AddRange(AccountRows(setup));
        if (api || setup.Account != null)
        {
            rows.Add(ModelRow(a, api));
            var test = Button(aiBusy == "test" ? Strings.Settings_Ai_Testing : Strings.Common_Test, null, () => _ = RunAi("test", async t => { await setup.TestAsync(t); return Strings.Settings_Ai_Connected; }));
            Enable(test, aiBusy == null);
            rows.Add(aiResult is { } r && aiLast != "proxy" ? Row(Strings.Settings_Ai_TestConnection, null, Status(r.Ok, r.Text), test) : Row(Strings.Settings_Ai_TestConnection, Strings.Settings_Ai_TestConnectionDescription, test));
        }
        if (aiFieldError != null) rows.Add(Row(Strings.Common_NotSaved, null, Status(false, aiFieldError)));
        // 思考程度（042，用户提）：ChatGPT 的推理模型等支持；不支持的模型服务自动去掉这个参数
        rows.Add(Row(Strings.Settings_Ai_Reasoning, a.Reasoning == AiReasoningEffort.Default ? Strings.Settings_Ai_DefaultReasoningDescription : Strings.Settings_Ai_ReasoningDescription,
            Seg([("Default", Strings.Common_Default), ("Low", Strings.Settings_Ai_ReasoningLow), ("Medium", Strings.Settings_Ai_ReasoningMedium), ("High", Strings.Settings_Ai_ReasoningHigh)], a.Reasoning.ToString(),
                v => { SaveAi(x => x with { Reasoning = Enum.Parse<AiReasoningEffort>(v) }); Later(); })));
        Group(Strings.Settings_Ai_Service, rows.ToArray());

        RenderAiActions(a);

        // 超级面板自动翻译：没配 AI 时开关灰掉（2026-10-30）
        bool configured = app.Ai.Status != AiStatus.NotConfigured;
        var autoSwitch = Switch(a.AutoTranslateSelection, v => SaveAi(x => x with { AutoTranslateSelection = v }));
        autoSwitch.IsEnabled = configured;
        autoSwitch.Opacity = configured ? 1 : 0.4;
        Group(Strings.Settings_Ai_Translation,
            Row(Strings.Settings_Ai_TargetLanguage, a.TranslateTarget == "auto" ? Strings.Settings_Ai_AutoLanguageDescription : null,
                Seg([("auto", Strings.Common_Auto), ("zh", Strings.Translate_LanguageChinese), ("en", Strings.Translate_LanguageEnglish), ("ja", Strings.Translate_LanguageJapanese)], a.TranslateTarget,
                    v => SaveAi(x => x with { TranslateTarget = v }))),
            TranslationModelRow(a),
            Row(Strings.Settings_Ai_AutoTranslate, configured ? Strings.Settings_Ai_AutoTranslateDescription : Strings.Settings_Ai_ConfigureFirst, autoSwitch));
        // 代理（042）：跟随系统 / 不走代理 / 自定义（http、socks5）；选「自定义」后填了地址才算数
        var mode = proxyCustomPending ? AiProxyMode.Custom : a.Proxy;
        var network = new List<FrameworkElement>
        {
            Row(Strings.Settings_Ai_Proxy, mode switch
                {
                    AiProxyMode.None => Strings.Settings_Ai_DirectDescription,
                    AiProxyMode.Custom => Strings.Settings_Ai_CustomProxyDescription,
                    _ => Strings.Settings_Ai_SystemProxyDescription,
                },
                Seg([("System", Strings.Settings_FollowSystem), ("None", Strings.Settings_Ai_Direct), ("Custom", Strings.Settings_Ai_CustomProxy)], mode.ToString(), v =>
                {
                    var m = Enum.Parse<AiProxyMode>(v);
                    proxyCustomPending = m == AiProxyMode.Custom && a.ProxyUrl == null;
                    if (!proxyCustomPending) SaveAi(x => x with { ProxyMode = m });
                    if (aiLast == "proxy") aiResult = null;
                    Later();
                })),
        };
        if (mode == AiProxyMode.Custom)
            network.Add(Row(Strings.Settings_Ai_ProxyAddress, Strings.Settings_Ai_ProxyPlaceholder,
                CommitField("http://127.0.0.1:7890", 260, a.ProxyUrl ?? "", v =>
                {
                    if (v.Length == 0) return Strings.Settings_Ai_ProxyRequired;
                    if (!AiSettings.ValidProxy(v)) return Strings.Settings_Ai_ProxyInvalid;
                    var error = Try(() => SaveAi(x => x with { ProxyUrl = v, ProxyMode = AiProxyMode.Custom }));
                    if (error == null) proxyCustomPending = false;
                    return error;
                })));
        var proxyTest = Button(aiBusy == "proxy" ? Strings.Settings_Ai_Testing : Strings.Common_Test, null, () => _ = RunAi("proxy", async t => { await app.AiSetup.TestProxyAsync(t); return Strings.Settings_Ai_ProxyConnected; }));
        Enable(proxyTest, aiBusy == null && !proxyCustomPending);
        network.Add(aiResult is { } pr && aiLast == "proxy" ? Row(Strings.Settings_Ai_TestProxy, null, Status(pr.Ok, pr.Text), proxyTest) : Row(Strings.Settings_Ai_TestProxy, Strings.Settings_Ai_TestProxyDescription, proxyTest));
        Group(Strings.Settings_Ai_Network, network.ToArray(),
            Strings.Settings_Ai_PrivacyDescription);
    }

    // API Key：只显示掩码；输入框只在要改的时候用，保存后清空
    FrameworkElement KeyRow(IAiSetup setup)
    {
        var box = new PasswordBox
        {
            PlaceholderText = setup.ApiKeyMask == null ? "sk-…" : Strings.Settings_Ai_ReplaceKey, Width = 220, Height = PaneTokens.ControlMd, Padding = new Thickness(10, 6, 10, 0),
            FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize,
        };
        void Save()
        {
            if (box.Password.Trim() is not { Length: > 0 } key) return;
            aiFieldError = Try(() => setup.SaveApiKey(key));
            aiResult = null;
            Later(0);
        }
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Save(); e.Handled = true; } };
        var wrap = new Border
        {
            Child = box, CornerRadius = new CornerRadius(((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft),
            Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
        };
        var controls = new List<FrameworkElement> { wrap, Button(Strings.Common_Save, "check", Save) };
        if (setup.ApiKeyMask != null)
            controls.Add(IconButton("close", Strings.Settings_Ai_DeleteKey, () => { aiFieldError = Try(() => setup.SaveApiKey("")); Later(0); }));
        return Row("API Key", setup.ApiKeyMask is { } mask ? string.Format(Strings.Settings_Ai_KeySaved, (object?)(mask)) : Strings.Settings_Ai_KeyDescription, controls);
    }

    IEnumerable<FrameworkElement> AccountRows(IAiSetup setup)
    {
        if (aiBusy == "signin")
        {
            yield return Row(Strings.Settings_Ai_LoggingIn, Strings.Settings_Ai_LoginPendingDescription,
                Button(Strings.Common_Cancel, null, () => signIn?.Cancel()));
            yield break;
        }
        if (setup.Account is not { } account)
        {
            yield return Row(Strings.Settings_Ai_LoginTitle, Strings.Settings_Ai_LoginDescription,
                Button(Strings.Settings_Ai_Login, "login", () => _ = SignIn(setup)));
            if (aiResult is { Ok: false } r) yield return Row(Strings.Settings_Ai_LoginFailed, null, Status(false, r.Text));
            yield break;
        }
        // 套餐名官方不保证给：不知道时只说能不能用，不猜 Plus / Pro
        var plan = !account.CanUsePlan ? Strings.Settings_Ai_FreeAccount
            : account.PlanName is { Length: > 0 } name ? string.Format(Strings.Settings_Ai_Subscription, (object?)(name)) : Strings.Settings_Ai_SubscriptionUnknown;
        yield return Row(account.Email, plan, Button(Strings.Settings_Ai_Logout, "logout", () => _ = RunAi("signout", async t => { await setup.SignOutAsync(t); return null; })));
    }

    async Task SignIn(IAiSetup setup)
    {
        signIn?.Dispose();
        signIn = new CancellationTokenSource();
        await RunAi("signin", async _ => { await setup.SignInAsync(signIn.Token); return null; });
    }

    // 模型：可以手填；拉取列表后，下面按输入过滤出最多 12 个可点的候选
    FrameworkElement ModelRow(AiSettings a, bool api)
    {
        string current = (api ? a.ApiModel : a.ChatGptModel) ?? "";
        var box = Field(api ? Strings.Settings_Ai_ModelId : Strings.Settings_Ai_FirstModelDefault, 220);
        box.Text = current;
        var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        void Commit(string value)
        {
            var v = value.Trim();
            aiFieldError = Try(() => SaveAi(x => api ? x with { ApiModel = v.Length == 0 ? null : v } : x with { ChatGptModel = v.Length == 0 ? null : v }));
        }
        void Fill()
        {
            chips.Children.Clear();
            if (aiModels is not { } models) return;
            var q = box.Text.Trim();
            foreach (var m in models.Where(m => q.Length == 0 || q == current || m.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(12))
            {
                var chip = Chip(m.DisplayName == m.Id ? m.Id : $"{m.DisplayName}（{m.Id}）");
                chip.Height = PaneTokens.ControlSm;
                chip.CornerRadius = new CornerRadius(13);
                if (m.Id == current) chip.BorderBrush = Accent();
                chip.Tapped += (_, _) => { Commit(m.Id); Later(0); };
                chips.Children.Add(chip);
            }
        }
        box.TextChanged += (_, _) => Fill();
        box.LostFocus += (_, _) => { if (box.Text.Trim() != current) { Commit(box.Text); Later(); } };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(box.Text); Later(0); e.Handled = true; } };
        Fill();
        var load = Button(aiBusy == "models" ? Strings.Settings_Ai_LoadingModels : Strings.Settings_Ai_FetchModels, "refresh", () => _ = RunAi("models", async t =>
        {
            aiModels = await app.Ai.ListModelsAsync(t);
            return aiModels.Count == 0 ? Strings.Settings_Ai_NoModels : null;
        }));
        Enable(load, aiBusy == null);
        var row = Row(Strings.Settings_Ai_Model, aiModels is { } list ? string.Format(Strings.Settings_Ai_ModelsCount, (object?)(list.Count)) : api ? Strings.Settings_Ai_ModelDescription : Strings.Settings_Ai_FirstModelDescription, Wrap(box), load);
        if (aiModels == null) return row;
        var stack = new StackPanel();
        stack.Children.Add(row);
        chips.Margin = new Thickness(14, 0, 14, 10);
        stack.Children.Add(chips);
        return stack;
    }

    // 启动器动作行（2026-10-30）：输入任何文字都出现的那一排。显示的在前、按顺序，可上移；关掉的排在后面。
    // 候选：翻译、问 AI、搜文件、其余 AI 预设动作、每个网页搜索站点
    void RenderActionRow(LauncherSettings l)
    {
        var ai = app.Store.Current.Ai;
        var all = new List<(string Id, string Name, string Glyph)> { ("ai:translate", Strings.Common_Translate, "translate"), ("ai:ask", Strings.Launcher_AskAi, "ai"), ("files", Strings.Launcher_SearchFiles, "folder") };
        all.AddRange(ai.Actions.Where(a => a.Id != "translate").Select(a => ("ai:" + a.Id, a.Name, a.Glyph)));
        all.AddRange(l.SearchEngines.Select(e => ("web:" + e.Keyword, string.Format(Strings.Launcher_SearchWith, (object?)(e.Name)), "globe")));
        var shown = l.ActionRow.Where(id => all.Any(a => a.Id == id)).ToList();
        var ordered = shown.Select(id => all.First(a => a.Id == id)).Concat(all.Where(a => !shown.Contains(a.Id))).ToList();
        var rows = new List<FrameworkElement>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (id, name, glyph) = ordered[i];
            bool on = shown.Contains(id);
            int pos = shown.IndexOf(id);
            var tools = new List<FrameworkElement>();
            if (on && pos > 0)
                tools.Add(IconButton("U", Strings.Common_MoveUp, () => { SaveLauncher(x => { var list = x.ActionRow.Where(v => all.Any(a => a.Id == v)).ToList(); (list[pos - 1], list[pos]) = (list[pos], list[pos - 1]); return x with { ActionRow = list.ToArray() }; }); Render(); }));
            tools.Add(Switch(on, v =>
            {
                SaveLauncher(x => x with { ActionRow = v ? [.. x.ActionRow.Where(a => a != id), id] : x.ActionRow.Where(a => a != id).ToArray() });
                Render();
            }));
            rows.Add(ListRow(glyph, name, on ? string.Format(pos < 9 ? Strings.Settings_Launcher_ActionPositionHotkey : Strings.Settings_Launcher_ActionPosition, pos + 1, pos + 1) : Strings.Settings_Launcher_ActionHidden, false, tools.ToArray()));
        }
        Group(Strings.Settings_Launcher_ActionRow, rows.ToArray(), Strings.Settings_Launcher_ActionRowDescription);
    }

    // 翻译用模型：单独指定一个快模型，空着 = 和主模型相同。候选用上面「拉取模型列表」拉到的
    FrameworkElement TranslationModelRow(AiSettings a)
    {
        string current = a.TranslationModel ?? "";
        var box = Field(Strings.Settings_Ai_SameAsMain, 220);
        box.Text = current;
        void Commit(string value)
        {
            var v = value.Trim();
            aiFieldError = Try(() => SaveAi(x => x with { TranslationModel = v.Length == 0 ? null : v }));
        }
        box.LostFocus += (_, _) => { if (box.Text.Trim() != current) { Commit(box.Text); Later(); } };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(box.Text); Later(0); e.Handled = true; } };
        var row = Row(Strings.Settings_Ai_TranslationModel, aiModels is null ? Strings.Settings_Ai_TranslationModelDescription : Strings.Settings_Ai_TranslationModelManualDescription, Wrap(box));
        if (aiModels is not { } models) return row;
        var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(14, 0, 14, 10) };
        var main = Chip(Strings.Settings_Ai_SameAsMain);
        main.Height = PaneTokens.ControlSm; main.CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2);
        if (current.Length == 0) main.BorderBrush = Accent();
        main.Tapped += (_, _) => { Commit(""); Later(0); };
        chips.Children.Add(main);
        foreach (var m in models.Take(12))
        {
            var chip = Chip(m.DisplayName == m.Id ? m.Id : $"{m.DisplayName}（{m.Id}）");
            chip.Height = PaneTokens.ControlSm; chip.CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2);
            if (m.Id == current) chip.BorderBrush = Accent();
            chip.Tapped += (_, _) => { Commit(m.Id); Later(0); };
            chips.Children.Add(chip);
        }
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(chips);
        return stack;
    }

    // 预设动作：名称 + 提示词（多行，离开输入框时保存），可上移、删除（翻译是启动器固定入口，不能删），可添加
    void RenderAiActions(AiSettings a)
    {
        var list = new List<FrameworkElement>();
        for (int i = 0; i < a.Actions.Length; i++)
        {
            var act = a.Actions[i];
            int index = i;
            var name = Field(Strings.Common_Name, 120);
            name.Text = act.Name;
            name.LostFocus += (_, _) => { var v = name.Text.Trim(); if (v.Length > 0 && v != act.Name) aiFieldError = Try(() => SaveAction(index, x => x with { Name = v })); };
            var prompt = Field(Strings.Settings_Ai_ActionPrompt, 0);
            prompt.Width = double.NaN;
            prompt.Height = 92;
            prompt.AcceptsReturn = true;
            prompt.TextWrapping = TextWrapping.Wrap;
            prompt.Padding = new Thickness(10, 8, 10, 8);
            prompt.Text = act.PromptTemplate;
            prompt.LostFocus += (_, _) =>
            {
                var v = prompt.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                if (v.Trim().Length == 0) { aiFieldError = Strings.Settings_Ai_ActionPromptRequired; Later(0); return; }
                if (v != act.PromptTemplate) aiFieldError = Try(() => SaveAction(index, x => x with { PromptTemplate = v }));
            };
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new PaneIcon { Glyph = act.Glyph, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = IconProto.Foreground });
            var nameBox = Wrap(name);
            Grid.SetColumn(nameBox, 1);
            head.Children.Add(nameBox);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (index > 0) tools.Children.Add(IconButton("U", Strings.Common_MoveUp, () => { MoveAction(index); Render(); }));
            if (act.Id != "translate")
                tools.Children.Add(IconButton("close", Strings.Settings_Ai_DeleteAction, () => { SaveAi(x => x with { Actions = x.Actions.Where((_, j) => j != index).ToArray() }); Render(); }));
            Grid.SetColumn(tools, 3);
            head.Children.Add(tools);
            var block = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 12) };
            block.Children.Add(head);
            block.Children.Add(Wrap(prompt));
            list.Add(block);
        }
        list.Add(AddRow(Button(Strings.Settings_Ai_AddAction, "plus", () =>
        {
            SaveAi(x => x with { Actions = [.. x.Actions, new AiActionSettings("custom-" + DateTime.Now.ToString("yyyyMMddHHmmss"), "新动作", "ai", "对下面的内容……\n\n{text}")] }); // 不翻译：用户数据
            Render();
        })));
        Group(Strings.Settings_Ai_Actions, list.ToArray(), Strings.Settings_Ai_ActionsDescription);
    }

    void SaveAction(int index, Func<AiActionSettings, AiActionSettings> change) =>
        SaveAi(x => x with { Actions = x.Actions.Select((v, j) => j == index ? change(v) : v).ToArray() });

    void MoveAction(int index) => SaveAi(x =>
    {
        var list = x.Actions.ToList();
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        return x with { Actions = list.ToArray() };
    });

    // 输入框：离开或回车时提交；commit 返回 null 表示保存成功，否则是要显示的错误
    Border CommitField(string placeholder, double width, string value, Func<string, string?> commit)
    {
        var box = Field(placeholder, width);
        box.Text = value;
        void Commit() { var v = box.Text.Trim(); if (v == value) return; aiFieldError = commit(v); Later(0); }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
        return Wrap(box);
    }

    // 联网操作：同一时间一个；结果写进 aiResult，完成后刷新（还在 AI 分区才刷新）
    async Task RunAi(string what, Func<CancellationToken, Task<string?>> work)
    {
        if (aiBusy != null) return;
        aiBusy = what;
        aiLast = what;
        aiResult = null;
        Render();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(what == "signin" ? 6 : 1));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, what == "signin" && signIn != null ? signIn.Token : CancellationToken.None);
            var text = await work(linked.Token);
            if (text != null) aiResult = (what != "models" || aiModels is { Count: > 0 }, text);
        }
        catch (OperationCanceledException) { }
        catch (AiException e) { aiResult = (false, e.Message); }
        catch (Exception e) { aiResult = (false, App.FailureReason(e) ?? Strings.Common_UnexpectedProblem); }
        finally { aiBusy = null; }
        if (current.Id == "ai") Render();
    }

    static string? Try(Action save)
    {
        try { save(); return null; }
        catch (Exception e) { return e is ArgumentException ? Strings.Settings_SaveInvalid : App.FailureReason(e) ?? Strings.Settings_SaveFailed; }
    }

    void SaveAi(Func<AiSettings, AiSettings> change) =>
        app.Store.Save(app.Store.Current with { Ai = change(app.Store.Current.Ai) });

    // 任何设置保存都会让 AI 服务发 Changed；立刻重绘会把刚拨的开关换成新控件，滑块动画被截断成跳变（2026-10-31），
    // 所以等开关动画（PaneDurNormal）播完再重绘
    void OnAiChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "ai" && aiBusy == null) Later(); });

    // ================= 手势编辑器（添加 / 编辑一个全局手势） =================
    // 画方向：在画板里按住左键画（和真实手势用同一个识别器），或点右边的 8 个方向键逐段添加。
    // 草稿只在设置窗口里，点「保存」才写进 gestures.json 并立即生效。

    sealed class GestureDraft
    {
        public int Index = -1;                                  // -1 = 新加
        public int App = -1;                                    // -1 = 全局手势；≥0 = 按程序设置里第几个程序的专用手势
        public G.GestureButton Button = G.GestureButton.Right;
        public List<G.GestureDirection> Directions = [];
        public G.GestureModifier Modifier = G.GestureModifier.None;
        public string Name = "";
        public G.GestureCommand Command = new G.HotkeyCommand([]);
        public bool ExecuteOnModifier;
        public string? Error;
    }

    GestureDraft? draft;

    static readonly (string Key, string Label)[] CommandKinds =
    [
        ("hotkey", Strings.Settings_Gestures_CommandHotkey), ("window", Strings.Settings_Gestures_CommandWindow), ("volume", Strings.Settings_Gestures_CommandVolume), ("open", Strings.Settings_Gestures_CommandOpen), ("run", Strings.Settings_Gestures_Run),
        ("text", Strings.Settings_Gestures_SendText), ("search", Strings.Settings_Gestures_CommandWebSearch), ("task", Strings.Settings_Gestures_SwitchWindow), ("pause", Strings.Settings_Gestures_Pause), ("none", Strings.Settings_Gestures_NoOp),
    ];

    static string KindOf(G.GestureCommand c) => c switch
    {
        G.HotkeyCommand => "hotkey", G.WindowCommand => "window", G.VolumeCommand => "volume", G.OpenCommand => "open",
        G.RunCommand => "run", G.SendTextCommand => "text", G.WebSearchCommand => "search", G.TaskSwitchCommand => "task",
        G.PauseGesturesCommand => "pause", _ => "none",
    };

    G.GestureCommand NewCommand(string kind) => kind switch
    {
        "hotkey" => new G.HotkeyCommand([]),
        "window" => new G.WindowCommand(G.WindowAction.MaximizeRestore),
        "volume" => new G.VolumeCommand(G.VolumeAction.Up, 5),
        "open" => new G.OpenCommand(""),
        "run" => new G.RunCommand("", false, true),
        "text" => new G.SendTextCommand(""),
        // 默认用启动器里设为默认的搜索站点
        "search" => new G.WebSearchCommand(app.Store.Current.Launcher.SearchEngines.FirstOrDefault(e => e.Keyword == app.Store.Current.Launcher.DefaultEngine)?.UrlTemplate ?? "https://www.bing.com/search?q={query}"),
        "task" => new G.TaskSwitchCommand(),
        "pause" => new G.PauseGesturesCommand(),
        _ => new G.DoNothingCommand(),
    };

    void EditGesture(int index, int appIndex = -1) { draft = NewDraft(index, appIndex); revealKey = "gesture"; Render(); }

    // 草稿所属的手势列表：全局，或某个程序的专用手势
    static IReadOnlyList<G.GestureBinding> BindingsOf(G.GestureConfig config, int appIndex) =>
        appIndex < 0 ? config.Global : appIndex < config.Apps.Count ? config.Apps[appIndex].Bindings : [];

    GestureDraft NewDraft(int index, int appIndex = -1)
    {
        var config = app.GestureConfig;
        var list = BindingsOf(config, appIndex);
        if (index < 0 || index >= list.Count) return new GestureDraft { App = appIndex, Button = FirstButton(config.Settings.EnabledButtons) };
        var g = list[index];
        return new GestureDraft
        {
            Index = index, App = appIndex, Button = g.Pattern.Button, Directions = [.. g.Pattern.Directions], Modifier = g.Pattern.Modifier,
            Name = g.Name, Command = g.Command, ExecuteOnModifier = g.ExecuteOnModifier,
        };
    }

    static G.GestureButton FirstButton(G.GestureButton enabled) =>
        new[] { G.GestureButton.Right, G.GestureButton.Middle, G.GestureButton.X1, G.GestureButton.X2 }.FirstOrDefault(b => enabled.HasFlag(b), G.GestureButton.Right);

    FrameworkElement GestureEditor(GestureDraft d, G.GestureConfig config)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(14, 14, 14, 14) };

        // ---- 画方向 ----
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
        void ShowArrows()
        {
            arrows.Children.Clear();
            if (d.Directions.Count == 0) { arrows.Children.Add(Text(d.Modifier == G.GestureModifier.None ? Strings.Settings_Gestures_DirectionMissing : Strings.Settings_Gestures_ModifierOnly, DescProto)); return; }
            foreach (var dir in d.Directions)
                arrows.Children.Add(new PaneIcon { Glyph = ArrowGlyphs[(int)dir], Width = 24, Height = 24, StrokeWidth = 2.2, Foreground = LabelProto.Foreground });
        }
        ShowArrows();

        var pad = DrawPad(d, config.Settings.AllowDiagonalMultiStroke, ShowArrows);
        var keys = new Grid { RowSpacing = 4, ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++) { keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); }
        // 九宫格：外圈 8 个方向（顺序同 GestureDirection 的位置），中间是退一格
        (int Row, int Col, G.GestureDirection Dir)[] layout =
        [
            (0, 0, G.GestureDirection.UpLeft), (0, 1, G.GestureDirection.Up), (0, 2, G.GestureDirection.UpRight),
            (1, 0, G.GestureDirection.Left), (1, 2, G.GestureDirection.Right),
            (2, 0, G.GestureDirection.DownLeft), (2, 1, G.GestureDirection.Down), (2, 2, G.GestureDirection.DownRight),
        ];
        foreach (var (r, c, dir) in layout)
        {
            var b = IconButton(ArrowGlyphs[(int)dir], Strings.Settings_Gestures_AddDirection, () =>
            {
                if (d.Directions.Count > 0 && d.Directions[^1] == dir) return;   // 同方向连着算一段
                d.Directions.Add(dir); d.Error = null; ShowArrows();
            });
            Grid.SetRow(b, r); Grid.SetColumn(b, c); keys.Children.Add(b);
        }
        var back = IconButton("close", Strings.Settings_Gestures_RemoveDirection, () => { if (d.Directions.Count > 0) d.Directions.RemoveAt(d.Directions.Count - 1); ShowArrows(); });
        Grid.SetRow(back, 1); Grid.SetColumn(back, 1); keys.Children.Add(back);
        var drawRow = new Grid { ColumnSpacing = 16 };
        drawRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        drawRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        drawRow.Children.Add(pad);
        Grid.SetColumn(keys, 1); drawRow.Children.Add(keys);
        panel.Children.Add(drawRow);
        panel.Children.Add(FormLine(Strings.Settings_Gestures_Direction, arrows));

        // ---- 触发键、附加动作 ----
        panel.Children.Add(FormLine(Strings.Settings_Gestures_TriggerButton, Choices([("Right", Strings.Settings_Gestures_RightButton), ("Middle", Strings.Settings_Gestures_MiddleButton), ("X1", Strings.Settings_Gestures_BackButton), ("X2", Strings.Settings_Gestures_ForwardButton)], d.Button.ToString(),
            v => d.Button = Enum.Parse<G.GestureButton>(v))));
        panel.Children.Add(FormLine(Strings.Settings_Gestures_Modifier, Choices([("None", Strings.Common_None), ("WheelUp", Strings.Settings_Gestures_ModifierWheelUp), ("WheelDown", Strings.Settings_Gestures_ModifierWheelDown), ("LeftClick", Strings.Settings_Gestures_ModifierLeft), ("MiddleClick", Strings.Settings_Gestures_ModifierMiddle), ("X1Click", Strings.Settings_Gestures_ModifierBack), ("X2Click", Strings.Settings_Gestures_ModifierForward)],
            d.Modifier.ToString(), v => d.Modifier = Enum.Parse<G.GestureModifier>(v))));
        if (d.Modifier != G.GestureModifier.None)
        {
            var repeat = Switch(d.ExecuteOnModifier, v => d.ExecuteOnModifier = v);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(repeat);
            line.Children.Add(Text(Strings.Settings_Gestures_ModifierDescription, DescProto));
            panel.Children.Add(FormLine("", line));
        }

        // ---- 动作 ----
        panel.Children.Add(FormLine(Strings.Settings_Gestures_Action, Choices(CommandKinds, KindOf(d.Command), v => d.Command = NewCommand(v))));
        if (CommandEditor(d) is { } editor) panel.Children.Add(FormLine("", editor));

        var name = Field(Strings.Settings_Gestures_NameDescription, 300);
        name.Text = d.Name;
        name.TextChanged += (_, _) => d.Name = name.Text;
        panel.Children.Add(FormLine(Strings.Common_Name, Wrap(name)));

        // ---- 保存 ----
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foot.Children.Add(Button(Strings.Common_Save, "check", () => SaveDraft(d)));
        foot.Children.Add(Button(Strings.Common_Cancel, null, () => Collapse("gesture", () => { draft = null; Render(); })));
        if (d.Error != null) foot.Children.Add(Status(false, d.Error));
        panel.Children.Add(FormLine("", foot));
        return panel;
    }

    // 画板：按住左键画，松开就是这次的方向（和真实手势同一个识别器；阈值比屏幕上小一点，画板不大）
    FrameworkElement DrawPad(GestureDraft d, bool diagonal, Action changed)
    {
        var line = new Microsoft.UI.Xaml.Shapes.Polyline { Stroke = Accent(), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var canvas = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        canvas.Children.Add(line);
        var hint = Text(Strings.Settings_Gestures_DrawHint, DescProto);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.IsHitTestVisible = false;
        var host = new Grid { Height = 168 };
        host.Children.Add(hint);
        host.Children.Add(canvas);
        G.GestureRecognizer? recognizer = null;
        canvas.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(canvas);
            if (!p.Properties.IsLeftButtonPressed) return;
            canvas.CapturePointer(e.Pointer);
            recognizer = new G.GestureRecognizer(new((int)p.Position.X, (int)p.Position.Y), 64, diagonal);
            line.Points.Clear();
            line.Points.Add(p.Position);
            hint.Opacity = 0;
            d.Directions.Clear(); d.Error = null; changed();
            e.Handled = true;
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (recognizer == null) return;
            var p = e.GetCurrentPoint(canvas).Position;
            line.Points.Add(p);
            if (recognizer.Add(new((int)p.X, (int)p.Y))) { d.Directions.Clear(); d.Directions.AddRange(recognizer.Directions); changed(); }
        };
        void End(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { if (recognizer == null) return; recognizer = null; canvas.ReleasePointerCapture(e.Pointer); }
        canvas.PointerReleased += End;
        canvas.PointerCaptureLost += (_, _) => recognizer = null;
        return new Border
        {
            CornerRadius = new CornerRadius(((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft), Background = FillProto.Background,
            BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1), Child = host,
        };
    }

    // 每种动作自己的参数
    FrameworkElement? CommandEditor(GestureDraft d)
    {
        switch (d.Command)
        {
            case G.HotkeyCommand h:
            {
                // 一个或连续几个按键（WGestures 的「连续按键」）
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var chords = h.Chords.ToList();
                for (int i = 0; i <= chords.Count; i++)
                {
                    if (i == chords.Count && chords.Count > 0) break;
                    int at = i;
                    var k = at < chords.Count ? chords[at] : default;
                    var box = new HotkeyBox(new Settings.Hotkey((KeyModifiers)(int)k.Modifiers, k.VirtualKey), SmallProto, KbdProto, FillProto.Background, FillProto.BorderBrush, Accent()) { AllowBareKeys = true };
                    box.RecordingChanged += (_, on) => app.SetHotkeysSuspended(on);
                    box.Changed += (_, v) =>
                    {
                        var next = new G.KeyChord((G.KeyModifiers)(int)v.Modifiers, v.VirtualKey);
                        var list = ((G.HotkeyCommand)d.Command).Chords.ToList();
                        if (at < list.Count) list[at] = next; else list.Add(next);
                        d.Command = new G.HotkeyCommand(list); d.Error = null;
                    };
                    row.Children.Add(box);
                }
                if (chords.Count > 0)
                {
                    row.Children.Add(IconButton("plus", Strings.Settings_Gestures_AddChord, () => { d.Command = new G.HotkeyCommand([.. ((G.HotkeyCommand)d.Command).Chords, default]); Render(); }));
                    if (chords.Count > 1) row.Children.Add(IconButton("close", Strings.Settings_Gestures_RemoveChord, () => { d.Command = new G.HotkeyCommand(((G.HotkeyCommand)d.Command).Chords.SkipLast(1).ToArray()); Render(); }));
                }
                return row;
            }
            case G.WindowCommand w:
                return Choices([("MaximizeRestore", Strings.Settings_Gestures_Maximize), ("Minimize", Strings.Settings_Gestures_Minimize), ("Close", Strings.Settings_Gestures_Close), ("ToggleTopmost", Strings.Settings_Gestures_ToggleTopmost), ("SnapLeft", Strings.Settings_Gestures_LeftHalf), ("SnapRight", Strings.Settings_Gestures_RightHalf)],
                    w.Action.ToString(), v => d.Command = new G.WindowCommand(Enum.Parse<G.WindowAction>(v)));
            case G.VolumeCommand v:
            {
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(Choices([("Up", Strings.Settings_Gestures_IncreaseVolume), ("Down", Strings.Settings_Gestures_DecreaseVolume), ("ToggleMute", Strings.Settings_Gestures_ToggleMute), ("WheelAdjust", Strings.Settings_Gestures_FollowWheel)], v.Action.ToString(),
                    x => d.Command = (G.VolumeCommand)d.Command with { Action = Enum.Parse<G.VolumeAction>(x) }));
                if (v.Action != G.VolumeAction.ToggleMute)
                    stack.Children.Add(Choices([("2", Strings.Settings_Gestures_VolumeStepTwo), ("5", Strings.Settings_Gestures_VolumeStepFive), ("10", Strings.Settings_Gestures_VolumeStepTen)], v.Step.ToString(),
                        x => d.Command = (G.VolumeCommand)d.Command with { Step = int.Parse(x) }));
                return stack;
            }
            case G.OpenCommand o:
            {
                var box = Field(Strings.Settings_Gestures_OpenPlaceholder, 300);
                box.Text = o.Target;
                box.TextChanged += (_, _) => d.Command = new G.OpenCommand(box.Text.Trim());
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(Wrap(box));
                row.Children.Add(Button(Strings.Common_ChooseFile, "folder", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                    if ((await picker.PickSingleFileAsync())?.Path is { } path) { d.Command = new G.OpenCommand(path); if (d.Name.Length == 0) d.Name = "打开 " + Path.GetFileNameWithoutExtension(path); Render(); } // 不翻译：用户数据
                }));
                return row;
            }
            case G.RunCommand r:
            {
                var box = Field(Strings.Settings_Gestures_RunPlaceholder, 300);
                box.Text = r.CommandLine;
                box.TextChanged += (_, _) => d.Command = (G.RunCommand)d.Command with { CommandLine = box.Text.Trim() };
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(Wrap(box));
                var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                opts.Children.Add(Switch(r.InExplorerFolder, x => d.Command = (G.RunCommand)d.Command with { InExplorerFolder = x }));
                opts.Children.Add(Text(Strings.Settings_Gestures_RunInExplorer, DescProto));
                opts.Children.Add(Switch(r.ShowWindow, x => d.Command = (G.RunCommand)d.Command with { ShowWindow = x }));
                opts.Children.Add(Text(Strings.Settings_Gestures_ShowCommandWindow, DescProto));
                stack.Children.Add(opts);
                return stack;
            }
            case G.SendTextCommand t:
            {
                var box = Field(Strings.Settings_Gestures_TextPlaceholder, 300);
                box.Text = t.Sequence;
                box.TextChanged += (_, _) => d.Command = new G.SendTextCommand(box.Text);
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(Wrap(box));
                stack.Children.Add(Text(Strings.Settings_Gestures_SendKeysDescription, DescProto));
                return stack;
            }
            case G.WebSearchCommand s:
            {
                var box = Field(Strings.Settings_Gestures_UrlPlaceholder, 360);
                box.Text = s.UrlTemplate;
                box.TextChanged += (_, _) => d.Command = new G.WebSearchCommand(box.Text.Trim());
                return Wrap(box);
            }
        }
        return null;
    }

    void SaveDraft(GestureDraft d)
    {
        var config = app.GestureConfig;
        var pattern = new G.GesturePattern(d.Button, d.Directions.ToArray(), d.Modifier);
        string? error =
            d.Directions.Count == 0 && d.Modifier == G.GestureModifier.None ? Strings.Settings_Gestures_PatternRequired
            : d.Command switch
            {
                G.HotkeyCommand h when h.Chords.Count == 0 || h.Chords.Any(k => k.VirtualKey == 0) => Strings.Settings_Gestures_ChordRequired,
                G.OpenCommand o when o.Target.Length == 0 => Strings.Settings_Gestures_TargetRequired,
                G.RunCommand r when r.CommandLine.Length == 0 => Strings.Settings_Gestures_CommandRequired,
                G.SendTextCommand t when t.Sequence.Length == 0 => Strings.Settings_Gestures_TextRequired,
                G.WebSearchCommand w when !w.UrlTemplate.Contains("{query}") => Strings.Settings_Gestures_QueryRequired,
                G.VolumeCommand { Action: G.VolumeAction.WheelAdjust } when d.Modifier is not (G.GestureModifier.WheelUp or G.GestureModifier.WheelDown) => Strings.Settings_Gestures_WheelRequired,
                _ => null,
            }
            // 只在同一个列表里查重：程序专用手势和全局一样是允许的（这个程序里用专用的）
            ?? (BindingsOf(config, d.App).Select((g, i) => (g, i)).FirstOrDefault(x => x.i != d.Index && G.GestureMatcher.Same(x.g.Pattern, pattern)) is { g: { } same } ? string.Format(Strings.Settings_Gestures_DuplicatePattern, (object?)(same.Name)) : null);
        if (error != null) { d.Error = error; Render(); return; }
        var name = d.Name.Trim().Length > 0 ? d.Name.Trim() : CommandText(d.Command);
        var binding = new G.GestureBinding(name, pattern, d.Command, d.Modifier != G.GestureModifier.None && d.ExecuteOnModifier);
        var (index, appIndex) = (d.Index, d.App);
        draft = null;
        static IReadOnlyList<G.GestureBinding> Put(IReadOnlyList<G.GestureBinding> list, int index, G.GestureBinding b) =>
            index < 0 ? [.. list, b] : list.Select((g, i) => i == index ? b : g).ToArray();
        if (appIndex < 0) SaveGestures(x => x with { Global = Put(x.Global, index, binding) });
        else SaveGestures(x => x with { Apps = x.Apps.Select((r, j) => j == appIndex ? r with { Bindings = Put(r.Bindings, index, binding) } : r).ToArray() });
    }

    // 单选的小标签（复用 ToggleChip：选中的那个点了不响应）
    FrameworkElement Choices((string Key, string Label)[] options, string value, Action<string> pick)
    {
        var wrap = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (var (key, label) in options)
            wrap.Children.Add(ToggleChip(label, null, key == value, true, on => { if (on) pick(key); }));
        return wrap;
    }

    // 编辑器里的一行：左边 72px 的标签，右边控件
    FrameworkElement FormLine(string label, FrameworkElement control)
    {
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Text(label, SmallProto);
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(0, 5, 0, 0);
        line.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        line.Children.Add(control);
        return line;
    }

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
        // 关掉超级面板时其余各项变灰（原型 .srow.disabled）
        foreach (var c in new Control[] { trigger, hold, fullscreen, borrow }) Enable(c, p.Enabled);
        holdValue.Opacity = p.Enabled ? 1 : 0.4;
        Group(Strings.Settings_Super_Basic,
            Row(Strings.Settings_Super_Enable, null, Switch(p.Enabled, v => { SaveSuper(x => x with { Enabled = v }); Later(); })),
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
            Enable(toggle, p.Enabled && (on || !full));
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
        foreach (var c in new Control[] { jump, menu, many, auto, recent }) Enable(c, f.Enabled);
        var problem = f.Enabled ? app.FolderJump?.HotkeyProblem : null;
        Group(Strings.Settings_FolderJump_Basic,
            Row(Strings.Settings_FolderJump_Enable, f.Enabled ? Strings.Settings_FolderJump_EnableDescription : Strings.Settings_FolderJump_Disabled,
                Switch(f.Enabled, v => { SaveFolderJump(x => x with { Enabled = v }); Later(); })));
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

    // ================= 关于 =================

    void RenderAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? Strings.Update_DevelopmentVersion;
        Group(null,
            Row("Pane", Strings.Settings_About_DescriptionTagline, Chip(version)));
        if (app.Updates is { } updates) RenderUpdate(updates);
        Group(Strings.Settings_About_Information,
            Row(Strings.Settings_About_License, Strings.Settings_About_LicenseDescription, Button(Strings.Common_Open, "external", () => OpenUrl(SourceUrl))),
            Row(Strings.Settings_About_DataLocation, app.Store.DirectoryPath, Button(Strings.Common_Open, "external", () =>
            {
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(app.Store.DirectoryPath);
                System.Diagnostics.Process.Start(start)?.Dispose();
            })));
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        var noticeButton = Button(Strings.Settings_About_ViewAll, "external", () => OpenUrl(notices));
        Enable(noticeButton, File.Exists(notices));
        Group(Strings.Settings_About_Components,
            ListRow("app", Strings.Settings_About_Lucide, Strings.Settings_About_LucideLicense, false),
            ListRow("app", "Windows App SDK", Strings.Settings_About_WinUiLicense, false),
            ListRow("text", "MiSans", Strings.Settings_About_FontLicense, false),
            Row(Strings.Settings_About_AllComponents, Strings.Settings_About_NoticesLocation, noticeButton));
    }

    const string SourceUrl = "https://github.com/LBEILC/Pane";

    static void OpenUrl(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    // 软件内更新（2026-10-30，023）：状态一行 + 按钮；有新版时下面列出更新内容；「自动更新」开关
    CancellationTokenSource? updateWork;
    void RenderUpdate(IUpdateService u)
    {
        void Run(Func<CancellationToken, Task> work)
        {
            updateWork?.Cancel();
            updateWork = new CancellationTokenSource();
            _ = Guard(work(updateWork.Token));
            static async Task Guard(Task t) { try { await t; } catch (Exception) { } }   // 失败由服务放进 State / Error
        }
        string Ago(DateTimeOffset? t) => t is not { } v ? Strings.Update_NeverChecked
            : DateTimeOffset.Now - v < TimeSpan.FromMinutes(1) ? Strings.Update_JustChecked
            : DateTimeOffset.Now - v < TimeSpan.FromHours(1) ? string.Format(Strings.Update_CheckedMinutesAgo, (object?)((int)(DateTimeOffset.Now - v).TotalMinutes))
            : DateTimeOffset.Now - v < TimeSpan.FromDays(1) ? string.Format(Strings.Update_CheckedHoursAgo, (object?)((int)(DateTimeOffset.Now - v).TotalHours)) : string.Format(Strings.Update_CheckedOn, (object?)(v.LocalDateTime));
        string Size(long b) => $"{b / 1024.0 / 1024:0.#} MB";
        var check = Button(u.State == UpdateState.Checking ? Strings.Update_Checking : Strings.Update_CheckNow, "refresh", () => Run(u.CheckAsync));
        Enable(check, u.State is not (UpdateState.Checking or UpdateState.Downloading));
        var rows = new List<FrameworkElement>();
        var latest = u.Latest;
        switch (u.State)
        {
            case UpdateState.Available when latest is not null:
                rows.Add(Row(string.Format(Strings.Update_AvailableVersion, (object?)(latest.Version)), string.Format(Strings.Update_ReleaseDetails, (object?)(Size(latest.SizeBytes)), (object?)(latest.PublishedAt.LocalDateTime)),
                    Button(Strings.Common_ViewDetails, "external", () => OpenUrl(latest.PageUrl)), Button(Strings.Update_Download, "plus", () => Run(u.DownloadAsync))));
                break;
            case UpdateState.Downloading when latest is not null:
                rows.Add(Row(string.Format(Strings.Update_DownloadingVersion, (object?)(latest.Version)), string.Format(Strings.Update_DownloadProgress, (object?)(u.DownloadProgress), (object?)(Size(latest.SizeBytes))), Chip($"{u.DownloadProgress:P0}")));
                break;
            case UpdateState.Ready when latest is not null:
                rows.Add(Row(string.Format(Strings.Update_ReadyVersion, (object?)(latest.Version)), Strings.Update_InstallDescription,
                    Button(Strings.Update_RestartAndUpdate, "refresh", () => u.InstallAndRestart())));
                break;
            case UpdateState.Failed:
                rows.Add(Row(Strings.Update_Failed, null, Status(false, u.Error ?? Strings.Common_UnexpectedProblem), Button(Strings.Common_Retry, "refresh", () => Run(u.FailedStep == UpdateStep.Download ? u.DownloadAsync : u.CheckAsync))));
                break;
            default:
                rows.Add(Row(u.State == UpdateState.UpToDate ? Strings.Update_UpToDate : Strings.Update_CheckNow, string.Format(Strings.Update_CurrentVersion, (object?)(u.CurrentVersion), (object?)(Ago(u.LastChecked))), check));
                break;
        }
        if (u.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready && latest is { Notes.Length: > 0 })
        {
            var notes = new StackPanel { Spacing = 4, Padding = new Thickness(14, 2, 14, 12) };
            foreach (var line in latest.Notes.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•').Trim()).Where(l => l.Length > 0).Take(12))
            {
                var t = Text("· " + line, SmallProto);
                t.TextWrapping = TextWrapping.Wrap;
                notes.Children.Add(t);
            }
            rows.Add(notes);
        }
        rows.Add(Row(Strings.Update_Automatic, Strings.Update_AutomaticDescription,
            Switch(app.Store.Current.Update.AutoUpdate, v => app.Store.Save(app.Store.Current with { Update = app.Store.Current.Update with { AutoUpdate = v } }))));
        Group(Strings.Update_Title, rows.ToArray());
    }

    // ================= 拼装（原型 group / row / lrow） =================

    void Group(string? title, params FrameworkElement[] rows) => Group(title, rows, null);

    void Group(string? title, FrameworkElement[] rows, string? note)
    {
        var group = new StackPanel { Margin = new Thickness(0, Body.Children.Count == 0 ? 8 : 20, 0, 0) };
        if (title != null) group.Children.Add(new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 8), FontFamily = GroupTitleProto.FontFamily, FontSize = GroupTitleProto.FontSize, FontWeight = GroupTitleProto.FontWeight, Foreground = GroupTitleProto.Foreground });
        var list = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
            list.Children.Add(new Border { BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0), Child = rows[i] });
        group.Children.Add(new Border { CornerRadius = RowsProto.CornerRadius, Background = RowsProto.Background, BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(1), Child = list });
        if (note != null) group.Children.Add(new TextBlock { Text = note, Margin = new Thickness(2, 6, 2, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = DescProto.Foreground });
        Body.Children.Add(group);
    }

    // ---------------- 就地展开 / 收起的动效（设计改 2026-10-30，用户提：「玻璃参数（高级）」展开没有动效）----------------
    // 页面每次整页重建，展开的几行原来一下子出现、下面的内容直接跳下去。改为：展开的行放进一个容器，
    // 容器高度从 0 长到内容高度（超出部分裁掉）、内容同时淡入，下面的分组被平滑推开；收起反过来，播完才真的去掉。
    // 用法：Group(..., [header, Reveal("key", 展开的行...)])；打开前设 revealKey = "key"；收起用 Collapse("key", 真正收起)。
    string? revealKey;
    readonly Dictionary<string, (Grid Host, FrameworkElement List)> reveals = [];
    readonly Dictionary<string, Action> designToggles = [];   // 演示预览 PANE_DESIGN_REVEAL 用：各展开开关的点击动作

    FrameworkElement Reveal(string key, IEnumerable<FrameworkElement> rows)
    {
        var list = new StackPanel();
        int i = 0;
        foreach (var r in rows) list.Children.Add(new Border { BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(0, i++ == 0 ? 0 : 1, 0, 0), Child = r });
        var host = new Grid();
        host.Children.Add(list);
        reveals[key] = (host, list);
        if (revealKey == key)
        {
            revealKey = null;
            host.Height = 0;   // 第一帧就从 0 开始，不先露一下全高
            list.Opacity = 0;
            // 排到页面恢复滚动位置之后再开始（Render 末尾把原来的滚动位置排进了队列）
            void Once(object s, RoutedEventArgs e) { host.Loaded -= Once; DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => PlayReveal(host, list, true, null)); }
            host.Loaded += Once;
        }
        return host;
    }

    void Collapse(string key, Action done)
    {
        if (reveals.TryGetValue(key, out var r) && r.Host.IsLoaded && r.Host.ActualHeight > 0) PlayReveal(r.Host, r.List, false, done);
        else done();
    }

    double RevealMs => GlassParameters.Token("PaneDurNormal") * app.Glass.Current.MotionSpeed;

    void PlayReveal(Grid host, FrameworkElement list, bool open, Action? done)
    {
        double width = host.ActualWidth > 0 ? host.ActualWidth : double.PositiveInfinity;
        list.Measure(new Size(width, double.PositiveInfinity));
        double full = open ? list.DesiredSize.Height : host.ActualHeight;
        double ms = RevealMs;
        if (ms < 1 || full <= 0)
        {
            host.ClearValue(HeightProperty); list.Opacity = 1;
            done?.Invoke();
            return;
        }
        var clip = new RectangleGeometry { Rect = new Rect(0, 0, 100000, open ? 0 : full) };
        host.Clip = clip;
        void Resized(object s, SizeChangedEventArgs e) => clip.Rect = new Rect(0, 0, 100000, e.NewSize.Height);
        host.SizeChanged += Resized;
        // 在页面底部点开时，展开的内容会长在可视区外面：页面跟着往上滚，让展开的部分露出来（标题行留在视野里）
        double follow = 0;
        if (open)
        {
            double top = host.TransformToVisual(Scroller).TransformPoint(new Point(0, 0)).Y;
            follow = Math.Max(0, Math.Min(top + full + 16 - Scroller.ViewportHeight, top - 72));
        }
        double startOffset = Scroller.VerticalOffset;
        void Follow(object? s, object e) => Scroller.ChangeView(null, Math.Min(startOffset + follow, Scroller.ScrollableHeight), null, true);
        if (follow > 0) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Follow;
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(Ease(host, "Height", open ? 0 : full, open ? full : 0, ms, dependent: true));
        // 淡入比长高短一点、淡出比收起快一点，避免文字被裁一半时还很实
        board.Children.Add(Ease(list, "Opacity", open ? 0 : 1, open ? 1 : 0, open ? ms : ms * 0.6, dependent: false));
        board.Completed += (_, _) =>
        {
            host.SizeChanged -= Resized;
            if (follow > 0) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Follow; Follow(null, EventArgs.Empty); }
            if (open) { host.ClearValue(HeightProperty); host.Clip = null; list.Opacity = 1; }
            done?.Invoke();
        };
        board.Begin();
    }

    // 照令牌 PaneEaseSmooth（0.2,0,0,1）的关键帧动画
    static Microsoft.UI.Xaml.Media.Animation.Timeline Ease(DependencyObject target, string property, double from, double to, double ms, bool dependent)
    {
        var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames { EnableDependentAnimation = dependent };
        a.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        a.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(ms), Value = to,
            KeySpline = new Microsoft.UI.Xaml.Media.Animation.KeySpline { ControlPoint1 = new Point(0.2, 0), ControlPoint2 = new Point(0, 1) },
        });
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, property);
        return a;
    }

    // 展开箭头：跟着转（0° 收起、180° 展开）
    void SpinChevron(PaneIcon chevron, double to)
    {
        if (chevron.RenderTransform is not RotateTransform rotate) return;
        if (RevealMs < 1) { rotate.Angle = to; return; }
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(Ease(rotate, "Angle", rotate.Angle, to, RevealMs, dependent: false));
        board.Begin();
    }

    Grid Row(string label, string? desc, params FrameworkElement[] controls) => Row(label, desc, (IEnumerable<FrameworkElement>)controls);

    Grid Row(string label, string? desc, IEnumerable<FrameworkElement> controls)
    {
        var row = new Grid { MinHeight = 56, Padding = new Thickness(14, 10, 14, 10), ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (label.Length > 0) text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontFamily = LabelProto.FontFamily, FontSize = LabelProto.FontSize, Foreground = LabelProto.Foreground });
        if (desc != null) text.Children.Add(new TextBlock { Text = desc, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = DescProto.Foreground });
        row.Children.Add(text);
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls) { c.VerticalAlignment = VerticalAlignment.Center; ctl.Children.Add(c); }
        Grid.SetColumn(ctl, 1);
        row.Children.Add(ctl);
        return row;
    }

    FrameworkElement ListRow(string glyph, string label, string sub, bool mono, params FrameworkElement[] tail) => ListRow(glyph, Text(label, LabelProto), sub, mono, tail);

    FrameworkElement ListRow(string glyph, FrameworkElement label, string sub, bool mono, params FrameworkElement[] tail) =>
        ListRow(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new PaneIcon { Glyph = glyph, Width = 16, Height = 16, Foreground = IconProto.Foreground },
        }, label, sub, mono, tail);

    // lead：行首的图块；手势列表换成方向箭头（原型 .arrows）
    FrameworkElement ListRow(FrameworkElement lead, FrameworkElement label, string sub, bool mono, params FrameworkElement[] tail)
    {
        var row = new Grid { MinHeight = 48, Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lead.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(lead);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(label);
        var subText = Text(sub, mono ? MonoProto : DescProto);
        subText.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(subText);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in tail) ctl.Children.Add(c);
        Grid.SetColumn(ctl, 2);
        row.Children.Add(ctl);
        return row;
    }

    FrameworkElement AddRow(params FrameworkElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(14, 10, 14, 10) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    // ================= 控件 =================

    static TextBlock Text(string text, TextBlock proto) => new()
    {
        Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        FontFamily = proto.FontFamily, FontSize = proto.FontSize, Foreground = proto.Foreground,
    };

    SolidColorBrush Accent()
    {
        uint argb = Convert.ToUInt32(app.Glass.Current.AccentColor[1..], 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    PaneSwitch Switch(bool value, Action<bool> changed)
    {
        var s = new PaneSwitch(value, FillProto.Background, FillProto.BorderBrush, Accent());
        s.Toggled += (_, v) => changed(v);
        return s;
    }

    PaneSegmented Seg((string, string)[] options, string value, Action<string> changed)
    {
        var s = new PaneSegmented(options, value, SmallStrongProto, SmallProto, FillProto.Background, FillProto.BorderBrush) { MinWidth = options.Length > 2 ? 220 : 180 };
        s.Changed += (_, v) => changed(v);
        return s;
    }

    HotkeyBox Hotkey(Settings.Hotkey value, Settings.Hotkey other, Action<Settings.Hotkey> changed)
    {
        var box = new HotkeyBox(value, SmallProto, KbdProto, FillProto.Background, FillProto.BorderBrush, Accent())
        {
            Validate = h => h == other ? Strings.Settings_Hotkey_Duplicate
                : h.Modifiers == KeyModifiers.Win && h.VirtualKey == 'V' ? (app.WinV is null ? Strings.Settings_Hotkey_WinVOccupied : Strings.Settings_Hotkey_WinVReplacement)
                : null,
        };
        box.Changed += (_, h) => { changed(h); Later(400); };   // 宿主在后台线程重新注册，稍后刷新占用提示
        box.RecordingChanged += (_, on) => app.SetHotkeysSuspended(on);
        return box;
    }

    // 注册失败 = 被占用
    static string ConflictText(HotkeyConflict c) => string.Format(Strings.Settings_Hotkey_Occupied, (object?)(HotkeyBox.Text(c.Hotkey).Replace("Space", Strings.Common_SpaceKey)));

    // 开发和试用时 PANE_DEV_HOTKEYS 会改掉实际注册的键，照实说明
    static string? DevHotkey(string name)
    {
        var text = Environment.GetEnvironmentVariable("PANE_DEV_HOTKEYS");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var pair = text.Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == name);
        return pair == null ? null : string.Format(Strings.Settings_Hotkey_DevelopmentOverride, (object?)(pair[1]));
    }

    Border Swatch(string color, string name, bool selected)
    {
        uint argb = Convert.ToUInt32(color[1..], 16);
        var c = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        // 原型：选中时外面隔 2px 再套一圈 2px 同色环
        var ring = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(2), BorderBrush = selected ? new SolidColorBrush(c) : null, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ring.Child = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1) };
        ToolTipService.SetToolTip(ring, name);
        ring.Tapped += (_, _) => SaveGeneral(x => x with { AccentColor = color });
        return ring;
    }

    Border Chip(string text) => new()
    {
        Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = SelectedProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = SmallProto.Foreground },
    };

    FrameworkElement Status(bool ok, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new PaneIcon { Glyph = ok ? "check" : "warn", Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(ok ? Windows.UI.Color.FromArgb(255, 0x30, 0xa4, 0x6c) : Windows.UI.Color.FromArgb(255, 0xf5, 0xa5, 0x24)) });
        row.Children.Add(Text(text, SmallProto));
        return row;
    }

    Button Button(string text, string? glyph, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph != null) content.Children.Add(new PaneIcon { Glyph = glyph, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = LabelProto.Foreground });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = ButtonProto.Style, Height = PaneTokens.ControlSm, Content = content };
        b.Click += (_, _) => click();
        return b;
    }

    Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Style = IconButtonProto.Style, Content = new PaneIcon { Glyph = glyph, Width = 15, Height = 15, Foreground = IconProto.Foreground } };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // 不靠 IsEnabledChanged：元素还没进树时它不一定触发
    static void Enable(Control c, bool on) { c.IsEnabled = on; c.Opacity = on ? 1 : 0.4; }

    // ================= IPanelView =================

    public Size PreferredSize => new(900, 620);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
    public UIElement? DragRegion => Head;
}
