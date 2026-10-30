// AI 结果卡片的逻辑：逐字追加结果、闪烁光标、未配置 / 出错状态、取消。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pane.Contracts;

namespace Pane.Views.Controls;

public sealed partial class AiCard : UserControl
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer blink;
    CancellationTokenSource? running;
    IAiService? ai;
    AiRequest? request;
    Action? openSettings;

    public AiCard()
    {
        InitializeComponent();
        blink = DispatcherQueue.CreateTimer();
        blink.Interval = TimeSpan.FromMilliseconds(450); // 原型 caret 0.9s 两步
        blink.Tick += (_, _) => Caret.Opacity = Caret.Opacity > 0 ? 0 : 1;
        Unloaded += (_, _) => Cancel();
    }

    /// <summary>当前已经拿到的结果文字（流式进行中也可以读）。</summary>
    public string Result => OutRun.Text;
    public bool IsDone { get; private set; }

    /// <summary>结果写完（或出错、取消）时触发；内容变化时也会触发 <see cref="ContentGrew"/> 方便面板跟着长高。</summary>
    public event EventHandler? Finished;
    public event EventHandler? ContentGrew;

    /// <param name="compact">超级面板用小号：结果 14px、没有最小高度；启动器用 16px、最小三行半。</param>
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
        Out.FontSize = size;
        Out.LineHeight = Math.Round(size * 1.7);
        Out.MinHeight = compact ? 0 : Math.Round(size * 1.7 * 2);
        Caret.Height = Math.Round(size * 1.05);
        KeyHints.Fill(Foot, hints, HintLabelProto, KbdProto);
        _ = Run();
    }

    public void Retry() { if (ai is not null && request is not null) _ = Run(); }

    public void Cancel()
    {
        running?.Cancel();
        running = null;
        blink.Stop();
        Caret.Visibility = Visibility.Collapsed;
    }

    async Task Run()
    {
        running?.Cancel();
        var cts = running = new CancellationTokenSource();
        IsDone = false;
        OutRun.Text = "";
        ProblemBox.Visibility = Visibility.Collapsed;
        OutScroll.Visibility = Visibility.Visible;
        if (ai!.Status != AiStatus.Ready)
        {
            ShowProblem(ai.Status == AiStatus.NeedsSignIn ? "ChatGPT 登录已过期，请重新登录。" : "还没配置 AI 服务。", ai.Status == AiStatus.NeedsSignIn ? "重新登录" : "去设置", settings: true);
            return;
        }
        Caret.Visibility = Visibility.Visible;
        Caret.Opacity = 1;
        blink.Start();
        try
        {
            await foreach (var chunk in ai.RunAsync(request!, cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                OutRun.Text += chunk;
                ContentGrew?.Invoke(this, EventArgs.Empty);
                OutScroll.ChangeView(null, OutScroll.ScrollableHeight, null, true);
            }
            IsDone = true;
        }
        catch (OperationCanceledException) { return; }
        catch (AiCredentialException) { ShowProblem("API Key 无效，请到设置里检查。", "去设置", settings: true); }
        catch (AiSignInRequiredException) { ShowProblem("ChatGPT 登录已过期，请重新登录。", "重新登录", settings: true); }
        catch (AiQuotaException e) { ShowProblem(string.IsNullOrEmpty(e.Message) ? "这个账号的额度用完了。" : e.Message, "重试", settings: false); }
        catch (AiException e) { ShowProblem(e.Message, "重试", settings: false); }
        catch (Exception) { ShowProblem("出了点问题，没拿到结果。", "重试", settings: false); }
        finally
        {
            if (running == cts)
            {
                blink.Stop();
                Caret.Visibility = Visibility.Collapsed;
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    bool problemOpensSettings;

    void ShowProblem(string text, string button, bool settings)
    {
        blink.Stop();
        Caret.Visibility = Visibility.Collapsed;
        OutScroll.Visibility = OutRun.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemText.Text = text;
        ProblemButton.Content = button;
        problemOpensSettings = settings;
        ProblemBox.Visibility = Visibility.Visible;
        ContentGrew?.Invoke(this, EventArgs.Empty);
    }

    void OnProblemButton(object sender, RoutedEventArgs e)
    {
        if (problemOpensSettings) openSettings?.Invoke();
        else Retry();
    }
}
