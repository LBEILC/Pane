using Pane.Localization;
// 批量重命名窗口逻辑（设计负责人，040）。照原型 prototype/rename.html：
// 左边规则按顺序叠加（拖左边的握把调整顺序，开关暂时不用某条），右边实时预览（停手 90 ms 重算）；
// 有问题的行标红、右端写原因，有任何问题就不能执行；执行后行停在「原名 → 新名」并标「已改名」，再动规则才按新名字重新预览。
// 「添加规则」「预设」都在窗口里就地展开，不弹菜单。Ctrl+Enter 执行，Ctrl+Z 撤销刚才那次，Esc 收起展开的面板，Ctrl+W 关。
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class RenameView : UserControl, ISecondaryView
{
    // 七种规则：名字、图标、一句话说明（添加规则时显示）
    static readonly (RenameRuleKind Kind, string Name, string Glyph, string Desc)[] Kinds =
    [
        (RenameRuleKind.Replace, Strings.Rename_Replace, "replace", Strings.Rename_ReplaceDescription),
        (RenameRuleKind.Insert, Strings.Rename_Insert, "insert", Strings.Rename_InsertDescription),
        (RenameRuleKind.Number, Strings.Rename_Number, "hash", Strings.Rename_NumberDescription),
        (RenameRuleKind.Remove, Strings.Rename_Delete, "eraser", Strings.Rename_DeleteDescription),
        (RenameRuleKind.Case, Strings.Rename_Case, "case", Strings.Rename_CaseDescription),
        (RenameRuleKind.Extension, Strings.Rename_Extension, "ext", Strings.Rename_ExtensionDescription),
        (RenameRuleKind.Date, Strings.Rename_Date, "calendar", Strings.Rename_DateDescription),
    ];
    const int MaxRules = 20, MaxPresets = 30;
    static readonly string[] Orders = [Strings.Rename_OrderOriginal, Strings.Rename_OrderName, Strings.Rename_OrderTime];

    sealed class RuleItem(RenameRule rule)
    {
        public RenameRule Rule = rule;
        public Border Card = null!;
        public TextBlock? Error;
    }

    readonly IBatchRenamer renamer;
    readonly List<RuleItem> rules = [];
    readonly List<RenameRow> rows = [];          // 按给进来的顺序
    readonly Dictionary<string, DateTime> times = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherQueueTimerHolder debounce;
    readonly SlidingHighlight hover;
    List<RenameRow> shown = [];
    IReadOnlyList<RenamePreview> preview = [];
    CancellationTokenSource? previewRun;
    int order;
    bool topmost, busy;
    // 执行之后：行停在「原名 → 新名」，直到再动规则；undo = 能撤销的那一次
    bool applied;
    string? undoId;
    List<(RenameRow Row, string OldPath)> undoRows = [];

    public RenameView(IBatchRenamer renamer)
    {
        this.renamer = renamer;
        InitializeComponent();
        debounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(90), () => _ = Refresh());
        hover = new SlidingHighlight(RowHoverLayer, hover: true);
        PreviewKeyDown += OnKeyDown;
        BuildKinds();
        Unloaded += (_, _) => { previewRun?.Cancel(); hover.Dispose(); };
    }

    static App Host => (App)Application.Current;

    /// <summary>打开（或复用）窗口时宿主调用：换成这批文件；第一次打开时规则接着上次执行时的。</summary>
    public void Open(IReadOnlyList<string> files)
    {
        if (rules.Count == 0)
        {
            var last = Host.Store.Current.Rename.LastRules;
            SetRules(last.Length > 0 ? last : [new RenameRule(RenameRuleKind.Replace)], animate: false);
        }
        rows.Clear(); times.Clear();
        AddFiles(files, refresh: false);
        ResetApplied();
        _ = Refresh();
    }

    /// <summary>换成这组规则（设计预览用）。</summary>
    public void UseRules(IEnumerable<RenameRule> list) { SetRules(list, animate: false); Schedule(); }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(940, 600);
    public Size MinimumSize => new(760, 440);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => FocusFirstField(rules.FirstOrDefault());
    public void OnHidden() => previewRun?.Cancel();

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;
        ToolTipService.SetToolTip(PinButton, topmost ? Strings.Common_AlwaysOnTopEnabled : Strings.Common_AlwaysOnTop);
        TopmostChanged?.Invoke(this, topmost);
    }

    // ---------------- 文件 ----------------

    void AddFiles(IEnumerable<string> files, bool refresh = true)
    {
        var have = new HashSet<string>(rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            if (!string.IsNullOrWhiteSpace(f) && have.Add(f)) rows.Add(new RenameRow(f));
        UpdateWhere();
        if (refresh) { ResetApplied(); _ = Refresh(); }
    }

    void UpdateWhere()
    {
        if (rows.Count == 0) { WhereText.Text = Strings.Rename_EmptyFiles; return; }
        var folders = rows.Select(r => System.IO.Path.GetDirectoryName(r.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        WhereText.Inlines.Clear();
        WhereText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = string.Format(Strings.Rename_FileCount, (object?)(rows.Count)), FontWeight = FontWeights.SemiBold, Foreground = SmallStrongProto.Foreground });
        WhereText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = folders.Count == 1 ? $" · {folders[0]}" : string.Format(Strings.Rename_FolderCountSuffix, (object?)(folders.Count)) });
        ToolTipService.SetToolTip(WhereText, string.Join("\n", folders.Take(8)) + (folders.Count > 8 ? "\n…" : ""));
    }

    async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var picked = await picker.PickMultipleFilesAsync();
            if (picked is { Count: > 0 }) AddFiles(picked.Select(f => f.Path));
        }
        catch (Exception ex) { Host.ReportFailure(Strings.Rename_AddFiles, ex); }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Strings.Rename_AddSelected;
        DropHint.Opacity = 1;
    }

    void OnDragLeave(object sender, DragEventArgs e) => DropHint.Opacity = 0;

    async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Opacity = 0;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        AddFiles(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
    }

    // ---------------- 预览 ----------------

    void Schedule() { ResetApplied(); debounce.Restart(); }

    // 执行过以后再动规则：按改好的名字重新开始
    void ResetApplied()
    {
        if (!applied) return;
        applied = false;
        foreach (var r in rows) r.OldName = System.IO.Path.GetFileName(r.Path);
    }

    List<RenameRow> Ordered() => order switch
    {
        1 => [.. rows.OrderBy(r => r.OldName, NaturalComparer.Instance)],
        2 => [.. rows.OrderBy(r => TimeOf(r.Path))],
        _ => [.. rows],
    };

    DateTime TimeOf(string path)
    {
        if (!times.TryGetValue(path, out var t)) times[path] = t = File.Exists(path) || Directory.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
        return t;
    }

    IReadOnlyList<RenameRule> Rules => [.. rules.Select(r => r.Rule)];

    async Task Refresh()
    {
        debounce.Stop();
        previewRun?.Cancel();
        var cts = previewRun = new CancellationTokenSource();
        var list = Ordered();
        IReadOnlyList<RenamePreview> result;
        try { result = await renamer.PreviewAsync([.. list.Select(r => r.Path)], Rules, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { if (cts == previewRun) Host.ReportFailure(Strings.Rename_Preview, e); return; }
        if (cts != previewRun || applied) return;
        for (int i = 0; i < list.Count && i < result.Count; i++) list[i].Show(result[i]);
        preview = result;
        if (!list.SequenceEqual(shown))
        {
            shown = list;
            List.ItemsSource = shown.ToList();   // 只给副本（issue 7 的教训：ItemsRepeater 不知道原列表变了）
            hover.Hide();
        }
        EmptyBox.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool bad = result.Any(p => p.Issue == RenameIssue.BadPattern);
        foreach (var r in rules)
            if (r.Error is { } err) err.Visibility = bad && r.Rule.Enabled && r.Rule.Regex && BadRegex(r.Rule) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    static bool BadRegex(RenameRule r)
    {
        if (r.Find.Length == 0) return false;
        try { _ = new Regex(r.Find); return false; } catch (ArgumentException) { return true; }
    }

    int ChangeCount => preview.Count(p => p.Issue == RenameIssue.None);
    int ProblemCount => preview.Count(p => p.Issue is not (RenameIssue.None or RenameIssue.Unchanged));

    void UpdateSummary(string? doneText = null, int failed = 0)
    {
        Summary.Children.Clear();
        if (doneText is not null)
        {
            Summary.Children.Add(Dot(doneText, PaneTokens.Success));
            if (failed > 0) Summary.Children.Add(Dot(string.Format(Strings.Rename_FailedCount, (object?)(failed)), PaneTokens.Danger));
            GoText.Text = Strings.Rename_Finished;
            GoButton.IsEnabled = false;
            return;
        }
        int change = ChangeCount, same = preview.Count(p => p.Issue == RenameIssue.Unchanged), problems = ProblemCount;
        bool badPattern = preview.Any(p => p.Issue == RenameIssue.BadPattern);
        if (rows.Count == 0) Summary.Children.Add(Dot(Strings.Rename_SelectFilesFirst, null));
        else if (badPattern) Summary.Children.Add(Dot(rules.Any(r => r.Rule.Enabled && r.Rule.Regex && BadRegex(r.Rule)) ? Strings.Rename_InvalidRegex : Strings.Rename_InvalidRule, PaneTokens.Danger));
        else
        {
            Summary.Children.Add(Dot(string.Format(Strings.Rename_ChangedCount, (object?)(change)), Text2Proto.Foreground));
            if (same > 0) Summary.Children.Add(Dot(string.Format(Strings.Rename_UnchangedCount, (object?)(same)), Text3Proto.Foreground));
            if (problems > 0) Summary.Children.Add(Dot(string.Format(Strings.Rename_IssuesCount, (object?)(problems)), PaneTokens.Danger));
        }
        GoButton.IsEnabled = change > 0 && problems == 0 && !busy;
        GoText.Text = change > 0 ? string.Format(Strings.Rename_RenameCount, (object?)(change)) : Strings.Common_RenameAction;
    }

    // 底栏的一项：小圆点 + 文字；圆点颜色表示类别，警示类文字也用警示色
    FrameworkElement Dot(string text, Brush? color)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        if (color is not null) p.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = color, VerticalAlignment = VerticalAlignment.Center });
        bool status = ReferenceEquals(color, PaneTokens.Danger) || ReferenceEquals(color, PaneTokens.Success);
        p.Children.Add(new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, FontFamily = SmallProto.FontFamily, FontSize = SmallProto.FontSize,
            Foreground = ReferenceEquals(color, PaneTokens.Danger) ? PaneTokens.Danger : status ? SmallStrongProto.Foreground : SmallProto.Foreground,
        });
        return p;
    }

    void OnSort(object sender, RoutedEventArgs e)
    {
        order = (order + 1) % Orders.Length;
        SortText.Text = Orders[order];
        Schedule();
        _ = Refresh();
    }

    // 预览行：悬停底色滑过去，右端的 × 出现；点 × 把这一项移出列表（不改它）
    void OnListPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var el = RowElement(e.OriginalSource);
        foreach (var r in shown) r.Hovered = el?.DataContext == r;
        if (el is not null) hover.MoveTo(el); else hover.Hide();
    }

    void OnListPointerExited(object sender, PointerRoutedEventArgs e)
    {
        foreach (var r in shown) r.Hovered = false;
        hover.Hide();
    }

    void OnListTapped(object sender, TappedRoutedEventArgs e)
    {
        if (RowElement(e.OriginalSource) is not { DataContext: RenameRow row }) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != List; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: "drop" })
            {
                rows.Remove(row);
                UpdateWhere();
                ResetApplied();
                _ = Refresh();
                return;
            }
    }

    FrameworkElement? RowElement(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != List; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: RenameRow } el && VisualTreeHelper.GetParent(el) == List) return el;
        return null;
    }

    // ---------------- 执行、撤销 ----------------

    void OnGo(object sender, RoutedEventArgs e) => _ = Apply();

    async Task Apply()
    {
        if (busy || applied || !GoButton.IsEnabled) return;
        await Refresh();   // 用最新的规则再算一遍，免得执行的和看到的不一样
        if (ChangeCount == 0 || ProblemCount > 0) return;
        busy = true;
        GoButton.IsEnabled = false;
        GoText.Text = Strings.Rename_Running;
        var plan = preview;
        var byPath = shown.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = await renamer.ApplyAsync(plan, CancellationToken.None);
            var failed = result.Failed.ToDictionary(f => f.Path, f => f.Reason, StringComparer.OrdinalIgnoreCase);
            undoRows = [];
            applied = true;
            var moved = new List<(string From, string To)>();
            foreach (var p in plan)
            {
                if (p.Issue != RenameIssue.None || !byPath.TryGetValue(p.Path, out var row)) continue;
                if (failed.TryGetValue(p.Path, out var reason)) { row.ShowResult(false, reason); continue; }
                undoRows.Add((row, row.Path));
                var to = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(row.Path) ?? "", p.NewName);
                moved.Add((row.Path, to));
                row.Path = to;
                row.ShowResult(true, null);
            }
            // 同一批里改了文件夹，又改了它里面的东西：里面那些的路径要换成新的文件夹名。从最深的文件夹往外换
            foreach (var (from, to) in moved.OrderByDescending(m => m.From.Length))
            {
                var prefix = System.IO.Path.TrimEndingDirectorySeparator(from) + System.IO.Path.DirectorySeparatorChar;
                var target = System.IO.Path.TrimEndingDirectorySeparator(to) + System.IO.Path.DirectorySeparatorChar;
                foreach (var r in rows)
                    if (r.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) r.Path = target + r.Path[prefix.Length..];
            }
            times.Clear();
            undoId = result.UndoId;
            UndoButton.Visibility = undoId is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateSummary(string.Format(Strings.Rename_CompleteCount, (object?)(result.Renamed)), failed.Count);
            UpdateWhere();
            SaveLastRules();
        }
        catch (Exception ex) { Host.ReportFailure(Strings.Common_Rename, ex); UpdateSummary(); }
        finally { busy = false; }
    }

    void OnUndo(object sender, RoutedEventArgs e) => _ = Undo();

    async Task Undo()
    {
        if (busy || undoId is not { } id) return;
        busy = true;
        try
        {
            var result = await renamer.UndoAsync(id, CancellationToken.None);
            var failed = new HashSet<string>(result.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var (row, old) in undoRows)
                if (!failed.Contains(row.Path)) row.Path = old;
            // 有没改回去的：服务给了新的 UndoId 就留着，可以再点一次撤销重试
            undoId = result.Failed.Count > 0 ? result.UndoId : null;
            undoRows = undoId is null ? [] : [.. undoRows.Where(u => failed.Contains(u.Row.Path))];
            UndoButton.Visibility = undoId is null ? Visibility.Collapsed : Visibility.Visible;
            times.Clear();
            applied = true;   // 让 ResetApplied 把行的原名换回撤销后的名字
            ResetApplied();
            UpdateWhere();
            busy = false;
            await Refresh();
            if (result.Failed.Count > 0) Host.ReportFailure(Strings.Common_Undo, new IOException(string.Format(Strings.Rename_UndoFailed, (object?)(result.Failed.Count), (object?)(result.Failed[0].Reason))));
        }
        catch (Exception ex) { Host.ReportFailure(Strings.Common_Undo, ex); }
        finally { busy = false; }
    }

    void SaveLastRules()
    {
        try { Host.Store.Save(Host.Store.Current with { Rename = Host.Store.Current.Rename with { LastRules = [.. Rules] } }); }
        catch (Exception) { /* 记不住上次的规则不影响这次改名 */ }
    }

    // ---------------- 规则卡片 ----------------

    void SetRules(IEnumerable<RenameRule> list, bool animate)
    {
        rules.Clear();
        RulesPanel.Children.Clear();
        foreach (var r in list.Take(MaxRules)) AddRuleCard(r, animate);
        UpdateAddButton();
    }

    void AddRuleCard(RenameRule rule, bool animate)
    {
        var item = new RuleItem(rule);
        item.Card = BuildCard(item);
        rules.Add(item);
        RulesPanel.Children.Add(item.Card);
        if (animate) Enter(item.Card);
    }

    // 新卡片：从上面一点淡入落下
    static void Enter(UIElement el)
    {
        el.Opacity = 0;
        el.Translation = new Vector3(0, -6, 0);
        el.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(Token("PaneDurNormal")) };
        el.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(Token("PaneDurNormal")) };
        el.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { el.Opacity = 1; el.Translation = Vector3.Zero; });
    }

    static double Token(string key) => GlassParameters.Token(key) * Host.Glass.Current.MotionSpeed;

    Border BuildCard(RuleItem item)
    {
        var kind = Kinds.First(k => k.Kind == item.Rule.Kind);
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(24, 4, 0, 0) };
        var name = new TextBlock { Text = kind.Name, VerticalAlignment = VerticalAlignment.Center, FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize, FontWeight = FontWeights.SemiBold, Foreground = SmallStrongProto.Foreground };

        var grip = new Border { Width = 18, Height = PaneTokens.ControlXs, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new PaneIcon { Glyph = "grip", Width = 14, Height = 14, Foreground = Text3Proto.Foreground } };
        ToolTipService.SetToolTip(grip, Strings.Rename_DragOrder);
        grip.PointerEntered += (_, _) => grip.Background = HoverProto.Background;
        grip.PointerExited += (_, _) => { if (dragging != item) grip.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); };
        grip.PointerPressed += (s, e) => StartDrag(item, grip, e);
        grip.PointerMoved += (s, e) => MoveDrag(item, e);
        grip.PointerReleased += (s, e) => EndDrag(item, grip);
        grip.PointerCaptureLost += (s, e) => EndDrag(item, grip);

        var icon = new Border { Width = PaneTokens.ControlXs, Height = PaneTokens.ControlXs, CornerRadius = new CornerRadius(6), Background = SelectedProto.Background, BorderBrush = SelectedProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new PaneIcon { Glyph = kind.Glyph, Width = 13, Height = 13, Foreground = SmallStrongProto.Foreground } };
        var sw = new PaneSwitch(item.Rule.Enabled, FillProto.Background, FillProto.BorderBrush, AccentProto.Background)
        { RenderTransform = new ScaleTransform { ScaleX = 0.82, ScaleY = 0.82, CenterX = 18, CenterY = 11 }, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(sw, item.Rule.Enabled ? Strings.Rename_DisableRule : Strings.Rename_EnableRule);
        var del = new Button { Style = (Style)Application.Current.Resources["PaneIconButton"], Width = PaneTokens.ControlXs, Height = PaneTokens.ControlXs, CornerRadius = new CornerRadius(6),
            Content = new PaneIcon { Glyph = "close", Width = 13, Height = 13 } };
        ToolTipService.SetToolTip(del, Strings.Rename_DeleteRule);
        del.Click += (_, _) => RemoveRule(item);

        var head = new Grid { Height = PaneTokens.ControlSm, ColumnSpacing = 6 };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        AddAt(head, grip, 0); AddAt(head, icon, 1); AddAt(head, name, 2); AddAt(head, sw, 4); AddAt(head, del, 5);

        var card = new Border
        {
            Padding = new Thickness(6, 6, 8, 10), CornerRadius = (CornerRadius)Application.Current.Resources["PaneRadiusItem"],
            Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { head, body } },
        };
        void Dim() { body.Opacity = name.Opacity = item.Rule.Enabled ? 1 : 0.42; }
        sw.Toggled += (_, on) =>
        {
            item.Rule = item.Rule with { Enabled = on };
            ToolTipService.SetToolTip(sw, on ? Strings.Rename_DisableRule : Strings.Rename_EnableRule);
            Dim();
            Schedule();
        };
        Dim();
        FillBody(item, body);
        return card;
    }

    static void AddAt(Grid g, FrameworkElement el, int column) { Grid.SetColumn(el, column); g.Children.Add(el); }

    void RemoveRule(RuleItem item)
    {
        if (dragging is not null) return;
        rules.Remove(item);
        var card = item.Card;
        card.IsHitTestVisible = false;
        card.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(Token("PaneDurFast")) };
        card.Opacity = 0;
        _ = RemoveLater(card);
        UpdateAddButton();
        Schedule();
    }

    async Task RemoveLater(UIElement card)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Token("PaneDurFast")));
        RulesPanel.Children.Remove(card);
    }

    // 每种规则的输入项（照原型 ruleBody）。改动一律 item.Rule = item.Rule with {…} 然后重新预览
    void FillBody(RuleItem item, StackPanel body)
    {
        var r = item.Rule;
        void Set(Func<RenameRule, RenameRule> change) { item.Rule = change(item.Rule); Schedule(); }
        switch (r.Kind)
        {
            case RenameRuleKind.Replace:
            {
                var find = Text(r.Find, r.Regex ? Strings.Rename_RegexPlaceholder : Strings.Rename_FindPlaceholder, r.Regex, v => Set(x => x with { Find = v }));
                var with = Text(r.Text, r.Regex ? Strings.Rename_ReplacementGroupPlaceholder : Strings.Rename_ReplacementEmptyPlaceholder, false, v => Set(x => x with { Text = v }));
                var space = Hint(Strings.Rename_FindSpace);
                void Space() => space.Visibility = item.Rule.Find.Length > 0 && item.Rule.Find.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                ((TextBox)find.Child).TextChanged += (_, _) => Space();
                Space();
                item.Error = Hint(Strings.Rename_RegexHint);
                item.Error.Foreground = PaneTokens.Danger;
                item.Error.Visibility = Visibility.Collapsed;
                var regex = Toggle(Strings.Rename_Regex, r.Regex, on =>
                {
                    Set(x => x with { Regex = on });
                    var box = (TextBox)find.Child;
                    box.FontFamily = on ? MonoProto.FontFamily : SmallStrongProto.FontFamily;
                    box.PlaceholderText = on ? Strings.Rename_RegexPlaceholder : Strings.Rename_FindPlaceholder;
                    ((TextBox)with.Child).PlaceholderText = on ? Strings.Rename_ReplacementGroupPlaceholder : Strings.Rename_ReplacementEmptyPlaceholder;
                });
                body.Children.Add(Line(Strings.Rename_Find, find));
                body.Children.Add(Line(Strings.Rename_ReplaceWith, with));
                body.Children.Add(Line("", Row(regex, Toggle(Strings.Rename_CaseSensitive, r.MatchCase, on => Set(x => x with { MatchCase = on })))));
                body.Children.Add(space);
                body.Children.Add(item.Error);
                break;
            }
            case RenameRuleKind.Insert:
            {
                var index = IndexLine(item, Strings.Rename_BeforeCharacter, Strings.Rename_AfterCharacter, Set);
                body.Children.Add(Line(Strings.Rename_Text, Text(r.Text, Strings.Rename_InsertPlaceholder, false, v => Set(x => x with { Text = v }))));
                body.Children.Add(Line(Strings.Rename_Position, Segments([("Start", Strings.Rename_Beginning), ("End", Strings.Rename_End), ("Index", Strings.Rename_CharacterIndex)], r.Position.ToString(), v =>
                {
                    Set(x => x with { Position = Enum.Parse<RenamePosition>(v) });
                    index.Visibility = v == "Index" ? Visibility.Visible : Visibility.Collapsed;
                })));
                index.Visibility = r.Position == RenamePosition.Index ? Visibility.Visible : Visibility.Collapsed;
                body.Children.Add(index);
                break;
            }
            case RenameRuleKind.Number:
                body.Children.Add(Line(Strings.Rename_Position, Segments([("Start", Strings.Rename_Beginning), ("End", Strings.Rename_End)], r.Position == RenamePosition.Start ? "Start" : "End", v => Set(x => x with { Position = Enum.Parse<RenamePosition>(v) }))));
                body.Children.Add(Line(Strings.Rename_NumberStart, Row(
                    Number(r.Start, 0, v => Set(x => x with { Start = v })),
                    Mid(Strings.Rename_NumberStep), Number(r.Step, 1, v => Set(x => x with { Step = v })),
                    Mid(Strings.Rename_NumberDigits, Strings.Rename_NumberPadding), Number(r.Digits, 1, v => Set(x => x with { Digits = Math.Min(v, 9) })))));
                body.Children.Add(Line(Strings.Rename_NumberSeparator, Text(r.Separator, Strings.Rename_NumberSeparatorPlaceholder, false, v => Set(x => x with { Separator = v }))));
                break;
            case RenameRuleKind.Remove:
            {
                var fromText = Mid(r.FromEnd ? Strings.Rename_DeleteFromEnd : Strings.Rename_DeleteFromStart);
                var tail = Mid(r.FromEnd ? Strings.Rename_DeleteBackward : Strings.Rename_DeleteForward);
                body.Children.Add(Line(Strings.Rename_Delete, Row(fromText, Number(r.Index, 1, v => Set(x => x with { Index = v })), tail, Number(r.Count, 1, v => Set(x => x with { Count = v })), Mid(Strings.Rename_CharacterCount))));
                body.Children.Add(Line("", Row(Toggle(Strings.Rename_CountFromEnd, r.FromEnd, on =>
                {
                    Set(x => x with { FromEnd = on });
                    fromText.Text = on ? Strings.Rename_DeleteFromEnd : Strings.Rename_DeleteFromStart;
                    tail.Text = on ? Strings.Rename_DeleteBackward : Strings.Rename_DeleteForward;
                }))));
                break;
            }
            case RenameRuleKind.Case:
                body.Children.Add(Line(Strings.Rename_ChangeTo, Segments([("Lower", Strings.Rename_LowerCase), ("Upper", Strings.Rename_UpperCase), ("Title", Strings.Rename_TitleCase)], r.Case.ToString(), v => Set(x => x with { Case = Enum.Parse<RenameCase>(v) }))));
                body.Children.Add(Line("", Row(Toggle(Strings.Rename_IncludeExtension, r.IncludeExtension, on => Set(x => x with { IncludeExtension = on })))));
                break;
            case RenameRuleKind.Extension:
                body.Children.Add(Line(Strings.Rename_ChangeTo, Text(r.Text, Strings.Rename_ExtensionPlaceholder, false, v => Set(x => x with { Text = v }))));
                body.Children.Add(Hint(Strings.Rename_ExtensionDescriptionHint));
                break;
            case RenameRuleKind.Date:
                body.Children.Add(Line(Strings.Rename_Date, Segments([("Modified", Strings.Rename_ModifiedDate), ("Created", Strings.Rename_CreatedDate)], r.DateSource.ToString(), v => Set(x => x with { DateSource = Enum.Parse<RenameDateSource>(v) }))));
                body.Children.Add(Line(Strings.Rename_DateFormat, Text(r.DateFormat, "yyyy-MM-dd", true, v => Set(x => x with { DateFormat = v }))));
                body.Children.Add(Line(Strings.Rename_Position, Segments([("Start", Strings.Rename_Beginning), ("End", Strings.Rename_End)], r.Position == RenamePosition.Start ? "Start" : "End", v => Set(x => x with { Position = Enum.Parse<RenamePosition>(v) }))));
                body.Children.Add(Line(Strings.Rename_DateSeparator, Text(r.Separator, Strings.Rename_DateSeparatorPlaceholder, false, v => Set(x => x with { Separator = v }))));
                break;
        }
    }

    // 插入文字「第几个字」那一行：第 [n] 个字前面 / 倒数第 [n] 个字后面 + 从末尾数
    FrameworkElement IndexLine(RuleItem item, string before, string after, Action<Func<RenameRule, RenameRule>> set)
    {
        var r = item.Rule;
        var lead = Mid(r.FromEnd ? Strings.Rename_CharacterFromEnd : Strings.Rename_CharacterFromStart);
        var tail = Mid(r.FromEnd ? after : before);
        var toggle = Toggle(Strings.Rename_CountFromEnd, r.FromEnd, on =>
        {
            set(x => x with { FromEnd = on });
            lead.Text = on ? Strings.Rename_CharacterFromEnd : Strings.Rename_CharacterFromStart;
            tail.Text = on ? after : before;
        });
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Row(lead, Number(r.Index, 1, v => set(x => x with { Index = v })), tail));
        AddAt(g, toggle, 1);
        return Line("", g);
    }

    Grid Line(string label, FrameworkElement content)
    {
        var g = new Grid { ColumnSpacing = 6, MinHeight = PaneTokens.ControlSm };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontFamily = XsProto.FontFamily, FontSize = XsProto.FontSize, Foreground = XsProto.Foreground });
        content.VerticalAlignment = VerticalAlignment.Center;
        AddAt(g, content, 1);
        return g;
    }

    static StackPanel Row(params FrameworkElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var i in items) { i.VerticalAlignment = VerticalAlignment.Center; p.Children.Add(i); }
        return p;
    }

    TextBlock Mid(string text, string? tip = null)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontFamily = XsProto.FontFamily, FontSize = XsProto.FontSize, Foreground = XsProto.Foreground };
        if (tip is not null) ToolTipService.SetToolTip(t, tip);
        return t;
    }

    TextBlock Hint(string text) => new()
    {
        Text = text, Margin = new Thickness(46, 0, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 16,
        FontFamily = XsProto.FontFamily, FontSize = XsProto.FontSize, Foreground = XsProto.Foreground,
    };

    // 输入框：控件底色 + 细描边的圆角框（和设置页一样），高 28
    Border Text(string value, string placeholder, bool mono, Action<string> changed)
    {
        var box = new TextBox
        {
            Text = value, PlaceholderText = placeholder, Height = PaneTokens.ControlSm, MinHeight = 0, Padding = new Thickness(8, 5, 8, 0),
            FontFamily = mono ? MonoProto.FontFamily : SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
        };
        box.TextChanged += (_, _) => changed(box.Text);
        return Frame(box);
    }

    Border Number(int value, int min, Action<int> changed)
    {
        var box = new TextBox
        {
            Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 48, Height = PaneTokens.ControlSm, MinHeight = 0, MinWidth = 0, Padding = new Thickness(4, 5, 4, 0),
            TextAlignment = TextAlignment.Center, FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize, InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };
        box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out var v)) changed(Math.Clamp(v, min, 9999)); };
        box.LostFocus += (_, _) => { if (!int.TryParse(box.Text, out var v) || v < min) box.Text = min.ToString(System.Globalization.CultureInfo.InvariantCulture); };
        // ↑↓ 加减 1
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
            var v = int.TryParse(box.Text, out var n) ? n : min;
            box.Text = Math.Clamp(v + (e.Key == VirtualKey.Up ? 1 : -1), min, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
            box.SelectionStart = box.Text.Length;
            e.Handled = true;
        };
        var frame = Frame(box);
        frame.HorizontalAlignment = HorizontalAlignment.Left;
        return frame;
    }

    Border Frame(TextBox box) => new()
    {
        Child = box, CornerRadius = (CornerRadius)Application.Current.Resources["PaneRadiusControl"],
        Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
    };

    PaneSegmented Segments((string Value, string Label)[] options, string value, Action<string> changed)
    {
        var s = new PaneSegmented(options, value, SmallStrongProto, SmallProto, FillProto.Background, FillProto.BorderBrush);
        s.Changed += (_, v) => changed(v);
        return s;
    }

    // 开关式小标签（原型 .tog）：开着是选中底色 + 正文色，关着只有细描边
    Border Toggle(string label, bool on, Action<bool> changed)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontFamily = XsProto.FontFamily, FontSize = XsProto.FontSize };
        var b = new Border { Height = PaneTokens.ControlXs, Padding = new Thickness(9, 0, 9, 0), CornerRadius = new CornerRadius(PaneTokens.ControlXs / 2), BorderThickness = new Thickness(1), BorderBrush = FillProto.BorderBrush, Child = text };
        void Paint()
        {
            b.Background = on ? SelectedProto.Background : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            text.Foreground = on ? SmallStrongProto.Foreground : SmallProto.Foreground;
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        b.Tapped += (_, e) => { on = !on; Paint(); changed(on); e.Handled = true; };
        b.PointerEntered += (_, _) => { if (!on) b.Background = HoverProto.Background; };
        b.PointerExited += (_, _) => Paint();
        Paint();
        return b;
    }

    void FocusFirstField(RuleItem? item)
    {
        if (item is null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Find<TextBox>(item.Card)?.Focus(FocusState.Programmatic));
    }

    static T? Find<T>(DependencyObject root) where T : class
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (Find<T>(c) is { } found) return found;
        }
        return null;
    }

    // ---------------- 拖动排序 ----------------
    // 拿起的卡片跟着指针走；越过别的卡片中线，别的卡片滑开让位；松手后才真正换顺序（拖动中不动子元素，指针捕获不会丢）

    RuleItem? dragging;
    double dragStart, dragHeight;
    int dragFrom, dragTo;

    void StartDrag(RuleItem item, UIElement grip, PointerRoutedEventArgs e)
    {
        if (dragging is not null || rules.Count < 2) return;
        dragging = item;
        dragFrom = dragTo = rules.IndexOf(item);
        dragStart = e.GetCurrentPoint(RulesPanel).Position.Y;
        dragHeight = item.Card.ActualHeight + RulesPanel.Spacing;
        grip.CapturePointer(e.Pointer);
        item.Card.TranslationTransition = null;
        Canvas.SetZIndex(item.Card, 10);
        item.Card.Background = SelectedProto.Background;
        item.Card.Shadow = new ThemeShadow();
        item.Card.Translation = new Vector3(0, 0, 16);
        foreach (var r in rules.Where(r => r != item)) r.Card.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(Token("PaneDurNormal")) };
        e.Handled = true;
    }

    void MoveDrag(RuleItem item, PointerRoutedEventArgs e)
    {
        if (dragging != item) return;
        var dy = e.GetCurrentPoint(RulesPanel).Position.Y - dragStart;
        // 别的卡片按原来的位置判断中线
        var top = LayoutTop(item.Card) + dy;
        var mid = top + item.Card.ActualHeight / 2;
        int to = 0;
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i] == item) continue;
            var c = rules[i].Card;
            if (mid > LayoutTop(c) + c.ActualHeight / 2) to++;
        }
        item.Card.Translation = new Vector3(0, (float)dy, 16);
        if (to == dragTo) return;
        dragTo = to;
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i] == item) continue;
            // 原来在拖动卡片后面、现在被越过的往上让；原来在前面、被越过的往下让
            float shift = i > dragFrom && i <= dragTo ? -(float)dragHeight : i < dragFrom && i >= dragTo ? (float)dragHeight : 0;
            rules[i].Card.Translation = new Vector3(0, shift, 0);
        }
    }

    double LayoutTop(FrameworkElement el) => Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(el).Top;

    void EndDrag(RuleItem item, UIElement grip)
    {
        if (dragging != item) return;
        dragging = null;
        grip.ReleasePointerCaptures();
        ((Border)grip).Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var visualTop = LayoutTop(item.Card) + item.Card.Translation.Y;
        foreach (var r in rules) { r.Card.TranslationTransition = null; r.Card.Translation = Vector3.Zero; }
        item.Card.Shadow = null;
        item.Card.Background = FillProto.Background;
        Canvas.SetZIndex(item.Card, 0);
        if (dragTo != dragFrom)
        {
            rules.Remove(item);
            rules.Insert(dragTo, item);
            RulesPanel.Children.Remove(item.Card);
            RulesPanel.Children.Insert(dragTo, item.Card);
            Schedule();
        }
        // 松手：从指针下的位置滑到新位置
        RulesPanel.UpdateLayout();
        item.Card.Translation = new Vector3(0, (float)(visualTop - LayoutTop(item.Card)), 0);
        item.Card.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(Token("PaneDurNormal")) };
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => item.Card.Translation = Vector3.Zero);
    }

    // ---------------- 添加规则 ----------------

    void BuildKinds()
    {
        for (int i = 0; i < Kinds.Length; i++)
        {
            if (i % 2 == 0) KindGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = Kinds[i];
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["PaneTextButton"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6), Height = double.NaN,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new PaneIcon { Glyph = k.Glyph, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = SmallProto.Foreground },
                        new TextBlock { Text = k.Name, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            ToolTipService.SetToolTip(b, k.Desc);
            b.Click += (_, _) => AddRule(k.Kind);
            Grid.SetRow(b, i / 2); Grid.SetColumn(b, i % 2);
            KindGrid.Children.Add(b);
        }
    }

    void OnAddRule(object sender, RoutedEventArgs e) => ShowKinds(KindGrid.Visibility != Visibility.Visible);

    void ShowKinds(bool show)
    {
        KindGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AddRuleTurn.Angle = show ? 45 : 0;
        AddRuleText.Text = show ? Strings.Common_Collapse : Strings.Rename_AddRule;
        if (show) { Enter(KindGrid); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => KindGrid.StartBringIntoView()); }
    }

    void AddRule(RenameRuleKind kind)
    {
        if (rules.Count >= MaxRules) return;
        var rule = kind switch
        {
            RenameRuleKind.Date => new RenameRule(kind) { Position = RenamePosition.Start, Separator = " " },
            RenameRuleKind.Number => new RenameRule(kind) { Digits = 2, Separator = "-" },
            _ => new RenameRule(kind),
        };
        ShowKinds(false);
        AddRuleCard(rule, animate: true);
        UpdateAddButton();
        Schedule();
        var item = rules[^1];
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => item.Card.StartBringIntoView());
        FocusFirstField(item);
    }

    void UpdateAddButton()
    {
        AddRuleButton.IsEnabled = rules.Count < MaxRules;
        ToolTipService.SetToolTip(AddRuleButton, rules.Count < MaxRules ? null : string.Format(Strings.Rename_RuleLimit, (object?)(MaxRules)));
    }

    // ---------------- 预设 ----------------

    void OnPresets(object sender, RoutedEventArgs e) => ShowPresets(PresetBox.Visibility != Visibility.Visible);

    void ShowPresets(bool show)
    {
        PresetChevronTurn.Angle = show ? 180 : 0;
        PresetBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        FillPresets();
        Enter(PresetBox);
        RulesScroller.ChangeView(null, 0, null);
    }

    void FillPresets()
    {
        PresetList.Children.Clear();
        var presets = Host.Store.Current.Rename.Presets;
        if (presets.Length == 0)
        {
            PresetList.Children.Add(new TextBlock { Text = Strings.Rename_EmptyPresets, TextWrapping = TextWrapping.Wrap, FontFamily = SmallProto.FontFamily, FontSize = SmallProto.FontSize, Foreground = SmallProto.Foreground });
            return;
        }
        foreach (var p in presets)
        {
            var name = new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize, Foreground = SmallStrongProto.Foreground };
            var x = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = new PaneIcon { Glyph = "close", Width = 11, Height = 11, Foreground = Text3Proto.Foreground } };
            ToolTipService.SetToolTip(x, Strings.Rename_DeletePreset);
            x.PointerEntered += (_, _) => x.Background = HoverProto.Background;
            x.PointerExited += (_, _) => x.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            x.Tapped += (_, e) => { e.Handled = true; DeletePreset(p); };
            var chip = new Border
            {
                Height = PaneTokens.ControlSm, Padding = new Thickness(12, 0, 5, 0), CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2),
                Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { name, x } },
            };
            ToolTipService.SetToolTip(chip, string.Join("，", p.Rules.Select(r => Kinds.First(k => k.Kind == r.Kind).Name)));
            chip.PointerEntered += (_, _) => chip.Background = HoverProto.Background;
            chip.PointerExited += (_, _) => chip.Background = FillProto.Background;
            chip.Tapped += (_, _) => { SetRules(p.Rules, animate: true); ShowPresets(false); Schedule(); FocusFirstField(rules.FirstOrDefault()); };
            PresetList.Children.Add(chip);
        }
    }

    void DeletePreset(RenamePreset preset)
    {
        var s = Host.Store.Current;
        TrySave(s with { Rename = s.Rename with { Presets = [.. s.Rename.Presets.Where(p => !ReferenceEquals(p, preset))] } });
        FillPresets();
    }

    void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (rules.Count == 0) return;
        SaveButton.Visibility = Visibility.Collapsed;
        SaveBox.Visibility = Visibility.Visible;
        SaveName.Text = "";
        SaveName.Focus(FocusState.Programmatic);
    }

    void OnSaveConfirm(object sender, RoutedEventArgs e) => SavePreset();

    void OnSaveNameKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { SavePreset(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseSave(); e.Handled = true; }
    }

    void SavePreset()
    {
        var name = SaveName.Text.Trim();
        if (name.Length == 0) { SaveName.Focus(FocusState.Programmatic); return; }
        var s = Host.Store.Current;
        // 同名的覆盖；超出上限去掉最早的
        var list = s.Rename.Presets.Where(p => p.Name != name).Append(new RenamePreset(name, [.. Rules])).ToList();
        while (list.Count > MaxPresets) list.RemoveAt(0);
        if (TrySave(s with { Rename = s.Rename with { Presets = [.. list] } }))
        {
            CloseSave();
            if (PresetBox.Visibility == Visibility.Visible) FillPresets();
            UpdateSummaryNote(string.Format(Strings.Rename_PresetSaved, (object?)(name)));
        }
    }

    void CloseSave() { SaveBox.Visibility = Visibility.Collapsed; SaveButton.Visibility = Visibility.Visible; }

    static bool TrySave(PaneSettings value)
    {
        try { Host.Store.Save(value); return true; }
        catch (Exception e) { Host.ReportFailure(Strings.Rename_SavePreset, e); return false; }
    }

    // 底栏左边临时说一句（存好预设），下次预览刷新时换回汇总
    void UpdateSummaryNote(string text)
    {
        if (applied) return;
        Summary.Children.Clear();
        Summary.Children.Add(Dot(text, PaneTokens.Success));
    }

    // ---------------- 键盘 ----------------

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control);
        bool inText = FocusManager.GetFocusedElement(XamlRoot) is TextBox;
        switch (e.Key)
        {
            case VirtualKey.Enter when ctrl: _ = Apply(); break;
            case VirtualKey.Z when ctrl && !inText && undoId is not null: _ = Undo(); break;
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            case VirtualKey.Escape when KindGrid.Visibility == Visibility.Visible: ShowKinds(false); break;
            case VirtualKey.Escape when PresetBox.Visibility == Visibility.Visible: ShowPresets(false); break;
            case VirtualKey.Escape when SaveBox.Visibility == Visibility.Visible: CloseSave(); break;
            default: return;
        }
        e.Handled = true;
    }
}

/// <summary>名字按「自然顺序」排：照片 2 在照片 10 前面（和资源管理器一样）。</summary>
sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
