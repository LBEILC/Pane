using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

internal static class Program
{
    [STAThread] static void Main()
    {
        var args=Environment.GetCommandLineArgs().Skip(1).ToArray();
        Pane.Hosting.StartupDirectory.Initialize(args);
        if(Pane.Hosting.SetupEntry.TryRun(args,RunApplication))return;
        if(args.FirstOrDefault()=="--launcher-index") { Environment.ExitCode=Pane.Services.Launcher.IndexWorker.Run(args);return; }
        var command=Pane.Hosting.LaunchArguments.Parse(args);if(command==Pane.Hosting.LaunchCommand.Invalid){Environment.ExitCode=2;return;}
        try {Pane.Hosting.RestartProcess.WaitForParent();if(!Pane.Hosting.SingleInstance.Enter(command))return;RunApplication();}
        catch(Exception error){if(Pane.Hosting.RuntimeMode.Current.IsProduction)new Pane.Hosting.CrashLog(Pane.Hosting.RuntimeMode.Current.DataDirectory).Write(error);Environment.ExitCode=1;}
        finally {Pane.Hosting.SingleInstance.Release();}
        if(Pane.Hosting.RestartProcess.Requested && Environment.ExitCode==0) {
            try { Pane.Hosting.RestartProcess.Start(); }
            catch(Exception error){if(Pane.Hosting.RuntimeMode.Current.IsProduction)new Pane.Hosting.CrashLog(Pane.Hosting.RuntimeMode.Current.DataDirectory).Write(error);Environment.ExitCode=1;}
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void RunApplication()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters=>{
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new Pane.App();
        });
    }
}
// 复制 008 的透明内容岛背景。
internal sealed partial class TransparentBackdrop : SystemBackdrop
{
    Windows.UI.Composition.Compositor? compositor;
    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target,XamlRoot root)
    {
        base.OnTargetConnected(target,root);
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        compositor=new();target.SystemBackdrop=compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
    }
    // 窗口激活、系统主题变化时 WinUI 会调这个；基类实现在我们的 DesktopWindowXamlSource 上抛 E_INVALIDARG，
    // 正式版里设置窗口获得 / 失去焦点就会崩（2026-10-29 崩溃记录）。透明背景不随这些配置变化，什么都不做。
    protected override void OnDefaultSystemBackdropConfigurationChanged(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target,XamlRoot root){}
    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {target.SystemBackdrop=null;compositor?.Dispose();base.OnTargetDisconnected(target);}
}
