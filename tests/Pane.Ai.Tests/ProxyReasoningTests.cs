using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Settings;
using Pane.Services.Ai;

internal static partial class Program
{
    sealed class CountingHandler(SocketsHttpHandler inner):DelegatingHandler(inner)
    {
        internal readonly List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Paths.Add(request.RequestUri!.AbsolutePath);return base.SendAsync(request,ct);}
    }
    static async Task ProxyReasoningTests(string directory)
    {
        var settings=new AiSettings();
        foreach(var mode in Enum.GetValues<AiProxyMode>()){
            using var handler=AiService.CreateHandler(settings with{ProxyMode=mode,ProxyUrl="http://127.0.0.1:1234"});
            Check(handler.UseProxy==(mode!=AiProxyMode.None),"handler UseProxy");
            Check((handler.Proxy!=null)==(mode==AiProxyMode.Custom),"handler explicit proxy");
        }
        foreach(var scheme in new[]{"http","https","socks5","socks5h"}){
            var s=settings with{ProxyMode=AiProxyMode.Custom,ProxyUrl=$"{scheme}://user:p%40ss@127.0.0.1:1234"};s.Validate();
            using var handler=AiService.CreateHandler(s);var proxy=(WebProxy)handler.Proxy!;
            Check(proxy.Address!.Scheme==(scheme=="socks5h"?"socks5":scheme),"proxy protocol");
            Check(proxy.Credentials!.GetCredential(proxy.Address,"Basic") is {UserName:"user",Password:"p@ss"},"proxy credentials");
        }
        foreach(var bad in new[]{settings with{ProxyMode=AiProxyMode.Custom},settings with{ProxyUrl="ftp://host"},settings with{ProxyUrl="relative"},settings with{ProxyUrl="http://host/#fragment"},settings with{ProxyUrl=""}})
            await Error<ArgumentException>(()=>{bad.Validate();return Task.CompletedTask;},"proxy validation");
        var old=JsonSerializer.Deserialize("{\"ProxyUrl\":\"socks5://127.0.0.1:1234\"}",SettingsJson.Default.AiSettings)!;
        Check(old.Proxy==AiProxyMode.Custom&&old.Enabled&&old.Reasoning==AiReasoningEffort.Default,"old settings custom and enabled");
        Check(JsonSerializer.Deserialize("{}",SettingsJson.Default.AiSettings)!.Proxy==AiProxyMode.System,"old settings system");
        Check(!JsonSerializer.Deserialize("{\"Disabled\":true}",SettingsJson.Default.AiSettings)!.Enabled,"disabled persisted");
        foreach(bool chat in new[]{false,true})foreach(var effort in Enum.GetValues<AiReasoningEffort>()){
            using var doc=JsonDocument.Parse(AiProtocol.Body("model",[new(AiChatRole.User,"fixture")],chat,effort));
            bool has=doc.RootElement.TryGetProperty(chat?"reasoning":"reasoning_effort",out var value);
            Check(has==(effort!=AiReasoningEffort.Default),"reasoning present or omitted");
            if(has)Check((chat?value.GetProperty("effort"):value).GetString()==effort.ToString().ToLowerInvariant(),"reasoning value");
        }
        await using var server=new FakeServer();
        var store=new SettingsStore(Path.Combine(directory,"042"),Defaults());
        store.Save(store.Current with{Ai=settings with{ApiBaseUrl=server.Url,ApiModel="fixture-model",ProxyMode=AiProxyMode.None}});
        var handlers=new List<CountingHandler>();
        using var ai=new AiService(store,new(){Api=server.Url,Issuer=server.Url,Probe=server.Url,OpenBrowser=server.Browser,WrapHandler=h=>{var wrapper=new CountingHandler(h);handlers.Add(wrapper);return wrapper;}});
        ai.SaveApiKey("fixture-key-042");
        var initial=ai.Client(store.Current.Ai);
        Check(ReferenceEquals(initial,ai.Client(store.Current.Ai with{ProxyUrl="http://unused:1234"})),"none ignores retained proxy url");
        Check(!ReferenceEquals(initial,ai.Client(store.Current.Ai with{ProxyMode=AiProxyMode.System})),"cache separates proxy modes");
        Check(!ReferenceEquals(initial,ai.Client(store.Current.Ai with{ProxyMode=AiProxyMode.Custom,ProxyUrl=server.Url})),"cache separates custom");
        int clients=handlers.Count;
        foreach(var provider in new[]{AiProvider.Api,AiProvider.ChatGpt}){
            store.Save(store.Current with{Ai=store.Current.Ai with{Provider=provider,ChatGptModel="fixture-model",Reasoning=AiReasoningEffort.High}});
            if(provider==AiProvider.ChatGpt){await ai.SignInAsync(default);await server.CallbackTask!;}
            server.Mode="normal";await ai.ListModelsAsync(default);
            string field=provider==AiProvider.ChatGpt?"reasoning":"reasoning_effort";
            await Run(ai);Check(server.LastBody!.Contains('"'+field+'"'),"main model has effort");
            await foreach(var _ in ai.ChatAsync([new(AiChatRole.User,"fixture")],"chat-model",default)){}
            Check(server.LastBody!.Contains('"'+field+'"'),"chat has effort");
            await foreach(var _ in ai.RunAsync(new("summary","fixture",null,null),default)){}
            Check(server.LastBody!.Contains('"'+field+'"'),"action has effort");
            store.Save(store.Current with{Ai=store.Current.Ai with{TranslationModel="translation-model"}});
            server.Mode="reasoning-message";int start=server.Requests.Count;
            await foreach(var _ in ai.RunAsync(new("translate","fixture",null,null),default)){}
            Check(server.Requests.Count==start+2&&!server.LastBody!.Contains('"'+field+'"'),"translation retries once");
            server.Mode="reasoning";start=server.Requests.Count;await Run(ai);
            Check(server.Requests.Count==start+2&&!server.LastBody!.Contains('"'+field+'"'),"main retries once");
            start=server.Requests.Count;await Run(ai);Check(server.Requests.Count==start+1&&!server.LastBody!.Contains('"'+field+'"'),"unsupported model remembered");
            start=server.Requests.Count;await foreach(var _ in ai.RunAsync(new("translate","fixture",null,null),default)){}
            Check(server.Requests.Count==start+1,"translation remembered separately");
            server.Mode="reasoning-always";start=server.Requests.Count;
            await Error<AiCredentialException>(async()=>{await foreach(var _ in ai.ChatAsync([new(AiChatRole.User,"fixture")],"always-reject",default)){}},"second rejection surfaces");
            Check(server.Requests.Count==start+2,"retry bounded");
            server.Mode="400";start=server.Requests.Count;
            await Error<AiCredentialException>(async()=>{await foreach(var _ in ai.ChatAsync([new(AiChatRole.User,"fixture")],"other-error",default)){}},"other 400 surfaces");
            Check(server.Requests.Count==start+1,"other 400 not retried");
            server.Mode="normal";
            await ai.TestProxyAsync(default);Check(server.LastHeaders[0].StartsWith("HEAD ")&&server.LastBody==""&&!server.LastHeaders.Any(h=>h.StartsWith("Authorization:")||h.StartsWith("Cookie:")),"probe no data or key");
        }
        Check(handlers.Count==clients,"all operations reuse cached client");
        Check(handlers[0].Paths.Contains("/api/accounts/oauth/token")&&server.Refreshes==1&&handlers[0].Paths.Contains("/keys")&&handlers[0].Paths.Contains("/models")&&handlers[0].Paths.Contains("/responses")&&handlers[0].Paths.Contains("/chat/completions"),"login refresh models and generation same handler");
        await ai.SignOutAsync(default);
        store.Save(store.Current with{Ai=store.Current.Ai with{Provider=AiProvider.Api,ProxyMode=AiProxyMode.Custom,ProxyUrl=server.Url,ApiBaseUrl="http://target.invalid/v1"}});
        await ai.TestProxyAsync(default);Check(server.LastHeaders[0].StartsWith("HEAD http://target.invalid/v1"),"probe uses local proxy");
        async Task ProbeFailure(string expected){try{await ai.TestProxyAsync(default);throw new Exception("expected probe failure");}catch(AiNetworkException e){Check(e.Message.Contains(expected),"probe reason "+expected);}}
        server.Mode="407";await ProbeFailure("代理拒绝");
        server.Mode="probe-timeout";var watch=System.Diagnostics.Stopwatch.StartNew();await ProbeFailure("超时");Check(watch.Elapsed.TotalSeconds is >=2.5 and <6,"three second timeout");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();await Error<OperationCanceledException>(()=>ai.TestProxyAsync(cancel.Token),"caller cancellation preserved");}
        server.Mode="normal";
        var closed=new TcpListener(IPAddress.Loopback,0);closed.Start();string closedUrl="http://127.0.0.1:"+((IPEndPoint)closed.LocalEndpoint).Port;closed.Stop();
        store.Save(store.Current with{Ai=store.Current.Ai with{ProxyUrl=closedUrl}});await ProbeFailure("连不上代理");
        store.Save(store.Current with{Ai=store.Current.Ai with{ProxyMode=AiProxyMode.None,ApiBaseUrl=closedUrl}});await ProbeFailure("目标连不上");
        foreach(var scheme in new[]{"socks5","socks5h"})await SocksProbe(store,ai,scheme);
    }
    static async Task SocksProbe(SettingsStore store,AiService ai,string scheme)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string? target=null;
        var serving=Task.Run(async()=>{
            using var peer=await listener.AcceptTcpClientAsync(stop.Token);var stream=peer.GetStream();
            var hello=new byte[2];await stream.ReadExactlyAsync(hello,stop.Token);var methods=new byte[hello[1]];await stream.ReadExactlyAsync(methods,stop.Token);
            await stream.WriteAsync(new byte[]{5,0},stop.Token);
            var command=new byte[5];await stream.ReadExactlyAsync(command,stop.Token);
            Check(command[0]==5&&command[1]==1&&command[3]==3,"socks domain connect");
            var name=new byte[command[4]];await stream.ReadExactlyAsync(name,stop.Token);target=Encoding.ASCII.GetString(name);var port=new byte[2];await stream.ReadExactlyAsync(port,stop.Token);
            await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},stop.Token);
            using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);while(await reader.ReadLineAsync(stop.Token) is {Length:>0}){}
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),stop.Token);
        });
        store.Save(store.Current with{Ai=store.Current.Ai with{ProxyMode=AiProxyMode.Custom,ProxyUrl=$"{scheme}://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",ApiBaseUrl="http://fixture.invalid/v1"}});
        try{await ai.TestProxyAsync(default);await serving;Check(target=="fixture.invalid","socks remote DNS");}
        finally{stop.Cancel();listener.Stop();try{await serving;}catch(OperationCanceledException){}}
    }
}
