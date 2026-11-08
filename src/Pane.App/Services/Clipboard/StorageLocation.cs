using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.CompilerServices;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Clipboard;

internal static class StorageLocation
{
    sealed class State(LargeFilesLocation location){internal LargeFilesLocation Location=location;}
    static readonly ConditionalWeakTable<SettingsStore,State> states=new();
    public static LargeFilesLocation Resolve(SettingsStore settings)=>Volatile.Read(ref states.GetValue(settings,s=>new(Initial(s))).Location);
    public static void Set(SettingsStore settings,LargeFilesLocation location)=>Volatile.Write(ref states.GetValue(settings,s=>new(Initial(s))).Location,location);
    static LargeFilesLocation Initial(SettingsStore settings)
    {
        string fallback=Path.Combine(settings.DirectoryPath,"clipboard");
        string? configured=settings.Current.Storage.LargeFilesDirectory;
        bool unavailable=configured!=null&&!Available(configured);
        return new(configured==null||unavailable?fallback:configured,configured==null||unavailable,configured,unavailable);
    }
    public static bool Available(string path)
    {
        try{if(!Directory.Exists(path))return false;Probe(path);return true;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){return false;}
    }
    public static void Probe(string path)
    {
        using var stream=new FileStream(Path.Combine(path,".pane-write-"+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose);
        stream.WriteByte(0);
    }
    public static bool Within(string path,string root)=>path.Equals(Path.TrimEndingDirectorySeparator(root),StringComparison.OrdinalIgnoreCase)||path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    internal static bool IsGeneratedImage(string path)=>Guid.TryParseExact(Path.GetFileName(path).Split('.')[0],"N",out _);
    public static string ResolvePath(string path)
    {
        path=Path.GetFullPath(path);var ancestor=new DirectoryInfo(path);
        while(!ancestor.Exists)ancestor=ancestor.Parent??throw new DirectoryNotFoundException();
        return Path.GetFullPath(Path.Combine(FinalPath(ancestor.FullName),Path.GetRelativePath(ancestor.FullName,path)));
    }
    // 与 075 一样，通过目录句柄解析 junction、符号链接和卷的真实路径。
    static string FinalPath(string path)
    {
        using var handle=CreateFileW(path,0,FileShare.ReadWrite|FileShare.Delete,0,FileMode.Open,0x02000000,0);
        if(handle.IsInvalid)throw new IOException();
        var buffer=new StringBuilder(512);uint length=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,0);
        if(length>=buffer.Capacity){buffer.EnsureCapacity(checked((int)length+1));length=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,0);}
        if(length==0||length>=buffer.Capacity)throw new IOException();
        string result=buffer.ToString();return result.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)?@"\\"+result[8..]:result.StartsWith(@"\\?\")?result[4..]:result;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path,uint access,FileShare share,nint security,FileMode mode,uint flags,nint template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
}
