using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed partial class EngineIcons : IEngineIcons, IDisposable
{
    const int Limit=512*1024;
    readonly string directory;
    readonly bool network;
    readonly HttpClient client;
    readonly TimeProvider clock;
    readonly object sync=new();
    readonly Dictionary<string,Task> pending=[];
    readonly Dictionary<string,DateTimeOffset> failures=[];
    CancellationTokenSource lifetime=new();
    bool disposed,clearing;
    public event EventHandler<string>? Ready;
    internal EngineIcons(string directory,bool network=true,HttpMessageHandler? handler=null,TimeProvider? clock=null)
    {
        this.directory=Path.Combine(directory,"engine-icons");this.network=network;this.clock=clock??TimeProvider.System;
        client=new(handler??new HttpClientHandler{UseCookies=false,UseDefaultCredentials=false,Credentials=null,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(3)};
    }
    internal static EngineIcon? BuiltIn(string host)
    {
        host=host.ToLowerInvariant();string? name=host switch{
            "bing.com" or "www.bing.com" or "cn.bing.com"=>"bing",
            "baidu.com" or "www.baidu.com"=>"baidu",
            "github.com"=>"github",
            _ when host=="bilibili.com"||host.EndsWith(".bilibili.com",StringComparison.Ordinal)=>"bilibili",
            _ when GoogleDomain(host)=>"google",_=>null};
        return name==null?null:new(Path.Combine(AppContext.BaseDirectory,"Assets","Engines",name+".png"),name is "baidu" or "github"?EngineIconKind.Monochrome:EngineIconKind.Color,name=="baidu"?"#2932E1":null);
    }
    static bool GoogleDomain(string host)
    {
        if(host.StartsWith("www.",StringComparison.Ordinal))host=host[4..];
        if(!host.StartsWith("google.",StringComparison.Ordinal))return false;
        var suffix=host[7..].Split('.');
        return suffix.Length==1&&(suffix[0]=="com"||suffix[0].Length==2)||suffix.Length==2&&(suffix[0] is "com" or "co")&&suffix[1].Length==2;
    }
    public EngineIcon? TryGet(SearchEngine engine)
    {
        if(!Uri.TryCreate(engine.UrlTemplate,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https"))return null;
        if(BuiltIn(uri.IdnHost) is {} builtIn)return builtIn;
        if(!network)return null;
        string key=engine.UrlTemplate,path=Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))+".png");
        lock(sync){
            if(disposed||clearing)return null;
            if(File.Exists(path))return new(path,EngineIconKind.Color);
            if(pending.ContainsKey(key))return null;
            if(failures.TryGetValue(key,out var remembered)&&clock.GetUtcNow()-remembered<TimeSpan.FromDays(1))return null;
            if(File.Exists(path+".failed")){
                if(!failures.TryGetValue(key,out var time))try{time=new DateTimeOffset(File.GetLastWriteTimeUtc(path+".failed"));failures[key]=time;}catch(IOException){ }
                if(clock.GetUtcNow()-time<TimeSpan.FromDays(1))return null;
            }
            var token=lifetime.Token;pending[key]=Task.Run(()=>Fetch(key,uri,path,token));return null;
        }
    }
    async Task<byte[]> Download(Uri uri,CancellationToken token)
    {
        if(uri.Scheme is not("http" or "https"))throw new IOException();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>Limit)throw new IOException();
        using var input=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);using var output=new MemoryStream();var buffer=new byte[8192];
        int count;while((count=await input.ReadAsync(buffer,timeout.Token).ConfigureAwait(false))>0){if(output.Length+count>Limit)throw new IOException();output.Write(buffer,0,count);}return output.ToArray();
    }
    [GeneratedRegex(@"<link\b[^>]*>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)]
    private static partial Regex Links();
    [GeneratedRegex("([a-zA-Z][a-zA-Z0-9_-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",RegexOptions.CultureInvariant)]
    private static partial Regex Attributes();
    internal static Uri[] Candidates(Uri home,string html)
    {
        var candidates=new List<(Uri Uri,int Rank)>();
        foreach(Match link in Links().Matches(html)){
            var attrs=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match attr in Attributes().Matches(link.Value))attrs[attr.Groups[1].Value]=WebUtility.HtmlDecode(attr.Groups[2].Success?attr.Groups[2].Value:attr.Groups[3].Success?attr.Groups[3].Value:attr.Groups[4].Value);
            var rel=attrs.GetValueOrDefault("rel","").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(!rel.Any(s=>s.Equals("icon",StringComparison.OrdinalIgnoreCase)||s.Equals("apple-touch-icon",StringComparison.OrdinalIgnoreCase))||!attrs.TryGetValue("href",out var href)||!Uri.TryCreate(home,href,out var uri)||uri.Scheme is not("http" or "https"))continue;
            bool png=attrs.GetValueOrDefault("type","").Equals("image/png",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath.EndsWith(".png",StringComparison.OrdinalIgnoreCase);
            bool large=attrs.GetValueOrDefault("sizes","").Split(' ').Any(s=>{var parts=s.Split('x','X');return parts.Length==2&&int.TryParse(parts[0],out int w)&&int.TryParse(parts[1],out int h)&&w>=32&&h>=32;});
            candidates.Add((uri,(png&&large?4:0)+(png?2:0)+(large?1:0)));
        }
        return candidates.OrderByDescending(c=>c.Rank).Select(c=>c.Uri).Distinct().Take(16).ToArray();
    }
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
    static extern int PaneEngineIconConvert(string path,string output);
    async Task Fetch(string key,Uri configured,string path,CancellationToken token)
    {
        bool ready=false;string input=path+".download",output=path+".tmp";
        try{
            var home=new Uri(configured.GetLeftPart(UriPartial.Authority)+"/");Uri[] candidates=[];
            try{candidates=Candidates(home,Encoding.UTF8.GetString(await Download(home,token).ConfigureAwait(false)));}
            catch(Exception e) when(e is HttpRequestException or IOException or OperationCanceledException){token.ThrowIfCancellationRequested();}
            foreach(var candidate in candidates.Append(new Uri(home,"favicon.ico")).Distinct()){
                try{
                    var bytes=await Download(candidate,token).ConfigureAwait(false);Directory.CreateDirectory(directory);await File.WriteAllBytesAsync(input,bytes,token).ConfigureAwait(false);
                    if(PaneEngineIconConvert(input,output)!=1)continue;
                    token.ThrowIfCancellationRequested();File.Move(output,path,true);File.Delete(path+".failed");ready=true;break;
                }catch(Exception e) when(e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException){token.ThrowIfCancellationRequested();}
            }
            if(!ready){var time=clock.GetUtcNow();lock(sync)failures[key]=time;Directory.CreateDirectory(directory);File.WriteAllText(path+".failed","");File.SetLastWriteTimeUtc(path+".failed",time.UtcDateTime);}
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or OperationCanceledException){ }
        finally{
            foreach(string temp in new[]{input,output})try{File.Delete(temp);}catch(IOException){ }catch(UnauthorizedAccessException){ }
            lock(sync)pending.Remove(key);
        }
        if(ready&&!token.IsCancellationRequested)foreach(EventHandler<string> handler in Ready?.GetInvocationList()??[])try{handler(this,key);}catch(Exception){ }
    }
    internal Task Completion {get{lock(sync)return Task.WhenAll(pending.Values.ToArray());}}
    internal async Task ClearCacheAsync(Action clear,CancellationToken token)
    {
        Task work;CancellationTokenSource previous;
        lock(sync){clearing=true;previous=lifetime;work=Task.WhenAll(pending.Values.ToArray());}
        previous.Cancel();
        try{await work.ConfigureAwait(false);token.ThrowIfCancellationRequested();clear();}
        finally{lock(sync){failures.Clear();if(!disposed)lifetime=new();clearing=false;}previous.Dispose();}
    }
    public void Dispose(){lock(sync){if(disposed)return;disposed=true;}lifetime.Cancel();client.Dispose();}
}
