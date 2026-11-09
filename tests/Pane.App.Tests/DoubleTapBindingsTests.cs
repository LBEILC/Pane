using Pane.Hosting;
using Pane.Settings;
static class DoubleTapBindingsTests
{
    internal static int Run()
    {
        int checks=0;void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
        var chord=new Hotkey(KeyModifiers.Alt,32);
        var left=new Hotkey(KeyModifiers.None,0xA2){DoubleTap=true};
        var right=new Hotkey(KeyModifiers.None,0xA3){DoubleTap=true};
        foreach(var (text,key) in new[]{("2xLShift",0xA0),("2xRShift",0xA1),("2xLCtrl",0xA2),("2xRCtrl",0xA3),("2xLAlt",0xA4),("2xRAlt",0xA5)})
            Check(ShellHotkeyBindings.ParseOverride(text)==new Hotkey(KeyModifiers.None,(ushort)key){DoubleTap=true},"six override names");
        Check(ShellHotkeyBindings.ParseOverride("Ctrl+Alt+Space")==new Hotkey(KeyModifiers.Ctrl|KeyModifiers.Alt,32),"chord override preserved");
        Check(ShellHotkeyBindings.DoubleTapKeys(chord,chord,true,false)==(0u,0u),"no double tap");
        Check(ShellHotkeyBindings.DoubleTapKeys(left,right,true,false)==(0xA2u,0xA3u),"two distinct keys");
        Check(ShellHotkeyBindings.DoubleTapKeys(left,left,true,false)==(0xA2u,0u),"duplicate launcher wins");
        Check(ShellHotkeyBindings.DoubleTapKeys(chord,right,false,false)==(0u,0u),"disabled clipboard");
        Check(ShellHotkeyBindings.DoubleTapKeys(left,right,true,true)==(0u,0u),"recording suspension");
        foreach(var invalid in new[]{left with{Modifiers=KeyModifiers.Ctrl},left with{VirtualKey=65}}){
            Check(!invalid.IsValidDoubleTap&&ShellHotkeyBindings.DoubleTapKeys(invalid,chord,true,false)==(0u,0u),"invalid double tap no hook");
            List<int> registered=[1,2,3],removed=[],requested=[];
            ShellHotkeyBindings.Rebind(registered,removed.Add,(id,_,_)=>requested.Add(id),invalid,right,true,false);
            Check(removed.SequenceEqual([1,2,3])&&requested.SequenceEqual([1]),"invalid dispatched to conflict path; valid double tap not registered");
        }
        Check(ShellHotkeyBindings.Command(1)=="hotkey-launcher"&&ShellHotkeyBindings.Command(2)=="hotkey-clipboard"&&ShellHotkeyBindings.Command(3)=="hotkey-clipboard","commands preserved");
        Check(ShellHotkeyBindings.Command(4)=="hotkey-words","words command");
        foreach(bool enabled in new[]{false,true})foreach(bool suspended in new[]{false,true})foreach(var key in new Hotkey?[]{null,chord,left})
        {
            List<int> registered=[4],removed=[],requested=[];
            ShellHotkeyBindings.Rebind(registered,removed.Add,(id,_,_)=>requested.Add(id),chord,chord,false,suspended,false,words:key,wordsEnabled:enabled);
            Check(removed.SequenceEqual([4])&&requested.Contains(4)==(enabled&&!suspended&&key==chord),"words registration lifetime");
        }
        return checks;
    }
}
