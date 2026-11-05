using System.Diagnostics;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Launcher;

internal sealed record PortableCandidate(CatalogEntry Row,string Owner,PortableKind Kind);
internal sealed record PortableScan(PortableCandidate[] Candidates,Dictionary<string,PortableStatus> Status,string[] Truncated);

// 仅由索引子进程调用；单文件解析也供打开成功后的记忆逻辑使用，不枚举目录。
internal static class PortableCatalog
{
    internal static string? Normalize(string path)=>UninstallMatching.Normalize(path,false);
    internal static string Id(string path)=>"app:portable:"+(Normalize(path)??path).ToUpperInvariant();
    internal static bool Auxiliary(string name)
    {
        string lower=name.ToLowerInvariant();
        return WindowsAppCatalog.IsAuxiliaryName(name)||new[]{"setup","install","update","updater","crash","crashpad","helper","reporter"}.Any(lower.Contains);
    }
    internal static string DisplayName(string path,string? description)
    {
        string name=Path.GetFileNameWithoutExtension(path),text=description?.Trim()??"";
        string[] generic=["application","app","program","executable","launcher","microsoft .net host","应用程序","應用程式"];
        return text.Length==0||generic.Contains(text,StringComparer.OrdinalIgnoreCase)&&!text.Contains(name,StringComparison.OrdinalIgnoreCase)?name:text;
    }
    internal static ActionTarget? Resolve(string path,LauncherActionPlatform platform)
    {
        string? full=Normalize(path);
        if(full==null||!File.Exists(full)||Path.GetExtension(full).ToLowerInvariant() is not (".exe" or ".lnk"))return null;
        return platform.Resolve(full);
    }
    internal static CatalogEntry? Candidate(string path,LauncherActionPlatform platform)
    {
        var target=Resolve(path,platform);if(target==null)return null;
        string? description=null;
        try{description=FileVersionInfo.GetVersionInfo(target.Path).FileDescription;}catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){}
        // 参数按 Windows 引号规则重新编码，保留大小写及空参数。
        string args=target.RawArguments??string.Join(' ',target.Arguments.Select(Quote));
        return new(DisplayName(target.Path,description),"",target.Path,args,Normalize(path)!,2,Id(path),target.Path,0);
    }
    internal static string Quote(string value)
    {
        if(value.Length>0&&!value.Any(c=>char.IsWhiteSpace(c)||c=='"'))return value;
        var result=new System.Text.StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;}
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    static bool Plain(string path)
    {
        try{
            // 根目录本身或任一祖先是重解析点也不进入。
            for(string? current=path;current!=null;current=Path.GetDirectoryName(current))
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return false;
            return true;
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException){return false;}
    }
    static string[] Children(string path,bool directories)
    {
        try{return directories?Directory.GetDirectories(path):Directory.GetFiles(path,"*.exe");}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return [];}
    }
    internal static PortableScan Scan(PortableEntry[] entries,CancellationToken token,LauncherActionPlatform? platform=null)
    {
        platform??=new WindowsLauncherActionPlatform();var rows=new List<PortableCandidate>();
        var status=new Dictionary<string,PortableStatus>(StringComparer.OrdinalIgnoreCase);var truncated=new List<string>();
        foreach(var entry in entries.DistinctBy(e=>Normalize(e.Path)??e.Path,StringComparer.OrdinalIgnoreCase)){
            token.ThrowIfCancellationRequested();string? path=Normalize(entry.Path);
            bool exists=path!=null&&(entry.Kind==PortableKind.Folder?Directory.Exists(path):File.Exists(path));
            status[entry.Path]=new(exists?PortableScanState.Ready:PortableScanState.Missing,0);if(!exists)continue;
            if(entry.Kind!=PortableKind.Folder){
                var row=Candidate(path!,platform);if(row!=null)rows.Add(new(row,entry.Path,entry.Kind));
                status[entry.Path]=new(PortableScanState.Ready,1);continue;
            }
            if(!Plain(path!))continue;
            var paths=Children(path!,false).Concat(Children(path!,true).Where(Plain).SelectMany(p=>Children(p,false)))
                .Where(p=>!Auxiliary(Path.GetFileNameWithoutExtension(p))&&Plain(p))
                .OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ThenBy(p=>p,StringComparer.OrdinalIgnoreCase);
            int accepted=0;
            foreach(string file in paths){
                token.ThrowIfCancellationRequested();var row=Candidate(file,platform);if(row==null)continue;
                if(accepted==300){truncated.Add(entry.Path);break;}
                rows.Add(new(row,entry.Path,entry.Kind));accepted++;
            }
            status[entry.Path]=new(PortableScanState.Ready,accepted);
        }
        return new(rows.ToArray(),status,truncated.ToArray());
    }
    internal static Dictionary<string,PortableStatus> Counts(PortableScan scan,CatalogGroup[] groups)
    {
        var result=new Dictionary<string,PortableStatus>(scan.Status,StringComparer.OrdinalIgnoreCase);
        foreach(var entry in scan.Candidates.Where(c=>c.Kind==PortableKind.Folder).GroupBy(c=>c.Owner,StringComparer.OrdinalIgnoreCase)){
            var ids=entry.Select(c=>c.Row.LegacyId).ToHashSet();
            result[entry.Key]=new(PortableScanState.Ready,groups.Count(g=>g.Rows[0].Source==2&&g.Rows.Any(r=>ids.Contains(r.LegacyId))));
        }
        return result;
    }
}
