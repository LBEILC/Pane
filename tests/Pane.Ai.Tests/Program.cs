using Pane.Contracts;
using Pane.Settings;
using Pane.Services.Ai;
using System.Text.Json;

internal static partial class Program
{
    static int count;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;}
    static async Task Error<T>(Func<Task> action,string name) where T:Exception {try{await action();throw new Exception(name);}catch(T){count++;}}
    static PaneSettings Defaults()=>new(1,new(false,false,ThemeMode.Dark,"#FF5874C9",20,1,new(.2,.2,1,1,1,0),new(.2,.2,1,1,1,0)),new(new(KeyModifiers.Ctrl|KeyModifiers.Alt|KeyModifiers.Shift,76)),new(new(KeyModifiers.Ctrl|KeyModifiers.Alt|KeyModifiers.Shift,67)));
    static async Task<string> Run(AiService ai,CancellationToken ct=default){var text="";await foreach(var part in ai.RunAsync(new("ask","fixture attachment",null,"fixture question"),ct))text+=part;return text;}
    static async Task Main(string[] args)
    {
        if(args.Length==2&&args[0]=="server"){
            await using var fake=new FakeServer();File.WriteAllText(args[1]+".url",fake.Url);while(!File.Exists(args[1]+".stop"))await Task.Delay(100);return;
        }
        string directory=Path.Combine(AppContext.BaseDirectory,"results",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var store=new SettingsStore(directory,Defaults());
        var settings=new AiSettings();
        await ProxyReasoningTests(directory);
        Check(AiProtocol.Prompt(settings,new("translate","你好",null,null)).Contains("英文"),"auto han");
        Check(AiProtocol.Prompt(settings,new("translate","hi",null,null)).Contains("中文"),"auto latin");
        Check(AiProtocol.Prompt(settings,new("translate","中A",null,null)).Contains("英文"),"auto half");
        Check(AiProtocol.Prompt(settings,new("translate","𠀀",null,null)).Contains("英文"),"auto rune");
        Check(AiProtocol.Prompt(settings,new("translate","   ",null,null)).Contains("中文"),"auto empty");
        foreach(var lang in new[]{"zh","en","ja","ko","fr","de","es","ru"})Check(!AiProtocol.Prompt(settings,new("translate","x",lang,null)).Contains("{lang}"),"language");
        Check(AiProtocol.Prompt(settings,new("translate","literal {lang}","ja",null)).EndsWith("literal {lang}"),"no recursive substitution");
        Check(AiProtocol.Prompt(settings,new("ask","attachment",null,"question"))=="question\n\n---\n下面是附带的内容：\nattachment","attachment");
        Check(AiProtocol.Prompt(settings,new("ask","",null,"question"))=="question","ask");
        var old=JsonSerializer.SerializeToElement(Defaults(),SettingsJson.Default.PaneSettings);
        File.WriteAllText(Path.Combine(directory,"settings.json"),"{"+string.Join(",",old.EnumerateObject().Where(p=>p.Name!="Ai").Select(p=>("\""+p.Name+"\"")+":"+p.Value.GetRawText()))+"}");
        store.Load();Check(store.Current.Ai.Actions.Length==4&&!Directory.GetFiles(directory,"*.bad-*").Any(),"old settings");
        foreach(var bad in new[]{settings with{ApiBaseUrl="file:///x"},settings with{ProxyUrl="invalid"},settings with{Actions=[new("x","x","x","x"),new("x","y","y","y")]},settings with{Actions=[new("","x","x","x")]},settings with{Actions=[new("x","","x","x")]},settings with{Actions=[new("x","x","x","")]}})await Error<ArgumentException>(()=>{store.Save(store.Current with{Ai=bad});return Task.CompletedTask;},"validation");
        await using var server=new FakeServer();
        using var ai=new AiService(store,new(){Api=server.Url,Issuer=server.Url,OpenBrowser=server.Browser});
        int events=0;ai.StatusChanged+=(_,_)=>events++;
        Check(ai.Status==AiStatus.NotConfigured,"not configured");
        const string fakeKey="fixture-key-015-private";
        ai.SaveApiKey(fakeKey);Check(ai.ApiKeyMask=="fixt…vate","mask");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory,"ai-key.bin"))).Contains(fakeKey),"encrypted");
        using(var roundtrip=new AiService(store))Check(roundtrip.ApiKeyMask==ai.ApiKeyMask,"dpapi roundtrip");
        ai.SaveApiKey("tiny");Check(ai.ApiKeyMask=="••••","short mask");ai.SaveApiKey(fakeKey);
        store.Save(store.Current with{Ai=settings with{ApiBaseUrl=server.Url,ApiModel="fixture-model"}});
        Check(ai.Status==AiStatus.Ready&&events>=4,"ready and events");
        Check(await Run(ai)=="你好","split utf8 SSE blank invalid done");
        using(var body=JsonDocument.Parse(server.LastBody!)){Check(body.RootElement.GetProperty("stream").GetBoolean(),"stream true");Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()==AiProtocol.SystemPrompt,"system prompt");}
        await ChatTests(ai,store,server);
        var models=await ai.ListModelsAsync(default);Check(models.Count==1&&ai.CurrentModelName=="Fixture Model","models");
        foreach(var status in new[]{"401","403","400","model-error"}){server.Mode=status;await Error<AiCredentialException>(()=>Run(ai),"credential");}
        server.Mode="500";await Error<AiNetworkException>(()=>Run(ai),"5xx");
        server.Mode="429";await Error<AiQuotaException>(()=>Run(ai),"rate");
        server.Mode="quota";try{await Run(ai);throw new Exception("quota");}catch(AiQuotaException e){Check(e.Message.Contains("额度"),"quota message");}
        server.Mode="malformed";await Error<AiNetworkException>(()=>Run(ai),"malformed");
        server.Mode="cancel";server.Disconnected=new();server.Connected=new();
        using(var cancel=new CancellationTokenSource()){var task=Run(ai,cancel.Token);await server.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Error<OperationCanceledException>(()=>task,"cancel");await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(true,"cancel disconnected");}
        server.Mode="stall";server.Disconnected=new();ai.FirstTimeout=TimeSpan.FromMilliseconds(150);
        await Error<AiNetworkException>(()=>Run(ai),"first timeout");await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(true,"timeout disconnected");ai.FirstTimeout=TimeSpan.FromSeconds(30);
        server.Mode="cancel";server.Disconnected=new();ai.TotalTimeout=TimeSpan.FromMilliseconds(180);await Error<AiNetworkException>(()=>Run(ai),"total timeout after first");await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));ai.TotalTimeout=TimeSpan.FromSeconds(120);
        server.Mode="normal";server.ModelsAsChat=true;store.Save(store.Current with{Ai=store.Current.Ai with{Provider=AiProvider.ChatGpt}});Check(ai.Status==AiStatus.NeedsSignIn,"needs signin");
        await ai.SignInAsync(default);if(server.CallbackTask!=null)await server.CallbackTask;
        Check(ai.Status==AiStatus.Ready&&ai.Account is {Email:"fixture@example.invalid",CanUsePlan:true},"login");
        Check(await Run(ai)=="你好"&&server.Refreshes==1,"chat stream refresh");
        using(var body=JsonDocument.Parse(server.LastBody!)){Check(server.LastPath=="/responses"&&!body.RootElement.GetProperty("store").GetBoolean(),"responses format");Check(body.RootElement.GetProperty("instructions").GetString()==AiProtocol.SystemPrompt,"responses system");}
        Check((await ai.ListModelsAsync(default)).Count==1,"chat models");
        await ChatTests(ai,store,server);
        await ai.SignOutAsync(default);Check(ai.Account==null&&ai.Status==AiStatus.NeedsSignIn&&server.Revocations==1,"signout");
        server.Mode="free";await ai.SignInAsync(default);if(server.CallbackTask!=null)await server.CallbackTask;Check(ai.Account?.CanUsePlan==false,"free plan");await Error<AiQuotaException>(()=>Run(ai),"free quota");await ai.SignOutAsync(default);
        server.Mode="normal";await ai.SignInAsync(default);if(server.CallbackTask!=null)await server.CallbackTask;server.RefreshFails=true;await Error<AiSignInRequiredException>(()=>Run(ai),"refresh invalid");Check(ai.Account==null,"invalid session clear");
        ai.SaveApiKey("");store.Save(store.Current with{Ai=store.Current.Ai with{Provider=AiProvider.Api}});Check(ai.Status==AiStatus.NotConfigured&&ai.ApiKeyMask==null,"delete key");
                var pendingDirectory=Path.Combine(directory,"pending");var pendingStore=new SettingsStore(pendingDirectory,Defaults());
        string? authUrl=null;
        using(var pending=new AiService(pendingStore,new(){Api=server.Url,Issuer=server.Url,OpenBrowser=u=>authUrl=u})){
            using var cancel=new CancellationTokenSource();var attempt=pending.SignInAsync(cancel.Token);
            while(authUrl==null)await Task.Delay(5);
            var redirect=Uri.UnescapeDataString(new Uri(authUrl).Query.TrimStart('?').Split('&').First(p=>p.StartsWith("redirect_uri=")).Split('=',2)[1]);
            int port=new Uri(redirect).Port;cancel.Cancel();await Error<OperationCanceledException>(()=>attempt,"login cancellation");
            using var socket=new System.Net.Sockets.TcpClient();await Error<System.Net.Sockets.SocketException>(()=>socket.ConnectAsync("127.0.0.1",port),"callback closed");Check(pending.Account==null,"cancel no session");
            pending.LoginTimeout=TimeSpan.FromMilliseconds(120);await Error<AiNetworkException>(()=>pending.SignInAsync(default),"login timeout");
        }
        server.Mode="normal";ai.SaveApiKey(fakeKey);store.Save(store.Current with{Ai=store.Current.Ai with{ApiModel="changed-model",Actions=[new("custom","custom","ai","{lang}:{text}")]}});
        Check(ai.Actions.Single().Id=="custom","action change");await Run(ai);using(var changed=JsonDocument.Parse(server.LastBody!))Check(changed.RootElement.GetProperty("model").GetString()=="changed-model","model change");
        server.Mode="403";try{await Run(ai);}catch(AiException e){Check(!e.ToString().Contains(fakeKey),"credential redaction");}
        Console.WriteLine($"PASS ai assertions={count}; loopbackOnly=True; realCredentials=0; listenersStoppedOnExit=True");
    }
}
