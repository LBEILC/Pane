using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Ai;
using Pane.Settings;

internal static partial class Program
{
    static async Task PresetTests(string directory)
    {
        int before=count;
        foreach(var preset in AiPresets.All){
            Check(AiSettings.ValidUrl(preset.BaseUrl),preset.Id+" url");
            foreach(var url in new[]{preset.BaseUrl,preset.BaseUrl+"/",preset.BaseUrl.ToUpperInvariant()+"///"})Check(AiPresets.Match(url)==preset,preset.Id+" match");
            await using var server=new FakeServer();
            var store=new SettingsStore(Path.Combine(directory,"052-"+preset.Id),Defaults());
            store.Save(store.Current with{Ai=new(){ApiEndpoint="custom-00000001",Endpoints=[FixtureEndpoint(server.Url) with{Model=preset.Model,TranslationModel=preset.TranslationModel}],ProxyMode=AiProxyMode.None,Reasoning=AiReasoningEffort.Medium}});
            using var ai=new AiService(store);ai.SaveApiKey("fixture-052");
            // 以下仅按官方协议合成响应，不表示真实账号或线上模型已实测。
            string mode="normal";
            server.Fixture=(path,body)=>{
                (int,string,string) Json(int status,string json)=>(status,json,"application/json");
                if(mode=="key")return Json(401,preset.Id switch{
                    "zhipu"=>"{\"error\":{\"code\":\"1000\",\"message\":\"身份验证失败\"}}",
                    "kimi"=>"{\"error\":{\"type\":\"invalid_authentication_error\",\"message\":\"Invalid Authentication\"}}",
                    "qwen"=>"{\"error\":{\"code\":\"InvalidApiKey\",\"message\":\"Invalid API-key provided.\"}}",
                    _=>"{\"error\":{\"code\":\"invalid_api_key\",\"message\":\"Invalid API key\"}}"});
                if(mode=="quota")return preset.Id switch{
                    "deepseek"=>Json(402,"{\"error\":{\"message\":\"Insufficient Balance\"}}"),
                    "qwen"=>Json(403,"{\"error\":{\"code\":\"Arrearage\",\"message\":\"Access denied, please make sure your account is in good standing.\"}}"),
                    "zhipu"=>Json(429,"{\"error\":{\"code\":\"1113\",\"message\":\"您的账户已欠费，请充值后重试\"}}"),
                    "kimi"=>Json(429,"{\"error\":{\"type\":\"exceeded_current_quota_error\"}}"),
                    _=>Json(429,"{\"error\":{\"code\":\"insufficient_quota\"}}")};
                if(path=="/models")return mode=="no-models"?Json(404,"{\"error\":{\"message\":\"Not Found\"}}"):
                    Json(200,"{\"object\":\"list\",\"data\":[{\"id\":\""+preset.Model+"\",\"object\":\"model\"}]}");
                using var doc=JsonDocument.Parse(body);
                if(mode=="reasoning"&&doc.RootElement.TryGetProperty("reasoning_effort",out _))return Json(preset.Id=="deepseek"?422:400,preset.Id=="zhipu"
                    ?"{\"error\":{\"code\":\"1214\",\"message\":\"reasoning_effort 参数非法。请检查文档\"}}"
                    :"{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Invalid reasoning_effort value\"}}");
                return (200,"data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":null}}]}\n\ndata: {\"choices\":[{\"delta\":{\"reasoning_content\":\"fixture reasoning\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"回答\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"total_tokens\":3}}\n\ndata: [DONE]\n\n","text/event-stream");
            };
            Check(await Run(ai)=="回答",preset.Id+" stream");
            Check((await ai.ListModelsAsync(default)).Single().Id==preset.Model,preset.Id+" models");
            mode="no-models";await Error<AiCredentialException>(async()=>{await ai.ListModelsAsync(default);},preset.Id+" unavailable models");
            Check(await Run(ai)=="回答"&&ai.CurrentModelId==preset.Model,preset.Id+" manual model still usable");
            mode="reasoning";int requests=server.Requests.Count;Check(await Run(ai)=="回答",preset.Id+" reasoning fallback");
            Check(server.Requests.Count==requests+2&&!server.LastBody!.Contains("reasoning_effort"),preset.Id+" one retry without effort");
            requests=server.Requests.Count;await Run(ai);Check(server.Requests.Count==requests+1,preset.Id+" fallback cached");
            mode="key";await Error<AiCredentialException>(()=>Run(ai),preset.Id+" credential mapping");
            mode="quota";try{await Run(ai);throw new Exception("quota missing");}catch(AiQuotaException e){Check(e.Message==Pane.Localization.Strings.Ai_QuotaExceeded,preset.Id+" quota mapping");}
        }
        Check(AiPresets.Match(null)==null&&AiPresets.Match("https://custom.invalid/v1")==null,"custom preset");
        await using(var server=new FakeServer{Mode="thinking-first"}){
            var store=new SettingsStore(Path.Combine(directory,"052-thinking"),Defaults());
            store.Save(store.Current with{Ai=new(){ApiEndpoint="custom-00000001",Endpoints=[FixtureEndpoint(server.Url) with{Model="fixture"}],ProxyMode=AiProxyMode.None}});
            using var ai=new AiService(store){FirstTimeout=TimeSpan.FromMilliseconds(200)};ai.SaveApiKey("fixture-052");
            Check(await Run(ai)=="你好","thinking token prevents false first-token timeout without exposing reasoning");
        }
        Console.WriteLine($"PASS presets assertions={count-before}; simulatedProviders=5; realKeys=0");
    }
}
