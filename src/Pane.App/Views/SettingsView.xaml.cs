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
    // Mod：这个分区属于哪块能力（055），能力停用时分区从导航里收起；null = 不属于任何能力，总在
    sealed record Section(string Id, string Name, string Glyph, string Desc, Action Render, string? Mod = null);

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
            new("modules", Strings.Settings_Modules, "blocks", Strings.Settings_Modules_Description, RenderModules),
            new("launcher", Strings.Settings_Launcher, "search", Strings.Settings_Launcher_Description, RenderLauncher, ModuleIds.Launcher),
            new("clipboard", Strings.Settings_Clipboard, "clipboard", Strings.Settings_Clipboard_Description, RenderClipboard, ModuleIds.Clipboard),
            new("words", Strings.Module_Words_Name, "words", Strings.Settings_Words_Description, RenderWords, ModuleIds.Words),
            new("gesture", Strings.Settings_Gestures, "gesture", Strings.Settings_Gestures_Description, RenderGesture, ModuleIds.Gestures),
            new("superpanel", Strings.Settings_SuperPanel, "pointer", Strings.Settings_SuperPanel_Description, RenderSuper, ModuleIds.SuperPanel),
            new("dialog", Strings.Settings_FolderJump, "folder", Strings.Settings_FolderJump_Description, RenderFolderJump, ModuleIds.FolderJump),
            new("ai", "AI", "ai", Strings.Settings_Ai_Description, RenderAi, ModuleIds.Ai),
            new("icons", Strings.Settings_Icons, "app", Strings.Settings_Icons_Description, RenderIcons),
            new("data", Strings.Settings_Data, "database", Strings.Settings_Data_Description, RenderData),
            new("about", Strings.Settings_About, "info", Strings.Settings_About_Description, RenderAbout),
        ];
        current = sections[0];
        // 设计预览（只在演示模式）：PANE_DESIGN_TOOLTIP=1 打开后直接显示关闭按钮的提示气泡，截图核对 Controls.xaml 的 ToolTip 样式
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_TOOLTIP") == "1")
            Loaded += async (_, _) =>
            {
                await Task.Delay(150);   // 截图脚本 show 之后很快就取图，早点打开，取图时淡入已经播完
                var tip = new ToolTip { Content = Strings.Common_CloseEscapeHint };
                ToolTipService.SetToolTip(CloseButton, tip);
                tip.IsOpen = true;
            };
        // 设计预览：PANE_DESIGN_SETTINGS=分区 Id（可加 @像素 先滚下去，如 gesture@700），或 glass（通用页展开「玻璃参数（高级）」并滚到底）
        var design = Environment.GetEnvironmentVariable("PANE_DESIGN_SETTINGS");
        if (design?.Split('@') is [var id, var px] && double.TryParse(px, out var y))
        {
            design = id;
            // 页面长时刚加载那一下内容高度还不够，滚不到位；稍后再滚一次
            Loaded += async (_, _) => { Scroller.ChangeView(null, y, null, true); await Task.Delay(800); Scroller.ChangeView(null, y, null, true); };
        }
        // 设计预览（只在演示模式）：PANE_DESIGN_ICONS=list 预置几条按应用设置；search:关键字 同时打开「添加应用」搜索框
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_ICONS") is { Length: > 0 } iconPreview)
        {
            SaveIcons(x => x with { Overrides = new() { ["app:photoshop"] = IconMode.ForceMask, ["app:obs"] = IconMode.Auto, ["app:steam"] = IconMode.Raw }, CustomIcons = new() { ["app:obs"] = "obs.png" } });
            if (iconPreview.StartsWith("search:", StringComparison.Ordinal)) iconQuery = iconPreview[7..];
        }
        // 设计预览（只在演示模式）：PANE_DESIGN_PORTABLE=1 预置几条便携软件（扫到的、找不到的、单个程序）和 5 个记住的程序；=open 同时展开记住的程序
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_PORTABLE") is "1" or "open")
        {
            learnedOpen = Environment.GetEnvironmentVariable("PANE_DESIGN_PORTABLE") == "open";
            var now = DateTimeOffset.Now;
            PortableEntry[] seed =
            [
                new(@"D:\绿色软件", PortableKind.Folder, now), new(@"D:\便携工具", PortableKind.Folder, now), new(@"C:\Windows\System32\notepad.exe", PortableKind.Program, now), // 不翻译：演示数据
                .. Enumerable.Range(1, 5).Select(i => new PortableEntry($@"D:\下载\工具{i}.exe", PortableKind.Learned, now)), // 不翻译：演示数据
            ];
            SaveLauncher(x => x with { Portable = seed });
        }
        // 设计预览（只在演示模式）：PANE_DESIGN_AI_ENDPOINTS=1 预置两个自定义服务商（065），当前选中 Anthropic 格式的那个
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_AI_ENDPOINTS") == "1")
            SaveAi(x => x with
            {
                Provider = AiProvider.Api, ApiEndpoint = "custom-0000c1a0",
                Endpoints = [new("custom-00000e1a", "公司中转", "https://llm.example.com/v1", AiApiFormat.OpenAi, "gpt-6-luna", null), // 不翻译：演示数据
                    new("custom-0000c1a0", "Claude", "https://api.anthropic.com/v1", AiApiFormat.Anthropic, "claude-sonnet-5-5", null)], // 不翻译：演示数据
            });
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
        // 设计预览（只在演示模式）：PANE_DESIGN_MODULES=launcher,super-panel 先停用这几块能力（看「能力」页和导航收起）
        if (Environment.GetEnvironmentVariable("PANE_DEMO") == "1" && Environment.GetEnvironmentVariable("PANE_DESIGN_MODULES") is { Length: > 0 } off)
            foreach (var modId in off.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (app.Modules.Modules.Any(m => m.Id == modId)) _ = app.Modules.ApplyAsync(app.Modules.Preview(modId, false), CancellationToken.None);
        navHighlight = new SlidingHighlight(NavLayer);
        BuildNav();
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "pane-app.svg");
        if (File.Exists(logo)) Logo.Source = new SvgImageSource(new Uri(logo));

        // 焦点变了不自动滚（issue 46）：窗口重新拿回焦点时宿主把焦点放到第一个可聚焦控件（页面顶上那个），
        // 自动滚的话页面每次都跳回顶部。只有键盘 Tab 切过去时才滚到控件；代码聚焦的输入框各自滚
        Body.GotFocus += (_, e) => { if (e.OriginalSource is Control { FocusState: FocusState.Keyboard } c) c.StartBringIntoView(); };

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
        if (PortableApps is { } pa) pa.Changed += OnFileSearchChanged;   // 扫描完成：同样只重建启动器分区
        Data.Changed += OnDataChanged;   // 数据分区（082）
        if (AppAliases is { } aa) aa.Changed += OnFileSearchChanged;   // 别名改了（启动器里设的）：重建启动器分区
        app.Modules.Changed += OnModulesChanged;
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
        if (PortableApps is { } pa) pa.Changed -= OnFileSearchChanged;
        Data.Changed -= OnDataChanged;
        if (AppAliases is { } aa) aa.Changed -= OnFileSearchChanged;
        app.Modules.Changed -= OnModulesChanged;
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
    // 正在 CDK 框里打字时不重建（重建会换掉输入框、丢焦点）：记下来，离开输入框时再刷新（SettingsView.About.cs）
    void OnUpdateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (current.Id != "about") return;
        if (cdkBox is { FocusState: not FocusState.Unfocused }) { updateRenderPending = true; return; }
        Later(0);
    });
    void OnFileSearchChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "launcher") Later(0); });
    void Later(double ms = 320) { rerender.Stop(); rerender.Interval = TimeSpan.FromMilliseconds(ms * app.Glass.Current.MotionSpeed); rerender.Start(); }

    // ================= 外壳 =================

    bool Visible(Section s) => s.Mod is null || app.Modules.IsActive(s.Mod);

    // 导航只列启用了的能力的分区；当前分区所属的能力被停用了，就回到「能力」页
    void BuildNav()
    {
        Nav.Children.Clear();
        navItems.Clear();
        if (!Visible(current)) current = sections.First(s => s.Id == "modules");
        navPlaced = false;
        foreach (var s in sections.Where(Visible)) Nav.Children.Add(NavItem(s));
    }

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
        if (navPlaced && navItems.Find(i => ReferenceEquals(i.Tag, s)) is { } item) navHighlight.MoveTo(item);
        Render(resetScroll: changed);
    }

    /// <summary>打开到某个分区（各处「去设置」用，issue 11）；认不出的 Id 不动。</summary>
    public void ShowSection(string id)
    {
        if (sections.Find(s => s.Id == id) is { } s && Visible(s)) Show(s);
    }

    void Render() => Render(false);

    void Render(bool resetScroll)
    {
        rerender.Stop();
        if (resetScroll) actionRowOrder = null;   // 换分区：动作行按显示顺序重新排
        // 刚拨的开关 / 分段还在播动画：推迟到播完再重建（换分区 resetScroll 不等，旧控件本来就不要了）
        if (!resetScroll && Pane.Views.Controls.MotionHold.Remaining is > 0 and var wait)
        {
            rerender.Interval = TimeSpan.FromMilliseconds(wait + 16);
            rerender.Start();
            return;
        }
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

    // ================= IPanelView =================

    public Size PreferredSize => new(900, 620);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { }
    public UIElement? DragRegion => Head;
}
