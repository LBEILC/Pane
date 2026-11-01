using System.Net;
using System.Net.Http;
using System.Text.Json;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed class WebSuggestions: IWebSuggestions,IDisposable
{
    readonly HttpClient client;readonly Func<bool> enabled;readonly TimeProvider clock;readonly object sync=new();
    readonly Dictionary<(string,string),(DateTimeOffset Time,string[] Items)> cache=[];
    public WebSuggestions(Func<bool> enabled,HttpMessageHandler? handler=null,TimeProvider? clock=null)
    {
        this.enabled=enabled;this.clock=clock??TimeProvider.System;
        client=new(handler??new HttpClientHandler{UseCookies=false,UseDefaultCredentials=false,Credentials=null,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(1)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
    }
    public async Task<IReadOnlyList<string>> SuggestAsync(SearchEngine engine,string query,CancellationToken cancellationToken)
    {
        if(!enabled()||string.IsNullOrWhiteSpace(query)||cancellationToken.IsCancellationRequested)return [];
        if(!Uri.TryCreate(engine.UrlTemplate,UriKind.Absolute,out var configured))return [];
        string? site=configured.Host.ToLowerInvariant() switch{"bing.com" or "www.bing.com" or "cn.bing.com"=>"bing","baidu.com" or "www.baidu.com"=>"baidu",_=>null};
        if(site==null)return [];
        var key=(site,query);lock(sync)if(cache.TryGetValue(key,out var saved)&&clock.GetUtcNow()-saved.Time<TimeSpan.FromMinutes(1))return saved.Items;
        string[] result;
        try{
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(1));
            string url=site=="bing"?"https://api.bing.com/osjson.aspx?query=":"https://suggestion.baidu.com/su?ie=utf-8&oe=utf-8&action=opensearch&wd=";
            using var response=await client.GetAsync(url+Uri.EscapeDataString(query),timeout.Token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var root=json.RootElement;
            result=root.ValueKind==JsonValueKind.Array&&root.GetArrayLength()>1&&root[1].ValueKind==JsonValueKind.Array?root[1].EnumerateArray().Where(e=>e.ValueKind==JsonValueKind.String).Select(e=>e.GetString()!).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(8).ToArray():[];
        }catch(Exception e) when(e is HttpRequestException or OperationCanceledException or JsonException or IOException){result=[];}
        if(cancellationToken.IsCancellationRequested||!enabled())return [];
        lock(sync){if(cache.Count>=128)cache.Clear();cache[key]=(clock.GetUtcNow(),result);}
        return result;
    }
    public void Dispose()=>client.Dispose();
}
