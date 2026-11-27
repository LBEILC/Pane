using System.Text.Json;
using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;

namespace Pane.Services.Ai;

internal static class AnthropicProtocol
{
    internal static byte[] Body(string model,IReadOnlyList<AiChatMessage> messages,AiReasoningEffort effort,bool test=false, IReadOnlyDictionary<string,AiImage>? images=null)
    {
        using var stream=new MemoryStream();
        using(var w=new Utf8JsonWriter(stream)){
            w.WriteStartObject();w.WriteString("model",model);w.WriteBoolean("stream",true);
            w.WriteNumber("max_tokens",test?16:16384);w.WriteString("system",AiProtocol.SystemPrompt);
            if(!test&&effort!=AiReasoningEffort.Default){
                w.WriteStartObject("thinking");w.WriteString("type","enabled");
                w.WriteNumber("budget_tokens",effort switch{AiReasoningEffort.Low=>1024,AiReasoningEffort.Medium=>4096,AiReasoningEffort.High=>8192,_=>throw new ArgumentOutOfRangeException(nameof(effort))});
                w.WriteEndObject();
            }
            w.WriteStartArray("messages");
            // 合并相邻同角色消息，确保发送给 Messages 的角色交替。
            for(int i=0;i<messages.Count;i++){
                var role=messages[i].Role;
                int start=i;
                string text=messages[i].Text;
                while(i+1<messages.Count&&messages[i+1].Role==role)text+="\n\n"+messages[++i].Text;
                w.WriteStartObject();w.WriteString("role",role switch{AiChatRole.User=>"user",AiChatRole.Assistant=>"assistant",_=>throw new AiCredentialException(Strings.Ai_InvalidChatRole)});
                if (images != null && messages.Skip(start).Take(i-start+1).Any(m => m.Images is { Count: > 0 }))
                {
                    w.WriteStartArray("content");
                    for (int j=start; j<=i; j++)
                    {
                        foreach (var path in messages[j].Images ?? []) images[path].Write(w, false, true);
                        AiImage.WriteText(w, messages[j].Text, false);
                    }
                    w.WriteEndArray();
                }
                else w.WriteString("content",text);
                w.WriteEndObject();
            }
            w.WriteEndArray();w.WriteEndObject();
        }
        return stream.ToArray();
    }
    internal static bool UnsupportedThinking(JsonElement body)
    {
        var error=body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("error",out var e)?e:body;
        var message=(AiProtocol.String(error,"message")??"").ToLowerInvariant();
        return message.Contains("thinking")&&(message.Contains("unsupported")||message.Contains("not support")||message.Contains("unknown")||message.Contains("unrecognized")||message.Contains("not allowed")||message.Contains("不支持"));
    }
    internal static AiException Error(int status,JsonElement body)
    {
        var error=body.ValueKind==JsonValueKind.Object&&body.TryGetProperty("error",out var e)?e:body;
        var type=AiProtocol.String(error,"type");
        var message=(AiProtocol.String(error,"message")??"").ToLowerInvariant();
        if(status is 401 or 403||type is "authentication_error" or "permission_error")return new AiCredentialException(Strings.Ai_InvalidKey);
        if(type is "overloaded_error" or "api_error"||status>=500)return new AiNetworkException(Strings.Ai_Unavailable);
        if(type=="rate_limit_error"||status==429)return new AiQuotaException(Strings.Ai_RateLimited);
        if(message.Contains("credit balance")||message.Contains("insufficient balance")||message.Contains("insufficient quota")||message.Contains("余额不足")||message.Contains("额度不足"))return new AiQuotaException(Strings.Ai_QuotaExceeded);
        return AiProtocol.Error(status,body,false);
    }
    internal static (string? Text,bool Done,bool Progress) Frame(string data)
    {
        try{
            using var doc=JsonDocument.Parse(data);var root=doc.RootElement;
            var type=AiProtocol.String(root,"type");
            if(type=="error"||root.TryGetProperty("error",out _))throw Error(0,root);
            if(type=="message_stop")return(null,true,false);
            if(type=="content_block_delta"&&root.TryGetProperty("delta",out var delta)){
                var kind=AiProtocol.String(delta,"type");
                return(kind=="text_delta"?AiProtocol.String(delta,"text"):null,false,kind is "text_delta" or "thinking_delta");
            }
            return(null,false,false);
        }catch(Exception e) when(e is JsonException or InvalidOperationException){throw new AiNetworkException(Strings.Ai_InvalidResponse);}
    }
}
