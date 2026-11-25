using Pane.Localization;
// 剪贴板的就地模式（设计负责人，2026-11-23）。docs/design.md「剪贴板操作列表、富文本、标签、编辑、高度」，原型 prototype/ui/clipboard.js。
// - 操作列表（Ctrl+K / 右键）：列表就地换成这一条的操作（同启动器，不弹菜单）；数字键 1～9 直接执行，Enter 执行选中，Esc / Ctrl+K 回到列表、选回这一条；打字退回列表接着搜。
// - 标签选择（Ctrl+T 或「标签…」）：搜索框就地变成标签输入；Enter / 点击打上或去掉，可以连着打几个；名字不存在时第一行「新建标签」。
// - 编辑（Ctrl+E，只给文字类）：列表区换成编辑框；Ctrl+S 保存、Ctrl+Enter 保存并粘贴、Esc 放弃。
// 不激活方式下进模式前先切到激活方式（宿主 Engage），原窗口的编辑框会失去焦点，同「打字切到搜索」。
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Views.Clipboard;
using Windows.System;

namespace Pane.Views;

public sealed partial class ClipboardView
{
    enum ClipMode { None, Menu, Tags, Edit }
    ClipMode mode;
    ClipEntry? modeTarget;
    string savedQuery = "";
    bool suppressText;
    int opSel = -1;
    readonly ObservableCollection<ClipOp> ops = [];

    /// <summary>操作列表里交给宿主做的事：open / reveal / save-as / open-link（宿主用启动器的附件动作或系统打开网址）。</summary>
    public event EventHandler<(ClipEntry Entry, string Action)>? ActionRequested;
    /// <summary>宿主能不能做打开、在资源管理器中显示、另存为（启动器能力停用时不能）。</summary>
    public Func<bool> FileActionsAvailable { get; set; } = () => true;

    bool InMode => mode != ClipMode.None;

    // ---------------- 进出模式 ----------------

    void EnterMode(ClipMode next, ClipEntry target, string label)
    {
        if (Passive) EngageRequested?.Invoke(this, EventArgs.Empty);
        if (mode == ClipMode.None) savedQuery = Input.Text;
        mode = next;
        modeTarget = target;
        highlight.Hide();
        Sections.Visibility = Visibility.Collapsed;
        Empty.Visibility = Visibility.Collapsed;
        ModePanel.Visibility = Visibility.Visible;
        ModeLabel.Text = label;
        OpList.Visibility = next == ClipMode.Edit ? Visibility.Collapsed : Visibility.Visible;
        ModeNote.Visibility = next == ClipMode.Tags ? Visibility.Visible : Visibility.Collapsed;
        EditPanel.Visibility = next == ClipMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        ModeEmpty.Visibility = Visibility.Collapsed;
        ChipScroller.Opacity = 0.45;
        ChipScroller.IsHitTestVisible = false;
        Scroller.ChangeView(null, 0, null, true);
        BuildFoot();
    }

    /// <summary>回到列表。keepTyping：操作列表里打字退回列表时，保留打的字接着搜。</summary>
    void LeaveMode(bool keepTyping = false)
    {
        if (mode == ClipMode.None) return;
        var target = modeTarget;
        mode = ClipMode.None;
        modeTarget = null;
        ops.Clear();
        opSel = -1;
        ModePanel.Visibility = Visibility.Collapsed;
        Sections.Visibility = Visibility.Visible;
        ChipScroller.Opacity = 1;
        ChipScroller.IsHitTestVisible = true;
        Input.PlaceholderText = Strings.Clipboard_SearchPlaceholder;
        if (!keepTyping) SetInput(savedQuery);
        Render(keepSelection: false);
        if (!keepTyping && target is not null && rows.Find(r => r.Entry.Id == target.Id) is { } row) Select(row.Index);
        BuildFoot();
        if (!Passive) Input.Focus(FocusState.Programmatic);
    }

    void SetInput(string text)
    {
        suppressText = true;
        Input.Text = text;
        Input.SelectionStart = text.Length;
        suppressText = false;
    }

