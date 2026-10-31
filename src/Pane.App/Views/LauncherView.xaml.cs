// 启动器视图逻辑（设计负责人）。交互照原型 prototype/launcher.html（2026-10-29 新排法）：
// 空输入显示「最近使用 / 已固定」格子；输入时按内容分排法：算式卡片 → 应用格子 → 动作行 → 文件列表（前 3 个 + 「查看全部」）。
// 方向键：左右按顺序走（只在选中格子时；选中列表行时留给输入框），上下走到上一行 / 下一行里横向最近的一项。
// 底部提示栏写明选中项按 Enter / Tab / Ctrl+Enter 各做什么。按住 Alt 动作格子出数字，Alt+数字直接触发。
// Tab：动作「翻译 / 问 AI / 搜文件」、文件、「查看全部」展开成二级窗口（宿主接），带上输入。
// 附件：复制后 3 秒内呼出自动带入，或从剪贴板「交给启动器」；有附件时列表换成对附件的动作。
// AI：选 AI 项后结果卡片代替列表；Enter 粘贴回原窗口、Ctrl+C 复制、Ctrl+R 重新生成、Tab 继续对话（翻译是翻译窗口）。
// Esc 分步：AI 卡片 → 回到列表；有输入 → 清空；有附件 → 去掉；最后收起（docs/design.md「Esc 分步」）。
using System.Numerics;
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

public sealed partial class LauncherView : UserControl, IPanelView
{
    const double Width_ = 592, SearchRowHeight = 60, ResultsMaxHeight = 452, FootHeight = 34;
    const int MaxApps = 14, MaxFiles = 3;
    const string Placeholder = "搜索应用、文件，或输入任何文字";
    // 设置 → 启动器：「复制后自动带入」时长、「再次呼出时」怎么处理上次输入、动作行
    static Settings.LauncherSettings Options => ((App)Application.Current).Store.Current.Launcher;
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
    double lastHeight;
    ClipEntry? attachment, pendingAttachment;
    nint previousWindow;
    string shownQuery = "";
    AiRequest? cardRequest;
    string cardTitle = "";

    /// <summary>文件窗口：查询文字、要选中的文件路径（文件格按 Tab 时）。宿主负责创建或复用窗口。</summary>
    public event Action<string, string?>? OpenFiles;
    /// <summary>翻译窗口：带上要翻译的文字。</summary>
    public event Action<string>? OpenTranslate;
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

