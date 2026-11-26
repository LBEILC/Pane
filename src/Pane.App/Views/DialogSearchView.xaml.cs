using Pane.Localization;
// 对话框搜索框（设计负责人，2026-11-23，issue 81）。docs/design.md「对话框搜索」，原型 prototype/dialog-search.html。
// 视图只管内容和按键；贴在哪、多宽、往哪边展开由宿主（App.DialogBar）按对话框位置算好，通过 Up / RequestedHeight 配合。
// - 空输入：已打开的资源管理器窗口、最近使用（IFolderJump.Entries）；打字：文件夹、文件（IDialogSearch.SearchAsync）。
// - 文件夹：Enter 跳过去。「打开」里的文件：Enter 直接打开、Shift+Enter 仅填入文件名（设置 OpenOnEnter 为 false 时两者对调）。
//   「另存为」里的文件：Enter 保存到此处（文件名不变），Ctrl+Enter 使用此文件名。
// - 结果没列全时最后一行「在文件窗口中查看全部 N 个」（2026-11-25，任务 125）：Tab 在任何一行上都打开文件窗口「为对话框选」，选中这一行时 Enter 也打开。
//   选了一项（跳转）就记进搜索记忆，下次同样的输入排最前。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class DialogSearchView : UserControl, IPanelView
{
    /// <summary>一条结果：文件夹或文件。Group：Explorer / Recent / Folders / Files / More（「查看全部」那一行）。</summary>
    sealed record Item(string Path, string Name, bool Folder, string Group, IReadOnlyList<TextSpan>? Highlights = null)
    {
        public bool More => Group == "more";
    }

    readonly SlidingHighlight highlight;
    readonly List<(Item Item, Grid Row)> items = [];
    int selected = -1;
    CancellationTokenSource? search;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer debounce;

    public const double CollapsedHeight = 40, ListMax = 312, FootHeight = 33;

    public DialogSearchView()
    {
        InitializeComponent();
        Controls.PaneCaret.Attach(Input);
        highlight = new SlidingHighlight(HighlightLayer);
        debounce = DispatcherQueue.CreateTimer();
        debounce.IsRepeating = false;
        debounce.Interval = TimeSpan.FromMilliseconds(120);
        debounce.Tick += (_, _) => _ = Refresh();
        Rows.PointerMoved += (_, e) => { if (RowIndex(e.OriginalSource) is { } i && i != selected) Select(i, scroll: false); };
        Rows.Tapped += (_, e) => { if (RowIndex(e.OriginalSource) is { } i) Run(items[i].Item, Action.Enter); };
        // 输入框模板自带的清除按钮不要（同剪贴板）：Esc 清空
        Loaded += (_, _) =>
        {
            if (Find(Input, "DeleteButton") is Button b) { b.MinWidth = 0; b.Width = 0; b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false; }
        };
    }

    static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement f && f.Name == name) return f;
            if (Find(child, name) is { } found) return found;
        }
        return null;
    }

    // ---------------- 宿主给的 ----------------

    /// <summary>当前对话框（宿主在 DialogChanged 时更新）。</summary>
    public FileDialogInfo? Info { get; private set; }
    /// <summary>「打开」里的文件按 Enter 直接打开（设置 FolderJumpSettings.OpenOnEnter）。</summary>
    public bool OpenOnEnter { get; set; } = true;
    public Func<IReadOnlyList<FolderJumpEntry>> Entries { get; set; } = () => [];
    public Func<string, FileDialogInfo?, CancellationToken, Task<DialogSearchResult>>? Search { get; set; }
    /// <summary>贴在对话框上方：结果往上展开（搜索行在最下面）。</summary>
    public bool Up { get; private set; }
    /// <summary>这一侧最多能给多高（DIP），宿主按屏幕剩下的空间算。</summary>
    public double Room { get; set; } = CollapsedHeight + ListMax + FootHeight;
    public bool Expanded { get; private set; }

    /// <summary>选好了：宿主让对话框跳过去（folder、fileName 为 null 表示文件名不动、confirm）。</summary>
    public event EventHandler<(string Folder, string? FileName, bool Confirm)>? Navigate;
    /// <summary>高度变了（展开、收起、结果数变化），宿主重新摆位置。</summary>
    public event EventHandler? LayoutChanged;
    /// <summary>Esc 收起后焦点还给对话框。</summary>
    public event EventHandler? ReturnFocus;
    /// <summary>「查看全部」（任务 125）：宿主打开文件窗口为这个对话框选。Query 是输入，Focus 是当时选中的那一项（「查看全部」行本身为 null）。</summary>
    public event EventHandler<(string Query, string? Focus)>? ViewAll;
    /// <summary>选了一项：宿主记进搜索记忆（ISearchMemory.RecordFile）。空输入时（已打开、最近使用两组）不记。</summary>
    public event EventHandler<(string Query, string Path)>? Chosen;

    public void Apply(FileDialogInfo info, bool up)
    {
        bool changedDialog = Info?.Dialog != info.Dialog;
        Info = info;
        if (Up != up) { Up = up; Arrange(); }
        FilterChip.Visibility = info.Extensions is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        FilterText.Text = string.Format(Strings.DialogSearch_FilterChip, info.FilterName ?? string.Join("、", info.Extensions ?? []));
        if (changedDialog) { Input.Text = ""; Collapse(); }
    }

    // 往上展开：结果和按键提示放在搜索行上面（原型 .dsb.up 的 column-reverse）
    void Arrange()
    {
        Root.RowDefinitions[0].Height = Up ? new GridLength(1, GridUnitType.Star) : new GridLength(CollapsedHeight);
        Root.RowDefinitions[1].Height = Up ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Root.RowDefinitions[2].Height = Up ? new GridLength(CollapsedHeight) : GridLength.Auto;
        Grid.SetRow(Head, Up ? 2 : 0);
        Grid.SetRow(Body, 1);
        Grid.SetRow(Foot, Up ? 0 : 2);
        BodyLine.VerticalAlignment = Up ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        FootLine.VerticalAlignment = Up ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Scroller.Margin = Up ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
    }

    /// <summary>现在要的高度（DIP）。</summary>
    public double RequestedHeight
    {
        get
        {
            if (!Expanded) return CollapsedHeight;
            double list = Math.Min(ListMax, Math.Max(120, Room - CollapsedHeight - FootHeight));
            Rows.Measure(new Size(Math.Max(1, ActualWidth - 12), double.PositiveInfinity));
            double content = Math.Max(56, Rows.DesiredSize.Height + 10);
            return CollapsedHeight + 1 + Math.Min(list, content) + FootHeight;
        }
    }

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(560, CollapsedHeight);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }   // 尺寸由宿主按对话框算（SetBounds）
    public event EventHandler? CloseRequested { add { } remove { } }
    public UIElement? DragRegion => null;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() { Collapse(); }

    /// <summary>设计预览：预先填入搜索词。</summary>
    public void SetQuery(string text) { Input.Text = text; Input.SelectionStart = text.Length; }

    /// <summary>对话框在前台时按了启动器热键：聚焦搜索框。</summary>
    public void FocusSearch()
    {
        Input.Focus(FocusState.Programmatic);
        Input.SelectionStart = Input.Text.Length;
        Expand();
    }

    // ---------------- 展开 / 收起 ----------------

    void OnInputFocus(object sender, RoutedEventArgs e) => Expand();

    void Expand()
    {
        if (Expanded) return;
        Expanded = true;
        Body.Visibility = Foot.Visibility = Visibility.Visible;
        _ = Refresh();
    }

    public void Collapse()
    {
        search?.Cancel();
        if (!Expanded) return;
        Expanded = false;
        Body.Visibility = Foot.Visibility = Visibility.Collapsed;
        highlight.Hide();
        Rows.Children.Clear();
        items.Clear();
        selected = -1;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!Expanded) Expand();
        debounce.Stop();
        if (Input.Text.Trim().Length == 0) _ = Refresh(); else debounce.Start();
    }

    // ---------------- 结果 ----------------

    async Task Refresh()
    {
        search?.Cancel();
        var token = (search = new CancellationTokenSource()).Token;
        var q = Input.Text.Trim();
        var list = new List<Item>();
        string? problem = null;
        int total = 0;
        if (q.Length == 0)
        {
            try
            {
                foreach (var e in Entries())
                    list.Add(new Item(e.Path, e.Name, true, e.Source == FolderJumpSource.Explorer ? "explorer" : "recent"));
            }
            catch (Exception) { }
        }
        else if (Search is { } run)
        {
            try
            {
                var result = await run(q, Info, token);
                if (token.IsCancellationRequested) return;
                foreach (var h in result.Items) list.Add(new Item(h.Path, h.Name, h.Category == FileCategory.Folder, h.Category == FileCategory.Folder ? "folders" : "files", h.Highlights));
                total = result.Total;
            }
            catch (OperationCanceledException) { return; }
            catch (FileSearchUnavailableException e) { problem = e.Message; }
            catch (Exception e) { problem = e.Message; }
        }
        if (token.IsCancellationRequested || !Expanded) return;
        // 没列全：最后一行「在文件窗口中查看全部 N 个」
        if (total > list.Count) list.Add(new Item("", string.Format(Strings.Launcher_AllFileResults, (object?)total), false, "more"));
        Draw(list, problem);
    }

    void Draw(List<Item> list, string? problem)
    {
        Rows.Children.Clear();
        items.Clear();
        highlight.Hide();
        selected = -1;
        string? last = null;
        foreach (var it in list)
        {
            if (it.Group != last && !it.More)
            {
                last = it.Group;
                Rows.Children.Add(new TextBlock
                {
                    Style = LabelProto.Style,
                    Text = it.Group switch { "explorer" => Strings.FolderJump_Explorer, "recent" => Strings.FolderJump_Recent, "folders" => Strings.DialogSearch_Folders, _ => Strings.DialogSearch_Files },
                });
            }
            var row = RowFor(it);
            items.Add((it, row));
            Rows.Children.Add(row);
        }
        if (items.Count == 0)
            Rows.Children.Add(new TextBlock
            {
                Text = problem ?? (Input.Text.Trim().Length == 0 ? Strings.FolderJump_Empty : Strings.DialogSearch_NoMatches),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 16), TextWrapping = TextWrapping.Wrap,
                FontFamily = HintLabelProto.FontFamily, FontSize = 12, Foreground = HintLabelProto.Foreground,
            });
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        UpdateLayout();
        if (items.Count > 0) Select(0, scroll: false);
        else Hints.Children.Clear();
        Scroller.ChangeView(null, Up ? Scroller.ScrollableHeight : 0, null, true);
    }

    // 行同启动器的列表行，缩到 42 高：28 图标格、名字（命中加粗）、所在路径；选中行右侧 Enter 键帽
    Grid RowFor(Item it)
    {
        if (it.More) return MoreRow(it);
        var row = new Grid { Height = 42, Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 10, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Tag = it };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = it.Folder ? "folder" : IsImage(it.Name) ? "image" : "doc";
        row.Children.Add(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["PaneFillControlBrush"], BorderBrush = (Brush)Application.Current.Resources["PaneStrokeBrush"],
            Child = new PaneIcon { Glyph = glyph, Width = 15, Height = 15, Foreground = HintLabelProto.Foreground },
        });
        var name = new TextBlock { FontFamily = HintLabelProto.FontFamily, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)Application.Current.Resources["PaneText1Brush"] };
        AddRuns(name, it.Name, it.Highlights);
        var dir = it.Folder ? it.Path : System.IO.Path.GetDirectoryName(it.Path) ?? it.Path;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = dir, FontFamily = HintLabelProto.FontFamily, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = HintLabelProto.Foreground });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var key = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        KeyCombo.SetText(key, "Enter");
        Grid.SetColumn(key, 2);
        row.Children.Add(key);
        return row;
    }

    // 「查看全部」行：同启动器的 More 行，38 高、外链图标居中在 28 的图标格里、次要文字色，右边 Tab 键帽常显
    Grid MoreRow(Item it)
    {
        var row = new Grid { Height = 38, Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 10, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Tag = it };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new PaneIcon { Glyph = "external", Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = HintLabelProto.Foreground });
        var text = new TextBlock { Text = it.Name, VerticalAlignment = VerticalAlignment.Center, FontFamily = HintLabelProto.FontFamily, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)Application.Current.Resources["PaneText2Brush"] };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var key = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        KeyCombo.SetText(key, "Tab");
        Grid.SetColumn(key, 2);
        row.Children.Add(key);
        return row;
    }

    static void AddRuns(TextBlock block, string text, IReadOnlyList<TextSpan>? spans)
    {
        int at = 0;
        foreach (var s in (spans ?? []).OrderBy(s => s.Start))
        {
            if (s.Start < at || s.Start + s.Length > text.Length) continue;
            if (s.Start > at) block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[at..s.Start] });
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text.Substring(s.Start, s.Length), FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            at = s.Start + s.Length;
        }
        if (at < text.Length) block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[at..] });
    }

    static bool IsImage(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".psd" or ".heic" or ".svg" or ".tif" or ".tiff" or ".ico" or ".avif";

    int? RowIndex(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is Grid { Tag: Item it }) return items.FindIndex(x => ReferenceEquals(x.Item, it));
        return null;
    }

    void Select(int index, bool scroll = true)
    {
        if (items.Count == 0) return;
        if (selected >= 0 && selected < items.Count && !items[selected].Item.More) KeyOf(items[selected].Row).Opacity = 0;
        selected = (index % items.Count + items.Count) % items.Count;
        var row = items[selected].Row;
        KeyOf(row).Opacity = 1;
        highlight.MoveTo(row);
        if (scroll) row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        BuildHints();
    }

    static FrameworkElement KeyOf(Grid row) => (FrameworkElement)row.Children[2];

    // ---------------- 动作 ----------------

    enum Action { Enter, Shift, Ctrl }

    (string[] keys, string label)[] HintItems()
    {
        var it = selected >= 0 && selected < items.Count ? items[selected].Item : null;
        var list = new List<(string[], string)> { (["↑", "↓"], Strings.Clipboard_Select) };
        if (it is { More: true }) list.Add((["Enter"], Strings.Launcher_OpenFiles));
        else if (it is { Folder: true }) list.Add((["Enter"], Strings.DialogSearch_Jump));
        else if (it is not null && Info?.Save == true) { list.Add((["Enter"], Strings.DialogSearch_SaveHere)); list.Add((["Ctrl", "+", "Enter"], Strings.DialogSearch_UseName)); }
        else if (it is not null)
        {
            list.Add((["Enter"], OpenOnEnter ? Strings.DialogSearch_OpenNow : Strings.DialogSearch_FillName));
            list.Add((["Shift", "+", "Enter"], OpenOnEnter ? Strings.DialogSearch_FillName : Strings.DialogSearch_OpenNow));
        }
        if (it is { More: false } && HasMore) list.Add((["Tab"], Strings.DialogSearch_ViewAllHint));
        list.Add((["Esc"], Strings.Common_Collapse));
        return list.ToArray();
    }

    void BuildHints() => KeyHints.Fill(Hints, HintItems(), HintLabelProto, KbdProto);

    bool HasMore => items.Count > 0 && items[^1].Item.More;

    void OpenAll()
    {
        var focus = selected >= 0 && selected < items.Count && !items[selected].Item.More ? items[selected].Item.Path : null;
        ViewAll?.Invoke(this, (Input.Text.Trim(), focus));
    }

    void Run(Item it, Action how)
    {
        if (it.More) { OpenAll(); return; }
        var q = Input.Text.Trim();
        if (q.Length > 0) Chosen?.Invoke(this, (q, it.Path));
        var dir = it.Folder ? it.Path : System.IO.Path.GetDirectoryName(it.Path);
        if (dir is null) return;
        if (it.Folder) Navigate?.Invoke(this, (dir, null, false));
        else if (Info?.Save == true) Navigate?.Invoke(this, (dir, how == Action.Ctrl ? it.Name : null, false));
        else
        {
            bool open = OpenOnEnter ? how == Action.Enter : how == Action.Shift;
            Navigate?.Invoke(this, (dir, it.Name, open));
        }
        Input.Text = "";
        Collapse();
    }

    void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Down: if (Expanded) Select(selected + 1); break;
            case VirtualKey.Up: if (Expanded) Select(selected - 1); break;
            case VirtualKey.Enter:
                if (selected >= 0 && selected < items.Count) Run(items[selected].Item, Down(VirtualKey.Control) ? Action.Ctrl : Down(VirtualKey.Shift) ? Action.Shift : Action.Enter);
                break;
            case VirtualKey.Tab when HasMore: OpenAll(); break;
            case VirtualKey.Escape:
                if (Input.Text.Length > 0) Input.Text = "";
                else { Collapse(); ReturnFocus?.Invoke(this, EventArgs.Empty); }
                break;
            default: return;
        }
        e.Handled = true;
    }

    void OnExplorerClick(object sender, RoutedEventArgs e)
    {
        // 同 Ctrl+G：跳到最近用的资源管理器文件夹；没有开着的就列出文件夹
        var first = Entries().FirstOrDefault(x => x.Source == FolderJumpSource.Explorer);
        if (first is null) { Input.Text = ""; FocusSearch(); return; }
        Navigate?.Invoke(this, (first.Path, null, false));
    }

    void OnRecentClick(object sender, RoutedEventArgs e) { Input.Text = ""; FocusSearch(); }
}
