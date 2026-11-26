namespace Pane.Hosting;

internal sealed record RuntimeMode(bool IsProduction,string DataDirectory,bool IsPortable=false)
{
    public static RuntimeMode Current {get;}=Resolve(AppContext.BaseDirectory,Environment.GetEnvironmentVariable("PANE_DATA_DIR"),Settings.SettingsStore.ProductionDirectory);
    public static RuntimeMode Resolve(string executableDirectory,string? configured,string productionDirectory)
    {
        DirectoryInfo? project=null;
        for(var directory=new DirectoryInfo(executableDirectory);directory!=null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"Pane.App.csproj"))){project=directory;break;}
        bool portable = project == null && File.Exists(Path.Combine(executableDirectory, "portable"));
        string data = !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured)
            : project != null ? Path.Combine(project.FullName, "bin", "data")
            : portable ? Path.Combine(executableDirectory, "data") : productionDirectory;
        return new(project == null, data, portable);
    }

    internal int CheckWritable(string folder, Action<string> showError, Action<string>? probe = null)
    {
        if (!IsPortable) return 0;
        try
        {
            (probe ?? ProbeDirectory)(folder);
            if (Directory.Exists(DataDirectory)) (probe ?? ProbeDirectory)(DataDirectory);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            showError(folder);
            return 1;
        }
    }

    static void ProbeDirectory(string directory)
    {
        using var file = new FileStream(Path.Combine(directory, Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
}
internal enum LaunchCommand { Show,Autostart,Exit,Invalid }
internal static class LaunchArguments
{
    public static LaunchCommand Parse(string[] args)=>args.Length==0?LaunchCommand.Show:args.Length!=1?LaunchCommand.Invalid:args[0] switch {
        "--exit"=>LaunchCommand.Exit,"--autostart"=>LaunchCommand.Autostart,_=>LaunchCommand.Invalid
    };
}
