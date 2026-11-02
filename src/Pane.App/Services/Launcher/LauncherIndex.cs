using Pane.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;
using Pane.Settings;
using Pane.Services.Icons;
namespace Pane.Services.Launcher;

internal sealed record PersistedIndex(int Version,LauncherSnapshot Snapshot,IndexMetrics Metrics,DateTimeOffset BuiltAt);
internal sealed record IndexRequest(FavoriteEntry[] Favorites) { public IconSettings Icons {get;init;}=new(); }
[JsonSerializable(typeof(PersistedIndex))]
[JsonSerializable(typeof(IndexRequest))]
internal partial class IndexJson:JsonSerializerContext { }
internal interface ILauncherIndex
{
    PersistedIndex? Load();
    Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token);
}
internal static class IndexStorage
{
    public static PersistedIndex Read(string path){
        using var stream=File.OpenRead(path);
        var value=JsonSerializer.Deserialize(stream,IndexJson.Default.PersistedIndex);
        if(value is not {Version:1,Snapshot:not null,Metrics:not null}||value.Snapshot.Apps==null||value.Snapshot.Favorites==null||value.Snapshot.Commands==null)throw new InvalidDataException("索引格式不兼容"); // 不翻译：日志异常
        return value;
    }
    public static void Write(string path,PersistedIndex value){
        using(var stream=File.Create(path+".tmp"))JsonSerializer.Serialize(stream,value,IndexJson.Default.PersistedIndex);
        File.Move(path+".tmp",path,true);
    }
}
internal static class IndexBuilder
{
    public static PersistedIndex Build(IAppCatalog catalog,FavoriteEntry[] favorites,CancellationToken token){
        var table=new PinyinTable();var index=catalog.Build(table,token);
        var entries=favorites.Select(f=>new SearchEntry("favorite:"+f.Path,f.IsFolder?LauncherItemKind.Folder:LauncherItemKind.File,table.Create(FileTitle(f.Path)),Path.GetDirectoryName(f.Path)??f.Path,f.IsFolder?"folder":"doc")).ToArray();
        (string Id,string Name,string Glyph)[] commands=[("lock",Strings.Simplified(nameof(Strings.Launcher_CommandLock)),"lock"),("sleep",Strings.Simplified(nameof(Strings.Launcher_CommandSleep)),"moon"),("restart",Strings.Simplified(nameof(Strings.Launcher_CommandRestart)),"restart"),("shutdown",Strings.Simplified(nameof(Strings.Launcher_CommandShutdown)),"power"),("recycle",Strings.Simplified(nameof(Strings.Launcher_CommandEmptyRecycleBin)),"trash"),("clipboard",Strings.Simplified(nameof(Strings.Launcher_CommandClipboard)),"clipboard"),("settings",Strings.Simplified(nameof(Strings.Launcher_CommandSettings)),"gear")];
        var commandEntries=commands.Select(c=>new SearchEntry("command:"+c.Id,LauncherItemKind.Command,table.Create(c.Name),c.Id is "clipboard" or "settings"?"Pane":Strings.Simplified(nameof(Strings.Launcher_CommandSubtitle)),c.Glyph)).ToArray();
        token.ThrowIfCancellationRequested();return new(1,new(index.Apps,entries,commandEntries),index.Metrics,DateTimeOffset.UtcNow);
    }
    static string FileTitle(string path){var name=Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));return name.Length>0?name:path;}
}
internal static class IndexWorker
{
    public static int Run(string[] args){
        try{
            if(args.Length!=4)return 2;
            using var process=Process.GetCurrentProcess();process.PriorityClass=ProcessPriorityClass.BelowNormal;
            var request=JsonSerializer.Deserialize(File.ReadAllText(args[2]),IndexJson.Default.IndexRequest)??throw new InvalidDataException();
            var value=IndexBuilder.Build(new WindowsAppCatalog(args[1]),request.Favorites,default);
            IndexStorage.Write(args[3]+".raw",value);
            value=IconProcessing.Apply(value,args[1],request.Icons,default);
            IndexStorage.Write(args[3],value);return 0;
        }catch(Exception){return 1;}
    }
}
internal sealed class ProcessLauncherIndex(string directory,string? executable=null,TimeSpan? timeout=null):ILauncherIndex
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters {
        public uint Size,PageFaults;
        public nuint PeakWorking,Working,PeakPaged,Paged,PeakNonpaged,Nonpaged,Pagefile,PeakPagefile;
    }
    [DllImport("kernel32.dll")] static extern bool K32GetProcessMemoryInfo(nint process,ref MemoryCounters counters,uint size);
    void SamplePeak(Process child){
        // 只读取这个句柄；Process.Refresh/PeakWorkingSet64 会走系统进程信息快照，产生不必要的分配。
        var counters=new MemoryCounters{Size=(uint)Marshal.SizeOf<MemoryCounters>()};
        if(K32GetProcessMemoryInfo(child.Handle,ref counters,counters.Size))PeakWorkingSet=Math.Max(PeakWorkingSet,(long)counters.PeakWorking);
    }
    // 进程级串行，同时使用目录锁阻止同数据目录的其他实例并发写入。
    static readonly SemaphoreSlim gate=new(1);
    public double LastMilliseconds {get;private set;}
    public long PeakWorkingSet {get;private set;}
    public int LastProcessId {get;private set;}
    public int? LastExitCode {get;private set;}
    public string Outcome {get;private set;}="none";
    public Func<IconSettings> IconSettingsProvider {get;set;}=()=>new();
    public event Action<PersistedIndex>? OriginalsReady;
    string ResultPath=>Path.Combine(directory,"launcher-index.json");
    public PersistedIndex? Load()=>File.Exists(ResultPath)?IndexStorage.Read(ResultPath):null;
    public async Task<PersistedIndex> BuildAsync(FavoriteEntry[] favorites,CancellationToken token){
        await gate.WaitAsync(token).ConfigureAwait(false);
        try{
            Directory.CreateDirectory(directory);
            using var lease=new FileStream(Path.Combine(directory,"launcher-index.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            string request=Path.Combine(directory,"launcher-index-request.json"),candidate=Path.Combine(directory,"launcher-index-candidate.json");
            File.Delete(candidate);File.Delete(candidate+".tmp");File.Delete(candidate+".raw");File.Delete(candidate+".raw.tmp");
            File.WriteAllText(request,JsonSerializer.Serialize(new IndexRequest(favorites){Icons=IconSettingsProvider()},IndexJson.Default.IndexRequest));
            var start=new ProcessStartInfo(executable??Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            foreach(var arg in new[]{"--launcher-index",directory,request,candidate})start.ArgumentList.Add(arg);
            using var child=new Process{StartInfo=start};var watch=Stopwatch.StartNew();PeakWorkingSet=0;LastProcessId=0;LastExitCode=null;Outcome="starting";
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout??TimeSpan.FromMinutes(5));
            try{
                if(!child.Start())throw new IOException(Strings.Launcher_IndexStartFailed);LastProcessId=child.Id;
                try{child.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}
                Outcome="running";
                var exited=child.WaitForExitAsync();bool originalsPublished=false;
                while(!child.HasExited){
                    SamplePeak(child);
                    if(!originalsPublished&&File.Exists(candidate+".raw")){OriginalsReady?.Invoke(IndexStorage.Read(candidate+".raw"));originalsPublished=true;}
                    await Task.WhenAny(exited,Task.Delay(20,deadline.Token)).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                }
                child.WaitForExit();SamplePeak(child);LastExitCode=child.ExitCode;
                if(child.ExitCode!=0)throw new IOException(string.Format(Strings.Launcher_IndexFailed, child.ExitCode));
                var value=IndexStorage.Read(candidate);deadline.Token.ThrowIfCancellationRequested();
                // 只有父进程在确认成功后提交；超时/崩溃绝不覆盖上次可用快照。
                File.Move(candidate,ResultPath,true);IconCache.Cleanup(directory,value.Snapshot.Apps.Select(a=>a.Icon));Outcome="completed";return value;
            }catch(OperationCanceledException){Outcome=token.IsCancellationRequested?"cancelled":"timeout";throw;}
            catch{Outcome="error";throw;}
            finally{
                if(LastProcessId!=0){if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync().ConfigureAwait(false);}LastExitCode=child.ExitCode;}
                LastMilliseconds=watch.Elapsed.TotalMilliseconds;File.Delete(request);File.Delete(candidate);File.Delete(candidate+".tmp");File.Delete(candidate+".raw");File.Delete(candidate+".raw.tmp");
            }
        }finally{gate.Release();}
    }
}
