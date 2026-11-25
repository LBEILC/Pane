using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services.Launcher;

internal sealed class AppHotkeyBindings : IAppHotkeys
{
    Dictionary<string,AppHotkeyState> states = [];
    readonly Dictionary<int,string> commands = [];
    public event EventHandler? Changed;
    public AppHotkeyState State(string appId) =>
        Volatile.Read(ref states).GetValueOrDefault(appId,AppHotkeyState.Off);
    internal string? AppFor(int id) => commands.GetValueOrDefault(id);

    // 由宿主线程在旧注册释放、内置组合键注册完毕后调用。
    internal void Bind(AppHotkey[] entries,bool enabled,Func<string,bool> exists,Func<int,Hotkey,bool> register)
    {
        commands.Clear();
        Dictionary<string,AppHotkeyState> next = [];
        int id=5;
        foreach(var entry in entries){
            if(entry?.AppId is not {Length:>0} app || next.ContainsKey(app))continue;
            var key=entry.Hotkey;
            // TODO(design): 双击、空组合和暂停均显示 Off。
            var state=AppHotkeyState.Off;
            if(enabled && key is {DoubleTap:false,VirtualKey:>0} && key.Modifiers!=KeyModifiers.None){
                if(!exists(app))state=AppHotkeyState.AppMissing;
                else if(id<0xbfff && register(id,key)){
                    commands.Add(id,app);
                    state=AppHotkeyState.Active;
                }else state=AppHotkeyState.Occupied;
                id++;
            }
            next.Add(app,state);
        }
        var previous=Interlocked.Exchange(ref states,next);
        if(previous.Count!=next.Count || next.Any(p=>!previous.TryGetValue(p.Key,out var old)||old!=p.Value))
            Changed?.Invoke(this,EventArgs.Empty);
    }
}
