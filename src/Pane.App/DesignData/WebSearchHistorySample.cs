using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;
namespace Pane.DesignData;

internal sealed class WebSearchHistorySample : IWebSearchHistory
{
    readonly WebSearchHistory history;
    internal WebSearchHistorySample(Func<bool> enabled)
    {
        history=new(enabled);
        history.Record(new("Google","g","https://www.google.com/search?q={query}"),"天气");
    }
    public void Record(SearchEngine engine,string text)=>history.Record(engine,text);
    public IReadOnlyList<string> Recent(SearchEngine engine,string prefix,int max)=>history.Recent(engine,prefix,max);
    public void Remove(SearchEngine engine,string text)=>history.Remove(engine,text);
    public void Clear()=>history.Clear();
    public int Count=>history.Count;
}
