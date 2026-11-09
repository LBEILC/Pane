using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed record WebSearchRecord(string Text, DateTimeOffset Time);
[JsonSerializable(typeof(Dictionary<string,List<WebSearchRecord>>))]
internal partial class WebHistoryJson : JsonSerializerContext { }

internal sealed class WebSearchHistory : IWebSearchHistory, IDisposable
{
    readonly Func<bool> enabled;
    readonly string? path;
    readonly object sync=new();
    readonly TimeProvider clock;
    Dictionary<string,List<WebSearchRecord>> history=[];
    Task pending=Task.CompletedTask;
    bool disposed;
    public WebSearchHistory(SettingsStore store):this(()=>store.Current.Launcher.RememberWebSearches,store.DirectoryPath) { }
    internal WebSearchHistory(Func<bool> enabled,string? directory=null,TimeProvider? clock=null)
    {
        this.enabled=enabled;this.clock=clock??TimeProvider.System;
        if(directory==null)return;
        path=Path.Combine(directory,"web-search-history.json");
        try{if(File.Exists(path))history=Normalize(JsonSerializer.Deserialize(File.ReadAllText(path),WebHistoryJson.Default.DictionaryStringListWebSearchRecord)??[]);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException){ }
    }
    internal static bool Valid(string? text)=>!string.IsNullOrWhiteSpace(text)&&!char.IsWhiteSpace(text[0])&&text.Trim().Length<=200;
    internal static Dictionary<string,List<WebSearchRecord>> Normalize(Dictionary<string,List<WebSearchRecord>> records)=>records.ToDictionary(p=>p.Key,p=>(p.Value??[]).Where(r=>r!=null&&Valid(r.Text)).Select(r=>r with{Text=r.Text.Trim()}).OrderByDescending(r=>r.Time).DistinctBy(r=>r.Text,StringComparer.OrdinalIgnoreCase).Take(50).ToList(),StringComparer.Ordinal);
    public void Record(SearchEngine engine,string text)
    {
        if(!enabled()||!Valid(text))return;
        lock(sync){if(disposed)return;var list=history.GetValueOrDefault(engine.UrlTemplate)??(history[engine.UrlTemplate]=[]);text=text.Trim();list.RemoveAll(r=>r.Text.Equals(text,StringComparison.OrdinalIgnoreCase));list.Insert(0,new(text,clock.GetUtcNow()));if(list.Count>50)list.RemoveRange(50,list.Count-50);Save();}
    }
    public IReadOnlyList<string> Recent(SearchEngine engine,string prefix,int max)
    {
        lock(sync)return !enabled()||max<=0?[]:(history.GetValueOrDefault(engine.UrlTemplate)??[]).Where(r=>prefix.Length==0||r.Text.Contains(prefix,StringComparison.OrdinalIgnoreCase)&&!r.Text.Equals(prefix,StringComparison.OrdinalIgnoreCase)).OrderByDescending(r=>r.Text.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).Take(max).Select(r=>r.Text).ToArray();
    }
    public int Count {get{lock(sync)return history.Sum(p=>p.Value.Count);}}
    public void Remove(SearchEngine engine,string text){lock(sync){if(disposed)return;if(history.TryGetValue(engine.UrlTemplate,out var list)){list.RemoveAll(r=>r.Text.Equals(text,StringComparison.OrdinalIgnoreCase));if(list.Count==0)history.Remove(engine.UrlTemplate);Save();}}}
    public void Clear(){lock(sync){if(disposed)return;history.Clear();Save();}}
    void Save()
    {
        if(path==null)return;
        var snapshot=history.ToDictionary(p=>p.Key,p=>p.Value.ToList());
        pending=pending.ContinueWith(_=>{
            try{Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path+".tmp",JsonSerializer.Serialize(snapshot,WebHistoryJson.Default.DictionaryStringListWebSearchRecord));File.Move(path+".tmp",path,true);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){ }
        },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
    }
    internal Task Completion {get{lock(sync)return pending;}}
    public void Dispose(){Task task;lock(sync){disposed=true;task=pending;}task.GetAwaiter().GetResult();}

    // 现有界面只传最终 URL；按模板的固定前后缀还原实际搜索词，避免依赖显示文案。
    internal void RecordUrl(IEnumerable<SearchEngine> engines,string url)
    {
        foreach(var engine in engines.OrderByDescending(e=>e.UrlTemplate.Length)){
            int at=engine.UrlTemplate.IndexOf("{query}",StringComparison.Ordinal);if(at<0)continue;
            string before=engine.UrlTemplate[..at],after=engine.UrlTemplate[(at+7)..];
            if(url.Length<before.Length+after.Length||!url.StartsWith(before,StringComparison.Ordinal)||!url.EndsWith(after,StringComparison.Ordinal))continue;
            string text=Uri.UnescapeDataString(url.Substring(before.Length,url.Length-before.Length-after.Length));
            if(engine.UrlTemplate.Replace("{query}",Uri.EscapeDataString(text),StringComparison.Ordinal)==url){Record(engine,text);return;}
        }
    }
}
