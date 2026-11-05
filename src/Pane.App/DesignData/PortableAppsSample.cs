// 便携软件扫描状态的假数据（设计负责人，057）。真实服务没接上时（SettingsView.PortableApps 为空）设置页用它：
// 文件夹在就当扫到 12 个程序，程序文件在就是 1 个；不在就是「找不到」。只查文件是否存在，不扫描。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class PortableAppsSample : IPortableAppsStatus
{
    public static readonly PortableAppsSample Instance = new();

    public PortableStatus Get(string path) =>
        Directory.Exists(path) ? new(PortableScanState.Ready, 12)
        : File.Exists(path) ? new(PortableScanState.Ready, 1)
        : new(PortableScanState.Missing, 0);

    public event EventHandler? Changed { add { } remove { } }
}
