using Pane.Localization;
// 文件窗口逻辑（设计负责人）。交互照原型 prototype/launcher.html「二级窗口：文件搜索」：
// 输入防抖后搜；左侧分类带数量，Tab / Shift+Tab 切换；结果分批加载（每批 50，滚到接近底部再要）；
// 上下键选中，Enter 打开，Ctrl+Enter 打开所在文件夹，可拖出；右侧预览可关；底部换排序。
// Esc 有输入时清空，输入为空时关窗口（2026-11-03，issue 11）；Ctrl+W 关。
// 为对话框选（2026-11-25，任务 125，docs/design.md「文件搜索的相关度、对话框搜索「查看全部」」）：从对话框搜索的「查看全部」进来时，
// 头部标出是哪个对话框，结果按对话框的文件类型筛选（「选择文件夹」只要文件夹），Enter / 双击 / 预览按钮照对话框搜索条的规则让对话框跳过去。
// 打开、打开所在文件夹、为对话框选了一项都记进搜索记忆（ISearchMemory.RecordFile）。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Pane.Views.Files;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class FilesView : UserControl, ISecondaryView, IDisposable
{
    const int Batch = 50;
    const double RowHeight = 50;
    readonly IFileSearch? search;
    readonly ISelectionActions output;
    readonly List<CategoryItem> categories = Enum.GetValues<FileCategory>().Select(c => new CategoryItem(c)).ToList();
    readonly List<FileRow> rows = [];
    readonly DispatcherQueueTimerHolder debounce, previewDebounce;
    readonly PaneSwitch previewSwitch;
    readonly SlidingHighlight listHighlight, railHighlight, railHover;
    FileCategory category = FileCategory.All;
    FileSort sort = FileSort.Relevance;
    int selected = -1, total;
    // 120：关键词是完整文件夹路径时，第一行是这个文件夹本身，底部数量只算它里面的（Total 多算了这一行）
    bool listingFolder;
    bool loading, topmost;
    string? focusPath;
    CancellationTokenSource? pending, previewPending;
    // 为对话框选：目标对话框、「打开」里 Enter 是不是直接打开（设置 FolderJumpSettings.OpenOnEnter）
    FileDialogInfo? dialog;
    bool openOnEnter = true;

    public FilesView(IFileSearch? search, ISelectionActions output)
    {
        this.search = search;
        this.output = output;
        InitializeComponent();
        Controls.PaneCaret.Attach(Input);
        Rail.ItemsSource = categories;
        categories[0].IsSelected = true;
        debounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(120), () => _ = Search());
        previewDebounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(80), () => _ = LoadPreview());
        previewSwitch = new PaneSwitch(true, SwitchProto.Background, SwitchProto.BorderBrush, AccentProto.Background) { RenderTransform = new ScaleTransform { ScaleX = 0.8, ScaleY = 0.8, CenterY = 11 } };
        previewSwitch.Toggled += (_, on) => SetPreviewVisible(on);
        PreviewToggle.Children.Add(previewSwitch);
        // 选中和悬停都是滑动高亮（同启动器，2026-10-31 用户提：原来没有悬停态、点了也没有动效）
        listHighlight = new SlidingHighlight(ListHighlight);
        railHighlight = new SlidingHighlight(RailHighlight, _ => ((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft);
        railHover = new SlidingHighlight(RailHover, _ => ((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft, hover: true);
        List.ElementPrepared += (_, a) => { if (a.Index == selected && a.Element is FrameworkElement el) DispatcherQueue.TryEnqueue(() => listHighlight.MoveTo(el, animate: false)); };
        Rail.Loaded += (_, _) => MoveRailHighlight(animate: false);
        thumbFade = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(900), () => { if (!overThumb && !draggingThumb) FadeThumb(0); });
        ElementCompositionPreview.GetElementVisual(ScrollThumb).Opacity = 0;
        FillHints();
        PreviewKeyDown += OnKeyDown;   // 先于输入框拿到方向键、Tab、Enter
        Loaded += (_, _) => HideDelete();
        Input.GotFocus += (_, _) => HideDelete();
        ShowEmpty();
        UpdatePreview();
        // 窗口关掉就释放，取消订阅免得服务一直引用它
        Icons.Ready += OnIconReady;
        Unloaded += (_, _) => Icons.Ready -= OnIconReady;
    }

    /// <summary>
    /// 打开（或复用）窗口时宿主调用：带上查询文字，focus 是要选中的文件（启动器里文件格按 Tab）。
    /// dialog 不为 null：为这个对话框选（对话框搜索的「查看全部」，任务 125）；为 null 是普通文件窗口。
    /// </summary>
    public void Open(string query, string? focus, FileDialogInfo? dialog = null, bool openOnEnter = true)
    {
        focusPath = focus;
        this.openOnEnter = openOnEnter;
        SetDialog(dialog);
        SelectCategory(dialog?.FoldersOnly == true ? FileCategory.Folder : FileCategory.All, search: false);
        // 直接搜，不等 TextChanged：窗口还没进可视树时改文字，TextChanged 偶尔不来，结果区一直是空的（2026-10-31 实测）
        Input.Text = query;
        debounce.Stop();
        _ = Search();
        Input.SelectionStart = Input.Text.Length;
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(880, 580);
    public Size MinimumSize => new(640, 420);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => Input.Focus(FocusState.Programmatic);
    public void OnHidden() { pending?.Cancel(); previewPending?.Cancel(); }

    // 永久销毁时由宿主调用（GlassPanel.Dispose，055 起能力停用、二级窗口关闭都会销毁视图）。
    // 不能只靠 Unloaded：内容岛直接释放时它不一定来。退订常驻对象的事件、停计时器、取消任务、释放附着的合成视觉
    public void Dispose()
    {
        OnHidden();
        Icons.Ready -= OnIconReady;
        debounce.Dispose(); previewDebounce.Dispose(); thumbFade.Dispose();
        listHighlight.Dispose(); railHighlight.Dispose(); railHover.Dispose();
    }

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---------------- 为对话框选 ----------------

    /// <summary>选好了（任务 125）：宿主让 Dialog 跳到 Folder（FileName 为 null 表示文件名不动、Confirm 确认），记进搜索记忆，再关窗口。</summary>
    public event EventHandler<(FileDialogInfo Dialog, string Folder, string? FileName, bool Confirm, string Query, string Path)>? DialogPick;
    /// <summary>打开或打开所在文件夹（普通模式）：宿主记进搜索记忆。</summary>
    public event EventHandler<(string Query, string Path)>? Chosen;

    /// <summary>现在在为哪个对话框选；宿主在对话框关掉或换了一个时调用 <see cref="ExitDialog"/>。</summary>
    public nint DialogHandle => dialog?.Dialog ?? 0;

    /// <summary>对话框关了（或换了一个）：回到普通文件窗口，结果按不筛选重搜。</summary>
    public void ExitDialog()
    {
        if (dialog is null) return;
        SetDialog(null);
        _ = Search();
    }

    void OnExitDialog(object sender, RoutedEventArgs e) { ExitDialog(); Input.Focus(FocusState.Programmatic); }

    void SetDialog(FileDialogInfo? d)
    {
        dialog = d;
        TargetChip.Visibility = d is null ? Visibility.Collapsed : Visibility.Visible;
        if (d is not null)
        {
            var kind = d.FoldersOnly ? Strings.Files_DialogFolder : d.Save ? Strings.Files_DialogSave : Strings.Files_DialogOpen;
            TargetText.Text = d.AppName is { Length: > 0 } app ? string.Format(Strings.Files_DialogTarget, (object?)kind, (object?)app) : string.Format(Strings.Files_DialogTargetNoApp, (object?)kind);
            ToolTipService.SetToolTip(TargetChip, TargetText.Text);
        }
        bool filter = d is { FoldersOnly: false, Extensions.Count: > 0 };
        FilterChip.Visibility = filter ? Visibility.Visible : Visibility.Collapsed;
        if (filter) FilterText.Text = string.Format(Strings.DialogSearch_FilterChip, (object?)(d!.FilterName ?? string.Join("、", d.Extensions!)));
        foreach (var c in categories) c.Enabled = d?.FoldersOnly != true || c.Category == FileCategory.Folder;
        if (d?.FoldersOnly != true && !categories.First(c => c.Category == category).Enabled) category = FileCategory.All;
        FillHints();
        UpdateActions();
    }

    FileQuery Query(string q) => new(q, category, sort, dialog?.FoldersOnly == true ? null : dialog?.Extensions, dialog?.FoldersOnly == true);

    enum How { Enter, Shift, Ctrl }

    // 选中项在对话框里能做什么（照对话框搜索条，DialogSearchView.HintItems / Run）：第一项是 Enter
    (string[] Keys, string Label, How How)[] DialogActions(FileHit? hit)
    {
        if (dialog is null || hit is null) return [];
        if (hit.Category == FileCategory.Folder) return [(["Enter"], Strings.DialogSearch_Jump, How.Enter)];
        if (dialog.Save) return [(["Enter"], Strings.DialogSearch_SaveHere, How.Enter), (["Ctrl", "+", "Enter"], Strings.DialogSearch_UseName, How.Ctrl)];
        return openOnEnter
            ? [(["Enter"], Strings.DialogSearch_OpenNow, How.Enter), (["Shift", "+", "Enter"], Strings.DialogSearch_FillName, How.Shift)]
            : [(["Enter"], Strings.DialogSearch_FillName, How.Enter), (["Shift", "+", "Enter"], Strings.DialogSearch_OpenNow, How.Shift)];
    }

    void Pick(How how)
    {
        if (dialog is not { } d || Current is not { } row) return;
        var hit = row.Hit;
        if (!DialogActions(hit).Any(a => a.How == how)) return;
        var dir = hit.Category == FileCategory.Folder ? hit.Path : System.IO.Path.GetDirectoryName(hit.Path);
        if (dir is null) return;
        (string? name, bool confirm) = hit.Category == FileCategory.Folder ? (null, false)
            : d.Save ? (how == How.Ctrl ? hit.Name : null, false)
            : (hit.Name, openOnEnter ? how == How.Enter : how == How.Shift);
        DialogPick?.Invoke(this, (d, dir, name, confirm, Input.Text.Trim(), hit.Path));
    }

    // 预览里的两个按钮：普通模式「打开」「所在文件夹」；为对话框选时是这一项的对话框动作（文件夹只有「跳转」）
    void UpdateActions()
    {
        var acts = DialogActions(Current?.Hit);
        if (dialog is null || acts.Length == 0)
        {
            PrimaryButton.Content = Strings.Common_Open;
            SecondaryButton.Content = Strings.Files_ContainingFolder;
            SecondaryButton.Visibility = Visibility.Visible;
            return;
        }
        PrimaryButton.Content = acts[0].Label;
        SecondaryButton.Visibility = acts.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (acts.Length > 1) SecondaryButton.Content = acts[1].Label;
    }

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;   // 开着：图钉实心（状态靠形状表达）
        ToolTipService.SetToolTip(PinButton, topmost ? Strings.Common_AlwaysOnTopEnabled : Strings.Common_AlwaysOnTop);
        TopmostChanged?.Invoke(this, topmost);
    }

    // ---------------- 搜索 ----------------

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        HideDelete();
        debounce.Restart();
        if ((Input.Text.Length == 0) != hintsForEmpty) FillHints();
    }

    // 提示栏的 Esc：有输入时是「清空」，空的时候是「关闭」；为对话框选时 Enter 那几项跟着选中项（文件夹 / 文件）换
    bool hintsForEmpty;
    string? hintsKey;
    void FillHints()
    {
        hintsForEmpty = Input.Text.Length == 0;
        (string[] keys, string label)[] items = dialog is null
            ? [(["Enter"], Strings.Common_Open), (["Ctrl", "+", "Enter"], Strings.Common_OpenContainingFolder)]
            : DialogActions(Current?.Hit).Select(a => (a.Keys, a.Label)).ToArray();
        items = [.. items, (["Esc"], hintsForEmpty ? Strings.Common_Close : Strings.Common_Clear)];
        var key = string.Join("|", items.Select(i => i.label));
        if (key == hintsKey) return;
        hintsKey = key;
        KeyHints.Fill(Hints, items, HintLabelProto, KbdProto);
    }

    async Task Search()
    {
        debounce.Stop();
        pending?.Cancel();
        var cts = pending = new CancellationTokenSource();
        var q = Input.Text.Trim();
        rows.Clear();
        selected = -1;
        total = 0;
        listingFolder = false;
        if (q.Length == 0 || search is null)
        {
            foreach (var c in categories) c.SetCount(null);
            countsQuery = null;
            Bind();
            ShowEmpty(search is null ? Strings.Files_SearchUnavailable : null);
            return;
        }
        FilePage page;
        try { page = await search.SearchAsync(Query(q), 0, Batch, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (FileSearchUnavailableException ex) { if (cts == pending) { Bind(); ShowEmpty(ex.Message); } return; }
        catch (Exception ex) { if (cts == pending) { Bind(); ShowEmpty(string.Format(Strings.Files_SearchError, App.FailureReason(ex) ?? Strings.Common_UnexpectedProblem)); } return; }
        if (cts.IsCancellationRequested) return;
        total = page.Total;
        listingFolder = page.Folder is not null;
        // 分类数量第一页常常还没算好（null）：先留着上一次的数字，新的到了再换，不清空——
        // 原来先清空再填，切分类、改排序、每打一个字，左边的数字都会闪一下（2026-11-26 用户报）
        if (page.Counts is { } counts) { foreach (var c in categories) c.SetCount(counts.GetValueOrDefault(c.Category)); countsQuery = CountsKey(q); }
        else if (countsQuery != CountsKey(q)) _ = FetchCounts(q, cts);
        Append(page.Items);
        Bind();
        int focus = focusPath is { } p ? rows.FindIndex(r => string.Equals(r.Hit.Path, p, StringComparison.OrdinalIgnoreCase)) : -1;
        focusPath = null;
        Select(Math.Max(0, focus));
        if (rows.Count == 0) ShowEmpty(string.Format(Strings.Files_NotFound, (object?)(q)));
        else Empty.Visibility = Visibility.Collapsed;
    }

    // 分类数量服务在后台算（第一页先给 null，021），没有「算完了」的通知：每 100ms 用同一关键词再要一次（只要 1 条，读它的缓存），最多约 2.5 秒
    string? countsQuery;   // 左边数字现在是哪个关键词的
    // 数量只跟关键词和对话框的类型限定有关，和选中哪个分类、怎么排序无关
    string CountsKey(string q) => $"{q}|{dialog?.FoldersOnly}|{(dialog?.Extensions is { } e ? string.Join(";", e) : "")}";

    async Task FetchCounts(string q, CancellationTokenSource cts)
    {
        for (int i = 0; i < 25 && search is not null; i++)
        {
            try
            {
                await Task.Delay(100, cts.Token);   // 服务算数量约 0.2 秒（021 实测），每 100ms 读一次缓存，数字约 0.25 秒内出来
                var page = await search.SearchAsync(Query(q), 0, 1, cts.Token);
                if (cts != pending || page.Counts is null) continue;
                foreach (var c in categories) c.SetCount(page.Counts.GetValueOrDefault(c.Category));
                countsQuery = CountsKey(q);
                return;
            }
            catch (OperationCanceledException) { return; }   // 换了关键词：新的那次接着取
            catch (Exception) { break; }
        }
        // 出错或一直没算好：旧关键词的数字不能留着冒充，清掉
        if (cts == pending) { foreach (var c in categories) c.SetCount(null); countsQuery = null; }
    }

    void Append(IEnumerable<FileHit> hits)
    {
        bool system = SystemIcons.Enabled;
        foreach (var h in hits)
        {
            var row = new FileRow(h, rows.Count);
            if (system) row.SetSystemIcon(Icons.TryGet(h.Path, h.Category == FileCategory.Folder));
            rows.Add(row);
        }
    }

    // 系统文件图标（036）：没缓存的后台去取，好了按路径补到行上（可能在后台线程触发）
    static Pane.Contracts.IFileIcons Icons => ((App)Application.Current).FileIcons;
    void OnIconReady(object? sender, string path) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!SystemIcons.Enabled) return;
        foreach (var r in rows)
            if (string.Equals(r.Hit.Path, path, StringComparison.OrdinalIgnoreCase)) r.SetSystemIcon(Icons.TryGet(path, r.Hit.Category == FileCategory.Folder));
        if (Current is { } c && string.Equals(c.Hit.Path, path, StringComparison.OrdinalIgnoreCase)) UpdateThumbIcon(c.Hit);
    });

    void UpdateThumbIcon(FileHit hit)
    {
        var icon = SystemIcons.Enabled ? SystemIcons.Load(Icons.TryGet(hit.Path, hit.Category == FileCategory.Folder)) : null;
        ThumbSystem.Source = icon;
        ThumbSystem.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        ThumbTile.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    void Bind()
    {
        listHighlight.Hide();
        List.ItemsSource = null;
        // 界面只拿副本（issue 7）：ItemsRepeater 记着列表长度，List 改了不会通知它。原来直接给 rows，
        // 下次搜索先清空 rows 再等结果，这期间一滚动 / 重新排版，它就按旧长度取行，越界崩溃（打字快、输入法改词时常见）
        List.ItemsSource = rows.ToList();
        ListScroller.ChangeView(null, 0, null, true);
        TotalText.Text = rows.Count == 0 ? "" : string.Format(Strings.Files_Count, (object?)(listingFolder ? total - 1 : total));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateThumb);
    }

    // 滚到离底部不到 5 行时要下一批
    async void OnListScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateThumb();
        if (!draggingThumb) ShowThumb();
        if (ListScroller.VerticalOffset + ListScroller.ViewportHeight < ListScroller.ExtentHeight - RowHeight * 5) return;
        await LoadMore();
    }

    async Task LoadMore()
    {
        if (loading || search is null || rows.Count == 0 || rows.Count >= total) return;
        loading = true;
        var cts = pending;
        try
        {
            var page = await search.SearchAsync(Query(Input.Text.Trim()), rows.Count, Batch, cts?.Token ?? default);
            if (cts != pending || cts?.IsCancellationRequested == true) return;
            int before = rows.Count;
            Append(page.Items);
            // ItemsRepeater 绑的是 List：换一个新列表让它知道多了（保持滚动位置）
            var offset = ListScroller.VerticalOffset;
            List.ItemsSource = rows.ToList();
            ListScroller.ChangeView(null, offset, null, true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateThumb);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }   // 后面的批次失败就停在已有的结果上
        finally { loading = false; }
    }

    void ShowEmpty(string? message = null)
    {
        Empty.Text = message ?? Strings.Files_SearchHint;
        Empty.Visibility = Visibility.Visible;
        TotalText.Text = "";
        UpdatePreview();
    }

    // ---------------- 分类与排序 ----------------

    void SelectCategory(FileCategory c, bool search = true)
    {
        if (!categories.First(x => x.Category == c).Enabled) return;
        category = c;
        foreach (var item in categories) item.IsSelected = item.Category == c;
        MoveRailHighlight(animate: true);
        if (search) _ = Search();
    }

    void MoveRailHighlight(bool animate)
    {
        if (Rail.ContainerFromIndex(categories.FindIndex(x => x.Category == category)) is FrameworkElement el) railHighlight.MoveTo(el, animate);
    }

    // 悬停（2026-11-27 issue 93 改）：每一项自己接 PointerEntered，滑到这一项的容器（和选中块同一个元素、同样大小）；
    // 原来在整个列表上接 PointerMoved、往上找带 CategoryItem 的容器，找不到就没有反馈
    void OnRailItemEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement item) railHover.MoveTo(VisualTreeHelper.GetParent(item) as FrameworkElement ?? item);
    }

    // PointerExited 会从子元素冒泡上来：鼠标还在列表范围里（项之间的缝、字和底之间移动）不收，真离开列表才收
    void OnRailPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!PaneHover.Inside(Rail, e)) railHover.Hide();
    }

    void OnRailTapped(object sender, TappedRoutedEventArgs e)
    {
        if (DataOf<CategoryItem>(e.OriginalSource) is { } item) SelectCategory(item.Category);
        Input.Focus(FocusState.Programmatic);
    }

    void OnSort(object sender, RoutedEventArgs e)
    {
        sort = sort == FileSort.Relevance ? FileSort.Modified : FileSort.Relevance;
        SortText.Text = sort == FileSort.Relevance ? Strings.Files_SortRelevance : Strings.Files_SortModified;
        _ = Search();
        Input.Focus(FocusState.Programmatic);
    }

    void SetPreviewVisible(bool on)
    {
        PreviewColumn.Width = new GridLength(on ? 268 : 0);
        Preview.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) UpdatePreview();
    }

    // ---------------- 选中与预览 ----------------

    void Select(int index, bool scroll = true)
    {
        if (rows.Count == 0) { selected = -1; listHighlight.Hide(); UpdatePreview(); FillHints(); return; }
        index = Math.Clamp(index, 0, rows.Count - 1);
        bool had = selected >= 0 && selected < rows.Count;
        if (had) rows[selected].IsSelected = false;
        selected = index;
        rows[selected].IsSelected = true;
        if (List.TryGetElement(index) is FrameworkElement el) listHighlight.MoveTo(el, animate: had);
        FillHints();
        UpdateActions();
        if (!scroll) { UpdatePreview(); return; }
        // 行高固定：直接按下标算位置滚进视野
        double top = index * RowHeight, bottom = top + RowHeight, y = ListScroller.VerticalOffset, view = ListScroller.ViewportHeight;
        if (top < y) ListScroller.ChangeView(null, top, null, true);
        else if (bottom > y + view - 12) ListScroller.ChangeView(null, bottom - view + 12, null, true);
        if (index >= rows.Count - 5) _ = LoadMore();
        UpdatePreview();
    }

    FileRow? Current => selected >= 0 && selected < rows.Count ? rows[selected] : null;

    void UpdatePreview()
    {
        previewPending?.Cancel();
        if (Current is not { } row || Preview.Visibility != Visibility.Visible)
        {
            Preview.Opacity = Current is null ? 0 : 1;
            return;
        }
        Preview.Opacity = 1;
        var hit = row.Hit;
        PreviewName.Text = hit.Name;
        ThumbTile.Background = FileKinds.Brush(hit.Category);
        ThumbGlyph.Glyph = FileKinds.Glyph(hit.Category);
        UpdateThumbIcon(hit);
        ThumbImage.Source = null;
        ThumbImage.Visibility = Visibility.Collapsed;
        ThumbBox.Visibility = Visibility.Visible;
        TextHeadBox.Visibility = Visibility.Collapsed;
        // 图片：界面自己按路径解码缩略图（按显示宽度 2 倍解码，省内存）
        if (hit.Category == FileCategory.Image && !hit.Name.EndsWith(".psd", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ThumbImage.Source = new BitmapImage { DecodePixelWidth = 472, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(hit.Path) };
                ThumbImage.Visibility = Visibility.Visible;
            }
            catch (UriFormatException) { }
        }
        FillMeta(hit, null);
        previewDebounce.Restart();
    }

    async Task LoadPreview()
    {
        if (search is null || Current is not { } row) return;
        previewPending?.Cancel();
        var cts = previewPending = new CancellationTokenSource();
        FilePreview preview;
        try { preview = await search.PreviewAsync(row.Hit, cts.Token); }
        catch (Exception) { return; }
        if (cts.IsCancellationRequested || Current != row) return;
        if (preview.TextHead is { Length: > 0 } text)
        {
            TextHead.Text = text;
            TextHeadBox.Visibility = Visibility.Visible;
            ThumbBox.Visibility = Visibility.Collapsed;
        }
        FillMeta(row.Hit, preview);
    }

    void FillMeta(FileHit hit, FilePreview? p)
    {
        Meta.Children.Clear();
        Meta.RowDefinitions.Clear();
        var items = new List<(string, string, bool)> { (Strings.Files_MetaType, FileKinds.Name(hit.Category) is var n && hit.Category != FileCategory.All ? n : Strings.Common_File, false) };
        if (hit.Category == FileCategory.Folder) { if (p?.FolderItems is { } count) items.Add((Strings.Files_MetaContents, string.Format(Strings.Files_ItemCount, (object?)(count)), false)); }
        else items.Add((Strings.Files_MetaSize, FileKinds.Size(hit.Size), false));
        if (p?.ImageWidth is { } w && p.ImageHeight is { } h) items.Add((Strings.Files_MetaDimensions, $"{w} × {h}", false));
        items.Add((Strings.Files_MetaModified, FileKinds.When(hit.Modified), false));
        items.Add((Strings.Files_MetaLocation, System.IO.Path.GetDirectoryName(hit.Path) ?? hit.Path, true));
        foreach (var (label, value, selectable) in items)
        {
            if (value.Length == 0) continue;
            Meta.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int r = Meta.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, FontFamily = HintLabelProto.FontFamily, FontSize = HintLabelProto.FontSize, Foreground = HintLabelProto.Foreground };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontFamily = MetaValueProto.FontFamily, FontSize = MetaValueProto.FontSize, Foreground = MetaValueProto.Foreground, IsTextSelectionEnabled = selectable };
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            Meta.Children.Add(l); Meta.Children.Add(v);
        }
    }

    // ---------------- 打开、所在文件夹、复制路径、拖出 ----------------

    async Task Run(Func<string, CancellationToken, Task> action, string what)
    {
        if (Current is not { } row || search is null) return;
        if (Input.Text.Trim() is { Length: > 0 } q) Chosen?.Invoke(this, (q, row.Hit.Path));
        try { await action(row.Hit.Path, CancellationToken.None); }
        catch (Exception e) { if (App.FailureReason(e) is { } reason) Empty.Text = string.Format(Strings.Common_ActionFailed, (object?)(what), (object?)(reason)); }
    }

    void OnOpenSelected(object sender, RoutedEventArgs e) { if (dialog is not null) Pick(DialogActions(Current?.Hit) is { Length: > 0 } a ? a[0].How : How.Enter); else _ = Run(search!.OpenAsync, Strings.Common_Open); }
    void OnRevealSelected(object sender, RoutedEventArgs e) { if (dialog is not null) { if (DialogActions(Current?.Hit) is { Length: > 1 } a) Pick(a[1].How); } else _ = Run(search!.RevealAsync, Strings.Common_OpenContainingFolder); }
    void OnCopyPath(object sender, RoutedEventArgs e) { if (Current is { } row) _ = output.CopyAsync(row.Hit.Path, CancellationToken.None); }

    void OnListTapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { } row) Select(row.Index);
        Input.Focus(FocusState.Programmatic);
    }

    void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { } row) return;
        Select(row.Index);
        if (dialog is not null) Pick(How.Enter); else _ = Run(search!.OpenAsync, Strings.Common_Open);
    }

    // 拖出：按住移动超过几像素才开始拖（2026-10-31）。原来行上直接开 CanDrag，鼠标单击被拖放吞掉，点结果没反应
    FrameworkElement? pressRow;
    Point pressAt;

    void OnListPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(List);
        pressRow = p.Properties.IsLeftButtonPressed ? RowOf(e.OriginalSource) : null;
        pressAt = p.Position;
    }

    async void OnListMoved(object sender, PointerRoutedEventArgs e)
    {
        // 悬停即选中，高亮滑过去（同启动器）；预览有 80ms 防抖，划过去不会连着加载
        if (pressRow is null && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && RowAt(e.OriginalSource) is { } hovered && hovered.Index != selected)
            Select(hovered.Index, scroll: false);
        ShowThumb();
        if (pressRow is not { } row) return;
        var p = e.GetCurrentPoint(List);
        if (!p.Properties.IsLeftButtonPressed) { pressRow = null; return; }
        if (Math.Abs(p.Position.X - pressAt.X) < 6 && Math.Abs(p.Position.Y - pressAt.Y) < 6) return;
        pressRow = null;
        row.DragStarting -= OnDragStarting;
        row.DragStarting += OnDragStarting;
        try { await row.StartDragAsync(e.GetCurrentPoint(row)); } catch (Exception) { }
    }

    void OnListReleased(object sender, PointerRoutedEventArgs e) => pressRow = null;

    // 认行按「列表的第几个子元素」：ItemsRepeater 用 x:Bind 模板时行元素没有 DataContext，原来按 DataContext 找永远找不到，
    // 单击、双击、拖出都落空（2026-10-31，用户录屏）
    FrameworkElement? RowOf(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement f && VisualTreeHelper.GetParent(d) == List) return f;
        return null;
    }

    FileRow? RowAt(object source) => RowOf(source) is { } f && List.GetElementIndex(f) is var i && i >= 0 && i < rows.Count ? rows[i] : null;

    // 把文件（或文件夹）交给拖放的目标（资源管理器、聊天窗口……）
    async void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (RowAt(sender) is not { } row) { args.Cancel = true; return; }
        var deferral = args.GetDeferral();
        try
        {
            Windows.Storage.IStorageItem item = row.Hit.Category == FileCategory.Folder
                ? await Windows.Storage.StorageFolder.GetFolderFromPathAsync(row.Hit.Path)
                : await Windows.Storage.StorageFile.GetFileFromPathAsync(row.Hit.Path);
            args.Data.SetStorageItems([item]);
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
        catch (Exception) { args.Cancel = true; }
        finally { deferral.Complete(); }
    }

    // ---------------- 键盘 ----------------

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Down: Select(selected + 1); break;
            case VirtualKey.Up: Select(selected - 1); break;
            case VirtualKey.PageDown: Select(selected + (int)(ListScroller.ViewportHeight / RowHeight)); break;
            case VirtualKey.PageUp: Select(selected - (int)(ListScroller.ViewportHeight / RowHeight)); break;
            case VirtualKey.Enter when Current is not null && dialog is not null: Pick(ctrl ? How.Ctrl : shift ? How.Shift : How.Enter); break;
            case VirtualKey.Enter when Current is not null: _ = Run(ctrl ? search!.RevealAsync : search!.OpenAsync, ctrl ? Strings.Common_OpenContainingFolder : Strings.Common_Open); break;
            case VirtualKey.Tab:
                // 只在可用的分类之间走（为「选择文件夹」选时只有文件夹）
                var usable = categories.Where(c => c.Enabled).ToList();
                int i = usable.FindIndex(c => c.Category == category);
                SelectCategory(usable[(i + (shift ? usable.Count - 1 : 1) + usable.Count) % usable.Count].Category);
                break;
            case VirtualKey.Escape:
                // Esc：有输入先清空；输入已经是空的再按就关窗口（issue 11：原来只能点 X 或 Ctrl+W）
                if (Input.Text.Length == 0) { CloseRequested?.Invoke(this, EventArgs.Empty); break; }
                Input.Text = "";
                Input.Focus(FocusState.Programmatic);
                break;
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------- 滚动条 ----------------
    // 系统滚动条带上下箭头、和玻璃不搭：隐藏它，画一条 4px 圆角细条。滚动、鼠标在列表上时淡入，900ms 不动就淡出；
    // 鼠标移到细条上变宽，可以拖动，点在空白处直接跳过去（2026-10-31 用户提）
    readonly DispatcherQueueTimerHolder thumbFade;
    bool overThumb, draggingThumb;
    double dragStartY, dragStartOffset, thumbOpacity;

    void OnListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumb();
    void OnListPointerEntered(object sender, PointerRoutedEventArgs e) => ShowThumb();
    void OnListPointerExited(object sender, PointerRoutedEventArgs e) => thumbFade.Restart();

    void UpdateThumb()
    {
        double view = ListScroller.ViewportHeight, extent = ListScroller.ExtentHeight;
        if (view <= 0 || extent <= view + 1) { ScrollTrack.Visibility = Visibility.Collapsed; return; }
        ScrollTrack.Visibility = Visibility.Visible;
        double track = ScrollTrack.ActualHeight > 0 ? ScrollTrack.ActualHeight : view;
        double thumb = Math.Max(28, track * view / extent);
        ScrollThumb.Height = thumb;
        // 滑块贴右边画在列表的 8 像素留白里，离窗口边 2，不压选中高亮（2026-11-26 用户定）；可拖范围仍是整条 12 宽
        ScrollThumb.Margin = new Thickness(0, (track - thumb) * ListScroller.VerticalOffset / (extent - view), 2, 0);
    }

    void ShowThumb()
    {
        if (ScrollTrack.Visibility != Visibility.Visible) return;
        FadeThumb(1);
        thumbFade.Restart();
    }

    void FadeThumb(double to)
    {
        if (Math.Abs(thumbOpacity - to) < 0.01) return;
        thumbOpacity = to;
        var visual = ElementCompositionPreview.GetElementVisual(ScrollThumb);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, (float)to, PaneSwitch.Ease(visual.Compositor, "PaneEaseSmooth"));
        fade.Duration = TimeSpan.FromMilliseconds(GlassParameters.Token("PaneDurFast") * ((App)Application.Current).Glass.Current.MotionSpeed + 1);
        visual.StartAnimation("Opacity", fade);
    }

    void ThumbWide(bool wide) { ScrollThumb.Width = wide ? 6 : 4; ScrollThumb.CornerRadius = new CornerRadius(wide ? 3 : 2); }
    void OnThumbEntered(object sender, PointerRoutedEventArgs e) { overThumb = true; ThumbWide(true); FadeThumb(1); }
    void OnThumbExited(object sender, PointerRoutedEventArgs e) { overThumb = false; if (!draggingThumb) { ThumbWide(false); thumbFade.Restart(); } }

    void OnThumbPressed(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(ScrollTrack).Position.Y;
        double top = ScrollThumb.Margin.Top, thumb = ScrollThumb.Height;
        // 点在细条外：先让细条中心跳到点击处，再从这里开始拖
        if (y < top || y > top + thumb) ScrollTo(y - thumb / 2);
        draggingThumb = true;
        dragStartY = y;
        dragStartOffset = ListScroller.VerticalOffset;
        ScrollTrack.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void OnThumbMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!draggingThumb) return;
        double view = ListScroller.ViewportHeight, extent = ListScroller.ExtentHeight, track = ScrollTrack.ActualHeight, thumb = ScrollThumb.Height;
        if (track - thumb <= 0) return;
        var y = e.GetCurrentPoint(ScrollTrack).Position.Y;
        ListScroller.ChangeView(null, dragStartOffset + (y - dragStartY) * (extent - view) / (track - thumb), null, true);
        UpdateThumb();
    }

    void ScrollTo(double thumbTop)
    {
        double view = ListScroller.ViewportHeight, extent = ListScroller.ExtentHeight, track = ScrollTrack.ActualHeight, thumb = ScrollThumb.Height;
        if (track - thumb <= 0) return;
        ListScroller.ChangeView(null, Math.Clamp(thumbTop, 0, track - thumb) * (extent - view) / (track - thumb), null, true);
        UpdateThumb();
    }

    void OnThumbReleased(object sender, PointerRoutedEventArgs e) { EndThumbDrag(); ScrollTrack.ReleasePointerCapture(e.Pointer); }
    void OnThumbCaptureLost(object sender, PointerRoutedEventArgs e) => EndThumbDrag();

    void EndThumbDrag()
    {
        if (!draggingThumb) return;
        draggingThumb = false;
        if (!overThumb) ThumbWide(false);
        thumbFade.Restart();
    }

    void OnThumbWheel(object sender, PointerRoutedEventArgs e)
    {
        ListScroller.ChangeView(null, ListScroller.VerticalOffset - e.GetCurrentPoint(ScrollTrack).Properties.MouseWheelDelta, null);
        e.Handled = true;
    }

    // ---------------- 小工具 ----------------

    static T? DataOf<T>(object source) where T : class
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: T t }) return t;
        return null;
    }

    // TextBox 自带的「清空」叉号和原型不符：模板加载时机不定（窗口还没显示时先设了文字），几个时机都收一次
    // 叉号是获得焦点、有字时才延迟创建的，所以排到这一轮布局之后再找
    void HideDelete() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        if (FindDelete(Input) is { } b && b.Width != 0) { b.MinWidth = 0; b.Width = 0; b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false; }
    });

    static Button? FindDelete(DependencyObject root)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "DeleteButton" } b) return b;
            if (FindDelete(child) is { } found) return found;
        }
        return null;
    }
}
