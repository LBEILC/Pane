using System.Text.Json;
using Pane.Settings;
using Pane.Services;

static class FolderJumpSettingsTests
{
    public static int Run(PaneSettings defaults)
    {
        int count=0;
        void Check(bool value,string name) { count++; if(!value)throw new Exception(name); }
        var root=Path.Combine(AppContext.BaseDirectory,"results","folder-jump-046");
        Directory.CreateDirectory(root);
        var store=new SettingsStore(root,defaults);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(defaults,SettingsJson.Default.PaneSettings));
        var old="{"+string.Join(",",json.RootElement.EnumerateObject().Where(p=>p.Name!="FolderJump").Select(p=>"\""+p.Name+"\":"+p.Value.GetRawText()))+"}";
        File.WriteAllText(Path.Combine(root,"settings.json"),old);store.Load();
        Check(store.Current.FolderJump==new FolderJumpSettings(),"old file gets folder jump defaults");
        Check(!store.Current.FolderJump.MenuWhenMany,"old settings default to direct jump");
        store.Save(defaults with {FolderJump=new(MenuWhenMany:true)});store.Load();
        Check(store.Current.FolderJump.MenuWhenMany,"multiple Explorer preference roundtrip");
        foreach(bool many in new[]{false,true})
            foreach(int entries in new[]{0,1,2,3})
            {
                Check(FolderJumpService.ShouldRequestMenu(1,many,entries)==(entries==0||(many&&entries>=2)),"Jump menu routing");
                Check(!FolderJumpService.ShouldRequestMenu(3,many,entries),"automatic never requests menu");
                Check(FolderJumpService.ShouldRequestMenu(2,many,entries),"explicit menu always requests menu");
            }
        store.Save(defaults with {FolderJump=new(false,new(KeyModifiers.Alt,74),new(KeyModifiers.Win|KeyModifiers.Shift,75),true,false)});store.Load();
        Check(store.Current.FolderJump==new FolderJumpSettings(false,new(KeyModifiers.Alt,74),new(KeyModifiers.Win|KeyModifiers.Shift,75),true,false),"all folder settings roundtrip");
        store.Save(defaults with {FolderJump=new(JumpHotkey:new(KeyModifiers.None,74),MenuHotkey:new(KeyModifiers.None,75))});
        Check(store.Current.FolderJump.Jump==FolderJumpSettings.DefaultJump&&store.Current.FolderJump.Menu==FolderJumpSettings.DefaultMenu,"bare keys fall back on save");
        var bad=defaults with {FolderJump=new(JumpHotkey:new(KeyModifiers.None,74),MenuHotkey:new(KeyModifiers.Alt,75))};
        File.WriteAllText(Path.Combine(root,"settings.json"),JsonSerializer.Serialize(bad,SettingsJson.Default.PaneSettings));store.Load();
        Check(store.Current.FolderJump.Jump==FolderJumpSettings.DefaultJump&&store.Current.FolderJump.Menu==bad.FolderJump.Menu,"load normalizes only invalid binding");
        foreach(var modifier in new[]{KeyModifiers.Ctrl,KeyModifiers.Alt,KeyModifiers.Shift,KeyModifiers.Win})
            Check(SettingsStore.NormalizeFolderJump(new(JumpHotkey:new(modifier,74))).Jump==new Hotkey(modifier,74),"each modifier valid");
        using var service=new FolderJumpService();
        service.Apply(new(JumpHotkey:new(KeyModifiers.Alt,74)));
        using var changed=new AutoResetEvent(false);
        service.ProblemChanged+=(_,_)=>changed.Set();
        service.SetHotkeyProblem(1);
        Check(changed.WaitOne(2000)&&service.HotkeyProblem=="Alt+J 被其他程序占用","hotkey problem and changed event");
        service.SetHotkeyProblem(0);
        Check(changed.WaitOne(2000)&&service.HotkeyProblem==null,"successful registration clears problem with event");
        service.SetHotkeyProblem(1);changed.WaitOne(2000);
        service.Apply(new(JumpHotkey:new(KeyModifiers.Alt,75)));
        Check(changed.WaitOne(2000)&&service.HotkeyProblem==null,"changing combination clears problem with event");
        Check(FolderJumpService.HotkeyText(FolderJumpSettings.DefaultMenu)=="Ctrl+Shift+G","setting UI modifier spelling");
        service.Apply(new(Enabled:false));
        Check(service.Entries().Count==0,"disabled service returns no menu without Shell access");
        Console.WriteLine($"PASS folder settings assertions={count}; userData=0; realClipboard=0");
        return count;
    }
}
