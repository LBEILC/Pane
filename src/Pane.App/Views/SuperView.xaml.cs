using Pane.Localization;
// 超级面板逻辑（设计负责人）。交互照原型 app.js「超级面板」，另加键盘：
// ←/→ 选动作、1~6 直接执行、Enter 执行（有结果时 = 粘贴替换选中文字）、Ctrl+C 复制结果、Ctrl+R 重新生成、Esc 关闭。
// 选中外文自动翻译（2026-10-30，原型 launcher.html #sp，设置 → AI 里打开）：弹出就翻、不防抖，单个单词也翻；
// 超过约 2000 字不自动翻，给「翻译」按钮；中文照旧。自动翻译时动作换成 复制译文 / 翻译窗口 / 问 AI / 总结 / 复制原文 / 搜索。
// Tab：到翻译窗口继续（自动翻译、翻译结果卡片时）。
// 选中的是文件（040）：动作换成 批量重命名 / 复制路径 / 复制名称（每行一个），不自动翻译。
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class SuperAction : INotifyPropertyChanged
{
    public SuperAction(string id, string name, string glyph, bool ai, double width = 56) { Id = id; Name = name; Glyph = glyph; IsAi = ai; ItemWidth = width; }
    /// <summary>格子宽：平时 6 个各 56；选中文件时只有 3 个，加宽到 84（「批量重命名」放得下）。</summary>
    public double ItemWidth { get; }
    public string Id { get; }
    public string Name { get; }
    public string Glyph { get; }
    public bool IsAi { get; }
    bool enabled = true;
    public bool Enabled { get => enabled; set { if (enabled == value) return; enabled = value; PropertyChanged?.Invoke(this, new(nameof(ItemOpacity))); } }
    public double ItemOpacity => enabled ? 1 : 0.4;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class SuperView : UserControl, IPanelView
{
    const string DemoSelection = "Glassmorphism relies on background blur, translucency and a subtle highlight border.";

    readonly IAiService ai;
    readonly ISelectionActions actions;
    readonly SlidingHighlight highlight;
    readonly List<SuperAction> auto;
    List<SuperAction> normal;   // 每次呼出按设置重建
    List<SuperAction> items;
    SelectionResult? selection;
    const int AutoLimit = 2000;
    CancellationTokenSource? autoRun;
    string autoResult = "";
    bool autoMode;

    /// <summary>到翻译窗口继续（带上选中的文字）。</summary>
    public event Action<string>? OpenTranslate;
    /// <summary>问 AI：打开对话窗口，选中的文字放进输入框，等你写问题。</summary>
    public event Action<string>? OpenChat;
    /// <summary>批量重命名：打开重命名窗口，带上选中的文件。</summary>
    public event Action<IReadOnlyList<string>>? OpenRename;
    readonly List<SuperAction> fileActions = [new("rename", Strings.Common_Rename, "pencil", false, 84), new("copy-path", Strings.Common_CopyPath, "link", false, 84), new("copy-name", Strings.SuperPanel_CopyName, "copy", false, 84)];
    IReadOnlyList<string>? files;
    int selected;
    bool activate;
    double lastHeight;

    public SuperView() : this(new AiSample(), new SelectionActionsSample()) { }

    public SuperView(IAiService ai, ISelectionActions actions)
    {
        this.ai = ai;
        this.actions = actions;
        InitializeComponent();
        normal = NormalActions();
        var summary = ai.Actions.FirstOrDefault(a => a.Id == "summary");
        auto = [new("copy-translation", Strings.SuperPanel_CopyTranslation, "paste", false), new("translate-window", Strings.SuperPanel_TranslateWindow, "external", false), new("ask", Strings.Launcher_AskAi, "ai", false),
            .. summary is null ? Array.Empty<SuperAction>() : [new SuperAction(summary.Id, summary.Name, summary.Glyph, true)],
            new("copy", Strings.SuperPanel_CopyOriginal, "clipboard", false), new("search", Strings.Common_Search, "globe", false)];
        items = normal;
        Actions.ItemsSource = items;
        highlight = new SlidingHighlight(ActionHighlightLayer);
        Actions.PointerMoved += OnActionsPointerMoved;
        Actions.Tapped += OnActionsTapped;
        KeyDown += OnKeyDown;
        Root.SizeChanged += (_, _) => RequestHeight();
        Card.ContentGrew += (_, _) => RequestHeight();
    }

    // 设置 → 超级面板 →「面板里的动作」（2026-10-30）：按设置的顺序；AI 动作被删了就跳过
    List<SuperAction> NormalActions()
    {
        var ids = ((App)Application.Current).Store.Current.SuperPanel.Actions ?? Settings.SuperPanelSettings.DefaultActions(ai.Actions.Select(a => a.Id));
        var list = new List<SuperAction>();
        foreach (var id in ids)
        {
            if (!AiOptions.Enabled && id.StartsWith("ai:", StringComparison.Ordinal)) continue;   // AI 总开关关着（issue 8）
            SuperAction? a = id switch
            {
                "search" => new("search", Strings.Common_Search, "globe", false),
                "copy" => new("copy", Strings.Common_Copy, "clipboard", false),
                _ when id.StartsWith("ai:", StringComparison.Ordinal) && ai.Actions.FirstOrDefault(x => x.Id == id[3..]) is { } act => new(act.Id, act.Name, act.Glyph, true),
                _ => null,
            };
            if (a is not null && list.All(x => x.Id != a.Id)) list.Add(a);
            if (list.Count == Settings.SuperPanelSettings.MaxActions) break;
        }
        return list;
    }

    /// <summary>宿主在显示面板之前调用（选区必须在面板抢走焦点之前读好）。</summary>
    public void SetSelection(SelectionResult result) => selection = result;

    // ---------------- IPanelView ----------------

    public Size PreferredSize => new(360, 140);
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => null;

    public void OnShowing(PanelShowContext context)
    {
        activate = context.Activate;
        normal = NormalActions();
        Card.Cancel();
        ResultArea.Visibility = Visibility.Collapsed;
        // 设计预览：PANE_DESIGN_SELECTION 指定选中文字（"-" 表示没读到）；宿主没给选区时用演示文字
        var preview = Environment.GetEnvironmentVariable("PANE_DESIGN_SELECTION");
        var sel = selection ?? (preview == "-" ? new SelectionResult(null, 0, SelectionFailure.Empty)
                              : preview == "files" ? new SelectionResult(null, 0, SelectionFailure.None) { Files = RenameSample.Photos }
                                              : new SelectionResult(string.IsNullOrEmpty(preview) ? DemoSelection : preview, 0, SelectionFailure.None));
        selection = null; // 每次呼出由宿主重新给
        current = sel;
        files = sel.Files is { Count: > 0 } f ? f : null;
        QuoteBox.Visibility = files is null ? Visibility.Visible : Visibility.Collapsed;
        FileBox.Visibility = files is null ? Visibility.Collapsed : Visibility.Visible;
        if (files is not null)
        {
            ShowFiles(files);
            StartAuto(null);
            SetItems(fileActions);
            foreach (var a in items) a.Enabled = true;
            highlight.Hide();
            selected = 0;
            UpdateLayout();
            Select(0);
            return;
        }
        var has = !string.IsNullOrWhiteSpace(sel.Text);
        QuoteText.Text = has ? sel.Text! : sel.Failure switch
        {
            SelectionFailure.Elevated => Strings.SuperPanel_SelectionPermission,
            SelectionFailure.Timeout => Strings.SuperPanel_SelectionTimeout,
            SelectionFailure.ClipboardUnavailable => Strings.SuperPanel_ClipboardUnavailable,
            _ => Strings.SuperPanel_SelectionEmpty,
        };
        QuoteText.Opacity = has ? 1 : 0.7;
        StartAuto(has ? sel.Text! : null);
        foreach (var a in items) a.Enabled = has;
        highlight.Hide();
        selected = 0;
        UpdateLayout();
        if (has) Select(0); // 没读到选区时动作全是灰的，不显示选中
    }

    SelectionResult current = new(null, 0, SelectionFailure.Empty);

    // 文件头：第一项的类型决定图标和颜色；标题说「N 个文件」或「N 个文件和文件夹」；副标题前三个名字
    void ShowFiles(IReadOnlyList<string> list)
    {
        static bool IsFolder(string p) => System.IO.Directory.Exists(p);
        var first = list[0];
        var category = Services.Launcher.FileSearch.Category(System.IO.Path.GetFileName(first), IsFolder(first));
        var tile = Files.FileKinds.Brush(category);
        FileTile1.Background = FileTile2.Background = FileTile3.Background = tile;
        FileGlyph.Glyph = Files.FileKinds.Glyph(category);
        int folders = list.Take(200).Count(IsFolder);
        FileTitle.Text = list.Count == 1 ? System.IO.Path.GetFileName(first)
            : string.Format(folders == 0 ? Strings.SuperPanel_FileCount : folders == Math.Min(list.Count, 200) ? Strings.SuperPanel_FolderCount : Strings.SuperPanel_MixedCount, list.Count);
        FileNames.Text = list.Count == 1 ? System.IO.Path.GetDirectoryName(first) ?? ""
            : list.Count > 3 ? string.Format(Strings.SuperPanel_MoreNames, string.Join("、", list.Take(3).Select(System.IO.Path.GetFileName))) : string.Join("、", list.Take(3).Select(System.IO.Path.GetFileName));
    }

    async Task ExecuteFile(int index)
    {
        if (files is null || index < 0 || index >= items.Count) return;
        Select(index);
        var id = items[index].Id;
        var list = files;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        if (id == "rename") { OpenRename?.Invoke(list); return; }
        var text = string.Join(Environment.NewLine, id == "copy-path" ? list : list.Select(p => System.IO.Path.GetFileName(p)));
        try { await actions.CopyAsync(text, CancellationToken.None); }
        catch (Exception e) { ((App)Application.Current).ReportFailure(id == "copy-path" ? Strings.Common_CopyPath : Strings.SuperPanel_CopyName, e); }
    }

    public void OnShown()
    {
        if (activate) Focus(FocusState.Programmatic);
        // 设计预览：PANE_DESIGN_RUN 指定一开始就执行的动作（如 translate），截图看结果卡片
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_RUN") is { Length: > 0 } run && items.FindIndex(a => a.Id == run) is var i and >= 0) _ = Execute(i);
    }

    public void OnHidden() { Card.Cancel(); autoRun?.Cancel(); }

    // ---------------- 自动翻译 ----------------

    static Settings.AiSettings AiOptions => ((App)Application.Current).Store.Current.Ai;

    void StartAuto(string? text)
    {
        autoRun?.Cancel();
        autoResult = "";
        AutoBox.Visibility = LongBox.Visibility = Visibility.Collapsed;
        // 设计预览：PANE_DESIGN_AUTO=1 不管设置都当打开
        bool on = AiOptions.Enabled && AiOptions.AutoTranslateSelection || Environment.GetEnvironmentVariable("PANE_DESIGN_AUTO") == "1";
        autoMode = on && text is not null && ai.Status == AiStatus.Ready && !Languages.IsChinese(text) && text.Trim().Length > 0;
        if (autoMode && text!.Length > AutoLimit)
        {
            autoMode = false;
            LongText.Text = string.Format(Strings.SuperPanel_AutoTranslateTooLong, (object?)(text.Length));
            LongBox.Visibility = Visibility.Visible;
        }
        SetItems(autoMode ? auto : normal);
        if (!autoMode) return;
        var target = Languages.AutoTarget(text!, AiOptions.TranslateTarget);
        AutoDirection.Text = $"{Languages.Detect(text!)} → {Languages.NameOf(target)}" + (ai.TranslationModelName is { } m ? $" · {m}" : "");
        AutoOut.Text = "…";
        AutoOut.Opacity = 0.6;
        AutoBox.Visibility = Visibility.Visible;
        _ = RunAuto(text!, target, autoRun = new CancellationTokenSource());
    }

    async Task RunAuto(string text, string target, CancellationTokenSource cts)
    {
        try
        {
            var first = true;
            await foreach (var piece in ai.RunAsync(new AiRequest("translate", text, target, null), cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                if (first) { AutoOut.Text = ""; AutoOut.Opacity = 1; first = false; }
                autoResult += piece;
                AutoOut.Text = autoResult;
                RequestHeight();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!cts.IsCancellationRequested) { AutoOut.Text = string.Format(Strings.SuperPanel_TranslateError, App.FailureReason(e) ?? Strings.Common_UnexpectedProblem); AutoOut.Opacity = 0.7; RequestHeight(); } }
    }

    void SetItems(List<SuperAction> list)
    {
        if (ReferenceEquals(items, list)) return;
        items = list;
        Actions.ItemsSource = items;
    }

    void OnCopyTranslation(object sender, RoutedEventArgs e) => CopyTranslation();
    void OnOpenTranslate(object sender, RoutedEventArgs e) { if (current.Text is { } t) OpenTranslate?.Invoke(t); }
    void OnTranslateLong(object sender, RoutedEventArgs e)
    {
        LongBox.Visibility = Visibility.Collapsed;
        if (normal.FindIndex(a => a.Id == "translate") is var i and >= 0) _ = Execute(i);
        else if (current.Text is { } t) OpenTranslate?.Invoke(t);   // 用户在设置里把「翻译」从这一排关掉了
    }

    void CopyTranslation()
    {
        if (autoResult.Length == 0) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _ = actions.CopyAsync(autoResult, CancellationToken.None);
    }

    void RequestHeight()
    {
        UpdateLayout();
        var h = Math.Ceiling(Root.ActualHeight);
        if (h <= 0 || Math.Abs(h - lastHeight) < 0.5) return;
        lastHeight = h;
        SizeRequested?.Invoke(this, new Size(360, h));
    }

    // ---------------- 选中与执行 ----------------

    string? cardAction;

    void Select(int index)
    {
        selected = (index % items.Count + items.Count) % items.Count;
        if (Actions.ContainerFromItem(items[selected]) is FrameworkElement el) highlight.MoveTo(el);
    }

    async Task Execute(int index)
    {
        if (files is not null) { await ExecuteFile(index); return; }
        if (index < 0 || index >= items.Count || current.Text is not { } text || !items[index].Enabled) return;
        Select(index);
        var a = items[index];
        switch (a.Id)
        {
            case "copy-translation": CopyTranslation(); return;
            case "translate-window": OpenTranslate?.Invoke(text); return;
            case "ask": OpenChat?.Invoke(text); return;
        }
        if (!a.IsAi)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            try
            {
                if (a.Id == "search") await actions.SearchWebAsync(text, CancellationToken.None);
                else await actions.CopyAsync(text, CancellationToken.None);
            }
            catch (Exception e) { ((App)Application.Current).ReportFailure(a.Id == "search" ? Strings.SuperPanel_WebSearch : Strings.Common_Copy, e); }
            return;
        }
        ResultArea.Visibility = Visibility.Visible;
        cardAction = a.Id;
        Card.Start(ai, new AiRequest(a.Id, text, null, null), a.Name, a.Glyph, source: null, compact: true,
            a.Id == "translate"
                ? [(["Enter"], Strings.SuperPanel_PasteReplace), (["Ctrl", "+", "C"], Strings.Common_Copy), (["Tab"], Strings.SuperPanel_TranslateWindow), (["Esc"], Strings.Common_Close)]
                : [(["Enter"], Strings.SuperPanel_PasteReplaceSelection), (["Ctrl", "+", "C"], Strings.Common_Copy), (["Ctrl", "+", "R"], Strings.Common_Regenerate), (["Esc"], Strings.Common_Close)]);
        RequestHeight();
    }

    // ---------------- 键盘、鼠标 ----------------

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        var ctrl = Down(VirtualKey.Control);
        var hasResult = ResultArea.Visibility == Visibility.Visible && Card.Result.Length > 0;
        switch (e.Key)
        {
            case VirtualKey.Left when current.Text is not null || files is not null: Select(selected - 1); break;
            case VirtualKey.Right when current.Text is not null || files is not null: Select(selected + 1); break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number6 when !ctrl: _ = Execute(e.Key - VirtualKey.Number1); break;
            case VirtualKey.Enter when hasResult: _ = Replace(); break;
            case VirtualKey.Enter: _ = Execute(selected); break;
            case VirtualKey.C when ctrl && hasResult: _ = actions.CopyAsync(Card.Result, CancellationToken.None); break;
            case VirtualKey.R when ctrl && ResultArea.Visibility == Visibility.Visible: Card.Retry(); break;
            case VirtualKey.Tab when current.Text is { } t && (autoMode || ResultArea.Visibility == Visibility.Visible && cardAction == "translate"): OpenTranslate?.Invoke(t); break;
            case VirtualKey.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }

    async Task Replace()
    {
        var text = Card.Result;
        var window = current.SourceWindow;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try { await actions.ReplaceSelectionAsync(text, window, CancellationToken.None); }
        catch (Exception e) { ((App)Application.Current).ReportFailure(Strings.SuperPanel_ReplaceSelection, e); }
    }

    void OnActionsPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ActionFrom(e.OriginalSource) is { } i && i != selected && items[i].Enabled) Select(i);
    }

    void OnActionsTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ActionFrom(e.OriginalSource) is { } i) _ = Execute(i);
    }

    int? ActionFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: SuperAction a }) return items.IndexOf(a);
        return null;
    }
}
