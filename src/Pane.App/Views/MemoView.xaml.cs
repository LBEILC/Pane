using Pane.Localization;
// 备忘录窗口（设计负责人，2026-11-25，第一期「随手记」）。照原型 prototype/memo.html（docs/design.md「备忘录」）：
// 新建的空白备忘录先放在内存里（draft），第一次有字才交给存储；什么都没写就离开（换一条、关窗口）不留下。
// 改了停手 500 ms 存，换一条、关窗口立即存。删除进回收站，底部撤销条 5 秒。
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class MemoView : UserControl, ISecondaryView, IDisposable
{
    const string DraftId = "";   // 还没交给存储的新建备忘录

    readonly INoteStore store;
    readonly ISelectionActions output;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer copiedTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    readonly List<(string Id, Border Row)> rows = [];
    string? selected;           // 选中的 id；DraftId = 新建的空白；null = 没有选中
    string draftText = "";
    bool preview, topmost, loading;
    Note? deleted;
    MarkdownStyle? style;

    public MemoView(INoteStore store, ISelectionActions output)
    {
        InitializeComponent();
        this.store = store;
        this.output = output;
        KeyHints.Fill(Hints, [(["Ctrl", "+", "N"], Strings.Memo_New), (["Ctrl", "+", "F"], Strings.Common_Search), (["Ctrl", "+", "E"], Strings.Memo_Preview), (["Ctrl", "+", "P"], Strings.Memo_Pin)],
            HintLabelProto, KbdProto);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
        undoTimer.Tick += (_, _) => { undoTimer.Stop(); UndoBar.Visibility = Visibility.Collapsed; deleted = null; };
        copiedTimer.Tick += (_, _) => { copiedTimer.Stop(); CopyIcon.Glyph = "copy"; };
        store.Changed += OnStoreChanged;
        PreviewKeyDown += OnKeyDown;
        Unloaded += OnUnloaded;
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(760, 500);
    public Size MinimumSize => new(560, 360);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { if (selected == DraftId || selected != null && !preview) FocusEditor(); else Query.Focus(FocusState.Programmatic); }
    public void OnHidden() { SaveNow(); DropDraft(); }

    void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();
    public void Dispose()
    {
        Unloaded -= OnUnloaded;
        store.Changed -= OnStoreChanged;
        saveTimer.Stop(); undoTimer.Stop(); copiedTimer.Stop();
    }

    // ---------------- 打开 ----------------

    /// <summary>热键、启动器命令「备忘录」：列表最上面多一条空白备忘录，光标在编辑区。</summary>
    public void OpenNew()
    {
        store.Refresh();
        SaveNow();
        Query.Text = "";
        preview = false;
        draftText = "";
        selected = DraftId;
        Render();
        FocusEditor();
    }

    /// <summary>从启动器搜到的、托盘：选中那一条（id 为 null = 不新建，选第一条）。</summary>
    public void OpenNote(string? id)
    {
        store.Refresh();
        SaveNow();
        DropDraft();
        Query.Text = "";
        selected = id != null && store.All.Any(n => n.Id == id) ? id : store.All.FirstOrDefault()?.Id;
        Render();
        if (selected != null && !preview) FocusEditor();
    }

    // ---------------- 列表 ----------------

    IReadOnlyList<Note> Visible()
    {
        var q = Query.Text.Trim();
        var all = store.All;
        if (q.Length == 0) return all;
        var hits = store.Search(q, 200).Select(m => m.Note.Id).ToHashSet();
        return all.Where(n => hits.Contains(n.Id)).ToList();
    }

    void Render()
    {
        RenderList();
        RenderMain();
    }

    void RenderList()
    {
        List.Children.Clear();
        rows.Clear();
        var list = Visible();
        var q = Query.Text.Trim();
        bool any = store.All.Count > 0 || selected == DraftId;
        var pinned = list.Where(n => n.Pinned).ToList();
        var others = list.Where(n => !n.Pinned).ToList();
        bool draft = selected == DraftId;
        if (pinned.Count > 0)
        {
            List.Children.Add(Label(Strings.Memo_Pinned));
            foreach (var n in pinned) AddRow(n, n.Text);
            if (others.Count > 0 || draft) List.Children.Add(Label(Strings.Common_All));
        }
        if (draft) AddRow(null, draftText);   // 新建的一条在「全部」最上面（固定的始终在前，同原型）
        foreach (var n in others) AddRow(n, n.Text);
        ListEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Text = !any ? Strings.Memo_Empty : string.Format(Strings.Memo_NotFound, (object?)q);
        CountText.Text = store.All.Count > 0 ? string.Format(Strings.Memo_Count, (object?)store.All.Count) : "";
    }

    TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(8, 10, 8, 4), FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, Foreground = SubProto.Foreground };

    void AddRow(Note? note, string text)
    {
        string id = note?.Id ?? DraftId;
        var title = TitleOf(text);
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var t = new TextBlock { Text = title.Length > 0 ? title : Strings.Memo_Untitled, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            FontFamily = TitleProto.FontFamily, FontSize = TitleProto.FontSize, FontWeight = title.Length > 0 ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = title.Length > 0 ? TitleProto.Foreground : SubProto.Foreground };
        var w = new TextBlock { Text = Files.FileKinds.When(note?.Modified ?? DateTimeOffset.Now), FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, Foreground = SubProto.Foreground };
        Grid.SetColumn(w, 1);
        var rest = Rest(text);
        if (note?.From is { } from) rest = string.Format(Strings.Memo_From, (object?)from) + " · " + rest;
        var p = new TextBlock { Text = rest.Length > 0 ? rest : " ", TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, Foreground = SubProto.Foreground };
        Grid.SetRow(p, 1); Grid.SetColumnSpan(p, 2);
        grid.Children.Add(t); grid.Children.Add(w); grid.Children.Add(p);
        var row = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid };
        Paint(row, id == selected, false);
        row.PointerEntered += (_, _) => Paint(row, id == selected, true);
        row.PointerExited += (_, _) => Paint(row, id == selected, false);
        row.Tapped += (_, _) => Select(id, focus: true);
        rows.Add((id, row));
        List.Children.Add(row);
    }

    void Paint(Border row, bool on, bool hover)
    {
        row.Background = on ? SelectedProto.Background : hover ? HoverProto.Background : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.BorderBrush = on ? SelectedProto.BorderBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // 第一行是标题（去掉开头的 #）；预览去掉 Markdown 记号（行首 #、- 、- [ ] 、>，以及 ** 和反引号），正文里的 - 保留
    static string TitleOf(string text) { int end = text.IndexOf('\n'); return (end < 0 ? text : text[..end]).TrimStart('#').Trim(); }
    static string Rest(string text)
    {
        var lines = text.Split('\n').Skip(1).Select(l =>
        {
            var s = l.TrimStart();
            if (s.StartsWith('#')) s = s.TrimStart('#');
            else if (s.StartsWith("- [ ] ") || s.StartsWith("- [x] ")) s = s[6..];
            else if (s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("> ")) s = s[2..];
            return s.Replace("**", "").Replace("`", "").Trim();
        }).Where(s => s.Length > 0);
        var r = string.Join(" ", lines);
        return r.Length > 120 ? r[..120] : r;
    }

    void Select(string? id, bool focus)
    {
        if (id == selected) { if (focus && !preview) FocusEditor(); return; }
        SaveNow();
        if (selected == DraftId) DropDraft();
        selected = id;
        Render();
        if (focus && !preview) FocusEditor();
    }

    void DropDraft()
    {
        if (selected != DraftId) return;
        selected = store.All.FirstOrDefault()?.Id;
        draftText = "";
    }

    // ---------------- 编辑 ----------------

    Note? Current => selected is { Length: > 0 } id ? store.All.FirstOrDefault(n => n.Id == id) : null;

    void RenderMain()
    {
        var note = Current;
        bool draft = selected == DraftId;
        bool has = note != null || draft;
        foreach (var b in new[] { NotePinButton, PreviewButton, CopyButton, DeleteButton }) b.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoneText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        NoneText.Text = store.All.Count == 0 ? Strings.Memo_Empty : "";
        Editor.Visibility = has && !preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewScroller.Visibility = has && preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewIcon.Glyph = preview ? "pencil" : "eye";
        NotePinIcon.IsFilled = note?.Pinned == true;
        ToolTipService.SetToolTip(NotePinButton, note?.Pinned == true ? Strings.Memo_UnpinHint : Strings.Memo_PinHint);
        Meta.Text = has ? string.Format(Strings.Memo_EditedAt, (object?)Files.FileKinds.When(note?.Modified ?? DateTimeOffset.Now)) : "";
        FromChip.Visibility = note?.From is null ? Visibility.Collapsed : Visibility.Visible;
        FromText.Text = note?.From is { } from ? string.Format(Strings.Memo_From, (object?)from) : "";
        var text = draft ? draftText : note?.Text ?? "";
        if (EditorText != text) { loading = true; Editor.Text = text; loading = false; }
        if (preview && has) RenderPreview(text);
    }

    void RenderPreview(string text)
    {
        PreviewPanel.Children.Clear();
        foreach (var block in MarkdownRender.Build(text, Md, null, out _)) PreviewPanel.Children.Add(block);
    }

    MarkdownStyle Md => style ??= new MarkdownStyle
    {
        Font = BodyProto.FontFamily, Mono = MonoProto.FontFamily, MathFont = MathProto.FontFamily, Size = BodyProto.FontSize, LineHeight = 26,
        Text1 = BodyProto.Foreground, Text2 = MonoProto.Foreground, Text3 = SubProto.Foreground,
        Stroke = CodeProto.BorderBrush, CodeFill = CodeProto.Background, Accent = (SolidColorBrush)MathProto.Foreground,
    };

    // WinUI 输入框把换行统一成 CR：读出来一律换回 LF，文件里也保持 LF
    string EditorText => Editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');

    void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        // 程序赋值后 TextChanged 隔一拍才来：内容和存储里的一样就不算修改
        if (selected is { Length: > 0 } && Current?.Text == EditorText) return;
        if (selected == DraftId)
        {
            draftText = EditorText;
            // 第一次有字：交给存储，变成真正的一条
            if (draftText.Trim().Length > 0)
            {
                try { var n = store.Add(draftText); selected = n.Id; draftText = ""; RenderList(); RenderMainMeta(); return; }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
            }
            UpdateCurrentRow(EditorText);
            return;
        }
        saveTimer.Stop(); saveTimer.Start();
        UpdateCurrentRow(EditorText);
    }

    // 只改选中那一行的标题和预览，不整列表重建（打字时不闪）
    void UpdateCurrentRow(string text)
    {
        var row = rows.FirstOrDefault(r => r.Id == selected).Row;
        if (row?.Child is not Grid g || g.Children.Count < 3) { RenderList(); return; }
        var title = TitleOf(text);
        if (g.Children[0] is TextBlock t) { t.Text = title.Length > 0 ? title : Strings.Memo_Untitled; t.FontWeight = title.Length > 0 ? FontWeights.SemiBold : FontWeights.Normal; t.Foreground = title.Length > 0 ? TitleProto.Foreground : SubProto.Foreground; }
        if (g.Children[1] is TextBlock w) w.Text = Files.FileKinds.When(DateTimeOffset.Now);
        if (g.Children[2] is TextBlock p) { var r = Rest(text); p.Text = r.Length > 0 ? r : " "; }
        Meta.Text = string.Format(Strings.Memo_EditedAt, (object?)Files.FileKinds.When(DateTimeOffset.Now));
    }

    // 草稿刚变成真正的一条：按钮、固定状态跟着换，编辑区内容不动（光标不跳）
    void RenderMainMeta()
    {
        var note = Current;
        NotePinIcon.IsFilled = note?.Pinned == true;
        Meta.Text = string.Format(Strings.Memo_EditedAt, (object?)Files.FileKinds.When(note?.Modified ?? DateTimeOffset.Now));
    }

    void SaveNow()
    {
        saveTimer.Stop();
        if (selected is not { Length: > 0 } id || Editor.Visibility != Visibility.Visible && !preview) return;
        var note = store.All.FirstOrDefault(n => n.Id == id);
        if (note == null || note.Text == EditorText) return;
        try { store.Update(id, EditorText); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

    void FocusEditor()
    {
        if (Editor.Visibility != Visibility.Visible) return;
        Editor.Focus(FocusState.Programmatic);
        Editor.Select(Editor.Text.Length, 0);
    }

    // 外部编辑、同步进来、别处存入：重建列表；正在编辑的这条只在内容不同而且编辑区没有未存的修改时才换
    void OnStoreChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (selected is { Length: > 0 } id && store.All.All(n => n.Id != id)) selected = store.All.FirstOrDefault()?.Id;
        if (saveTimer.IsEnabled) { RenderList(); return; }
        Render();
    });

    // ---------------- 按钮 ----------------

    void OnNew(object sender, RoutedEventArgs e) => OpenNew();
    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPinWindow(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;
        ToolTipService.SetToolTip(PinButton, topmost ? Strings.Common_AlwaysOnTopEnabled : Strings.Common_AlwaysOnTop);
        TopmostChanged?.Invoke(this, topmost);
    }

    void OnPinNote(object sender, RoutedEventArgs e)
    {
        SaveNow();
        if (Current is not { } n) return;
        try { store.SetPinned(n.Id, !n.Pinned); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        Render();
    }

    void OnTogglePreview(object sender, RoutedEventArgs e) => TogglePreview();
    void TogglePreview()
    {
        SaveNow();
        preview = !preview;
        RenderMain();
        if (!preview) FocusEditor();
    }

    async void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var text = selected == DraftId ? draftText : Current?.Text ?? "";
        if (text.Length == 0) return;
        try { await output.CopyAsync(text, CancellationToken.None); CopyIcon.Glyph = "check"; copiedTimer.Stop(); copiedTimer.Start(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

    void OnDelete(object sender, RoutedEventArgs e) => Delete();
    void Delete()
    {
        if (selected == DraftId) { DropDraft(); Render(); return; }
        SaveNow();
        if (Current is not { } n) return;
        var list = Visible();
        int i = list.ToList().FindIndex(x => x.Id == n.Id);
        try { store.Delete(n.Id); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); return; }
        deleted = n;
        UndoText.Text = string.Format(Strings.Memo_Deleted, (object?)(n.Title.Length > 0 ? n.Title : Strings.Memo_Untitled));
        UndoBar.Visibility = Visibility.Visible;
        undoTimer.Stop(); undoTimer.Start();
        var rest = list.Where(x => x.Id != n.Id).ToList();
        selected = rest.Count == 0 ? null : rest[Math.Clamp(i, 0, rest.Count - 1)].Id;
        Render();
    }

    void OnUndo(object sender, RoutedEventArgs e)
    {
        if (deleted is not { } n) return;
        try { store.Restore(n); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); return; }
        deleted = null;
        undoTimer.Stop();
        UndoBar.Visibility = Visibility.Collapsed;
        selected = n.Id;
        Render();
    }

    void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        var list = Visible();
        if (selected is { Length: > 0 } id && list.All(n => n.Id != id)) { SaveNow(); selected = list.FirstOrDefault()?.Id; }
        Render();
    }

    // ---------------- 键盘 ----------------

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool inQuery = Query.FocusState != FocusState.Unfocused;
        bool inEditor = Editor.FocusState != FocusState.Unfocused;
        switch (e.Key)
        {
            case VirtualKey.N when ctrl: OpenNew(); break;
            case VirtualKey.F when ctrl: Query.Focus(FocusState.Programmatic); Query.SelectAll(); break;
            case VirtualKey.E when ctrl: TogglePreview(); break;
            case VirtualKey.P when ctrl: OnPinNote(this, new RoutedEventArgs()); break;
            case VirtualKey.D when ctrl: Delete(); break;
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            case VirtualKey.Escape when inQuery && Query.Text.Length > 0: Query.Text = ""; break;
            case VirtualKey.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            case VirtualKey.Down or VirtualKey.Up when inQuery: Move(e.Key == VirtualKey.Down ? 1 : -1); break;
            case VirtualKey.Enter when inQuery: preview = false; RenderMain(); FocusEditor(); break;
            case VirtualKey.Delete when !inQuery && !inEditor: Delete(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Move(int step)
    {
        var ids = rows.Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        int i = ids.IndexOf(selected ?? "\0");
        Select(ids[Math.Clamp(i + step, 0, ids.Count - 1)], focus: false);
    }
}
