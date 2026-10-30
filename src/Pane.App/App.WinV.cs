namespace Pane;

public sealed partial class App
{
    void StartWinV()
    {
        // 开发、诊断和演示实例不连接真实注册表或资源管理器。
        if (!IsProduction || demo || shell == null) return;
        var service = new Services.WinV.WinVReplacement(Store.DirectoryPath,
            new Services.WinV.WindowsWinVRegistry(),
            new Services.WinV.ExplorerRestart(new Services.WinV.WindowsExplorerPlatform()), shell);
        WinVService = service;
        shell.WinVRegistrationChanged += service.RegistrationChanged;
        _ = service.InitializeAsync(cancellation.Token);
    }
}
