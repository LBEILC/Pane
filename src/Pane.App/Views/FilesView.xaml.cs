// 文件窗口逻辑（设计负责人）。交互照原型 prototype/launcher.html「二级窗口：文件搜索」：
// 输入防抖后搜；左侧分类带数量，Tab / Shift+Tab 切换；结果分批加载（每批 50，滚到接近底部再要）；
// 上下键选中，Enter 打开，Ctrl+Enter 打开所在文件夹，可拖出；右侧预览可关；底部换排序。
// Esc 只清空输入（不关窗口）；Ctrl+W 关。
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

public sealed partial class FilesView : UserControl, ISecondaryView
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
    bool loading, topmost;
    string? focusPath;
    CancellationTokenSource? pending, previewPending;

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
        KeyHints.Fill(Hints, [(["Enter"], "打开"), (["Ctrl", "+", "Enter"], "打开所在文件夹"), (["Esc"], "清空")], HintLabelProto, KbdProto);
        PreviewKeyDown += OnKeyDown;   // 先于输入框拿到方向键、Tab、Enter
        Loaded += (_, _) => HideDelete();
        Input.GotFocus += (_, _) => HideDelete();
        ShowEmpty();
        UpdatePreview();
    }

    /// <summary>打开（或复用）窗口时宿主调用：带上查询文字，focus 是要选中的文件（启动器里文件格按 Tab）。</summary>
    public void Open(string query, string? focus)
    {
        focusPath = focus;
        SelectCategory(FileCategory.All, search: false);
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

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;   // 开着：图钉实心（状态靠形状表达）
        ToolTipService.SetToolTip(PinButton, topmost ? "总在最前：开" : "总在最前");
        TopmostChanged?.Invoke(this, topmost);
    }

    // ---------------- 搜索 ----------------

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        HideDelete();
        debounce.Restart();
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
        if (q.Length == 0 || search is null)
        {
            foreach (var c in categories) c.SetCount(null);
            Bind();
            ShowEmpty(search is null ? "文件搜索暂时用不了" : null);
            return;
        }
        FilePage page;
        try { page = await search.SearchAsync(new FileQuery(q, category, sort), 0, Batch, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (FileSearchUnavailableException ex) { if (cts == pending) { Bind(); ShowEmpty(ex.Message); } return; }
        catch (Exception ex) { if (cts == pending) { Bind(); ShowEmpty("搜索失败：" + (App.FailureReason(ex) ?? "出了点问题")); } return; }
        if (cts.IsCancellationRequested) return;
        total = page.Total;
        foreach (var c in categories) c.SetCount(page.Counts?.GetValueOrDefault(c.Category));
        if (page.Counts is null) _ = FetchCounts(q, cts);
        Append(page.Items);
        Bind();
        int focus = focusPath is { } p ? rows.FindIndex(r => string.Equals(r.Hit.Path, p, StringComparison.OrdinalIgnoreCase)) : -1;
        focusPath = null;
        Select(Math.Max(0, focus));
        if (rows.Count == 0) ShowEmpty($"没有找到「{q}」\n换个关键词，或者在左边换个分类");
        else Empty.Visibility = Visibility.Collapsed;
    }

    // 分类数量服务在后台算（第一页先给 null，021），没有「算完了」的通知：每 100ms 用同一关键词再要一次（只要 1 条，读它的缓存），最多约 2.5 秒
    async Task FetchCounts(string q, CancellationTokenSource cts)
    {
        for (int i = 0; i < 25 && search is not null; i++)
        {
            try
            {
                await Task.Delay(100, cts.Token);   // 服务算数量约 0.2 秒（021 实测），每 100ms 读一次缓存，数字约 0.25 秒内出来
                var page = await search.SearchAsync(new FileQuery(q, category, sort), 0, 1, cts.Token);
                if (cts != pending || page.Counts is null) continue;
                foreach (var c in categories) c.SetCount(page.Counts.GetValueOrDefault(c.Category));
                return;
            }
            catch (Exception) { return; }   // 取消（换了关键词）或出错：就不显示数字
        }
    }

    void Append(IEnumerable<FileHit> hits) { foreach (var h in hits) rows.Add(new FileRow(h, rows.Count)); }

    void Bind()
    {
        listHighlight.Hide();
        List.ItemsSource = null;
        // 界面只拿副本（issue 7）：ItemsRepeater 记着列表长度，List 改了不会通知它。原来直接给 rows，
        // 下次搜索先清空 rows 再等结果，这期间一滚动 / 重新排版，它就按旧长度取行，越界崩溃（打字快、输入法改词时常见）
        List.ItemsSource = rows.ToList();
        ListScroller.ChangeView(null, 0, null, true);
        TotalText.Text = rows.Count == 0 ? "" : $"共 {total:N0} 个";
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
            var page = await search.SearchAsync(new FileQuery(Input.Text.Trim(), category, sort), rows.Count, Batch, cts?.Token ?? default);
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
        Empty.Text = message ?? "输入文件名的一部分开始搜索\n由 Everything 提供，结果是实时的";
        Empty.Visibility = Visibility.Visible;
        TotalText.Text = "";
        UpdatePreview();
    }

    // ---------------- 分类与排序 ----------------

    void SelectCategory(FileCategory c, bool search = true)
    {
        category = c;
        foreach (var item in categories) item.IsSelected = item.Category == c;
        MoveRailHighlight(animate: true);
        if (search) _ = Search();
    }

    void MoveRailHighlight(bool animate)
    {
        if (Rail.ContainerFromIndex(categories.FindIndex(x => x.Category == category)) is FrameworkElement el) railHighlight.MoveTo(el, animate);
    }

    void OnRailPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != Rail; d = VisualTreeHelper.GetParent(d))
            if (d is ContentPresenter { DataContext: CategoryItem } el) { railHover.MoveTo(el); return; }
    }

    void OnRailPointerExited(object sender, PointerRoutedEventArgs e) => railHover.Hide();

    void OnRailTapped(object sender, TappedRoutedEventArgs e)
    {
        if (DataOf<CategoryItem>(e.OriginalSource) is { } item) SelectCategory(item.Category);
        Input.Focus(FocusState.Programmatic);
    }

    void OnSort(object sender, RoutedEventArgs e)
    {
        sort = sort == FileSort.Relevance ? FileSort.Modified : FileSort.Relevance;
        SortText.Text = sort == FileSort.Relevance ? "按相关度" : "按修改时间";
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
        if (rows.Count == 0) { selected = -1; listHighlight.Hide(); UpdatePreview(); return; }
        index = Math.Clamp(index, 0, rows.Count - 1);
        bool had = selected >= 0 && selected < rows.Count;
        if (had) rows[selected].IsSelected = false;
        selected = index;
        rows[selected].IsSelected = true;
        if (List.TryGetElement(index) is FrameworkElement el) listHighlight.MoveTo(el, animate: had);
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
        var items = new List<(string, string)> { ("类型", FileKinds.Name(hit.Category) is var n && hit.Category != FileCategory.All ? n : "文件") };
        if (hit.Category == FileCategory.Folder) { if (p?.FolderItems is { } count) items.Add(("内容", $"{count:N0} 项")); }
        else items.Add(("大小", FileKinds.Size(hit.Size)));
        if (p?.ImageWidth is { } w && p.ImageHeight is { } h) items.Add(("尺寸", $"{w} × {h}"));
        items.Add(("修改", FileKinds.When(hit.Modified)));
        items.Add(("位置", System.IO.Path.GetDirectoryName(hit.Path) ?? hit.Path));
        foreach (var (label, value) in items)
        {
            if (value.Length == 0) continue;
            Meta.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int r = Meta.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, FontFamily = HintLabelProto.FontFamily, FontSize = HintLabelProto.FontSize, Foreground = HintLabelProto.Foreground };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontFamily = MetaValueProto.FontFamily, FontSize = MetaValueProto.FontSize, Foreground = MetaValueProto.Foreground, IsTextSelectionEnabled = label == "位置" };
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            Meta.Children.Add(l); Meta.Children.Add(v);
        }
    }

    // ---------------- 打开、所在文件夹、复制路径、拖出 ----------------

    async Task Run(Func<string, CancellationToken, Task> action, string what)
    {
        if (Current is not { } row || search is null) return;
        try { await action(row.Hit.Path, CancellationToken.None); }
        catch (Exception e) { if (App.FailureReason(e) is { } reason) Empty.Text = $"{what}失败：{reason}"; }
    }

    void OnOpenSelected(object sender, RoutedEventArgs e) => _ = Run(search!.OpenAsync, "打开");
    void OnRevealSelected(object sender, RoutedEventArgs e) => _ = Run(search!.RevealAsync, "打开所在文件夹");
    void OnCopyPath(object sender, RoutedEventArgs e) { if (Current is { } row) _ = output.CopyAsync(row.Hit.Path, CancellationToken.None); }

    void OnListTapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { } row) Select(row.Index);
        Input.Focus(FocusState.Programmatic);
    }

    void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowAt(e.OriginalSource) is { } row) { Select(row.Index); _ = Run(search!.OpenAsync, "打开"); }
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
            case VirtualKey.Enter when Current is not null: _ = Run(ctrl ? search!.RevealAsync : search!.OpenAsync, ctrl ? "打开所在文件夹" : "打开"); break;
            case VirtualKey.Tab:
                int i = categories.FindIndex(c => c.Category == category);
                SelectCategory(categories[(i + (shift ? categories.Count - 1 : 1)) % categories.Count].Category);
                break;
            case VirtualKey.Escape:
                // Esc 不关窗口：只清空输入
                if (Input.Text.Length > 0) Input.Text = "";
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
        ScrollThumb.Margin = new Thickness(0, (track - thumb) * ListScroller.VerticalOffset / (extent - view), 0, 0);
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
