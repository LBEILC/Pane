using Pane.Localization;
// AI 结果卡片的逻辑：流式结果按 Markdown 排（Markdown.cs）、闪烁光标、跟随底部、未配置 / 出错状态、取消。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;

namespace Pane.Views.Controls;

public sealed partial class AiCard : UserControl
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer blink, render;
    readonly Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object> blinkTick, renderTick;
    readonly PointerEventHandler wheel;

    /// <summary>所在视图永久销毁时调用：停掉生成并退订计时器（计时器不参与 XAML 引用跟踪，不退订会把整个视图留住）。</summary>
    public void Release()
    {
        Cancel();
        blink.Stop(); blink.Tick -= blinkTick;
        render.Stop(); render.Tick -= renderTick;
        OutScroll.RemoveHandler(PointerWheelChangedEvent, wheel);
    }
    CancellationTokenSource? running;
    IAiService? ai;
    AiRequest? request;
    Action? openSettings;
    MarkdownStyle? style;
    string result = "";
    bool live;
    readonly Border caret = new() { Width = 2, Margin = new Thickness(2, 0, 0, -3) };
    InlineUIContainer? caretHost;

    public AiCard()
    {
        InitializeComponent();
        blink = DispatcherQueue.CreateTimer();
        blink.Interval = TimeSpan.FromMilliseconds(450); // 原型 caret 0.9s 两步
        blinkTick = (_, _) => caret.Opacity = caret.Opacity > 0 ? 0 : 1;
        blink.Tick += blinkTick;
        // 流式结果每 50 毫秒最多重排一次（整段按 Markdown 重排，逐块追加会把没写完的写法排错）
        render = DispatcherQueue.CreateTimer();
        render.Interval = TimeSpan.FromMilliseconds(50);
        render.IsRepeating = false;
        renderTick = (_, _) => RenderOut();
        render.Tick += renderTick;
        // 跟随底部：用户往上滚（滚轮、触摸板）就不再自动滚到底；滚回底部恢复
        wheel = (_, e) => { if (e.GetCurrentPoint(OutScroll).Properties.MouseWheelDelta > 0) Unfollow(); };
        OutScroll.AddHandler(PointerWheelChangedEvent, wheel, handledEventsToo: true);
        OutScroll.DirectManipulationStarted += (_, _) => Unfollow();
        JumpText.Text = Strings.Ai_Card_JumpToLatest;
        Unloaded += (_, _) => Cancel();
    }

    /// <summary>当前已经拿到的结果文字（流式进行中也可以读）。</summary>
    public string Result => result;
    public bool IsDone { get; private set; }

    /// <summary>
    /// 卡片整体最多多高（不含卡片外边距）；大于 0 时结果区按它放大（启动器给，用户群反馈「问 AI 时界面太短」），
    /// 0 时结果区最高 300（超级面板）。
    /// </summary>
    public double HeightBudget { get; set; }

    /// <summary>结果写完（或出错、取消）时触发；内容变化时也会触发 <see cref="ContentGrew"/> 方便面板跟着长高。</summary>
    public event EventHandler? Finished;
    public event EventHandler? ContentGrew;

    /// <param name="compact">超级面板用小号：结果 14px、没有最小高度；启动器用 16px、最小两行。</param>
    /// <param name="openSettings">「去设置」按钮做什么（未配置时）。</param>
    public void Start(IAiService service, AiRequest req, string title, string glyph, string? source, bool compact,
        IEnumerable<(string[] keys, string label)> hints, Action? openSettings = null)
    {
        Cancel();
        ai = service;
        request = req;
        this.openSettings = openSettings;
        HeadIcon.Glyph = glyph;
        HeadTitle.Text = title;
        HeadModel.Text = (req.ActionId == "translate" ? service.TranslationModelName : service.CurrentModelName) ?? "";   // 翻译用单独指定的快模型
        SourceText.Text = source ?? "";
        SourceBox.Visibility = string.IsNullOrEmpty(source) ? Visibility.Collapsed : Visibility.Visible;
        var size = compact ? 14.0 : 16.0;
        style = new MarkdownStyle
        {
            Font = Text1Proto.FontFamily,
            Mono = Text2Proto.FontFamily,
            MathFont = MathProto.FontFamily,
            Size = size,
            LineHeight = Math.Round(size * 1.7),
            Text1 = Text1Proto.Foreground,
            Text2 = Text2Proto.Foreground,
            Text3 = HintLabelProto.Foreground,
            Stroke = CodeProto.BorderBrush,
            CodeFill = CodeProto.Background,
            Accent = (SolidColorBrush)MathProto.Foreground,
        };
        Out.MinHeight = compact ? 0 : Math.Round(size * 1.7 * 2);
        caret.Height = Math.Round(size * 1.05);
        caret.Background = Text1Proto.Foreground;
        Waiting.Height = style.LineHeight;
        WaitingText.Text = Strings.Ai_Card_Waiting;
        KeyHints.Fill(Foot, hints, HintLabelProto, KbdProto);
        OutScroll.MaxHeight = 300;
        _ = Run();
    }

    public void Retry() { if (ai is not null && request is not null) _ = Run(); }

    public void Cancel()
    {
        running?.Cancel();
        running = null;
        blink.Stop();
        render.Stop();
        if (live) { live = false; RenderOut(); }
        ShowWaiting(false);
        UpdateJump();
    }

    /// <summary>启动器里按 ↑ ↓ / PageUp PageDown 滚动结果（卡片没有焦点）。往上滚就不再跟随底部。</summary>
    public void ScrollBy(double delta)
    {
        if (delta < 0) Unfollow();
        OutScroll.ChangeView(null, Math.Clamp(OutScroll.VerticalOffset + delta, 0, OutScroll.ScrollableHeight), null);
    }

    /// <summary>一页的高度（PageUp / PageDown 用）。</summary>
    public double PageHeight => Math.Max(60, OutScroll.ViewportHeight - (style?.LineHeight ?? 24));

    // ---------------- 跟随底部 ----------------

    bool follow = true;

    void Unfollow()
    {
        if (OutScroll.ScrollableHeight <= 0) return;
        follow = false;
        UpdateJump();
    }

    void OnOutScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!follow && !e.IsIntermediate && OutScroll.VerticalOffset >= OutScroll.ScrollableHeight - 2) follow = true;
        UpdateJump();
    }

    void OnJump(object sender, RoutedEventArgs e)
    {
        follow = true;
        UpdateJump();
        OutScroll.ChangeView(null, OutScroll.ScrollableHeight, null);
    }

    void UpdateJump() => JumpButton.Visibility = !follow && live ? Visibility.Visible : Visibility.Collapsed;

    void ToBottom()
    {
        if (!follow) return;
        OutScroll.UpdateLayout();
        OutScroll.ChangeView(null, OutScroll.ScrollableHeight, null, true);
    }

    // ---------------- 排版 ----------------

    void RenderOut()
    {
        if (style is null) return;
        if (caretHost is not null) { caretHost.Child = null; caretHost = null; }
        Out.Children.Clear();
        if (result.Length > 0 || live)
            foreach (var block in MarkdownRender.Build(result, style, live ? caret : null, out caretHost)) Out.Children.Add(block);
        FitBudget();
        ContentGrew?.Invoke(this, EventArgs.Empty);
        ToBottom();
    }

    // 启动器：结果区最高 = 预算 − 标题行、原文、快捷键提示占的高度；至少 300
    void FitBudget()
    {
        if (HeightBudget <= 0) return;
        UpdateLayout();
        double chrome = ActualHeight - OutScroll.ActualHeight;
        if (chrome > 0) OutScroll.MaxHeight = Math.Max(300, HeightBudget - chrome);
    }

    // 等待状态（issue 12）：三个点依次亮起，动画在合成线程上跑，不唤醒界面线程；第一个字回来、出错、取消时停掉
    bool waiting;
    void ShowWaiting(bool on)
    {
        if (waiting == on) return;
        waiting = on;
        Waiting.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Shapes.Ellipse[] dots = [Dot1, Dot2, Dot3];
        for (int i = 0; i < dots.Length; i++)
        {
            var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(dots[i]);
            if (!on) { v.StopAnimation("Opacity"); v.Opacity = 1; continue; }
            var pulse = v.Compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0, 0.25f);
            pulse.InsertKeyFrame(0.3f, 1f);
            pulse.InsertKeyFrame(0.6f, 0.25f);
            pulse.InsertKeyFrame(1, 0.25f);
            pulse.Duration = TimeSpan.FromMilliseconds(1200);
            pulse.DelayTime = TimeSpan.FromMilliseconds(i * 180);
            pulse.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
            pulse.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            v.StartAnimation("Opacity", pulse);
        }
    }

    async Task Run()
    {
        running?.Cancel();
        var cts = running = new CancellationTokenSource();
        IsDone = false;
        result = "";
        live = false;
        follow = true;
        render.Stop();
        RenderOut();
        ProblemBox.Visibility = Visibility.Collapsed;
        OutScroll.Visibility = Visibility.Visible;
        if (ai!.Status != AiStatus.Ready)
        {
            ShowProblem(ai.Status == AiStatus.NeedsSignIn ? Strings.Ai_Card_LoginExpired : Strings.Ai_Card_NotConfigured, ai.Status == AiStatus.NeedsSignIn ? Strings.Ai_Card_LoginAgain : Strings.Common_GoToSettings, settings: true);
            return;
        }
        ShowWaiting(true);
        try
        {
            await foreach (var chunk in ai.RunAsync(request!, cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                if (waiting && chunk.Length > 0)
                {
                    // 第一个字回来：等待状态换成逐字出现的光标
                    ShowWaiting(false);
                    live = true;
                    caret.Opacity = 1;
                    blink.Start();
                    result += chunk;
                    RenderOut();
                    continue;
                }
                result += chunk;
                if (!render.IsRunning) render.Start();
            }
            IsDone = true;
        }
        catch (OperationCanceledException) { return; }
        catch (AiCredentialException) { ShowProblem(Strings.Ai_Card_InvalidKey, Strings.Common_GoToSettings, settings: true); }
        catch (AiSignInRequiredException) { ShowProblem(Strings.Ai_Card_LoginExpired, Strings.Ai_Card_LoginAgain, settings: true); }
        catch (AiQuotaException e) { ShowProblem(string.IsNullOrEmpty(e.Message) ? Strings.Ai_Card_QuotaExceeded : e.Message, Strings.Common_Retry, settings: false); }
        catch (AiException e) { ShowProblem(e.Message, Strings.Common_Retry, settings: false); }
        catch (Exception) { ShowProblem(Strings.Ai_Card_Failed, Strings.Common_Retry, settings: false); }
        finally
        {
            if (running == cts)
            {
                ShowWaiting(false);
                blink.Stop();
                render.Stop();
                live = false;
                RenderOut();
                UpdateJump();
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    bool problemOpensSettings;

    void ShowProblem(string text, string button, bool settings)
    {
        ShowWaiting(false);
        blink.Stop();
        render.Stop();
        live = false;
        RenderOut();
        OutScroll.Visibility = result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemText.Text = text;
        ProblemButton.Content = button;
        problemOpensSettings = settings;
        ProblemBox.Visibility = Visibility.Visible;
        ContentGrew?.Invoke(this, EventArgs.Empty);
    }

    void OnProblemButton(object sender, RoutedEventArgs e)
    {
        // 调用方没给就直接打开「设置 → AI」（issue 11：启动器里没传，点了没反应）
        if (problemOpensSettings) (openSettings ?? (() => ((App)Application.Current).OpenSettings("ai")))();
        else Retry();
    }
}
