using Pane.Contracts;
using Pane.Localization;

namespace Pane.Views.Launcher;

internal static class LauncherGroupLabels
{
    public static string Get(string group) => group switch
    {
        LauncherGroups.Applications => Strings.Launcher_Group_Applications,
        LauncherGroups.Files => Strings.Launcher_Group_Files,
        LauncherGroups.Commands => Strings.Launcher_Group_Commands,
        LauncherGroups.Calculation => Strings.Launcher_Group_Calculation,
        LauncherGroups.Ai => Strings.Launcher_Group_Ai,
        LauncherGroups.Web => Strings.Launcher_Group_Web,
        LauncherGroups.Settings => Strings.Launcher_Group_Settings,
        LauncherGroups.Terminal => Strings.Launcher_Group_Terminal,
        LauncherGroups.Recent => Strings.Launcher_Group_Recent,
        LauncherGroups.Pinned => Strings.Launcher_Group_Pinned,
        LauncherGroups.Completion => Strings.Launcher_Group_Completion,
        LauncherGroups.Favorites => Strings.Launcher_Group_Favorites,
        LauncherGroups.Images => Strings.Launcher_Group_Images,
        LauncherGroups.Other => Strings.Launcher_Group_Other,
        LauncherGroups.Actions => Strings.Launcher_Group_Actions,
        _ => group
    };
}