    static string Summary(ClipEntry e)
    {
        if (e.Kind == ClipKind.Files && e.Files is { Count: > 0 } f)
        {
            var first = System.IO.Path.GetFileName(f[0].TrimEnd('\\'));
            return f.Count > 1 ? string.Format(Strings.Clipboard_FilesSummary, first, f.Count) : first;
        }
        var t = System.Text.RegularExpressions.Regex.Replace(e.Text ?? "", @"\s+", " ").Trim();
        return t.Length > 40 ? t[..40] : t;
    }

    ClipEntry? Fresh(string id) => history.Snapshot().FirstOrDefault(e => e.Id == id);

    // ---------------- 操作列表 ----------------

    void OpenMenu()
    {
        if (InMode || SelectedRow is not { } row) return;
        var e = row.Entry;
        var list = new List<ClipOp>();
        int n = 0;
        ClipOp Op(string id, string title, string glyph, string keys = "", bool danger = false) => new(id, title, glyph, keys, ++n, danger);
        if (e.Kind == ClipKind.Files)
        {
            list.Add(Op("paste", Strings.Clipboard_PasteFiles, "paste", "Enter"));
            list.Add(Op("paste-path", Strings.Clipboard_PastePath, "text", "Shift+Enter"));
            list.Add(Op("paste-name", Strings.Clipboard_PasteFileName, "file-name"));
            if (FileActionsAvailable())
            {
                if (e.Files is { Count: 1 } one)
                {
                    bool folder = System.IO.Directory.Exists(one[0]);
                    list.Add(Op("open", folder ? Strings.Clipboard_OpenFolder : Strings.Common_Open, folder ? "folder-open" : "external"));
                }
                list.Add(Op("reveal", Strings.Clipboard_RevealInExplorer, "folder"));
            }
        }
        else if (e.Kind == ClipKind.Image)
        {
            list.Add(Op("paste", Strings.Common_Paste, "paste", "Enter"));
            list.Add(Op("paste-file", Strings.Clipboard_PasteAsFile, "file-image"));
            if (FileActionsAvailable()) list.Add(Op("save-as", Strings.Common_SaveAs, "save"));
        }
        else
        {
            list.Add(Op("paste", Strings.Common_Paste, "paste", "Enter"));
            if (e.Rich) list.Add(Op("paste-plain", Strings.Clipboard_PastePlain, "text", "Shift+Enter"));
            if (e.Kind == ClipKind.Link) list.Add(Op("open-link", Strings.Clipboard_OpenLink, "external"));
            if (e.Kind == ClipKind.Color) { list.Add(Op("paste-hex", Strings.Clipboard_PasteHex, "hash")); list.Add(Op("paste-rgb", Strings.Clipboard_PasteRgb, "palette")); }
            list.Add(Op("edit", Strings.Common_Edit, "pencil", "Ctrl+E"));
            if (row.CanSplit) list.Add(Op("words", Strings.Words_Open, "words", "Ctrl+B"));
            if (ModuleGate.On(ModuleIds.Memo) && !string.IsNullOrWhiteSpace(e.Text)) list.Add(Op("memo", Strings.Memo_SaveAs, "note"));   // 备忘录（123）
        }
        if (ModuleGate.On(ModuleIds.Launcher)) list.Add(Op("send", Strings.Clipboard_SendToLauncher, "R", "Tab"));
        list.Add(Op("tags", Strings.Clipboard_TagsAction, "tag", "Ctrl+T"));
        list.Add(Op("pin", e.Pinned ? Strings.Clipboard_Unpin : Strings.Clipboard_Pin, e.Pinned ? "pin-off" : "pin", "Ctrl+P"));
        list.Add(Op("delete", Strings.Common_Delete, "trash", "", danger: true));
        EnterMode(ClipMode.Menu, e, Summary(e));
        ops.Clear();
        foreach (var op in list) ops.Add(op);
        OpList.ItemsSource = ops;
        UpdateLayout();
        OpSelect(0);
    }

