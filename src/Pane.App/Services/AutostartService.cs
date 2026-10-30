using Microsoft.Win32;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services;

// 仅工程内部的系统边界，产品接口仍使用 Contracts/Autostart.cs。
internal interface IStartupRegistry
{
    string? Read();
    bool Disabled {get;}
    void Write(string command);
    void Delete();
}
internal sealed class WindowsStartupRegistry:IStartupRegistry
{
    const string Run=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(){using var key=Registry.CurrentUser.OpenSubKey(Run);return key?.GetValue("Pane") as string;}
    public bool Disabled {get {using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");return key?.GetValue("Pane") is byte[] b&&b.Length>=4&&(b[0] is 3 or 7);}}
    public void Write(string command){using var key=Registry.CurrentUser.CreateSubKey(Run);key.SetValue("Pane",command,RegistryValueKind.String);}
    public void Delete(){using var key=Registry.CurrentUser.OpenSubKey(Run,true);key?.DeleteValue("Pane",false);}
}
internal sealed class AutostartService(bool production,string executable,SettingsStore store,IStartupRegistry registry):IAutostart
{
    string Command=>$"\"{executable}\" --autostart";
    public string? UnavailableReason=>production?null:"开发版不注册开机启动";
    public AutostartState State {get {
        if(!production)return AutostartState.Unavailable;
        var value=registry.Read();
        if(value==null||!IsPane(value))return AutostartState.Off;
        if(registry.Disabled)return AutostartState.DisabledInTaskManager;
        return PointsTo(value,executable)?AutostartState.On:AutostartState.Off;
    }}
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
        if(old!=null&&!IsPane(old))throw new InvalidOperationException("启动项 Pane 指向其他程序");
        try {
            if(enabled)registry.Write(Command);else if(old!=null)registry.Delete();
            store.Save(store.Current with {General=store.Current.General with {StartWithWindows=enabled}});
        }catch {if(old==null)registry.Delete();else registry.Write(old);throw;}
    }
}
