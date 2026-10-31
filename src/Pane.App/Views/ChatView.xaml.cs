// AI 对话窗口逻辑（设计负责人）。照原型 prototype/launcher.html「二级：AI 对话窗口」：
// 用户消息右侧气泡，AI 回答左侧正文（段落、列表、行内代码、代码块），逐字流出；回答下方复制，最后一条可「重新回答」。
// 生成中发送键变成停止键；Esc 先停止、再清空输入（不关窗口）；Ctrl+N 新对话；Ctrl+W 关。只保留当前这次对话，关窗口即清空。
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class ChatView : UserControl, ISecondaryView
{
    sealed class Message(AiChatRole role, string text)
    {
        public AiChatRole Role { get; } = role;
        public string Text { get; set; } = text;
        public bool Live, Stopped;
        public string? Error;
        public FrameworkElement? Element;
    }

    readonly IAiService ai;
    readonly ISelectionActions output;
    readonly List<Message> messages = [];
    CancellationTokenSource? running;
    string? modelId;
    string? modelName;
    bool topmost;

    public ChatView(IAiService ai, ISelectionActions output)
    {
        this.ai = ai;
        this.output = output;
        InitializeComponent();
        Controls.PaneCaret.Attach(Input);
        KeyHints.Fill(Hints, [(["Enter"], "发送"), (["Shift", "+", "Enter"], "换行"), (["Esc"], "停止生成 / 清空")], HintLabelProto, KbdProto);
        Hints.Children.Add(new TextBlock { Text = "只保留这一次对话", VerticalAlignment = VerticalAlignment.Center, FontFamily = HintLabelProto.FontFamily, FontSize = HintLabelProto.FontSize, Foreground = HintLabelProto.Foreground });
        PreviewKeyDown += OnKeyDown;
        PointerPressed += (_, e) => { if (ModelMenu.Visibility == Visibility.Visible && !IsInside(e.OriginalSource, ModelMenu) && !IsInside(e.OriginalSource, ModelButton)) ModelMenu.Visibility = Visibility.Collapsed; };
        Render();
        SetBusy(false);
    }

    /// <summary>
    /// 打开（或复用）窗口时宿主调用。history：从启动器 AI 卡片「继续对话」带过来的一问一答（替换当前对话）；
    /// question：直接发出的问题（启动器「问 AI」按 Tab）。
    /// </summary>
    public void Open(string? question, IReadOnlyList<AiChatMessage>? history)
    {
        UpdateModel();
        if (history is { Count: > 0 })
        {
            Stop();
            messages.Clear();
            foreach (var m in history) messages.Add(new Message(m.Role, m.Text));
            Render();
        }
        if (!string.IsNullOrWhiteSpace(question)) Ask(question);
    }

    /// <summary>超级面板「问 AI」：选中的文字放进输入框（不发出），光标放在后面等你写问题。</summary>
    public void SetDraft(string text)
    {
        UpdateModel();
        Input.Text = text.TrimEnd() + "\n\n";
        Input.SelectionStart = Input.Text.Length;
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(780, 620);
    public Size MinimumSize => new(520, 420);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => Input.Focus(FocusState.Programmatic);
    public void OnHidden() => Stop();

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;
        ToolTipService.SetToolTip(PinButton, topmost ? "总在最前：开" : "总在最前");
        TopmostChanged?.Invoke(this, topmost);
    }

    // ---------------- 模型 ----------------

    void UpdateModel()
    {
        modelName ??= ai.CurrentModelName;
        ModelText.Text = ai.Status == AiStatus.NotConfigured ? "还没配置 AI" : modelName ?? "模型";
    }

    async void OnModel(object sender, RoutedEventArgs e)
    {
        if (ModelMenu.Visibility == Visibility.Visible) { ModelMenu.Visibility = Visibility.Collapsed; return; }
        if (ai.Status == AiStatus.NotConfigured) return;
        ModelList.Children.Clear();
        ModelList.Children.Add(MenuNote("正在读取模型…"));
        ModelMenu.Visibility = Visibility.Visible;
        IReadOnlyList<AiModel> models;
        try { models = await ai.ListModelsAsync(CancellationToken.None); }
        catch (Exception ex) { ModelList.Children.Clear(); ModelList.Children.Add(MenuNote("读取失败：" + (App.FailureReason(ex) ?? "出了点问题"))); return; }
        ModelList.Children.Clear();
        var current = modelId ?? ai.CurrentModelId;
        foreach (var m in models.Take(30))
        {
            bool on = m.Id == current || current is null && m.DisplayName == modelName;
            var row = new Grid { Height = PaneTokens.ControlMd, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = m.DisplayName, VerticalAlignment = VerticalAlignment.Center, FontFamily = BodyProto.FontFamily, FontSize = 13, Foreground = BodyProto.Foreground, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal });
            if (on) { var check = new PaneIcon { Glyph = "check", Width = 14, Height = 14, Foreground = BodyProto.Foreground, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(check, 1); row.Children.Add(check); }
            row.PointerEntered += (_, _) => row.Background = HoverProto.Background;
            row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.Tapped += (_, _) => { modelId = m.Id; modelName = m.DisplayName; ModelText.Text = m.DisplayName; ModelMenu.Visibility = Visibility.Collapsed; Input.Focus(FocusState.Programmatic); };
            ModelList.Children.Add(row);
        }
    }

    TextBlock MenuNote(string text) => new() { Text = text, Margin = new Thickness(10, 6, 10, 6), FontFamily = HintLabelProto.FontFamily, FontSize = 12, Foreground = HintLabelProto.Foreground, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };

    static bool IsInside(object source, DependencyObject container)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d)) if (d == container) return true;
        return false;
    }

    // ---------------- 发送与生成 ----------------

    bool Busy => running is not null;

    void OnInputChanged(object sender, TextChangedEventArgs e) => SetBusy(Busy);

    void OnSend(object sender, RoutedEventArgs e) { if (Busy) Stop(); else Ask(Input.Text); }

    void OnSuggestion(object sender, RoutedEventArgs e) => Ask((string)((Button)sender).Content);

    void OnNew(object sender, RoutedEventArgs e) => NewChat();

    void NewChat()
    {
        Stop();
        messages.Clear();
        Render();
        Input.Focus(FocusState.Programmatic);
    }

    void Ask(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || Busy) return;
        messages.Add(new Message(AiChatRole.User, text));
        Input.Text = "";
        _ = Reply();
    }

    async Task Reply()
    {
        var history = messages.Where(m => m.Error is null).Select(m => new AiChatMessage(m.Role, m.Text)).ToList();
        var answer = new Message(AiChatRole.Assistant, "") { Live = true };
        messages.Add(answer);
        Render();
        var cts = running = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            if (ai.Status == AiStatus.NotConfigured) throw new AiCredentialException("还没配置 AI 服务，去 设置 → AI 里配置");
            await foreach (var piece in ai.ChatAsync(history, modelId, cts.Token))
            {
                if (cts.IsCancellationRequested) break;
                answer.Text += piece;
                Refresh(answer);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { answer.Error = App.FailureReason(e) ?? "出了点问题，稍后再试"; }
        finally
        {
            answer.Live = false;
            if (cts.IsCancellationRequested && answer.Error is null) answer.Stopped = true;
            if (running == cts) running = null;
            Render();
            SetBusy(Busy);
        }
    }

    void Stop()
    {
        running?.Cancel();
        running = null;
        SetBusy(false);
    }

    void Again()
    {
        if (Busy || messages.Count < 2 || messages[^1].Role != AiChatRole.Assistant) return;
        messages.RemoveAt(messages.Count - 1);
        _ = Reply();
    }

    void SetBusy(bool busy)
    {
        SendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopIcon.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // 停止键：深浅色反过来（正文色底 + 玻璃色方块），和原型 .send.stop 一样
        SendButton.Background = busy ? StopProto.Background : AccentBrush();
        StopIcon.Background = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 22, 22, 25) : Microsoft.UI.Colors.White);
        SendButton.IsEnabled = busy || Input.Text.Trim().Length > 0;
        ToolTipService.SetToolTip(SendButton, busy ? "停止（Esc）" : "发送（Enter）");
    }

    Brush? accent;
    Brush AccentBrush() => accent ??= SendButton.Background;

    // ---------------- 渲染 ----------------

    void Render()
    {
        Messages.Children.Clear();
        EmptyState.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            m.Element = m.Role == AiChatRole.User ? UserBubble(m.Text) : AiBlock(m, i == messages.Count - 1);
            Messages.Children.Add(m.Element);
        }
        ScrollToEnd();
    }

    // 生成中只重建这一条
    void Refresh(Message m)
    {
        int i = Messages.Children.IndexOf(m.Element);
        if (i < 0) return;
        m.Element = AiBlock(m, true);
        Messages.Children[i] = m.Element;
        ScrollToEnd();
    }

    void ScrollToEnd() { ChatScroller.UpdateLayout(); ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight, null, true); }

    FrameworkElement UserBubble(string text) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = Math.Max(240, (ActualWidth > 0 ? ActualWidth : 780) * 0.78 - 44),
        Padding = new Thickness(14, 9, 14, 9),
        CornerRadius = new CornerRadius(16, 16, 4, 16),
        Background = BubbleProto.Background,
        BorderBrush = BubbleProto.BorderBrush,
        BorderThickness = new Thickness(1),
        Child = Body(text, selectable: true),
    };

    FrameworkElement AiBlock(Message m, bool last)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = Math.Max(300, (ActualWidth > 0 ? ActualWidth : 780) * 0.92 - 44) };
        if (m.Text.Length > 0) foreach (var block in Markdown(m.Text)) panel.Children.Add(block);
        if (m.Live) panel.Children.Add(Caret());
        if (m.Stopped) panel.Children.Add(Note("已停止", Text3Proto.Foreground));
        if (m.Error is { } error) panel.Children.Add(Note(error, ErrorProto.Foreground));
        if (!m.Live)
        {
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(-4, -4, 0, 0) };
            if (m.Text.Length > 0) acts.Children.Add(MiniButton("paste", "复制", () => _ = output.CopyAsync(m.Text, CancellationToken.None)));
            if (last) acts.Children.Add(MiniButton("refresh", "重新回答", Again));
            if (acts.Children.Count > 0) panel.Children.Add(acts);
        }
        return panel;
    }

    Button MiniButton(string glyph, string tip, Action click)
    {
        var b = new Button { Style = IconButtonProto.Style, Width = PaneTokens.ControlXs, Height = PaneTokens.ControlXs, Content = new PaneIcon { Glyph = glyph, Width = 14, Height = 14 } };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    TextBlock Note(string text, Brush brush) => new() { Text = text, FontFamily = BodyProto.FontFamily, FontSize = 13, Foreground = brush, TextWrapping = TextWrapping.Wrap };

    Border Caret() => new() { Width = 7, Height = 15, CornerRadius = new CornerRadius(2), Background = Text3Proto.Foreground, HorizontalAlignment = HorizontalAlignment.Left };

    TextBlock Body(string text, bool selectable)
    {
        var t = new TextBlock { FontFamily = BodyProto.FontFamily, FontSize = BodyProto.FontSize, Foreground = BodyProto.Foreground, LineHeight = 24, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = selectable };
        Inline(t, text);
        return t;
    }

    // 很小的 Markdown 子集：``` 代码块、"- " / "* " / "1. " 列表、空行分段；行内 `代码` 和 **加粗**
    IEnumerable<FrameworkElement> Markdown(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                foreach (var p in Flush()) yield return p;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                yield return CodeBlock(string.Join("\n", code));
                continue;
            }
            if (ListItem(line) is { } item)
            {
                foreach (var p in Flush()) yield return p;
                var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(6, 0, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(new TextBlock { Text = item.Bullet, FontFamily = BodyProto.FontFamily, FontSize = BodyProto.FontSize, Foreground = Text3Proto.Foreground, LineHeight = 24 });
                var body = Body(item.Text, true);
                Grid.SetColumn(body, 1);
                grid.Children.Add(body);
                yield return grid;
                continue;
            }
            if (line.Trim().Length == 0) { foreach (var p in Flush()) yield return p; continue; }
            para.Add(line);
        }
        foreach (var p in Flush()) yield return p;

        IEnumerable<FrameworkElement> Flush()
        {
            if (para.Count == 0) yield break;
            var t = string.Join("\n", para);
            para.Clear();
            yield return Body(t, true);
        }
    }

    static (string Bullet, string Text)? ListItem(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) return ("•", t[2..]);
        int dot = t.IndexOf(". ", StringComparison.Ordinal);
        if (dot is > 0 and <= 3 && t[..dot].All(char.IsAsciiDigit)) return (t[..(dot + 1)], t[(dot + 2)..]);
        return null;
    }

    Border CodeBlock(string code) => new()
    {
        Padding = new Thickness(12, 10, 12, 10),
        CornerRadius = new CornerRadius(12),
        Background = CodeProto.Background,
        BorderBrush = CodeProto.BorderBrush,
        BorderThickness = new Thickness(1),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock { Text = code, FontFamily = MonoProto.FontFamily, FontSize = 12, LineHeight = 19, Foreground = BodyProto.Foreground, IsTextSelectionEnabled = true },
        },
    };

    void Inline(TextBlock t, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int code = text.IndexOf('`', i), bold = text.IndexOf("**", i, StringComparison.Ordinal);
            int next = code < 0 ? bold : bold < 0 ? code : Math.Min(code, bold);
            if (next < 0) { t.Inlines.Add(new Run { Text = text[i..] }); break; }
            if (next > i) t.Inlines.Add(new Run { Text = text[i..next] });
            if (next == code)
            {
                int end = text.IndexOf('`', code + 1);
                if (end < 0) { t.Inlines.Add(new Run { Text = text[code..] }); break; }
                t.Inlines.Add(new Run { Text = text[(code + 1)..end], FontFamily = MonoProto.FontFamily, FontSize = 12.5, Foreground = MonoProto.Foreground });
                i = end + 1;
            }
            else
            {
                int end = text.IndexOf("**", bold + 2, StringComparison.Ordinal);
                if (end < 0) { t.Inlines.Add(new Run { Text = text[bold..] }); break; }
                var b = new Bold();
                b.Inlines.Add(new Run { Text = text[(bold + 2)..end] });
                t.Inlines.Add(b);
                i = end + 2;
            }
        }
    }

    // ---------------- 键盘 ----------------

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Enter when !shift && !ctrl && ReferenceEquals(e.OriginalSource, Input): Ask(Input.Text); break;
            case VirtualKey.Escape:
                // 不关窗口：先关模型菜单，再停止生成，再清空输入
                if (ModelMenu.Visibility == Visibility.Visible) ModelMenu.Visibility = Visibility.Collapsed;
                else if (Busy) Stop();
                else Input.Text = "";
                Input.Focus(FocusState.Programmatic);
                break;
            case VirtualKey.N when ctrl: NewChat(); break;
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }
}
