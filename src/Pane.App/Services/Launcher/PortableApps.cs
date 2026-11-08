using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Launcher;

internal sealed partial class LauncherSearch:IPortableAppsStatus
{
    Dictionary<string,PortableStatus> portableStatus=new(StringComparer.OrdinalIgnoreCase);
    event EventHandler? PortableChanged;
    event EventHandler? IPortableAppsStatus.Changed {add=>PortableChanged+=value;remove=>PortableChanged-=value;}
    public PortableStatus Get(string path)
    {
        lock(sync)return portableStatus.GetValueOrDefault(path)??new(PortableScanState.Scanning,0);
    }
    void MarkPortableScanning()
    {
        portableStatus=lastPortable.DistinctBy(e=>e.Path,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(e=>e.Path,_=>new PortableStatus(PortableScanState.Scanning,0),StringComparer.OrdinalIgnoreCase);
    }
    void PrunePortable()
    {
        var entries=store.Current.Launcher.Portable;
        var current=Volatile.Read(ref snapshot);
        Volatile.Write(ref snapshot,current with{Apps=current.Apps.Where(a=>a.PortablePath==null||entries.Any(e=>SamePath(e.Path,a.PortablePath)&&e.Kind==a.PortableKind)).ToArray()});
    }
    static bool SamePath(string a,string b)=>string.Equals(PortableCatalog.Normalize(a)??a,PortableCatalog.Normalize(b)??b,StringComparison.OrdinalIgnoreCase);
    static bool SameTarget(ActionTarget a,ActionTarget b)=>SamePath(a.Path,b.Path)&&a.Arguments.SequenceEqual(b.Arguments,StringComparer.Ordinal);
    ActionTarget? IndexedTarget(IndexedApp app)=>app.Target is {Length:>0}?new(app.Target,app.Arguments??[]):app.Packaged?null:portablePlatform.Resolve(app.Launch);
    int remembering;
    internal async Task RememberOpenedAsync(string path)
    {
        CancellationToken token;
        lock(sync){if(disposed)return;token=lifetime.Token;}
        // 超时只结束等待；Shell 真正返回前不放开名额，避免堆积卡住的线程。
        if(Interlocked.CompareExchange(ref remembering,1,0)!=0)return;
        var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var work=Task.Factory.StartNew(()=>{
            try{RememberOpened(path,deadline.Token);}
            catch(OperationCanceledException) when(deadline.IsCancellationRequested){}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception){IndexError="portable:"+e.GetType().Name;}
            finally{deadline.Dispose();Volatile.Write(ref remembering,0);}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try{await work.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);}
        catch(TimeoutException){}
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
    }
    void RememberOpened(string path,CancellationToken token)
    {
        var settings=store.Current;
        if(!settings.Launcher.RememberOpenedPrograms)return;
        var captured=Volatile.Read(ref snapshot);
        token.ThrowIfCancellationRequested();
        var target=PortableCatalog.Resolve(path,portablePlatform);
        token.ThrowIfCancellationRequested();
        if(target==null||PortableCatalog.Auxiliary(Path.GetFileNameWithoutExtension(target.Path))||PortableCatalog.Auxiliary(Path.GetFileNameWithoutExtension(path)))return;
        IndexedApp? existing=null;
        foreach(var app in captured.Apps){
            var other=IndexedTarget(app);token.ThrowIfCancellationRequested();
            if(other!=null&&SameTarget(target,other)){existing=app;break;}
        }
        bool duplicate=false;
        if(existing==null)foreach(var entry in settings.Launcher.Portable){
            if(SamePath(entry.Path,path)){duplicate=true;break;}
            var other=entry.Kind==PortableKind.Folder?null:PortableCatalog.Resolve(entry.Path,portablePlatform);
            token.ThrowIfCancellationRequested();
            if(other!=null&&SameTarget(target,other)){duplicate=true;break;}
        }
        var normalized=PortableCatalog.Normalize(path)!;
        bool used=false;
        // 写锁先于短暂的 sync；所有调用方均禁止反向持锁。
        store.Update(current=>{
            lock(sync){
                if(disposed||token.IsCancellationRequested||!ReferenceEquals(current,settings)||!ReferenceEquals(snapshot,captured))return current;
                if(existing!=null){RecordUseCore(existing.Id);used=true;return current;}
                if(duplicate)return current;
                var launcher=current.Launcher;
                var entries=launcher.Portable.Append(new PortableEntry(normalized,PortableKind.Learned,DateTimeOffset.UtcNow)).ToList();
                var remove=entries.Where(e=>e.Kind==PortableKind.Learned)
                    .OrderBy(e=>uses.GetValueOrDefault(PortableCatalog.Id(e.Path))?.LastUsed??e.AddedAt)
                    .Take(Math.Max(0,entries.Count(e=>e.Kind==PortableKind.Learned)-100)).ToHashSet();
                return current with{Launcher=launcher with{Portable=entries.Where(e=>!remove.Contains(e)).ToArray()}};
            }
        });
        if(used)SaveUsage();
    }
    internal async Task OpenFileAsync(string path,CancellationToken token)
    {
        await executor.OpenAsync(path,false,token).ConfigureAwait(false);
        await RememberOpenedAsync(path).ConfigureAwait(false);
    }
    void RemovePortable(LauncherItem item)
    {
        var app=ActionApp(item);
        if(app?.PortablePath==null||app.PortableKind is not (PortableKind.Program or PortableKind.Learned))throw new LauncherActionException(Pane.Localization.Strings.Launcher_ActionUnavailable);
        store.Update(settings=>settings with{Launcher=settings.Launcher with{Portable=settings.Launcher.Portable.Where(e=>!SamePath(e.Path,app.PortablePath)).ToArray()}});
        lock(sync)PrunePortable();
    }
}