    void RunOp(ClipOp op)
    {
        if (modeTarget is not { } e) return;
        if (mode == ClipMode.Tags) { ToggleTag(op); return; }
        switch (op.Id)
        {
            case "paste": LeaveQuiet(); _ = PasteEntry(e, false); break;
            case "paste-plain" or "paste-path": LeaveQuiet(); _ = PasteEntry(e, true); break;
            case "paste-name": LeaveQuiet(); _ = PasteAs(e, ClipPasteAs.FileNames); break;
            case "paste-file": LeaveQuiet(); _ = PasteAs(e, ClipPasteAs.ImageFile); break;
            case "paste-hex": LeaveQuiet(); _ = PasteAs(e, ClipPasteAs.Hex); break;
            case "paste-rgb": LeaveQuiet(); _ = PasteAs(e, ClipPasteAs.Rgb); break;
            case "edit": StartEdit(e); break;
            case "tags": OpenTags(e); break;
            case "words": LeaveMode(); OpenWords?.Invoke(this, e); break;
            case "memo": LeaveMode(); SaveMemo?.Invoke(this, e); break;
            case "send": LeaveQuiet(); if (ForLauncher) UseInLauncher?.Invoke(this, e); else SendToLauncher?.Invoke(this, e); break;
            case "pin": history.SetPinned(e.Id, !e.Pinned); LeaveMode(); break;
            case "delete": LeaveMode(); history.Delete(e.Id); break;
            default: LeaveQuiet(); AfterUse(); ActionRequested?.Invoke(this, (e, op.Id!)); break;
        }
    }

    // 执行后面板要收起的：不重建列表，直接退出模式状态（收起时 OnHidden 会清掉列表）
    void LeaveQuiet()
    {
        mode = ClipMode.None;
        modeTarget = null;
        ModePanel.Visibility = Visibility.Collapsed;
        Sections.Visibility = Visibility.Visible;
        ChipScroller.Opacity = 1;
        ChipScroller.IsHitTestVisible = true;
        SetInput(savedQuery);
        Input.PlaceholderText = Strings.Clipboard_SearchPlaceholder;
    }

    async Task PasteAs(ClipEntry e, ClipPasteAs kind)
    {
        if (ForLauncher) { UseInLauncher?.Invoke(this, e); return; }
        AfterUse();
        try { await history.PasteAsAsync(e.Id, kind, Target, CancellationToken.None); }
        catch (Exception ex) { ((App)Application.Current).ReportFailure(Strings.Common_Paste, ex); }
    }

    void OpSelect(int index, bool scroll = true)
    {
        if (ops.Count == 0) { opSel = -1; highlight.Hide(); return; }
        opSel = (index % ops.Count + ops.Count) % ops.Count;
        UpdateLayout();
        if (OpList.ContainerFromIndex(opSel) is FrameworkElement el)
        {
            highlight.MoveTo(el);
            if (scroll) ScrollIntoView(el);
        }
    }

