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
            new("general", "通用", "gear", "启动方式、外观和动效。", RenderGeneral),
            new("launcher", "启动器", "search", "呼出、搜索范围、常用入口和网页搜索。", RenderLauncher),
            new("clipboard", "剪贴板", "clipboard", "记录什么、保留多久，以及隐私保护。", RenderClipboard),
            new("gesture", "鼠标手势", "gesture", "按住右键画方向，执行对应动作。", RenderGesture),
            new("superpanel", "超级面板", "pointer", "选中文字后长按鼠标侧键呼出，就地翻译、总结；短按侧键照常后退 / 前进。", RenderSuper),
            new("ai", "AI", "ai", "翻译、提问和快捷动作用的 AI 服务。", RenderAi),
            new("icons", "图标", "app", "应用图标的遮罩规则和逐个应用的设置；文件图标用 Pane 风格还是系统的。", RenderIcons),
            new("about", "关于", "info", "版本、数据位置和开源组件。", RenderAbout),
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
        Group("启动",
            AutostartRow(),
            Row("在任务栏通知区域显示图标", "关掉后仍可用热键打开设置",
                Switch(g.ShowTrayIcon, v => SaveGeneral(x => x with { ShowTrayIcon = v }))));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (color, name) in new[] { ("#FF5874C9", "雾蓝"), ("#FF17958B", "青碧"), ("#FF5F8F4E", "苔绿"), ("#FF4A505C", "石墨") })
            swatches.Children.Add(Swatch(color, name, string.Equals(g.AccentColor, color, StringComparison.OrdinalIgnoreCase)));
        Group("外观",
            Row("主题", null, Seg([("System", "跟随系统"), ("Light", "浅色"), ("Dark", "深色")], g.Theme.ToString(),
                v => SaveGeneral(x => x with { Theme = Enum.Parse<ThemeMode>(v) }))),
            Row("主题色", "只用在开关、滑块、焦点等控件上，不给文字上色", swatches),
            Row("窗口圆角", "玻璃面板的圆角（自绘，带抗锯齿）", Seg([("8", "小 8px"), ("20", "大圆角 20px")], g.PanelRadius <= 8 ? "8" : "20",
                v => SaveGeneral(x => x with { PanelRadius = double.Parse(v) }))),
            Row("玻璃浓度", "越高越不透，背后的壁纸越淡（就是下面高级参数里的「着色浓度」）",
                GlassSlider(0, 1, 0.01, p => p.TintOpacity, (p, v) => p with { TintOpacity = v }, null)),
            Row("动效", "「减弱」会缩短过渡", Seg([("normal", "标准"), ("reduced", "减弱")], g.MotionSpeed < 1 ? "reduced" : "normal",
                v => SaveGeneral(x => x with { MotionSpeed = v == "reduced" ? 0.5 : 1 }))));
        RenderFonts(g);
        RenderGlassAdvanced();
    }

    // 界面字体（2026-10-30）：从本机字体里选，能写中文的排前面；每个字体用它自己显示一行预览。换了字体重启 Pane 后生效
    IReadOnlyList<FontFamilyInfo>? fonts;
    string fontQuery = "";
    bool fontsLoading, fontsOpen;
    void RenderFonts(GeneralSettings g)
    {
        var chosen = g.UiFont;
        var shown = chosen ?? $"默认（{App.DefaultUiFont}）";
        bool pending = chosen != app.AppliedUiFont;
        void ToggleFonts()
        {
            if (fontsOpen) Collapse("fonts", () => { fontsOpen = false; Render(); });
            else { fontsOpen = true; revealKey = "fonts"; Render(); }
        }
        var toggle = Button(fontsOpen ? "收起" : "选择字体", fontsOpen ? "chev-d" : "chev-r", ToggleFonts);
        designToggles["fonts"] = ToggleFonts;
        var rows = new List<FrameworkElement>
        {
            pending
                ? Row("界面字体", "已保存，重启 Pane 后生效", Chip(shown), Button("立即重启", "refresh", app.Restart), toggle)
                : Row("界面字体", "所有面板和窗口的文字；没装或缺字时用微软雅黑补上", Chip(shown), toggle),
        };
        if (fontsOpen)
        {
            var more = new List<FrameworkElement>();
            if (app.Fonts is null)
            {
                // 读不到本机字体列表时（024 之前的正式版）：手填字族名
                var box = Field("字族名，如 HarmonyOS Sans SC", 260);
                box.Text = chosen ?? "";
                void Commit() { var v = box.Text.Trim(); SaveGeneral(x => x with { UiFont = v.Length == 0 ? null : v }); Render(); }
                box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
                more.Add(Row("字族名", "空着就用默认；回车保存", Wrap(box), Button("保存", "check", Commit)));
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
                more.Add(Row("正在读取本机字体…", null));
            }
            else
            {
                var search = Field("搜索字体", 220);
                search.Text = fontQuery;
                var list = new StackPanel();
                void Fill()
                {
                    list.Children.Clear();
                    var q = search.Text.Trim();
                    list.Children.Add(FontRow(null, $"默认（{App.DefaultUiFont}，没装时用微软雅黑）", "Pane 启动器 · 剪贴板 · 鼠标手势", chosen is null));
                    var hits = fonts.Where(f => q.Length == 0 || f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.HasChinese).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(40).ToList();
                    foreach (var f in hits)
                        list.Children.Add(FontRow(f.Name, f.DisplayName, f.HasChinese ? "Pane 启动器 · 剪贴板 · 鼠标手势 Aa" : "Pane Launcher Aa（不含汉字，中文会用别的字体补）", f.Name == chosen));
                    if (hits.Count == 0) list.Children.Add(Row($"没有找到「{q}」", null));
                }
                search.TextChanged += (_, _) => { fontQuery = search.Text; Fill(); };
                Fill();
                more.Add(Row("本机字体", $"共 {fonts.Count} 款，能写中文的排在前面；最多列 40 个，可以搜索", Wrap(search)));
                more.Add(list);
            }
            rows.Add(Reveal("fonts", more));
        }
        Group("字体", rows.ToArray(), "MiSans 不随 Pane 分发；想用的话到小米官网下载安装，再在这里选。");

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
        var header = Row("玻璃参数（高级）", glassOpen ? $"现在调的是{(app.Glass.Dark ? "深色" : "浅色")}这一套（跟随当前主题）；调好后一般不用再动" : "着色、亮度、饱和度、高光边、阴影、加强模糊", chevron);
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
                GlassRow("着色浓度", "玻璃本身的颜色有多浓", 0, 1, 0.01, p => p.TintOpacity, (p, v) => p with { TintOpacity = v }, d.TintOpacity),
                GlassRow("亮度层", "深色压暗、浅色提亮的一层", 0, 1, 0.01, p => p.LuminosityOpacity, (p, v) => p with { LuminosityOpacity = v }, d.LuminosityOpacity),
                GlassRow("饱和度", "背后颜色透过来的鲜艳程度", 1, 2, 0.05, p => p.Saturation, (p, v) => p with { Saturation = v }, d.Saturation),
                GlassRow("高光边强度", "边缘的细高光和外描边", 0, 1, 0.01, p => p.Highlight, (p, v) => p with { Highlight = v }, d.Highlight),
                GlassRow("阴影强度", "面板下面的柔和阴影", 0, 1, 0.01, p => p.Shadow, (p, v) => p with { Shadow = v }, d.Shadow),
                GlassRow("加强模糊", "在系统模糊上再加一层，0 = 只用系统模糊；越大越占显卡", 0, 40, 1, p => p.ExtraBlur, (p, v) => p with { ExtraBlur = v }, d.ExtraBlur),
                Row("", null, Button("全部恢复默认", "refresh", () => SaveGlass(_ => d))),
            ]));
        }
        Group(null, rows.ToArray());
    }

    FrameworkElement GlassRow(string label, string desc, double min, double max, double step,
        Func<GlassThemeParameters, double> get, Func<GlassThemeParameters, double, GlassThemeParameters> set, double fallback)
    {
        var value = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = MonoProto.FontFamily, FontSize = 12, Foreground = SmallProto.Foreground };
        var slider = GlassSlider(min, max, step, get, set, value);
        var reset = IconButton("refresh", "恢复默认", () => SaveGlass(p => set(p, fallback)));
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
        const string label = "开机时启动 Pane";
        var auto = app.Autostart;
        var state = auto.State;
        var toggle = Switch(state is AutostartState.On or AutostartState.DisabledInTaskManager, v =>
        {
            try { auto.Set(v); autostartError = null; }
            catch (Exception e) { autostartError = App.FailureReason(e) ?? "没有改成功"; }
            Later(0);
        });
        if (state == AutostartState.Unavailable)
        {
            Enable(toggle, false);
            return Row(label, (auto.UnavailableReason ?? "现在不能注册") + "；装成正式版后可以打开", toggle);
        }
        if (autostartError != null) return Row(label, null, Status(false, "没有改成功：" + autostartError), toggle);
        if (state == AutostartState.DisabledInTaskManager)
            return Row(label, "Pane 不会替你改回来；要开机启动，请在任务管理器的「启动应用」里重新启用", Status(false, "已在任务管理器里禁用"), toggle);
        return Row(label, "登录 Windows 后在后台启动，只有托盘图标；按热键直接呼出", toggle);
    }

    // ================= 启动器 =================

    void RenderLauncher()
    {
        var s = app.Store.Current;
        var conflict = app.HotkeyConflicts.FirstOrDefault(c => c.Target == "launcher");
        var hotkey = Hotkey(s.Launcher.Hotkey, s.Clipboard.Hotkey, h => app.Store.Save(app.Store.Current with { Launcher = app.Store.Current.Launcher with { Hotkey = h } }));
        string? hotkeyDesc = conflict != null ? null : DevHotkey("launcher");
        var hotkeyRow = conflict != null
            ? Row("呼出热键", "换一个组合，或者在占用它的程序里改掉", Status(false, ConflictText(conflict)), hotkey)
            : Row("呼出热键", hotkeyDesc, hotkey);
        bool moved = s.Launcher.Position != null;
        var l = s.Launcher;
        var reset = Button("恢复默认位置", "refresh", () => { SaveLauncher(x => x with { Position = null }); Render(); });
        Enable(reset, moved);
        Group("呼出",
            hotkeyRow,
            Row("位置", moved ? "已记住拖动后的位置。双击启动器搜索行的空白处也能恢复" : "在鼠标所在屏幕的上部居中；拖动搜索行空白处可以挪开，会记住位置", reset),
            Row("失去焦点时隐藏", "点到别的窗口时自动收起", Switch(l.HideOnBlur, v => SaveLauncher(x => x with { HideOnBlur = v }))),
            Row("再次呼出时", "上次输入的文字怎么处理",
                Seg([("Clear", "清空"), ("OneMinute", "1 分钟内保留"), ("Always", "一直保留")], l.KeepInput.ToString(),
                    v => SaveLauncher(x => x with { KeepInput = Enum.Parse<KeepInputMode>(v) }))),
            Row("没输入时显示", l.Home == LauncherHome.Nothing ? "呼出时只有一行搜索栏" : "最近打开过的应用和固定的应用",
                Seg([("RecentAndPinned", "最近和固定"), ("PinnedOnly", "只显示固定"), ("Nothing", "都不显示")], l.Home.ToString(),
                    v => { SaveLauncher(x => x with { Home = Enum.Parse<LauncherHome>(v) }); Later(); })),
            Row("复制后自动带入", "复制后这么久内呼出，刚复制的内容作为附件带入；不会清空剪贴板",
                Seg([("0", "关闭"), ("1", "1 秒"), ("3", "3 秒"), ("5", "5 秒")], l.AutoAttachSeconds.ToString(),
                    v => SaveLauncher(x => x with { AutoAttachSeconds = int.Parse(v) }))));

        // 演示模式没有真实服务（FileSearchStatus 为 null），状态用假数据
        var fs = app.FileSearchStatus;
        // 状态对象要检测过一次才准（默认是「没有运行」）：每次打开设置窗口、第一次进这个分区时检测一次，结果通过 Changed 刷新
        if (fs != null && !everythingChecked) { everythingChecked = true; _ = fs.RefreshAsync(CancellationToken.None); }
        bool ready = fs == null ? SettingsSample.EverythingRunning : fs.State == FileSearchState.Ready;
        string status = fs == null ? (ready ? "已连接 Everything 1.4" : "Everything 没有运行")
            : fs.State switch
            {
                FileSearchState.Ready => $"已连接 Everything {fs.Version}",
                FileSearchState.Unsupported => "Everything 版本太老或连不上",
                _ => "Everything 没有运行，文件搜索暂不可用",
            };
        var check = Button("重新检测", "refresh", async () =>
        {
            if (fs == null) { SettingsSample.EverythingRunning = !SettingsSample.EverythingRunning; Render(); return; }
            try { await fs.RefreshAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            Render();
        });
        Group("文件搜索",
            // 2026-10-29 起启动器固定列前 3 个文件，其余在文件窗口里看，「每次显示的文件结果」一行去掉
            Row("Everything", "文件名搜索依赖 Everything；Pane 不自建全盘索引。启动器列前 3 个，其余在文件窗口里看", Status(ready, status), check));
        RenderActionRow(l);

        var favorites = l.Favorites.Select(f => ListRow(f.IsFolder ? "folder" : "doc", FavoriteTitle(f.Path), f.Path, false,
            IconButton("close", "移除", () => { SaveLauncher(x => x with { Favorites = x.Favorites.Where(e => e != f).ToArray() }); Render(); }))).ToList();
        favorites.Add(AddRow(
            Button("添加文件", "plus", async () => { if (await PickFavorite(false) is { } e) AddFavorite(e); }),
            Button("添加文件夹", "folder-plus", async () => { if (await PickFavorite(true) is { } e) AddFavorite(e); })));
        Group("常用文件和文件夹", favorites.ToArray(), "常用的文件和文件夹在启动器里排在普通文件前面，也能用拼音搜到。");

        var engines = l.SearchEngines.Select(e =>
        {
            bool def = e.Keyword == l.DefaultEngine;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(Text(e.Name, LabelProto));
            label.Children.Add(Chip(e.Keyword));
            if (def) label.Children.Add(Chip("默认"));
            var tail = new List<FrameworkElement>();
            if (!def) tail.Add(Button("设为默认", null, () => { SaveLauncher(x => x with { DefaultEngine = e.Keyword }); Render(); }));
            var del = IconButton("close", def ? "默认站点不能删" : "删除", () =>
            {
                if (e.Keyword == app.Store.Current.Launcher.DefaultEngine) return;
                SaveLauncher(x => x with { SearchEngines = x.SearchEngines.Where(v => v != e).ToArray() });
                Render();
            });
            Enable(del, !def);
            tail.Add(del);
            return ListRow("globe", label, e.UrlTemplate, true, tail.ToArray());
        }).ToList();
        engines.Add(addingEngine ? Reveal("engine", [EngineForm()]) : AddRow(Button("添加搜索站点", "plus", () => { addingEngine = true; revealKey = "engine"; Render(); })));
        engines.Insert(0, Row("搜索建议", "用关键字搜网页时（比如「bd 天气」）向那个站点要搜索建议；平时打字不联网",
            Switch(l.WebSuggestions, v => SaveLauncher(x => x with { WebSuggestions = v }))));
        Group("网页搜索", engines.ToArray(), "地址里用 {query} 代表搜索词；在启动器输入「关键字 空格 搜索词」直接用这个站点搜。");

        // 终端命令（037）：> 开头在终端里运行；记住最近 20 条用来补全
        var clearHistory = Button("清空", "trash", () => { app.TerminalHistory?.Clear(); terminalCleared = true; Render(); });
        Enable(clearHistory, !terminalCleared);
        Group("终端命令", [Row("记住运行过的命令", terminalCleared ? "已清空" : "最近 20 条，用来补全；以空格开头的命令不记",
                Switch(l.RememberCommands, v => SaveLauncher(x => x with { RememberCommands = v })), clearHistory)],
            "在启动器里输入 > 加命令：Enter 在终端里运行，Ctrl+Shift+Enter 以管理员身份运行。");
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
        var name = Field("名称", 96);
        var key = Field("关键字", 72);
        var url = Field("地址，用 {query} 代表搜索词", 260);
        var error = Text("", DescProto);
        var save = Button("保存", "check", () =>
        {
            var current = app.Store.Current.Launcher;
            string n = name.Text.Trim(), k = key.Text.Trim(), u = url.Text.Trim();
            string? problem = n.Length == 0 || k.Length == 0 || u.Length == 0 ? "三项都要填"
                : k.Contains(' ') ? "关键字不能有空格"
                : current.SearchEngines.Any(e => string.Equals(e.Keyword, k, StringComparison.OrdinalIgnoreCase)) ? $"关键字「{k}」已经有了"
                : !u.Contains("{query}") ? "地址里要有 {query}"
                : !(u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ? "地址要以 http:// 或 https:// 开头"
                : null;
            if (problem != null) { error.Text = problem; return; }
            SaveLauncher(x => x with { SearchEngines = [.. x.SearchEngines, new SearchEngine(n, k, u)] });
            addingEngine = false;
            Render();
        });
        var cancel = Button("取消", null, () => Collapse("engine", () => { addingEngine = false; Render(); }));
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
        Group("基本", Row("启用剪贴板", c.Enabled
                ? (winvOn ? "先关掉下面的「用 Pane 替换 Win+V」，才能关剪贴板" : "已经有别的剪贴板工具时可以关掉")
                : "已关闭：不监听复制、不记录，呼出热键也不占用；以前的记录还在",
            master));
        if (!c.Enabled) { RenderClipboardData(); return; }
        var conflict = app.HotkeyConflicts.FirstOrDefault(x => x.Target == "clipboard");
        var hotkey = Hotkey(c.Hotkey, s.Launcher.Hotkey, h => SaveClipboard(x => x with { Hotkey = h }));
        var winv = app.WinV;
        var call = new List<FrameworkElement>
        {
            conflict != null ? Row("呼出热键", "换一个组合，或者在占用它的程序里改掉", Status(false, ConflictText(conflict)), hotkey)
                             : Row("呼出热键", DevHotkey("clipboard") ?? (winv?.State == WinVState.On ? "Win+V 也会打开 Pane 的剪贴板" : null), hotkey),
        };
        if (winv != null) call.AddRange(WinVRows(winv));
        Group("呼出", call.ToArray());

        // 三种类型至少留一种（设置模型要求）：只剩一种时点它不响应，说明里写清楚
        int kinds = (c.RecordText ? 1 : 0) + (c.RecordImages ? 1 : 0) + (c.RecordFiles ? 1 : 0);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(ToggleChip("文本", "text", c.RecordText, kinds == 1, v => SaveClipboard(x => x with { RecordText = v })));
        chips.Children.Add(ToggleChip("图片", "image", c.RecordImages, kinds == 1, v => SaveClipboard(x => x with { RecordImages = v })));
        chips.Children.Add(ToggleChip("文件", "folder", c.RecordFiles, kinds == 1, v => SaveClipboard(x => x with { RecordFiles = v })));
        Group("记录",
            Row("记录哪些内容", kinds == 1 ? "至少记录一种" : "文本包括链接、颜色和代码", chips),
            Row("最多保留", "已固定的条目不计入，也不会被自动清理",
                Seg([("200", "200 条"), ("500", "500 条"), ("1000", "1000 条"), ("5000", "5000 条")], c.Limit.ToString(),
                    v => SaveClipboard(x => x with { Limit = int.Parse(v) }))),
            Row("连续重复只留一条", "再复制一遍同样的内容，只把原来那条的时间更新", Switch(c.Dedupe, v => SaveClipboard(x => x with { Dedupe = v }))),
            Row("粘贴后移到最前", "下次打开剪贴板时生效；面板开着时条目不挪位置", Switch(c.MoveToTopAfterPaste, v => SaveClipboard(x => x with { MoveToTopAfterPaste = v }))));

        var excluded = c.ExcludedPrograms.Select(name => ListRow("shield", name, "从这个程序复制的内容不记录", false,
            IconButton("close", "移除", () => { SaveClipboard(x => x with { ExcludedPrograms = x.ExcludedPrograms.Where(v => !string.Equals(v, name, StringComparison.OrdinalIgnoreCase)).ToArray() }); Render(); }))).ToList();
        excluded.Insert(0, Row("遵守「不进历史」标记", "密码管理器等程序复制的内容如果带这个标记，就不记录", Switch(c.HonorExcludeMarker, v => SaveClipboard(x => x with { HonorExcludeMarker = v }))));
        excluded.Add(AddRow(Button("添加排除的程序", "plus", async () =>
        {
            // 服务按程序文件名匹配（不区分大小写），只存文件名
            if (await PickProgram() is not { } exe) return;
            var name = Path.GetFileName(exe);
            SaveClipboard(x => x with { ExcludedPrograms = x.ExcludedPrograms.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) ? x.ExcludedPrograms : [.. x.ExcludedPrograms, name] });
            Render();
        })));
        Group("隐私", excluded.ToArray());
        RenderClipboardData();
    }

    void RenderClipboardData()
    {
        // 清空要点两次：第一次按钮变成「再点一次清空」，3 秒内不点就复原（同启动器里关机 / 重启的确认）
        var clear = Button(clearArmed ? "再点一次清空" : "清空", "trash", () =>
        {
            if (!clearArmed) { clearArmed = true; Render(); _ = DisarmClear(); return; }
            clearArmed = false;
            app.ClipboardHistory.ClearUnpinned();
            clearedAt = DateTime.Now;
            Render();
        });
        var cleared = (DateTime.Now - clearedAt).TotalSeconds < 4;
        Group("数据",
            cleared ? Row("清空剪贴板历史", null, Status(true, "已清空，固定的条目保留了"), clear)
                    : Row("清空剪贴板历史", clearArmed ? "未固定的记录会全部删除，不能撤销" : "已固定的条目会保留", clear));
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
        const string label = "用 Pane 替换 Win+V";
        if (winvBusy)
        {
            yield return Row(label, null, Status(true, "正在处理…（重启资源管理器时任务栏会闪一下）"));
            yield break;
        }
        if (winvConfirm)
        {
            // 说明一行占满宽度，问题和按钮另起一行（挤在一行时说明被压成很窄的一列）
            yield return Row(replaced ? "恢复系统的 Win+V" : label, replaced
                ? "把系统设置原样恢复，Win+V 回到 Windows 自带的剪贴板历史。"
                : "Pane 会在当前用户的注册表里改一项，让资源管理器不再占用 Win+V（Explorer\\Advanced 的 DisabledHotkeys 加上 V）。不需要管理员权限；改之前记下原值，关掉时原样恢复。");
            yield return Row("现在重启资源管理器吗？", "重启才生效，任务栏和打开的文件夹会闪一下；不重启就等下次登录",
                Button("取消", null, () => { winvConfirm = false; Render(); }),
                Button("下次登录", null, () => Run(t => replaced ? w.DisableAsync(false, t) : w.EnableAsync(false, t))),
                Button("现在重启", "refresh", () => Run(t => replaced ? w.DisableAsync(true, t) : w.EnableAsync(true, t))));
            yield break;
        }
        // 关闭 Pending（资源管理器还没放开 Win+V）不用重启，直接恢复；其余先就地确认
        var toggle = Switch(replaced, _ => { if (state == WinVState.Pending) Run(t => w.DisableAsync(false, t)); else { winvConfirm = true; Render(); } });
        FrameworkElement? status = w.Error is { } err ? Status(false, err) : state switch
        {
            WinVState.On => Status(true, "已生效"),
            WinVState.Pending => Status(false, "下次登录生效"),
            WinVState.RestorePending => Status(false, "下次登录恢复"),
            _ => null,
        };
        var now = state is WinVState.Pending or WinVState.RestorePending
            ? Button("重启资源管理器", "refresh", () => Run(t => state == WinVState.Pending ? w.EnableAsync(true, t) : w.DisableAsync(true, t)))
            : null;
        yield return Row(label, "Win+V 交给 Pane；关掉时把系统设置原样恢复",
            new[] { status, now, toggle }.OfType<FrameworkElement>());
        var history = Switch(w.SystemHistoryDisabled, v => Run(t => w.SetSystemHistoryDisabledAsync(v, t)));
        Enable(history, replaced);
        yield return Row("同时关闭 Windows 自带的剪贴板历史", replaced ? "避免同一份内容在两个地方各存一份；关闭替换时一起恢复" : "先打开上面的替换", history);
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
            Group(null, Row("手势配置读不出来", "gestures.json 可能损坏了，可以从 WGestures 重新导入", Status(false, "配置无效")));
            RenderImport();
            return;
        }
        var st = config.Settings;
        bool on = st.EnabledButtons != G.GestureButton.None;

        var state = app.GestureState switch
        {
            G.GestureServiceState.WGesturesRunning => "WGestures 正在运行，Pane 的手势先不启用；关掉 WGestures 后重启 Pane",
            G.GestureServiceState.HookFailed => "鼠标钩子没装上，重启 Pane 再试",
            G.GestureServiceState.Paused => "已暂停（用暂停热键或手势命令恢复）",
            _ => null,
        };

        int buttons = System.Numerics.BitOperations.PopCount((uint)st.EnabledButtons);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (b, name) in new[] { (G.GestureButton.Right, "右键"), (G.GestureButton.Middle, "中键"), (G.GestureButton.X1, "后退键"), (G.GestureButton.X2, "前进键") })
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
        if (state != null) rows.Add(Row("状态", null, Status(false, state)));
        rows.Add(Row("启用鼠标手势", null, Switch(on, v =>
        {
            if (!v) gestureButtonsBeforeOff = app.GestureConfig.Settings.EnabledButtons;
            SaveGestureSettings(x => x with { EnabledButtons = v ? (gestureButtonsBeforeOff == G.GestureButton.None ? G.GestureButton.Right : gestureButtonsBeforeOff) : G.GestureButton.None }, render: true, delay: true);
        })));
        rows.Add(Row("用哪些键画", "按住拖动画方向；不移动就松开，照常是原来的点击", chips));
        rows.Add(Row("显示轨迹", null, trail));
        rows.Add(Row("轨迹淡出", "松开后轨迹慢慢消失；关掉则立即消失", fade));
        rows.Add(Row("显示手势提示", "在屏幕中间显示方向和将要执行的动作", hint));
        rows.Add(Row("全屏程序里暂停", "玩游戏、看视频时不触发", fullscreen));
        rows.Add(PauseHotkeyRow(st.PauseHotkey));
        if (gestureError != null) rows.Add(Row("没有保存", null, Status(false, gestureError)));
        Group("基本", rows.ToArray());

        // 编辑中的那一行换成编辑器；新加的编辑器放在最后
        var list = config.Global.Select((g, i) => draft is { App: < 0 } d && d.Index == i ? Reveal("gesture", [GestureEditor(d, config)])
            : ListRow(Arrows(g), Text(g.Name, LabelProto), GestureSub(g), false,
                IconButton("chev-r", "编辑", () => EditGesture(i)),
                IconButton("close", "删除这个手势", () => { draft = null; SaveGestures(x => x with { Global = x.Global.Where((_, j) => j != i).ToArray() }); }))).ToList();
        if (draft is { App: < 0, Index: < 0 } fresh) list.Add(Reveal("gesture", [GestureEditor(fresh, config)]));
        else if (draft is not { App: < 0 }) list.Add(AddRow(Button("添加手势", "plus", () => EditGesture(-1))));
        Group($"手势（{config.Global.Count}）", list.ToArray(),
            "八个方向，建议不超过 4 段。同一个键、同样的方向和附加动作只能对应一个手势；按程序设置里的专用手势优先。");

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
            var sub = a.Mode == G.AppRuleMode.Disabled ? "这个程序里不触发手势"
                : a.Bindings.Count > 0 ? $"{a.Bindings.Count} 个专用手势，其余沿用全局" : "沿用全局手势";
            var mode = Seg([("on", "启用"), ("off", "禁用")], a.Mode == G.AppRuleMode.Disabled ? "off" : "on",
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
                ToolTipService.SetToolTip(expand, open ? "收起" : "专用手势");
                void ToggleApp()
                {
                    if (open) { SpinChevron(chevron, 0); Collapse("app", () => { openApp = -1; if (draft is { App: >= 0 }) draft = null; Render(); }); }
                    else { openApp = at; if (draft is { App: >= 0 }) draft = null; revealKey = "app"; Render(); }
                }
                expand.Click += (_, _) => ToggleApp();
                if (!designToggles.ContainsKey("app") || open) designToggles["app"] = ToggleApp;   // 演示：第一个启用的程序，展开后换成它自己的收起
                tail.Add(expand);
            }
            tail.Add(IconButton("close", "移除", () =>
            {
                openApp = -1;
                if (draft is { App: >= 0 }) draft = null;
                SaveGestures(x => x with { Apps = x.Apps.Where((_, j) => j != at).ToArray() });
            }));
            rows.Add(ListRow("app", name, sub, false, tail.ToArray()));
            if (open) rows.Add(Reveal("app", AppBindingRows(config, at)));
        }
        rows.Add(AddRow(Button("添加程序", "plus", async () =>
        {
            if (await PickProgram() is not { } exe) return;
            SaveGestures(x => x.Apps.Any(r => string.Equals(r.ExePath, exe, StringComparison.OrdinalIgnoreCase) && r.WindowClass == null) ? x
                : x with { Apps = [.. x.Apps, new G.AppRule(exe, null, G.AppRuleMode.Disabled, [])] });
        })));
        Group("按程序设置", rows.ToArray(), "新加的程序默认禁用手势；改成「启用」就沿用全局手势，再点右边的箭头可以加这个程序专用的手势。");
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
            var sub = GestureSub(b) + (covered != null ? $" · 代替全局的「{covered.Name}」" : "");
            var row = ListRow(Arrows(b), Text(b.Name, LabelProto), sub, false,
                IconButton("chev-r", "编辑", () => EditGesture(at, appIndex)),
                IconButton("close", "删除这个专用手势", () =>
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
            var add = AddRow(Button("添加专用手势", "plus", () => EditGesture(-1, appIndex)));
            add.Margin = new Thickness(28, 0, 0, 0);
            rows.Add(add);
        }
        return rows;
    }

    // 暂停热键可以不设；设了之后能改、能清除
    FrameworkElement PauseHotkeyRow(G.KeyChord? chord)
    {
        const string label = "暂停 / 恢复热键";
        if (chord is not { } k)
            return Row(label, "没有设置", Button("设置", "plus", () => SaveGestureSettings(x => x with { PauseHotkey = new G.KeyChord(G.KeyModifiers.Ctrl | G.KeyModifiers.Alt, 'G') }, render: true)));
        var box = Hotkey(new Settings.Hotkey((KeyModifiers)(int)k.Modifiers, k.VirtualKey), app.Store.Current.Launcher.Hotkey,
            h => SaveGestureSettings(x => x with { PauseHotkey = new G.KeyChord((G.KeyModifiers)(int)h.Modifiers, h.VirtualKey) }));
        return Row(label, null, box, IconButton("close", "清除", () => SaveGestureSettings(x => x with { PauseHotkey = null }, render: true)));
    }

    // 方向箭头列：定宽，各行的名字对齐（最多 4 段，WGestures 导入的老手势可能更长，超出就挤）
    FrameworkElement Arrows(G.GestureBinding g)
    {
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Width = 92 };
        foreach (var d in g.Pattern.Directions)
            arrows.Children.Add(new PaneIcon { Glyph = ArrowGlyphs[(int)d], Width = 16, Height = 16, StrokeWidth = 2.2, Foreground = LabelProto.Foreground });
        if (g.Pattern.Directions.Count == 0) arrows.Children.Add(Text("不画方向", DescProto));
        return arrows;
    }

    static string GestureSub(G.GestureBinding g)
    {
        var parts = new List<string>();
        if (g.Pattern.Button != G.GestureButton.Right)
            parts.Add(g.Pattern.Button switch { G.GestureButton.Middle => "中键", G.GestureButton.X1 => "后退键", G.GestureButton.X2 => "前进键", _ => g.Pattern.Button.ToString() });
        if (g.Pattern.Modifier != G.GestureModifier.None)
            parts.Add(g.Pattern.Modifier switch
            {
                G.GestureModifier.WheelUp => "按住时滚轮向上", G.GestureModifier.WheelDown => "按住时滚轮向下",
                G.GestureModifier.LeftClick => "按住时点左键", G.GestureModifier.MiddleClick => "按住时点中键",
                G.GestureModifier.X1Click => "按住时点后退键", G.GestureModifier.X2Click => "按住时点前进键", _ => "",
            });
        parts.Add(CommandText(g.Command));
        return string.Join(" · ", parts);
    }

    static string CommandText(G.GestureCommand c) => c switch
    {
        G.HotkeyCommand h => "快捷键 " + string.Join(" ", h.Chords.Select(k => HotkeyBox.Text(new Settings.Hotkey((KeyModifiers)(int)k.Modifiers, k.VirtualKey)))),
        G.SendTextCommand => "输入文字",
        G.WebSearchCommand => "网页搜索选中文字",
        G.RunCommand => "运行命令",
        G.WindowCommand w => "窗口 · " + w.Action switch
        {
            G.WindowAction.MaximizeRestore => "最大化 / 还原", G.WindowAction.Minimize => "最小化", G.WindowAction.Close => "关闭",
            G.WindowAction.ToggleTopmost => "置顶 / 取消置顶", G.WindowAction.SnapLeft => "贴到左半边", _ => "贴到右半边",
        },
        G.VolumeCommand v => v.Action switch
        {
            G.VolumeAction.Up => "音量加", G.VolumeAction.Down => "音量减", G.VolumeAction.ToggleMute => "静音 / 取消静音", _ => "滚轮调音量",
        },
        G.TaskSwitchCommand => "切换窗口",
        G.OpenCommand o => "打开 " + (Path.GetFileName(o.Target) is { Length: > 0 } f ? f : o.Target),
        G.PauseGesturesCommand => "暂停手势",
        _ => "什么都不做",
    };

    IReadOnlyList<string> importSearched = [];
    // 从 WGestures 导入：先预览（只读它的配置文件），确认后整套替换
    void RenderImport()
    {
        FrameworkElement row;
        if (importPreview is { } p)
        {
            var desc = $"找到 WGestures {p.SourceVersion} 的配置：{p.GlobalCount} 个手势、{p.AppRuleCount} 条程序规则、{p.DisabledAppCount} 个禁用程序"
                + (p.Unsupported.Count > 0 ? $"；{p.Unsupported.Count} 项不支持，会跳过" : "") + "。导入会替换 Pane 现在的手势和程序设置";
            row = Row("从 WGestures 导入", desc,
                Button("取消", null, () => { importPreview = null; Render(); }),
                Button("导入", "check", () =>
                {
                    try { app.ImportWGestures(p); importStatus = $"已导入 {p.GlobalCount} 个手势"; importOk = true; }
                    catch (Exception e) { importStatus = "导入失败：" + e.Message; importOk = false; }
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
                    if (importPreview == null) { importStatus = "没找到 WGestures 的配置"; importOk = false; importSearched = app.WGesturesSearched; }
                    else { importStatus = null; importSearched = []; }
                }
                catch (Exception e) { importStatus = "读不出 WGestures 的配置：" + e.Message; importOk = false; importSearched = []; }
                Render();
            }
            var find = Button("查找配置", "search", () => Load(null));
            // 找不到时手动选（043）：WGestures 数据目录里的 gestures.wg2 / gestures.wg，或在 WGestures 里「导出」的 .wgb
            var pick = Button("选择文件…", "folder", async () =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    foreach (var ext in new[] { ".wg2", ".wg", ".wgb" }) picker.FileTypeFilter.Add(ext);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                    if (await picker.PickSingleFileAsync() is { } file) Load(file.Path);
                }
                catch (Exception e) { importStatus = "选不了文件：" + (App.FailureReason(e) ?? e.Message); importOk = false; Render(); }
            });
            row = importStatus != null
                ? Row("从 WGestures 导入", null, Status(importOk, importStatus), find, pick)
                : Row("从 WGestures 导入", "读取 WGestures 的手势、程序规则和轨迹颜色；不会改动 WGestures 本身", find, pick);
            if (importSearched.Count > 0)
                row = new StackPanel { Children = { row, Row("找过这些位置", string.Join("\n", importSearched.Take(6)) + (importSearched.Count > 6 ? "\n…" : "")
                    + "\n在 WGestures 里用「导出」存一个 .wgb 文件，再点「选择文件…」也可以") } };
        }
        // 恢复默认：点一下先问一句，再点「恢复」才替换
        var reset = resetConfirm
            ? Row("恢复默认手势", "会替换现在的全部手势和程序设置，换成 Pane 自带的那一套",
                Button("取消", null, () => { resetConfirm = false; Render(); }),
                Button("恢复", "check", () =>
                {
                    resetConfirm = false;
                    try { app.ResetGestures(); gestureError = null; }
                    catch (Exception e) { gestureError = e.Message; }
                    Render();
                }))
            : Row("恢复默认手势", "后退、前进、关闭标签页、按住右键滚轮切换标签页等", Button("恢复默认", "refresh", () => { resetConfirm = true; Render(); }));
        Group("迁移", row, reset);
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
            Row("默认使用图标遮罩", "不规则图标放到由图标颜色生成的底板上，满幅图标裁成圆角方形；关掉后全部用程序原来的图标",
                Switch(s.MaskByDefault, v => { SaveIcons(x => x with { MaskByDefault = v }); Later(); })),
        };
        if (catalog.Rebuilding) rows.Add(Row("正在重新生成图标", "在后台进行，十几秒后启动器里就是新图标", Status(true, "生成中")));
        Group("遮罩", rows.ToArray());

        // 文件图标（036，评论区建议）：默认仍是 Pane 的彩色类型图标；选「系统」和资源管理器一样。只影响文件窗口和启动器里的文件行
        Group("文件图标", Row("文件和文件夹", s.SystemFileIcons ? "和资源管理器里一样的系统图标；第一次见到的类型要等一下才出来" : "Pane 的彩色类型图标，一眼看出是文档、表格还是图片",
            Seg([("pane", "Pane 风格"), ("system", "系统")], s.SystemFileIcons ? "system" : "pane", v => { SaveIcons(x => x with { SystemFileIcons = v == "system" }); Later(); })));

        var apps = catalog.Apps().ToDictionary(a => a.Id);
        var ids = s.Overrides.Keys.Union(s.CustomIcons.Keys)
            .OrderBy(id => apps.TryGetValue(id, out var a) ? a.Name : id, StringComparer.CurrentCultureIgnoreCase).ToList();
        var list = new List<FrameworkElement>();
        foreach (var id in ids)
        {
            apps.TryGetValue(id, out var info);
            var mode = s.Overrides.GetValueOrDefault(id);
            bool custom = s.CustomIcons.ContainsKey(id);
            var sub = info == null ? "这个应用已不在列表里" : KindText(s.MaskByDefault ? mode : IconMode.Raw, info.Kind) + (custom ? " · 用的是你换的图片" : "");
            var seg = Seg([("Auto", "自动"), ("ForceMask", "强制遮罩"), ("Raw", "原样")], mode.ToString(),
                v => { SaveIcons(x => x with { Overrides = new(x.Overrides) { [id] = Enum.Parse<IconMode>(v) } }); Later(); });
            seg.MinWidth = 220;
            Enable(seg, s.MaskByDefault);
            list.Add(ListRow(AppIcon(info?.Icon), Text(info?.Name ?? id, LabelProto), sub, false, seg,
                IconButton("image", "换成自己的图片", async () => { if (await PickIcon(id)) Render(); }),
                IconButton("close", "恢复默认", () => { ResetIcon(id); Render(); })));
        }
        list.Add(iconQuery == null
            ? AddRow(Button("添加应用", "plus", () => { iconQuery = ""; Render(); }))
            : IconSearch(apps.Values, ids));
        Group("按应用设置", list.ToArray(), "自动：按图标形状决定裁成圆角方形还是放到底板上。换的图片支持 PNG、ICO、JPG、BMP，方形、256×256 以上、透明背景的 PNG 效果最好；读不出来时用程序自己的图标。");
    }

    static string KindText(IconMode mode, AppIconKind kind) => mode == IconMode.Raw ? "原样显示" : kind switch
    {
        AppIconKind.Full => "满幅，裁成圆角方形",
        AppIconKind.Plate => "放在彩色底板上",
        AppIconKind.Neutral => "放在中性底板上",
        AppIconKind.Raw => "原样显示",
        _ => "图标还没生成",
    } + (mode == IconMode.ForceMask ? "（强制遮罩）" : "");

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
        var box = Field("输入应用名称", 260);
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
            if (iconResults.Children.Count == 0) iconResults.Children.Add(Text("没有找到", DescProto));
        }
        box.TextChanged += (_, _) => { iconQuery = box.Text; Fill(); };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { iconQuery = null; Render(); e.Handled = true; } };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        Fill();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Wrap(box));
        head.Children.Add(Button("取消", null, () => { iconQuery = null; Render(); }));
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
        Group("基本", Row("启用 AI", a.Enabled ? "不用 AI 时可以关掉"
                : "已关闭：启动器和超级面板里不再出现翻译、问 AI 等 AI 动作；下面的配置都还在",
            Switch(a.Enabled, v => { SaveAi(x => x with { Disabled = !v }); Later(); })));
        if (!a.Enabled) return;
        var setup = app.AiSetup;
        bool api = a.Provider == AiProvider.Api;
        var rows = new List<FrameworkElement>
        {
            Row("服务方式", null, Seg([("Api", "第三方 API"), ("ChatGpt", "ChatGPT 账号")], a.Provider.ToString(), v =>
            {
                SaveAi(x => x with { Provider = Enum.Parse<AiProvider>(v) });
                aiModels = null; aiResult = null;
                Later(0);
            })),
        };
        if (api)
        {
            rows.Add(Row("接口地址", "兼容 OpenAI 接口格式的服务", CommitField("https://…/v1", 300, a.ApiBaseUrl, v =>
                Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? Try(() => SaveAi(x => x with { ApiBaseUrl = v })) : "接口地址要以 http:// 或 https:// 开头")));
            rows.Add(KeyRow(setup));
        }
        else rows.AddRange(AccountRows(setup));
        if (api || setup.Account != null)
        {
            rows.Add(ModelRow(a, api));
            var test = Button(aiBusy == "test" ? "测试中…" : "测试", null, () => _ = RunAi("test", async t => { await setup.TestAsync(t); return "连接正常"; }));
            Enable(test, aiBusy == null);
            rows.Add(aiResult is { } r && aiLast != "proxy" ? Row("测试连接", null, Status(r.Ok, r.Text), test) : Row("测试连接", "用现在的设置发一个很小的请求", test));
        }
        if (aiFieldError != null) rows.Add(Row("没有保存", null, Status(false, aiFieldError)));
        // 思考程度（042，用户提）：ChatGPT 的推理模型等支持；不支持的模型服务自动去掉这个参数
        rows.Add(Row("思考程度", a.Reasoning == AiReasoningEffort.Default ? "用服务的默认；只对支持思考的模型有效，不支持的自动忽略" : "越深回答越慢、越费额度；只对支持思考的模型有效，不支持的自动忽略",
            Seg([("Default", "默认"), ("Low", "低"), ("Medium", "中"), ("High", "高")], a.Reasoning.ToString(),
                v => { SaveAi(x => x with { Reasoning = Enum.Parse<AiReasoningEffort>(v) }); Later(); })));
        Group("服务", rows.ToArray());

        RenderAiActions(a);

        // 超级面板自动翻译：没配 AI 时开关灰掉（2026-10-30）
        bool configured = app.Ai.Status != AiStatus.NotConfigured;
        var autoSwitch = Switch(a.AutoTranslateSelection, v => SaveAi(x => x with { AutoTranslateSelection = v }));
        autoSwitch.IsEnabled = configured;
        autoSwitch.Opacity = configured ? 1 : 0.4;
        Group("翻译",
            Row("目标语言", a.TranslateTarget == "auto" ? "原文以中文为主就译成英文，否则译成中文" : null,
                Seg([("auto", "自动"), ("zh", "中文"), ("en", "英文"), ("ja", "日文")], a.TranslateTarget,
                    v => SaveAi(x => x with { TranslateTarget = v }))),
            TranslationModelRow(a),
            Row("超级面板选中外文自动翻译", configured ? "选中外文、超级面板一弹出就翻译，单个单词也翻；超过约 2000 字不自动翻；中文照旧只显示动作" : "先在上面配置好 AI 服务", autoSwitch));
        // 代理（042）：跟随系统 / 不走代理 / 自定义（http、socks5）；选「自定义」后填了地址才算数
        var mode = proxyCustomPending ? AiProxyMode.Custom : a.Proxy;
        var network = new List<FrameworkElement>
        {
            Row("代理", mode switch
                {
                    AiProxyMode.None => "直接连接，不管系统有没有设代理",
                    AiProxyMode.Custom => "用下面这个代理；支持 http 和 socks5",
                    _ => "和浏览器一样，用系统设置里的代理",
                },
                Seg([("System", "跟随系统"), ("None", "不走代理"), ("Custom", "自定义")], mode.ToString(), v =>
                {
                    var m = Enum.Parse<AiProxyMode>(v);
                    proxyCustomPending = m == AiProxyMode.Custom && a.ProxyUrl == null;
                    if (!proxyCustomPending) SaveAi(x => x with { ProxyMode = m });
                    if (aiLast == "proxy") aiResult = null;
                    Later();
                })),
        };
        if (mode == AiProxyMode.Custom)
            network.Add(Row("代理地址", "比如 http://127.0.0.1:7890 或 socks5://127.0.0.1:7891",
                CommitField("http://127.0.0.1:7890", 260, a.ProxyUrl ?? "", v =>
                {
                    if (v.Length == 0) return "填上代理地址，或者换回「跟随系统」";
                    if (!AiSettings.ValidProxy(v)) return "代理地址要以 http://、https:// 或 socks5:// 开头";
                    var error = Try(() => SaveAi(x => x with { ProxyUrl = v, ProxyMode = AiProxyMode.Custom }));
                    if (error == null) proxyCustomPending = false;
                    return error;
                })));
        var proxyTest = Button(aiBusy == "proxy" ? "测试中…" : "测试", null, () => _ = RunAi("proxy", async t => { await app.AiSetup.TestProxyAsync(t); return "能连上"; }));
        Enable(proxyTest, aiBusy == null && !proxyCustomPending);
        network.Add(aiResult is { } pr && aiLast == "proxy" ? Row("测试代理", null, Status(pr.Ok, pr.Text), proxyTest) : Row("测试代理", "按上面的代理连一下 AI 服务，不发送任何内容", proxyTest));
        Group("网络", network.ToArray(),
            "只在你主动触发时，才把那一段文字发给上面配置的服务；不会自动发送剪贴板内容。Key 和登录信息用 Windows 数据保护加密保存在本机。");
    }

    // API Key：只显示掩码；输入框只在要改的时候用，保存后清空
    FrameworkElement KeyRow(IAiSetup setup)
    {
        var box = new PasswordBox
        {
            PlaceholderText = setup.ApiKeyMask == null ? "sk-…" : "输入新的 Key 替换", Width = 220, Height = PaneTokens.ControlMd, Padding = new Thickness(10, 6, 10, 0),
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
        var controls = new List<FrameworkElement> { wrap, Button("保存", "check", Save) };
        if (setup.ApiKeyMask != null)
            controls.Add(IconButton("close", "删除已保存的 Key", () => { aiFieldError = Try(() => setup.SaveApiKey("")); Later(0); }));
        return Row("API Key", setup.ApiKeyMask is { } mask ? $"已保存：{mask}（加密保存在本机）" : "保存后只显示开头和结尾，加密保存在本机", controls);
    }

    IEnumerable<FrameworkElement> AccountRows(IAiSetup setup)
    {
        if (aiBusy == "signin")
        {
            yield return Row("正在登录", "已在浏览器打开授权页面，完成后会自动回到这里（5 分钟内有效）",
                Button("取消", null, () => signIn?.Cancel()));
            yield break;
        }
        if (setup.Account is not { } account)
        {
            yield return Row("用 ChatGPT 账号登录", "在浏览器里授权，翻译和提问算在你的 Plus / Pro 套餐里，不用填 API Key。免费账号能登录，但用不了套餐额度",
                Button("登录 ChatGPT", "login", () => _ = SignIn(setup)));
            if (aiResult is { Ok: false } r) yield return Row("没有登录上", null, Status(false, r.Text));
            yield break;
        }
        // 套餐名官方不保证给：不知道时只说能不能用，不猜 Plus / Pro
        var plan = !account.CanUsePlan ? "免费账号：能登录，但用不了套餐额度"
            : account.PlanName is { Length: > 0 } name ? $"ChatGPT {name} · 用套餐额度" : "已登录 · 用套餐额度（套餐名称未知）";
        yield return Row(account.Email, plan, Button("退出登录", "logout", () => _ = RunAi("signout", async t => { await setup.SignOutAsync(t); return null; })));
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
        var box = Field(api ? "模型 Id" : "默认（账号的第一个模型）", 220);
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
        var load = Button(aiBusy == "models" ? "拉取中…" : "拉取模型列表", "refresh", () => _ = RunAi("models", async t =>
        {
            aiModels = await app.Ai.ListModelsAsync(t);
            return aiModels.Count == 0 ? "服务没有返回模型" : null;
        }));
        Enable(load, aiBusy == null);
        var row = Row("模型", aiModels is { } list ? $"共 {list.Count} 个，点一个就用它；也可以直接输入" : api ? "填服务商给的模型 Id，或拉取列表来选" : "空着就用账号模型列表里的第一个", Wrap(box), load);
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
        var all = new List<(string Id, string Name, string Glyph)> { ("ai:translate", "翻译", "translate"), ("ai:ask", "问 AI", "ai"), ("files", "搜文件", "folder") };
        all.AddRange(ai.Actions.Where(a => a.Id != "translate").Select(a => ("ai:" + a.Id, a.Name, a.Glyph)));
        all.AddRange(l.SearchEngines.Select(e => ("web:" + e.Keyword, $"用 {e.Name} 搜", "globe")));
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
                tools.Add(IconButton("U", "上移", () => { SaveLauncher(x => { var list = x.ActionRow.Where(v => all.Any(a => a.Id == v)).ToList(); (list[pos - 1], list[pos]) = (list[pos], list[pos - 1]); return x with { ActionRow = list.ToArray() }; }); Render(); }));
            tools.Add(Switch(on, v =>
            {
                SaveLauncher(x => x with { ActionRow = v ? [.. x.ActionRow.Where(a => a != id), id] : x.ActionRow.Where(a => a != id).ToArray() });
                Render();
            }));
            rows.Add(ListRow(glyph, name, on ? $"第 {pos + 1} 个{(pos < 9 ? $"，Alt+{pos + 1}" : "")}" : "不显示", false, tools.ToArray()));
        }
        Group("动作行", rows.ToArray(), "启动器里输入任何文字都会出现这一排，位置固定。按住 Alt 显示序号，Alt+数字直接触发。网页搜索站点在下面「网页搜索」里增删。");
    }

    // 翻译用模型：单独指定一个快模型，空着 = 和主模型相同。候选用上面「拉取模型列表」拉到的
    FrameworkElement TranslationModelRow(AiSettings a)
    {
        string current = a.TranslationModel ?? "";
        var box = Field("和主模型相同", 220);
        box.Text = current;
        void Commit(string value)
        {
            var v = value.Trim();
            aiFieldError = Try(() => SaveAi(x => x with { TranslationModel = v.Length == 0 ? null : v }));
        }
        box.LostFocus += (_, _) => { if (box.Text.Trim() != current) { Commit(box.Text); Later(); } };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(box.Text); Later(0); e.Handled = true; } };
        var row = Row("翻译用模型", aiModels is null ? "翻译窗口和自动翻译用它，选个快一点的；空着就用主模型。在上面拉取模型列表后可以直接点选" : "翻译窗口和自动翻译用它，选个快一点的；空着就用主模型", Wrap(box));
        if (aiModels is not { } models) return row;
        var chips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(14, 0, 14, 10) };
        var main = Chip("和主模型相同");
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
            var name = Field("名称", 120);
            name.Text = act.Name;
            name.LostFocus += (_, _) => { var v = name.Text.Trim(); if (v.Length > 0 && v != act.Name) aiFieldError = Try(() => SaveAction(index, x => x with { Name = v })); };
            var prompt = Field("提示词", 0);
            prompt.Width = double.NaN;
            prompt.Height = 92;
            prompt.AcceptsReturn = true;
            prompt.TextWrapping = TextWrapping.Wrap;
            prompt.Padding = new Thickness(10, 8, 10, 8);
            prompt.Text = act.PromptTemplate;
            prompt.LostFocus += (_, _) =>
            {
                var v = prompt.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                if (v.Trim().Length == 0) { aiFieldError = "提示词不能是空的"; Later(0); return; }
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
            if (index > 0) tools.Children.Add(IconButton("U", "上移", () => { MoveAction(index); Render(); }));
            if (act.Id != "translate")
                tools.Children.Add(IconButton("close", "删除这个动作", () => { SaveAi(x => x with { Actions = x.Actions.Where((_, j) => j != index).ToArray() }); Render(); }));
            Grid.SetColumn(tools, 3);
            head.Children.Add(tools);
            var block = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 12) };
            block.Children.Add(head);
            block.Children.Add(Wrap(prompt));
            list.Add(block);
        }
        list.Add(AddRow(Button("添加动作", "plus", () =>
        {
            SaveAi(x => x with { Actions = [.. x.Actions, new AiActionSettings("custom-" + DateTime.Now.ToString("yyyyMMddHHmmss"), "新动作", "ai", "对下面的内容……\n\n{text}")] });
            Render();
        })));
        Group("预设动作", list.ToArray(), "可用变量：{text} 要处理的文字，{lang} 目标语言。离开输入框时自动保存。启动器和超级面板按这里的顺序显示。");
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
        catch (Exception e) { aiResult = (false, App.FailureReason(e) ?? "出了点问题"); }
        finally { aiBusy = null; }
        if (current.Id == "ai") Render();
    }

    static string? Try(Action save)
    {
        try { save(); return null; }
        catch (Exception e) { return e is ArgumentException ? "填的内容不对，没有保存" : App.FailureReason(e) ?? "没有保存成功"; }
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
        ("hotkey", "快捷键"), ("window", "窗口"), ("volume", "音量"), ("open", "打开"), ("run", "运行命令"),
        ("text", "输入文字"), ("search", "网页搜索"), ("task", "切换窗口"), ("pause", "暂停手势"), ("none", "什么都不做"),
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
            if (d.Directions.Count == 0) { arrows.Children.Add(Text(d.Modifier == G.GestureModifier.None ? "还没有方向" : "不画方向（只用下面的附加动作）", DescProto)); return; }
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
            var b = IconButton(ArrowGlyphs[(int)dir], "加一段", () =>
            {
                if (d.Directions.Count > 0 && d.Directions[^1] == dir) return;   // 同方向连着算一段
                d.Directions.Add(dir); d.Error = null; ShowArrows();
            });
            Grid.SetRow(b, r); Grid.SetColumn(b, c); keys.Children.Add(b);
        }
        var back = IconButton("close", "去掉最后一段", () => { if (d.Directions.Count > 0) d.Directions.RemoveAt(d.Directions.Count - 1); ShowArrows(); });
        Grid.SetRow(back, 1); Grid.SetColumn(back, 1); keys.Children.Add(back);
        var drawRow = new Grid { ColumnSpacing = 16 };
        drawRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        drawRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        drawRow.Children.Add(pad);
        Grid.SetColumn(keys, 1); drawRow.Children.Add(keys);
        panel.Children.Add(drawRow);
        panel.Children.Add(FormLine("方向", arrows));

        // ---- 触发键、附加动作 ----
        panel.Children.Add(FormLine("用哪个键", Choices([("Right", "右键"), ("Middle", "中键"), ("X1", "后退键"), ("X2", "前进键")], d.Button.ToString(),
            v => d.Button = Enum.Parse<G.GestureButton>(v))));
        panel.Children.Add(FormLine("附加动作", Choices([("None", "无"), ("WheelUp", "滚轮向上"), ("WheelDown", "滚轮向下"), ("LeftClick", "点左键"), ("MiddleClick", "点中键"), ("X1Click", "点后退键"), ("X2Click", "点前进键")],
            d.Modifier.ToString(), v => d.Modifier = Enum.Parse<G.GestureModifier>(v))));
        if (d.Modifier != G.GestureModifier.None)
        {
            var repeat = Switch(d.ExecuteOnModifier, v => d.ExecuteOnModifier = v);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(repeat);
            line.Children.Add(Text("按住触发键时，每滚一下 / 点一下都执行一次（适合调音量、切换窗口）", DescProto));
            panel.Children.Add(FormLine("", line));
        }

        // ---- 动作 ----
        panel.Children.Add(FormLine("动作", Choices(CommandKinds, KindOf(d.Command), v => d.Command = NewCommand(v))));
        if (CommandEditor(d) is { } editor) panel.Children.Add(FormLine("", editor));

        var name = Field("显示在手势提示里，例如「关闭标签页」", 300);
        name.Text = d.Name;
        name.TextChanged += (_, _) => d.Name = name.Text;
        panel.Children.Add(FormLine("名称", Wrap(name)));

        // ---- 保存 ----
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foot.Children.Add(Button("保存", "check", () => SaveDraft(d)));
        foot.Children.Add(Button("取消", null, () => Collapse("gesture", () => { draft = null; Render(); })));
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
        var hint = Text("在这里按住鼠标左键画方向", DescProto);
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
                    row.Children.Add(IconButton("plus", "再按一个键（连续按）", () => { d.Command = new G.HotkeyCommand([.. ((G.HotkeyCommand)d.Command).Chords, default]); Render(); }));
                    if (chords.Count > 1) row.Children.Add(IconButton("close", "去掉最后一个", () => { d.Command = new G.HotkeyCommand(((G.HotkeyCommand)d.Command).Chords.SkipLast(1).ToArray()); Render(); }));
                }
                return row;
            }
            case G.WindowCommand w:
                return Choices([("MaximizeRestore", "最大化 / 还原"), ("Minimize", "最小化"), ("Close", "关闭"), ("ToggleTopmost", "置顶 / 取消"), ("SnapLeft", "贴左半边"), ("SnapRight", "贴右半边")],
                    w.Action.ToString(), v => d.Command = new G.WindowCommand(Enum.Parse<G.WindowAction>(v)));
            case G.VolumeCommand v:
            {
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(Choices([("Up", "加"), ("Down", "减"), ("ToggleMute", "静音 / 取消"), ("WheelAdjust", "跟着滚轮调")], v.Action.ToString(),
                    x => d.Command = (G.VolumeCommand)d.Command with { Action = Enum.Parse<G.VolumeAction>(x) }));
                if (v.Action != G.VolumeAction.ToggleMute)
                    stack.Children.Add(Choices([("2", "每次 2%"), ("5", "每次 5%"), ("10", "每次 10%")], v.Step.ToString(),
                        x => d.Command = (G.VolumeCommand)d.Command with { Step = int.Parse(x) }));
                return stack;
            }
            case G.OpenCommand o:
            {
                var box = Field("程序、文件、文件夹或网址", 300);
                box.Text = o.Target;
                box.TextChanged += (_, _) => d.Command = new G.OpenCommand(box.Text.Trim());
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(Wrap(box));
                row.Children.Add(Button("选择文件", "folder", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                    if ((await picker.PickSingleFileAsync())?.Path is { } path) { d.Command = new G.OpenCommand(path); if (d.Name.Length == 0) d.Name = "打开 " + Path.GetFileNameWithoutExtension(path); Render(); }
                }));
                return row;
            }
            case G.RunCommand r:
            {
                var box = Field("命令行，例如 notepad.exe", 300);
                box.Text = r.CommandLine;
                box.TextChanged += (_, _) => d.Command = (G.RunCommand)d.Command with { CommandLine = box.Text.Trim() };
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(Wrap(box));
                var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                opts.Children.Add(Switch(r.InExplorerFolder, x => d.Command = (G.RunCommand)d.Command with { InExplorerFolder = x }));
                opts.Children.Add(Text("在资源管理器当前的文件夹里运行", DescProto));
                opts.Children.Add(Switch(r.ShowWindow, x => d.Command = (G.RunCommand)d.Command with { ShowWindow = x }));
                opts.Children.Add(Text("显示命令窗口", DescProto));
                stack.Children.Add(opts);
                return stack;
            }
            case G.SendTextCommand t:
            {
                var box = Field("要输入的文字", 300);
                box.Text = t.Sequence;
                box.TextChanged += (_, _) => d.Command = new G.SendTextCommand(box.Text);
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(Wrap(box));
                stack.Children.Add(Text("按 WGestures / SendKeys 写法：{ENTER} 回车，^ Ctrl，% Alt，+ Shift", DescProto));
                return stack;
            }
            case G.WebSearchCommand s:
            {
                var box = Field("地址，用 {query} 代表选中的文字", 360);
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
            d.Directions.Count == 0 && d.Modifier == G.GestureModifier.None ? "先画方向，或选一个附加动作"
            : d.Command switch
            {
                G.HotkeyCommand h when h.Chords.Count == 0 || h.Chords.Any(k => k.VirtualKey == 0) => "还没有录按键",
                G.OpenCommand o when o.Target.Length == 0 => "要打开什么还没填",
                G.RunCommand r when r.CommandLine.Length == 0 => "命令还没填",
                G.SendTextCommand t when t.Sequence.Length == 0 => "要输入的文字还没填",
                G.WebSearchCommand w when !w.UrlTemplate.Contains("{query}") => "搜索地址里要有 {query}",
                G.VolumeCommand { Action: G.VolumeAction.WheelAdjust } when d.Modifier is not (G.GestureModifier.WheelUp or G.GestureModifier.WheelDown) => "「跟着滚轮调」要配合附加动作「滚轮」",
                _ => null,
            }
            // 只在同一个列表里查重：程序专用手势和全局一样是允许的（这个程序里用专用的）
            ?? (BindingsOf(config, d.App).Select((g, i) => (g, i)).FirstOrDefault(x => x.i != d.Index && G.GestureMatcher.Same(x.g.Pattern, pattern)) is { g: { } same } ? $"和「{same.Name}」的手势一样" : null);
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
        var trigger = Seg([("X1", "长按后退键"), ("X2", "长按前进键"), ("Middle", "长按中键")], p.TriggerButton.ToString(),
            v => SaveSuper(x => x with { TriggerButton = Enum.Parse<SuperPanelButton>(v) }));
        // 2026-10-29 用户要求：改滑杆，0.1～1 秒自己调（原来是 0.3 / 0.4 / 0.6 三档）。拖动时只改读数，松手才存
        var holdValue = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = MonoProto.FontFamily, FontSize = 12, Foreground = SmallProto.Foreground };
        var hold = new PaneSlider(100, 1000, 50, Math.Clamp(p.HoldMilliseconds, 100, 1000), FillProto.Background, FillProto.BorderBrush, Accent());
        void HoldReadout(double v) => holdValue.Text = (v / 1000).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " 秒";
        HoldReadout(hold.Value);
        hold.ValueChanged += (_, v) => HoldReadout(v);
        hold.Committed += (_, _) => { int ms = (int)Math.Round(hold.Value); if (ms != app.Store.Current.SuperPanel.HoldMilliseconds) SaveSuper(x => x with { HoldMilliseconds = ms }); };
        var fullscreen = Switch(p.PauseInFullscreen, v => SaveSuper(x => x with { PauseInFullscreen = v }));
        var borrow = Switch(p.ClipboardFallback, v => SaveSuper(x => x with { ClipboardFallback = v }));
        // 关掉超级面板时其余各项变灰（原型 .srow.disabled）
        foreach (var c in new Control[] { trigger, hold, fullscreen, borrow }) Enable(c, p.Enabled);
        holdValue.Opacity = p.Enabled ? 1 : 0.4;
        Group("基本",
            Row("启用超级面板", null, Switch(p.Enabled, v => { SaveSuper(x => x with { Enabled = v }); Later(); })),
            Row("触发键", "长按才弹出；短按照常是原来的功能（后退 / 前进 / 中键点击）。同一个键打开了手势时：按住拖动是手势，按住不动才是超级面板", trigger),
            Row("长按多久", "太短容易误触，太长不跟手；默认 0.4 秒", hold, holdValue),
            Row("全屏程序里不触发", "玩游戏、看视频时侧键保持原样", fullscreen),
            Row("读不到选中内容时借用剪贴板", "优先直接读取选区，不碰剪贴板；读不到时先暂存剪贴板、模拟复制、取完再恢复，这次复制不进剪贴板历史", borrow));

        var programs = p.DisabledPrograms.Select(path => ListRow("app", Path.GetFileNameWithoutExtension(path), path, false,
            IconButton("close", "移除", () => { SaveSuper(x => x with { DisabledPrograms = x.DisabledPrograms.Where(v => !string.Equals(v, path, StringComparison.OrdinalIgnoreCase)).ToArray() }); Render(); }))).ToList();
        programs.Add(AddRow(Button("添加程序", "plus", async () =>
        {
            if (await PickProgram() is not { } exe) return;
            SaveSuper(x => x with { DisabledPrograms = x.DisabledPrograms.Any(v => string.Equals(v, exe, StringComparison.OrdinalIgnoreCase)) ? x.DisabledPrograms : [.. x.DisabledPrograms, exe] });
            Render();
        })));
        Group("在这些程序里不触发", programs.ToArray(), "在这些程序里长按侧键也照常传给程序。");
        RenderSuperActions(p);
    }

    // 面板里的动作（2026-10-30，原型 settings.js「面板里的动作」）：照启动器动作行，显示的在前、按顺序，可上移；关掉的排在后面。
    // 一排放 6 个，开满了其余的开关变灰。候选：AI 预设动作、搜索、复制
    void RenderSuperActions(SuperPanelSettings p)
    {
        var all = app.Store.Current.Ai.Actions.Select(a => ("ai:" + a.Id, a.Name, a.Glyph))
            .Append(("search", "搜索", "globe")).Append(("copy", "复制", "clipboard")).ToList();
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
                tools.Add(IconButton("U", "上移", () => { SaveSuper(x => { var list = Shown(x); (list[pos - 1], list[pos]) = (list[pos], list[pos - 1]); return x with { Actions = list.ToArray() }; }); Render(); }));
            var toggle = Switch(on, v =>
            {
                SaveSuper(x => { var list = Shown(x).Where(a => a != id).ToList(); if (v) list.Add(id); return x with { Actions = list.ToArray() }; });
                Render();
            });
            Enable(toggle, p.Enabled && (on || !full));
            tools.Add(toggle);
            rows.Add(ListRow(glyph, name, on ? $"第 {pos + 1} 个" : full ? "已经放满 6 个，先关掉一个" : "不显示", false, tools.ToArray()));
        }
        Group("面板里的动作", rows.ToArray(), "选中的不是中文、又开了自动翻译时，面板换成翻译专用的一排，不受这里影响。AI 动作的名字和提示词在「AI」里改。");
    }

    void SaveSuper(Func<SuperPanelSettings, SuperPanelSettings> change) =>
        app.Store.Save(app.Store.Current with { SuperPanel = change(app.Store.Current.SuperPanel) });

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
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "开发版";
        Group(null,
            Row("Pane", "Windows 上的启动器、鼠标手势和剪贴板工具", Chip(version)));
        if (app.Updates is { } updates) RenderUpdate(updates);
        Group("信息",
            Row("开源许可", "GPL-3.0（附加许可：可与微软运行库一起分发）；源代码在 GitHub", Button("打开", "external", () => OpenUrl(SourceUrl))),
            Row("数据位置", app.Store.DirectoryPath, Button("打开", "external", () =>
            {
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(app.Store.DirectoryPath);
                System.Diagnostics.Process.Start(start)?.Dispose();
            })));
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        var noticeButton = Button("查看全部", "external", () => OpenUrl(notices));
        Enable(noticeButton, File.Exists(notices));
        Group("开源组件",
            ListRow("app", "Lucide 图标", "lucide-static 1.60.0 · ISC 许可", false),
            ListRow("app", "Windows App SDK", "WinUI 3 · MIT 许可", false),
            ListRow("text", "MiSans", "使用本机已安装的字体，不随程序分发；没装时用微软雅黑", false),
            Row("全部第三方组件", "安装目录里的 THIRD_PARTY_NOTICES.md", noticeButton));
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
        string Ago(DateTimeOffset? t) => t is not { } v ? "还没检查过"
            : DateTimeOffset.Now - v < TimeSpan.FromMinutes(1) ? "刚刚检查过"
            : DateTimeOffset.Now - v < TimeSpan.FromHours(1) ? $"{(int)(DateTimeOffset.Now - v).TotalMinutes} 分钟前检查过"
            : DateTimeOffset.Now - v < TimeSpan.FromDays(1) ? $"{(int)(DateTimeOffset.Now - v).TotalHours} 小时前检查过" : $"{v.LocalDateTime:M 月 d 日}检查过";
        string Size(long b) => $"{b / 1024.0 / 1024:0.#} MB";
        var check = Button(u.State == UpdateState.Checking ? "检查中…" : "检查更新", "refresh", () => Run(u.CheckAsync));
        Enable(check, u.State is not (UpdateState.Checking or UpdateState.Downloading));
        var rows = new List<FrameworkElement>();
        var latest = u.Latest;
        switch (u.State)
        {
            case UpdateState.Available when latest is not null:
                rows.Add(Row($"发现新版本 {latest.Version}", $"{Size(latest.SizeBytes)} · {latest.PublishedAt.LocalDateTime:M 月 d 日}发布",
                    Button("查看详情", "external", () => OpenUrl(latest.PageUrl)), Button("下载更新", "plus", () => Run(u.DownloadAsync))));
                break;
            case UpdateState.Downloading when latest is not null:
                rows.Add(Row($"正在下载 {latest.Version}", $"{u.DownloadProgress:P0} · 共 {Size(latest.SizeBytes)}；下载好会提示你", Chip($"{u.DownloadProgress:P0}")));
                break;
            case UpdateState.Ready when latest is not null:
                rows.Add(Row($"{latest.Version} 已经下载好", "重启后更新：安装窗口会自己装完并打开新版，设置和数据都保留",
                    Button("重启并更新", "refresh", () => u.InstallAndRestart())));
                break;
            case UpdateState.Failed:
                rows.Add(Row("更新没成功", null, Status(false, u.Error ?? "出了点问题"), Button("重试", "refresh", () => Run(latest is not null && u.Error?.Contains("下载") == true ? u.DownloadAsync : u.CheckAsync))));
                break;
            default:
                rows.Add(Row(u.State == UpdateState.UpToDate ? "已是最新版本" : "检查更新", $"当前 {u.CurrentVersion} · {Ago(u.LastChecked)}", check));
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
        rows.Add(Row("自动更新", "每天检查一次，有新版就在后台下载好，再提示你重启更新；关掉后只在这里手动检查",
            Switch(app.Store.Current.Update.AutoUpdate, v => app.Store.Save(app.Store.Current with { Update = app.Store.Current.Update with { AutoUpdate = v } }))));
        Group("更新", rows.ToArray());
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
            Validate = h => h == other ? "和另一个热键重复了"
                : h.Modifiers == KeyModifiers.Win && h.VirtualKey == 'V' ? (app.WinV is null ? "Win+V 被系统占用，换一个组合" : "Win+V 被系统占用；想用它打开剪贴板，打开下面的「用 Pane 替换 Win+V」")
                : null,
        };
        box.Changed += (_, h) => { changed(h); Later(400); };   // 宿主在后台线程重新注册，稍后刷新占用提示
        box.RecordingChanged += (_, on) => app.SetHotkeysSuspended(on);
        return box;
    }

    // 注册失败 = 被占用
    static string ConflictText(HotkeyConflict c) => $"{HotkeyBox.Text(c.Hotkey).Replace("Space", "空格")} 被其他程序占用";

    // 开发和试用时 PANE_DEV_HOTKEYS 会改掉实际注册的键，照实说明
    static string? DevHotkey(string name)
    {
        var text = Environment.GetEnvironmentVariable("PANE_DEV_HOTKEYS");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var pair = text.Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == name);
        return pair == null ? null : $"开发模式：实际注册的是 {pair[1]}（PANE_DEV_HOTKEYS）";
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
