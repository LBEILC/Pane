using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// 只绑定回环地址；不记录请求头、令牌或请求正文。
internal sealed class FakeServer:IAsyncDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);
    readonly CancellationTokenSource stop=new();
    readonly List<Task> workers=[];
    readonly Task loop;
    readonly RSA rsa=RSA.Create(2048);
    public string Url {get;}
    public string Mode="normal",Nonce="";
    public string? LastBody,LastPath;
    public int Refreshes,Revocations;
    public bool RefreshFails;
    public bool ModelsAsChat;
    public readonly List<(string Path,string Body)> Requests=[];
    public string[] LastHeaders=[];
    public TaskCompletionSource Disconnected=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Connected=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? CallbackTask;
    public FakeServer(){listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;loop=Loop();}
    async Task Loop(){try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);var worker=Serve(client);lock(workers)workers.Add(worker);}}catch(OperationCanceledException){}catch(SocketException) when(stop.IsCancellationRequested){} }
    static string B64(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
    string Identity(){var header=B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"test\"}"));var claims=new JsonObject{["iss"]=Url,["aud"]="pane-fixture-client",["sub"]="fixture-subject",["nonce"]=Nonce,["exp"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300,["email"]="fixture@example.invalid",["https://api.openai.com/auth"]=new JsonObject{["chatgpt_plan_type"]=Mode=="free"?"free":"plus"}};string body=header+"."+B64(Encoding.UTF8.GetBytes(claims.ToJsonString()));return body+"."+B64(rsa.SignData(Encoding.ASCII.GetBytes(body),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));}
    public void Browser(string url){var q=Query(new Uri(url).Query);Nonce=q["nonce"];CallbackTask=Callback(q);}
    static Dictionary<string,string> Query(string value)=>value.TrimStart('?').Split('&').Select(s=>s.Split('=',2)).Where(p=>p.Length==2).ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p[1]));
    async Task Callback(Dictionary<string,string> q){using var http=new HttpClient();using var r=await http.GetAsync(q["redirect_uri"]+"?state="+q["state"]+"&code=fixture-code&client_id=pane-fixture-client",stop.Token);await r.Content.ReadAsStringAsync(stop.Token);}
    async Task Serve(TcpClient client)
    {
        using(client)try{
            var stream=client.GetStream();var bytes=new List<byte>();var one=new byte[1];
            while(await stream.ReadAsync(one,stop.Token)>0){bytes.Add(one[0]);if(bytes.Count>=4&&bytes.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10}))break;if(bytes.Count>65536)return;}
            var headers=Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");var path=headers[0].Split(' ')[1];int length=0;
            foreach(var h in headers)if(h.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(h.Split(':')[1]);
            var body=new byte[length];await stream.ReadExactlyAsync(body,stop.Token);LastBody=Encoding.UTF8.GetString(body);LastPath=path;
            LastHeaders=headers;Requests.Add((path,LastBody));
            if(Mode=="probe-timeout"){await Task.Delay(Timeout.Infinite,stop.Token);return;}
            if(Mode.StartsWith("reasoning")&&path is "/responses" or "/chat/completions"){
                bool has=LastBody.Contains("\"reasoning");
                if(has||Mode=="reasoning-always"){
                    await Reply(stream,400,Mode=="reasoning-message"?"{\"error\":{\"message\":\"This model does not support reasoning_effort\"}}":"{\"error\":{\"param\":\"reasoning_effort\"}}");return;
                }
            }
            if(path=="/.well-known/openid-configuration"){await Reply(stream,200,new JsonObject{["issuer"]=Url,["jwks_uri"]=Url+"/keys",["revocation_endpoint"]=Url+"/revoke"}.ToJsonString());return;}
            if(path=="/keys"){var p=rsa.ExportParameters(false);await Reply(stream,200,new JsonObject{["keys"]=new JsonArray(new JsonObject{["kty"]="RSA",["kid"]="test",["n"]=B64(p.Modulus!),["e"]=B64(p.Exponent!)})}.ToJsonString());return;}
            if(path=="/revoke"){Revocations++;await Reply(stream,200,"{}");return;}
            if(path=="/api/accounts/oauth/token"){
                bool refresh=LastBody.Contains("grant_type=refresh_token");if(refresh)Refreshes++;
                if(refresh&&RefreshFails){await Reply(stream,400,"{\"error\":\"invalid_grant\"}");return;}
                await Reply(stream,200,new JsonObject{["access_token"]="fixture-access-015-private",["refresh_token"]="fixture-refresh-015-private",["expires_in"]=refresh?3600:1,["scope"]="openid chatgpt.tokens.use.direct",["id_token"]=Identity()}.ToJsonString());return;
            }
            if(int.TryParse(Mode,out var status)){await Reply(stream,status,"{\"error\":{\"code\":\"fixture\"}}");return;}
            if(Mode=="quota"){await Reply(stream,429,"{\"error\":{\"code\":\"insufficient_quota\"}}");return;}
            if(Mode=="model-error"){await Reply(stream,404,"{\"error\":{\"code\":\"model_not_found\"}}");return;}
            if(path.EndsWith("/models")){await Reply(stream,200,ModelsAsChat?"{\"models\":[{\"slug\":\"fixture-model\",\"display_name\":\"Fixture Model\",\"visibility\":\"list\"},{\"slug\":\"hidden\",\"visibility\":\"hide\"}]}":"{\"data\":[{\"id\":\"fixture-model\",\"display_name\":\"Fixture Model\"}]}");return;}
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"),stop.Token);
            string first=path.EndsWith("/responses")?"data: {\"type\":\"response.output_text.delta\",\"delta\":\"你\"}\n\n":"data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n";
            if(Mode!="stall")foreach(byte b in Encoding.UTF8.GetBytes("\n: ping\ninvalid-line\n"+first))await stream.WriteAsync(new byte[]{b},stop.Token);
            Connected.TrySetResult();
            if(Mode is "cancel" or "stall"){
                try{int read=await stream.ReadAsync(one,stop.Token);if(read==0)Disconnected.TrySetResult();}catch(IOException){Disconnected.TrySetResult();}return;
            }
            if(Mode=="malformed"){await stream.WriteAsync(Encoding.UTF8.GetBytes("data: {broken}\n\n"),stop.Token);return;}
            string rest=path.EndsWith("/responses")?"data: {\"type\":\"response.output_text.delta\",\"delta\":\"好\"}\n\ndata: {\"type\":\"response.completed\"}\n\n":"data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\ndata: [DONE]\n\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(rest),stop.Token);
        }catch(Exception e) when(e is IOException or OperationCanceledException or SocketException){}
    }
    async Task Reply(NetworkStream stream,int status,string body){var bytes=Encoding.UTF8.GetBytes(body);await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"),stop.Token);await stream.WriteAsync(bytes,stop.Token);}
    public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();await loop;Task[] pending;lock(workers)pending=workers.ToArray();await Task.WhenAll(pending);rsa.Dispose();stop.Dispose();}
}
