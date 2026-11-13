using Pane.Localization;
using Microsoft.Win32;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services;

// 仅工程内部的系统边界，产品接口仍使用 Contracts/Autostart.cs。
internal interface IStartupRegistry
{
    string? Read();
    bool Disabled {get;}
    string? ReadCompatibility(string executable,bool allUsers);
    void Write(string command);
    void Delete();
}
internal sealed class WindowsStartupRegistry:IStartupRegistry
{
    const string Run=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(){using var key=Registry.CurrentUser.OpenSubKey(Run);return key?.GetValue("Pane") as string;}
    public bool Disabled {get {using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");return key?.GetValue("Pane") is byte[] b&&b.Length>=4&&(b[0] is 3 or 7);}}
    public string? ReadCompatibility(string executable,bool allUsers){
        using var key=(allUsers?Registry.LocalMachine:Registry.CurrentUser).OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
        // 注册表值名按完整路径匹配；显式忽略大小写，与启动项判断一致。
        var name=key?.GetValueNames().FirstOrDefault(name=>string.Equals(name,executable,StringComparison.OrdinalIgnoreCase));
        return name==null?null:key!.GetValue(name) as string;
    }
    public void Write(string command){using var key=Registry.CurrentUser.CreateSubKey(Run);key.SetValue("Pane",command,RegistryValueKind.String);}
    public void Delete(){using var key=Registry.CurrentUser.OpenSubKey(Run,true);key?.DeleteValue("Pane",false);}
}
internal sealed class AutostartService(bool production,string executable,SettingsStore store,IStartupRegistry registry):IAutostart
{
    string Command=>$"\"{executable}\" --autostart";
    public string? UnavailableReason=>production?null:Strings.Settings_Autostart_Development;
    public AutostartState State=>GetState(production,executable,registry);
    internal static AutostartState GetState(bool production,string executable,IStartupRegistry registry){
        if(!production)return AutostartState.Unavailable;
        var value=registry.Read();
        if(!PointsTo(value,executable))return AutostartState.Off;
        var user=registry.ReadCompatibility(executable,false);
        var machine=registry.ReadCompatibility(executable,true);
        if(RequiresElevation(user)||RequiresElevation(machine))return AutostartState.ElevationRequired;
        if(registry.Disabled)return AutostartState.DisabledInTaskManager;
        return AutostartState.On;
    }
    internal static bool RequiresElevation(string? flags)=>flags?.Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains("RUNASADMIN",StringComparer.OrdinalIgnoreCase)==true;
    internal static string? Executable(string? command){if(string.IsNullOrWhiteSpace(command))return null;command=command.Trim();if(command[0]=='"'){int end=command.IndexOf('"',1);return end>1?command[1..end]:null;}int space=command.IndexOf(' ');return space<0?command:command[..space];}
    internal static bool PointsTo(string? command,string executable)=>string.Equals(Executable(command),executable,StringComparison.OrdinalIgnoreCase);
    static bool IsPane(string? command)=>string.Equals(Path.GetFileName(Executable(command)),"Pane.App.exe",StringComparison.OrdinalIgnoreCase);
    public void Align(){
        if(!production||!store.Current.General.StartWithWindows)return;
        var old=registry.Read();
        if(old==null?!registry.Disabled:IsPane(old)&&!PointsTo(old,executable))registry.Write(Command);
    }
    public void Set(bool enabled){
        if(!production)throw new InvalidOperationException(UnavailableReason);
        string? old=registry.Read();
        if(old!=null&&!IsPane(old))throw new InvalidOperationException(Strings.Settings_Autostart_TargetConflict);
        try {
            if(enabled)registry.Write(Command);else if(old!=null)registry.Delete();
            store.Save(store.Current with {General=store.Current.General with {StartWithWindows=enabled}});
        }catch {if(old==null)registry.Delete();else registry.Write(old);throw;}
    }
}
