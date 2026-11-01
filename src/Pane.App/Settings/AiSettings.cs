using System.Text.Json.Serialization;
namespace Pane.Settings;

public enum AiProvider { Api, ChatGpt }
/// <summary>AI 请求走不走代理（042）：跟随系统代理、直连、用 <see cref="AiSettings.ProxyUrl"/>。</summary>
public enum AiProxyMode { System, None, Custom }
/// <summary>思考程度（042）：Default = 不传这个参数，用服务的默认；模型不支持时服务自动去掉。</summary>
public enum AiReasoningEffort { Default, Low, Medium, High }
public sealed record AiActionSettings(string Id, string Name, string Glyph, string PromptTemplate);
public sealed record AiSettings
{
    /// <summary>AI 总开关（设计加，2026-11-01，issue 8）：关掉后启动器、超级面板里的 AI 动作全部隐藏，选中外文也不自动翻译；其余配置保留。</summary>
    [JsonIgnore] public bool Enabled => !Disabled;
    /// <summary>
    /// 存的是「关掉了」（2026-11-01 改）：设置文件里没有的 init 属性，源生成的 JSON 读出来是类型默认值、不走 = true 这类初始值。
    /// 0.1.244 存成 Enabled（默认 true），从老版本升级的人文件里没有这一项，读出来成了「关」。改成 Disabled 后缺省 = false = 开着；
    /// 0.1.244 文件里的 Enabled 一律不认（会让在 0.1.244 里主动关过 AI 的人需要再关一次）。
    /// </summary>
    public bool Disabled { get; init; }
    public AiProvider Provider { get; init; }
    public string ApiBaseUrl { get; init; }="https://api.openai.com/v1";
    public string? ApiModel { get; init; }
    public string? ChatGptModel { get; init; }
    public string TranslateTarget { get; init; }="auto";
    /// <summary>翻译用的模型 Id（设计加，2026-10-29）：单独指定一个快模型；null = 和主模型相同。</summary>
    public string? TranslationModel { get; init; }
    /// <summary>超级面板选中外文自动翻译（设计加，2026-10-30）：默认关；没配 AI 时设置页灰掉。</summary>
    public bool AutoTranslateSelection { get; init; }
    /// <summary>自定义代理地址：http / https / socks5 / socks5h。<see cref="Proxy"/> 是 Custom 时用。</summary>
    public string? ProxyUrl { get; init; }
    /// <summary>代理方式（042），存的值；null = 旧设置文件没有这一项（见 <see cref="Proxy"/>）。写设置时写它。</summary>
    public AiProxyMode? ProxyMode { get; init; }
    /// <summary>实际的代理方式：没存过的，填过 ProxyUrl 当作 Custom，没填的跟随系统。</summary>
    [JsonIgnore] public AiProxyMode Proxy => ProxyMode ?? (ProxyUrl != null ? AiProxyMode.Custom : AiProxyMode.System);
    /// <summary>思考程度（042），对话、动作、翻译都按它带参数。</summary>
    public AiReasoningEffort Reasoning { get; init; }
    public AiActionSettings[] Actions { get; init; }=[
        new("translate","翻译","translate","把下面的文字翻译成{lang}。只输出译文，不要解释；保留原文的换行和格式。\n\n{text}"),
        new("summary","总结","note","简要总结下面的内容：先用一句话说结论，必要时再列不超过 3 个要点。用中文回答，只输出总结。\n\n{text}"),
        new("polish","润色","brush","润色下面的文字：改正错别字和语病，让表达更通顺自然；保持原意、原来的语言和大致长度。只输出润色后的文字。\n\n{text}"),
        new("explain","解释","ai","用通俗易懂的中文解释下面的内容。如果是代码，说明它做什么；如果是术语，给出含义和一个例子。\n\n{text}")];
    internal static bool ValidUrl(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme is "http" or "https"&&!string.IsNullOrEmpty(u.Host)&&string.IsNullOrEmpty(u.UserInfo)&&string.IsNullOrEmpty(u.Fragment);
    internal static bool ValidProxy(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme is "http" or "https" or "socks5" or "socks5h"&&!string.IsNullOrEmpty(u.Host)&&string.IsNullOrEmpty(u.Fragment);
    internal void Validate()
    {
        if(TranslationModel!=null&&string.IsNullOrWhiteSpace(TranslationModel)||!Enum.IsDefined(Provider)||!ValidUrl(ApiBaseUrl)||(ProxyUrl!=null&&!ValidProxy(ProxyUrl))||!Enum.IsDefined(Proxy)||Proxy==AiProxyMode.Custom&&ProxyUrl==null||!Enum.IsDefined(Reasoning)||TranslateTarget is not ("auto" or "zh" or "en" or "ja" or "ko" or "fr" or "de" or "es" or "ru")||Actions==null||Actions.Any(a=>a==null||string.IsNullOrWhiteSpace(a.Id)||string.IsNullOrWhiteSpace(a.Name)||string.IsNullOrWhiteSpace(a.PromptTemplate))||Actions.Select(a=>a.Id).Distinct(StringComparer.Ordinal).Count()!=Actions.Length)
            throw new ArgumentException("AI 设置无效");
    }
}
