using Pane.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed record LaunchRequest(string FileName,string[] Arguments,string WorkingDirectory,bool Elevated=false);
internal interface ILaunchProcess { Task StartAsync(LaunchRequest request,CancellationToken token); }
internal sealed class LaunchProcess:ILaunchProcess
{
    public Task StartAsync(LaunchRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start=new ProcessStartInfo(request.FileName){UseShellExecute=true,WorkingDirectory=request.WorkingDirectory,Verb=request.Elevated?"runas":""};
        foreach(var arg in request.Arguments)start.ArgumentList.Add(arg);
        using var process=Process.Start(start);
        return Task.CompletedTask;
    }
}
internal interface ILauncherEnvironment
{
    string Home {get;}
    string? FindProgram(string name);
    bool HibernateAvailable {get;}
}
internal sealed class LauncherEnvironment:ILauncherEnvironment
{
    [DllImport("powrprof.dll")][return:MarshalAs(UnmanagedType.U1)] static extern bool IsPwrHibernateAllowed();
    public string Home=>Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public bool HibernateAvailable=>IsPwrHibernateAllowed();
    public string? FindProgram(string name)
    {
        foreach(var dir in new[]{Environment.SystemDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"ImmersiveControlPanel"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps")}.Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)))
        {
            try{var path=Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')),name);if(Path.IsPathFullyQualified(path)&&File.Exists(path))return path;}catch(ArgumentException){}
        }
        return null;
    }
}
[JsonSerializable(typeof(List<string>))]
internal partial class TerminalJson:JsonSerializerContext {}

internal sealed class TerminalHistory:ITerminalHistory
{
    readonly SettingsStore store;readonly object sync=new();readonly string path;List<string> history=[];
    public TerminalHistory(SettingsStore store)
    {
        this.store=store;path=Path.Combine(store.DirectoryPath,"terminal-history.json");
        try{if(File.Exists(path))history=(JsonSerializer.Deserialize(File.ReadAllText(path),TerminalJson.Default.ListString)??[]).Where(s=>!string.IsNullOrWhiteSpace(s)&&!char.IsWhiteSpace(s[0])).Distinct(StringComparer.Ordinal).Take(20).ToList();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){}
    }
    public string[] Match(string prefix){lock(sync)return store.Current.Launcher.RememberCommands?history.Where(s=>s.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray():[];}
    public void Record(string raw)
    {
        // > 后首字符为空白时，执行可去掉前导空白，但整条命令不进入历史。
        if(!store.Current.Launcher.RememberCommands||string.IsNullOrWhiteSpace(raw)||char.IsWhiteSpace(raw[0]))return;
        lock(sync){history.RemoveAll(s=>s==raw);history.Insert(0,raw);if(history.Count>20)history.RemoveRange(20,history.Count-20);Save();}
    }
    public void Clear(){lock(sync){history.Clear();Save();}}
    void Save(){Directory.CreateDirectory(store.DirectoryPath);File.WriteAllText(path+".tmp",JsonSerializer.Serialize(history,TerminalJson.Default.ListString));File.Move(path+".tmp",path,true);}
}

internal sealed class TerminalPrograms:IDisposable
{
    readonly object sync=new();readonly List<FileSystemWatcher> watchers=[];readonly Dictionary<string,string[]> directories=new(StringComparer.OrdinalIgnoreCase);
    readonly string[] appPaths;string[] names=[];bool disposed;
    public TerminalPrograms(IEnumerable<string>? paths=null,IEnumerable<string>? registered=null,bool watch=true)
    {
        appPaths=(registered??ReadAppPaths()).ToArray();
        foreach(var directory in (paths??(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)).Select(p=>Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(directory))continue;
            Refresh(directory);
            if(!watch)continue;
            try{var watcher=new FileSystemWatcher(directory){NotifyFilter=NotifyFilters.FileName};watcher.Created+=(_,_)=>Refresh(directory);watcher.Deleted+=(_,_)=>Refresh(directory);watcher.Renamed+=(_,_)=>Refresh(directory);watcher.Error+=(_,_)=>Refresh(directory);watcher.EnableRaisingEvents=true;watchers.Add(watcher);}catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException){}
        }
        lock(sync)Rebuild();
    }
    static IEnumerable<string> ReadAppPaths()
    {
        var result=new List<string>();
        foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
        {try{using var root=RegistryKey.OpenBaseKey(hive,view);using var key=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");if(key!=null)foreach(var name in key.GetSubKeyNames()){using var app=key.OpenSubKey(name);if(app?.GetValue("") is string path&&File.Exists(Environment.ExpandEnvironmentVariables(path.Trim('"'))))result.Add(Path.GetFileNameWithoutExtension(name));}}catch(Exception e) when(e is System.Security.SecurityException or UnauthorizedAccessException or IOException){}}
        return result;
    }
    void Refresh(string directory)
    {
        string[] found;
        try{found=Directory.EnumerateFiles(directory).Where(p=>new[]{".exe",".com",".cmd",".bat"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();}catch(Exception e) when(e is IOException or UnauthorizedAccessException){found=[];}
        lock(sync){if(disposed)return;directories[directory]=found;Rebuild();}
    }
    void Rebuild()=>names=directories.Values.SelectMany(x=>x).Concat(appPaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public string[] Match(string prefix){if(prefix.Any(char.IsWhiteSpace))return [];lock(sync)return names.Where(n=>n.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();}
    public void Dispose(){lock(sync){disposed=true;}foreach(var watcher in watchers)watcher.Dispose();}
}

internal sealed class TerminalService:IDisposable
{
    readonly ILauncherEnvironment environment;readonly ILaunchProcess process;readonly TerminalPrograms programs;
    public TerminalHistory History {get;}
    public TerminalService(SettingsStore store,ILauncherEnvironment environment,ILaunchProcess process,TerminalPrograms? programs=null){this.environment=environment;this.process=process;this.programs=programs??new();History=new(store);}
    public LauncherResult Search(string query)
    {
        string raw=query[1..],command=raw.TrimStart();
        var rows=new List<LauncherItem>{string.IsNullOrWhiteSpace(command)?new("terminal:hint",LauncherItemKind.Action,LauncherGroups.Terminal,Strings.Launcher_TerminalInputHint,null,new("terminal")):new("terminal:run:"+raw,LauncherItemKind.Action,LauncherGroups.Terminal,Strings.Launcher_TerminalRunDescription,command,new("terminal"),Hotkey:"Enter")};
        rows.AddRange(History.Match(command).Concat(programs.Match(command)).Distinct(StringComparer.OrdinalIgnoreCase).Where(s=>s!=command).Take(6).Select(s=>new LauncherItem("complete:"+s,LauncherItemKind.Action,LauncherGroups.Completion,s,null,new("terminal"))));
        return new(rows,false);
    }
    public static bool Owns(LauncherItem item)=>item.Id.StartsWith("terminal:",StringComparison.Ordinal)||item.Id.StartsWith("complete:",StringComparison.Ordinal);
    public async Task ExecuteAsync(LauncherItem item,bool elevated,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(item.Id=="terminal:hint")return;
        string raw=item.Id.StartsWith("complete:",StringComparison.Ordinal)?item.Id[9..]:item.Id.StartsWith("terminal:run:",StringComparison.Ordinal)?item.Id[13..]:throw new NotSupportedException(item.Id);
        string command=raw.TrimStart();if(string.IsNullOrWhiteSpace(command))return;
        var cmd=environment.FindProgram("cmd.exe")??"cmd.exe";var terminal=environment.FindProgram("wt.exe");
        // 提权必须创建独立窗口，避免把命令转发给已有的普通权限窗口；-d 固定新标签页的工作目录。
        string[] args=terminal==null?["/k",command]:["-w",elevated?"new":"0","new-tab","-d",environment.Home,"--",cmd,"/k",command];
        await process.StartAsync(new(terminal??cmd,args,environment.Home,elevated),token).ConfigureAwait(false);
        History.Record(raw);
    }
    public void Dispose()=>programs.Dispose();
}
