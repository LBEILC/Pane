using Pane.Contracts;
using Pane.Localization;
namespace Pane.Services.Launcher;

internal sealed class DirectSource(ILauncherExecutor executor):LauncherSource(LauncherGroups.Direct,-1,"direct:")
{
    // 可替换的后台探测仅用于测试超时；不改变界面契约。
    internal Func<string,LauncherItemKind?> Probe {get;set;}=path=>Directory.Exists(path)?LauncherItemKind.Folder:File.Exists(path)?LauncherItemKind.File:null;
    public override int Limit(SourceContext c)=>1;
    public override TimeSpan Timeout=>TimeSpan.FromMilliseconds(300);
    internal static string? Url(string text)
    {
        if(text.Any(char.IsWhiteSpace))return null;
        if(text.StartsWith("www.",StringComparison.OrdinalIgnoreCase))text="https://"+text;
        if(!text.StartsWith("https://",StringComparison.OrdinalIgnoreCase)&&!text.StartsWith("http://",StringComparison.OrdinalIgnoreCase))return null;
        return Uri.TryCreate(text,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.Host.Length>0?uri.AbsoluteUri:null;
    }
    internal static string? LocalPath(string text)
    {
        try{
            text=Environment.ExpandEnvironmentVariables(text).Replace('/','\\');
            if(text.Length<3||!char.IsAsciiLetter(text[0])||text[1]!=':'||text[2]!='\\'||text.IndexOf(':',2)>=0)return null;
            return Path.GetFullPath(text);
        }catch(Exception e) when(e is ArgumentException or NotSupportedException or IOException){return null;}
    }
    public override async Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(Url(c.Query) is {} url)return [new("direct:url:"+url,LauncherItemKind.Web,Group,url,Strings.Launcher_DirectOpenUrl,new("globe"))];
        if(LocalPath(c.Query) is not {} path)return [];
        try{
            var kind=await Task.Run(()=>Probe(path),token).WaitAsync(Timeout,token).ConfigureAwait(false);
            return kind is LauncherItemKind.Folder or LauncherItemKind.File
                ?[new("direct:path:"+path,kind.Value,Group,path,kind==LauncherItemKind.Folder?Strings.Launcher_DirectOpenFolder:Strings.Launcher_DirectOpenFile,new(kind==LauncherItemKind.Folder?"folder":"doc"))]:[];
        }catch(TimeoutException){return [];}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){return [];}
    }
    public override Task ExecuteAsync(LauncherItem item,SourceContext c,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target=item.Id.StartsWith("direct:url:",StringComparison.Ordinal)?Url(item.Id[11..]):item.Id.StartsWith("direct:path:",StringComparison.Ordinal)?LocalPath(item.Id[12..]):null;
        return target!=null?executor.OpenAsync(target,false,token):throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
    }
}
