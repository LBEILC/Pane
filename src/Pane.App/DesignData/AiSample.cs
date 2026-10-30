// 界面开发用的 AI 假服务：照原型 prototype/data.js 的 aiCanned，逐字吐出写好的结果。不联网。
// 环境变量 PANE_DESIGN_AI=notconfigured / error 用来预览「未配置」「出错」两种卡片。
using System.Runtime.CompilerServices;
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class AiSample : IAiService
{
    public AiStatus Status => Environment.GetEnvironmentVariable("PANE_DESIGN_AI") == "notconfigured" ? AiStatus.NotConfigured : AiStatus.Ready;
    public event EventHandler? StatusChanged { add { } remove { } }
    public string? CurrentModelName => Status == AiStatus.Ready ? "演示模型" : null;

    public IReadOnlyList<AiAction> Actions { get; } =
    [
        new("translate", "翻译", "translate", "把下面的文字翻译成{lang}：\n{text}"),
        new("summary", "总结", "note", "用一句话总结：\n{text}"),
        new("polish", "润色", "brush", "润色下面的文字，保持原意：\n{text}"),
        new("explain", "解释", "ai", "用通俗的话解释：\n{text}"),
    ];

    static readonly Dictionary<string, string> Translations = new()
    {
        ["Glassmorphism relies on background blur, translucency and a subtle highlight border."] = "玻璃拟态依靠背景模糊、半透明，以及一圈细微的高光边框。",
        ["Frosted glass makes the interface feel light, but text must stay readable."] = "磨砂玻璃让界面显得轻盈，但文字必须保持清晰可读。",
    };

    public async IAsyncEnumerable<string> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken); // 模拟首字等待
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_AI") == "error")
            throw new AiNetworkException("连不上 AI 服务，请检查网络后重试。");
        var answer = request.ActionId switch
        {
            "translate" => Translations.GetValueOrDefault(request.Text)
                ?? (request.Text.Any(c => c is >= '一' and <= '龥') ? "(Demo) The English translation will appear here." : "（演示）这里显示 AI 返回的中文译文。"),
            "summary" => "（演示）一句话总结：界面要轻，但可读性优先。正式版由你配置的 AI 生成。",
            "polish" => "（演示）润色后：Frosted glass gives the interface a light, airy feel — as long as the text stays easy to read.",
            "explain" => "（演示）解释：Glassmorphism（玻璃拟态）是一种界面风格，用模糊的半透明面板模拟磨砂玻璃，再加细亮边增加层次。",
            _ => $"（演示）关于“{request.Question}”：正式版会把问题发给你在设置里配置的 AI 服务，回答像这样逐字出现。",
        };
        var chars = answer.EnumerateRunes().Select(r => r.ToString()).ToArray();
        for (int i = 0; i < chars.Length; i += 2)
        {
            await Task.Delay(28, cancellationToken);
            yield return string.Concat(chars.Skip(i).Take(2));
        }
    }

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiModel>>([new("demo", "演示模型"), new("demo-mini", "演示模型 mini")]);

    public string? TranslationModelName => Status == AiStatus.Ready ? "演示模型 mini" : null;
    public string? CurrentModelId => Status == AiStatus.Ready ? "demo" : null;

    // 对话：照原型 answerFor，按关键词给一段写好的回答（含列表、行内代码、代码块，看排版用）
    public async IAsyncEnumerable<string> ChatAsync(IReadOnlyList<AiChatMessage> messages, string? modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(320, cancellationToken);
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_AI") == "error")
            throw new AiNetworkException("连不上 AI 服务，请检查网络后重试。");
        var q = messages.LastOrDefault(m => m.Role == AiChatRole.User)?.Text ?? "";
        string answer;
        if (q.Contains("玻璃") || q.Contains("清晰") || q.Contains("glass", StringComparison.OrdinalIgnoreCase))
            answer = "让玻璃上的文字保持清晰，主要靠三件事：背景要糊、底色要压、文字要够实。\n\n- 模糊半径不低于 30 像素，背后的细节糊掉，文字才不打架；\n- 深色主题加一层深色着色（约 40%），亮壁纸也压得住；\n- 正文用不透明的文字色，`主题色` 只用在小面积的强调上。";
        else if (q.Contains("代码") || q.Contains("函数") || q.Contains("防抖"))
            answer = "可以用一个简单的防抖函数：每次输入都重新计时，停手 600 毫秒后才真正执行。\n\n```\nfunction debounce(fn, ms = 600) {\n  let t;\n  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };\n}\n```";
        else
            answer = $"（演示）关于「{q}」：正式版会把这段对话发给你在设置里配置的 AI，回答逐字流出来。\n\n可以接着追问，上下文会一直带着；关掉窗口就清空。";
        var chars = answer.EnumerateRunes().Select(r => r.ToString()).ToArray();
        for (int i = 0; i < chars.Length; i += 2)
        {
            await Task.Delay(24, cancellationToken);
            yield return string.Concat(chars.Skip(i).Take(2));
        }
    }
}

/// <summary>超级面板非 AI 动作的假实现：什么都不做（不开网页、不写剪贴板、不粘贴）。</summary>
public sealed class SelectionActionsSample : ISelectionActions
{
    public Task SearchWebAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CopyAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ReplaceSelectionAsync(string text, nint window, CancellationToken cancellationToken) => Task.CompletedTask;
}
