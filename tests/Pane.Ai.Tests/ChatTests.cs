using System.Text.Json;
using Pane.Contracts;
using Pane.Settings;
using Pane.Services.Ai;
internal static partial class Program
{
    static async Task ChatTests(AiService ai,SettingsStore store,FakeServer server)
    {
        var saved=store.Current;
        AiChatMessage[] messages=[new(AiChatRole.User,"first"),new(AiChatRole.Assistant,"answer"),new(AiChatRole.User,"next")];
        async Task<string> Chat(string? model=null,CancellationToken ct=default){string result="";await foreach(var part in ai.ChatAsync(messages,model,ct))result+=part;return result;}
        Check(await Chat("temporary")=="你好","chat streaming");
        bool account=store.Current.Ai.Provider==AiProvider.ChatGpt;
        using(var doc=JsonDocument.Parse(server.LastBody!)){
            var root=doc.RootElement;var array=root.GetProperty(account?"input":"messages");
            Check(root.GetProperty("model").GetString()=="temporary","chat override");
            var turns=array.EnumerateArray().Where(m=>m.GetProperty("role").GetString()!="system").ToArray();
            Check(turns.Select(t=>t.GetProperty("role").GetString()).SequenceEqual(["user","assistant","user"]),"ordered roles");
            Check(turns.Select(t=>t.GetProperty("content").GetString()).SequenceEqual(messages.Select(m=>m.Text)),"ordered content");
            if(account)Check(!root.GetProperty("store").GetBoolean(),"account no storage");
        }
        Check(store.Current==saved&&ai.CurrentModelId=="fixture-model","override not persisted");
        store.Save(saved with{Ai=saved.Ai with{TranslationModel="fast-model"}});
        Check(ai.TranslationModelName=="fast-model","translation name");
        await foreach(var _ in ai.RunAsync(new("translate","fixture",null,null),default)){}
        using(var doc=JsonDocument.Parse(server.LastBody!))Check(doc.RootElement.GetProperty("model").GetString()=="fast-model","translation model");
        await Chat();using(var doc=JsonDocument.Parse(server.LastBody!))Check(doc.RootElement.GetProperty("model").GetString()=="fixture-model","chat main model");
        foreach(var invalid in new[]{""," "})await Error<ArgumentException>(()=>{store.Save(saved with{Ai=saved.Ai with{TranslationModel=invalid}});return Task.CompletedTask;},"translation validation");
        foreach(var invalid in new string[][]{[null!],[""],[" "]})await Error<ArgumentException>(()=>{store.Save(saved with{Launcher=saved.Launcher with{ActionRow=invalid}});return Task.CompletedTask;},"action row validation");
        store.Save(saved with{Launcher=saved.Launcher with{ActionRow=["future-action"]}});
        Check(store.Current.Launcher.ActionRow[0]=="future-action","unknown action accepted");
        store.Save(saved);
        server.Mode="cancel";server.Connected=new();server.Disconnected=new();
        using(var ct=new CancellationTokenSource()){
            var work=Chat(null,ct.Token);await server.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));ct.Cancel();
            await Error<OperationCanceledException>(()=>work,"chat cancel");await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        server.Mode="500";await Error<AiNetworkException>(()=>Chat(),"chat error category");server.Mode="normal";
    }
}
