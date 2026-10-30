using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Ai;

public sealed partial class AiService : IAiService,IAiSetup,IDisposable
{
    readonly SettingsStore store;
    readonly SecretStore secrets;
    readonly AiEndpoints endpoints;
    readonly object gate=new();
    readonly Dictionary<string,HttpClient> clients=[];
    readonly CancellationTokenSource lifetime=new();
    CancellationTokenSource session=new();
    string? key;
    LoginRecord login=new();
    IReadOnlyList<AiModel> models=[];
    string? modelsSource;
    internal TimeSpan FirstTimeout {get;set;}=TimeSpan.FromSeconds(30);
    internal TimeSpan TotalTimeout {get;set;}=TimeSpan.FromSeconds(120);
    public AiService(SettingsStore store):this(store,new()){}
    internal AiService(SettingsStore store,AiEndpoints endpoints)
    {
        this.store=store;this.endpoints=endpoints;secrets=new(store.DirectoryPath);
        try{key=secrets.Read("ai-key.bin");}catch(AiException){}
        try{var saved=secrets.Read("ai-account.bin");if(saved!=null)login=JsonSerializer.Deserialize(saved,AiJson.Default.LoginRecord)??new();}catch(Exception e) when(e is AiException or JsonException){}
        store.Changed+=SettingsChanged;
    }
    public event EventHandler? StatusChanged;
    public event EventHandler? Changed;
    void Notify(){StatusChanged?.Invoke(this,EventArgs.Empty);Changed?.Invoke(this,EventArgs.Empty);}
    void SettingsChanged(object? sender,EventArgs e){lock(gate){models=[];modelsSource=null;}Notify();}
    public AiStatus Status=>store.Current.Ai.Provider==AiProvider.Api?(AiSettings.ValidUrl(store.Current.Ai.ApiBaseUrl)&&!string.IsNullOrWhiteSpace(key)&&!string.IsNullOrWhiteSpace(store.Current.Ai.ApiModel)?AiStatus.Ready:AiStatus.NotConfigured):Account!=null?AiStatus.Ready:AiStatus.NeedsSignIn;
    public string? CurrentModelId {get{var s=store.Current.Ai;return s.Provider==AiProvider.Api?s.ApiModel:s.ChatGptModel??models.FirstOrDefault()?.Id;}}
    string? ModelName(string? id)=>models.FirstOrDefault(m=>m.Id==id)?.DisplayName??id;
    public string? CurrentModelName=>ModelName(CurrentModelId);
    public string? TranslationModelName=>ModelName(store.Current.Ai.TranslationModel??CurrentModelId);
    public IReadOnlyList<AiAction> Actions=>store.Current.Ai.Actions.Select(a=>new AiAction(a.Id,a.Name,a.Glyph,a.PromptTemplate)).ToArray();
    public string? ApiKeyMask {get{var value=key;return value==null?null:value.Length<=8?"••••":value[..4]+"…"+value[^4..];}}
    public void SaveApiKey(string value){value=value.Trim();if(value.Any(char.IsControl))throw new AiCredentialException("API Key 格式无效");lock(gate){secrets.Write("ai-key.bin",value.Length==0?null:value);key=value.Length==0?null:value;}Notify();}
    HttpClient Client(AiSettings settings)
    {
        lock(gate){ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested,this);string proxy=settings.ProxyUrl??"";
            if(!clients.TryGetValue(proxy,out var client)){
                var handler=new SocketsHttpHandler{AllowAutoRedirect=false,PooledConnectionIdleTimeout=TimeSpan.FromSeconds(30)};
                if(settings.ProxyUrl!=null)handler.Proxy=new WebProxy(settings.ProxyUrl);
                clients[proxy]=client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
            }return client;
        }
    }
    internal async Task<HttpResponseMessage> Send(HttpRequestMessage request,AiSettings settings,CancellationToken ct)
    {
        try{return await Client(settings).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);}
        catch(OperationCanceledException){throw;}
        catch(Exception e) when(e is HttpRequestException or IOException){throw new AiNetworkException("无法连接 AI 服务，请检查网络和代理");}
    }
    internal static async Task<JsonElement> Json(HttpResponseMessage response,CancellationToken ct)
    {
        try{using var stream=await response.Content.ReadAsStreamAsync(ct);using var doc=await JsonDocument.ParseAsync(stream,cancellationToken:ct);return doc.RootElement.Clone();}
        catch(JsonException){return default;}
        catch(IOException){throw new AiNetworkException("AI 连接中断，请重试");}
    }
    async Task<HttpResponseMessage> Authorized(AiSettings settings,string path,byte[]? body,CancellationToken ct)
    {
        bool chat=settings.Provider==AiProvider.ChatGpt;
        for(int attempt=0;;attempt++){
            var bearer=chat?(await Access(settings,ct,attempt>0)).AccessToken:key;
            if(string.IsNullOrEmpty(bearer))throw new AiCredentialException("还没配置 AI 服务");
            using var request=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,(chat?endpoints.Api:settings.ApiBaseUrl).TrimEnd('/')+path);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
            request.Headers.Accept.ParseAdd(body==null?"application/json":"text/event-stream");
            if(body!=null){request.Content=new ByteArrayContent(body);request.Content.Headers.ContentType=new("application/json");}
            var response=await Send(request,settings,ct);
            if(response.IsSuccessStatusCode)return response;
            using(response){if(chat&&response.StatusCode==HttpStatusCode.Unauthorized&&attempt==0)continue;
                var bodyError=await Json(response,ct);
                if(chat&&bodyError.ValueKind==JsonValueKind.Object&&bodyError.TryGetProperty("error",out var detail)&&AiProtocol.String(detail,"code")=="subscription_sharing_user_not_eligible"){
                    lock(gate)SaveLogin(login with{PlanDenied=true});Notify();
                }
                var error=AiProtocol.Error((int)response.StatusCode,bodyError,chat);
                if(error is AiSignInRequiredException)ClearSession();
                throw error;
            }
        }
    }
    public IAsyncEnumerable<string> RunAsync(AiRequest request,CancellationToken cancellationToken)
    {
        var settings=store.Current.Ai;
        return Stream(settings,[new(AiChatRole.User,AiProtocol.Prompt(settings,request))],request.ActionId=="translate"?settings.TranslationModel:null,cancellationToken);
    }
    public IAsyncEnumerable<string> ChatAsync(IReadOnlyList<AiChatMessage> messages,string? modelId,CancellationToken cancellationToken)
        =>Stream(store.Current.Ai,messages.ToArray(),modelId,cancellationToken);
    async IAsyncEnumerable<string> Stream(AiSettings settings,IReadOnlyList<AiChatMessage> messages,string? modelId,[EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CancellationToken sessionToken;lock(gate)sessionToken=settings.Provider==AiProvider.ChatGpt?session.Token:default;
        using var total=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token,sessionToken);total.CancelAfter(TotalTimeout);
        using var first=CancellationTokenSource.CreateLinkedTokenSource(total.Token);first.CancelAfter(FirstTimeout);
        await using var iterator=RunCore(settings,messages,modelId,first,first.Token).GetAsyncEnumerator(first.Token);
        while(true){
            bool next;
            try{next=await iterator.MoveNextAsync();}
            catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested&&!lifetime.IsCancellationRequested&&!sessionToken.IsCancellationRequested){throw new AiNetworkException("AI 服务没有响应，稍后重试");}
            catch(IOException){throw new AiNetworkException("AI 连接中断，请重试");}
            catch(HttpRequestException){throw new AiNetworkException("AI 连接中断，请重试");}
            if(!next)yield break;yield return iterator.Current;
        }
    }
    async IAsyncEnumerable<string> RunCore(AiSettings settings,IReadOnlyList<AiChatMessage> messages,string? modelId,CancellationTokenSource first,[EnumeratorCancellation] CancellationToken ct)
    {
        bool chat=settings.Provider==AiProvider.ChatGpt;
        if(chat){await Access(settings,ct);if(Account?.CanUsePlan!=true)throw new AiQuotaException("当前账号无法使用套餐额度，请检查账号套餐");}
        string? model=modelId??(chat?settings.ChatGptModel:settings.ApiModel);
        if(chat&&model==null){lock(gate){if(modelsSource==ModelSource(settings))model=models.FirstOrDefault()?.Id;}model??=(await Models(settings,ct)).FirstOrDefault()?.Id;}
        if(string.IsNullOrWhiteSpace(model))throw new AiCredentialException("请先在设置里选择模型");
        using var response=await Authorized(settings,chat?"/responses":"/chat/completions",AiProtocol.Body(model,messages,chat),ct);
        using var stream=await response.Content.ReadAsStreamAsync(ct);using var reader=new StreamReader(stream,Encoding.UTF8);
        var frame=new StringBuilder();bool firstText=true;
        while(true){
            var line=await reader.ReadLineAsync(ct);
            if(line==null)throw new AiNetworkException("AI 响应流在完成前断开，请重试");
            if(line.Length==0){if(frame.Length==0)continue;var result=AiProtocol.Frame(frame.ToString().TrimEnd('\n'),chat);frame.Clear();
                if(result.Done)yield break;
                if(!string.IsNullOrEmpty(result.Text)){if(firstText){first.CancelAfter(Timeout.InfiniteTimeSpan);firstText=false;}yield return result.Text;}
            }else if(line.StartsWith("data:",StringComparison.Ordinal)){frame.Append(line.AsSpan(5).TrimStart());frame.Append('\n');if(frame.Length>4*1024*1024)throw new AiNetworkException("AI 响应过大，请缩短请求");}
        }
    }
    string ModelSource(AiSettings settings)=>settings.Provider+":"+(settings.Provider==AiProvider.Api?settings.ApiBaseUrl:login.ClientId);
    async Task<IReadOnlyList<AiModel>> Models(AiSettings settings,CancellationToken ct)
    {
        string source=ModelSource(settings);
        using var response=await Authorized(settings,"/models",null,ct);var root=await Json(response,ct);var result=new List<AiModel>();
        if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Array)
            foreach(var item in data.EnumerateArray()){var id=AiProtocol.String(item,"id");if(!string.IsNullOrEmpty(id))result.Add(new(id,AiProtocol.String(item,"display_name")??id));}
        else if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("models",out data)&&data.ValueKind==JsonValueKind.Array)
            foreach(var item in data.EnumerateArray()){var id=AiProtocol.String(item,"slug");if(AiProtocol.String(item,"visibility")=="list"&&!string.IsNullOrEmpty(id))result.Add(new(id,AiProtocol.String(item,"display_name")??id));}
        if(result.Count==0)throw new AiCredentialException("没有可用模型，请检查接口地址或账号权限");
        lock(gate){models=result;modelsSource=source;}return result;
    }
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings=store.Current.Ai;CancellationToken sessionToken;lock(gate)sessionToken=settings.Provider==AiProvider.ChatGpt?session.Token:default;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token,sessionToken);timeout.CancelAfter(FirstTimeout);
        try{return await Models(settings,timeout.Token);}catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested&&!lifetime.IsCancellationRequested&&!sessionToken.IsCancellationRequested){throw new AiNetworkException("AI 服务没有响应，稍后重试");}
    }
    public async Task TestAsync(CancellationToken cancellationToken){await foreach(var _ in RunAsync(new("ask","",null,"只回答一个字：好"),cancellationToken)){} }
    public void Dispose(){store.Changed-=SettingsChanged;lifetime.Cancel();lock(gate){pendingLogin?.Cancel();foreach(var client in clients.Values)client.Dispose();clients.Clear();}}
}

internal sealed record AiEndpoints
{
    internal string Issuer {get;init;}="https://auth.openai.com";
    internal string Api {get;init;}="https://api.openai.com/v1";
    internal Action<string> OpenBrowser {get;init;}=url=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});
}
