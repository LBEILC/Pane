using Pane.Localization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Ai;

internal sealed record LoginRecord
{
    public string HostId {get;init;}="urn:uuid:"+Guid.NewGuid();
    public string? ClientId {get;init;}
    public string? Subject {get;init;}
    public string? Email {get;init;}
    public string? Plan {get;init;}
    public bool PlanDenied {get;init;}
    public string? Scope {get;init;}
    public string? AccessToken {get;init;}
    public string? RefreshToken {get;init;}
    public string? IdToken {get;init;}
    public DateTimeOffset Expires {get;init;}
    public DateTimeOffset EarliestRefresh {get;init;}
}
[JsonSerializable(typeof(LoginRecord))]
internal partial class AiJson:JsonSerializerContext { }

public sealed partial class AiService
{
    const string Resource="https://api.openai.com/v1";
    const string Scopes="openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    readonly SemaphoreSlim refreshGate=new(1,1);
    CancellationTokenSource? pendingLogin;
    int epoch;
    internal TimeSpan LoginTimeout {get;set;}=TimeSpan.FromMinutes(5);
    public AiAccount? Account {get{var r=login;return r.AccessToken==null?null:new(r.Email??"",r.Plan,!r.PlanDenied&&!string.Equals(r.Plan,"free",StringComparison.OrdinalIgnoreCase)&&HasPermission(r.Scope));}}
    static bool HasPermission(string? scope)=>scope?.Split(' ').Contains("chatgpt.tokens.use.direct")==true;
    void SaveLogin(LoginRecord value){secrets.Write("ai-account.bin",JsonSerializer.Serialize(value,AiJson.Default.LoginRecord));login=value;}
    void RenewSession(){var previous=session;session=new();previous.Cancel();previous.Dispose();}
    void ClearSession(){lock(gate){epoch++;SaveLogin(login with{AccessToken=null,RefreshToken=null,IdToken=null,Expires=default});RenewSession();models=[];}Notify();}
    static string B64(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    static byte[] Un64(string value)=>Convert.FromBase64String(value.Replace('-','+').Replace('_','/').PadRight((value.Length+3)/4*4,'='));
    async Task<JsonElement> FetchJson(string url,AiSettings settings,CancellationToken ct,Dictionary<string,string>? form=null)
    {
        using var request=new HttpRequestMessage(form==null?HttpMethod.Get:HttpMethod.Post,url);
        if(form!=null)request.Content=new FormUrlEncodedContent(form);
        using var response=await Send(request,settings,ct);var body=await Json(response,ct);
        if(!response.IsSuccessStatusCode)throw AiProtocol.Error((int)response.StatusCode,body,true);
        if(body.ValueKind!=JsonValueKind.Object)throw new AiNetworkException(Strings.Ai_InvalidChatResponse);return body;
    }
    async Task<JsonElement> Discovery(AiSettings settings,CancellationToken ct)
    {
        var data=await FetchJson(endpoints.Issuer+"/.well-known/openid-configuration",settings,ct);
        if(AiProtocol.String(data,"issuer")!=endpoints.Issuer)throw new AiSignInRequiredException(Strings.Ai_IssuerMismatch);
        foreach(var name in new[]{"jwks_uri","revocation_endpoint"}){
            if(!Uri.TryCreate(AiProtocol.String(data,name),UriKind.Absolute,out var u)||u.GetLeftPart(UriPartial.Authority)!=new Uri(endpoints.Issuer).GetLeftPart(UriPartial.Authority))throw new AiSignInRequiredException(Strings.Ai_AuthorizationAddressInvalid);
        }return data;
    }
    static LoginRecord Tokens(JsonElement body,LoginRecord old)
    {
        string? access=AiProtocol.String(body,"access_token"),refresh=AiProtocol.String(body,"refresh_token");
        if(string.IsNullOrEmpty(access)||access.Any(char.IsControl)||string.IsNullOrEmpty(refresh)||!body.TryGetProperty("expires_in",out var expiry)||expiry.ValueKind!=JsonValueKind.Number||!expiry.TryGetDouble(out var seconds)||!double.IsFinite(seconds)||seconds<=0||seconds>31536000)throw new AiSignInRequiredException(Strings.Ai_TokenIncomplete);
        DateTimeOffset earliest=default;
        if(body.TryGetProperty("earliest_refresh_at",out var at)){
            if(at.ValueKind==JsonValueKind.Number&&at.TryGetInt64(out var unix)&&unix is >0 and <253402300799)earliest=DateTimeOffset.FromUnixTimeSeconds(unix);
            else if(at.ValueKind==JsonValueKind.String)DateTimeOffset.TryParse(at.GetString(),out earliest);
        }
        return old with{AccessToken=access,RefreshToken=refresh,IdToken=AiProtocol.String(body,"id_token")??old.IdToken,Expires=DateTimeOffset.UtcNow.AddSeconds(seconds),Scope=AiProtocol.String(body,"scope")??old.Scope,EarliestRefresh=earliest};
    }
    async Task<LoginRecord> Access(AiSettings settings,CancellationToken ct,bool force=false)
    {
        var observed=login;
        if(observed.AccessToken==null)throw new AiSignInRequiredException(Strings.Ai_LoginRequired);
        if(!force&&observed.Expires>DateTimeOffset.UtcNow.AddSeconds(30))return observed;
        await refreshGate.WaitAsync(ct);
        try {
            var old=login;int version=epoch;
            if(old.AccessToken==null)throw new AiSignInRequiredException(Strings.Ai_LoginAgain);
            if(old!=observed||(!force&&old.Expires>DateTimeOffset.UtcNow.AddSeconds(30)))return old;
            if(old.EarliestRefresh>DateTimeOffset.UtcNow){if(!force&&old.Expires>DateTimeOffset.UtcNow)return old;throw new AiNetworkException(Strings.Ai_RefreshTooEarly);}
            try {
                var body=await FetchJson(endpoints.Issuer+"/api/accounts/oauth/token",settings,ct,new(){["grant_type"]="refresh_token",["client_id"]=old.ClientId!,["refresh_token"]=old.RefreshToken!,["resource"]=Resource});
                var next=Tokens(body,old);
                lock(gate){ct.ThrowIfCancellationRequested();if(version!=epoch)throw new OperationCanceledException(ct);SaveLogin(next);}Notify();return next;
            }catch(AiSignInRequiredException){if(version==epoch)ClearSession();throw;}
            catch(AiException) when(old.Expires<=DateTimeOffset.UtcNow){if(version==epoch)ClearSession();throw new AiSignInRequiredException(Strings.Ai_RefreshFailed);}
        }finally{refreshGate.Release();}
    }
    internal static JsonElement ValidateIdentity(string token,JsonElement jwks,string issuer,string audience,string nonce)
    {
        try {
            var parts=token.Split('.');if(parts.Length!=3)throw new FormatException();
            using var header=JsonDocument.Parse(Un64(parts[0]));using var claims=JsonDocument.Parse(Un64(parts[1]));var h=header.RootElement;var c=claims.RootElement;
            if(AiProtocol.String(h,"alg")!="RS256")throw new FormatException();
            var keys=jwks.GetProperty("keys").EnumerateArray().Where(k=>AiProtocol.String(k,"kid")==AiProtocol.String(h,"kid")&&AiProtocol.String(k,"kty")=="RSA"&&AiProtocol.String(k,"use") is null or "sig"&&AiProtocol.String(k,"alg") is null or "RS256").ToArray();
            if(keys.Length!=1)throw new FormatException();using var rsa=RSA.Create();rsa.ImportParameters(new(){Modulus=Un64(keys[0].GetProperty("n").GetString()!),Exponent=Un64(keys[0].GetProperty("e").GetString()!)});
            if(!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),Un64(parts[2]),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))throw new FormatException();
            var aud=c.GetProperty("aud");var audiences=aud.ValueKind==JsonValueKind.Array?aud.EnumerateArray().Select(v=>v.GetString()).ToArray():[aud.GetString()];var azp=AiProtocol.String(c,"azp");
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(AiProtocol.String(c,"iss")!=issuer||!audiences.Contains(audience)||(audiences.Length>1&&azp!=audience)||(azp!=null&&azp!=audience)||c.GetProperty("exp").GetDouble()<=now||(c.TryGetProperty("nbf",out var nbf)&&nbf.GetDouble()>now)||AiProtocol.String(c,"nonce")!=nonce||string.IsNullOrEmpty(AiProtocol.String(c,"sub")))throw new FormatException();
            return c.Clone();
        }catch(Exception e) when(e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or CryptographicException or ArgumentException){throw new AiSignInRequiredException(Strings.Ai_IdentityTokenInvalid);}
    }
    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);timeout.CancelAfter(LoginTimeout);
        int version;LoginRecord old;
        lock(gate){if(pendingLogin!=null)throw new AiSignInRequiredException(Strings.Ai_LoginPending);pendingLogin=timeout;version=epoch;old=login;SaveLogin(old);}
        var settings=store.Current.Ai;var listener=new TcpListener(IPAddress.Loopback,0);
        try {
            var ct=timeout.Token;var metadata=await Discovery(settings,ct);
            string verifier=B64(RandomNumberGenerator.GetBytes(32)),state=B64(RandomNumberGenerator.GetBytes(32)),nonce=B64(RandomNumberGenerator.GetBytes(32));
            listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;string redirect=$"http://127.0.0.1:{port}/auth/callback";
            var query=new Dictionary<string,string>{["client_id"]=old.ClientId??"dynamic_agent_client",["redirect_uri"]=redirect,["response_type"]="code",["scope"]=Scopes,["resource"]=Resource,["state"]=state,["nonce"]=nonce,["code_challenge"]=B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),["code_challenge_method"]="S256",["ext_agent_host_id"]=old.HostId};
            if(old.ClientId==null)query["agent_name_hint"]="Pane";
            if(old.Email!=null)query["login_hint"]=old.Email;
            if(old.IdToken!=null)query["id_token_hint"]=old.IdToken;
            string url=endpoints.Issuer+"/api/accounts/authorize?"+string.Join("&",query.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
            ct.ThrowIfCancellationRequested();try{endpoints.OpenBrowser(url);}catch{throw new AiNetworkException(Strings.Ai_BrowserFailed);}
            while(true){
                using var client=await listener.AcceptTcpClientAsync(ct);using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.UTF8,leaveOpen:true);
                // 每个无效连接最多占用两秒，登录总计仍受五分钟限制。
                using var readDeadline=CancellationTokenSource.CreateLinkedTokenSource(ct);readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                string? line;var headers=new List<string>();
                try{while((line=await reader.ReadLineAsync(readDeadline.Token)) is not null&&line.Length>0){headers.Add(line);if(headers.Count>64||line.Length>16384)break;}}catch(OperationCanceledException) when(!ct.IsCancellationRequested){continue;}
                var requestLine=headers.FirstOrDefault()?.Split(' ');
                if(requestLine?.Length!=3||requestLine[0]!="GET"||!headers.Any(h=>h.Equals($"Host: 127.0.0.1:{port}",StringComparison.OrdinalIgnoreCase))||!Uri.TryCreate("http://127.0.0.1:"+port+requestLine[1],UriKind.Absolute,out var callback)||callback.AbsolutePath!="/auth/callback"){await Reply(stream,Strings.Ai_CallbackInvalid,ct);continue;}
                var values=new Dictionary<string,string>();foreach(var pair in callback.Query.TrimStart('?').Split('&')){var p=pair.Split('=',2);if(p.Length==2)values[Uri.UnescapeDataString(p[0])]=Uri.UnescapeDataString(p[1].Replace('+',' '));}
                if(!values.TryGetValue("state",out var returnedState)||returnedState!=state){await Reply(stream,Strings.Ai_LoginStateInvalid,ct);continue;}
                try {
                    if(values.ContainsKey("error")||!values.TryGetValue("code",out var code))throw new AiSignInRequiredException(Strings.Ai_LoginIncomplete);
                    string? issued=values.GetValueOrDefault("client_id")??old.ClientId;
                    if(string.IsNullOrEmpty(issued)||issued=="dynamic_agent_client"||(old.ClientId!=null&&issued!=old.ClientId))throw new AiSignInRequiredException(Strings.Ai_ClientMismatch);
                    // 回调签发的注册标识不是登录凭据；即使换码失败也保留，避免重复注册。
                    lock(gate){ct.ThrowIfCancellationRequested();if(version!=epoch)throw new OperationCanceledException(ct);SaveLogin(old with{ClientId=issued});}
                    var body=await FetchJson(endpoints.Issuer+"/api/accounts/oauth/token",settings,ct,new(){["grant_type"]="authorization_code",["client_id"]=issued,["code"]=code,["code_verifier"]=verifier,["redirect_uri"]=redirect,["resource"]=Resource});
                    var keys=await FetchJson(AiProtocol.String(metadata,"jwks_uri")!,settings,ct);
                    var claims=ValidateIdentity(AiProtocol.String(body,"id_token")??"",keys,endpoints.Issuer,issued,nonce);
                    string? sub=AiProtocol.String(claims,"sub");if(old.Subject!=null&&old.Subject!=sub)throw new AiSignInRequiredException(Strings.Ai_AccountMismatch);
                    string? plan=claims.TryGetProperty("https://api.openai.com/auth",out var auth)?AiProtocol.String(auth,"chatgpt_plan_type"):null;
                    // TODO(design): 官方不保证返回套餐名称；缺失时设置页按未知套餐展示，不臆造 Plus/Pro。
                    // 调用资格以显式授权 scope 为准；明确的 free 声明或服务端拒绝会禁用套餐调用。
                    var next=Tokens(body,old with{ClientId=issued,Subject=sub,Email=AiProtocol.String(claims,"email"),Plan=plan,PlanDenied=false});
                    lock(gate){ct.ThrowIfCancellationRequested();if(version!=epoch)throw new OperationCanceledException(ct);SaveLogin(next);epoch++;RenewSession();models=[];}
                    Notify();await Reply(stream,Strings.Ai_LoginComplete,ct);return;
                }catch(AiException e){await Reply(stream,e.Message,ct);throw;}
            }
        }catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested&&!lifetime.IsCancellationRequested){throw new AiNetworkException(Strings.Ai_LoginTimeout);}
        catch(SocketException){throw new AiNetworkException(Strings.Ai_CallbackListenFailed);}
        catch(IOException){throw new AiNetworkException(Strings.Ai_LoginInterrupted);}
        finally{listener.Stop();lock(gate){if(pendingLogin==timeout)pendingLogin=null;}}
    }
    static async Task Reply(NetworkStream stream,string message,CancellationToken ct)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(message);byte[] head=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {bytes.Length}\r\n\r\n");
        await stream.WriteAsync(head,ct);await stream.WriteAsync(bytes,ct);
    }
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        LoginRecord old;lock(gate){pendingLogin?.Cancel();old=login;}ClearSession();
        if(old.RefreshToken==null)return;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);timeout.CancelAfter(FirstTimeout);
        try{var s=store.Current.Ai;var metadata=await Discovery(s,timeout.Token);using var request=new HttpRequestMessage(HttpMethod.Post,AiProtocol.String(metadata,"revocation_endpoint"));request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{["token"]=old.RefreshToken,["token_type_hint"]="refresh_token",["client_id"]=old.ClientId!});using var response=await Send(request,s,timeout.Token);}
        catch(AiException){}catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){}
    }
}
