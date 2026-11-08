using Pane.Settings;
namespace Pane.Services.Launcher;

internal static class AppSettingsMigration
{
    // 保留项已经有显式设置时优先保留；否则按别名的稳定顺序继承第一份设置。
    // TODO(design): 多份旧图标设置冲突时暂用上述保守策略；如需其他冲突处理方式，由设计确定。
    static Dictionary<string,T> Remap<T>(Dictionary<string,T> source,IReadOnlyDictionary<string,string> aliases){
        var result=new Dictionary<string,T>(source);
        foreach(var (oldId,id) in aliases.OrderBy(p=>p.Key,StringComparer.Ordinal)){
            if(oldId==id||!result.Remove(oldId,out var value))continue;
            result.TryAdd(id,value);
        }
        return result;
    }
    public static IconSettings Icons(IconSettings settings,IReadOnlyDictionary<string,string> aliases)=>settings with{
        Overrides=Remap(settings.Overrides,aliases),CustomIcons=Remap(settings.CustomIcons,aliases)
    };
    public static PaneSettings Apply(PaneSettings settings,IReadOnlyDictionary<string,string> aliases){
        if(!aliases.Keys.Any(id=>settings.Launcher.PinnedApps.Contains(id)||settings.Launcher.AppAliases.Any(a=>a.AppId==id)||settings.Icons.Overrides.ContainsKey(id)||settings.Icons.CustomIcons.ContainsKey(id)))return settings;
        return settings with{
            Launcher=settings.Launcher with{
                PinnedApps=settings.Launcher.PinnedApps.Select(id=>aliases.GetValueOrDefault(id,id)).Distinct().ToArray(),
                AppAliases=settings.Launcher.AppAliases.GroupBy(a=>aliases.GetValueOrDefault(a.AppId,a.AppId)).Select(g=>
                    new AppAliasEntry(g.Key,g.FirstOrDefault(a=>a.AppId==g.Key)?.Name??g.First().Name,
                        g.SelectMany(a=>a.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray())).ToArray()
            },
            Icons=Icons(settings.Icons,aliases)
        };
    }
}
