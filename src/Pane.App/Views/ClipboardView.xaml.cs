// 剪贴板视图逻辑（设计负责人）。交互照原型 app.js「剪贴板」：
// 搜索 + 类型筛选；已固定在前；Enter 粘贴、Shift+Enter 纯文本、Alt+1~9 快速粘贴、Ctrl+P 固定、Esc 分步；
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
    static readonly (string key, string label)[] Filters = [("all", "全部"), ("text", "文本"), ("image", "图片"), ("files", "文件"), ("pinned", "已固定")];

    readonly IClipboardHistory history;
    readonly SlidingHighlight highlight;
    readonly SlidingHighlight chipHighlight;
    readonly List<ChipItem> chips = Filters.Select(f => new ChipItem(f.key, f.label)).ToList();

    // 一次只建一批行（含缩略图），往下滚或用方向键走到底时再补一批；历史可能有几千条
    const int Batch = 40;

    System.Collections.ObjectModel.ObservableCollection<ClipSection> sections = [];
    List<ClipRow> rows = [];
    List<ClipEntry> matched = [];
    string query = string.Empty;
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
        // 面板收起时不跟着历史变化重建列表，下次呼出（OnShowing）再渲染
        history.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (visible) Render(keepSelection: true); });
        Scroller.ViewChanged += (_, _) => { if (NearBottom()) AppendMore(); };
        Sections.PointerMoved += OnListPointerMoved;
        Sections.Tapped += OnListTapped;
        Chips.Tapped += OnChipTapped;
        Loaded += (_, _) => { BuildFoot(); HideClearButton(); };
    }

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(372, 640);
    public event EventHandler<Size>? SizeRequested { add { } remove { } } // 固定尺寸，不请求改尺寸
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => null;

    public void OnShowing(PanelShowContext context)
    {
        visible = true;
        activate = context.Activate;
        previousWindow = context.PreviousForegroundWindow;
        filter = "all";
        // 设计预览：PANE_DESIGN_QUERY / PANE_DESIGN_FILTER 预先填入，不按键盘就能截图
        var preview = Environment.GetEnvironmentVariable("PANE_DESIGN_QUERY") ?? string.Empty;
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_FILTER") is { } f && Filters.Any(x => x.key == f)) filter = f;
        if (Input.Text != preview) Input.Text = preview; // TextChanged 会触发渲染
        else Render(keepSelection: false);
    }

    public void OnShown()
    {
        if (activate) Input.Focus(FocusState.Programmatic);
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
        frozen = [];
        selected = -1;
    }

    // ---------------- 渲染 ----------------

    void OnTextChanged(object sender, TextChangedEventArgs e) => Render(keepSelection: false);

    // 面板开着时顺序冻结：固定 / 取消固定、粘贴后挪到最前都只改条目本身（图钉变实心 / 空心），不挪位置、列表不跟着跑；
    // 下次呼出、换搜索词或换筛选时才按「已固定在前、其余按时间」归位。开着时新复制的条目插在已固定区后面。
    Dictionary<string, (int Order, string Group)> frozen = [];

    void Render(bool keepSelection)
    {
        var q = Input.Text.Trim();
        var keep = keepSelection && selected >= 0 && selected < rows.Count ? rows[selected].Entry.Id : null;
        var count = keepSelection ? Math.Max(Batch, rows.Count) : Batch;
        query = q;
        var all = history.Snapshot();
        if (!keepSelection) frozen = [];
        if (frozen.Count == 0) matched = all.Where(e => Matches(e, q, ignorePin: false)).ToList();
        else
        {
            // 已显示过的按原顺序和原分组留着（「已固定」筛选下取消固定的也先留着），新来的放在已固定区之后
            var known = all.Where(e => frozen.ContainsKey(e.Id) && Matches(e, q, ignorePin: true)).OrderBy(e => frozen[e.Id].Order).ToList();
            var fresh = all.Where(e => !frozen.ContainsKey(e.Id) && Matches(e, q, ignorePin: false));
            matched = [.. known.Where(e => frozen[e.Id].Group == "已固定"), .. fresh, .. known.Where(e => frozen[e.Id].Group != "已固定")];
        }
        frozen = matched.Select((e, i) => (e.Id, (i, GroupOf(e)))).ToDictionary(x => x.Id, x => x.Item2);
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
        var index = keep is null ? 0 : Math.Max(0, rows.FindIndex(r => r.Entry.Id == keep));
        Select(Math.Min(index, rows.Count - 1), scroll: keepSelection);
    }

    bool Matches(ClipEntry e, string q, bool ignorePin)
    {
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
    string GroupOf(ClipEntry e) => frozen.TryGetValue(e.Id, out var f) ? f.Group
        : e.Pinned ? "已固定" : (DateTimeOffset.Now - e.CopiedAt).TotalMinutes < 1440 ? "今天" : "更早";

    void Append(int until)
    {
        for (var i = rows.Count; i < until; i++)
        {
            var row = new ClipRow(matched[i], i, query, this);
            rows.Add(row);
            var group = GroupOf(row.Entry);
            if (sections.Count == 0 || sections[^1].Label != group) sections.Add(new ClipSection(group));
            sections[^1].Rows.Add(row);
        }
    }

    bool NearBottom() => visible && rows.Count < matched.Count
        && Scroller.VerticalOffset + Scroller.ViewportHeight > Scroller.ExtentHeight - 240;

    void AppendMore()
    {
        if (rows.Count >= matched.Count) return;
        Append(Math.Min(rows.Count + Batch, matched.Count));
        UpdateLayout();
        if (selected >= 0 && selected < rows.Count && ContainerOf(rows[selected]) is { } el) highlight.MoveTo(el);
    }

    void MoveChipHighlight()
    {
        var chip = chips.First(c => c.Key == filter);
        if (Chips.ContainerFromItem(chip) is FrameworkElement el) chipHighlight.MoveTo(el);
    }

    // ---------------- 选中 ----------------

    void Select(int index, bool scroll = true)
    {
        if (rows.Count == 0 || index < 0) { selected = -1; highlight.Hide(); return; }
        if (index >= rows.Count && rows.Count < matched.Count) AppendMore(); // 方向键走到底：先补一批，不绕回顶部
        index = (index % rows.Count + rows.Count) % rows.Count;
        if (selected >= 0 && selected < rows.Count) rows[selected].IsSelected = false;
        selected = index;
        rows[selected].IsSelected = true;
        UpdateLayout();
        if (ContainerOf(rows[selected]) is not { } el) return;
        highlight.MoveTo(el);
        if (scroll) ScrollIntoView(el);
    }

    FrameworkElement? ContainerOf(ClipRow row)
    {
        var section = sections.FirstOrDefault(s => s.Rows.Contains(row));
        if (section is null || Sections.ContainerFromItem(section) is not DependencyObject outer) return null;
        return FindDescendant<ItemsControl>(outer)?.ContainerFromItem(row) as FrameworkElement;
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

        if (alt && e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
        {
            var i = e.Key - VirtualKey.Number1;
            if (i < rows.Count) { Select(i); _ = Paste(plainText: false); }
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Down: Select(selected + 1); e.Handled = true; break;
            case VirtualKey.Up: Select(selected - 1); e.Handled = true; break;
            case VirtualKey.Enter: _ = Paste(plainText: shift); e.Handled = true; break;
            case VirtualKey.P when ctrl: if (selected >= 0) TogglePin(rows[selected]); e.Handled = true; break;
            case VirtualKey.Tab: if (selected >= 0) Send(rows[selected]); e.Handled = true; break;
            case VirtualKey.Escape:
                if (Input.Text.Length > 0) Input.Text = string.Empty;
                else CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    void OnListPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { } row && row.Index != selected) Select(row.Index, scroll: false);
    }

    void OnListTapped(object sender, TappedRoutedEventArgs e)
    {
        // 点到按钮上由按钮自己处理
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is Button) return;
        if (RowFrom(e.OriginalSource) is { } row) { Select(row.Index, scroll: false); _ = Paste(plainText: false); }
    }

    void OnChipTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: ChipItem chip })
            {
                filter = chip.Key;
                Render(keepSelection: false);
                Input.Focus(FocusState.Programmatic);
                return;
            }
        }
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

    async Task Paste(bool plainText)
    {
        if (selected < 0 || selected >= rows.Count) return;
        var entry = rows[selected].Entry;
        // 图片没有文字可贴，Shift+Enter 按普通粘贴处理（不让按键落空）
        if (entry.Kind == ClipKind.Image) plainText = false;
        var id = entry.Id;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try { await history.PasteAsync(id, plainText, previousWindow, CancellationToken.None); }
        catch (Exception e) { ((App)Application.Current).ReportFailure("粘贴", e); } // 面板已收起：屏幕中间提示
    }

    /// <summary>「交给启动器」：宿主收到后把条目交给启动器（LauncherView.AttachNext）并切到启动器。</summary>
    public event EventHandler<ClipEntry>? SendToLauncher;

    public void Send(ClipRow row) => SendToLauncher?.Invoke(this, row.Entry);

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
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => history.Delete(row.Entry.Id));
    }

    // ---------------- 底部快捷键提示 ----------------

    void BuildFoot()
    {
        if (Foot.Children.Count > 0) return;
        KeyHints.Fill(Foot,
        [
            (["↑", "↓"], "选择"), (["Enter"], "粘贴"), (["Shift", "+", "Enter"], "纯文本"),
            (["Alt", "+", "1~9"], "快速粘贴"), (["Tab"], "交给启动器"), (["Ctrl", "+", "P"], "固定"),
        ], HintLabelProto, KbdProto);
    }

    // ---------------- 小工具 ----------------

    void HideClearButton()
    {
        if (FindDescendant<Button>(Input, "DeleteButton") is { } b) { b.Width = 0; b.IsTabStop = false; }
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
