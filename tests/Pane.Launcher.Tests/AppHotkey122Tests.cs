using System.Diagnostics;
using System.Runtime.InteropServices;
using Pane.Contracts;
using Pane.Settings;
using Pane.Services.Launcher;

static partial class Program
{
    static async Task AppHotkeyTests(){
        var bindings=new AppHotkeyBindings();
        Hotkey launcher=new(KeyModifiers.Ctrl,32);
        AppHotkey first=new("app:a","A",new(KeyModifiers.Alt,65));
        AppHotkey second=new("app:b","B",new(KeyModifiers.Alt,66));
        Dictionary<int,Hotkey> registered=[];
        List<int> ids=[];
        void Bind(bool enabled,bool suspended,params AppHotkey[] entries){
            Pane.Hosting.ShellHotkeyBindings.Rebind(ids,id=>registered.Remove(id),(id,_,key)=>{ids.Add(id);registered.Add(id,key);},launcher,launcher,false,suspended,enabled);
            bindings.Bind(entries,enabled&&!suspended,id=>id!="missing",(id,key)=>{
                if(registered.ContainsValue(key) || key.VirtualKey==67)return false;
                ids.Add(id);registered.Add(id,key);return true;
            });
        }
        Bind(true,false,first,second);
        Check(registered.Keys.Order().SequenceEqual([1,5,6]) && bindings.AppFor(6)==second.AppId,"两项注册");
        first=first with{Hotkey=new(KeyModifiers.Win,65)};
        Bind(true,false,first);
        Check(registered.Count==2&&registered[5]==first.Hotkey&&bindings.AppFor(6)==null,"修改与删除");
        foreach(var (enabled,suspended) in new[]{(false,false),(true,false),(true,true),(true,false)}){
            Bind(enabled,suspended,first);
            bool active=enabled&&!suspended;
            Check(registered.Count==(active?2:0)&&bindings.State(first.AppId)==(active?AppHotkeyState.Active:AppHotkeyState.Off),"暂停或停用后恢复");
        }
        Bind(true,false,first with{Hotkey=launcher},second with{Hotkey=new(KeyModifiers.Ctrl,67)},first with{AppId="missing"});
        Check(bindings.State(first.AppId)==AppHotkeyState.Occupied&&bindings.State(second.AppId)==AppHotkeyState.Occupied&&bindings.State("missing")==AppHotkeyState.AppMissing,"冲突与缺失");
        Bind(true,false,first with{Hotkey=new(KeyModifiers.None,162){DoubleTap=true}});
        Check(registered.Keys.SequenceEqual([1])&&bindings.State(first.AppId)==AppHotkeyState.Off,"双击停用");
        List<string> actions=[];
        nint foreground=0;
        Task Press(List<nint> windows)=>AppHotkeyWindows.SwitchOrLaunch(windows,foreground,w=>w==1,
            w=>actions.Add("restore"),w=>{foreground=w;actions.Add("activate"+w);},()=>{actions.Add("launch");return Task.CompletedTask;});
        await Press([]);await Press([1]);
        Check(actions.SequenceEqual(["launch","restore","activate1"]),"启动、还原和激活");
        actions.Clear();
        await Press([1,3,2]);await Press([2,1,3]);await Press([3,2,1]);
        Check(actions.SequenceEqual(["activate2","activate3","restore","activate1"]),"三窗轮换");
    }

#if PANE_HIDDEN_DESKTOP
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowExW(int ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] static extern int GetWindowLongW(nint w,int index);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint w,nint after,int x,int y,int cx,int cy,uint flags);
    [StructLayout(LayoutKind.Sequential)]
    struct Message { public nint window; public uint id;public nuint w;public nint l;public uint time;public int x,y;public uint reserved; }
    [DllImport("user32.dll")] static extern bool PeekMessageW(out Message message,nint window,uint min,uint max,uint remove);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref Message message);
    static bool Above(nint window,nint other){
        for(var next=GetWindow(window,2);next!=0;next=GetWindow(next,2))if(next==other)return true;
        return false;
    }
    static async Task HiddenAppHotkeyTest(string exe){
        HiddenDesktop027.Require();
        var log=exe+".log";
        var app=new IndexedApp("app:probe",new("probe",[]),exe,false,null){Target=exe};
        int launches=0;
        Task Launch(){launches++;return new WindowsLauncherExecutor(_=>{}).OpenAsync(exe,false,default);}
        try{
            await AppHotkeyWindows.OpenAsync(app,Launch);
            var timer=Stopwatch.StartNew();
            while(!File.Exists(exe+".ready")&&timer.ElapsedMilliseconds<8000)await Task.Delay(30);
            nint window=(nint)long.Parse(File.ReadAllText(exe+".ready"));
            Check(launches==1&&IsIconic(window),"已启动最小化测试窗口");
            var witness=CreateWindowExW(0,"STATIC","122",0x10cf0000,0,0,50,50,0,0,0,0);
            try{
            SetWindowPos(witness,-2,0,0,0,0,0x13);
            SetWindowPos(witness,0,0,0,0,0,0x13);
            Check(witness!=0&&Above(witness,window),"测试窗口初始在后");
            AppHotkeyWindows.OpenAsync(app,Launch).GetAwaiter().GetResult();
            timer.Restart();
            while((IsIconic(window)||!Above(window,witness))&&timer.ElapsedMilliseconds<3000){
                while(PeekMessageW(out var message,0,0,0,1))DispatchMessageW(ref message);
                Thread.Sleep(10);
            }
            File.WriteAllText(log,$"launches={launches} restored={!IsIconic(window)} raised={Above(window,witness)} styles={GetWindowLongW(window,-20):X}/{GetWindowLongW(witness,-20):X} hwnd={window} foreground={GetForegroundWindow()}");
            Check(launches==1&&!IsIconic(window)&&Above(window,witness),"已有窗口还原并切到对照窗口之前");
            }finally{DestroyWindow(witness);}
        } catch(Exception ex){File.AppendAllText(log,ex.ToString());throw;}
        finally{File.WriteAllText(exe+".exit","");}
    }
#endif
}
