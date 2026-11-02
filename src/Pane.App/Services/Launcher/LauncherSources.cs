using Pane.Localization;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed record SourceContext(string Query,LauncherSnapshot Snapshot,LauncherSettings Settings,IReadOnlyDictionary<string,AppUse> Usage);
// 来源可以封装配置或进程通信；汇总者隔离失败和超时，不要求同步完成。
internal interface ILauncherSource
{
    string Group {get;}
    int Order {get;}
    int Limit(SourceContext context);
    TimeSpan Timeout {get;}
    Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context,CancellationToken token);
    bool Owns(LauncherItem item);
    Task ExecuteAsync(LauncherItem item,SourceContext context,CancellationToken token);
}
internal abstract class LauncherSource(string group,int order,string prefix):ILauncherSource
{
    public string Group=>group;
    public int Order=>order;
    public virtual TimeSpan Timeout=>TimeSpan.FromSeconds(2);
    public abstract int Limit(SourceContext context);
    public bool Owns(LauncherItem item)=>item.Id.StartsWith(prefix,StringComparison.Ordinal);
    public abstract Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context,CancellationToken token);
    public abstract Task ExecuteAsync(LauncherItem item,SourceContext context,CancellationToken token);
    protected static Task<IReadOnlyList<LauncherItem>> Rows(IEnumerable<LauncherItem> rows)=>Task.FromResult<IReadOnlyList<LauncherItem>>(rows.ToArray());
}
internal sealed class ApplicationSource(ILauncherExecutor executor,Action<string> used):LauncherSource(LauncherGroups.Applications,1,"app:")
{
    public override int Limit(SourceContext c)=>14;
    static LauncherItem Item(IndexedApp app,string group)=>new(app.Id,LauncherItemKind.App,group,app.Name.Name,Strings.Launcher_ApplicationSubtitle,new("app",app.Icon));
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token){
        token.ThrowIfCancellationRequested();
        if(c.Query.Length==0){
            var home=c.Snapshot.Apps.Where(a=>c.Usage.ContainsKey(a.Id)).OrderByDescending(a=>c.Usage[a.Id].LastUsed).Take(8).Select(a=>Item(a,LauncherGroups.Recent)).ToList();
            var byId=c.Snapshot.Apps.ToDictionary(a=>a.Id);var pinned=new HashSet<string>();foreach(var id in c.Settings.PinnedApps)if(byId.TryGetValue(c.Snapshot.Aliases.GetValueOrDefault(id,id),out var app)&&pinned.Add(app.Id))home.Add(Item(app,LauncherGroups.Pinned));
            return Rows(home);
        }
        var hits=new List<(IndexedApp App,NameMatch Match)>();
        foreach(var app in c.Snapshot.Apps){token.ThrowIfCancellationRequested();if(app.Name.Match(c.Query) is {} match)hits.Add((app,match));}
        return Rows(hits.OrderByDescending(x=>x.Match.Score).ThenByDescending(x=>c.Usage.GetValueOrDefault(x.App.Id)?.Count??0).ThenByDescending(x=>c.Usage.GetValueOrDefault(x.App.Id)?.LastUsed??DateTimeOffset.MinValue).ThenBy(x=>x.App.Name.Name,StringComparer.OrdinalIgnoreCase).Take(Limit(c)).Select(x=>Item(x.App,Group) with{Highlights=x.Match.Spans}));
    }
    public override async Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token){
        var app=c.Snapshot.Apps.FirstOrDefault(a=>a.Id==item.Id)??throw new InvalidOperationException(Strings.Launcher_AppMissing);
        await executor.OpenAsync(app.Launch,app.Packaged,token).ConfigureAwait(false);used(app.Id);
    }
}
internal abstract class EntrySource(string group,int order,string prefix):LauncherSource(group,order,prefix)
{
    public override int Limit(SourceContext c)=>3;
    protected abstract SearchEntry[] Entries(SourceContext c);
    protected virtual NameMatch? Match(SearchEntry entry,string query)=>entry.Name.Match(query);
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token){
        token.ThrowIfCancellationRequested();if(c.Query.Length==0)return Rows([]);
        return Rows(Entries(c).Select(e=>(Entry:e,Match:Match(e,c.Query))).Where(x=>x.Match!=null).OrderByDescending(x=>x.Match!.Score).Take(Limit(c)).Select(x=>new LauncherItem(x.Entry.Id,x.Entry.Kind,Group,x.Entry.Name.Name,x.Entry.Subtitle,new(x.Entry.Glyph),x.Match!.Spans)));
    }
}
internal sealed class FavoriteSource(ILauncherExecutor executor):EntrySource(LauncherGroups.Favorites,2,"favorite:")
{
    protected override SearchEntry[] Entries(SourceContext c)=>c.Snapshot.Favorites;
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>executor.OpenAsync(item.Id[9..],false,token);
}
internal sealed class CommandSource(ILauncherExecutor executor):EntrySource(LauncherGroups.Commands,4,"command:")
{
    static readonly SearchEntry Rename=new("rename",LauncherItemKind.Command,new PinyinTable().CreateLocalized(Strings.Launcher_CommandRename, Strings.Simplified(nameof(Strings.Launcher_CommandRename))),Strings.Launcher_CommandSubtitle,"pencil");
    static readonly SearchName EnglishRename=new("rename","rename".Select((c,i)=>new NameToken(i,1,[c.ToString()],[c.ToString()])).ToArray());
    protected override SearchEntry[] Entries(SourceContext c)=>[..c.Snapshot.Commands.Where(e=>e.Id!="rename").Select(LocalizeCommand),Rename];
    static SearchEntry LocalizeCommand(SearchEntry entry)
    {
        string title = entry.Id switch
        {
            "command:lock" => Strings.Launcher_CommandLock,
            "command:sleep" => Strings.Launcher_CommandSleep,
            "command:restart" => Strings.Launcher_CommandRestart,
            "command:shutdown" => Strings.Launcher_CommandShutdown,
            "command:recycle" => Strings.Launcher_CommandEmptyRecycleBin,
            "command:clipboard" => Strings.Launcher_CommandClipboard,
            "command:settings" => Strings.Launcher_CommandSettings,
            _ => entry.Name.Name
        };
        return entry with { Name = entry.Name.WithDisplayName(title), Subtitle = entry.Id is "command:clipboard" or "command:settings" ? "Pane" : Strings.Launcher_CommandSubtitle };
    }
    protected override NameMatch? Match(SearchEntry entry,string query)=>base.Match(entry,query)??(entry.Id=="rename"&&EnglishRename.Match(query) is {} alias?alias with{Spans=[]}:null);
    // TODO(design): 关机、重启由 LauncherView 在调用 ExecuteAsync 前确认（沿用 013）。
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>executor.CommandAsync(item.Id[8..],token);
}
internal sealed class FileSource(ILauncherFiles files,ILauncherExecutor executor):LauncherSource(LauncherGroups.Files,3,"file:")
{
    public override int Limit(SourceContext c)=>3;
    public override TimeSpan Timeout=>TimeSpan.FromMilliseconds(150);
    public override async Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token){
        token.ThrowIfCancellationRequested();if(c.Query.Length<2)return [];
        try{var rows=await files.SearchAsync(c.Query,Limit(c),token).WaitAsync(Timeout,token).ConfigureAwait(false);return new LauncherFileRows(rows.Take(Limit(c)).ToArray(),rows is LauncherFileRows page?page.Total:rows.Count);}catch(TimeoutException){return [];}
    }
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>executor.OpenAsync(item.Id[5..],false,token);
}
internal sealed class CalculationSource(ILauncherExecutor executor):LauncherSource(LauncherGroups.Calculation,0,"calc:")
{
    public override int Limit(SourceContext c)=>1;
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token){token.ThrowIfCancellationRequested();var value=Calculator.Calc(c.Query);return Rows(value==null?[]:[new("calc:"+value,LauncherItemKind.Calculation,Group,value,c.Query+" =",new("equal"))]);}
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>executor.CopyAsync(item.Id[5..],token);
}
internal sealed class AiSource:LauncherSource
{
    public AiSource():base(LauncherGroups.Ai,5,"ai:"){}
    public override int Limit(SourceContext c)=>2;
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token){
        token.ThrowIfCancellationRequested();return Rows(c.Query.Length==0?[]:[new("ai:translate",LauncherItemKind.Ai,Group,string.Format(Strings.Launcher_TranslateQuery, (object?)(c.Query)),Strings.Launcher_TranslateDescription,new("translate")),new("ai:ask",LauncherItemKind.Ai,Group,string.Format(Strings.Launcher_AskQuery, (object?)(c.Query)),Strings.Launcher_AskDescription,new("ai"),Hotkey:"Tab")]);
    }
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>throw new NotSupportedException(item.Id);
}
internal sealed class WebSource(ILauncherExecutor executor):LauncherSource(LauncherGroups.Web,6,"web:")
{
    public override int Limit(SourceContext c)=>2;
    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token){
        token.ThrowIfCancellationRequested();if(c.Query.Length==0)return Rows([]);var rows=new List<LauncherItem>();int space=c.Query.IndexOf(' ');
        if(space>0&&c.Settings.SearchEngines.FirstOrDefault(e=>string.Equals(e.Keyword,c.Query[..space],StringComparison.OrdinalIgnoreCase)) is {} engine)Add(engine,c.Query[(space+1)..].Trim());
        Add(LauncherSearch.DefaultEngine(c.Settings),c.Query);return Rows(rows);
        void Add(SearchEngine engine,string text)=>rows.Add(new("web:"+LauncherSearch.BuildUrl(engine,text),LauncherItemKind.Web,Group,string.Format(Strings.Launcher_SearchQuery, (object?)(engine.Name), (object?)(text)),engine.Name,new("search")));
    }
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)=>executor.OpenAsync(item.Id[4..],false,token);
}
