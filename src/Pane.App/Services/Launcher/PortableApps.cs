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
    internal Task RememberOpenedAsync(string path)=>Task.Run(()=>{
        try{
            if(!store.Current.Launcher.RememberOpenedPrograms||lifetime.IsCancellationRequested)return;
            var target=PortableCatalog.Resolve(path,portablePlatform);
            if(target==null||PortableCatalog.Auxiliary(Path.GetFileNameWithoutExtension(target.Path))||PortableCatalog.Auxiliary(Path.GetFileNameWithoutExtension(path)))return;
            lock(sync){
                if(disposed)return;
                var existing=Volatile.Read(ref snapshot).Apps.FirstOrDefault(a=>IndexedTarget(a) is {} other&&SameTarget(target,other));
                if(existing!=null){RecordUse(existing.Id);return;}
                store.Update(settings=>{
                    var launcher=settings.Launcher;
                    if(!launcher.RememberOpenedPrograms)return settings;
                    if(launcher.Portable.Any(e=>SamePath(e.Path,path)||e.Kind!=PortableKind.Folder&&PortableCatalog.Resolve(e.Path,portablePlatform) is {} other&&SameTarget(target,other)))return settings;
                    var added=new PortableEntry(PortableCatalog.Normalize(path)!,PortableKind.Learned,DateTimeOffset.UtcNow);
                    var entries=launcher.Portable.Append(added).ToList();
                    var remove=entries.Where(e=>e.Kind==PortableKind.Learned)
                        .OrderBy(e=>uses.GetValueOrDefault(PortableCatalog.Id(e.Path))?.LastUsed??e.AddedAt)
                        .Take(Math.Max(0,entries.Count(e=>e.Kind==PortableKind.Learned)-100)).ToHashSet();
                    return settings with{Launcher=launcher with{Portable=entries.Where(e=>!remove.Contains(e)).ToArray()}};
                });
            }
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception){IndexError="portable:"+e.GetType().Name;}
    });
    internal async Task OpenFileAsync(string path,CancellationToken token)
    {
        await executor.OpenAsync(path,false,token).ConfigureAwait(false);
        await RememberOpenedAsync(path).ConfigureAwait(false);
    }
    void RemovePortable(LauncherItem item)
    {
        lock(sync){
            var app=ActionApp(item);
            if(app?.PortablePath==null||app.PortableKind is not (PortableKind.Program or PortableKind.Learned))throw new LauncherActionException(Pane.Localization.Strings.Launcher_ActionUnavailable);
            store.Update(settings=>settings with{Launcher=settings.Launcher with{Portable=settings.Launcher.Portable.Where(e=>!SamePath(e.Path,app.PortablePath)).ToArray()}});
            PrunePortable();
        }
    }
}
