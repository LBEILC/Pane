namespace Pane.Settings;

/// <summary>
/// AI 内置服务商（设计定义，052，issue 11）：设置 → AI「服务商」一排标签的前几个。点了就用这一家，用户只需要粘贴 key。
/// 065 起用户还能添加自定义服务商（<see cref="AiEndpoint"/>），内置服务商的地址不能改，要改就添加一个自定义的。
/// </summary>
/// <param name="Id">稳定标识，不随界面语言变。</param>
/// <param name="Name">服务商名，各语言都用这个写法。</param>
/// <param name="BaseUrl">填进 <see cref="AiSettings.ApiBaseUrl"/>，不带结尾的斜杠。</param>
/// <param name="Model">填进 <see cref="AiSettings.ApiModel"/>：日常对话用的、便宜够用的那个。</param>
/// <param name="TranslationModel">填进 <see cref="AiSettings.TranslationModel"/>：翻译用的快模型；null = 和 Model 相同。</param>
/// <param name="KeyUrl">「获取 key」打开的页面。</param>
/// <param name="Format">接口格式（065）。现有四家都是 OpenAI 兼容。</param>
public sealed record AiPreset(string Id, string Name, string BaseUrl, string Model, string? TranslationModel, string KeyUrl, AiApiFormat Format = AiApiFormat.OpenAi);

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

    /// <summary>按 Id 找内置服务商；不是内置的返回 null。</summary>
    public static AiPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

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