        highlight = new SlidingHighlight(HighlightLayer);
        Loaded += (_, _) => HideClearButton();
        Input.GotFocus += (_, _) => HideClearButton();
        Sections.PointerMoved += OnResultsPointerMoved;
        Sections.Tapped += OnResultsTapped;
        Card.ContentGrew += (_, _) => RequestHeight();
        Card.SizeChanged += (_, _) => RequestHeight();
    }

    /// <summary>剪贴板「交给启动器」：宿主先调用这个，再显示启动器。</summary>
    public void AttachNext(ClipEntry entry) => pendingAttachment = entry;

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(Width_, SearchRowHeight);
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => SearchRow; // 宿主只在没有可交互控件（输入框、附件的叉）的位置开始拖动

    public void OnShowing(PanelShowContext context)
    {
        activate = context.Activate;
        previousWindow = context.PreviousForegroundWindow;
        ExitAi();
        // 附件：交过来的优先；否则复制后几秒内呼出（设置里定，0 = 关），带入刚复制的内容（不清空系统剪贴板）
        var attach = pendingAttachment;
        pendingAttachment = null;
        int seconds = Options.AutoAttachSeconds;
        if (attach is null && seconds > 0 && clipboard.Snapshot().OrderByDescending(e => e.CopiedAt).FirstOrDefault() is { } newest
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
        var text = Environment.GetEnvironmentVariable("PANE_DESIGN_QUERY") ?? (keep ? Input.Text : string.Empty);
        if (Input.Text != text) Input.Text = text;
        else _ = Refresh(text);
        if (text.Length > 0) Input.SelectAll();
    }

    public void OnShown()
    {
        if (activate) Input.Focus(FocusState.Programmatic);
        // 设计预览：PANE_DESIGN_LAUNCHER_RUN 指定一呼出就执行的条目 Id（如 act:ai:translate）
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_LAUNCHER_RUN") is { Length: > 0 } id
            && rows.FindIndex(r => r.Item.Id == id) is var i and >= 0) _ = Execute(i, false, false);
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
        Input.PlaceholderText = "对附件做什么？";
        if (animate) PlayAttachIn();
    }

    static string AttachmentLabel(ClipEntry e) => e.Kind switch
    {
        ClipKind.Files => string.Join("、", (e.Files ?? []).Select(System.IO.Path.GetFileName)),
        ClipKind.Image => $"{e.Text} {e.ImageWidth}×{e.ImageHeight}",
        _ => (e.Text ?? "").ReplaceLineEndings(" "),
    };

    static string AttachmentText(ClipEntry e) => e.Kind == ClipKind.Files ? string.Join("\n", e.Files ?? []) : e.Text ?? "";

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

    void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
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
            items.Add(new("open", LauncherItemKind.Action, "文件", "打开", System.IO.Path.GetFileName(first), new("folder")));
            items.Add(new("reveal", LauncherItemKind.Action, "文件", "打开所在文件夹", "文件资源管理器", new("folder")));
            items.Add(new("copy-path", LauncherItemKind.Action, "文件", "复制路径", $"{a.Files?.Count ?? 0} 个路径", new("link")));
        }
        else if (a.Kind == ClipKind.Image)
        {
            items.Add(new("save-image", LauncherItemKind.Action, "图片", "另存为…", AttachmentLabel(a), new("image")));
            items.Add(new("copy-image", LauncherItemKind.Action, "图片", "复制图片", "重新放回剪贴板", new("clipboard")));
        }
        else
        {
            foreach (var act in ai.Actions)
                items.Add(new("ai:" + act.Id, LauncherItemKind.Ai, "AI", act.Name,
                    act.Id == "translate" ? "自动判断：中文译成英文，其他译成中文" : "处理附件里的文字", new(act.Glyph)));
            items.Add(new("web-search", LauncherItemKind.Action, "其他", "网页搜索", "默认搜索引擎", new("globe")));
        }
        var q = query.Trim();
        if (q.Length > 0) items = items.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (q.Length > 0 && a.Kind != ClipKind.Image && a.Kind != ClipKind.Files)
            items.Add(new("ai:ask", LauncherItemKind.Ai, "AI", $"问 AI：“{q}”", "连同附件文字一起发送", new("ai")));
        return items;
    }

    // ---------------- 动作行 ----------------

    // 设置里的 ActionRow（顺序即显示顺序）→ 动作格子。认不出的 Id 跳过
    List<LauncherItem> ActionItems()
    {
        var items = new List<LauncherItem>();
        foreach (var id in Options.ActionRow)
        {
            (string name, string glyph)? a = id switch
            {
                "ai:translate" => ("翻译", "translate"),
                "ai:ask" => ("问 AI", "ai"),
                "files" => ("搜文件", "folder"),
                _ when id.StartsWith("ai:", StringComparison.Ordinal) && ai.Actions.FirstOrDefault(x => x.Id == id[3..]) is { } act => (act.Name, act.Glyph),
                _ when id.StartsWith("web:", StringComparison.Ordinal) && Engine(id) is { } engine => (engine.Name, "globe"),
                _ => null,
            };
            if (a is { } v) items.Add(new("act:" + id, LauncherItemKind.Action, "动作", v.name, null, new(v.glyph)));
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
        if (aiMode) ExitAi(); // 卡片打开时继续打字 = 回到列表重新搜
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
    }

    void Render(LauncherResult result, string query, bool attachmentMode, bool keepSelection = false)
    {
        var q = query.Trim();
        var keep = keepSelection && selected >= 0 && selected < rows.Count && selected != DefaultSelection() ? rows[selected].Item.Id : null;
        shownQuery = q;
        rows = [];
        sections = [];
        LauncherSection Section(string label, LauncherSectionKind kind) { var s = new LauncherSection(label, kind, []); sections.Add(s); return s; }
        void Add(LauncherSection s, LauncherItem item, LauncherCell cell, string? number = null) { var r = new LauncherRow(item, rows.Count, cell, number); rows.Add(r); s.Rows.Add(r); }

        if (result.IsHome)
        {
            // 设置 → 启动器 →「空启动器显示」：只显示固定时去掉「最近使用」，什么都不显示时只剩搜索栏
            var home = ((App)Application.Current).Store.Current.Launcher.Home;
            foreach (var group in result.Items.GroupBy(i => i.Group).Where(g => home switch
            {
                Pane.Settings.LauncherHome.Nothing => false,
                Pane.Settings.LauncherHome.PinnedOnly => g.Key != "最近使用",
                _ => true,
            }))
            {
                var s = Section(group.Key, LauncherSectionKind.Tiles);
                foreach (var item in group) Add(s, item, LauncherCell.App);
            }
        }
        else if (attachmentMode)
        {
            foreach (var item in result.Items)
            {
                if (sections.Count == 0 || sections[^1].Label != item.Group) Section(item.Group, LauncherSectionKind.List);
                Add(sections[^1], item, LauncherCell.Attachment);
            }
        }
        else if (q.Length > 0)
        {
            // 服务给的 AI / 网页两组不用了（动作行代替）
            foreach (var item in result.Items.Where(i => i.Kind == LauncherItemKind.Calculation).Take(1))
                Add(Section("", LauncherSectionKind.Calc), item, LauncherCell.Calc);
            var apps = result.Items.Where(i => i.Kind is LauncherItemKind.App or LauncherItemKind.Command).Take(MaxApps).ToList();
            if (apps.Count > 0)
            {
                var s = Section("应用", LauncherSectionKind.Tiles);
                foreach (var item in apps) Add(s, item, item.Kind == LauncherItemKind.App ? LauncherCell.App : LauncherCell.Command);
            }
            var actions = ActionItems();
            if (actions.Count > 0)
            {
                var s = Section($"对「{(q.Length > 24 ? q[..24] + "…" : q)}」", LauncherSectionKind.Tiles);
                for (int i = 0; i < actions.Count; i++) Add(s, actions[i], LauncherCell.Action, i < 9 ? (i + 1).ToString() : null);
            }
            // 常用（设置里加的文件 / 文件夹）在前，再是 Everything 的前 3 个
            var favorites = result.Items.Where(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder && i.Group != "文件").ToList();
            var files = result.Items.Where(i => i.Kind is LauncherItemKind.File or LauncherItemKind.Folder && i.Group == "文件").ToList();
            if (favorites.Count + files.Count > 0)
            {
                var s = Section("文件", LauncherSectionKind.List);
                foreach (var item in favorites.Concat(files.Take(MaxFiles))) Add(s, item, LauncherCell.File);
                int total = Math.Max(result.FileTotal, files.Count);
                if (total > MaxFiles)
                    Add(s, new("files:more", LauncherItemKind.Action, "文件", $"在文件窗口里查看全部 {total:N0} 个", null, new("external")), LauncherCell.More);
            }
        }

        Sections.ItemsSource = sections;
        ResultsArea.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Foot.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ChangeView(null, 0, null, true);
        highlight.Hide();
        selected = -1;
        UpdateLayout();
        int kept = keep is null ? -1 : rows.FindIndex(r => r.Item.Id == keep);
        Select(kept >= 0 ? kept : DefaultSelection(), scroll: false);
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
        double h = SearchRowHeight;
        if (aiMode) { UpdateLayout(); h += 1 + Card.ActualHeight + Card.Margin.Top + Card.Margin.Bottom; }
        else if (rows.Count > 0) h += 1 + Math.Min(ResultsContent.ActualHeight, ResultsMaxHeight) + FootHeight;
        h = Math.Ceiling(h);
        if (Math.Abs(h - lastHeight) < 0.5) return;
        lastHeight = h;
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
            LauncherCell.Calc => ($"复制 {item.Title}", [(["Enter"], "复制")]),
            LauncherCell.App => ($"打开 {item.Title}", [(["Enter"], "打开")]),
            LauncherCell.Command => (item.Title, [(["Enter"], "执行")]),
            LauncherCell.File => ($"打开 {item.Title}", [(["Enter"], "打开"), (["Ctrl", "+", "Enter"], "打开所在文件夹"), (["Tab"], "在文件窗口里看")]),
            LauncherCell.More => ($"在文件窗口里搜「{q}」", [(["Enter"], "打开文件窗口")]),
            LauncherCell.Action => ActionFoot(ActionId(item), item.Title, q),
            _ => (item.Title, [(["Enter"], "执行")]),
        };
        FootWhat.Text = what;
        KeyHints.Fill(FootKeys, keys, HintLabelProto, KbdProto);
    }

    static (string, (string[], string)[]) ActionFoot(string id, string name, string q) => id switch
    {
        "ai:translate" => ($"翻译「{q}」", [(["Enter"], "翻译"), (["Tab"], "打开翻译窗口")]),
        "ai:ask" => ($"问 AI：{q}", [(["Enter"], "问 AI"), (["Tab"], "打开 AI 对话窗口")]),
        "files" => ($"在文件里搜「{q}」", [(["Enter"], "打开文件窗口")]),
        _ when id.StartsWith("web:", StringComparison.Ordinal) => ($"用 {name} 搜「{q}」", [(["Enter"], "搜索")]),
        _ => ($"{name}「{q}」", [(["Enter"], name)]),
    };

    // ---------------- AI ----------------

    void RunAi(string actionId)
    {
        var q = Input.Text.Trim();
        var text = attachment is { } a ? AttachmentText(a) : q;
        if (actionId == "ask" && q.Length == 0 && attachment is null) return;
        if (text.Length == 0 && actionId != "ask") return;
        var act = ai.Actions.FirstOrDefault(x => x.Id == actionId);
        var title = actionId == "ask" ? "问 AI" : act?.Name ?? actionId;
        var glyph = actionId == "ask" ? "ai" : act?.Glyph ?? "ai";
        // 原型：问 AI 带附件时显示「附件：…」；预设动作显示被处理的原文
        var source = actionId == "ask" ? (attachment is not null ? "附件：" + text : null) : text;
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
        Card.Start(ai, request, title, glyph, source, compact: false,
            [(["Enter"], "粘贴"), (["Ctrl", "+", "C"], "复制"), (["Ctrl", "+", "R"], "重新生成"),
             (["Tab"], actionId == "translate" ? "翻译窗口" : "继续对话"), (["Esc"], "返回")]);
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
            ? (r.Text.Length > 0 ? $"{r.Question}\n\n附件：\n{r.Text}" : r.Question ?? "")
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
        catch (Exception e) { ((App)Application.Current).ReportFailure("粘贴", e); }
    }

    // ---------------- 选中 ----------------

    void Select(int index, bool scroll = true)
    {
        if (rows.Count == 0) { selected = -1; highlight.Hide(); UpdateFoot(); return; }
        index = Math.Clamp(index, 0, rows.Count - 1);
        if ((armed ?? failed) is { } marked && !ReferenceEquals(marked, rows[index])) Disarm();   // 换了选中项就取消待确认
        selected = index;
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

    bool SelectedIsTile => selected >= 0 && selected < rows.Count && sections.FirstOrDefault(s => s.Rows.Contains(rows[selected]))?.Kind == LauncherSectionKind.Tiles;

    void ShowNumbers(bool show) { foreach (var r in rows) r.ShowNumber = show; }

    // ---------------- 键盘与鼠标 ----------------

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Down(VirtualKey.Control);

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
                case VirtualKey.Up or VirtualKey.Down: break;
                default: return; // 其他键照常打字（打字会回到列表）
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
            case VirtualKey.Down: MoveLine(1); break;
            case VirtualKey.Up: MoveLine(-1); break;
            case VirtualKey.Right when SelectedIsTile: Select(selected + 1); break;
            case VirtualKey.Left when SelectedIsTile: Select(selected - 1); break;
            case VirtualKey.Enter: _ = Execute(selected, false, ctrl); break;
            case VirtualKey.Tab: _ = Execute(selected, true, false); break;
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

    static LauncherRow? RowFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: LauncherRow row }) return row;
        return null;
    }

    // 关机、重启：第一次 Enter 只把副标题换成确认提示，3 秒内再按一次才执行（013 约定由界面确认）
    LauncherRow? armed;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? armTimer;
    static bool NeedsConfirm(LauncherItem item) => item.Kind == LauncherItemKind.Command && item.Title is "关机" or "重启";

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
        LauncherItemKind.App or LauncherItemKind.File or LauncherItemKind.Folder or LauncherItemKind.Web => "打开",
        LauncherItemKind.Calculation => "复制",
        _ => item.Title,
    };

    bool Confirmed(LauncherRow row)
    {
        if (ReferenceEquals(armed, row)) { Disarm(); return true; }
        Disarm();
        armed = row;
        row.SetWarning($"再按一次 Enter 确认{row.Item.Title}");
        if (row.Index == selected) FootWhat.Text = $"再按一次 Enter 确认{row.Item.Title}";
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

    async Task Execute(int index, bool tab, bool ctrl)
    {
        if (index < 0 || index >= rows.Count) return;
        var row = rows[index];
        var item = row.Item;
        var q = shownQuery;

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
                catch (Exception e) { ((App)Application.Current).ReportFailure("打开所在文件夹", e); }
                return;
            case LauncherCell.Action:
                var id = ActionId(item);
                if (id == "files") { OpenFiles?.Invoke(q, null); return; }
                if (id == "ai:translate" && tab) { OpenTranslate?.Invoke(q); return; }
                if (id == "ai:ask" && tab) { OpenChat?.Invoke(q, null); return; }
                if (tab) return;
                if (id.StartsWith("ai:", StringComparison.Ordinal)) { RunAi(id[3..]); return; }
                if (id.StartsWith("web:", StringComparison.Ordinal) && Engine(id) is { } engine)
                {
                    var url = engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(q), StringComparison.Ordinal);
                    item = new("web:" + url, LauncherItemKind.Web, "网页", item.Title, null, item.Icon);
                    break;
                }
                return;
            default:
                if (tab) return;   // Tab 只展开二级窗口，别的项不响应（不误触发关机这类命令）
                break;
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
                ShowFailure(row, $"{Verb(item)}失败：{reason}");
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
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

/// <summary>可重启的一次性计时器（输入防抖）。</summary>
sealed class DispatcherQueueTimerHolder
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
    }
    public void Restart() { timer.Stop(); timer.Start(); }
    public void Stop() => timer.Stop();
}
