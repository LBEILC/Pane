using Pane.Localization;
// 分词窗口（设计负责人，093）。照原型 prototype/words.html（docs/design.md「分词」）：
// 拆好的词块平铺；点一下切换，按住拖动连选（起点原来没选就是「选」，选了就是「取消」，拖回去会缩回），Shift+点击选一段；
// 拖选默认跳过标点（设置「拖选时带上标点」），单点标点才选它；Ctrl+A 全选（含标点）。
// 拼出来的文字：原文里挨着的几块按原文接（中间原来的空白照留），不挨着的几段按拼接方式接。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class WordsView : UserControl, ISecondaryView, IDisposable
{
    const double Width_ = 600, HeadH = 56, ActionsH = 53, FootH = 38, BodyMin = 92, BodyMax = 300;

    readonly ITextSegmenter segmenter;
    readonly ISelectionActions actions;
    readonly PaneSegmented joinSeg;
    readonly List<WordChip> chips = [];
    WordChip.Palette? palette;
    WordSplit? split;
    CancellationTokenSource? work;
    HashSet<int> sel = [];
    HashSet<int>? shiftBase;            // Shift+方向连选开始时的选中（松开 Shift 清掉）
    int focus;
    int? anchor;
    bool keyboard;                      // 用过方向键才画键盘光标
    (int From, int To, bool On, HashSet<int> Base)? drag;
    WordsJoin join;

    static App Host => (App)Application.Current;
    WordsSettings Options => Host.Store.Current.Words;

    /// <summary>「交给启动器」：拼好的文字。宿主先关掉这个窗口，再打开启动器带上它。</summary>
    public event EventHandler<string>? SendToLauncher;

    public WordsView(ITextSegmenter segmenter, ISelectionActions actions)
    {
        InitializeComponent();
        this.segmenter = segmenter;
        this.actions = actions;
        join = Options.Join;
        joinSeg = new PaneSegmented([("smart", Strings.Words_JoinSmart), ("space", Strings.Words_JoinSpace), ("line", Strings.Words_JoinLine)], JoinValue(join),
            SmallStrongProto, SmallProto, FillProto.Background, FillProto.BorderBrush);
        joinSeg.Changed += (_, v) =>
        {
            join = v switch { "space" => WordsJoin.Space, "line" => WordsJoin.Line, _ => WordsJoin.Smart };
            try { Host.Store.Save(Host.Store.Current with { Words = Options with { Join = join } }); } catch { /* 记不住上次的选择不影响这次 */ }
            Paint();
            Focus(FocusState.Programmatic);
        };
        JoinHost.Child = joinSeg;
        KeyHints.Fill(Foot, [([Strings.Common_SpaceKey], Strings.Words_Select), (["Shift", "+", "←", "→"], Strings.Words_Extend), (["Ctrl", "+", "A"], Strings.Words_SelectAll)],
            HintLabelProto, KbdProto);
        SendButton.Visibility = ModuleGate.On(ModuleIds.Launcher) ? Visibility.Visible : Visibility.Collapsed;
        PreviewKeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Unloaded += OnUnloaded;
    }

    static string JoinValue(WordsJoin j) => j switch { WordsJoin.Space => "space", WordsJoin.Line => "line", _ => "smart" };

    void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();
    public void Dispose() { Unloaded -= OnUnloaded; work?.Cancel(); work?.Dispose(); work = null; }

    // ---------------- 打开 ----------------

    /// <summary>拆 <paramref name="text"/>；source 是来源程序名（可能为 null），fromClipboard 决定来源写法。窗口已开着时换成新的文字。</summary>
    public async void Open(string text, string? source, bool fromClipboard)
    {
        SourceText.Text = fromClipboard
            ? source is { Length: > 0 } ? string.Format(Strings.Words_FromClipboardApp, source) : Strings.Words_FromClipboard
            : source is { Length: > 0 } ? string.Format(Strings.Words_FromSelectionApp, source) : Strings.Words_FromSelection;
        work?.Cancel(); work?.Dispose();
        var cts = work = new CancellationTokenSource();
        WordSplit result;
        try { result = await segmenter.SplitAsync(text, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Host.ReportFailure(Strings.Module_Words_Name, e); CloseRequested?.Invoke(this, EventArgs.Empty); return; }
        if (cts.IsCancellationRequested) return;
        Build(result);
    }

    void Build(WordSplit result)
    {
        split = result;
        palette ??= WordChip.Palette.From(Accent(), FillProto.Background, FillProto.BorderBrush, HoverProto.Background,
            ChipProto.Foreground, Text2Proto.Foreground, Text3Proto.Foreground, ChipProto.FontFamily, ChipProto.FontSize);
        Chips.Children.Clear(); chips.Clear();
        int end = 0;
        foreach (var piece in result.Pieces)
        {
            // 原文里两块之间有换行：词块也换行
            if (chips.Count > 0 && result.Source.AsSpan(end, piece.Start - end).IndexOfAny('\r', '\n') >= 0) Chips.Children.Add(new WrapBreak());
            var chip = new WordChip(piece, chips.Count, palette);
            chips.Add(chip); Chips.Children.Add(chip);
            end = piece.Start + piece.Length;
        }
        Notice.Text = result.Pieces.Count == 0 ? Strings.Words_Empty : result.Truncated ? Strings.Words_Truncated : "";
        Notice.Visibility = Notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        sel = []; shiftBase = null; focus = 0; anchor = null; keyboard = false; drag = null;
        // 设计预览：PANE_DESIGN_WORDS_SEL=3-11,16 预先选中几块（序号从 0 起），不点鼠标就能截图
        foreach (var part in (Environment.GetEnvironmentVariable("PANE_DESIGN_WORDS_SEL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-');
            if (int.TryParse(ends[0], out int a) && int.TryParse(ends[^1], out int b))
                for (int k = a; k <= b && k < chips.Count; k++) if (a == b || !chips[k].IsPunctuation) sel.Add(k);
        }
        Scroller.ChangeView(null, 0, null, true);
        Paint();
        RequestHeight();
    }

    static Windows.UI.Color Accent()
    {
        uint argb = Convert.ToUInt32(Host.Glass.Current.AccentColor[1..], 16);
        return Windows.UI.Color.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    // 窗口高度跟着词块多少走：词块区 92～300，超出滚动
    void RequestHeight()
    {
        var body = (StackPanel)Scroller.Content;
        body.Measure(new Size(Width_, double.PositiveInfinity));
        double h = HeadH + Math.Clamp(body.DesiredSize.Height, BodyMin, BodyMax) + ActionsH + FootH;
        preferred = new Size(Width_, Math.Ceiling(h));
        SizeRequested?.Invoke(this, preferred);
    }

    // ---------------- 选中 ----------------

    void Paint()
    {
        foreach (var c in chips) { c.Selected = sel.Contains(c.Index); c.Focused = keyboard && c.Index == focus; }
        var runs = Runs();
        PickText.Inlines.Clear();
        if (runs.Count == 0)
        {
            PickText.Foreground = Text3Proto.Foreground;
            PickText.Inlines.Add(new Run { Text = split?.Pieces.Count > 0 ? Strings.Words_Hint : "" });
        }
        else
        {
            PickText.Foreground = SmallStrongProto.Foreground;
            string seam = join switch { WordsJoin.Space => " ␣ ", WordsJoin.Line => " ↵ ", _ => " · " };   // 不翻译：接缝记号
            for (int i = 0; i < runs.Count; i++)
            {
                if (i > 0) PickText.Inlines.Add(new Run { Text = seam, Foreground = Text3Proto.Foreground });
                PickText.Inlines.Add(new Run { Text = runs[i].ReplaceLineEndings(" ") });
            }
        }
        JoinHost.Visibility = runs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        int chars = runs.Sum(r => r.Count(ch => !char.IsWhiteSpace(ch)));
        CountText.Text = sel.Count > 0 ? string.Format(Strings.Words_Count, sel.Count, chars) : "";
        CopyButton.IsEnabled = SendButton.IsEnabled = runs.Count > 0;
    }

    // 选中的几段：在原文里挨着的块按原文接（中间原来的空白照留）
    List<string> Runs()
    {
        var runs = new List<string>();
        if (split is not { } s) return runs;
        int last = -2, runStart = 0, runEnd = 0;
        foreach (var k in sel.Order())
        {
            var p = s.Pieces[k];
            if (k != last + 1 && last >= 0) { runs.Add(s.Source[runStart..runEnd]); runStart = p.Start; }
            else if (last < 0) runStart = p.Start;
            runEnd = p.Start + p.Length; last = k;
        }
        if (last >= 0) runs.Add(s.Source[runStart..runEnd]);
        return runs;
    }

    string Joined()
    {
        var runs = Runs();
        if (join == WordsJoin.Space) return string.Join(" ", runs);
        if (join == WordsJoin.Line) return string.Join(Environment.NewLine, runs);
        // 连起来：两边都是英文字母或数字时补一个空格，免得粘成一个词
        var sb = new System.Text.StringBuilder();
        foreach (var r in runs)
        {
            if (sb.Length > 0 && char.IsAsciiLetterOrDigit(sb[^1]) && char.IsAsciiLetterOrDigit(r[0])) sb.Append(' ');
            sb.Append(r);
        }
        return sb.ToString();
    }

    // 把 a..b 设成 on；拖选时标点默认跳过（中间的也跳过），单点标点本身时才选它
    void RangeSet(int a, int b, bool on, HashSet<int> start, bool single = false)
    {
        sel = [.. start];
        var (lo, hi) = a < b ? (a, b) : (b, a);
        bool punct = Options.DragPunctuation;
        for (int k = lo; k <= hi; k++)
        {
            if (!single && !punct && chips[k].IsPunctuation) { sel.Remove(k); continue; }
            if (on) sel.Add(k); else sel.Remove(k);
        }
    }

    void SelectAll()
    {
        if (chips.Count == 0) return;
        sel = [.. chips.Select(c => c.Index)]; anchor = 0; shiftBase = null;
        Paint();
        double speed = Host.Glass.Current.MotionSpeed;
        foreach (var c in chips) c.Pop(speed);
    }

    // ---------------- 鼠标 ----------------

    WordChip? ChipAt(Point p)
    {
        foreach (var c in chips)
        {
            var o = c.ActualOffset;
            if (p.X >= o.X && p.X < o.X + c.ActualWidth && p.Y >= o.Y && p.Y < o.Y + c.ActualHeight) return c;
        }
        return null;
    }

    void OnChipsPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Chips);
        if (!point.Properties.IsLeftButtonPressed || ChipAt(point.Position) is not { } chip) return;
        e.Handled = true; keyboard = false; shiftBase = null;
        Focus(FocusState.Pointer);
        int k = chip.Index;
        if ((e.KeyModifiers & VirtualKeyModifiers.Shift) != 0 && anchor is { } a)
        {
            RangeSet(a, k, true, sel); focus = k; Paint(); return;
        }
        drag = (k, k, !sel.Contains(k), [.. sel]);
        anchor = k; focus = k;
        ApplyDrag();
        chip.Pop(Host.Glass.Current.MotionSpeed);
        Chips.CapturePointer(e.Pointer);
    }

    void OnChipsMoved(object sender, PointerRoutedEventArgs e)
    {
        if (drag is not { } d || ChipAt(e.GetCurrentPoint(Chips).Position) is not { } chip || chip.Index == d.To) return;
        drag = d with { To = chip.Index }; focus = chip.Index;
        ApplyDrag();
    }

    void OnChipsReleased(object sender, PointerRoutedEventArgs e) { drag = null; Chips.ReleasePointerCapture(e.Pointer); }
    void OnChipsCaptureLost(object sender, PointerRoutedEventArgs e) => drag = null;

    void ApplyDrag()
    {
        if (drag is not { } d) return;
        RangeSet(d.From, d.To, d.On, d.Base, d.From == d.To);
        Paint();
    }

    // ---------------- 键盘 ----------------

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.Right when chips.Count > 0:
                Move(focus + (e.Key == VirtualKey.Right ? 1 : -1), shift); break;
            case VirtualKey.Up or VirtualKey.Down when chips.Count > 0:
                Move(LineNeighbor(focus, e.Key == VirtualKey.Down ? 1 : -1), shift); break;
            case VirtualKey.Space when chips.Count > 0:
                keyboard = true; shiftBase = null; anchor = focus;
                if (!sel.Remove(focus)) sel.Add(focus);
                chips[focus].Pop(Host.Glass.Current.MotionSpeed); Paint(); break;
            case VirtualKey.A when ctrl: SelectAll(); break;
            case VirtualKey.Enter:
            case VirtualKey.C when ctrl: Copy(); break;
            case VirtualKey.Tab when SendButton.Visibility == Visibility.Visible: Send(); break;
            case VirtualKey.Escape:
                if (sel.Count > 0) { sel.Clear(); anchor = null; shiftBase = null; Paint(); }
                else CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }

    void OnKeyUp(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Shift) shiftBase = null; }

    static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    void Move(int to, bool extend)
    {
        to = Math.Clamp(to, 0, chips.Count - 1);
        keyboard = true;
        if (extend)
        {
            anchor ??= focus;
            shiftBase ??= [.. sel];
            RangeSet(anchor.Value, to, true, shiftBase);
        }
        else shiftBase = null;
        focus = to;
        Paint();
        chips[to].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
    }

    // 上下：到上一行 / 下一行里横向最近的一块
    int LineNeighbor(int k, int dir)
    {
        var o = chips[k].ActualOffset;
        double cx = o.X + chips[k].ActualWidth / 2;
        int best = k; double bestD = double.MaxValue;
        foreach (var c in chips)
        {
            var p = c.ActualOffset;
            if (dir > 0 ? p.Y <= o.Y + 4 : p.Y >= o.Y - 4) continue;
            double d = Math.Abs(p.Y - o.Y) * 4 + Math.Abs(p.X + c.ActualWidth / 2 - cx);
            if (d < bestD) { bestD = d; best = c.Index; }
        }
        return best;
    }

    // ---------------- 复制 / 交给启动器 ----------------

    async void Copy()
    {
        var text = Joined();
        if (text.Length == 0) return;
        try { await actions.CopyAsync(text, CancellationToken.None); }
        catch (Exception e) { Host.ReportFailure(Strings.Common_Copy, e); return; }
        if (Options.CloseAfterCopy) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    void Send()
    {
        var text = Joined();
        if (text.Length > 0) SendToLauncher?.Invoke(this, text);
    }

    void OnCopy(object sender, RoutedEventArgs e) => Copy();
    void OnSend(object sender, RoutedEventArgs e) => Send();
    void OnSelectAll(object sender, RoutedEventArgs e) { SelectAll(); Focus(FocusState.Programmatic); }
    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---------------- ISecondaryView ----------------

    Size preferred = new(Width_, HeadH + BodyMin + ActionsH + FootH);
    public Size PreferredSize => preferred;
    public Size MinimumSize => new(480, HeadH + BodyMin + ActionsH + FootH);
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged { add { } remove { } }
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => Focus(FocusState.Programmatic);
    public void OnHidden() { }
}
