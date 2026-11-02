using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed record AppUse(int Count,DateTimeOffset LastUsed);
[JsonSerializable(typeof(Dictionary<string,AppUse>))]
internal partial class LauncherJson:JsonSerializerContext { }
internal sealed record SearchEntry(string Id,LauncherItemKind Kind,SearchName Name,string Subtitle,string Glyph);
internal sealed record LauncherSnapshot(IndexedApp[] Apps,SearchEntry[] Favorites,SearchEntry[] Commands)
{
    public static readonly LauncherSnapshot Empty=new([],[],[]);
    public string? IconSettingsKey {get;init;}
    public Dictionary<string,string> Aliases {get;init;}=[];
}
internal sealed partial class LauncherSearch:ILauncherItemActions,ILauncherSearch,ILauncherAttachmentActions,IAppIconCatalog,IDisposable
{
    readonly TerminalService terminal;
    public ITerminalHistory TerminalHistory=>terminal.History;
    readonly SettingsStore store;readonly ILauncherIndex index;readonly ILauncherSource[] sources;readonly ILauncherExecutor executor;
    readonly object sync=new();readonly CancellationTokenSource lifetime=new();
    LauncherSnapshot snapshot=LauncherSnapshot.Empty;
    Dictionary<string,AppUse> uses=[];Task indexing=Task.CompletedTask;bool dirty=true,disposed;DateTimeOffset lastBuilt;
    FavoriteEntry[] lastFavorites;
    string lastIcons="";
    public IndexMetrics? Metrics {get;private set;}
    public string? IndexError {get;private set;}
    public Task Indexing {get {lock(sync)return indexing;}}
    public bool IndexReady=>Metrics!=null;
    internal ProcessLauncherIndex? ProcessIndex=>index as ProcessLauncherIndex;
    internal double CachedReadyMilliseconds {get;private set;}
    internal int CachedApps {get;private set;}
    internal string? CachedQuery {get;private set;}
    // 保留宿主已有接线；Shell 枚举只在子进程中调用。
    public LauncherSearch(SettingsStore store,WindowsAppCatalog catalog,ILauncherFiles files,ILauncherExecutor executor)
        :this(store,new ProcessLauncherIndex(store.DirectoryPath),files,executor) { }
    public LauncherSearch(SettingsStore store,ILauncherIndex index,ILauncherFiles files,ILauncherExecutor executor,ILauncherEnvironment? environment=null,ILaunchProcess? process=null,TerminalPrograms? programs=null,LauncherActionPlatform? actionPlatform=null)
    {
        var started=System.Diagnostics.Stopwatch.StartNew();
        this.store=store;this.index=index;this.executor=executor;lastFavorites=store.Current.Launcher.Favorites.ToArray();
        environment??=new LauncherEnvironment();process??=new LaunchProcess();
        terminal=new(store,environment,process,programs);
        actions=new(actionPlatform??new WindowsLauncherActionPlatform(),executor,process);
        sources=[new SystemSettingsSource(executor,process,environment,RecordUse),new ExtraCommandSource(executor,environment),new CalculationSource(executor),new ApplicationSource(executor,RecordUse),new FavoriteSource(executor),new FileSource(files,executor),new CommandSource(executor),new WebSource(executor)];
        var path=Path.Combine(store.DirectoryPath,"launcher-usage.json");
        try{if(File.Exists(path))uses=JsonSerializer.Deserialize(File.ReadAllText(path),LauncherJson.Default.DictionaryStringAppUse)??[];}
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){IndexError="usage:"+e.GetType().Name;}
        try{if(index.Load() is {} saved){Publish(saved);CachedApps=saved.Snapshot.Apps.Length;CachedQuery=saved.Snapshot.Apps.FirstOrDefault()?.Name.Name;CachedReadyMilliseconds=started.Elapsed.TotalMilliseconds;}}
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException){IndexError="snapshot:"+e.GetType().Name;}
        lastIcons=IconsStamp();if(index is ProcessLauncherIndex processIndex){processIndex.IconSettingsProvider=()=>store.Current.Icons;processIndex.OriginalsReady+=PublishOriginals;}
        store.Changed+=SettingsChanged;RefreshIndex();
    }
    string IconsStamp()=>Pane.Services.Icons.IconProcessing.SettingsKey(store.Current.Icons);
    void PublishOriginals(PersistedIndex value){if(Volatile.Read(ref snapshot).Apps.Length==0)Publish(value);}
    void SettingsChanged(object? sender,EventArgs args){lock(sync){var current=store.Current.Launcher.Favorites;var icons=IconsStamp();if(lastFavorites.SequenceEqual(current)&&lastIcons==icons)return;lastIcons=icons;lastFavorites=current.ToArray();dirty=true;}RefreshIndex();}
    public void RefreshIndex()
    {
        lock(sync){
            if(disposed||!indexing.IsCompleted||!dirty&&DateTimeOffset.UtcNow-lastBuilt<TimeSpan.FromMinutes(2))return;
            dirty=false;var favorites=lastFavorites.ToArray();building=true;
            indexing=Task.Run(async()=>{
                try{
                    Publish(await index.BuildAsync(favorites,lifetime.Token).ConfigureAwait(false));
                    // 构建阶段结束后压缩临时对象占过的 GC 段并归还空段；不修剪进程工作集。
                    // 在后台线程执行，不等待需要回到 UI 线程的终结器。
                    GC.Collect(2,GCCollectionMode.Aggressive,true,true);
                }
                catch(OperationCanceledException) when(!lifetime.IsCancellationRequested){IndexError="Timeout";lastBuilt=DateTimeOffset.UtcNow;}
                catch(OperationCanceledException){}
                catch(Exception e){IndexError=e.GetType().Name;lastBuilt=DateTimeOffset.UtcNow;}
                finally{
                    lock(sync){if(dirty&&!disposed){
                        // 设置变更发生在构建中时，等本轮结束再刷新。
                        _=indexing.ContinueWith(_=>RefreshIndex(),CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
                    }else building=false;}
                    if(!disposed)IconsChanged?.Invoke(this,EventArgs.Empty);
                }
            });
        }
    }
    void Publish(PersistedIndex value){
        lock(sync){
            var migrated=new Dictionary<string,AppUse>(uses);
            if(AppDeduplication.MergeUsage(migrated,value.Metrics.Aliases)){
                Directory.CreateDirectory(store.DirectoryPath);var path=Path.Combine(store.DirectoryPath,"launcher-usage.json");
                File.WriteAllText(path+".tmp",JsonSerializer.Serialize(migrated,LauncherJson.Default.DictionaryStringAppUse));File.Move(path+".tmp",path,true);uses=migrated;
            }
            Volatile.Write(ref snapshot,value.Snapshot with{Aliases=value.Metrics.Aliases});Metrics=value.Metrics;lastBuilt=value.BuiltAt;IndexError=null;
        }
    }
    // ---- 设置页「应用图标」（设计加，IAppIconCatalog）----
    volatile bool building;
    readonly Dictionary<string,AppIconKind> kinds=new(StringComparer.OrdinalIgnoreCase);
    event EventHandler? IconsChanged;
    event EventHandler? IAppIconCatalog.Changed{add=>IconsChanged+=value;remove=>IconsChanged-=value;}
    public bool Rebuilding{get{lock(sync)return building||dirty;}}
    public IReadOnlyList<AppIconInfo> Apps(){
        var s=Volatile.Read(ref snapshot);bool dark=Pane.Services.Icons.IconTheme.IsDark(store.Current.General.Theme);
        return s.Apps.Select(a=>{var masked=dark?a.DarkIcon:a.LightIcon;var path=masked!=null&&File.Exists(masked)?masked:a.Icon;return new AppIconInfo(a.Id,a.Name.Name,path,masked==null?AppIconKind.Unknown:Kind(a.DarkIcon!));})
            .OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    // 分类写在遮罩结果旁的 .kind 文件里（IconProcessing）；按路径缓存，路径随设置和图标指纹变化
    AppIconKind Kind(string dark){
        lock(kinds){
            if(kinds.TryGetValue(dark,out var k))return k;
            try{k=File.Exists(dark+".kind")&&Enum.TryParse<AppIconKind>(File.ReadAllText(dark+".kind").Trim(),out var v)?v:AppIconKind.Unknown;}catch(IOException){k=AppIconKind.Unknown;}
            if(kinds.Count>2000)kinds.Clear();kinds[dark]=k;return k;
        }
    }
    SourceContext Context(string query){Dictionary<string,AppUse> usage;lock(sync)usage=new(uses);return new(query.Trim(),Volatile.Read(ref snapshot),store.Current.Launcher,usage);}
    public async Task<LauncherResult> SearchAsync(string query,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();if(query.StartsWith('>'))return terminal.Search(query);RefreshIndex();var context=Context(query);
        var pending=sources.Where(s=>s is not WebSource).OrderBy(s=>s.Order).Select(s=>Task.Run(()=>SearchSource(s,context,cancellationToken),cancellationToken)).ToArray();
        var groups=await Task.WhenAll(pending).ConfigureAwait(false);cancellationToken.ThrowIfCancellationRequested();
        var rows=groups.SelectMany(g=>g).Where(item=>store.Current.Clipboard.Enabled||item.Id!="command:clipboard").ToArray();
        bool dark=Pane.Services.Icons.IconTheme.IsDark(store.Current.General.Theme);
        var apps=context.Snapshot.Apps.ToDictionary(a=>a.Id);
        if(context.Snapshot.IconSettingsKey==lastIcons)
            for(int i=0;i<rows.Length;i++)if(apps.TryGetValue(rows[i].Id,out var app)){var path=dark?app.DarkIcon:app.LightIcon;if(path!=null&&File.Exists(path))rows[i]=rows[i] with{Icon=new("app",path)};}
        return new(rows,context.Query.Length==0,groups.OfType<LauncherFileRows>().FirstOrDefault()?.Total??0);
    }
    static async Task<IReadOnlyList<LauncherItem>> SearchSource(ILauncherSource source,SourceContext context,CancellationToken token){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(source.Timeout);
        try{return await source.SearchAsync(context,timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(!token.IsCancellationRequested){return [];}
        catch(Exception) when(!token.IsCancellationRequested){return [];}
    }
    internal static SearchEngine DefaultEngine(LauncherSettings settings)=>settings.SearchEngines.First(e=>string.Equals(e.Keyword,settings.DefaultEngine,StringComparison.OrdinalIgnoreCase));
    internal static string BuildUrl(SearchEngine engine,string text)=>engine.UrlTemplate.Replace("{query}",Uri.EscapeDataString(text),StringComparison.Ordinal);
    public Task ExecuteAsync(LauncherItem item,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();if(item.Id=="rename")return Task.CompletedTask;if(TerminalService.Owns(item))return terminal.ExecuteAsync(item,false,cancellationToken);var source=sources.FirstOrDefault(s=>s.Owns(item))??throw new NotSupportedException(item.Id);
        return source.ExecuteAsync(item,Context(""),cancellationToken);
    }
    public Task ExecuteElevatedAsync(LauncherItem item,CancellationToken token)=>TerminalService.Owns(item)?terminal.ExecuteAsync(item,true,token):item.Kind==LauncherItemKind.App?ExecuteAppElevatedAsync(item,token):ExecuteAsync(item,token);
    void RecordUse(string id){
        lock(sync){var use=uses.GetValueOrDefault(id);uses[id]=new(Math.Min((use?.Count??0),int.MaxValue-1)+1,DateTimeOffset.UtcNow);Directory.CreateDirectory(store.DirectoryPath);var path=Path.Combine(store.DirectoryPath,"launcher-usage.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(uses,LauncherJson.Default.DictionaryStringAppUse));File.Move(path+".tmp",path,true);}
    }
    public async Task RunAsync(string actionId,ClipEntry attachment,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(attachment.Kind==ClipKind.Image)throw new NotSupportedException("图片附件动作将在 014 实现"); // 不翻译：异常
        if(attachment.Kind==ClipKind.Files&&attachment.Files is {Count:>0} paths){
            switch(actionId){
                case "copy-path":await executor.CopyAsync(string.Join(Environment.NewLine,paths),cancellationToken);return;
                case "open":foreach(var path in paths)await executor.OpenAsync(path,false,cancellationToken);return;
                case "reveal":foreach(var path in paths)await executor.RevealAsync(path,cancellationToken);return;
            }
        }
        if(actionId=="web-search"&&attachment.Text!=null){await executor.OpenAsync(BuildUrl(DefaultEngine(store.Current.Launcher),attachment.Text),false,cancellationToken);return;}
        throw new NotSupportedException(actionId);
    }
    public void Dispose(){lock(sync){if(disposed)return;disposed=true;store.Changed-=SettingsChanged;if(index is ProcessLauncherIndex processIndex)processIndex.OriginalsReady-=PublishOriginals;lifetime.Cancel();}indexing.GetAwaiter().GetResult();terminal.Dispose();lifetime.Dispose();}
}
