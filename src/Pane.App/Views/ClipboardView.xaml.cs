using Pane.Localization;
// 剪贴板视图逻辑（设计负责人）。交互照原型 app.js「剪贴板」：
// 搜索 + 类型筛选；已固定区在前、多于 3 条时收起（docs/design.md「剪贴板已固定区可折叠」）；Enter 粘贴、Shift+Enter 纯文本、Alt+1~9 快速粘贴、Ctrl+P 固定、Esc 分步；
// 默认不激活打开（「剪贴板不抢焦点」，059）：按键由宿主转来（HandleKey），打字 / 点搜索框时切到激活方式；
// 悬停 / 选中时右下角换成「交给启动器 / 固定 / 删除」按钮；删除带淡出右移。
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
using Pane.Views.Clipboard;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class ClipboardView : UserControl, IPanelView, IClipRowActions
{
    static readonly (string key, string label)[] Filters = [("all", Strings.Common_All), ("text", Strings.Common_Text), ("image", Strings.Common_Image), ("files", Strings.Common_File), ("pinned", Strings.Clipboard_Pinned)];

    readonly IClipboardHistory history;
    readonly SlidingHighlight highlight;
    readonly SlidingHighlight chipHighlight;
    readonly System.Collections.ObjectModel.ObservableCollection<ChipItem> chips = [];

    // 筛选条：类型筛选，再一道分隔和各个标签（2026-11-23）；选中的标签被删了就回到「全部」
    void BuildChips()
    {
        tagColors = null;
        var tags = history.Tags();
        chips.Clear();
        foreach (var f in Filters) chips.Add(new ChipItem(f.key, f.label));
        // Pane 云：同一账号两台以上电脑（含本机）时，类型后面加一组「本机 + 各电脑」（docs/cloud.md「剪贴板同步 → 区分设备」）
        var devices = OtherDevices();
        if (devices.Count > 0)
        {
            chips.Add(new ChipItem("sep", ""));
            chips.Add(new ChipItem("dev:", Strings.Clipboard_ThisDevice));
            foreach (var d in devices) chips.Add(new ChipItem("dev:" + d, d));
        }
        if (tags.Count > 0)
        {
            chips.Add(new ChipItem("sep", ""));
            foreach (var t in tags) chips.Add(new ChipItem("tag:" + t.Name, t.Name, TagColors.Dot(t.Color)));
        }
        if (!chips.Any(c => c.Key == filter)) filter = "all";
    }
    string? TagFilter => filter.StartsWith("tag:", StringComparison.Ordinal) ? filter[4..] : null;
    /// <summary>设备筛选：null = 不按设备筛；"" = 本机；其余是电脑名。</summary>
    string? DeviceFilter => filter.StartsWith("dev:", StringComparison.Ordinal) ? filter[4..] : null;

    static ISyncService? Sync => (Application.Current as App)?.Sync;
    static List<string> OtherDevices() => Sync is { Options: { Enabled: true, Clipboard: true } } sync ? sync.OtherDevices.Select(d => d.Name).Distinct().ToList() : [];
    static bool DevicesOnlyInFilter => Sync?.Options.DeviceView == ClipDeviceView.FilterOnly;

    // 一次只建一批行（含缩略图），往下滚或用方向键走到底时再补一批；历史可能有几千条
    const int Batch = 40;

    System.Collections.ObjectModel.ObservableCollection<ClipSection> sections = [];
    List<ClipRow> rows = [];
    List<ClipEntry> matched = [];   // 显示顺序：已固定区露出的条目 + 时间线（不含展开行）
    string query = string.Empty;
    // 已固定区：多于 PinPeek 条时收起，下面一行展开行；展开只在这次面板开着时有效，每次呼出复位
    const int PinPeek = 3;
    bool pinsOpen;
    ClipMoreRow? more;              // 展开行；没有折叠时为 null
    int lineStart;                  // 编号从 matched 的第几条起（时间线第一条；「已固定」筛选下为 0）
    string filter = "all";
    int selected = -1;
    bool activate;
    bool visible;
    nint previousWindow;

    public ClipboardView() : this(new ClipboardSample()) { }

    public ClipboardView(IClipboardHistory history)
    {
        this.history = history;
        InitializeComponent();
        Controls.PaneCaret.Attach(Input);
        highlight = new SlidingHighlight(HighlightLayer);
        chipHighlight = new SlidingHighlight(ChipHighlightLayer, size => size.Height / 2);
        Chips.ItemsSource = chips;
        BuildChips();
        // 面板收起时不跟着历史变化重建列表，下次呼出（OnShowing）再渲染；标签选择开着时只刷新标签行，编辑时不动
        history.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!visible) return;
            tagColors = null;
            if (mode == ClipMode.Tags) { if (modeTarget is { } t) modeTarget = Fresh(t.Id) ?? t; RenderTags(opSel); }
            else if (mode == ClipMode.None) Render(keepSelection: true);
        });
        Sections.RightTapped += OnListRightTapped;
        OpList.PointerMoved += (_, e) => { if (OpFrom(e.OriginalSource) is { } op && ops.IndexOf(op) is var i and >= 0 && i != opSel) OpSelect(i, scroll: false); };
        OpList.Tapped += (_, e) => { if (OpFrom(e.OriginalSource) is { } op) RunOp(op); };
        WireGrips();
        hintsTimer = DispatcherQueue.CreateTimer();
        hintsTimer.IsRepeating = false;
        hintsTimer.Tick += (_, _) => ShowHints(hintsWant);
        Scroller.ViewChanged += (_, _) => { if (NearBottom()) AppendMore(); };
        Sections.PointerMoved += OnListPointerMoved;
        Sections.Tapped += OnListTapped;
        Chips.Tapped += OnChipTapped;
        // 提示条（062）：「重启更新」「查看」交给宿主（查看会打开设置，剪贴板收起）；「忽略」「×」只收起这一条
        Notice.Primary += (_, n) => { CloseRequested?.Invoke(this, EventArgs.Empty); ((App)Application.Current).NoticePrimary(n); };
        Notice.Dismiss += (_, n) => { ((App)Application.Current).NoticeDismiss(n); Notice.Show(null); };
        // 输入框自己会处理按下，所以要连「已处理」的一起收（059：不激活方式下点搜索框切到激活）
        Input.AddHandler(PointerPressedEvent, new PointerEventHandler(OnInputPointerPressed), true);
        Loaded += (_, _) => { BuildFoot(); HideClearButton(); };
    }

    // ---------------- IPanelView ----------------

    // 高度可拖动改（2026-11-23）：存在 ClipboardSettings.PanelHeight（0 = 默认 640）；宽度固定
    const double DefaultHeight = 640, MinHeightDip = 360;
    static double StoredHeight()
    {
        var h = ((App)Application.Current).Store.Current.Clipboard.PanelHeight;
        return h > 0 ? Math.Max(MinHeightDip, h) : DefaultHeight;
    }
    public Size PreferredSize => new(372, StoredHeight());
    public event EventHandler<Size>? SizeRequested;
    /// <summary>拖动上下边缘：新高度（DIP）和是不是上边缘（上边缘变高时面板下边不动）。宿主转给 GlassPanel.ResizeTo。</summary>
    public event EventHandler<(double Height, bool FromTop)>? HeightDragged;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => null;

    public void OnShowing(PanelShowContext context)
    {
        visible = true;
        if (InMode) LeaveQuiet();
        BuildChips();
        SizeRequested?.Invoke(this, PreferredSize);
        ShowHints(false);
        if (IsLoaded) BuildFoot();
        if (((App)Application.Current).CurrentNotice() is { } notice) { Notice.Show(notice); ((App)Application.Current).NoticeShown(notice); }
        else Notice.Show(null);
        activate = context.Activate;
        previousWindow = context.PreviousForegroundWindow;
        filter = "all";
        pinsOpen = Environment.GetEnvironmentVariable("PANE_DESIGN_PINS_OPEN") == "1"; // 设计预览：展开已固定区
        // 设计预览：PANE_DESIGN_QUERY / PANE_DESIGN_FILTER 预先填入，不按键盘就能截图
        var preview = Environment.GetEnvironmentVariable("PANE_DESIGN_QUERY") ?? string.Empty;
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_FILTER") is { } f && Filters.Any(x => x.key == f)) filter = f;
        if (Input.Text != preview) Input.Text = preview; // TextChanged 会触发渲染
        else Render(keepSelection: false);
        // 输入框还没加载时设 Text 不触发 TextChanged（设计预览一启动就呼出），直接渲染
        if (preview.Length > 0 && !Input.IsLoaded) Render(keepSelection: false);
    }

    public void OnShown()
    {
        if (activate) Input.Focus(FocusState.Programmatic);
        // 显示前选中时面板还没排版，选中块可能没画上、也没滚到位：显示后再选一次
        if (selected >= 0) Select(selected, scroll: selected > 0);
        // 设计预览：选中第 N 条，看操作按钮
        if (int.TryParse(Environment.GetEnvironmentVariable("PANE_DESIGN_SELECT"), out var n)) Select(n);
    }

    public void OnHidden()
    {
        // 收起后放掉行和缩略图，常驻只留服务里的文本和元数据
        visible = false;
        highlight.Hide();
        Sections.ItemsSource = null;
        sections = [];
        rows = [];
        matched = [];
        more = null;
        frozen = [];
        selected = -1;
    }

    // ---------------- 渲染 ----------------

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressText) return;
        if (mode == ClipMode.Tags) { RenderTags(0); return; }
        if (mode == ClipMode.Menu) { LeaveMode(keepTyping: true); return; }   // 操作列表里打字：退回列表接着搜
        if (mode == ClipMode.Edit) return;
        Render(keepSelection: false);
    }

    // 面板开着时顺序冻结：固定 / 取消固定、粘贴后挪到最前都只改条目本身（图钉变实心 / 空心），不挪位置、列表不跟着跑；
    // 下次呼出、换搜索词或换筛选时才按「已固定在前、其余按时间」归位。开着时新复制的条目插在已固定区后面。
    enum ClipGroup { Pinned, Today, Earlier }
    Dictionary<string, (int Order, ClipGroup Group)> frozen = [];

    void Render(bool keepSelection)
    {
        if (InMode) return;
        var (q, qTags, qDevs) = ParseQuery(Input.Text);
        var keep = keepSelection ? SelectedRow?.Entry.Id : null;
        var keepMore = keepSelection && more is not null && selected == more.Index;
        var count = keepSelection ? Math.Max(Batch, rows.Count) : Batch;
        query = q;
        var all = history.Snapshot();
        if (!keepSelection) frozen = [];
        List<ClipEntry> found;
        if (TagFilter is not null) all = all.OrderByDescending(e => e.CopiedAt).ToList();   // 按标签筛选：不分已固定区，按时间排
        if (frozen.Count == 0) found = all.Where(e => Matches(e, q, qTags, qDevs, ignorePin: false)).ToList();
        else
        {
            // 已显示过的按原顺序和原分组留着（「已固定」筛选下取消固定的也先留着），新来的放在已固定区之后
            var known = all.Where(e => frozen.ContainsKey(e.Id) && Matches(e, q, qTags, qDevs, ignorePin: true)).OrderBy(e => frozen[e.Id].Order).ToList();
            var fresh = all.Where(e => !frozen.ContainsKey(e.Id) && Matches(e, q, qTags, qDevs, ignorePin: false));
            found = [.. known.Where(e => frozen[e.Id].Group == ClipGroup.Pinned), .. fresh, .. known.Where(e => frozen[e.Id].Group != ClipGroup.Pinned)];
        }
        // 冻结的是全部命中（含收起时藏着的固定条目），展开后它们按原位置出来
        frozen = found.Select((e, i) => (e.Id, (i, GroupOf(e)))).ToDictionary(x => x.Id, x => x.Item2);
        var pins = found.Where(e => GroupOf(e) == ClipGroup.Pinned).ToList();
        var line = found.Where(e => GroupOf(e) != ClipGroup.Pinned).ToList();
        // 搜索时全部展开（不藏命中）；「已固定」筛选下只有这一区，不折叠
        var searching = q.Length > 0 || qTags.Count > 0 || qDevs.Count > 0;
        var fold = !searching && filter != "pinned" && pins.Count > PinPeek;
        var shown = fold && !pinsOpen ? pins.Take(PinPeek).ToList() : pins;
        matched = [.. shown, .. line];
        lineStart = filter == "pinned" ? 0 : shown.Count;
        more = fold ? new ClipMoreRow(shown.Count, pins.Count - PinPeek, pinsOpen) : null;
        // 展开行接在已固定区末尾，已固定区一次建完（选中序列里展开行前面的行都得在）
        count = Math.Max(count, shown.Count);
        if (keep is not null) count = Math.Max(count, matched.FindIndex(e => e.Id == keep) + 1);

        rows = [];
        sections = [];
        Append(Math.Min(count, matched.Count));
        Sections.ItemsSource = sections;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in chips) c.IsSelected = c.Key == filter;
        if (!keepSelection) Scroller.ChangeView(null, 0, null, true);
        highlight.Hide();
        selected = -1;
        UpdateLayout();
        MoveChipHighlight();
        // 默认选中时间线第一条（Enter 贴最新复制的）；搜索时选最上面那条；时间线为空选已固定区第一条
        var kept = keep is null ? null : rows.Find(r => r.Entry.Id == keep);
        var index = keepMore && more is not null ? more.Index
            : kept is not null ? kept.Index
            : keepSelection || searching || line.Count == 0 ? 0
            : IndexOf(shown.Count);
        Select(Math.Min(index, Count - 1), scroll: keepSelection || index > 0);
    }

    // 搜索框里的「#标签」（2026-11-23）、「@电脑名」（2026-11-24，Pane 云）：拆成标签、电脑条件（都按开头匹配）和普通关键词，可以连用
    static (string Query, List<string> Tags, List<string> Devices) ParseQuery(string raw)
    {
        var tags = new List<string>();
        var devices = new List<string>();
        var words = new List<string>();
        foreach (var w in raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w.Length > 1 && w[0] == '#') tags.Add(w[1..]);
            else if (w.Length > 1 && w[0] == '@') devices.Add(w[1..]);
            else words.Add(w);
        return (string.Join(' ', words), tags, devices);
    }

    bool Matches(ClipEntry e, string q, List<string> qTags, List<string> qDevs, bool ignorePin)
    {
        if (TagFilter is { } tag && !e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return false;
        // 设备：「只在设备筛选里显示」时，没选设备就不出别的电脑的条目
        if (DeviceFilter is { } device ? !string.Equals(e.SourceDevice ?? "", device, StringComparison.OrdinalIgnoreCase) : e.SourceDevice is not null && DevicesOnlyInFilter) return false;
        if (qDevs.Any(d => !(e.SourceDevice ?? Strings.Clipboard_ThisDevice).StartsWith(d, StringComparison.OrdinalIgnoreCase))) return false;
        if (qTags.Any(t => !e.Tags.Any(n => n.StartsWith(t, StringComparison.OrdinalIgnoreCase)))) return false;
        switch (filter)
        {
            case "pinned" when !e.Pinned && !ignorePin: return false;
            case "text" when e.Kind is not (ClipKind.Text or ClipKind.Code or ClipKind.Link or ClipKind.Color): return false;
            case "image" when e.Kind != ClipKind.Image: return false;
            case "files" when e.Kind != ClipKind.Files: return false;
        }
        if (q.Length == 0) return true;
        var hay = (e.Files is { } f ? string.Join(" ", f.Select(System.IO.Path.GetFileName)) : e.Text ?? "") + " " + e.Source;
        return hay.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // 冻结期间沿用条目第一次出现时的分组
    ClipGroup GroupOf(ClipEntry e) => frozen.TryGetValue(e.Id, out var f) ? f.Group
        : TagFilter is not null ? ClipGroup.Today
        : e.Pinned ? ClipGroup.Pinned : (DateTimeOffset.Now - e.CopiedAt).TotalMinutes < 1440 ? ClipGroup.Today : ClipGroup.Earlier;

    void Append(int until)
    {
        for (var i = rows.Count; i < until; i++)
        {
            // 编号只给时间线：Alt+1 是时间线第一条（「已固定」筛选下从第一条起）
            var n = i - lineStart;
            var row = new ClipRow(matched[i], IndexOf(i), n is >= 0 and < 9 ? n + 1 : 0, query, this);
            rows.Add(row);
            var group = GroupOf(row.Entry);
            if (sections.Count == 0 || i == 0 || TagFilter is null && GroupOf(rows[i - 1].Entry) != group)
                sections.Add(TagFilter is { } tag ? new ClipSection("#" + tag) : group switch
                {
                    ClipGroup.Pinned => new ClipSection(Strings.Clipboard_Pinned, i == 0 ? more : null),
                    ClipGroup.Today => new ClipSection(Strings.Clipboard_Today),
                    _ => new ClipSection(Strings.Clipboard_Earlier),
                });
            sections[^1].Rows.Add(row);
        }
    }

    // 选中序列 = 行 + 已固定区末尾的展开行。IndexOf：matched 第 i 条在选中序列里的位置
    int IndexOf(int i) => more is not null && i >= more.Index ? i + 1 : i;
    int Count => rows.Count + (more is not null ? 1 : 0);
    ClipRow? RowAt(int index) => more is not null && index == more.Index ? null
        : rows.ElementAtOrDefault(more is not null && index > more.Index ? index - 1 : index);
    ClipRow? SelectedRow => selected >= 0 ? RowAt(selected) : null;

    bool NearBottom() => visible && rows.Count < matched.Count
        && Scroller.VerticalOffset + Scroller.ViewportHeight > Scroller.ExtentHeight - 240;

    void AppendMore()
    {
        if (rows.Count >= matched.Count) return;
        Append(Math.Min(rows.Count + Batch, matched.Count));
        UpdateLayout();
        if (selected >= 0 && ElementAt(selected) is { } el) highlight.MoveTo(el);
    }

    void MoveChipHighlight()
    {
        var chip = chips.FirstOrDefault(c => c.Key == filter) ?? chips[0];
        if (Chips.ContainerFromItem(chip) is FrameworkElement el)
        {
            chipHighlight.MoveTo(el);
            // 选中的标签在可见范围外就滚过去
            var x = el.TransformToVisual(ChipBox).TransformPoint(new Point(0, 0)).X;
            if (x < ChipScroller.HorizontalOffset + 16) ChipScroller.ChangeView(Math.Max(0, x - 16), null, null);
            else if (x + el.ActualWidth > ChipScroller.HorizontalOffset + ChipScroller.ViewportWidth - 16) ChipScroller.ChangeView(x + el.ActualWidth - ChipScroller.ViewportWidth + 16, null, null);
        }
    }

    // ---------------- 选中 ----------------

    void Select(int index, bool scroll = true)
    {
        if (Count == 0 || index < 0) { selected = -1; highlight.Hide(); return; }
        if (index >= Count && rows.Count < matched.Count) AppendMore(); // 方向键走到底：先补一批，不绕回顶部
        index = (index % Count + Count) % Count;
        SetSelected(selected, false);
        selected = index;
        SetSelected(selected, true);
        if (IsLoaded) BuildFoot();
        UpdateLayout();
        if (ElementAt(selected) is not { } el) return;
        highlight.MoveTo(el);
        if (scroll) ScrollIntoView(el);
    }

    void SetSelected(int index, bool value)
    {
        if (index < 0) return;
        if (RowAt(index) is { } row) row.IsSelected = value;
        else if (more is not null && index == more.Index) more.IsSelected = value;
    }

    FrameworkElement? ElementAt(int index)
    {
        if (RowAt(index) is { } row) return ContainerOf(row);
        // 展开行在已固定区（第一个分组）的模板里
        if (more is null || index != more.Index || sections.Count == 0 || Sections.ContainerFromItem(sections[0]) is not DependencyObject outer) return null;
        return FindDescendant<Grid>(outer, "MoreRow");
    }

    FrameworkElement? ContainerOf(ClipRow row)
    {
        var section = sections.FirstOrDefault(s => s.Rows.Contains(row));
        if (section is null || Sections.ContainerFromItem(section) is not DependencyObject outer) return null;
        return FindDescendant<ItemsControl>(outer)?.ContainerFromItem(row) as FrameworkElement;
    }

    // 展开后选中新露出来的第一条（列表停在接着往下看的位置）；收起后选中仍在展开行
    void ToggleMore()
    {
        pinsOpen = !pinsOpen;
        Render(keepSelection: true);
        if (pinsOpen) Select(PinPeek);
        else if (more is not null) Select(more.Index);
    }

    void ScrollIntoView(FrameworkElement el)
    {
        var top = el.TransformToVisual(ListContent).TransformPoint(new Point(0, 0)).Y;
        var bottom = top + el.ActualHeight;
        var view = Scroller.ViewportHeight;
        var y = Scroller.VerticalOffset;
        if (top - 4 < y) Scroller.ChangeView(null, Math.Max(0, top - 4), null);
        else if (bottom + 8 > y + view) Scroller.ChangeView(null, bottom + 8 - view, null);
    }

    // ---------------- 键盘、鼠标 ----------------

    void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var alt = Down(VirtualKey.Menu);

        PanelKeyPress? press =
            alt && e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9 ? new(PanelKey.Quick, e.Key - VirtualKey.Number1 + 1) :
            // 操作列表里数字键直接执行（不带修饰键）
            mode == ClipMode.Menu && !ctrl && !alt && !shift && e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9 ? new(PanelKey.Digit, e.Key - VirtualKey.Number1 + 1) :
            e.Key switch
            {
                VirtualKey.Down => new(PanelKey.Down),
                VirtualKey.Up => new(PanelKey.Up),
                VirtualKey.Enter => new(shift ? PanelKey.ShiftEnter : PanelKey.Enter),
                VirtualKey.P when ctrl => new(PanelKey.Pin),
                VirtualKey.B when ctrl => new(PanelKey.Words),
                VirtualKey.K when ctrl => new(PanelKey.More),
                VirtualKey.T when ctrl => new(PanelKey.Tags),
                VirtualKey.E when ctrl => new(PanelKey.Edit),
                VirtualKey.Tab => new(PanelKey.Send),
                VirtualKey.Escape => new(PanelKey.Escape),
                _ => null,
            };
        if (press is { } p) { HandleKey(p); e.Handled = true; }
    }

    /// <summary>
    /// 一个按键动作。搜索框有焦点时来自 OnInputKeyDown；不激活方式（059）下由宿主把 IPanelKeyRouter 拦下的键转过来。
    /// Type 由宿主处理（切到激活方式），这里不管。
    /// </summary>
    public void HandleKey(PanelKeyPress press)
    {
        if (ModeKey(press)) return;
        switch (press.Key)
        {
            case PanelKey.More: OpenMenu(); break;
            case PanelKey.Tags: if (SelectedRow is { } tr) OpenTags(tr.Entry); break;
            case PanelKey.Edit: if (SelectedRow is { } er) StartEdit(er.Entry); break;
            case PanelKey.Quick:
                var i = lineStart + press.Number - 1; // 编号只给时间线
                if (press.Number is >= 1 and <= 9 && i < matched.Count)
                {
                    if (i >= rows.Count) Append(i + 1);
                    UpdateLayout();
                    Select(IndexOf(i));
                    _ = Paste(plainText: false);
                }
                break;
            case PanelKey.Down: Select(selected + 1); break;
            case PanelKey.Up: Select(selected - 1); break;
            case PanelKey.Enter or PanelKey.ShiftEnter:
                if (more is not null && selected == more.Index) ToggleMore();
                else _ = Paste(plainText: press.Key == PanelKey.ShiftEnter);
                break;
            case PanelKey.Pin: if (SelectedRow is { } p) TogglePin(p); break;
            case PanelKey.Send: if (SelectedRow is { } t && ModuleGate.On(Contracts.ModuleIds.Launcher)) Send(t); break;
            case PanelKey.Words: if (SelectedRow is { CanSplit: true } w) Words(w); break;
            case PanelKey.Escape:
                if (Input.Text.Length > 0) Input.Text = string.Empty;
                else CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>不激活方式（059）：宿主打开时设 true，切到激活方式（Engage）后设 false。</summary>
    public bool Passive { get; set; }

    /// <summary>不激活方式下点了搜索框：宿主切到激活方式。</summary>
    public event EventHandler? EngageRequested;

    /// <summary>切到激活方式后聚焦搜索框（光标放到末尾）。</summary>
    public void FocusSearch()
    {
        Input.Focus(FocusState.Programmatic);
        Input.SelectionStart = Input.Text.Length;
    }

    void OnInputPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Passive) EngageRequested?.Invoke(this, EventArgs.Empty);
    }

    void OnListPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var index = RowFrom(e.OriginalSource)?.Index ?? MoreFrom(e.OriginalSource)?.Index;
        if (index is { } i && i != selected) Select(i, scroll: false);
    }

    void OnListTapped(object sender, TappedRoutedEventArgs e)
    {
        // 点到按钮上由按钮自己处理
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is Button) return;
        if (RowFrom(e.OriginalSource) is { } row) { Select(row.Index, scroll: false); _ = Paste(plainText: false); }
        else if (MoreFrom(e.OriginalSource) is not null) ToggleMore();
    }

    void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (InMode || RowFrom(e.OriginalSource) is not { } row) return;
        Select(row.Index, scroll: false);
        OpenMenu();
        e.Handled = true;
    }

    void OnChipTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: ChipItem chip })
            {
                if (chip.Key == "sep" || InMode) return;
                filter = chip.Key;
                Render(keepSelection: false);
                if (!Passive) Input.Focus(FocusState.Programmatic);   // 不激活方式下点筛选不去抢焦点（059）
                return;
            }
        }
    }

    static ClipMoreRow? MoreFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: ClipMoreRow m }) return m;
        return null;
    }

    void OnInputFocus(object sender, RoutedEventArgs e) =>
        FocusRing.Opacity = Input.FocusState != FocusState.Unfocused ? 0.45 : 0;

    static ClipRow? RowFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: ClipRow row }) return row;
        return null;
    }

    // ---------------- 动作 ----------------

    Task Paste(bool plainText) => SelectedRow is { } row ? PasteEntry(row.Entry, plainText) : Task.CompletedTask;

    async Task PasteEntry(ClipEntry entry, bool plainText)
    {
        // 图片没有文字可贴，Shift+Enter 按普通粘贴处理（不让按键落空）
        if (entry.Kind == ClipKind.Image) plainText = false;
        // 叠在启动器上打开的：不粘贴，交给启动器（宿主收起剪贴板、焦点回启动器）
        if (ForLauncher) { UseInLauncher?.Invoke(this, entry); return; }
        var id = entry.Id;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try { await history.PasteAsync(id, plainText, previousWindow, CancellationToken.None); }
        catch (Exception e) { ((App)Application.Current).ReportFailure(Strings.Common_Paste, e); } // 面板已收起：屏幕中间提示
    }

    /// <summary>启动器开着时按剪贴板热键，剪贴板叠在启动器上打开（2026-11-08 用户定）：选中条目交给启动器，不粘贴。宿主在显示前设。</summary>
    public bool ForLauncher { get; set; }

    /// <summary>ForLauncher 时选了一条（Enter、点一下、Alt+数字）。</summary>
    public event EventHandler<ClipEntry>? UseInLauncher;

    /// <summary>「交给启动器」：宿主收到后把条目交给启动器（LauncherView.AttachNext）并切到启动器。</summary>
    public event EventHandler<ClipEntry>? SendToLauncher;

    public void Send(ClipRow row)
    {
        if (ForLauncher) { UseInLauncher?.Invoke(this, row.Entry); return; }   // 启动器本来就开着，不重新呼出（会重置输入）
        SendToLauncher?.Invoke(this, row.Entry);
    }

    /// <summary>「分词」（093）：宿主收到后打开分词窗口拆这条文字（剪贴板收起）。</summary>
    public event EventHandler<ClipEntry>? OpenWords;
    public void Words(ClipRow row) => OpenWords?.Invoke(this, row.Entry);

    public void TogglePin(ClipRow row) => history.SetPinned(row.Entry.Id, !row.Entry.Pinned);

    public void Delete(ClipRow row)
    {
        // 原型：淡出 + 右移 24px，--dur-fast，然后从历史里删掉
        if (ContainerOf(row) is not { } el) { history.Delete(row.Entry.Id); return; }
        highlight.Hide();
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        var duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast"));
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = duration;
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1, new Vector3(24, 0, 0), ease);
        slide.Duration = duration;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            var from = el.ActualHeight;
            var collapse = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from, To = 0, EnableDependentAnimation = true,
                Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast") * ((App)Application.Current).Glass.Current.MotionSpeed),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(collapse, el);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(collapse, "Height");
            story.Children.Add(collapse);
            story.Completed += (_, _) => history.Delete(row.Entry.Id);
            story.Begin();
        });
    }

    // ---------------- 底部快捷键提示 ----------------

    // 启动器停用时不列「Tab 交给启动器」（055）；启动器状态变了下次呼出时重建
    // 按键提示（2026-11-23）：设置「按键提示」三档——收起为按钮（默认，搜索框右边的键盘按钮，悬停浮出全部快捷键）/ 常驻显示（底栏只列最常用的五项）/ 不显示。
    // 模式里（操作列表、标签、编辑）列那个模式的键
    void BuildFoot()
    {
        var hints = ((App)Application.Current).Store.Current.Clipboard.KeyHints;
        FootBar.Visibility = hints == Settings.ClipKeyHints.Always ? Visibility.Visible : Visibility.Collapsed;
        HintsButton.Visibility = hints == Settings.ClipKeyHints.Button ? Visibility.Visible : Visibility.Collapsed;
        bool send = ModuleGate.On(Contracts.ModuleIds.Launcher), words = ModuleGate.On(Contracts.ModuleIds.Words);
        (string[] keys, string label)[] compact, full;
        if (mode == ClipMode.Menu) compact = full = [(["↑", "↓"], Strings.Clipboard_Select), (["Enter"], Strings.Common_Execute), (["1~9"], Strings.Clipboard_RunNumber), (["Esc"], Strings.Common_Back)];
        else if (mode == ClipMode.Tags) compact = full = [(["↑", "↓"], Strings.Clipboard_Select), (["Enter"], Strings.Clipboard_TagToggle), (["Esc"], Strings.Common_Done)];
        else if (mode == ClipMode.Edit) compact = full = [(["Ctrl", "+", "S"], Strings.Common_Save), (["Ctrl", "+", "Enter"], Strings.Clipboard_SaveAndPaste), (["Esc"], Strings.Clipboard_Discard)];
        else if (ForLauncher) compact = full = [(["↑", "↓"], Strings.Clipboard_Select), (["Enter"], Strings.Clipboard_FillLauncher), (["Esc"], Strings.Clipboard_BackToLauncher)];
        else
        {
            var shift = SelectedRow?.Entry.Kind == ClipKind.Files ? Strings.Clipboard_PastePath : Strings.Clipboard_PlainText;
            compact = [(["Enter"], Strings.Common_Paste), (["Shift", "+", "Enter"], shift), (["Alt", "+", "1~9"], Strings.Clipboard_QuickPaste),
                (["Ctrl", "+", "K"], Strings.Launcher_MoreActions), .. send ? new[] { (new[] { "Tab" }, Strings.Clipboard_SendToLauncher) } : []];
            full = [(["↑", "↓"], Strings.Clipboard_Select), .. compact, (["Ctrl", "+", "T"], Strings.Clipboard_Tags), (["Ctrl", "+", "E"], Strings.Common_Edit),
                (["Ctrl", "+", "P"], Strings.Clipboard_Pin), .. words ? new[] { (new[] { "Ctrl", "+", "B" }, Strings.Words_Open) } : []];
        }
        var key = string.Join("|", compact.Select(c => c.label)) + "/" + string.Join("|", full.Select(c => c.label)) + "/" + hints;
        if (key == footKey) return;
        footKey = key;
        KeyHints.Fill(Foot, compact, HintLabelProto, KbdProto);
        KeyHints.Fill(HintsList, full, HintLabelProto, KbdProto);
    }
    string footKey = "";

    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer hintsTimer;
    bool hintsWant;
    void OnHintsEnter(object sender, PointerRoutedEventArgs e) => QueueHints(true, 200);
    void OnHintsExit(object sender, PointerRoutedEventArgs e) => QueueHints(false, 120);
    void OnHintsClick(object sender, RoutedEventArgs e) { hintsTimer.Stop(); ShowHints(HintsPop.Opacity < 0.5); }
    void QueueHints(bool show, double ms) { hintsWant = show; hintsTimer.Stop(); hintsTimer.Interval = TimeSpan.FromMilliseconds(ms); hintsTimer.Start(); }
    void ShowHints(bool show)
    {
        if (show) BuildFoot();
        HintsPop.Opacity = show ? 1 : 0;
        HintsPop.IsHitTestVisible = show;
    }

    // ---------------- 拖动上下边缘改高度（2026-11-23） ----------------

    double dragStart;
    void WireGrips()
    {
        foreach (var grip in new[] { TopGrip, BottomGrip })
        {
            grip.Started += (_, _) => dragStart = ActualHeight > 0 ? ActualHeight : StoredHeight();
            grip.Dragged += (_, dy) =>
            {
                var d = dy / (XamlRoot?.RasterizationScale ?? 1);
                HeightDragged?.Invoke(this, (grip.FromTop ? dragStart - d : dragStart + d, grip.FromTop));
            };
            grip.Completed += (_, _) => SaveHeight(Math.Round(ActualHeight));
            grip.Reset += (_, _) => { SaveHeight(0); SizeRequested?.Invoke(this, new Size(372, DefaultHeight)); };
        }
    }

    static void SaveHeight(double h)
    {
        var app = (App)Application.Current;
        app.Store.Save(app.Store.Current with { Clipboard = app.Store.Current.Clipboard with { PanelHeight = h } });
    }

    // ---------------- 小工具 ----------------

    void HideClearButton()
    {
        if (FindDescendant<Button>(Input, "DeleteButton") is { } b) { b.MinWidth = 0; b.Width = 0; b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false; }   // 模板给了最小宽度，只设 Width 不够
    }

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