    static ClipOp? OpFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: ClipOp op }) return op;
        return null;
    }

    // ---------------- 标签选择 ----------------

    void OpenTags(ClipEntry e)
    {
        bool fresh = mode != ClipMode.Tags;
        EnterMode(ClipMode.Tags, e, string.Format(Strings.Clipboard_TagTarget, Summary(e)));
        if (fresh) SetInput("");
        Input.PlaceholderText = Strings.Clipboard_TagSearchPlaceholder;
        Input.Focus(FocusState.Programmatic);
        RenderTags(0);
    }

    void RenderTags(int select)
    {
        if (modeTarget is not { } e) return;
        var typed = Input.Text.Trim().TrimStart('#').Trim();
        var defs = history.Tags();
        var counts = history.Snapshot().SelectMany(x => x.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        ops.Clear();
        if (typed.Length > 0 && !defs.Any(t => string.Equals(t.Name, typed, StringComparison.OrdinalIgnoreCase)))
            ops.Add(new ClipOp(null, string.Format(Strings.Clipboard_NewTag, typed), "plus") { Tag = typed, Create = true });
        foreach (var t in defs.Where(t => typed.Length == 0 || t.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            ops.Add(new ClipOp(null, t.Name, "tag") { Tag = t.Name, Dot = TagColors.Dot(t.Color), Sub = string.Format(Strings.Clipboard_TagCount, counts.GetValueOrDefault(t.Name)),
                On = e.Tags.Contains(t.Name, StringComparer.OrdinalIgnoreCase) });
        OpList.ItemsSource = ops;
        ModeEmpty.Text = Strings.Clipboard_NoTags;
        ModeEmpty.Visibility = ops.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
        OpSelect(Math.Clamp(select, 0, Math.Max(0, ops.Count - 1)), scroll: false);
    }

    void ToggleTag(ClipOp op)
    {
        if (modeTarget is not { } e || op.Tag is not { } name) return;
        var current = (Fresh(e.Id) ?? e).Tags;
        var next = current.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? current.Where(t => !string.Equals(t, name, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. current, name];
        var keep = opSel;
        history.SetTags(e.Id, next);   // 新名字由服务自动建定义
        modeTarget = Fresh(e.Id) ?? e;
        if (op.Create) { SetInput(""); BuildChips(); }
        RenderTags(op.Create ? Math.Max(0, ops.ToList().FindIndex(o => o.Tag == name)) : keep);
        if (op.Create) { var i = ops.ToList().FindIndex(o => string.Equals(o.Tag, name, StringComparison.OrdinalIgnoreCase)); if (i >= 0) OpSelect(i); }
    }

    // ---------------- 编辑 ----------------

    void StartEdit(ClipEntry e)
    {
        if (e.Kind is not (ClipKind.Text or ClipKind.Code or ClipKind.Link or ClipKind.Color)) return;
        EnterMode(ClipMode.Edit, e, Summary(e));
        Editor.Text = e.Text ?? "";
        Editor.FontFamily = (FontFamily)Application.Current.Resources[e.Kind == ClipKind.Code ? "PaneFontMono" : "PaneFontUi"];
        EditNote.Visibility = e.Rich ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
        Editor.Focus(FocusState.Programmatic);
        Editor.SelectionStart = Editor.Text.Length;
    }

    void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape) { LeaveMode(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.S) { SaveEdit(paste: false); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.Enter) { SaveEdit(paste: true); e.Handled = true; }
    }

    void OnEditCancel(object sender, RoutedEventArgs e) => LeaveMode();
    void OnEditSave(object sender, RoutedEventArgs e) => SaveEdit(paste: false);
    void OnEditPaste(object sender, RoutedEventArgs e) => SaveEdit(paste: true);

    void SaveEdit(bool paste)
    {
        if (modeTarget is not { } e) return;
        var text = Editor.Text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        if (text.Length > 0 && text != e.Text) history.UpdateText(e.Id, text);
        if (paste) { var updated = Fresh(e.Id) ?? e; LeaveQuiet(); _ = PasteEntry(updated, false); }
        else LeaveMode();
    }

    // ---------------- 模式里的按键 ----------------

    /// <summary>模式里处理了返回 true。</summary>
    bool ModeKey(PanelKeyPress press)
    {
        if (!InMode) return false;
        switch (press.Key)
        {
            case PanelKey.Down: if (mode != ClipMode.Edit) OpSelect(opSel + 1); return true;
            case PanelKey.Up: if (mode != ClipMode.Edit) OpSelect(opSel - 1); return true;
            case PanelKey.Enter or PanelKey.ShiftEnter: if (opSel >= 0 && opSel < ops.Count) RunOp(ops[opSel]); return true;
            case PanelKey.Digit when mode == ClipMode.Menu:
                if (press.Number >= 1 && press.Number <= ops.Count) RunOp(ops[press.Number - 1]);
                return true;
            case PanelKey.Escape: LeaveMode(); return true;
            case PanelKey.More when mode == ClipMode.Menu: LeaveMode(); return true;
            case PanelKey.Tags when mode == ClipMode.Tags: LeaveMode(); return true;
            default: return true;   // 模式里别的列表键不落到列表上
        }
    }

    // ---------------- 标签色点（IClipRowActions） ----------------

    Dictionary<string, int?>? tagColors;
    public Brush? TagDot(string name)
    {
        tagColors ??= history.Tags().ToDictionary(t => t.Name, t => t.Color, StringComparer.OrdinalIgnoreCase);
        return TagColors.Dot(tagColors.GetValueOrDefault(name));
    }

    // ---------------- Pane 云：别的电脑来的图片（IClipRowActions） ----------------

    public long? OriginalSize(string clipId) => (Sync as IClipboardCloud)?.OriginalSize(clipId);
    public void EnsureThumbnail(string clipId) => (Sync as IClipboardCloud)?.EnsureThumbnail(clipId);
}
