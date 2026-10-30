using System.Security.Cryptography;
using System.Text;
namespace Pane.Services.Launcher;

// 016 在原图提取后追加步骤；步骤可以生成另一路径，快照只发布最终产物。
internal interface IIconProcessingStep { string Process(string original,CancellationToken token); }
internal static class IconCache
{
    public static string? GetOrCreate(string cache,string key,Func<string,int> extract,out int result){
        var path=Path.Combine(cache,key+".png");result=1;
        if(File.Exists(path))return path;
        result=extract(path+".tmp");
        if(result==1){File.Move(path+".tmp",path,true);return path;}
        File.Delete(path+".tmp");return null;
    }
    public static string Key(string identity,params string[] paths)=>Fingerprint(identity,paths).Key;
    public static (string Key,bool HasMetadata) Fingerprint(string identity,params string[] paths){
        var stamp=new StringBuilder(identity);bool metadata=false;
        foreach(var path in paths){
            var expanded=Environment.ExpandEnvironmentVariables(path);stamp.Append('\n').Append(expanded.ToUpperInvariant());
            try{var file=new FileInfo(expanded);if(file.Exists){stamp.Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);metadata=true;}}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){}
        }
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()))),metadata);
    }
    public static void Cleanup(string directory,IEnumerable<string?> referenced){
        var cache=Path.Combine(directory,"launcher-icons");if(!Directory.Exists(cache))return;
        var keep=referenced.Where(p=>p!=null).SelectMany(p=>new[]{p,p+".source"}).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var path in Directory.EnumerateFiles(cache))if(!keep.Contains(path)){
            try{File.Delete(path);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        }
    }
}
