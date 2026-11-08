using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed partial class LauncherSearch : IAppAliases
{
    event EventHandler? AliasesChanged;
    event EventHandler? IAppAliases.Changed { add=>AliasesChanged+=value; remove=>AliasesChanged-=value; }
    AppAliasEntry[] observedAliases=[];

    IReadOnlyList<string> IAppAliases.Get(string appId)=>store.Current.Launcher.AppAliases.FirstOrDefault(a=>a.AppId==appId)?.Aliases.ToArray()??[];

    public void Set(string appId,IReadOnlyList<string> aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(aliases);
        var normalized=aliases.Select(a=>a?.Trim()??"").Where(a=>a.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(normalized.Length>8||normalized.Any(a=>a.Length>32||a.Any(char.IsWhiteSpace)))throw new ArgumentException("搜索别名超出限制",nameof(aliases));
        // 顺序固定为设置写锁 → sync；委托内只有内存操作。
        store.Update(current=>{
            lock(sync){
                ObjectDisposedException.ThrowIf(disposed,this);
                var id=snapshot.Aliases.GetValueOrDefault(appId,appId);
                var existing=current.Launcher.AppAliases.FirstOrDefault(a=>a.AppId==id);
                if((existing?.Aliases??[]).SequenceEqual(normalized))return current;
                // TODO(design): 未索引且没有保存记录的 Id 暂以 Id 作名字；正常界面仅提交已索引或已保存的应用。
                var name=snapshot.Apps.FirstOrDefault(a=>a.Id==id)?.Name.Name??existing?.Name??id;
                var entries=current.Launcher.AppAliases.Where(a=>a.AppId!=id).ToList();
                if(normalized.Length>0)entries.Add(new(id,name,normalized));
                return current with{Launcher=current.Launcher with{AppAliases=entries.ToArray()}};
            }
        });
    }

    public IReadOnlyList<AppAliasInfo> All()
    {
        var apps=Apps().ToDictionary(a=>a.Id);
        return store.Current.Launcher.AppAliases.Select(a=>{
            apps.TryGetValue(a.AppId,out var app);
            return new AppAliasInfo(a.AppId,app?.Name??a.Name,app?.Icon,a.Aliases.ToArray(),app==null);
        }).OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    void NotifyAliasSettings()
    {
        bool changed;
        lock(sync){var current=store.Current.Launcher.AppAliases;changed=!observedAliases.SequenceEqual(current);observedAliases=current;}
        if(changed)AliasesChanged?.Invoke(this,EventArgs.Empty);
    }
}
