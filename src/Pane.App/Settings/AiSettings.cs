using Pane.Localization;
using System.Text.Json.Serialization;
namespace Pane.Settings;

public enum AiProvider { Api, ChatGpt }
/// <summary>AI 请求走不走代理（042）：跟随系统代理、直连、用 <see cref="AiSettings.ProxyUrl"/>。</summary>
public enum AiProxyMode { System, None, Custom }
/// <summary>思考程度（042）：Default = 不传这个参数，用服务的默认；模型不支持时服务自动去掉。</summary>
public enum AiReasoningEffort { Default, Low, Medium, High }
public sealed record AiActionSettings(string Id, string Name, string Glyph, string PromptTemplate);

/// <summary>第三方 API 的接口格式（2026-11-06 用户定）：OpenAI 兼容（/chat/completions）或 Anthropic Messages（/messages）。</summary>
public enum AiApiFormat { OpenAi, Anthropic }

/// <summary>
/// 一个第三方 API 服务商（2026-11-06 用户定，任务 065）。每家各记各的地址、格式、模型和 key，来回切换不用重填。
/// <para>内置服务商：<see cref="Id"/> 等于 <see cref="AiPreset.Id"/>，只有 <see cref="Model"/> / <see cref="TranslationModel"/> 来自这里，
/// <see cref="Name"/> / <see cref="BaseUrl"/> / <see cref="Format"/> 一律以 <see cref="AiPresets"/> 为准（存了也不认）；没改过模型的内置服务商可以不在列表里。</para>
/// <para>自定义服务商：<see cref="Id"/> 形如 "custom-" + 8 位小写十六进制，生成后不变（key 跟着它存）；<see cref="Name"/> 用户起的名字（1～40 字，可重名）；
/// <see cref="BaseUrl"/> 可以暂时是空字符串（刚添加、还没填 = 未配置），非空时必须是 http / https 地址，不带结尾斜杠。</para>
/// </summary>
/// <param name="Model">日常对话用的模型 Id；null = 没填（内置服务商用预设的模型）。</param>
/// <param name="TranslationModel">翻译用的模型 Id；null = 和 <see cref="Model"/> 相同（内置服务商先看预设的翻译模型）。</param>
public sealed record AiEndpoint(string Id, string Name, string BaseUrl, AiApiFormat Format, string? Model, string? TranslationModel);

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
    /// <summary>
    /// 当前用的第三方 API 服务商（065）：<see cref="AiPreset.Id"/> 或自定义服务商的 Id。
    /// null = 0.1.388 及以前的设置文件，还没迁移（读设置时由 <see cref="ApiBaseUrl"/> / <see cref="ApiModel"/> / <see cref="TranslationModel"/> 和旧 key 迁过来）。
    /// 指向不存在的服务商时按 <see cref="CurrentEndpoint"/> 的规则退回。
    /// </summary>
    public string? ApiEndpoint { get; init; }
    /// <summary>服务商列表（065）：所有自定义服务商（按添加顺序，就是界面上的顺序），以及改过模型的内置服务商。null 当作空。最多 20 个自定义服务商。</summary>
    public AiEndpoint[]? Endpoints { get; init; }
    /// <summary>旧字段（0.1.388 及以前）：只用于迁移，迁移后写 null。新代码用 <see cref="CurrentEndpoint"/>。</summary>
    public string? ApiBaseUrl { get; init; }
    /// <summary>旧字段（0.1.388 及以前）：只用于迁移，迁移后写 null。新代码用 <see cref="CurrentEndpoint"/>。</summary>
    public string? ApiModel { get; init; }
    public string? ChatGptModel { get; init; }
    public string TranslateTarget { get; init; }="auto";
    /// <summary>
    /// ChatGPT 账号方式的翻译模型 Id（设计加，2026-10-29）：单独指定一个快模型；null = 和主模型相同。
    /// 065 起第三方 API 的翻译模型存在各自的 <see cref="AiEndpoint.TranslationModel"/>，这个字段只给 ChatGPT 用（迁移时 API 方式的值复制进服务商）。
    /// </summary>
    public string? TranslationModel { get; init; }

    /// <summary>内置服务商在 <see cref="Endpoints"/> 里的记录（没有就用预设的模型），名字、地址、格式取预设。</summary>
    public AiEndpoint PresetEndpoint(AiPreset preset)
    {
        var saved = Endpoints?.FirstOrDefault(e => e.Id == preset.Id);
        return new(preset.Id, preset.Name, preset.BaseUrl, preset.Format, saved?.Model ?? preset.Model, saved is null ? preset.TranslationModel : saved.TranslationModel);
    }
    /// <summary>界面上的服务商顺序：内置的在前（<see cref="AiPresets.All"/> 的顺序），自定义的按添加顺序。</summary>
    [JsonIgnore] public IReadOnlyList<AiEndpoint> AllEndpoints =>
        [.. AiPresets.All.Select(PresetEndpoint), .. (Endpoints ?? []).Where(e => AiPresets.Find(e.Id) is null)];
    /// <summary>当前服务商：<see cref="ApiEndpoint"/> 找得到就用它，找不到（被删了、还没迁移）就退回第一个内置服务商。服务和界面都读这个。</summary>
    [JsonIgnore] public AiEndpoint CurrentEndpoint =>
        AllEndpoints.FirstOrDefault(e => e.Id == ApiEndpoint) ?? PresetEndpoint(AiPresets.All[0]);
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
        new("translate","翻译","translate","把下面的文字翻译成{lang}。只输出译文，不要解释；保留原文的换行和格式。\n\n{text}"), // 不翻译：用户数据
        new("summary","总结","note","简要总结下面的内容：先用一句话说结论，必要时再列不超过 3 个要点。用中文回答，只输出总结。\n\n{text}"), // 不翻译：用户数据
        new("polish","润色","brush","润色下面的文字：改正错别字和语病，让表达更通顺自然；保持原意、原来的语言和大致长度。只输出润色后的文字。\n\n{text}"), // 不翻译：用户数据
        new("explain","解释","ai","用通俗易懂的中文解释下面的内容。如果是代码，说明它做什么；如果是术语，给出含义和一个例子。\n\n{text}")]; // 不翻译：用户数据
    internal static bool ValidUrl(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme is "http" or "https"&&!string.IsNullOrEmpty(u.Host)&&string.IsNullOrEmpty(u.UserInfo)&&string.IsNullOrEmpty(u.Fragment);
    internal static bool ValidProxy(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme is "http" or "https" or "socks5" or "socks5h"&&!string.IsNullOrEmpty(u.Host)&&string.IsNullOrEmpty(u.Fragment);
    internal AiSettings Migrate()
    {
        if(ApiEndpoint!=null)return this;
        ValidateEndpoints();
        var entries=(Endpoints??[]).ToList();
        string id=AiPresets.All[0].Id;
        if(ApiBaseUrl!=null){
            if(AiPresets.Match(ApiBaseUrl) is {} preset){
                id=preset.Id;
                if(ApiModel!=preset.Model||TranslationModel!=preset.TranslationModel){
                    entries.RemoveAll(e=>e.Id==id);
                    entries.Add(new(id,preset.Name,preset.BaseUrl,preset.Format,ApiModel,TranslationModel));
                }
            }else{
                do{id="custom-"+Guid.NewGuid().ToString("N")[..8];}while(entries.Any(e=>e.Id==id));
                entries.Add(new(id,Strings.Settings_Ai_CustomVendor,ApiBaseUrl.TrimEnd('/'),AiApiFormat.OpenAi,ApiModel,TranslationModel));
            }
        }
        return this with{ApiEndpoint=id,Endpoints=entries.ToArray(),ApiBaseUrl=null,ApiModel=null,TranslationModel=Provider==AiProvider.ChatGpt?TranslationModel:null};
    }
    internal static bool CustomId(string? id)=>id is {Length:15}&&id.StartsWith("custom-",StringComparison.Ordinal)&&id.AsSpan(7).IndexOfAnyExcept("0123456789abcdef")<0;
    internal void ValidateEndpoints()
    {
        var ids=new HashSet<string>(StringComparer.Ordinal);int custom=0;
        foreach(var e in Endpoints??[]){
            if(e==null||string.IsNullOrEmpty(e.Id)||!ids.Add(e.Id)||e.Model!=null&&string.IsNullOrWhiteSpace(e.Model)||e.TranslationModel!=null&&string.IsNullOrWhiteSpace(e.TranslationModel))
                throw new ArgumentException(Strings.Settings_Ai_Invalid);
            if(AiPresets.Find(e.Id)!=null)continue;
            if(!CustomId(e.Id)||++custom>20||e.Name==null||e.Name.Trim().Length is <1 or >40||e.BaseUrl!=""&&!ValidUrl(e.BaseUrl)||!Enum.IsDefined(e.Format))
                throw new ArgumentException(Strings.Settings_Ai_Invalid);
        }
    }
    internal void Validate()
    {
        ValidateEndpoints();
        if(TranslationModel!=null&&string.IsNullOrWhiteSpace(TranslationModel)||!Enum.IsDefined(Provider)||(ProxyUrl!=null&&!ValidProxy(ProxyUrl))||!Enum.IsDefined(Proxy)||Proxy==AiProxyMode.Custom&&ProxyUrl==null||!Enum.IsDefined(Reasoning)||TranslateTarget is not ("auto" or "zh" or "en" or "ja" or "ko" or "fr" or "de" or "es" or "ru")||Actions==null||Actions.Any(a=>a==null||string.IsNullOrWhiteSpace(a.Id)||string.IsNullOrWhiteSpace(a.Name)||string.IsNullOrWhiteSpace(a.PromptTemplate))||Actions.Select(a=>a.Id).Distinct(StringComparer.Ordinal).Count()!=Actions.Length)
            throw new ArgumentException(Strings.Settings_Ai_Invalid);
    }
}
