using Pane.Localization;
// 备忘录标签（设计负责人，2026-11-26，任务 144）。照原型 prototype/memo.html 的「标签」场景（docs/design.md「备忘录标签」）：
// 搜索框下面的筛选小片；条目第二行末尾和编辑区顶上的标签小片；Ctrl+T 左栏就地换成标签列表（备忘录标签 + 从剪贴板标签添加）。
// 和剪贴板是两套标签：从剪贴板标签里选，就按同名同色新建一个备忘录标签再打上，之后两边各改各的。
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Pane.Contracts;
using Pane.Views.Clipboard;
using Pane.Views.Controls;
using Windows.Foundation;

namespace Pane.Views;

public sealed partial class MemoView
{
    /// <summary>筛选小片末尾的「管理」：宿主打开 设置 → 备忘录（标签那一组在里面）。</summary>
    public event EventHandler? ManageTags;

    string? filterTag;          // 正在按哪个标签看；null = 全部
    bool moreTags;              // 筛选小片展开了全部
    bool tagMode;               // 左栏是标签选择
    int tagSel;
    string savedQuery = "";     // 进标签选择前的搜索词，出来时放回去

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    int CountOf(string tag) => store.All.Count(n => n.Tags.Any(t => Same(t, tag)));
    NoteTag? TagOf(string name) => store.Tags.FirstOrDefault(t => Same(t.Name, name));

    // ---------------- 小片 ----------------

