using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Pane.Contracts;
namespace Pane.Services.Launcher;

internal static class LauncherNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal delegate void AppRow([MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.LPWStr)]string target,[MarshalAs(UnmanagedType.LPWStr)]string args,[MarshalAs(UnmanagedType.LPWStr)]string parsing,int source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal delegate void FileRow([MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string folder,int isFolder);
    [DllImport("Pane.Native.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherEnumerate(AppRow callback);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherResolve(string parsing,StringBuilder target,StringBuilder args,StringBuilder icon,out int index);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherResourceIcon(string path,int index,string output,out int width,out int height);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherIcon(string parsing,string output);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherPackagePath(string id,StringBuilder path,int capacity);
    [DllImport("Pane.Native.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneEverythingVersion([Out]int[] version);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneEverythingQuery(string query,int limit,FileRow callback);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherOpen(string target,int packaged);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherReveal(string path);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherCopy(string text);
    [DllImport("Pane.Native.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern int PaneLauncherCommand(int command);
}
internal sealed record IndexedApp(string Id,SearchName Name,string Launch,bool Packaged,string? Icon) { public string? DarkIcon {get;init;} public string? LightIcon {get;init;} }
internal sealed record IndexMetrics(int AppsFolder,int Shortcuts,int Filtered,int Duplicates,int Total,int Icons,double Milliseconds)
{
    public Dictionary<string,string> Aliases {get;init;}=[];
    public int AcceptedAppsFolder {get;init;}
    public int AcceptedShortcuts {get;init;}
    public int UnresolvedIconMetadata {get;init;}
    public Dictionary<int,int> IconFailures {get;init;}=[];
}
internal sealed record AppIndex(IndexedApp[] Apps,IndexMetrics Metrics);
internal interface IAppCatalog { AppIndex Build(PinyinTable table,CancellationToken token); }
internal sealed class WindowsAppCatalog(string directory,IReadOnlyList<IIconProcessingStep>? steps=null):IAppCatalog
{
    public AppIndex Build(PinyinTable table,CancellationToken token)
    {
        var watch=Stopwatch.StartNew();var apps=new List<IndexedApp>();var candidates=new List<CatalogEntry>();
        int shell=0,links=0,filtered=0,icons=0,unresolvedMetadata=0;Exception? failure=null;var iconFailures=new Dictionary<int,int>();
        var cache=Path.Combine(directory,"launcher-icons");Directory.CreateDirectory(cache);
        LauncherNative.AppRow row=(name,id,target,args,parsing,source)=>{
            if(token.IsCancellationRequested||failure!=null)return;
            try {
                if(source==0)shell++;else links++;
                bool packaged=id.Contains('!');string ext=Path.GetExtension(target);
                if(source==0&&id.Length>0)parsing="shell:AppsFolder\\"+id;
                string lower=(name+" "+Path.GetFileName(target)).ToLowerInvariant();
                if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(parsing)||lower.Contains("uninstall")||lower.Contains("unins0")||lower.Contains("卸载")||lower.Contains("帮助")||lower.Contains("readme")||lower.Contains(" help")||lower.StartsWith("help")||(!packaged&&ext.Length>0&&!new[]{".exe",".com",".msc",".cpl",".lnk"}.Contains(ext,StringComparer.OrdinalIgnoreCase))||source==1&&string.IsNullOrEmpty(target)&&string.IsNullOrEmpty(id)){filtered++;return;}
                string stable=AppDeduplication.StableId(AppDeduplication.Identity(id,target,args,parsing));
                var resolved=new StringBuilder(32768);var arguments=new StringBuilder(32768);var resource=new StringBuilder(32768);
                if(LauncherNative.PaneLauncherResolve(parsing,resolved,arguments,resource,out int iconIndex)==1){if(resolved.Length>0)target=resolved.ToString();args=arguments.ToString();}
                candidates.Add(new(name,id,target,args,parsing,source,stable,resource.ToString(),iconIndex));
            }catch(Exception e){failure=e;}
        };
        int hr=LauncherNative.PaneLauncherEnumerate(row);GC.KeepAlive(row);token.ThrowIfCancellationRequested();if(failure!=null)throw failure;if(hr<0)Marshal.ThrowExceptionForHR(hr);
        var groups=AppDeduplication.Resolve(candidates);var aliases=new Dictionary<string,string>();var evidence=new List<CatalogEvidence>();
        foreach(var group in groups){
            token.ThrowIfCancellationRequested();var app=group.Rows[0];bool packaged=app.Id.Contains('!');
            foreach(var old in group.MergedIds)aliases[old]=group.StableId;
            var packagePath=new StringBuilder(32768);if(packaged)LauncherNative.PaneLauncherPackagePath(app.Id,packagePath,packagePath.Capacity);
            var fingerprint=IconCache.Fingerprint("resource-v2:"+AppDeduplication.Key(app)+":"+app.IconIndex,app.Target,app.Parsing,app.IconResource,packagePath.Length>0?Path.Combine(packagePath.ToString(),"AppxManifest.xml"):"");
            string key=fingerprint.Key;if(!fingerprint.HasMetadata){unresolvedMetadata++;key+="-"+Guid.NewGuid().ToString("N");}
            int width=0,height=0;bool direct=false;
            string? image=IconCache.GetOrCreate(cache,key,output=>{
                int result=LauncherNative.PaneLauncherResourceIcon(app.IconResource,app.IconIndex,output,out width,out height);direct=result==1;
                if(!direct){File.Delete(output);result=LauncherNative.PaneLauncherIcon(app.Parsing,output);}
                if(result==1)File.WriteAllText(Path.Combine(cache,key+".png.source"),$"{width},{height},{direct}");return result;
            },out int iconResult);
            if(image!=null&&File.Exists(image+".source")){var info=File.ReadAllText(image+".source").Split(',');width=int.Parse(info[0]);height=int.Parse(info[1]);direct=bool.Parse(info[2]);}
            if(iconResult!=1)iconFailures[iconResult]=iconFailures.GetValueOrDefault(iconResult)+1;
            if(image!=null){foreach(var step in steps??[])image=step.Process(image,token);icons++;}
            apps.Add(new(group.StableId,table.Create(app.Name),packaged?app.Id:app.Parsing,packaged,image));
            evidence.Add(new(app.Name,AppDeduplication.Key(app),group.Rows.Select(g=>g.Name).ToArray(),app.IconResource,app.IconIndex,width,height,direct));
        }
        File.WriteAllText(Path.Combine(directory,"catalog-evidence.json"),System.Text.Json.JsonSerializer.Serialize(evidence,CatalogEvidenceJson.Default.ListCatalogEvidence));
        return new(apps.ToArray(),new(shell,links,filtered,candidates.Count-groups.Length,apps.Count,icons,watch.Elapsed.TotalMilliseconds){Aliases=aliases,AcceptedAppsFolder=groups.Count(g=>g.Rows[0].Source==0),AcceptedShortcuts=groups.Count(g=>g.Rows[0].Source==1),UnresolvedIconMetadata=unresolvedMetadata,IconFailures=iconFailures});
    }
}
internal interface ILauncherExecutor
{
    Task OpenAsync(string target,bool packaged,CancellationToken token);
    Task RevealAsync(string path,CancellationToken token);
    Task CopyAsync(string value,CancellationToken token);
    Task CommandAsync(string id,CancellationToken token);
}
internal sealed class WindowsLauncherExecutor(Action<string> showPanel):ILauncherExecutor
{
    static Task Run(Func<int> action,CancellationToken token)=>Task.Run(()=>{token.ThrowIfCancellationRequested();if(action()==0)throw new InvalidOperationException("系统未能完成操作");},token);
    public Task OpenAsync(string target,bool packaged,CancellationToken token)=>Run(()=>LauncherNative.PaneLauncherOpen(target,packaged?1:0),token);
    public Task RevealAsync(string path,CancellationToken token)=>Run(()=>LauncherNative.PaneLauncherReveal(path),token);
    public Task CopyAsync(string value,CancellationToken token)=>Run(()=>LauncherNative.PaneLauncherCopy(value),token);
    public Task CommandAsync(string id,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(id is "clipboard" or "settings"){showPanel("show-"+id);return Task.CompletedTask;}
        int command=id switch{"lock"=>0,"sleep"=>1,"restart"=>2,"shutdown"=>3,"recycle"=>4,_=>throw new NotSupportedException(id)};
        return Run(()=>LauncherNative.PaneLauncherCommand(command),token);
    }
}
internal sealed class LauncherFileRows(IReadOnlyList<LauncherItem> rows,int total):List<LauncherItem>(rows) { public int Total {get;}=total; }
internal interface ILauncherFiles { Task<IReadOnlyList<LauncherItem>> SearchAsync(string query,int limit,CancellationToken token); }
internal sealed class EverythingSearch:ILauncherFiles,IFileSearchStatus
{
    readonly SemaphoreSlim gate=new(1);readonly object sync=new();
    public FileSearchState State {get;private set;}=FileSearchState.NotRunning;
    public string? Version {get;private set;}
    public event EventHandler? Changed;
    void Set(FileSearchState state,string? version){bool changed;lock(sync){changed=State!=state||Version!=version;State=state;Version=version;}if(changed)Changed?.Invoke(this,EventArgs.Empty);}
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if(!await gate.WaitAsync(0,cancellationToken))return;
        try {await Task.Run(()=>{cancellationToken.ThrowIfCancellationRequested();var v=new int[4];int result=LauncherNative.PaneEverythingVersion(v);Set(result==1?FileSearchState.Ready:result==0?FileSearchState.NotRunning:FileSearchState.Unsupported,result==1?string.Join('.',v):null);},cancellationToken);}finally{gate.Release();}
    }
    public async Task<IReadOnlyList<LauncherItem>> SearchAsync(string query,int limit,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(query.Length<2||!await gate.WaitAsync(0,token))return [];
        try{
            var page=await new EverythingIpc().QueryAsync(query,0,limit,1,token).ConfigureAwait(false);
            return new LauncherFileRows(page.Items.Select(hit=>new LauncherItem("file:"+hit.Path,hit.Category==FileCategory.Folder?LauncherItemKind.Folder:LauncherItemKind.File,"文件",hit.Name,Path.GetDirectoryName(hit.Path)??"",new(hit.Category==FileCategory.Folder?"folder":"doc"),FileSearch.Highlights(hit.Name,query))).ToArray(),page.Total);
        }catch(FileSearchUnavailableException){Set(FileSearchState.Unsupported,null);return [];}
        finally{gate.Release();}
    }
}
