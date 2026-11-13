using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed partial class LauncherSearch:ILauncherHiddenItems
{
    static bool CanHide(LauncherItem item)=>item.Group!=LauncherGroups.Direct&&!TerminalService.Owns(item)&&!item.Id.StartsWith("power:",StringComparison.Ordinal)
        &&(item.Kind==LauncherItemKind.App||item.Kind==LauncherItemKind.Command||item.Group==LauncherGroups.Settings);
    void Hide(LauncherItem item)
    {
        if(!CanHide(item))throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
        store.Update(current=>{
            var s=Volatile.Read(ref snapshot);var id=s.Aliases.GetValueOrDefault(item.Id,item.Id);
            if(current.Launcher.HiddenItems.Any(e=>s.Aliases.GetValueOrDefault(e.Id,e.Id)==id))return current;
            return current with{Launcher=current.Launcher with{HiddenItems=[..current.Launcher.HiddenItems,new(id,item.Title)]}};
        });
    }
    public void Unhide(string id)=>store.Update(current=>current.Launcher.HiddenItems.Any(e=>e.Id==id)
        ?current with{Launcher=current.Launcher with{HiddenItems=current.Launcher.HiddenItems.Where(e=>e.Id!=id).ToArray()}}:current);
    public IReadOnlyList<LauncherItem> List()
    {
        var s=Volatile.Read(ref snapshot);var apps=s.Apps.ToDictionary(a=>a.Id);
        var context=Context("");
        var commands=sources.OfType<EntrySource>().Where(source=>source.Group==LauncherGroups.Commands).SelectMany(source=>source.ListEntries(context)).ToDictionary(e=>e.Id);
        var systems=sources.OfType<SystemSettingsSource>().SelectMany(source=>source.ListEntries()).ToDictionary(e=>e.Id);
        bool dark=Pane.Services.Icons.IconTheme.IsDark(store.Current.General.Theme);
        return store.Current.Launcher.HiddenItems.Select(entry=>{
            var id=s.Aliases.GetValueOrDefault(entry.Id,entry.Id);
            if(apps.TryGetValue(id,out var app))return new LauncherItem(entry.Id,LauncherItemKind.App,LauncherGroups.Applications,app.Name.Name,Strings.Launcher_ApplicationSubtitle,new("app",s.IconSettingsKey==lastIcons?(dark?app.DarkIcon:app.LightIcon)??app.Icon:app.Icon));
            if(commands.TryGetValue(id,out var command))return new LauncherItem(entry.Id,command.Kind,LauncherGroups.Commands,command.Name.Name,command.Subtitle,new(command.Glyph));
            if(systems.TryGetValue(id,out var system))return system with{Id=entry.Id};
            bool application=id.StartsWith("app:",StringComparison.Ordinal);
            return new LauncherItem(entry.Id,application?LauncherItemKind.App:LauncherItemKind.Command,application?LauncherGroups.Applications:id.StartsWith("system:",StringComparison.Ordinal)?LauncherGroups.Settings:LauncherGroups.Commands,entry.Name,null,new(application?"app":"gear"));
        }).ToArray();
    }
}