    /// <summary>条目上、编辑区顶上的标签小片：样式同剪贴板条目上的（16 高、中性浅底、10.5 字，可带 5 的色点）。</summary>
    Border Pill(string name, Brush? dot, bool more = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (dot != null) row.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name, FontFamily = SubProto.FontFamily, FontSize = 10.5, Foreground = MonoProto.Foreground, Opacity = more ? 0.68 : 1, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Height = 16, Padding = new Thickness(more ? 5 : 6, 0, more ? 5 : 6, 0), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
            Background = CodeProto.Background, BorderBrush = CodeProto.BorderBrush, BorderThickness = new Thickness(1), Child = row };
    }

    /// <summary>最多 <paramref name="max"/> 个标签，其余「+N」。<paramref name="tap"/> 给了就能点（编辑区顶上那行：点了按它筛）。</summary>
    StackPanel Pills(IReadOnlyList<string> tags, int max, Action<string>? tap = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        FillPills(panel, tags, max, tap);
        return panel;
    }

    void FillPills(StackPanel panel, IReadOnlyList<string> tags, int max, Action<string>? tap = null)
    {
        panel.Children.Clear();
        foreach (var name in tags.Take(max))
        {
            var pill = Pill(name, TagColors.Dot(TagOf(name)?.Color));
            if (tap != null) { var n = name; pill.Tapped += (_, e) => { e.Handled = true; tap(n); }; }
            panel.Children.Add(pill);
        }
        if (tags.Count > max) panel.Children.Add(Pill("+" + (tags.Count - max), null, more: true));
    }

    // ---------------- 筛选 ----------------

    /// <summary>筛选小片：选中式小标签（选中底 + 主题色描边），24 高、xs 字；条数 text-3。</summary>
    Border Chip(string label, int? count, Brush? dot, bool on, Action tap, string? glyph = null, bool plain = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null) row.Children.Add(new PaneIcon { Glyph = glyph, Width = 12, Height = 12, Foreground = SubProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
        if (dot != null) row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, VerticalAlignment = VerticalAlignment.Center,
            Foreground = on ? TitleProto.Foreground : plain ? SubProto.Foreground : MonoProto.Foreground });
        if (count is { } c) row.Children.Add(new TextBlock { Text = c.ToString(System.Globalization.CultureInfo.InvariantCulture), FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize,
            Foreground = SubProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Height = 24, Padding = new Thickness(9, 0, 9, 0), CornerRadius = new CornerRadius(12), Child = row,
            Background = on ? SelectedProto.Background : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = on ? AccentProto.BorderBrush : plain ? null : CodeProto.BorderBrush, BorderThickness = new Thickness(1) };
        if (!on) { chip.PointerEntered += (_, _) => chip.Background = HoverProto.Background; chip.PointerExited += (_, _) => chip.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); }
        chip.Tapped += (_, e) => { e.Handled = true; tap(); };
        return chip;
    }

    void RenderFilter()
    {
        Filter.Children.Clear();
        var tags = store.Tags;
        if (tagMode || tags.Count == 0) { Filter.Visibility = Visibility.Collapsed; return; }
        Filter.Visibility = Visibility.Visible;
        var chips = new List<Border> { Chip(Strings.Common_All, null, null, filterTag == null, () => FilterBy(null)) };
        var tagChips = new List<(Border Chip, string Name)>();
        foreach (var t in tags)
        {
            var name = t.Name;
            var chip = Chip(name, CountOf(name), TagColors.Dot(t.Color), filterTag != null && Same(filterTag, name), () => FilterBy(name));
            chips.Add(chip); tagChips.Add((chip, name));
        }
        var manage = Chip(Strings.Memo_ManageTags, null, null, false, () => ManageTags?.Invoke(this, EventArgs.Empty), glyph: "gear", plain: true);
        ToolTipService.SetToolTip(manage, Strings.Memo_ManageTagsHint);
        if (moreTags)
        {
            foreach (var c in chips) Filter.Children.Add(c);
            Filter.Children.Add(Chip(Strings.Memo_LessTags, null, null, false, () => { moreTags = false; RenderFilter(); }, plain: true));
            Filter.Children.Add(manage);
            return;
        }
        // 收起时只露两行：按量出来的宽度模拟折行，放不下的标签收成「还有 N 个」（它和「管理」也要放得下；选中的那个不收）
        double width = Math.Max(1, (Filter.ActualWidth > 0 ? Filter.ActualWidth : 216));
        double Measure(Border b) { b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return b.DesiredSize.Width; }
        int Rows(IEnumerable<double> widths)
        {
            int rows = 1; double x = 0;
            foreach (var w in widths) { if (x > 0 && x + 4 + w > width) { rows++; x = w; } else x += (x > 0 ? 4 : 0) + w; }
            return rows;
        }
        var shown = chips.ToList();
        var hidden = new List<Border>();
        double manageW = Measure(manage);
        var widthOf = chips.ToDictionary(c => c, Measure);
        Border? more = null;
        while (true)
        {
            var seq = shown.Select(c => widthOf[c]).ToList();
            if (hidden.Count > 0)
            {
                more = Chip(string.Format(Strings.Memo_MoreTags, (object?)hidden.Count), null, null, false, () => { moreTags = true; RenderFilter(); }, plain: true);
                seq.Add(Measure(more));
            }
            seq.Add(manageW);
            if (Rows(seq) <= 2) break;
            var drop = tagChips.Select(t => t.Chip).LastOrDefault(c => shown.Contains(c) && !(filterTag != null && tagChips.First(t => t.Chip == c).Name.Equals(filterTag, StringComparison.OrdinalIgnoreCase)));
            if (drop == null) break;
            shown.Remove(drop); hidden.Insert(0, drop);
        }
        foreach (var c in shown) Filter.Children.Add(c);
        if (hidden.Count > 0 && more != null) Filter.Children.Add(more);
        Filter.Children.Add(manage);
    }

    void FilterBy(string? name)
    {
        filterTag = name != null && !(filterTag != null && Same(filterTag, name)) ? name : null;
        var list = Visible();
        if (selected is { Length: > 0 } id && list.All(n => n.Id != id)) { SaveNow(); selected = list.FirstOrDefault()?.Id; }
        Render();
    }

    // ---------------- 标签选择（Ctrl+T） ----------------

    sealed record TagRow(string? Create, NoteTag? Tag, bool FromClipboard);

    List<TagRow> TagRows()
    {
        var q = Query.Text.Trim();
        bool Hit(string name) => q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase);
        var memo = store.Tags.Where(t => Hit(t.Name)).Select(t => new TagRow(null, t, false));
        var clipTags = ClipboardTags();
        var clip = clipTags.Where(t => TagOf(t.Name) == null && Hit(t.Name)).Select(t => new TagRow(null, new NoteTag(t.Name, t.Color), true));
        var rows = new List<TagRow>();
        if (q.Length > 0 && !store.Tags.Any(t => Same(t.Name, q)) && !clipTags.Any(t => Same(t.Name, q))) rows.Add(new TagRow(q, null, false));
        rows.AddRange(memo); rows.AddRange(clip);
        return rows;
    }

    static IReadOnlyList<ClipTag> ClipboardTags()
    {
        try { return ((App)Application.Current).ClipboardHistory?.Tags() ?? []; }
        catch (Exception) { return []; }
    }

    void RenderTags()
    {
        List.Children.Clear();
        rows.Clear();
        ListEmpty.Visibility = Visibility.Collapsed;
        var note = Current;
        var list = TagRows();
        tagSel = list.Count == 0 ? 0 : Math.Clamp(tagSel, 0, list.Count - 1);
        int memoStart = list.FindIndex(r => r.Tag != null && !r.FromClipboard), clipStart = list.FindIndex(r => r.FromClipboard);
        for (int i = 0; i < list.Count; i++)
        {
            if (i == memoStart) List.Children.Add(Label(Strings.Memo_MemoTags));
            if (i == clipStart) List.Children.Add(Label(Strings.Memo_ClipboardTags));
            List.Children.Add(TagRowView(list[i], i, note));
        }
        if (list.Count == 0) { ListEmpty.Text = Strings.Clipboard_NoTags; ListEmpty.Visibility = Visibility.Visible; }
        if (clipStart >= 0)
        {
            var note2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 6, 8, 0), Padding = new Thickness(0, 8, 0, 0), BorderBrush = CodeProto.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0) };
            note2.Children.Add(new PaneIcon { Glyph = "info", Width = 13, Height = 13, Foreground = SubProto.Foreground, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            note2.Children.Add(new TextBlock { Text = Strings.Memo_ClipboardTagNote, TextWrapping = TextWrapping.Wrap, MaxWidth = 190, FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, Foreground = SubProto.Foreground, LineHeight = 18 });
            List.Children.Add(note2);
        }
    }

    // 行同剪贴板的标签选择：36 高，28 的图标格（色点或标签图标）、名字 + 条数 / 「剪贴板」、打上的右侧打勾、加粗
    Border TagRowView(TagRow r, int index, Note? note)
    {
        bool on = r.Tag != null && !r.FromClipboard && note != null && note.Tags.Any(t => Same(t, r.Tag.Name));
        var grid = new Grid { ColumnSpacing = 10, Height = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement lead = r.Create != null ? new PaneIcon { Glyph = "plus", Width = 15, Height = 15, Foreground = IconProto.Foreground }
            : TagColors.Dot(r.Tag!.Color) is { } dot ? new Ellipse { Width = 10, Height = 10, Fill = dot }
            : new PaneIcon { Glyph = "tag", Width = 15, Height = 15, Foreground = IconProto.Foreground };
        grid.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = CodeProto.Background, BorderBrush = CodeProto.BorderBrush, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = new Grid { Children = { lead } } });
        ((FrameworkElement)lead).HorizontalAlignment = HorizontalAlignment.Center; ((FrameworkElement)lead).VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Create != null ? string.Format(Strings.Clipboard_NewTag, (object?)r.Create) : r.Tag!.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = TitleProto.FontFamily, FontSize = TitleProto.FontSize, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = TitleProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
        if (r.FromClipboard)
        {
            var from = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            from.Children.Add(new PaneIcon { Glyph = "clipboard", Width = 11, Height = 11, Foreground = SubProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
            from.Children.Add(new TextBlock { Text = Strings.Common_Clipboard, FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, Foreground = SubProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(from);
        }
        else if (r.Tag != null)
            text.Children.Add(new TextBlock { Text = string.Format(Strings.Clipboard_TagCount, (object?)CountOf(r.Tag.Name)), FontFamily = SubProto.FontFamily, FontSize = SubProto.FontSize, Foreground = SubProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (on) { var check = new PaneIcon { Glyph = "check", Width = 16, Height = 16, Foreground = IconProto.Foreground, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(check, 2); grid.Children.Add(check); }
        var row = new Border { Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid };
        Paint(row, index == tagSel, false);
        row.PointerEntered += (_, _) => Paint(row, index == tagSel, true);
        row.PointerExited += (_, _) => Paint(row, index == tagSel, false);
        row.Tapped += (_, _) => { tagSel = index; ToggleTag(index); };
        return row;
    }

    void ToggleTag(int index)
    {
        var list = TagRows();
        if (index < 0 || index >= list.Count) return;
        // 新建的空白备忘录还没交给存储：先不让打标签（界面上按钮也是灰的）
        if (Current is not { } note) return;
        SaveNow();
        var r = list[index];
        var tags = note.Tags.ToList();
        try
        {
            if (r.Create != null) { store.CreateTag(r.Create, null); tags.Add(r.Create); Query.Text = ""; }
            else if (r.FromClipboard) { store.CreateTag(r.Tag!.Name, r.Tag.Color); tags.Add(r.Tag.Name); }   // 复制成备忘录标签再打上
            else if (tags.Any(t => Same(t, r.Tag!.Name))) tags.RemoveAll(t => Same(t, r.Tag!.Name));
            else tags.Add(r.Tag!.Name);
            store.SetTags(note.Id, tags);
        }
        catch (ArgumentException ex) { ListEmpty.Text = ex.Message; ListEmpty.Visibility = Visibility.Visible; return; }
        RenderTags();
        RenderMain();
    }

    void EnterTags()
    {
        if (Current == null) return;
        SaveNow();
        tagMode = true;
        tagSel = 0;
        savedQuery = Query.Text;
        loadingQuery = true; Query.Text = ""; loadingQuery = false;
        Query.PlaceholderText = Strings.Clipboard_TagSearchPlaceholder;
        TagIcon.IsFilled = true;
        FillHints();
        RenderFilter();
        RenderTags();
        Query.Focus(FocusState.Programmatic);
    }

    void LeaveTags()
    {
        if (!tagMode) return;
        tagMode = false;
        loadingQuery = true; Query.Text = savedQuery; loadingQuery = false;
        Query.PlaceholderText = Strings.Memo_SearchPlaceholder;
        TagIcon.IsFilled = false;
        FillHints();
        Render();
        if (!preview) FocusEditor();
    }

    void FillHints()
    {
        if (tagMode)
            KeyHints.Fill(Hints, [(["↑", "↓"], Strings.Clipboard_Select), (["Enter"], Strings.Clipboard_TagToggle), (["Esc"], Strings.Common_Done)], HintLabelProto, KbdProto);
        else
            KeyHints.Fill(Hints, [(["Ctrl", "+", "N"], Strings.Memo_New), (["Ctrl", "+", "F"], Strings.Common_Search), (["Ctrl", "+", "T"], Strings.Clipboard_Tags), (["Ctrl", "+", "E"], Strings.Memo_Preview), (["Ctrl", "+", "P"], Strings.Memo_Pin)],
                HintLabelProto, KbdProto);
    }

    void OnTagButton(object sender, RoutedEventArgs e) { if (tagMode) LeaveTags(); else EnterTags(); }
}
