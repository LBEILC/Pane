using Pane.Localization;
using System.Text;
using System.Text.Json;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Ai;

internal static class AiProtocol
{
    internal const string SystemPrompt="你在 Windows 小工具 Pane 里回答。回答简洁直接；除非用户要求，否则用提问所用的语言；尽量用纯文本，少用 Markdown。"; // 不翻译：协议提示词
    internal static string Prompt(AiSettings settings,AiRequest request)
    {
        if(request.ActionId=="ask")return (request.Question??"")+(string.IsNullOrWhiteSpace(request.Text)?"":"\n\n---\n下面是附带的内容：\n"+request.Text); // 不翻译：协议提示词
        var action=settings.Actions.FirstOrDefault(a=>a.Id==request.ActionId)??throw new AiCredentialException(Strings.Ai_ActionUnavailable);
        var target=request.TargetLanguage??settings.TranslateTarget;
        if(target=="auto"){
            int total=0,han=0;
            foreach(var r in request.Text.EnumerateRunes())if(!Rune.IsWhiteSpace(r)){total++;if(r.Value is >=0x3400 and <=0x4dbf or >=0x4e00 and <=0x9fff or >=0xf900 and <=0xfaff or >=0x20000 and <=0x323af)han++;}
            target=total>0&&han*2>=total?"en":"zh";
        }
        string lang=target switch {"zh"=>"中文","en"=>"英文","ja"=>"日文","ko"=>"韩文","fr"=>"法文","de"=>"德文","es"=>"西班牙文","ru"=>"俄文",_=>throw new AiCredentialException(Strings.Ai_TargetLanguageInvalid)}; // 不翻译：协议提示词
        // 单次扫描模板，避免把用户文本里的变量再次替换。
        var result=new StringBuilder();var template=action.PromptTemplate;
        for(int i=0;i<template.Length;){if(template.AsSpan(i).StartsWith("{text}")){result.Append(request.Text);i+=6;}else if(template.AsSpan(i).StartsWith("{lang}")){result.Append(lang);i+=6;}else result.Append(template[i++]);}
        return result.ToString();
    }
    internal static string? String(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
    internal static AiException Error(int status,JsonElement body,bool chat)
    {
        var error=body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("error",out var e)?e:body;
        string? code=error.ValueKind==JsonValueKind.String?error.GetString():String(error,"code")??String(error,"type");
        if(code is "insufficient_quota" or "usage_limit_reached" or "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_user_not_eligible")return new AiQuotaException(Strings.Ai_QuotaExceeded);
        if(status==429||code=="rate_limit_exceeded")return new AiQuotaException(Strings.Ai_RateLimited);
        if(status>=500||code is "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable")return new AiNetworkException(Strings.Ai_Unavailable);
        if(chat&&(status is 401 or 403||code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused"))return new AiSignInRequiredException(Strings.Ai_LoginExpired);
        if(status is 401 or 403)return new AiCredentialException(Strings.Ai_InvalidKey);
        if(code=="model_not_found"||String(error,"param")=="model")return new AiCredentialException(Strings.Ai_ModelUnavailable);
        if(status is >=400 and <500)return new AiCredentialException(Strings.Ai_InvalidRequest);
        return new AiNetworkException(Strings.Ai_InvalidResponse);
    }
    internal static (string? Text,bool Done) Frame(string data,bool chat)
    {
        if(data=="[DONE]"){if(chat)throw new AiNetworkException(Strings.Ai_IncompleteResponse);return(null,true);}
        try {
            using var doc=JsonDocument.Parse(data);var root=doc.RootElement;
            if(root.TryGetProperty("error",out _))throw Error(0,root,chat);
            if(chat){
                var type=String(root,"type");
                if(type=="response.completed")return(null,true);
                if(type is "response.failed" or "error")throw Error(0,root.TryGetProperty("response",out var response)?response:root,true);
                if(type=="response.incomplete")throw new AiNetworkException(Strings.Ai_IncompleteResponse);
                return(type=="response.output_text.delta"?String(root,"delta"):null,false);
            }
            if(root.TryGetProperty("choices",out var choices)&&choices.ValueKind==JsonValueKind.Array&&choices.GetArrayLength()>0&&choices[0].TryGetProperty("delta",out var delta))return(String(delta,"content"),false);
            return(null,false);
        }catch(Exception e) when(e is JsonException or InvalidOperationException){throw new AiNetworkException(Strings.Ai_InvalidResponse);}
    }
    internal static byte[] Body(string model,string prompt,bool chat)=>Body(model,[new(AiChatRole.User,prompt)],chat);
    internal static bool UnsupportedReasoning(JsonElement body)
    {
        var error=body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("error",out var e)?e:body;
        var param=String(error,"param");
        if(param is "reasoning" or "reasoning_effort"||param?.StartsWith("reasoning.",StringComparison.Ordinal)==true)return true;
        var message=(String(error,"message")??(error.ValueKind==JsonValueKind.String?error.GetString():null)??"").ToLowerInvariant();
        return (message.Contains("reasoning")||message.Contains("思考"))&& // 不翻译：外部数据匹配
            (message.Contains("unsupported")||message.Contains("not support")||message.Contains("unknown")||message.Contains("unrecognized")||message.Contains("not allowed")||message.Contains("不支持")||message.Contains("不支援")||message.Contains("不识别")||message.Contains("不識別")); // 不翻译：外部数据匹配
    }
    internal static byte[] Body(string model,IReadOnlyList<AiChatMessage> messages,bool chat,AiReasoningEffort reasoning=AiReasoningEffort.Default)
    {
        using var stream=new MemoryStream();using(var w=new Utf8JsonWriter(stream)){
            w.WriteStartObject();w.WriteString("model",model);w.WriteBoolean("stream",true);
            if(reasoning!=AiReasoningEffort.Default){
                string effort=reasoning switch{AiReasoningEffort.Low=>"low",AiReasoningEffort.Medium=>"medium",AiReasoningEffort.High=>"high",_=>throw new ArgumentOutOfRangeException(nameof(reasoning))};
                if(chat){w.WriteStartObject("reasoning");w.WriteString("effort",effort);w.WriteEndObject();}else w.WriteString("reasoning_effort",effort);
            }
            if(chat){w.WriteBoolean("store",false);w.WriteString("instructions",SystemPrompt);}
            w.WriteStartArray(chat?"input":"messages");
            if(!chat){w.WriteStartObject();w.WriteString("role","system");w.WriteString("content",SystemPrompt);w.WriteEndObject();}
            foreach(var message in messages){w.WriteStartObject();w.WriteString("role",message.Role switch{AiChatRole.User=>"user",AiChatRole.Assistant=>"assistant",_=>throw new AiCredentialException(Strings.Ai_InvalidChatRole)});w.WriteString("content",message.Text);w.WriteEndObject();}w.WriteEndArray();w.WriteEndObject();
        }return stream.ToArray();
    }
}
