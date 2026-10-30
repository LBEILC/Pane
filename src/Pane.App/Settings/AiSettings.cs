namespace Pane.Settings;

public enum AiProvider { Api, ChatGpt }
public sealed record AiActionSettings(string Id, string Name, string Glyph, string PromptTemplate);
public sealed record AiSettings
{
    public AiProvider Provider { get; init; }
    public string ApiBaseUrl { get; init; }="https://api.openai.com/v1";
    public string? ApiModel { get; init; }
    public string? ChatGptModel { get; init; }
    public string TranslateTarget { get; init; }="auto";
    /// <summary>翻译用的模型 Id（设计加，2026-10-29）：单独指定一个快模型；null = 和主模型相同。</summary>
    public string? TranslationModel { get; init; }
    /// <summary>超级面板选中外文自动翻译（设计加，2026-10-30）：默认关；没配 AI 时设置页灰掉。</summary>
    public bool AutoTranslateSelection { get; init; }
    public string? ProxyUrl { get; init; }
    public AiActionSettings[] Actions { get; init; }=[
        new("translate","翻译","translate","把下面的文字翻译成{lang}。只输出译文，不要解释；保留原文的换行和格式。\n\n{text}"),
        new("summary","总结","note","简要总结下面的内容：先用一句话说结论，必要时再列不超过 3 个要点。用中文回答，只输出总结。\n\n{text}"),
        new("polish","润色","brush","润色下面的文字：改正错别字和语病，让表达更通顺自然；保持原意、原来的语言和大致长度。只输出润色后的文字。\n\n{text}"),
        new("explain","解释","ai","用通俗易懂的中文解释下面的内容。如果是代码，说明它做什么；如果是术语，给出含义和一个例子。\n\n{text}")];
    internal static bool ValidUrl(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme is "http" or "https"&&!string.IsNullOrEmpty(u.Host)&&string.IsNullOrEmpty(u.UserInfo)&&string.IsNullOrEmpty(u.Fragment);
    internal void Validate()
    {
        if(TranslationModel!=null&&string.IsNullOrWhiteSpace(TranslationModel)||!Enum.IsDefined(Provider)||!ValidUrl(ApiBaseUrl)||(ProxyUrl!=null&&!ValidUrl(ProxyUrl))||TranslateTarget is not ("auto" or "zh" or "en" or "ja" or "ko" or "fr" or "de" or "es" or "ru")||Actions==null||Actions.Any(a=>a==null||string.IsNullOrWhiteSpace(a.Id)||string.IsNullOrWhiteSpace(a.Name)||string.IsNullOrWhiteSpace(a.PromptTemplate))||Actions.Select(a=>a.Id).Distinct(StringComparer.Ordinal).Count()!=Actions.Length)
            throw new ArgumentException("AI 设置无效");
    }
}
