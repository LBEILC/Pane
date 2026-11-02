using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Launcher;

internal sealed record ActionTarget(string Path,string[] Arguments);
internal sealed record UninstallEntry(string Id,string? Location,string? Icon,string? Command,bool SystemComponent);

internal static class UninstallMatching
{
    internal static string? Normalize(string? value,bool localOnly=true)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        try {
            var path=Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if(!Path.IsPathFullyQualified(path)||localOnly&&path.StartsWith(@"\\")||path.IndexOf(':',2)>=0)return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
    }
    internal static bool Broad(string path)
    {
        if(string.Equals(path,Path.GetPathRoot(path),StringComparison.OrdinalIgnoreCase))return true;
        string[] roots=[Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"C:\Program Files",@"C:\Program Files (x86)"];
        return roots.Any(root=>string.Equals(path,Normalize(root),StringComparison.OrdinalIgnoreCase))
            ||new[]{"Program Files","Program Files (x86)","Windows","Users","ProgramData","Common Files"}.Contains(Path.GetFileName(path),StringComparer.OrdinalIgnoreCase);
    }
    internal static UninstallEntry? Match(string target,IEnumerable<UninstallEntry> entries)
    {
        var path=Normalize(target);if(path==null)return null;
        var matches=entries.Where(e=>!e.SystemComponent&&!string.IsNullOrWhiteSpace(e.Command)).Where(e=>{
            var location=Normalize(e.Location);
            // 宽目录登记整条排除，即使图标恰好相同也不展示卸载。
            if(location!=null&&Broad(location))return false;
            var icon=e.Icon?.Trim();
            if(icon!=null){int comma=icon.LastIndexOf(',');if(comma>=0&&int.TryParse(icon[(comma+1)..],out _))icon=icon[..comma];}
            return location!=null&&path.StartsWith(location+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                ||string.Equals(path,Normalize(icon),StringComparison.OrdinalIgnoreCase);
        }).Take(2).ToArray();
        return matches.Length==1?matches[0]:null;
    }
}

internal abstract class LauncherActionPlatform
{
    internal abstract ActionTarget? Resolve(string launch);
    internal abstract IReadOnlyList<UninstallEntry> ReadUninstall();
    internal abstract Task PropertiesAsync(string path,CancellationToken token);
}

internal sealed class WindowsLauncherActionPlatform:LauncherActionPlatform
{
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SHObjectProperties(nint owner,uint type,string path,string? page);
    internal static string[] Split(string command)
    {
        var memory=CommandLineToArgvW(command,out int count);if(memory==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        try{return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size))!).ToArray();}finally{LocalFree(memory);}
    }
    internal override ActionTarget? Resolve(string launch)
    {
        string? path=UninstallMatching.Normalize(launch);string[] args=[];
        if(path==null||!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)){
            int hr=CoInitializeEx(0,0);
            try{
                var target=new StringBuilder(32768);var arguments=new StringBuilder(32768);var icon=new StringBuilder(32768);
                if(LauncherNative.PaneLauncherResolve(launch,target,arguments,icon,out _)!=1)return null;
                path=UninstallMatching.Normalize(target.ToString());
                if(arguments.Length>0)args=Split("placeholder.exe "+arguments)[1..];
            }finally{if(hr>=0)CoUninitialize();}
        }
        return path!=null&&Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&File.Exists(path)?new(path,args):null;
    }
    internal override IReadOnlyList<UninstallEntry> ReadUninstall()
    {
        // 按需读取，无常驻监听；每次执行重新匹配，避免使用过期卸载登记。
        var rows=new List<UninstallEntry>();
        foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}){
            using var root=RegistryKey.OpenBaseKey(hive,view);
            using var uninstall=root.OpenSubKey(hive==RegistryHive.CurrentUser&&view==RegistryView.Registry32?@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall":@"Software\Microsoft\Windows\CurrentVersion\Uninstall");if(uninstall==null)continue;
            foreach(var name in uninstall.GetSubKeyNames()){
                using var key=uninstall.OpenSubKey(name);if(key==null)continue;
                rows.Add(new($"{hive}/{view}/{name}",key.GetValue("InstallLocation") as string,key.GetValue("DisplayIcon") as string,key.GetValue("UninstallString") as string,Equals(key.GetValue("SystemComponent"),1)));
            }
        }
        return rows;
    }
    internal override Task PropertiesAsync(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Shell 属性扩展在独立 STA 线程中运行，属性页关闭后释放线程和 COM。
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            int hr=CoInitializeEx(0,2);
            try{token.ThrowIfCancellationRequested();if(hr<0)Marshal.ThrowExceptionForHR(hr);if(!SHObjectProperties(0,2,path,null))throw new Win32Exception(Marshal.GetLastWin32Error());completion.TrySetResult();}
            catch(OperationCanceledException){completion.TrySetCanceled(token);}
            catch(Exception e){completion.TrySetException(e);}
            finally{if(hr>=0)CoUninitialize();}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}

