using Pane.Localization;
// 翻译窗口逻辑（设计负责人）。照原型 prototype/launcher.html「二级：翻译窗口」：
// 边打边翻（停手 0.6 秒），Ctrl+Enter 立即翻；原文是中文翻成英文，其余翻成「翻译成」（默认跟设置，设置是自动时为中文）；
// 点「翻译成」临时换目标语言（清空后回到自动）；⇄ 把译文放到左边接着翻。翻译用设置里单独指定的快模型。
// Esc 只清空原文（不关窗口）；Ctrl+W 关。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views;

public sealed partial class TranslateView : UserControl, ISecondaryView
{
    static readonly (string Code, string Name)[] Langs = Languages.All;
    static string Idle => Strings.Translate_ResultPlaceholder;

    readonly IAiService ai;
    readonly ISelectionActions output;
    readonly DispatcherQueueTimerHolder debounce;
    CancellationTokenSource? running;
    string last = "", target = "zh";
    string? manualTarget;
    bool topmost;

    /// <summary>未配置 AI 时「去设置」。</summary>
    public event EventHandler? OpenSettings;

    public TranslateView(IAiService ai, ISelectionActions output)
    {
        this.ai = ai;
        this.output = output;
        InitializeComponent();
        Controls.PaneCaret.Attach(Source);
        debounce = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromMilliseconds(600), () => _ = Translate(force: false));
        KeyHints.Fill(Hints, [(["Ctrl", "+", "Enter"], Strings.Translate_TranslateNow), (["Esc"], Strings.Common_Clear)], HintLabelProto, KbdProto);
        PreviewKeyDown += OnKeyDown;
        ai.StatusChanged += OnStatusChanged;
        Unloaded += (_, _) => ai.StatusChanged -= OnStatusChanged;
        ShowIdle();
        UpdateModel();
    }

    void OnStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateModel);

    /// <summary>打开（或复用）窗口时宿主调用：带上要翻译的文字，立即翻。</summary>
    public void Open(string? text)
    {
        UpdateModel();
        if (text is null) return;
        manualTarget = null;
        Source.Text = text;
        Source.SelectionStart = text.Length;
        _ = Translate(force: true);
    }

    // ---------------- ISecondaryView ----------------

    public Size PreferredSize => new(760, 460);
    public Size MinimumSize => new(560, 340);
    public event EventHandler<Size>? SizeRequested { add { } remove { } }
    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? TopmostChanged;
    public UIElement? DragRegion => Head;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() => Source.Focus(FocusState.Programmatic);
    public void OnHidden() => running?.Cancel();

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    void OnPin(object sender, RoutedEventArgs e)
    {
        topmost = !topmost;
        PinIcon.IsFilled = topmost;
        ToolTipService.SetToolTip(PinButton, topmost ? Strings.Common_AlwaysOnTopEnabled : Strings.Common_AlwaysOnTop);
        TopmostChanged?.Invoke(this, topmost);
    }

    void UpdateModel() => ModelText.Text = ai.Status == AiStatus.NotConfigured || ai.TranslationModelName is null
        ? Strings.Translate_NotConfigured
        : string.Format(Strings.Translate_ModelHint, (object?)(ai.TranslationModelName));

    // ---------------- 翻译 ----------------

    void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        CountText.Text = string.Format(Strings.Translate_CharacterCount, (object?)(Source.Text.Length));
        if (Source.Text.Trim().Length == 0) { manualTarget = null; debounce.Stop(); _ = Translate(force: false); }
        else debounce.Restart();
    }

    static string Detect(string s) => Languages.Detect(s);
    static string NameOf(string code) => Languages.NameOf(code);
    string AutoTarget(string text) => Languages.AutoTarget(text, ((App)Application.Current).Store.Current.Ai.TranslateTarget);

    async Task Translate(bool force)
    {
        debounce.Stop();
        var text = Source.Text.Trim();
        if (!force && text == last) return;
        last = text;
        running?.Cancel();
        if (text.Length == 0) { FromText.Text = Strings.Translate_AutoDetect; ToText.Text = NameOf(AutoTarget("")); ShowIdle(); return; }

        target = manualTarget ?? AutoTarget(text);
        FromText.Text = string.Format(Strings.Translate_DetectedLanguage, (object?)(Detect(text)));
        ToText.Text = NameOf(target);
        ActionButton.Visibility = Visibility.Collapsed;
        if (ai.Status == AiStatus.NotConfigured)
        {
            ShowMessage(Strings.Translate_ConfigureHint, MessageAction.Settings);
            return;
        }

        var cts = running = new CancellationTokenSource();
        Out.Text = "";
        Out.Foreground = TextProto.Foreground;
        StateText.Text = Strings.Translate_Translating;
        try
        {
            await foreach (var piece in ai.RunAsync(new AiRequest("translate", text, target, null), cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                Out.Text += piece;
            }
            if (cts == running) StateText.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (AiSignInRequiredException e) { if (cts == running) ShowMessage(e.Message, MessageAction.Settings); }
        catch (AiCredentialException e) { if (cts == running) ShowMessage(e.Message, MessageAction.Settings); }
        catch (Exception e) { if (cts == running) ShowMessage(App.FailureReason(e) ?? Strings.Translate_Failed, MessageAction.Retry); }
    }

    void ShowIdle()
    {
        Out.Text = Idle;
        Out.Foreground = IdleProto.Foreground;
        StateText.Text = "";
        ActionButton.Visibility = Visibility.Collapsed;
    }

    enum MessageAction { Settings, Retry }
    MessageAction messageAction;
    void ShowMessage(string message, MessageAction action)
    {
        Out.Text = message;
        Out.Foreground = IdleProto.Foreground;
        StateText.Text = "";
        messageAction = action;
        ActionButton.Content = action == MessageAction.Settings ? Strings.Common_GoToSettings : Strings.Common_Retry;
        ActionButton.Visibility = Visibility.Visible;
    }

    bool HasResult => Out.Foreground == TextProto.Foreground && Out.Text.Length > 0;

    void OnAction(object sender, RoutedEventArgs e)
    {
        if (messageAction == MessageAction.Settings) OpenSettings?.Invoke(this, EventArgs.Empty);
        else _ = Translate(force: true);
    }

    void OnClear(object sender, RoutedEventArgs e) { Source.Text = ""; Source.Focus(FocusState.Programmatic); }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!HasResult) { StateText.Text = Strings.Translate_NoResult; return; }
        _ = output.CopyAsync(Out.Text, CancellationToken.None);
        StateText.Text = Strings.Translate_Copied;
    }

    // 换目标语言：按列表轮换，跳过和原文相同的语言；只对这段文字生效，清空后回到自动
    void OnTarget(object sender, RoutedEventArgs e)
    {
        int i = Array.FindIndex(Langs, l => l.Code == target);
        var from = Detect(Source.Text);
        for (int k = 1; k <= Langs.Length; k++)
        {
            var next = Langs[(i + k) % Langs.Length];
            if (next.Name != from) { manualTarget = next.Code; break; }
        }
        _ = Translate(force: true);
        Source.Focus(FocusState.Programmatic);
    }

    // 对调：译文放到左边，目标换成原来的语言（原文是中文就回到中文）
    void OnSwap(object sender, RoutedEventArgs e)
    {
        if (!HasResult) return;
        var from = Detect(Source.Text);
        manualTarget = Langs.FirstOrDefault(l => l.Name == from).Code;
        var text = Out.Text;
        Source.Text = text;
        Source.SelectionStart = text.Length;
        _ = Translate(force: true);
        Source.Focus(FocusState.Programmatic);
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Enter when ctrl: _ = Translate(force: true); break;
            case VirtualKey.Escape: if (Source.Text.Length > 0) Source.Text = ""; Source.Focus(FocusState.Programmatic); break;   // 不关窗口
            case VirtualKey.W when ctrl: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }
}
