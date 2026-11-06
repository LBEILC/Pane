using System.Text.Json;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Ai;
using Pane.Settings;

internal static partial class Program
{
    static AiEndpoint FixtureEndpoint(string url)=>new("custom-00000001","Fixture",url,AiApiFormat.OpenAi,"fixture-model",null);
    static AiSettings WithTranslation(AiSettings settings,string? model)=>settings.Provider==AiProvider.ChatGpt
        ?settings with{TranslationModel=model}:settings with{Endpoints=[settings.CurrentEndpoint with{TranslationModel=model}]};
    static async Task EndpointTests(string directory)
    {
        int before=count;
        SettingsStore Legacy(string name,AiSettings settings){
            var path=Path.Combine(directory,"065-"+name);Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path,"settings.json"),JsonSerializer.Serialize(Defaults() with{Ai=settings},SettingsJson.Default.PaneSettings));
            var result=new SettingsStore(path,Defaults());result.Load();return result;
        }
        var fresh=new SettingsStore(Path.Combine(directory,"065-fresh"),Defaults());fresh.Load();
        Check(fresh.Current.Ai.ApiEndpoint==AiPresets.All[0].Id&&File.Exists(Path.Combine(fresh.DirectoryPath,"settings.json")),"fresh endpoint persisted");
        int n=0;
        foreach(var url in new[]{"https://api.deepseek.com","https://api.deepseek.com/v1/"})foreach(bool different in new[]{false,true}){
            var preset=AiPresets.All[0];
            var migrated=Legacy("preset-"+n++,new(){ApiBaseUrl=url,ApiModel=different?"old-model":preset.Model,TranslationModel=different?"old-fast":preset.TranslationModel});
            var migratedAi=migrated.Current.Ai;
            Check(migratedAi.ApiEndpoint==preset.Id&&migratedAi.CurrentEndpoint.BaseUrl==preset.BaseUrl,"migration recognizes deepseek");
            Check(migratedAi.CurrentEndpoint.Model==(different?"old-model":preset.Model)&&migratedAi.CurrentEndpoint.TranslationModel==(different?"old-fast":preset.TranslationModel),"migration preserves models");
            Check((migratedAi.Endpoints?.Length??0)==(different?1:0),"only differing preset gets record");
            Check(migratedAi.ApiBaseUrl==null&&migratedAi.ApiModel==null&&migratedAi.TranslationModel==null,"migration clears legacy fields");
        }
        var custom=Legacy("custom",new(){ApiBaseUrl="http://127.0.0.1:1234/v1",ApiModel="legacy-model",TranslationModel="legacy-fast"});
        var endpoint=custom.Current.Ai.CurrentEndpoint;
        Check(AiSettings.CustomId(endpoint.Id)&&endpoint.Name==Strings.Settings_Ai_CustomVendor&&endpoint.BaseUrl=="http://127.0.0.1:1234/v1"&&endpoint.Format==AiApiFormat.OpenAi,"unknown migration custom endpoint");
        Check(endpoint.Model=="legacy-model"&&endpoint.TranslationModel=="legacy-fast","custom models migrated");
        string path=Path.Combine(custom.DirectoryPath,"settings.json"),content=File.ReadAllText(path);var stamp=File.GetLastWriteTimeUtc(path);
        custom.Load();Check(File.ReadAllText(path)==content&&File.GetLastWriteTimeUtc(path)==stamp&&custom.Current.Ai.CurrentEndpoint==endpoint,"migration only once");
        var account=Legacy("account",new(){Provider=AiProvider.ChatGpt,ApiBaseUrl="https://api.deepseek.com",ApiModel="legacy-model",TranslationModel="account-fast"});
        Check(account.Current.Ai.TranslationModel=="account-fast"&&account.Current.Ai.CurrentEndpoint.TranslationModel=="account-fast","account translation preserved");
        new SecretStore(custom.DirectoryPath).Write("ai-key.bin","legacy-fixture-key");
        var encrypted=File.ReadAllBytes(Path.Combine(custom.DirectoryPath,"ai-key.bin"));
        using(var service=new AiService(custom)){
            Check(service.ApiKeyMask=="lega…-key"&&!File.Exists(Path.Combine(custom.DirectoryPath,"ai-key.bin")),"legacy key moved");
            Check(encrypted.SequenceEqual(File.ReadAllBytes(Path.Combine(custom.DirectoryPath,AiService.KeyFile(endpoint.Id)))),"legacy DPAPI bytes preserved");
        }
        using(var service=new AiService(custom))Check(service.ApiKeyMask=="lega…-key","key migration only once");

        var valid=FixtureEndpoint("");
        foreach(var bad in new[]{
            new[]{valid,valid},new[]{valid with{Id="custom-xyz"}},new[]{valid with{Id="custom-ABCDEF12"}},
            Enumerable.Range(0,21).Select(i=>valid with{Id="custom-"+i.ToString("x8")}).ToArray(),
            new[]{valid with{Name=" "}},new[]{valid with{Name=new string('x',41)}},new[]{valid with{Name=null!}},
            new[]{valid with{BaseUrl="file:///x"}},new[]{valid with{BaseUrl=null!}},new[]{valid with{Format=(AiApiFormat)99}},
            new[]{valid with{Model=" "}},new[]{valid with{TranslationModel=""}},new[]{(AiEndpoint)null!},
            new[]{valid with{Id="deepseek",Model=" "}}})
            await Error<ArgumentException>(()=>{fresh.Save(fresh.Current with{Ai=fresh.Current.Ai with{Endpoints=bad}});return Task.CompletedTask;},"endpoint validation");
        fresh.Save(fresh.Current with{Ai=fresh.Current.Ai with{ApiEndpoint=valid.Id,Endpoints=[valid]}});
        Check(fresh.Current.Ai.CurrentEndpoint.BaseUrl=="","empty endpoint can save");
        fresh.Save(fresh.Current with{Ai=fresh.Current.Ai with{ApiEndpoint="deepseek",Endpoints=[valid with{Id="deepseek",Name="",BaseUrl="bad",Format=(AiApiFormat)99,Model="override",TranslationModel="fast"}]}});
        Check(fresh.Current.Ai.CurrentEndpoint is {Name:"DeepSeek",Format:AiApiFormat.OpenAi,Model:"override",TranslationModel:"fast"}&&fresh.Current.Ai.CurrentEndpoint.BaseUrl==AiPresets.All[0].BaseUrl,"preset ignores non-model fields");

        await using var server=new FakeServer();
        var a=FixtureEndpoint(server.Url);var b=a with{Id="custom-00000002",Name="Second",Format=AiApiFormat.Anthropic};
        var store=new SettingsStore(Path.Combine(directory,"065-service"),Defaults());
        store.Save(store.Current with{Ai=new(){ApiEndpoint=a.Id,Endpoints=[a,b],ProxyMode=AiProxyMode.None}});
        using var ai=new AiService(store);int changes=0;ai.Changed+=(_,_)=>changes++;
        ai.SaveApiKey("first-fixture-key");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint=b.Id}});Check(ai.ApiKeyMask==null,"new endpoint has no key");
        ai.SaveApiKey("second-fixture-key");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint=a.Id}});Check(ai.ApiKeyMask=="firs…-key"&&changes>=4,"switch restores first key and notifies");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint="deepseek"}});ai.SaveApiKey("preset-fixture-key");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint=b.Id}});Check(ai.ApiKeyMask=="seco…-key","switch restores second key");
        store.Save(store.Current with{Ai=store.Current.Ai with{Endpoints=[b]}});
        Check(!File.Exists(Path.Combine(store.DirectoryPath,AiService.KeyFile(a.Id))),"removed custom key deleted");
        Check(File.Exists(Path.Combine(store.DirectoryPath,AiService.KeyFile("deepseek"))),"preset key retained");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint="deepseek"}});Check(ai.ApiKeyMask=="pres…-key","preset key restored after switching");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint=b.Id}});
        store.Save(store.Current with{Ai=store.Current.Ai with{Endpoints=[b with{BaseUrl=""}]}});
        Check(ai.Status==AiStatus.NotConfigured,"empty url not configured");
        await Error<AiCredentialException>(()=>ai.TestProxyAsync(default),"empty url probe");
        store.Save(store.Current with{Ai=store.Current.Ai with{Endpoints=[b]}});

        const string stream="event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hidden reasoning\"}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"你\"}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"好\"}}\n\ndata: {\"type\":\"message_stop\"}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"ignored\"}}\n\n";
        string mode="normal";
        server.Fixture=(url,body)=>{
            if(int.TryParse(mode,out int code))return(code,"{\"error\":{\"type\":\"fixture\"}}","application/json");
            if(mode.StartsWith("event-"))return(200,"event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\""+mode[6..]+"\"}}\n\n","text/event-stream");
            if(mode=="balance")return(400,"{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low\"}}","application/json");
            if(url=="/models")return(200,"{\"data\":[{\"id\":\"fixture-model\",\"display_name\":\"Anthropic Fixture\"}]}","application/json");
            if(mode=="reject"&&body.Contains("\"thinking\""))return(400,"{\"error\":{\"message\":\"This model does not support thinking\"}}","application/json");
            return(200,stream,"text/event-stream");
        };
        foreach(var effort in Enum.GetValues<AiReasoningEffort>()){
            store.Save(store.Current with{Ai=store.Current.Ai with{Reasoning=effort}});
            string text="";await foreach(var part in ai.ChatAsync([new(AiChatRole.User,"first"),new(AiChatRole.Assistant,"answer"),new(AiChatRole.User,"next")],null,default))text+=part;
            Check(text=="你好","anthropic text only and stop");
            Check(server.LastPath=="/messages"&&server.LastHeaders[0].StartsWith("POST "),"anthropic messages path");
            Check(server.LastHeaders.Any(h=>h.Equals("x-api-key: second-fixture-key",StringComparison.OrdinalIgnoreCase))&&server.LastHeaders.Any(h=>h.Equals("anthropic-version: 2023-06-01",StringComparison.OrdinalIgnoreCase))&&!server.LastHeaders.Any(h=>h.StartsWith("Authorization:",StringComparison.OrdinalIgnoreCase)),"anthropic auth headers");
            Check(server.LastHeaders.Any(h=>h.Equals("Content-Type: application/json",StringComparison.OrdinalIgnoreCase)),"anthropic content type");
            using var doc=JsonDocument.Parse(server.LastBody!);var root=doc.RootElement;
            Check(root.GetProperty("model").GetString()==b.Model&&root.GetProperty("system").GetString()==AiProtocol.SystemPrompt&&root.GetProperty("stream").GetBoolean(),"anthropic model system stream");
            Check(root.GetProperty("messages").EnumerateArray().Select(m=>m.GetProperty("role").GetString()).SequenceEqual(["user","assistant","user"]),"anthropic alternating roles");
            Check(root.GetProperty("messages").EnumerateArray().Select(m=>m.GetProperty("content").GetString()).SequenceEqual(["first","answer","next"]),"anthropic multi-turn text");
            bool thinking=root.TryGetProperty("thinking",out var value);Check(thinking==(effort!=AiReasoningEffort.Default),"anthropic thinking switch");
            if(thinking)Check(value.GetProperty("type").GetString()=="enabled"&&value.GetProperty("budget_tokens").GetInt32()==(effort switch{AiReasoningEffort.Low=>1024,AiReasoningEffort.Medium=>4096,_=>8192})&&root.GetProperty("max_tokens").GetInt32()>value.GetProperty("budget_tokens").GetInt32(),"anthropic budget");
        }
        foreach(var failure in new[]{"401","403","429","529","event-authentication_error","event-permission_error","event-rate_limit_error","event-overloaded_error","balance"}){
            mode=failure;
            if(failure is "401" or "403" or "event-authentication_error" or "event-permission_error")await Error<AiCredentialException>(()=>Run(ai),failure);
            else if(failure is "529" or "event-overloaded_error")await Error<AiNetworkException>(()=>Run(ai),failure);
            else await Error<AiQuotaException>(()=>Run(ai),failure);
        }
        mode="normal";await ai.TestAsync(default);using(var probe=JsonDocument.Parse(server.LastBody!))Check(!probe.RootElement.TryGetProperty("thinking",out _)&&probe.RootElement.GetProperty("max_tokens").GetInt32()==16,"connection test disables thinking before fallback");
        mode="reject";int requests=server.Requests.Count;Check(await Run(ai)=="你好","anthropic thinking rejection succeeds");
        Check(server.Requests.Count==requests+2&&!server.LastBody!.Contains("\"thinking\""),"anthropic thinking retried once");
        requests=server.Requests.Count;await Run(ai);Check(server.Requests.Count==requests+1,"anthropic fallback remembered");
        mode="normal";
        Check((await ai.ListModelsAsync(default)).Single() is {Id:"fixture-model",DisplayName:"Anthropic Fixture"},"anthropic model list");
        Check(server.LastPath=="/models"&&server.LastHeaders[0].StartsWith("GET ")&&server.LastHeaders.Any(h=>h.StartsWith("x-api-key:")),"anthropic models authentication");
        await ai.TestAsync(default);using(var doc=JsonDocument.Parse(server.LastBody!))Check(doc.RootElement.GetProperty("max_tokens").GetInt32()==16&&!doc.RootElement.TryGetProperty("thinking",out _),"anthropic tiny connection test");
        store.Save(store.Current with{Ai=WithTranslation(store.Current.Ai,"fast-anthropic")});
        await foreach(var _ in ai.RunAsync(new("translate","fixture",null,null),default)){}
        using(var doc=JsonDocument.Parse(server.LastBody!))Check(doc.RootElement.GetProperty("model").GetString()=="fast-anthropic","anthropic translation endpoint model");
        store.Save(store.Current with{Ai=store.Current.Ai with{ApiEndpoint=a.Id,Endpoints=[a,b]}});ai.SaveApiKey("first-fixture-key");
        Check(ai.CurrentModelName=="fixture-model","provider switch invalidates model names");
        server.Fixture=null;Check(await Run(ai)=="你好"&&server.LastPath=="/chat/completions","switch back OpenAI protocol");
        Check((await ai.ListModelsAsync(default)).Single().DisplayName=="Fixture Model","switch model list source");
        Console.WriteLine($"PASS endpoints assertions={count-before}; loopbackOnly=True; realKeys=0");
    }
}