internal sealed class LauncherActions(LauncherActionPlatform platform,ILauncherExecutor executor,ILaunchProcess process)
{
    internal ActionTarget? Target(LauncherItem item,IndexedApp? app)=>item.Kind switch {
        LauncherItemKind.App=>app is {Packaged:false}?platform.Resolve(app.Launch):null,
        LauncherItemKind.File or LauncherItemKind.Folder=>UninstallMatching.Normalize(item.Id.StartsWith("favorite:")?item.Id[9..]:item.Id.StartsWith("file:")?item.Id[5..]:null,false) is {} path?new(path,[]):null,
        _=>null
    };
    internal IReadOnlyList<LauncherItemAction> Get(LauncherItem item,ActionTarget? target)
    {
        if(target==null)return [];
        var result=new List<LauncherItemAction>();
        if(item.Kind==LauncherItemKind.App)result.Add(LauncherItemAction.RunAsAdmin);
        result.AddRange([LauncherItemAction.OpenLocation,LauncherItemAction.CopyPath,LauncherItemAction.Properties]);
        if(item.Kind==LauncherItemKind.App&&UninstallMatching.Match(target.Path,platform.ReadUninstall())!=null)result.Add(LauncherItemAction.Uninstall);
        return result;
    }
    internal async Task Run(LauncherItem item,ActionTarget? target,LauncherItemAction action,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(target==null||!Enum.IsDefined(action)||item.Kind!=LauncherItemKind.App&&action is LauncherItemAction.RunAsAdmin or LauncherItemAction.Uninstall)throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
        switch(action){
            case LauncherItemAction.RunAsAdmin:await process.StartAsync(new(target.Path,target.Arguments,Path.GetDirectoryName(target.Path)!,true),token);break;
            case LauncherItemAction.OpenLocation:await executor.RevealAsync(target.Path,token);break;
            case LauncherItemAction.CopyPath:await executor.CopyAsync(target.Path,token);break;
            case LauncherItemAction.Properties:await platform.PropertiesAsync(target.Path,token);break;
            case LauncherItemAction.Uninstall:
                var entry=UninstallMatching.Match(target.Path,platform.ReadUninstall())??throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
                var parts=WindowsLauncherActionPlatform.Split(entry.Command!);token.ThrowIfCancellationRequested();
                await process.StartAsync(new(parts[0],parts[1..],""),token);break;
        }
    }
}

internal sealed partial class LauncherSearch
{
    readonly LauncherActions actions;
    IndexedApp? ActionApp(LauncherItem item)=>Volatile.Read(ref snapshot).Apps.FirstOrDefault(a=>a.Id==item.Id);
    public async Task<IReadOnlyList<LauncherItemAction>> GetActionsAsync(LauncherItem item,CancellationToken cancellationToken)
    {
        try{return await Task.Run(()=>{cancellationToken.ThrowIfCancellationRequested();var result=actions.Get(item,actions.Target(item,ActionApp(item)));cancellationToken.ThrowIfCancellationRequested();return result;},cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);}
        catch(OperationCanceledException){throw;}
        catch(Exception){throw new LauncherActionException(Strings.Launcher_ActionFailed);}
    }
    public async Task RunAsync(LauncherItem item,LauncherItemAction action,CancellationToken cancellationToken)
    {
        try{await Task.Run(async()=>{cancellationToken.ThrowIfCancellationRequested();await actions.Run(item,actions.Target(item,ActionApp(item)),action,cancellationToken).ConfigureAwait(false);},cancellationToken).ConfigureAwait(false);}
        catch(Win32Exception e) when(e.NativeErrorCode==1223){}
        catch(OperationCanceledException){throw;}
        catch(LauncherActionException){throw;}
        catch(Exception){throw new LauncherActionException(Strings.Launcher_ActionFailed);}
    }
    async Task ExecuteAppElevatedAsync(LauncherItem item,CancellationToken token)
    {
        if((await GetActionsAsync(item,token).ConfigureAwait(false)).Contains(LauncherItemAction.RunAsAdmin))await RunAsync(item,LauncherItemAction.RunAsAdmin,token).ConfigureAwait(false);
        else await ExecuteAsync(item,token).ConfigureAwait(false);
    }
}
