namespace Pane.Hosting;

internal sealed record RuntimeMode(bool IsProduction,string DataDirectory)
{
    public static RuntimeMode Current {get;}=Resolve(AppContext.BaseDirectory,Environment.GetEnvironmentVariable("PANE_DATA_DIR"),Settings.SettingsStore.ProductionDirectory);
    public static RuntimeMode Resolve(string executableDirectory,string? configured,string productionDirectory)
    {
        DirectoryInfo? project=null;
        for(var directory=new DirectoryInfo(executableDirectory);directory!=null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"Pane.App.csproj"))){project=directory;break;}
        return new(project==null,!string.IsNullOrWhiteSpace(configured)?Path.GetFullPath(configured):project==null?productionDirectory:Path.Combine(project.FullName,"bin","data"));
    }
}
internal enum LaunchCommand { Show,Autostart,Exit,Invalid }
internal static class LaunchArguments
{
    public static LaunchCommand Parse(string[] args)=>args.Length==0?LaunchCommand.Show:args.Length!=1?LaunchCommand.Invalid:args[0] switch {
        "--exit"=>LaunchCommand.Exit,"--autostart"=>LaunchCommand.Autostart,_=>LaunchCommand.Invalid
    };
}
