using Pane.Contracts;
namespace Pane;
public sealed partial class App
{
    // 与 TerminalHistory 一样通过宿主公开，供启动器和设置视图接入。
    public IWebSearchHistory? WebSearchHistory {get;private set;}
    public IEngineIcons? EngineIcons {get;private set;}
    Task ClearWebStorageCache(Action clear,CancellationToken token)=>Pane.Services.Launcher.ProcessLauncherIndex.ClearCacheAsync(
        ()=>{if(EngineIcons is Pane.Services.Launcher.EngineIcons icons)icons.ClearCacheAsync(clear,token).GetAwaiter().GetResult();else clear();},token);
}
