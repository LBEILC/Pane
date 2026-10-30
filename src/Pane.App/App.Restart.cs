using Pane.Hosting;
namespace Pane;
public sealed partial class App
{
    public void Restart()
    {
        if (closing) return;
        RestartProcess.Requested = true;
        Shutdown();
    }
}
