using Pane.Contracts;
using Pane.Hosting;
using Pane.Settings;
namespace Pane;

public sealed partial class App
{
    async Task WordsHotkeyProbe()
    {
        try
        {
            if(!demo||shell!=null||clipboardSource!=null||!controlsEnabled||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PANE_INSTANCE")))throw new InvalidOperationException("仅隔离演示实例运行");
            int checks=0;
            void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
            var key=new Hotkey(KeyModifiers.Ctrl|KeyModifiers.Alt|KeyModifiers.Shift,0x87);
            Store.Save(Store.Current with{General=Store.Current.General with{ShowTrayIcon=false},Words=new(key),Modules=new(Store.Current.Modules??[]){[ModuleIds.Launcher]=false,[ModuleIds.Clipboard]=false}});
            await Modules.ApplyAsync(Modules.Preview(ModuleIds.Launcher,false),default);
            await Modules.ApplyAsync(Modules.Preview(ModuleIds.Clipboard,false),default);
            shell=new ShellHost(_=>{},ModuleShellSettings());
            Check(shell.RegisteredCount==1&&shell.Conflicts.Count==0,"分词组合键注册");
            Check(shell.Diagnostics.Any(s=>s.StartsWith("words=")&&s.Contains("registered=True")),"诊断分词注册");
            shell.SetHotkeysSuspended(true);await shell.FlushAsync();
            Check(shell.RegisteredCount==0&&!shell.KeyboardHookActive,"录入暂停注销");
            // UI 线程占用组合键，外壳消息线程恢复时必须报告冲突；不生成输入。
            Check(Native.RegisterHotKey(0,93,1|2|4|ShellHotkeyBindings.NoRepeat,key.VirtualKey),"占用测试组合键");
            try
            {
                shell.SetHotkeysSuspended(false);await shell.FlushAsync();
                Check(shell.Conflicts.Any(c=>c.Target=="words"&&c.Error==1409),"分词冲突");
                shell.SetHotkeysSuspended(true);await shell.FlushAsync();
            }
            finally { Native.UnregisterHotKey(0,93); }
            shell.SetHotkeysSuspended(false);await shell.FlushAsync();
            Check(shell.RegisteredCount==1,"录入结束恢复");
            await Modules.ApplyAsync(Modules.Preview(ModuleIds.Words,false),default);await shell.FlushAsync();
            Check(Words==null&&shell.RegisteredCount==0,"能力停用注销");
            await Modules.ApplyAsync(Modules.Preview(ModuleIds.Words,true),default);await shell.FlushAsync();
            Check(Words!=null&&shell.RegisteredCount==1,"能力启用恢复");
            Store.Save(Store.Current with{Words=new(ShellHotkeyBindings.ParseOverride("2xRCtrl"))});
            shell.Apply(ModuleShellSettings());await shell.FlushAsync();
            Check(shell.KeyboardHookActive&&shell.RegisteredCount==0,"分词双击挂钩");
            shell.SetHotkeysSuspended(true);await shell.FlushAsync();
            Check(!shell.KeyboardHookActive,"双击录入暂停");
            shell.SetHotkeysSuspended(false);await shell.FlushAsync();
            Check(shell.KeyboardHookActive,"双击录入恢复");
            var clash=ModuleShellSettings();clash=clash with{Launcher=clash.Launcher with{Hotkey=clash.Words.Hotkey!},Modules=new(clash.Modules!){[ModuleIds.Launcher]=true}};
            shell.Apply(clash);await shell.FlushAsync();
            Check(shell.Conflicts.Any(c=>c.Target=="words"&&c.Error==1409),"双击重复冲突");
            shell.Apply(ModuleShellSettings());await shell.FlushAsync();
            await Modules.ApplyAsync(Modules.Preview(ModuleIds.Words,false),default);await shell.FlushAsync();
            Check(!shell.KeyboardHookActive&&shell.RegisteredCount==0,"能力停用卸钩");
            File.WriteAllText(output+".words-result",$"PASS assertions={checks};registered=0;keyhook=0;systemClipboard=0\n"+ModuleDiagnostics());
        }
        catch(Exception e){File.WriteAllText(output+".error",e.ToString());}
    }
}
