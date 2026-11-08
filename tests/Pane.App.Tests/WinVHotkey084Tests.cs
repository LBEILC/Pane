using Pane.Hosting;
using Pane.Settings;
using System.Text.Json;

internal static class WinVHotkey084Tests
{
    internal static int Run(PaneSettings defaults)
    {
        int count=0;
        void Check(bool ok,string name){count++;if(!ok)throw new Exception("084: "+name);}
        var chord=new Hotkey(KeyModifiers.Alt,86);
        var tap=new Hotkey(KeyModifiers.None,0xA3){DoubleTap=true};
        foreach(bool enabled in new[]{false,true})
        foreach(bool suspended in new[]{false,true})
        foreach(bool keep in new[]{false,true})
        foreach(bool active in new[]{false,true})
        {
            bool expected=enabled&&!suspended&&(keep||!active);
            bool original=ShellHotkeyBindings.KeepClipboardHotkey(enabled,suspended,keep,active);
            Check(original==expected,"注册条件");
            Check((ShellHotkeyBindings.DoubleTapKeys(chord,tap,original,suspended).Clipboard!=0)==expected,"双击修饰键同样适用");
        }
        foreach(var key in new[]{chord,tap})
        {
            List<int> ids=[];List<int> removed=[];
            bool available=false,requested=true,keep=false,enabled=true;
            void Register(int id,string name,Hotkey hotkey)=>ids.Add(id);
            void Bind()
            {
                bool active=ShellHotkeyBindings.Rebind(ids,removed.Add,Register,chord,key,enabled,false,false,keep,()=>
                {
                    if(!available||!requested||!enabled)return false;
                    Register(3,"winv",new(KeyModifiers.Win,86));return true;
                });
                bool original=ShellHotkeyBindings.KeepClipboardHotkey(enabled,false,keep,active);
                bool bound=key.DoubleTap?ShellHotkeyBindings.DoubleTapKeys(chord,key,original,false).Clipboard!=0:ids.Contains(2);
                Check(bound==(enabled&&(keep||!active)),"立即更新原热键");
                Check(ids.Contains(3)==(available&&requested&&enabled),"实际 Win+V 注册结果");
            }
            Bind();available=true;Bind();
            Check(key.DoubleTap||removed.Contains(2),"生效立即注销原组合键");
            available=false;Bind();available=true;Bind();
            requested=false;Bind();requested=true;Bind();
            keep=true;Bind();keep=false;Bind();enabled=false;Bind();
        }
        string root=TestResults.NewDirectory("084-");Directory.CreateDirectory(root);
        var store=new SettingsStore(root,defaults);
        string json=JsonSerializer.Serialize(defaults,SettingsJson.Default.PaneSettings);
        var node=System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Clipboard"]!.AsObject().Remove("KeepHotkeyWithWinV");
        File.WriteAllText(Path.Combine(root,"settings.json"),node.ToJsonString());
        Check(!File.ReadAllText(Path.Combine(root,"settings.json")).Contains("KeepHotkeyWithWinV"),"旧文件确实缺少新字段");
        store.Load();Check(store.Current.Clipboard.KeepHotkeyWithWinV,"旧设置默认保留");
        store.Save(store.Current with{Clipboard=store.Current.Clipboard with{KeepHotkeyWithWinV=false}});
        store.Load();Check(!store.Current.Clipboard.KeepHotkeyWithWinV,"关闭后往返保存");
        Console.WriteLine($"PASS winv084 assertions={count}; realHotkeys=0");
        return count;
    }
}
