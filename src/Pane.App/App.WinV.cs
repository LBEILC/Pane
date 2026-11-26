namespace Pane;

public sealed partial class App
{
    async Task StartWinV()
    {
        // 开发、诊断和演示实例不连接真实注册表或资源管理器。
        if (!IsProduction || demo || shell == null) return;
        var service = new Services.WinV.WinVReplacement(IsPortable ? Path.Combine(Store.DirectoryPath,"machines",Settings.WindowsMachine.Key()) : Store.DirectoryPath,
            new Services.WinV.WindowsWinVRegistry(),
            new Services.WinV.ExplorerRestart(new Services.WinV.WindowsExplorerPlatform()), shell);
        WinVService = service;
        shell.WinVRegistrationChanged += service.RegistrationChanged;
        await service.InitializeAsync(cancellation.Token);
    }
}
