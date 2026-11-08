using System.Security.Cryptography;
using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage : IDataStorage, IDataTransfer
{
    readonly SettingsStore settings;
    readonly Func<ClipboardHistory?> history;
    readonly Action rebuild;
    readonly Action<string>? notify;
    readonly Func<Action,CancellationToken,Task> clearCache;
    readonly HashSet<string> notified=new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim operation=new(1);
    LargeFilesLocation location;
    internal string InstallDirectory {get;init;}=InstallationRoot(AppContext.BaseDirectory);
    internal static string InstallationRoot(string executableDirectory)
    {
        var directory=new DirectoryInfo(executableDirectory);
        // 安装器布局为安装根目录/app；开发发布目录则直接保护可执行文件目录。
        return directory.Name.Equals("app",StringComparison.OrdinalIgnoreCase)?directory.Parent!.FullName:directory.FullName;
    }
    internal string[] SystemDirectories {get;init;}=new[]{Environment.SpecialFolder.Windows,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.CommonApplicationData}.Select(Environment.GetFolderPath).Where(p=>p.Length>0).ToArray();
    internal Func<string,long> FreeSpace {get;init;}=path=>new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
    internal Action<string,string> Copy {get;init;}=(source,target)=>File.Copy(source,target);
    public event EventHandler? Changed;
    public LargeFilesLocation LargeFiles=>Volatile.Read(ref location);
    public DataStorage(SettingsStore settings,Func<ClipboardHistory?> history,Action rebuild,Action<string>? notify=null,Func<Action,CancellationToken,Task>? clearCache=null)
    {
        this.settings=settings;this.history=history;this.rebuild=rebuild;this.notify=notify;
        this.clearCache=clearCache??((clear,token)=>{token.ThrowIfCancellationRequested();clear();return Task.CompletedTask;});
        location=StorageLocation.Resolve(settings);NotifyUnavailable();
    }
    internal void NotifyUnavailable()
    {
        if(location.Unavailable&&location.Configured is {} path&&notified.Add(path))notify?.Invoke(string.Format(Strings.Data_LargeFilesUnavailable,path));
    }
    void OnChanged(){foreach(EventHandler handler in Changed?.GetInvocationList()??[])try{handler(this,EventArgs.Empty);}catch(Exception){ }}
    static readonly string[] CacheFiles=["launcher-index.json","launcher-index.json.tmp","launcher-index-request.json","launcher-index-candidate.json","launcher-index-candidate.json.tmp","launcher-index-candidate.json.raw","launcher-index-candidate.json.raw.tmp","catalog-evidence.json","icon-measurements.json"];
    IEnumerable<string> Files(StorageCategory category,CancellationToken token)
    {
        string data=settings.DirectoryPath;
        IEnumerable<string> roots=category switch{
            StorageCategory.Settings=>new[]{"settings.json","gestures.json"}.Select(p=>Path.Combine(data,p)),
            StorageCategory.Usage=>new[]{"launcher-usage.json","terminal-history.json"}.Select(p=>Path.Combine(data,p)),
            StorageCategory.Clipboard=>new[]{Path.Combine(data,"clipboard")},
            StorageCategory.Cache=>CacheFiles.Concat(["launcher-icons","masked-icons","file-icons","updates"]).Select(p=>Path.Combine(data,p)),
            StorageCategory.Logs=>new[]{Path.Combine(data,"logs")},_=>[]};
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string root in roots)foreach(string path in Walk(root,token))if(seen.Add(path))yield return path;
        if(category==StorageCategory.Clipboard){
            foreach(string root in new[]{LargeFiles.Path,LargeFiles.Configured}.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                foreach(string path in Walk(root,token).Where(p=>Path.GetDirectoryName(p)!.Equals(root,StringComparison.OrdinalIgnoreCase)&&Path.GetExtension(p).Equals(".png",StringComparison.OrdinalIgnoreCase)))
                    if(seen.Add(path))yield return path;
        }
    }
    static IEnumerable<string> Walk(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try{attributes=File.GetAttributes(path);}catch(IOException){yield break;}catch(UnauthorizedAccessException){yield break;}
        if((attributes&FileAttributes.ReparsePoint)!=0)yield break;
        if((attributes&FileAttributes.Directory)==0){yield return path;yield break;}
        string[] children;
        try{children=Directory.GetFileSystemEntries(path);}catch(IOException){yield break;}catch(UnauthorizedAccessException){yield break;}
        foreach(string child in children)foreach(string file in Walk(child,token))yield return file;
    }
    static bool TrySize(string path,out long bytes)
    {
        try{using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);bytes=stream.Length;return true;}
        catch(IOException){bytes=0;return false;}catch(UnauthorizedAccessException){bytes=0;return false;}
    }
    public Task<StorageReport> MeasureAsync(CancellationToken cancellationToken)=>Task.Run(()=>{
        var items=new List<StorageUsage>();
        foreach(var category in Enum.GetValues<StorageCategory>()){
            long bytes=0;int count=0;
            foreach(string path in Files(category,cancellationToken))if(TrySize(path,out long size)){bytes+=size;count++;}
            string folder=category switch{StorageCategory.Clipboard=>Path.Combine(settings.DirectoryPath,"clipboard"),StorageCategory.Logs=>Path.Combine(settings.DirectoryPath,"logs"),_=>settings.DirectoryPath};
            items.Add(new(category,bytes,count,folder));
        }
        return new StorageReport(items);
    },cancellationToken);
    public Task<long> ClearAsync(StorageCategory category,CancellationToken cancellationToken)
    {
        if(category is not(StorageCategory.Cache or StorageCategory.Logs))throw new ArgumentException(nameof(category));
        return Task.Run(async()=>{
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            long bytes=0;
            try{
                void Clear(){foreach(string path in Files(category,cancellationToken)){
                    try{
                        // 不与已有读写句柄共享；自己的句柄允许删除，检查和删除之间不留下重新打开的空隙。
                        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Delete);
                        long size=file.Length;File.Delete(path);bytes+=size;
                    }
                    catch(IOException){ }catch(UnauthorizedAccessException){ }
                }}
                if(category==StorageCategory.Cache)await clearCache(Clear,cancellationToken).ConfigureAwait(false);else Clear();
                return bytes;
            }finally{
                // 即使清理被取消，已删的缓存也要重新生成。
                try{if(category==StorageCategory.Cache)rebuild();}finally{operation.Release();OnChanged();}
            }
        },cancellationToken);
    }
    string Target(string? folder,bool reuse)
    {
        if(folder==null)return Path.Combine(settings.DirectoryPath,"clipboard");
        try{
            if(!Path.IsPathFullyQualified(folder)||folder.StartsWith(@"\\")||folder.StartsWith("//"))throw new IOException();
            string target=Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if(!reuse&&!Path.GetFileName(target).Equals("Pane",StringComparison.OrdinalIgnoreCase))target=Path.Combine(target,"Pane");
            string real=StorageLocation.ResolvePath(target);
            if(real.StartsWith(@"\\")||new DriveInfo(Path.GetPathRoot(real)!).DriveType!=DriveType.Fixed)throw new IOException();
            foreach(string forbidden in SystemDirectories.Concat([InstallDirectory,settings.DirectoryPath]))
                if(StorageLocation.Within(real,StorageLocation.ResolvePath(forbidden)))throw new IOException();
            var ancestor=new DirectoryInfo(target);while(!ancestor.Exists)ancestor=ancestor.Parent??throw new IOException();
            StorageLocation.Probe(ancestor.FullName);return real;
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){throw new DataStorageException(Strings.Data_MoveTargetInvalid,e);}
    }
    public Task MoveLargeFilesAsync(string? folder,IProgress<double>? progress,CancellationToken cancellationToken)=>Move(folder,false,progress,cancellationToken);
    public Task ReuseConfiguredAsync(IProgress<double>? progress,CancellationToken cancellationToken)=>Move(LargeFiles.Configured,true,progress,cancellationToken);
    Task Move(string? folder,bool reuse,IProgress<double>? progress,CancellationToken token)=>Task.Run(async()=>{
        await operation.WaitAsync(token).ConfigureAwait(false);
        ClipboardHistory? active=null;
        try{
            if(reuse&&(folder==null||!StorageLocation.Available(folder)))throw new DataStorageException(Strings.Data_ReuseUnavailable);
            string target=Target(folder,reuse);
            if(history() is {} loading)await loading.Ready.ConfigureAwait(false);
            lock(settings.StorageSync){
                token.ThrowIfCancellationRequested();active=history();
                // 模块正在初始化时直接读取磁盘；它取得同一把锁后会加载本次提交的索引。
                if(active?.Ready.IsCompletedSuccessfully!=true)active=null;
                lock(active?.StorageGate??settings.StorageSync){
                string index=Path.Combine(settings.DirectoryPath,"clipboard","history.json");
                var disk=new ClipboardStorage();var old=active?.StorageSnapshot()??disk.Load(index);
                var referenced=old.SelectMany(c=>new[]{c.Entry.ContentPath,c.Entry.Thumbnail?.Path}).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var roots=new[]{Path.Combine(settings.DirectoryPath,"clipboard"),LargeFiles.Path,LargeFiles.Configured}.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var sources=roots.Where(Directory.Exists).SelectMany(root=>Directory.GetFiles(root,"*.png")).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(p=>Path.GetDirectoryName(p)!.Equals(Path.Combine(settings.DirectoryPath,"clipboard"),StringComparison.OrdinalIgnoreCase)||StorageLocation.IsGeneratedImage(p)||referenced.Contains(p))
                    .Where(p=>!Path.GetDirectoryName(p)!.Equals(target,StringComparison.OrdinalIgnoreCase)).ToArray();
                var files=sources.Select(p=>new StorageMoveFile(p,Path.Combine(target,Path.GetFileName(p)))).ToArray();
                if(files.Select(f=>f.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Length||files.Any(f=>File.Exists(f.Target)))throw new IOException();
                long needed=files.Sum(f=>new FileInfo(f.Source).Length),available=FreeSpace(target);
                if(needed>available){long missing=needed-available;string amount=missing>=1024L*1024*1024?$"{missing/(1024d*1024*1024):0.##} GB":$"{Math.Max(1,Math.Ceiling(missing/(1024d*1024))):0} MB";throw new DataStorageException(string.Format(Strings.Data_MoveNoSpace,amount));}
                var map=files.ToDictionary(f=>f.Source,f=>f.Target,StringComparer.OrdinalIgnoreCase);
                string? Remap(string? path)=>path!=null&&map.TryGetValue(path,out string? value)?value:path;
                var moved=old.Select(c=>c with{Entry=c.Entry with{ContentPath=Remap(c.Entry.ContentPath),Thumbnail=c.Entry.Thumbnail is {} thumb?thumb with{Path=Remap(thumb.Path)}:null}}).ToArray();
                bool created=!Directory.Exists(target);
                try{
                    settings.CommitStorage(folder==null?null:target,saveSettings=>{
                        StorageRecovery.Begin(settings.DirectoryPath,files);
                        try{
                            Directory.CreateDirectory(target);progress?.Report(0);
                            for(int i=0;i<files.Length;i++){
                                token.ThrowIfCancellationRequested();var file=files[i];Copy(file.Source,file.Target);
                                using var source=File.OpenRead(file.Source);using var destination=File.OpenRead(file.Target);
                                if(source.Length!=destination.Length||!SHA256.HashData(source).SequenceEqual(SHA256.HashData(destination)))throw new IOException();
                                progress?.Report((i+1d)/(files.Length+1));
                            }
                            token.ThrowIfCancellationRequested();disk.Save(index,moved,_=>{});saveSettings();
                            foreach(var file in files)File.Delete(file.Source);
                            StorageRecovery.Complete(settings.DirectoryPath);
                        }catch{StorageRecovery.Recover(settings.DirectoryPath);throw;}
                    });
                    active?.StorageMoved(moved,target);
                    Volatile.Write(ref location,new(target,folder==null,folder==null?null:target,false));
                    StorageLocation.Set(settings,location);
                }catch{
                    if(created)try{Directory.Delete(target);}catch(IOException){ }catch(UnauthorizedAccessException){ }
                    throw;
                }
            }}
        }catch(DataStorageException){throw;}
        catch(OperationCanceledException){throw;}
        catch(Exception e){string reason=e switch{UnauthorizedAccessException=>Strings.Common_InputPermissionDenied,FileNotFoundException or DirectoryNotFoundException=>Strings.Common_FileNotFound,TimeoutException=>Strings.Common_Timeout,_=>$"0x{e.HResult:X8}"};throw new DataStorageException(string.Format(Strings.Data_MoveFailed,reason),e);}
        finally{operation.Release();}
        active?.StorageNotify();settings.NotifyStorageChanged();OnChanged();
        try{progress?.Report(1);}catch(Exception){ }
    },token);
}
