namespace Pane.Settings;

/// <summary>
/// AI 服务商预设（设计定义，052，issue 11）：设置 → AI 里的「服务商」下拉框。选了一个就把地址、模型、翻译模型填好，用户只需要粘贴 key。
/// 都是 OpenAI 兼容接口（<see cref="AiProvider.Api"/>）。「自定义」不在列表里：地址对不上任何预设时界面显示「自定义」。
/// </summary>
/// <param name="Id">稳定标识，不随界面语言变。</param>
/// <param name="Name">服务商名，各语言都用这个写法。</param>
/// <param name="BaseUrl">填进 <see cref="AiSettings.ApiBaseUrl"/>，不带结尾的斜杠。</param>
/// <param name="Model">填进 <see cref="AiSettings.ApiModel"/>：日常对话用的、便宜够用的那个。</param>
/// <param name="TranslationModel">填进 <see cref="AiSettings.TranslationModel"/>：翻译用的快模型；null = 和 Model 相同。</param>
/// <param name="KeyUrl">「获取 key」打开的页面。</param>
public sealed record AiPreset(string Id, string Name, string BaseUrl, string Model, string? TranslationModel, string KeyUrl);

public static class AiPresets
{
    /// <summary>下拉框里的顺序。地址和模型于 2026-11-03 按各家官方文档核实（052）。</summary>
    public static IReadOnlyList<AiPreset> All { get; } =
    [
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-flash", null, "https://platform.deepseek.com/api_keys"),
        new("qwen", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen3.8-flash", null, "https://bailian.console.aliyun.com/cn-beijing/model/settings/api-key"), // 不翻译：服务商名
        // Kimi 先不列（设计，2026-11-03）：K3 系列多轮对话要回传 reasoning_content，现有 AiChatMessage 表达不了，「继续对话」会出错（052 汇报第 6 节）。
        // 接口扩展后再加回：new("kimi", "Kimi", "https://api.moonshot.cn/v1", "kimi-k3.1-pro", null, "https://platform.kimi.com/console/api-keys")
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-5.3-flash", null, "https://bigmodel.cn/usercenter/proj-mgmt/apikeys"), // 不翻译：服务商名
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-6-luna", null, "https://platform.openai.com/api-keys"),
    ];

    /// <summary>按地址认出是哪个预设（忽略大小写、结尾斜杠和结尾的 /v1：DeepSeek 两种写法都能用）；认不出 = 自定义，返回 null。</summary>
    public static AiPreset? Match(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var url = Normalize(baseUrl);
        return All.FirstOrDefault(p => string.Equals(Normalize(p.BaseUrl), url, StringComparison.OrdinalIgnoreCase));
    }

    static string Normalize(string url)
    {
        url = url.Trim().TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }
}
