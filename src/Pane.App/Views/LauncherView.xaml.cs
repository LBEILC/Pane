using Pane.Localization;
// 启动器视图逻辑（设计负责人）。交互照原型 prototype/launcher.html（2026-10-29 新排法）：
// 空输入显示「最近使用 / 已固定」格子；输入时按内容分排法：算式卡片 → 应用格子 → 动作行 → 文件列表（前 3 个 + 「查看全部」）。
// 方向键：左右按顺序走（只在选中格子时；选中列表行时留给输入框），上下走到上一行 / 下一行里横向最近的一项。
// 底部提示栏写明选中项按 Enter / Tab / Ctrl+Enter 各做什么。按住 Alt 动作格子出数字，Alt+数字直接触发。
// Tab：动作「翻译 / 问 AI / 搜文件」、文件、「查看全部」展开成二级窗口（宿主接），带上输入。
// 附件：复制后 3 秒内呼出自动带入，或从剪贴板「交给启动器」；有附件时列表换成对附件的动作。
// AI：选 AI 项后结果卡片代替列表；Enter 粘贴回原窗口、Ctrl+C 复制、Ctrl+R 重新生成、Tab 继续对话（翻译是翻译窗口）。
// Esc 分步：AI 卡片 → 回到列表；有输入 → 清空；有附件 → 去掉；最后收起（docs/design.md「Esc 分步」）。
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Hosting;
using Pane.Views.Controls;
using Pane.Views.Launcher;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class LauncherView : UserControl, IPanelView, IPanelOpeningSize, IDisposable
{
    const double SearchRowHeight = 60, FootHeight = 34;
    // 宽度、应用行数、文件个数跟设置走（issue 24，docs/design.md「启动器尺寸与显示数量」）：格子 82 宽，左右内边距各 8 + 1，
    // 紧凑 / 标准 / 宽 = 一行 6 / 7 / 9 个 = 510 / 592 / 756。每次呼出时按当前设置算一次
    double Width_ = 592, ResultsMaxHeight = 452, AiMaxHeight = 560;
    int columns = 7;
    int MaxApps => Math.Clamp(Options.AppRows, 1, 4) * columns;
    static int MaxFiles => Options.FileRows is 3 or 5 or 8 or 10 ? Options.FileRows : 3;
    internal static (double Width, int Columns) Geometry(Settings.LauncherWidth width) => width switch
    {
        Settings.LauncherWidth.Compact => (510, 6),
        Settings.LauncherWidth.Wide => (756, 9),
        _ => (592, 7),
    };
    // 结果区最高：所在显示器工作区高度的 70% 减去搜索行和底栏，至少 300（原来固定 452）
    void ApplyGeometry(PanelShowContext context)
    {
        (Width_, columns) = Geometry(Options.Width);
        Root.Width = Width_;
        double work = 1080, scale = XamlRoot?.RasterizationScale is > 0 and var r ? r : 1;
        var point = context.AnchorPhysical is { } a ? new Hosting.GlassPanel.Point { X = (int)a.X, Y = (int)a.Y } : new Hosting.GlassPanel.Point();
        var info = new Hosting.GlassPanel.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Hosting.GlassPanel.MonitorInfo>() };
        if (Hosting.GlassPanel.GetMonitorInfoW(Hosting.GlassPanel.MonitorFromPoint(point, 2), ref info)) work = (info.Work.Bottom - info.Work.Top) / scale;
        ResultsMaxHeight = Math.Max(300, Math.Floor(work * 0.7) - SearchRowHeight - FootHeight - 1);
        Scroller.MaxHeight = ResultsMaxHeight;
        // 问 AI 时面板可以更长（用户群反馈）：整个面板到工作区的 76%（面板顶在约 23% 处，下面还留一点边）
        AiMaxHeight = Math.Max(360, Math.Floor(work * 0.76) - SearchRowHeight - 1);
    }
    static string Placeholder => Strings.Launcher_SearchPlaceholder;
    // 设置 → 启动器：「复制后自动带入」时长、「再次呼出时」怎么处理上次输入、动作行
    static Settings.LauncherSettings Options => ((App)Application.Current).Store.Current.Launcher;
    // AI 停用（issue 8；055 起由「能力」页开关）：动作行、附件动作里的 AI 条目都不出现
    static bool AiOn => ((App)Application.Current).Store.Current.Ai.Enabled && ModuleGate.On(Contracts.ModuleIds.Ai);
    DateTimeOffset hiddenAt = DateTimeOffset.MinValue;

    readonly ILauncherSearch search;
    readonly IAiService ai;
    readonly ISelectionActions output;
    readonly ILauncherAttachmentActions attachmentActions;
    readonly IClipboardHistory clipboard;
    readonly SlidingHighlight highlight;

    List<LauncherSection> sections = [];
    List<LauncherRow> rows = [];
    int selected = -1;
    bool activate, aiMode;
    CancellationTokenSource? pending;
    double lastHeight, lastWidth = 592, noticeHeight;
    ClipEntry? attachment, pendingAttachment;
    nint previousWindow;
    string shownQuery = "";
    AiRequest? cardRequest;
    string cardTitle = "";
    // 附加操作（050，issue 10）：正在列谁的操作；每行对应的操作
    LauncherItem? menuItem;
    List<MenuEntry> menuEntries = [];

    /// <summary>操作列表的一行：Pin 是界面自己做的固定 / 取消固定（issue 27）；否则 Action 为服务给的操作，为空是「打开」。</summary>
    readonly record struct MenuEntry(LauncherItemAction? Action = null, bool Pin = false);

    /// <summary>文件窗口：查询文字、要选中的文件路径（文件格按 Tab 时）。宿主负责创建或复用窗口。</summary>
    public event Action<string, string?>? OpenFiles;
    /// <summary>翻译窗口：带上要翻译的文字。</summary>
    public event Action<string>? OpenTranslate;
    /// <summary>批量重命名（040）：文件附件给出文件；命令不带文件，宿主读呼出前那个资源管理器窗口（第二个参数）里选中的。</summary>
    public event Action<IReadOnlyList<string>?, nint>? OpenRename;
    /// <summary>「定时关机…」（058）：打开定时窗口。</summary>
    public event Action? OpenTimer;
    /// <summary>AI 对话窗口：要直接发出的问题，或带过去的一问一答（「继续对话」）。</summary>
    public event Action<string?, IReadOnlyList<AiChatMessage>?>? OpenChat;
    /// <summary>文件格 Ctrl+Enter「打开所在文件夹」用；宿主设置（021 之前演示模式用假服务）。</summary>
    public IFileSearch? Files { get; set; }

    public LauncherView() : this(new LauncherSampleSearch(), new AiSample(), new SelectionActionsSample(),
        new LauncherAttachmentActionsSample(), new ClipboardSample()) { }

    public LauncherView(ILauncherSearch search, IAiService ai, ISelectionActions output,
        ILauncherAttachmentActions attachmentActions, IClipboardHistory clipboard)
    {
        this.search = search;
        this.ai = ai;
        this.output = output;
        this.attachmentActions = attachmentActions;
        this.clipboard = clipboard;
        InitializeComponent();
        Controls.PaneCaret.Attach(Input);
        // 提示条（062）：「重启更新」「查看」交给宿主（查看会打开设置，启动器收起）；「忽略」「×」只收起这一条
        Notice.Primary += (_, n) => { CloseRequested?.Invoke(this, EventArgs.Empty); ((App)Application.Current).NoticePrimary(n); };
        Notice.Dismiss += (_, n) => { ((App)Application.Current).NoticeDismiss(n); noticeHeight = Notice.Show(null); RequestHeight(); };

        highlight = new SlidingHighlight(HighlightLayer);
        Loaded += (_, _) => HideClearButton();
        Input.GotFocus += (_, _) => HideClearButton();
        Sections.PointerMoved += OnResultsPointerMoved;
        Sections.Tapped += OnResultsTapped;
        Sections.RightTapped += OnResultsRightTapped;
        Card.ContentGrew += (_, _) => RequestHeight();
        Card.SizeChanged += (_, _) => RequestHeight();
        Icons.Ready += OnIconReady;
        // 引擎徽章的单色图标按主题上色，换主题时重画
        ActualThemeChanged += (_, _) => { if (badgeEngine is { } e) _ = FillBadge(e); };
    }

    /// <summary>剪贴板「交给启动器」：宿主先调用这个，再显示启动器。</summary>
    public void AttachNext(ClipEntry entry)
    {
        // 单行短文字当搜索词填进输入框，别的当附件（2026-11-08 用户定）
        if (QueryText(entry) is { } text) pendingQuery = text; else pendingAttachment = entry;
    }

    /// <summary>剪贴板叠在启动器上时选了一条：单行短文字替换输入框内容，别的当附件。启动器已显示。</summary>
    public void Use(ClipEntry entry)
    {
        if (QueryText(entry) is { } text) { SetAttachment(null); Fill(text); return; }   // Fill 改了文字会触发搜索
        SetAttachment(entry);
        _ = Refresh(Input.Text);
    }

    /// <summary>能直接当搜索词的剪贴板文字：不是图片 / 文件、单行、去掉首尾空白后 1～100 字。</summary>
    public static string? QueryText(ClipEntry e) =>
        e.Kind is not (ClipKind.Image or ClipKind.Files) && e.Text?.Trim() is { Length: > 0 and <= 100 } t && t.IndexOfAny(['\r', '\n']) < 0 ? t : null;

    string? pendingQuery;

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(Width_, SearchRowHeight);
    // 设置 → 启动器 →「从搜索栏展开」（issue 38）：打开时先只有搜索栏（和提示条），宿主再用弹簧长到结果的高度
    public Size? OpeningSize => Options.ExpandOnOpen && Options.Home != Settings.LauncherHome.Nothing && rows.Count > 0 && !aiMode
        ? new Size(Width_, noticeHeight + SearchRowHeight) : null;
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => SearchRow; // 宿主只在没有可交互控件（输入框、附件的叉、Pane 胶囊）的位置开始拖动

    public void OnShowing(PanelShowContext context)
    {
        activate = context.Activate;
        navigating = false;
        previousWindow = context.PreviousForegroundWindow;
        ApplyGeometry(context);
        var notice = ((App)Application.Current).CurrentNotice();
        noticeHeight = Notice.Show(notice);
        if (notice != null) ((App)Application.Current).NoticeShown(notice);
        ExitAi();
        if (aliasItem is not null) { aliasItem = null; SetAttachment(attachment, animate: false); }   // 上次没保存的别名编辑作废
        menuItem = null;   // 每次呼出都从结果列表开始
        // 附件：交过来的优先；否则复制后几秒内呼出（设置里定，0 = 关），带入刚复制的内容（不清空系统剪贴板）
        var attach = pendingAttachment;
        pendingAttachment = null;
        var query = pendingQuery;   // 剪贴板交过来的短文字：直接当搜索词，不再自动带附件
        pendingQuery = null;
        int seconds = Options.AutoAttachSeconds;
        if (attach is null && query is null && seconds > 0 && clipboard.Snapshot().OrderByDescending(e => e.CopiedAt).FirstOrDefault() is { } newest
            && DateTimeOffset.Now - newest.CopiedAt < TimeSpan.FromSeconds(seconds))
            attach = newest;
        // 设计预览：PANE_DESIGN_ATTACH=text|image|files 带一个对应类型的附件
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_ATTACH") is { Length: > 0 } kind)
            attach = clipboard.Snapshot().FirstOrDefault(e => kind switch
            {
                "image" => e.Kind == ClipKind.Image,
                "files" => e.Kind == ClipKind.Files,
                _ => e.Kind is ClipKind.Text,
            }) ?? attach;
        SetAttachment(attach, animate: false);
        // 上次的输入：按设置清空、1 分钟内保留或一直保留；保留时全选，直接打字就替换。设计预览：PANE_DESIGN_QUERY 预先填入
        var keep = Options.KeepInput switch
        {
            Settings.KeepInputMode.Always => true,
            Settings.KeepInputMode.OneMinute => DateTimeOffset.Now - hiddenAt < TimeSpan.FromMinutes(1),
            _ => false,
        };
        var text = Environment.GetEnvironmentVariable("PANE_DESIGN_QUERY") ?? query ?? (keep ? Input.Text : string.Empty);
        // issue 40：空输入先用上次的首页结果同步画一遍，宿主打开时就是首页的高度（「都不显示」= 只有搜索栏）；异步搜到新的再补上
        if (text.Length == 0 && attach is null && homeResult is { } home) Render(home, string.Empty, attachmentMode: false);
        if (Input.Text != text) Input.Text = text;
        else _ = Refresh(text);
        if (query is not null) Input.SelectionStart = text.Length;   // 交过来的搜索词光标放末尾，接着打
        else if (text.Length > 0) Input.SelectAll();
    }

    public void OnShown()
    {
        if (activate) Input.Focus(FocusState.Programmatic);
        // 设计预览：PANE_DESIGN_LAUNCHER_RUN 指定一呼出就执行的条目 Id（如 act:ai:translate）
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_LAUNCHER_RUN") is { Length: > 0 } id
            && rows.FindIndex(r => r.Item.Id == id) is var i and >= 0) _ = Execute(i, false, false);
        // 设计预览：PANE_DESIGN_LAUNCHER_MENU=1 一呼出就列出默认选中项的附加操作（050）
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_LAUNCHER_MENU") == "1") _ = OpenMenu(selected);
    }

    // 永久销毁时由宿主调用（GlassPanel.Dispose，055 起能力停用、二级窗口关闭都会销毁视图）。
    // 不能只靠 Unloaded：内容岛直接释放时它不一定来。退订常驻对象的事件、停计时器、取消任务、释放附着的合成视觉
    public void Dispose()
    {
        Icons.Ready -= OnIconReady;
        if (hookedIcons is not null) { hookedIcons.Ready -= OnEngineIconReady; hookedIcons = null; }
        pending?.Cancel();
        if (armTimer is not null) { armTimer.Stop(); armTimer.Tick -= OnArmExpired; }
        Card.Release();
        highlight.Dispose();
        Sections.ItemsSource = null;
        rows = [];
        sections = [];
    }

    public void OnHidden()
    {
        hiddenAt = DateTimeOffset.Now;
        pending?.Cancel();
        ShowNumbers(false);
        ExitAi();
    }

    // ---------------- 附件 ----------------

    void SetAttachment(ClipEntry? entry, bool animate = true)
    {
        attachment = entry;
        if (entry is null)
        {
            AttachChip.Visibility = Visibility.Collapsed;
            Input.PlaceholderText = Placeholder;
            return;
        }
        AttachIcon.Glyph = entry.Kind switch { ClipKind.Image => "image", ClipKind.Files => "folder", _ => "clipboard" };
        AttachText.Text = AttachmentLabel(entry);
        AttachChip.Visibility = Visibility.Visible;
        Input.PlaceholderText = Strings.Launcher_AttachmentPlaceholder;
        if (animate) PlayAttachIn();
    }

    static string AttachmentLabel(ClipEntry e) => e.Kind switch
    {
        ClipKind.Files => string.Join("、", (e.Files ?? []).Select(System.IO.Path.GetFileName)),
        ClipKind.Image => $"{e.Text} {e.ImageWidth}×{e.ImageHeight}",
        _ => (e.Text ?? "").ReplaceLineEndings(" "),
    };

    static string AttachmentText(ClipEntry e) => e.Kind == ClipKind.Files ? string.Join("\n", e.Files ?? []) : e.Text ?? "";

    // 文字附件拿去搜文件：第一行非空的文字，去掉首尾空白，最多 100 字（同剪贴板短文字当搜索词的上限）
    static string FileQuery(ClipEntry? e)
    {
        var line = (e?.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 100 ? line[..100] : line;
    }

    void PlayAttachIn()
    {
        // 原型 attach-in：从 0.85 放大、淡入，--dur-slow + --ease-spring
        AttachChip.UpdateLayout();
        var v = ElementCompositionPreview.GetElementVisual(AttachChip);
        var c = v.Compositor;
        v.CenterPoint = new Vector3((float)AttachChip.ActualWidth / 2, (float)AttachChip.ActualHeight / 2, 0);
        var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.36f), new Vector2(0.64f, 1f));
        var duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurSlow"));
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.85f, 0.85f, 1));
        scale.InsertKeyFrame(1, Vector3.One, spring);
        scale.Duration = duration;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, spring);
        fade.Duration = duration;
        v.StartAnimation("Scale", scale);
        v.StartAnimation("Opacity", fade);
    }

    // 右边「Pane」胶囊：收起启动器、打开设置（停在上次看的那一页）
    void OnSettingsBadge(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        ((App)Application.Current).OpenSettings(null);
    }

    void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (aliasItem is not null) { ExitAlias(); return; }
        SetAttachment(null);
        _ = Refresh(Input.Text);
        Input.Focus(FocusState.Programmatic);
    }

    // 附件可用的动作（原型 attachActions），列表排法
    List<LauncherItem> AttachmentItems(ClipEntry a, string query)
    {
        var items = new List<LauncherItem>();
        if (a.Kind == ClipKind.Files)
        {
            var first = a.Files is { Count: > 0 } f ? f[0] : "";
            items.Add(new("open", LauncherItemKind.Action, LauncherGroups.Files, Strings.Common_Open, System.IO.Path.GetFileName(first), new("folder")));
            items.Add(new("reveal", LauncherItemKind.Action, LauncherGroups.Files, Strings.Common_OpenContainingFolder, Strings.Launcher_Explorer, new("folder")));
            items.Add(new("copy-path", LauncherItemKind.Action, LauncherGroups.Files, Strings.Common_CopyPath, string.Format(Strings.Launcher_PathCount, (object?)(a.Files?.Count ?? 0)), new("link")));
            if (ModuleGate.On(Contracts.ModuleIds.Rename)) items.Add(new("rename", LauncherItemKind.Action, LauncherGroups.Files, Strings.Common_Rename, string.Format(Strings.Launcher_FileCount, (object?)(a.Files?.Count ?? 0)), new("pencil")));
        }
        else if (a.Kind == ClipKind.Image)
        {
            items.Add(new("save-image", LauncherItemKind.Action, LauncherGroups.Images, Strings.Common_SaveAs, AttachmentLabel(a), new("image")));
            items.Add(new("copy-image", LauncherItemKind.Action, LauncherGroups.Images, Strings.Launcher_CopyImage, Strings.Launcher_RestoreClipboard, new("clipboard")));
        }
        else
        {
            foreach (var act in AiOn ? ai.Actions : [])
                items.Add(new("ai:" + act.Id, LauncherItemKind.Ai, LauncherGroups.Ai, act.Name,
                    act.Id == "translate" ? Strings.Launcher_AttachmentTranslateDescription : Strings.Launcher_AttachmentActionDescription, new(act.Glyph)));
            items.Add(new("web-search", LauncherItemKind.Action, LauncherGroups.Other, Strings.Launcher_WebSearch, Strings.Launcher_DefaultSearchEngine, new("globe")));
            // 复制后几秒内呼出带进来的文字也能直接搜文件（issue 46）：用第一行，在文件窗口里搜
            items.Add(new("search-files", LauncherItemKind.Action, LauncherGroups.Other, Strings.Launcher_SearchFiles, Strings.Launcher_OpenFiles, new("folder")));
        }
        var q = query.Trim();
        if (q.Length > 0) items = items.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (q.Length > 0 && AiOn && a.Kind != ClipKind.Image && a.Kind != ClipKind.Files)
            items.Add(new("ai:ask", LauncherItemKind.Ai, LauncherGroups.Ai, string.Format(Strings.Launcher_AskQuery, (object?)(q)), Strings.Launcher_SendWithAttachment, new("ai")));
        return items;
    }

    // ---------------- 动作行 ----------------

    // 设置里的 ActionRow（顺序即显示顺序）→ 动作格子。认不出的 Id 跳过
    List<LauncherItem> ActionItems()
    {
        var items = new List<LauncherItem>();
        foreach (var id in Options.ActionRow)
        {
            if (!AiOn && id.StartsWith("ai:", StringComparison.Ordinal)) continue;
            (string name, string glyph)? a = id switch
            {
                "ai:translate" => (Strings.Common_Translate, "translate"),
                "ai:ask" => (Strings.Launcher_AskAi, "ai"),
                "files" => (Strings.Launcher_SearchFiles, "folder"),
                _ when id.StartsWith("ai:", StringComparison.Ordinal) && ai.Actions.FirstOrDefault(x => x.Id == id[3..]) is { } act => (act.Name, act.Glyph),
                _ when id.StartsWith("web:", StringComparison.Ordinal) && Engine(id) is { } engine => (engine.Name, "globe"),
                _ => null,
            };
            if (a is { } v) items.Add(new("act:" + id, LauncherItemKind.Action, LauncherGroups.Actions, v.name, null, new(v.glyph)));
        }
        return items;
    }

    static Settings.SearchEngine? Engine(string actionId) =>
        Options.SearchEngines.FirstOrDefault(e => string.Equals("web:" + e.Keyword, actionId, StringComparison.OrdinalIgnoreCase));

    static string ActionId(LauncherItem item) => item.Id.StartsWith("act:", StringComparison.Ordinal) ? item.Id[4..] : item.Id;

    // ---------------- 搜索与渲染 ----------------

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        HideClearButton();
        if (aliasItem is not null) { AliasFoot(); return; }   // 编辑别名时输入框装的是别名，不搜索
        if (aiMode) ExitAi(); // 卡片打开时继续打字 = 回到列表重新搜
        menuItem = null;      // 操作列表里打字 = 回到列表接着搜
        navigating = false;   // 打字 = 左右键回到移动光标（issue 50）
        // 设计改（2026-10-30）：不再防抖，每次按键直接搜（026 补测：防抖 60 ms 加等慢来源，结果比 uTools 晚 110～170 ms）
        _ = Refresh(Input.Text);
    }

    async Task Refresh(string query)
    {
        pending?.Cancel();
        if (attachment is { } a) { Render(new LauncherResult(AttachmentItems(a, query), false), query, attachmentMode: true); return; }
        var cts = pending = new CancellationTokenSource();
        bool partialShown = false;
        // 本地来源先到：先画；文件搜索回来再用完整结果补上（保留用户已经挪过的选中）
        void Partial(LauncherResult r) => DispatcherQueue.TryEnqueue(() =>
        {
            if (cts.IsCancellationRequested || partialShown) return;
            partialShown = true;
            Render(r, query, attachmentMode: false);
        });
        LauncherResult result;
        try { result = await search.SearchAsync(query.Trim().Length == 0 ? string.Empty : query, Partial, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        partialShown = true;   // 部分结果还在队列里没画的，就不画了
        Render(result, query, attachmentMode: false, keepSelection: shownQuery == query.Trim());
        _ = Suggest(query.Trim(), cts);
    }

    // ---------------- 网页搜索建议（037） ----------------

    const int MaxSettings = 4;
    LauncherResult? lastResult, homeResult;
    (string Query, IReadOnlyList<string> Items) suggested = ("", []);

    /// <summary>「关键字 空格 内容」，关键字是设置里的某个搜索站点；内容不能是空的。</summary>
    static (Settings.SearchEngine Engine, string Text)? KeywordQuery(string q)
    {
        int space = q.IndexOf(' ');
        if (space <= 0) return null;
        var text = q[(space + 1)..].Trim();
        if (text.Length == 0) return null;
        return Options.SearchEngines.FirstOrDefault(e => string.Equals(e.Keyword, q[..space], StringComparison.OrdinalIgnoreCase)) is { } engine ? (engine, text) : null;
    }

    /// <summary>
    /// 关键字直达（052，issue 11 / 12）：「关键字 空格 内容」，关键字是设置里的翻译 / 问 AI / 搜文件关键字。
    /// 返回动作行的动作 Id（ai:translate、ai:ask、files）和内容；和搜索站点关键字重复时站点优先；AI 关着时 AI 两项不认。
    /// </summary>
    (string ActionId, string Text)? DirectQuery(string q)
    {
        int space = q.IndexOf(' ');
        if (space <= 0) return null;
        var text = q[(space + 1)..].Trim();
        if (text.Length == 0) return null;
        var key = q[..space];
        if (Options.SearchEngines.Any(e => string.Equals(e.Keyword, key, StringComparison.OrdinalIgnoreCase))) return null;
        bool Has(string[] keys) => keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (AiOn && Has(Options.TranslateKeywords)) return ("ai:translate", text);
        if (AiOn && Has(Options.AskKeywords)) return ("ai:ask", text);
        if (Has(Options.FilesKeywords)) return ("files", text);
        return null;
    }

    static LauncherItem WebItem(Settings.SearchEngine engine, string text, string title, string? hotkey, string glyph = "search") =>
        new("web:" + engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(text), StringComparison.Ordinal), LauncherItemKind.Web, LauncherGroups.Web, title, null, new(glyph), Hotkey: hotkey);

    /// <summary>输入以「站点关键字 空格」开头（后面可以还没打字）：徽章和「只输入了关键字时列历史」用。</summary>
    static Settings.SearchEngine? KeywordEngine(string raw)
    {
        var s = raw.TrimStart();
        int space = s.IndexOf(' ');
        if (space <= 0) return null;
        return Options.SearchEngines.FirstOrDefault(e => string.Equals(e.Keyword, s[..space], StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- 引擎徽章（issue 43）：关键字搜网页时搜索框左边的放大镜换成站点徽章 ----------------

    Settings.SearchEngine? badgeEngine;
    IEngineIcons? hookedIcons;   // 订阅了 Ready 的那个服务（能力重启后服务会换，退订要对上同一个）
    int badgeVersion;

    void UpdateBadge(Settings.SearchEngine? engine)
    {
        if (engine == badgeEngine) return;
        bool wasWeb = badgeEngine is not null;
        badgeEngine = engine;
        if (engine is null) { PlayBadge(false, wasWeb); return; }
        _ = FillBadge(engine);
        PlayBadge(true, wasWeb);
    }

    async Task FillBadge(Settings.SearchEngine engine)
    {
        int version = ++badgeVersion;
        var app = (App)Application.Current;
        if (app.EngineIcons is { } icons && !ReferenceEquals(icons, hookedIcons))
        {
            if (hookedIcons is not null) hookedIcons.Ready -= OnEngineIconReady;
            icons.Ready += OnEngineIconReady; hookedIcons = icons;
        }
        EngineBadgeImages.Glyph? glyph = null;
        if (app.EngineIcons?.TryGet(engine) is { } icon) glyph = await EngineBadgeImages.LoadAsync(icon, ActualTheme == ElementTheme.Dark);
        if (version != badgeVersion) return;   // 等图标的时候已经换了站点
        EngineGlyph.Source = glyph is { FullBleed: false } ? glyph.Source : null;
        EngineFull.Source = glyph is { FullBleed: true } ? glyph.Source : null;
        EngineGlyph.Visibility = glyph is { FullBleed: false } ? Visibility.Visible : Visibility.Collapsed;
        EngineFull.Visibility = glyph is { FullBleed: true } ? Visibility.Visible : Visibility.Collapsed;
        // 取不到图标：玻璃底上放站点名的首字
        EngineMono.Text = EngineBadgeImages.Monogram(engine.Name);
        EngineMono.Visibility = glyph is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // 用户自加站点的图标在后台取好了；可能在后台线程触发
    void OnEngineIconReady(object? sender, string url) => DispatcherQueue.TryEnqueue(() =>
    {
        if (badgeEngine is { } e && e.UrlTemplate == url) _ = FillBadge(e);
    });

    // 放大镜和徽章交替：进入时放大镜缩小淡出，徽章回弹放大淡入；换站点时徽章轻轻弹一下；退出反过来（原型 .slot.web）
    void PlayBadge(bool web, bool wasWeb)
    {
        var glyph = ElementCompositionPreview.GetElementVisual(SearchGlyph);
        var badge = ElementCompositionPreview.GetElementVisual(EngineBadge);
        var c = glyph.Compositor;
        glyph.CenterPoint = new Vector3(11, 11, 0);
        badge.CenterPoint = new Vector3(14, 14, 0);
        var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.36f), new Vector2(0.64f, 1f));
        var smooth = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        double speed = ((App)Application.Current).Glass.Current.MotionSpeed;
        TimeSpan Ms(string token) => TimeSpan.FromMilliseconds(Math.Max(1, GlassParameters.Token(token) * speed));
        void Scale(Visual v, float from, float to, CompositionEasingFunction ease, TimeSpan d)
        {
            var a = c.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, new Vector3(from, from, 1)); a.InsertKeyFrame(1, new Vector3(to, to, 1), ease); a.Duration = d;
            v.StartAnimation("Scale", a);
        }
        void Fade(Visual v, float to, TimeSpan d)
        {
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(1, to, smooth); a.Duration = d;
            v.StartAnimation("Opacity", a);
        }
        if (web && wasWeb) { Scale(badge, 0.85f, 1, spring, Ms("PaneDurSlow")); return; }
        var divider = ElementCompositionPreview.GetElementVisual(EngineDivider);
        EngineDivider.Opacity = 1;
        if (web) { divider.Opacity = 0; Fade(divider, 1, Ms("PaneDurNormal")); }
        else Fade(divider, 0, Ms("PaneDurFast"));
        if (web)
        {
            EngineBadge.Opacity = 1;
            Fade(glyph, 0, Ms("PaneDurFast")); Scale(glyph, 1, 0.6f, smooth, Ms("PaneDurNormal"));
            badge.Opacity = 0; Fade(badge, 1, Ms("PaneDurNormal")); Scale(badge, 0.6f, 1, spring, Ms("PaneDurSlow"));
        }
        else
        {
            Fade(badge, 0, Ms("PaneDurFast")); Scale(badge, 1, 0.6f, smooth, Ms("PaneDurFast"));
            Fade(glyph, 1, Ms("PaneDurNormal")); Scale(glyph, 0.6f, 1, spring, Ms("PaneDurSlow"));
        }
    }

    // ---------------- 网页搜索历史（issue 39）：只输入关键字时列最近的词；打字后历史里匹配的在上（最多 3 条），联想词在下 ----------------

    static IWebSearchHistory? History => Options.RememberWebSearches ? ((App)Application.Current).WebSearchHistory : null;
    Settings.SearchEngine? webEngine;   // 这次渲染是哪个站点的网页搜索（历史行删除、底栏文字用）

    void AddHistory(LauncherSection s, Settings.SearchEngine engine, string text)
    {
        var row = new LauncherRow(WebItem(engine, text, text, null, "history"), rows.Count, LauncherCell.Command) { HistoryText = text, Remove = RemoveHistory };
        rows.Add(row);
        s.Rows.Add(row);
    }

    void RemoveHistory(LauncherRow row)
    {
        if (row.HistoryText is not { } text || webEngine is not { } engine || ((App)Application.Current).WebSearchHistory is not { } history) return;
        history.Remove(engine, text);
        int keep = row.Index;
        if (lastResult is { } r) Render(r, Input.Text, attachmentMode: false);
        Select(Math.Min(keep, rows.Count - 1), scroll: false);
    }

    // 只在用关键字搜网页时才向那个站点要建议（平时打字不联网）；停手 150 ms 再要，回来时输入没变才补上
    async Task Suggest(string q, CancellationTokenSource cts)
    {
        if (KeywordQuery(q) is not { } web || !Options.WebSuggestions || ((App)Application.Current).WebSuggestions is not { } service) return;
        var key = web.Engine.Keyword + " " + web.Text;
        if (suggested.Query == key) return;
        try
        {
            await Task.Delay(150, cts.Token);
            var items = await service.SuggestAsync(web.Engine, web.Text, cts.Token);
            if (cts.IsCancellationRequested || shownQuery != q || items.Count == 0 || lastResult is not { } r) return;
            suggested = (key, items.Take(8).ToList());
            Render(r, q, attachmentMode: false, keepSelection: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 建议取不到就不显示，不打扰 */ }
    }

    void Render(LauncherResult result, string query, bool attachmentMode, bool keepSelection = false)
    {
        if (menuItem is not null) { lastResult = result; return; }   // 列着操作时晚到的结果先记下，Esc 回去再画
        // 停用的能力不出现在结果里（055）：「批量重命名」「剪贴板」这两条命令
        if (!ModuleGate.On(Contracts.ModuleIds.Rename) || !ModuleGate.On(Contracts.ModuleIds.Clipboard))
            result = result with { Items = result.Items.Where(i => !(i.Kind == LauncherItemKind.Command && (i.Id == "rename" && !ModuleGate.On(Contracts.ModuleIds.Rename) || i.Id == "command:clipboard" && !ModuleGate.On(Contracts.ModuleIds.Clipboard)))).ToList() };
        var q = query.Trim();
        var keep = keepSelection && selected >= 0 && selected < rows.Count && selected != DefaultSelection() ? rows[selected].Item.Id : null;
        shownQuery = q;
        rows = [];
        sections = [];
        webEngine = null;
        LauncherSection Section(string label, LauncherSectionKind kind) { var s = new LauncherSection(label, kind, []); sections.Add(s); return s; }
        void Add(LauncherSection s, LauncherItem item, LauncherCell cell, string? number = null) { var r = new LauncherRow(item, rows.Count, cell, number); rows.Add(r); s.Rows.Add(r); }

        if (result.IsHome)
        {
            // 设置 → 启动器 →「未输入时显示」：只显示固定 / 只显示最近时去掉另一组，什么都不显示时只剩搜索栏
            var home = Options.Home;
            bool showRecent = home is Pane.Settings.LauncherHome.RecentAndPinned or Pane.Settings.LauncherHome.RecentOnly;
            bool showPinned = home is Pane.Settings.LauncherHome.RecentAndPinned or Pane.Settings.LauncherHome.PinnedOnly;
            var groups = result.Items.GroupBy(i => i.Group)
                .Where(g => g.Key == LauncherGroups.Recent ? showRecent : g.Key == LauncherGroups.Pinned ? showPinned : home != Pane.Settings.LauncherHome.Nothing)
                .Select(g => (g.Key, Items: g.ToList())).ToList();
            // 「应用最多显示 N 行」按行分（issue 47）：两组各自换行，所以按整行给。已固定占它需要的行，
            // 但两组都有时至少给最近使用留一行（N = 1 时只放已固定：固定是用户自己选的）；剩下的整行都给最近使用
            int maxRows = Math.Clamp(Options.AppRows, 1, 4);
            int RowsOf(int count) => (count + columns - 1) / columns;
            int pinnedCount = groups.Where(g => g.Key == LauncherGroups.Pinned).Sum(g => g.Items.Count);
            bool hasRecent = groups.Any(g => g.Key == LauncherGroups.Recent && g.Items.Count > 0);
            int pinnedRows = Math.Min(RowsOf(pinnedCount), hasRecent && maxRows > 1 ? maxRows - 1 : maxRows);
            int recentRows = maxRows - pinnedRows;
            foreach (var (key, items) in groups)
            {
                int take = key == LauncherGroups.Recent ? recentRows * columns : key == LauncherGroups.Pinned ? pinnedRows * columns : items.Count;
                if (take <= 0) continue;
                var s = Section(LauncherGroupLabels.Get(key), LauncherSectionKind.Tiles);
                foreach (var item in items.Take(take)) Add(s, item, LauncherCell.App);
            }
        }
        else if (attachmentMode)
        {
            foreach (var item in result.Items)
            {
                if (sections.Count == 0 || sections[^1].GroupId != item.Group) sections.Add(new LauncherSection(LauncherGroupLabels.Get(item.Group), LauncherSectionKind.List, []) { GroupId = item.Group });
                Add(sections[^1], item, LauncherCell.Attachment);
            }
        }
        else if (q.StartsWith('>'))
        {
            // 终端（037）：「在终端里运行」一组，补全一组；不出应用和文件
            foreach (var item in result.Items.Where(i => i.Group is LauncherGroups.Terminal or LauncherGroups.Completion))
            {
                if (sections.Count == 0 || sections[^1].GroupId != item.Group) sections.Add(new LauncherSection(LauncherGroupLabels.Get(item.Group), LauncherSectionKind.List, []) { GroupId = item.Group });
                Add(sections[^1], item, LauncherCell.Command);
            }
        }
        else if (KeywordQuery(q) is { } web)
        {
            // 用搜索关键字搜网页（「bd 天气」）：只出网页一组，第一项就是用这个站点搜，下面跟站点给的搜索建议（037）
            webEngine = web.Engine;
            // 关键字后面是网址：第一条直接打开它，搜索排在下面
            var address = WebAddress(web.Text);
            if (address is not null)
                Add(Section(Strings.Common_Open, LauncherSectionKind.List), new("direct:url:" + address, LauncherItemKind.Web, LauncherGroups.Direct, address, Strings.Launcher_DirectOpenUrl, new("globe"), Hotkey: "Enter"), LauncherCell.Command);
            var s = Section(web.Engine.Name, LauncherSectionKind.List);
            Add(s, WebItem(web.Engine, web.Text, Strings.FormatName(Strings.Launcher_SearchWithQuery, web.Engine.Name, web.Text), address is null ? "Enter" : ""), LauncherCell.Command);   // 有「打开网址」时 Enter 徽标只留给它（空串不显示，但仍按「第一项」处理 Tab）
            var recent = History?.Recent(web.Engine, web.Text, 3) ?? [];
            foreach (var text in recent) AddHistory(s, web.Engine, text);
            if (suggested.Query == web.Engine.Keyword + " " + web.Text)
                foreach (var text in suggested.Items.Where(t => t != web.Text && !recent.Contains(t, StringComparer.OrdinalIgnoreCase))) Add(s, WebItem(web.Engine, text, text, null), LauncherCell.Command);
        }
        else if (KeywordEngine(query) is { } only)
        {
            // 只输入了「关键字 空格」：列这个站点最近搜过的词；没有记录时一条提示（不执行）
            webEngine = only;
            var s = Section(only.Name, LauncherSectionKind.List);
            foreach (var text in History?.Recent(only, "", 6) ?? []) AddHistory(s, only, text);
            if (s.Rows.Count == 0) Add(s, new("web:hint", LauncherItemKind.Command, LauncherGroups.Web, Strings.FormatName(Strings.Launcher_WebHint, only.Name), null, new("search")), LauncherCell.Command);
        }
        else if (DirectQuery(q) is { } direct)
        {
            // 关键字直达（052，issue 12）：只出这一项、默认选中，Enter 直接翻译 / 问 AI / 进文件窗口，不用再去动作行里挑
            var (label, title, glyph) = direct.ActionId switch
            {
                "ai:translate" => (Strings.Common_Translate, string.Format(Strings.Launcher_TranslateTarget, (object?)(direct.Text)), "translate"),
                "ai:ask" => (Strings.Launcher_AskAi, string.Format(Strings.Launcher_AskTarget, (object?)(direct.Text)), "ai"),
                _ => (Strings.Launcher_SearchFiles, string.Format(Strings.Launcher_SearchFilesTarget, (object?)(direct.Text)), "folder"),
            };
            Add(Section(label, LauncherSectionKind.List), new("direct:" + direct.ActionId, LauncherItemKind.Action, LauncherGroups.Actions, title, null, new(glyph), Hotkey: "Enter"), LauncherCell.Command);
        }
        else if (q.Length > 0)
        {
            // 直达（098，issue 52）：输入本身是网址或路径，「打开网址 / 打开文件夹 / 打开文件」排在最前、列表样式，默认选中
            var opens = result.Items.Where(i => i.Group == LauncherGroups.Direct).ToList();
            if (opens.Count > 0)
            {
                var s = Section(Strings.Common_Open, LauncherSectionKind.List);
                foreach (var item in opens) Add(s, item, item.Kind is LauncherItemKind.File or LauncherItemKind.Folder ? LauncherCell.File : LauncherCell.Command);
            }
            // 定时关机（058）：「30 分钟后关机」这类说法排在最前、列表样式，默认选中
            var timers = result.Items.Where(i => i.Group == LauncherGroups.Timer).ToList();
            if (timers.Count > 0)
            {
                var s = Section(Strings.Launcher_Group_Timer, LauncherSectionKind.List);
                foreach (var item in timers) Add(s, item, LauncherCell.Command);
            }
            // 服务给的 AI / 网页两组不用了（动作行代替）
            foreach (var item in result.Items.Where(i => i.Kind == LauncherItemKind.Calculation).Take(1))
                Add(Section("", LauncherSectionKind.Calc), item, LauncherCell.Calc);
            var apps = result.Items.Where(i => i.Kind is LauncherItemKind.App or LauncherItemKind.Command && i.Group is not (LauncherGroups.Settings or LauncherGroups.Timer)).Take(MaxApps).ToList();
            if (apps.Count > 0)
            {
                var s = Section(Strings.Launcher_Group_Applications, LauncherSectionKind.Tiles);
                foreach (var item in apps) Add(s, item, item.Kind == LauncherItemKind.App ? LauncherCell.App : LauncherCell.Command);
            }
            // 系统设置和组件（037）：名字长，排成列表，在应用后面、动作行前面
            var settings = result.Items.Where(i => i.Kind == LauncherItemKind.Command && i.Group == LauncherGroups.Settings).Take(MaxSettings).ToList();
            if (settings.Count > 0)
            {
                var s = Section(Strings.Launcher_Group_Settings, LauncherSectionKind.List);
                foreach (var item in settings) Add(s, item, LauncherCell.Command);
            }
            var actions = ActionItems();
            if (actions.Count > 0)
            {
                var s = Section(string.Format(Strings.Launcher_AttachmentTarget, (object?)((q.Length > 24 ? q[..24] + "…" : q))), LauncherSectionKind.Tiles);
                for (int i = 0; i < actions.Count; i++) Add(s, actions[i], LauncherCell.Action, i < 9 ? (i + 1).ToString() : null);
            }
            // 常用（设置里加的文件 / 文件夹）在前，再是 Everything 的前 3 个
            var favorites = result.Items.Where(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder && i.Group is not (LauncherGroups.Files or LauncherGroups.Direct)).ToList();
            var files = result.Items.Where(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder && i.Group == LauncherGroups.Files).ToList();
            if (favorites.Count + files.Count > 0)
            {
                var s = Section(Strings.Launcher_Group_Files, LauncherSectionKind.List);
                foreach (var item in favorites.Concat(files.Take(MaxFiles))) Add(s, item, LauncherCell.File);
                if (Pane.Views.Files.SystemIcons.Enabled) foreach (var r in s.Rows) r.SetSystemIcon(FileIcon(r));
                int total = Math.Max(result.FileTotal, files.Count);
                if (total > MaxFiles)
                    Add(s, new("files:more", LauncherItemKind.Action, LauncherGroups.Files, string.Format(Strings.Launcher_AllFileResults, (object?)(total)), null, new("external")), LauncherCell.More);
            }
        }

        lastResult = result;
        if (result.IsHome && !attachmentMode) homeResult = result;
        Sections.ItemsSource = sections;
        ResultsArea.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Foot.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ChangeView(null, 0, null, true);
        highlight.Hide();
        selected = -1;
        UpdateLayout();
        int kept = keep is null ? -1 : rows.FindIndex(r => r.Item.Id == keep);
        Select(kept >= 0 ? kept : DefaultSelection(), scroll: false);
        UpdateBadge(attachmentMode ? null : KeywordEngine(query));
        RequestHeight();
    }

    // 默认选中：算式 > 第一个应用 > 第一个文件 > 第一个动作（翻译）。动作行位置固定，不因为默认选中挪位置
    int DefaultSelection()
    {
        int Find(Func<LauncherRow, bool> f) => rows.FindIndex(r => f(r));
        int i = Find(r => r.Cell is LauncherCell.Calc or LauncherCell.App or LauncherCell.Command or LauncherCell.Attachment);
        if (i < 0) i = Find(r => r.Cell == LauncherCell.File);
        return i < 0 ? 0 : i;
    }

    void RequestHeight()
    {
        double h = noticeHeight + SearchRowHeight;
        if (aiMode) { UpdateLayout(); h += 1 + Card.ActualHeight + Card.Margin.Top + Card.Margin.Bottom; }
        else if (rows.Count > 0) h += 1 + Math.Min(ResultsContent.ActualHeight, ResultsMaxHeight) + FootHeight;
        h = Math.Ceiling(h);
        if (Math.Abs(h - lastHeight) < 0.5 && Math.Abs(Width_ - lastWidth) < 0.5) return;
        lastHeight = h; lastWidth = Width_;
        SizeRequested?.Invoke(this, new Size(Width_, h));
    }

    // ---------------- 底部提示栏 ----------------

    void UpdateFoot()
    {
        if (selected < 0 || selected >= rows.Count) { FootWhat.Text = ""; FootKeys.Children.Clear(); return; }
        var row = rows[selected];
        var item = row.Item;
        var q = shownQuery;
        (string what, (string[], string)[] keys) = row.Cell switch
        {
            LauncherCell.Calc => (string.Format(Strings.Launcher_CopyTarget, (object?)(item.Title)), [(["Enter"], Strings.Common_Copy)]),
            LauncherCell.App => (string.Format(Strings.Launcher_OpenTarget, (object?)(item.Title)), [(["Enter"], Strings.Common_Open), (["Ctrl", "+", "Enter"], Strings.Common_OpenContainingFolder), (["Ctrl", "+", "K"], Strings.Launcher_MoreActions)]),
            LauncherCell.Menu when menuItem is { } target => (string.Format(Strings.Launcher_MenuTarget, (object?)(item.Title), (object?)(target.Title)), [(["Enter"], Strings.Common_Execute), (["Esc"], Strings.Common_Back)]),
            LauncherCell.Command when item.Id == "terminal:hint" => (Strings.Launcher_TerminalHelp, []),
            LauncherCell.Command when IsKeywordDirect(item) && DirectQuery(q) is { } d => ActionFoot(d.ActionId, item.Title, d.Text),
            LauncherCell.Command when item.Group == LauncherGroups.Terminal => (string.Format(Strings.Launcher_RunInTerminal, (object?)(item.Subtitle)), [(["Enter"], Strings.Common_Run), (["Ctrl", "+", "Shift", "+", "Enter"], Strings.Launcher_RunAsAdmin)]),
            LauncherCell.Command when item.Group == LauncherGroups.Completion => (string.Format(Strings.Launcher_RunTarget, (object?)(item.Title)), [(["Tab"], Strings.Launcher_FillInput), (["Enter"], Strings.Common_Run), (["Ctrl", "+", "Shift", "+", "Enter"], Strings.Launcher_AsAdmin)]),
            LauncherCell.Command when item.Id == "web:hint" => (item.Title, []),
            // 直达（098 / issue 52）：打开网址
            LauncherCell.Command when item.Group == LauncherGroups.Direct => (string.Format(Strings.Launcher_OpenTarget, (object?)(item.Title)), [(["Enter"], Strings.Common_Open)]),
            // 关键字搜网页（issue 43）：底栏写用哪个站点，不重复结果行；历史行多一个 Shift+Delete 删除
            LauncherCell.Command when item.Kind == LauncherItemKind.Web && webEngine is { } we => (Strings.FormatName(Strings.Launcher_SearchUsing, we.Name),
                row.HistoryText is null ? [(["Enter"], Strings.Common_Search), (["Tab"], Strings.Launcher_FillInput)] : [(["Enter"], Strings.Common_Search), (["Tab"], Strings.Launcher_FillInput), (["Shift", "+", "Delete"], Strings.Common_Delete)]),
            LauncherCell.Command when item.Kind == LauncherItemKind.Web => (item.Hotkey is null ? string.Format(Strings.Launcher_SearchTarget, (object?)(item.Title)) : item.Title, [(["Enter"], Strings.Common_Search), (["Tab"], Strings.Launcher_FillInput)]),
            LauncherCell.Command when item.Group == LauncherGroups.Timer => (item.Title.TrimEnd('…'), [(["Enter"], item.Id == "power:panel" ? Strings.Common_Open : item.Id == "power:cancel" ? Strings.Common_Execute : Strings.Timer_Go)]),
            LauncherCell.Command when item.Group == LauncherGroups.Settings => (string.Format(Strings.Launcher_OpenTarget, (object?)(item.Title)), [(["Enter"], Strings.Common_Open)]),
            LauncherCell.Command => (item.Title, [(["Enter"], Strings.Common_Execute)]),
            LauncherCell.File => (string.Format(Strings.Launcher_OpenTarget, (object?)(item.Title)), [(["Enter"], Strings.Common_Open), (["Tab"], Strings.Launcher_ViewInFiles), (["Ctrl", "+", "K"], Strings.Launcher_MoreActions)]),
            LauncherCell.More => (string.Format(Strings.Launcher_SearchInFiles, (object?)(q)), [(["Enter"], Strings.Launcher_OpenFiles)]),
            LauncherCell.Action => ActionFoot(ActionId(item), item.Title, q),
            LauncherCell.Attachment when item.Id == "search-files" => (string.Format(Strings.Launcher_SearchFilesTarget, (object?)(FileQuery(attachment))), [(["Enter"], Strings.Launcher_OpenFiles)]),
            _ => (item.Title, [(["Enter"], Strings.Common_Execute)]),
        };
        // Tab 兜底的行，提示放得下（不到三个）时加「Tab 搜文件」；应用行已有三个，不加
        if (TabFallback && keys.Length < 3 && !keys.Any(k => k.Item1 is ["Tab"]) && row.Cell is LauncherCell.Calc or LauncherCell.Command or LauncherCell.Action
            && item.Id != "terminal:hint" && !IsKeywordDirect(item) && item.Group is not (LauncherGroups.Terminal or LauncherGroups.Completion or LauncherGroups.Timer or LauncherGroups.Direct)
            && !(row.Cell == LauncherCell.Action && ActionId(item) == "files"))
            keys = [.. keys, (["Tab"], Strings.Launcher_SearchFiles)];
        FootWhat.Text = what;
        KeyHints.Fill(FootKeys, keys, HintLabelProto, KbdProto);
    }

    // Tab 兜底进文件窗口：终端模式、带附件时不兜底（issue 35）
    bool TabFallback => attachment is null && !shownQuery.StartsWith('>');
    void TabToFiles(string q) { if (TabFallback) OpenFiles?.Invoke(q, null); }

    static (string, (string[], string)[]) ActionFoot(string id, string name, string q) => id switch
    {
        "ai:translate" => (string.Format(Strings.Launcher_TranslateTarget, (object?)(q)), [(["Enter"], Strings.Common_Translate), (["Tab"], Strings.Launcher_OpenTranslate)]),
        "ai:ask" => (string.Format(Strings.Launcher_AskTarget, (object?)(q)), [(["Enter"], Strings.Launcher_AskAi), (["Tab"], Strings.Launcher_OpenChat)]),
        "files" => (string.Format(Strings.Launcher_SearchFilesTarget, (object?)(q)), [(["Enter"], Strings.Launcher_OpenFiles)]),
        _ when id.StartsWith("web:", StringComparison.Ordinal) => (Strings.FormatName(Strings.Launcher_SearchWithQuery, name, q), [(["Enter"], Strings.Common_Search)]),
        _ => ($"{name}「{q}」", [(["Enter"], name)]),
    };

    // ---------------- AI ----------------

    void RunAi(string actionId, string? query = null)
    {
        var q = query ?? Input.Text.Trim();   // 关键字直达传进来的是去掉关键字的内容
        var text = attachment is { } a ? AttachmentText(a) : q;
        if (actionId == "ask" && q.Length == 0 && attachment is null) return;
        if (text.Length == 0 && actionId != "ask") return;
        var act = ai.Actions.FirstOrDefault(x => x.Id == actionId);
        var title = actionId == "ask" ? Strings.Launcher_AskAi : act?.Name ?? actionId;
        var glyph = actionId == "ask" ? "ai" : act?.Glyph ?? "ai";
        // 原型：问 AI 带附件时显示「附件：…」；预设动作显示被处理的原文
        var source = actionId == "ask" ? (attachment is not null ? string.Format(Strings.Launcher_AttachmentText, text) : null) : text;
        var request = new AiRequest(actionId, actionId == "ask" ? (attachment is not null ? text : "") : text, null, actionId == "ask" ? q : null);
        cardRequest = request;
        cardTitle = title;

        aiMode = true;
        highlight.Hide();
        ShowNumbers(false);
        Scroller.Visibility = Visibility.Collapsed;
        Foot.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        ResultsArea.Visibility = Visibility.Visible;
        Card.HeightBudget = AiMaxHeight - Card.Margin.Top - Card.Margin.Bottom;
        Card.Start(ai, request, title, glyph, source, compact: false,
            [(["Enter"], Strings.Common_Paste), (["Ctrl", "+", "C"], Strings.Common_Copy), (["Ctrl", "+", "R"], Strings.Common_Regenerate),
             (["Tab"], actionId == "translate" ? Strings.Launcher_TranslateWindow : Strings.Launcher_ContinueChat), (["Esc"], Strings.Common_Back)],
            openSettings: () => { CloseRequested?.Invoke(this, EventArgs.Empty); ((App)Application.Current).OpenSettings("ai"); });
        RequestHeight();
    }

    void ExitAi()
    {
        if (!aiMode) return;
        aiMode = false;
        Card.Cancel();
        Card.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        ResultsArea.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Foot.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
        if (selected >= 0 && selected < rows.Count && ContainerOf(rows[selected]) is { } el) highlight.MoveTo(el, animate: false);
        RequestHeight();
    }

    // 卡片里按 Tab：翻译 → 翻译窗口；其余 → AI 对话窗口，带上这一问一答（没答完就只带问题，让对话窗口重新问）
    void ContinueCard()
    {
        if (cardRequest is not { } r) return;
        if (r.ActionId == "translate") { OpenTranslate?.Invoke(r.Text); return; }
        var question = r.ActionId == "ask"
            ? (r.Text.Length > 0 ? string.Format(Strings.Launcher_QuestionWithAttachment, (object?)(r.Question), (object?)(r.Text)) : r.Question ?? "")
            : $"{cardTitle}：\n{r.Text}";
        if (Card.IsDone && Card.Result.Length > 0) OpenChat?.Invoke(null, [new(AiChatRole.User, question), new(AiChatRole.Assistant, Card.Result)]);
        else OpenChat?.Invoke(question, null);
    }

    async Task PasteResult()
    {
        var text = Card.Result;
        if (text.Length == 0) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try { await output.ReplaceSelectionAsync(text, previousWindow, CancellationToken.None); }
        catch (Exception e) { ((App)Application.Current).ReportFailure(Strings.Common_Paste, e); }
    }

    // ---------------- 选中 ----------------

    void Select(int index, bool scroll = true)
    {
        if (rows.Count == 0) { selected = -1; highlight.Hide(); UpdateFoot(); return; }
        index = Math.Clamp(index, 0, rows.Count - 1);
        if ((armed ?? failed) is { } marked && !ReferenceEquals(marked, rows[index])) Disarm();   // 换了选中项就取消待确认
        if (selected >= 0 && selected < rows.Count) rows[selected].ShowRemove = false;
        selected = index;
        rows[selected].ShowRemove = true;   // 历史行选中（含鼠标悬停）时出 ×
        UpdateFoot();
        if (ContainerOf(rows[selected]) is not { } el) return;
        highlight.MoveTo(el);
        if (scroll) ScrollIntoView(el);
    }

    FrameworkElement? ContainerOf(LauncherRow row)
    {
        var section = sections.FirstOrDefault(s => s.Rows.Contains(row));
        if (section is null || Sections.ContainerFromItem(section) is not DependencyObject outer) return null;
        return FindDescendant<ItemsControl>(outer)?.ContainerFromItem(row) as FrameworkElement;
    }

    void ScrollIntoView(FrameworkElement el)
    {
        var top = el.TransformToVisual(ResultsContent).TransformPoint(new Point(0, 0)).Y;
        var bottom = top + el.ActualHeight;
        var view = Scroller.ViewportHeight;
        var y = Scroller.VerticalOffset;
        if (top - 8 < y) Scroller.ChangeView(null, Math.Max(0, top - 8), null);
        else if (bottom + 8 > y + view) Scroller.ChangeView(null, bottom + 8 - view, null);
    }

    // 上下键：走到上一行 / 下一行里横向（中心）最近的一项。格子和列表混排都成立（原型 move）
    void MoveLine(int dir)
    {
        if (selected < 0 || ContainerOf(rows[selected]) is not { } cur) return;
        var c = Center(cur);
        var cands = rows.Select(r => (r, el: ContainerOf(r))).Where(x => x.el is not null)
            .Select(x => (x.r, p: Center(x.el!), top: Top(x.el!)))
            .Where(x => dir > 0 ? x.top > Top(cur) + 4 : x.top < Top(cur) - 4).ToList();
        if (cands.Count == 0) return;
        var line = dir > 0 ? cands.Min(x => x.top) : cands.Max(x => x.top);
        var best = cands.Where(x => Math.Abs(x.top - line) < 4).OrderBy(x => Math.Abs(x.p - c)).First();
        Select(best.r.Index);

        double Top(FrameworkElement e) => e.TransformToVisual(ResultsContent).TransformPoint(new Point(0, 0)).Y;
        double Center(FrameworkElement e) => e.TransformToVisual(ResultsContent).TransformPoint(new Point(e.ActualWidth / 2, 0)).X;
    }

    // 左右键：在同一行格子里走，到行首再按左回到行尾、到行尾再按右回到行首（issue 6）
    void MoveInLine(int dir)
    {
        if (selected < 0 || ContainerOf(rows[selected]) is not { } cur) return;
        var section = sections.FirstOrDefault(s => s.Rows.Contains(rows[selected]));
        if (section is null) return;
        double top = TopOf(cur);
        var line = section.Rows.Where(r => ContainerOf(r) is { } el && Math.Abs(TopOf(el) - top) < 4).OrderBy(r => r.Index).ToList();
        if (line.Count < 2) return;
        int i = line.FindIndex(r => r.Index == selected);
        Select(line[(i + dir + line.Count) % line.Count].Index);
    }

    double TopOf(FrameworkElement e) => e.TransformToVisual(ResultsContent).TransformPoint(new Point(0, 0)).Y;

    // issue 50：设置「打字时左右键移动光标」打开时，有输入时左右键移动输入框里的光标；按过 ↑ ↓ 进入结果区之后才在格子之间切换，
    // 再打字就回到移动光标。关着（默认）照原来，左右键直接切换格子。输入为空（首页图块）时没有光标可移，左右键一直切换格子
    bool navigating;
    bool ArrowsMoveTiles => SelectedIsTile && (!Options.ArrowKeysMoveCaret || navigating || Input.Text.Length == 0);
    bool SelectedIsTile => selected >= 0 && selected < rows.Count && sections.FirstOrDefault(s => s.Rows.Contains(rows[selected]))?.Kind == LauncherSectionKind.Tiles;

    void ShowNumbers(bool show) { foreach (var r in rows) r.ShowNumber = show; }

    // ---------------- 键盘与鼠标 ----------------

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Down(VirtualKey.Control);

        if (aliasItem is not null)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter: SaveAlias(); break;
                case VirtualKey.Escape: ExitAlias(); break;
                case VirtualKey.Up or VirtualKey.Down or VirtualKey.Tab: break;
                default: return;   // 其他键照常打字
            }
            e.Handled = true;
            return;
        }

        if (aiMode)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter: _ = PasteResult(); break;
                case VirtualKey.C when ctrl && Card.Result.Length > 0 && Input.SelectionLength == 0:
                    _ = output.CopyAsync(Card.Result, CancellationToken.None); break;
                case VirtualKey.R when ctrl: Card.Retry(); break;
                case VirtualKey.Tab: ContinueCard(); break;
                case VirtualKey.Escape: ExitAi(); break;
                // 结果长了用 ↑ ↓ / PageUp PageDown 翻看；往上翻就不再自动滚到底
                case VirtualKey.Up: Card.ScrollBy(-72); break;
                case VirtualKey.Down: Card.ScrollBy(72); break;
                case VirtualKey.PageUp: Card.ScrollBy(-Card.PageHeight); break;
                case VirtualKey.PageDown: Card.ScrollBy(Card.PageHeight); break;
                default: return; // 其他键照常打字（打字会回到列表）
            }
            e.Handled = true;
            return;
        }

        // 操作列表：数字键直接执行，Esc 回到结果；上下、Enter、点一下照常
        if (menuItem is not null)
        {
            switch (e.Key)
            {
                case >= VirtualKey.Number1 and <= VirtualKey.Number9 when e.Key - VirtualKey.Number1 < rows.Count:
                    Select(e.Key - VirtualKey.Number1); _ = Execute(selected, false, false); break;
                case VirtualKey.Escape: CloseMenu(); break;
                case VirtualKey.K when ctrl: CloseMenu(); break;
                case VirtualKey.Tab or VirtualKey.Left or VirtualKey.Right: break;
                case VirtualKey.Down: MoveLine(1); break;
                case VirtualKey.Up: MoveLine(-1); break;
                case VirtualKey.Enter: _ = Execute(selected, false, false); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        // 按住 Alt：动作格子出数字；Alt+数字直接触发
        if (e.Key == VirtualKey.Menu) { ShowNumbers(true); e.Handled = true; return; }
        if (Down(VirtualKey.Menu) && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9)
        {
            var n = (e.Key - VirtualKey.Number1 + 1).ToString();
            if (rows.FirstOrDefault(r => r.Cell == LauncherCell.Action && r.Number == n) is { } hit) { Select(hit.Index); _ = Execute(hit.Index, false, false); }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.K when ctrl: _ = OpenMenu(selected); break;
            case VirtualKey.Down: navigating = true; MoveLine(1); break;
            case VirtualKey.Up: navigating = true; MoveLine(-1); break;
            case VirtualKey.Right when ArrowsMoveTiles: MoveInLine(1); break;
            case VirtualKey.Left when ArrowsMoveTiles: MoveInLine(-1); break;
            case VirtualKey.Enter: _ = Execute(selected, false, ctrl, Down(VirtualKey.Shift)); break;
            case VirtualKey.Tab: _ = Execute(selected, true, false); break;
            case VirtualKey.Delete when Down(VirtualKey.Shift) && selected >= 0 && selected < rows.Count && rows[selected].HistoryText is not null:
                RemoveHistory(rows[selected]); break;   // 网页搜索历史行：Shift+Delete 删掉这条
            case VirtualKey.Back when Input.Text.Length == 0 && attachment is not null:
                SetAttachment(null); _ = Refresh(string.Empty); break; // 空输入时退格去掉附件
            case VirtualKey.Escape:
                // Esc 分步：有输入先清空；有附件去掉附件；最后收起
                if (Input.Text.Length > 0) Input.Text = string.Empty;
                else if (attachment is not null) { SetAttachment(null); _ = Refresh(string.Empty); }
                else CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            default: return;
        }
        e.Handled = true;
    }

    void OnInputKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Menu) { ShowNumbers(false); e.Handled = true; }
    }

    void OnInputLostFocus(object sender, RoutedEventArgs e) => ShowNumbers(false);

    void OnResultsPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { } row && row.Index != selected) Select(row.Index, scroll: false);
    }

    void OnResultsTapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { } row) _ = Execute(row.Index, false, false);
    }

    void OnResultsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (menuItem is not null || RowFrom(e.OriginalSource) is not { } row) return;
        Select(row.Index, scroll: false);
        _ = OpenMenu(row.Index);
        e.Handled = true;
    }

    static LauncherRow? RowFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: LauncherRow row }) return row;
        return null;
    }

    // 关机、重启：第一次 Enter 只把副标题换成确认提示，3 秒内再按一次才执行（013 约定由界面确认）
    LauncherRow? armed;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? armTimer;
    static bool NeedsConfirm(LauncherItem item) => item.Kind == LauncherItemKind.Command && item.Id is "command:shutdown" or "command:restart" or "command:logoff";

    LauncherRow? failed;

    void Disarm()
    {
        armTimer?.Stop();
        armed?.SetWarning(null);
        armed = null;
        failed?.SetWarning(null);
        failed = null;
        if (selected >= 0) UpdateFoot();
    }

    void ShowFailure(LauncherRow row, string text)
    {
        Disarm();
        failed = row;
        row.SetWarning(text);
        if (row.Index == selected) FootWhat.Text = text;   // 格子没有副标题，失败原因写在提示栏
        StartArmTimer(TimeSpan.FromSeconds(5));
    }

    static string Verb(LauncherItem item) => item.Kind switch
    {
        LauncherItemKind.App or LauncherItemKind.File or LauncherItemKind.Folder or LauncherItemKind.Web => Strings.Common_Open,
        LauncherItemKind.Calculation => Strings.Common_Copy,
        _ => item.Title,
    };

    bool Confirmed(LauncherRow row)
    {
        if (ReferenceEquals(armed, row)) { Disarm(); return true; }
        Disarm();
        armed = row;
        row.SetWarning(string.Format(Strings.Launcher_ConfirmAction, (object?)(row.Item.Title)));
        if (row.Index == selected) FootWhat.Text = string.Format(Strings.Launcher_ConfirmAction, (object?)(row.Item.Title));
        StartArmTimer(TimeSpan.FromSeconds(3));
        return false;
    }

    void StartArmTimer(TimeSpan interval)
    {
        armTimer ??= DispatcherQueue.CreateTimer();
        armTimer.Interval = interval;
        armTimer.IsRepeating = false;
        armTimer.Tick -= OnArmExpired;
        armTimer.Tick += OnArmExpired;
        armTimer.Start();
    }

    void OnArmExpired(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => Disarm();

    // 文件条目的完整路径：服务的 Id 是 "file:路径" / "favorite:路径"；假数据没有前缀，用「位置 + 名字」拼
    static string FilePath(LauncherItem item)
    {
        foreach (var prefix in new[] { "file:", "favorite:" })
            if (item.Id.StartsWith(prefix, StringComparison.Ordinal)) return item.Id[prefix.Length..];
        return System.IO.Path.Combine(item.Subtitle ?? "", item.Title);
    }

    // 关键字直达（052）的动作项；098 的网址 / 路径直达（Group 为 Direct）不算，照常交给服务
    static bool IsKeywordDirect(LauncherItem item) => item.Id.StartsWith("direct:", StringComparison.Ordinal) && item.Group != LauncherGroups.Direct;

    // 网页关键字后面跟的是网址（issue 52，「go https://…」）：认法同服务的直达，只认 http / https 和 www. 开头
    static string? WebAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri.AbsoluteUri : null;
    }

    async Task Execute(int index, bool tab, bool ctrl, bool shift = false)
    {
        if (index < 0 || index >= rows.Count) return;
        var row = rows[index];
        var item = row.Item;
        var q = shownQuery;
        if (row.Cell == LauncherCell.Menu) { if (!tab) await RunMenu(row); return; }

        // 关键字直达（052）：和动作行同样的动作，只是内容去掉了关键字；Tab 照动作行展开成翻译 / 对话窗口
        if (IsKeywordDirect(item))
        {
            if (DirectQuery(q) is not { } d) return;
            switch (d.ActionId)
            {
                case "files": OpenFiles?.Invoke(d.Text, null); break;
                case "ai:translate" when tab: OpenTranslate?.Invoke(d.Text); break;
                case "ai:ask" when tab: OpenChat?.Invoke(d.Text, null); break;
                default: if (!tab) RunAi(d.ActionId[3..], d.Text); break;
            }
            return;
        }

        // Tab：补全项填进输入框（037）；网页建议填成「关键字 建议」接着改
        if (tab && item.Id.StartsWith("complete:", StringComparison.Ordinal)) { Fill(">" + item.Id["complete:".Length..]); return; }
        if (tab && item.Kind == LauncherItemKind.Web && KeywordQuery(q) is { } kw) { Fill($"{kw.Engine.Keyword} {(item.Hotkey is null ? item.Title : kw.Text)}"); return; }
        if (tab && row.HistoryText is { } past && webEngine is { } we) { Fill($"{we.Keyword} {past}"); return; }   // 只输入了关键字时的历史行
        if (item.Id is "terminal:hint" or "web:hint") return;   // 只有 > 时的提示项
        // 终端项 Ctrl+Shift+Enter：以管理员身份运行（走系统 UAC）
        if (ctrl && shift && item.Group is LauncherGroups.Terminal or LauncherGroups.Completion)
        {
            try { await search.ExecuteElevatedAsync(item, CancellationToken.None); CloseRequested?.Invoke(this, EventArgs.Empty); }
            catch (Exception e) { if (App.FailureReason(e) is { } reason) ShowFailure(row, string.Format(Strings.Launcher_RunFailed, (object?)(reason))); }
            return;
        }

        // 应用：Ctrl+Shift+Enter 以管理员身份运行，Ctrl+Enter 打开所在文件夹（和终端、文件窗口一致，050）
        if (ctrl && row.Cell == LauncherCell.App && search is ILauncherItemActions direct)
        {
            try { await direct.RunAsync(item, shift ? LauncherItemAction.RunAsAdmin : LauncherItemAction.OpenLocation, CancellationToken.None); }
            catch (Exception e) { MenuFailed(row, e); return; }
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 展开成二级窗口的几种
        switch (row.Cell)
        {
            case LauncherCell.More:
                OpenFiles?.Invoke(q, null); return;
            case LauncherCell.File when tab:
                OpenFiles?.Invoke(q, FilePath(item)); return;
            case LauncherCell.File when ctrl:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                try { if (Files is { } f) await f.RevealAsync(FilePath(item), CancellationToken.None); }
                catch (Exception e) { ((App)Application.Current).ReportFailure(Strings.Common_OpenContainingFolder, e); }
                return;
            case LauncherCell.Attachment when item.Id == "search-files":
                OpenFiles?.Invoke(FileQuery(attachment), null); return;
            case LauncherCell.Action:
                var id = ActionId(item);
                if (id == "files") { OpenFiles?.Invoke(q, null); return; }
                if (id == "ai:translate" && tab) { OpenTranslate?.Invoke(q); return; }
                if (id == "ai:ask" && tab) { OpenChat?.Invoke(q, null); return; }
                if (tab) { TabToFiles(q); return; }
                if (id.StartsWith("ai:", StringComparison.Ordinal)) { RunAi(id[3..]); return; }
                if (id.StartsWith("web:", StringComparison.Ordinal) && Engine(id) is { } engine)
                {
                    var url = engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(q), StringComparison.Ordinal);
                    item = new("web:" + url, LauncherItemKind.Web, LauncherGroups.Web, item.Title, null, item.Icon);
                    break;
                }
                return;
            default:
                // Tab 只展开二级窗口，不执行这一项（不误触发关机这类命令）；没有自己二级窗口的项，Tab 带着输入进文件窗口（issue 35）
                if (tab) { TabToFiles(q); return; }
                break;
        }

        // 定时关机（058）：「定时关机…」由界面打开定时窗口；定好 / 取消照常走服务
        if (item.Id == "power:panel")
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            OpenTimer?.Invoke();
            return;
        }
        // 批量重命名：命令（040，Id = rename）和文件附件的动作都由界面打开重命名窗口
        if (item.Id == "rename" && item.Kind is LauncherItemKind.Command or LauncherItemKind.Action)
        {
            var files = attachment is { Kind: ClipKind.Files, Files: { } f } ? f : null;
            var window = previousWindow;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            OpenRename?.Invoke(files, window);
            return;
        }
        if (NeedsConfirm(item) && !Confirmed(row)) return;
        if (item.Kind == LauncherItemKind.Ai) { RunAi(item.Id.StartsWith("ai:", StringComparison.Ordinal) ? item.Id[3..] : item.Id); return; }
        try
        {
            if (attachment is { } a && item.Kind == LauncherItemKind.Action)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                try { await attachmentActions.RunAsync(item.Id, a, CancellationToken.None); }
                catch (Exception e) { ((App)Application.Current).ReportFailure(item.Title, e); } // 面板已收起：屏幕中间提示
                return;
            }
            await search.ExecuteAsync(item, CancellationToken.None);
        }
        catch (Exception e)
        {
            // 启动器还开着：原因写在这一项的副标题上（格子写在提示栏），几秒后或换选中项时复原
            if (App.FailureReason(e) is { } reason && index < rows.Count && ReferenceEquals(rows[index], row))
                ShowFailure(row, string.Format(Strings.Common_ActionFailed, (object?)(Verb(item)), (object?)(reason)));
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 附加操作（050，issue 10） ----------------
    // Ctrl+K / 右键：结果区就地换成这一项的操作列表（WinUI 版不弹独立菜单，同批量重命名的「就地展开」）。
    // 第一行「打开」，应用第二行「固定到首页 / 取消固定」（issue 27），后面是服务给的操作，右边数字键；卸载要按两次。Esc 回到结果并选回这一项

    async Task OpenMenu(int index)
    {
        if (aiMode || menuItem is not null || index < 0 || index >= rows.Count) return;
        var row = rows[index];
        if (row.Cell is not (LauncherCell.App or LauncherCell.File)) return;
        bool pinnable = row.Item.Kind == LauncherItemKind.App;   // 首页图块也是应用；命令、文件不能固定
        IReadOnlyList<LauncherItemAction> list = [];
        if (search is ILauncherItemActions actions)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try { list = await actions.GetActionsAsync(row.Item, cts.Token).WaitAsync(cts.Token); }
            catch (Exception) { list = []; }
        }
        if (aiMode || menuItem is not null || index >= rows.Count || !ReferenceEquals(rows[index], row)) return;   // 等的时候结果换了
        if (list.Count == 0 && !pinnable) { if (index == selected) FootWhat.Text = Strings.Launcher_NoMoreActions; return; }

        Disarm();
        menuItem = row.Item;
        menuEntries = [new(), .. pinnable ? [new MenuEntry(Pin: true)] : Array.Empty<MenuEntry>(), .. list.Select(a => new MenuEntry(a))];
        var s = new LauncherSection(row.Item.Title, LauncherSectionKind.List, []);
        sections = [s];
        rows = [];
        foreach (var entry in menuEntries)
        {
            var n = (rows.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (title, glyph) = MenuLabel(entry, row.Item);
            var r = new LauncherRow(new("menu:" + n, LauncherItemKind.Action, LauncherGroups.Actions, title, null, new(glyph), Hotkey: rows.Count < 9 ? n : null), rows.Count, LauncherCell.Menu);
            rows.Add(r);
            s.Rows.Add(r);
        }
        Sections.ItemsSource = sections;
        Scroller.ChangeView(null, 0, null, true);
        highlight.Hide();
        selected = -1;
        UpdateLayout();
        Select(0, scroll: false);
        RequestHeight();
    }

    static (string Title, string Glyph) MenuLabel(MenuEntry entry, LauncherItem item) => entry.Pin
        ? IsPinned(item) ? (Strings.Launcher_UnpinFromHome, "pin-off") : (Strings.Launcher_PinToHome, "pin")
        : entry.Action switch
    {
        null => (Strings.Common_Open, "external"),
        LauncherItemAction.RunAsAdmin => (Strings.Launcher_RunAsAdmin, "shield"),
        LauncherItemAction.OpenLocation => (Strings.Common_OpenContainingFolder, "folder"),
        LauncherItemAction.CopyPath => (Strings.Common_CopyPath, "copy"),
        LauncherItemAction.Properties => (Strings.Launcher_Properties, "info"),
        LauncherItemAction.RemoveFromApps => (Strings.Launcher_RemoveFromApps, "eye-off"),   // 057：不删文件，只是不再列出
        LauncherItemAction.EditAliases => (Strings.Launcher_EditAliases, "hash"),           // 078 / issue 33
        LauncherItemAction.RemoveFromRecent => (Strings.Launcher_RemoveFromRecent, "eraser"), // 只删使用记录，应用照常能搜到
        LauncherItemAction.HideFromSearch => (Strings.Launcher_HideFromSearch, "eye-off"),   // 098 / issue 49：设置 → 启动器 →「已隐藏」里恢复
        LauncherItemAction.Uninstall => (Strings.Launcher_Uninstall, "trash"),
        _ => (Strings.Launcher_MoreActions, "info"),
    };

    void CloseMenu()
    {
        if (menuItem is not { } item) return;
        menuItem = null;
        Disarm();
        if (lastResult is { } result) Render(result, shownQuery, attachmentMode: false);
        int back = rows.FindIndex(r => r.Item.Id == item.Id);
        if (back >= 0) Select(back);
    }

    async Task RunMenu(LauncherRow row)
    {
        if (menuItem is not { } item || row.Index >= menuEntries.Count) return;
        if (menuEntries[row.Index].Pin) { await TogglePin(row, item); return; }
        var action = menuEntries[row.Index].Action;
        if (action == LauncherItemAction.EditAliases) { EnterAlias(item); return; }
        if (action == LauncherItemAction.Uninstall && !Confirmed(row)) return;
        try
        {
            if (action is not { } a) await search.ExecuteAsync(item, CancellationToken.None);
            else if (search is ILauncherItemActions actions) await actions.RunAsync(item, a, CancellationToken.None);
        }
        catch (Exception e) { MenuFailed(row, e); return; }
        // 从应用列表移除（057）：不收起启动器，回到结果重新搜，这一项就不在了
        if (action is LauncherItemAction.RemoveFromApps or LauncherItemAction.RemoveFromRecent or LauncherItemAction.HideFromSearch) { menuItem = null; Disarm(); _ = Refresh(Input.Text); return; }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // 固定到首页（issue 27）：界面直接改设置，搜索服务每次读最新设置。新固定的排最后。
    // 不收起启动器：回到结果、选回这一项，提示栏说一声；在首页取消固定时图块就从「已固定」里消失
    static bool IsPinned(LauncherItem item) => Options.PinnedApps.Contains(item.Id, StringComparer.Ordinal);

    async Task TogglePin(LauncherRow row, LauncherItem item)
    {
        var store = ((App)Application.Current).Store;
        var launcher = store.Current.Launcher;
        bool pin = !IsPinned(item);
        string[] pinned = pin ? [.. launcher.PinnedApps, item.Id] : [.. launcher.PinnedApps.Where(id => !string.Equals(id, item.Id, StringComparison.Ordinal))];
        try { store.Save(store.Current with { Launcher = launcher with { PinnedApps = pinned } }); }
        catch (Exception e) { ShowFailure(row, App.FailureReason(e) ?? Strings.Settings_SaveFailed); return; }
        menuItem = null;
        Disarm();
        await Refresh(Input.Text);
        if (menuItem is not null) return;   // 等的时候又打开了别的菜单
        int back = rows.FindIndex(r => r.Item.Id == item.Id);
        if (back >= 0) Select(back);
        FootWhat.Text = pin ? Strings.Launcher_PinnedToHome : Strings.Launcher_UnpinnedFromHome;
    }

    // ---------------- 搜索别名（078 / issue 33） ----------------
    // 菜单选「设置搜索别名」：输入框换成这个应用现有的别名（空格隔开），输入框左边的标签显示应用名；
    // 列表只留这一个应用；Enter 保存、Esc 取消，都回到原来的搜索、选回这一项。
    LauncherItem? aliasItem;
    string aliasReturnQuery = "";

    void EnterAlias(LauncherItem item)
    {
        if (search is not IAppAliases aliases) return;
        menuItem = null;
        Disarm();
        aliasReturnQuery = Input.Text;
        aliasItem = item;
        AttachIcon.Glyph = "hash";
        AttachText.Text = item.Title;
        AttachChip.Visibility = Visibility.Visible;
        PlayAttachIn();
        Input.PlaceholderText = Strings.Launcher_AliasPlaceholder;
        Render(new LauncherResult([item], false), aliasReturnQuery, attachmentMode: false);
        Input.Text = string.Join(" ", aliases.Get(item.Id));
        Input.SelectAll();
        Input.Focus(FocusState.Programmatic);
        AliasFoot();
    }

    void AliasFoot(string? message = null)
    {
        if (aliasItem is not { } item) return;
        FootWhat.Text = message ?? string.Format(Strings.Launcher_AliasEditing, (object?)item.Title);
        KeyHints.Fill(FootKeys, [(["Enter"], Strings.Common_Save), (["Esc"], Strings.Common_Cancel)], HintLabelProto, KbdProto);
    }

    void SaveAlias()
    {
        if (aliasItem is not { } item || search is not IAppAliases aliases) return;
        var names = Input.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 8) { AliasFoot(Strings.Launcher_AliasTooMany); return; }
        if (names.Any(n => n.Length > 32)) { AliasFoot(Strings.Launcher_AliasTooLong); return; }
        try { aliases.Set(item.Id, names); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { AliasFoot(App.FailureReason(e) ?? Strings.Settings_SaveFailed); return; }
        _ = ExitAlias(string.Format(names.Length > 0 ? Strings.Launcher_AliasSaved : Strings.Launcher_AliasCleared, (object?)item.Title));
    }

    void ExitAlias() => _ = ExitAlias(null);

    async Task ExitAlias(string? done)
    {
        if (aliasItem is not { } item) return;
        aliasItem = null;
        SetAttachment(attachment, animate: false);
        Input.Text = aliasReturnQuery;   // 触发重新搜索
        Input.SelectionStart = Input.Text.Length;
        await Refresh(Input.Text);
        int back = rows.FindIndex(r => r.Item.Id == item.Id);
        if (back >= 0) Select(back);
        if (done is not null) FootWhat.Text = done;
    }

    // 失败原因写在这一行；服务给的 LauncherActionException 本身就是给用户看的一句话；UAC 点「否」服务已经静默
    void MenuFailed(LauncherRow row, Exception e)
    {
        var reason = e is LauncherActionException ? e.Message : App.FailureReason(e);
        if (reason is not null && row.Index < rows.Count && ReferenceEquals(rows[row.Index], row)) ShowFailure(row, reason);
    }

    // 系统文件图标（036）：没缓存的后台去取，好了补到还显示着的那一行
    static Pane.Contracts.IFileIcons Icons => ((App)Application.Current).FileIcons;
    static string? FileIcon(LauncherRow r) => r.Cell == LauncherCell.File ? Icons.TryGet(FilePath(r.Item), r.Item.Kind == LauncherItemKind.Folder) : null;
    void OnIconReady(object? sender, string path) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!Pane.Views.Files.SystemIcons.Enabled) return;
        foreach (var r in rows)
            if (r.Cell == LauncherCell.File && string.Equals(FilePath(r.Item), path, StringComparison.OrdinalIgnoreCase)) r.SetSystemIcon(FileIcon(r));
    });

    void Fill(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
        Input.Focus(FocusState.Programmatic);
    }

    // ---------------- 小工具 ----------------

    // TextBox 自带的「清空」叉号和原型不符，收掉。它在获得焦点、有字时才延迟创建，样式里还有 MinWidth，
    // 所以排到这一轮布局之后再找，并把 MinWidth、透明度、点击一起去掉（只设 Width = 0 压不住）
    void HideClearButton() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        if (FindDescendant<Button>(Input, "DeleteButton") is { } b && b.Width != 0) { b.MinWidth = 0; b.Width = 0; b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false; }
    });

    static T? FindDescendant<T>(DependencyObject root, string? name = null) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && (name is null || t.Name == name)) return t;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// 可重启的一次性计时器（输入防抖）。视图永久销毁时要调 Dispose：DispatcherQueueTimer 不参与 XAML 的引用跟踪，
/// 「计时器 → Tick 处理函数 → 视图 → 计时器」这个环 GC 收不掉，视图连同整棵原生树一直留着（每开关一次窗口 1～2 MiB）。
/// </summary>
sealed class DispatcherQueueTimerHolder : IDisposable
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    readonly Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object> handler;
    public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        handler = (_, _) => tick();
        timer.Tick += handler;
    }
    public void Restart() { timer.Stop(); timer.Start(); }
    public void Stop() => timer.Stop();
    public void Dispose() { timer.Stop(); timer.Tick -= handler; }
}
