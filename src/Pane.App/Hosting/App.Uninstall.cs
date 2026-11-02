using Pane.Hosting;
using Pane.Services.Setup;
using Pane.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pane;
public sealed partial class App
{
    SetupPaths InstalledPaths => SetupPaths.Production.ResolveInstalled();
    public bool CanUninstall => IsProduction && File.Exists(InstalledPaths.Uninstaller)
        && SetupFiles.Full(Environment.ProcessPath!).Equals(SetupFiles.Full(InstalledPaths.Exe), StringComparison.OrdinalIgnoreCase);
    public void StartUninstall()
    {
        try
        {
            if (!CanUninstall) throw new InvalidOperationException();
            using var process = UninstallChain.Start(new ProcessStartInfo(InstalledPaths.Uninstaller)
                { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        catch (Exception error)
        {
            UninstallChain.Log("settings-entry", "failed", error);
            UninstallMessage(0, string.Format(Strings.Setup_UninstallChainFailed, "settings-entry", UninstallChain.LogPath), "Pane", 0x10);
        }
    }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    static extern int UninstallMessage(nint window, string text, string caption, uint flags);
}
