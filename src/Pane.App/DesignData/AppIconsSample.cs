// 设置页「应用图标」的假数据（演示模式没有启动器索引）。没有位图，界面用占位图块。
using Pane.Contracts;
namespace Pane.DesignData;

public sealed class AppIconsSample : IAppIconCatalog
{
    public IReadOnlyList<AppIconInfo> Apps() =>
    [
        new("app:photoshop", "Adobe Photoshop", null, AppIconKind.Full),
        new("app:obs", "OBS Studio", null, AppIconKind.Plate),
        new("app:steam", "Steam", null, AppIconKind.Neutral),
        new("app:wechat", "微信", null, AppIconKind.Full),
        new("app:netease", "网易云音乐", null, AppIconKind.Plate),
        new("app:vscode", "Visual Studio Code", null, AppIconKind.Plate),
    ];
    public bool Rebuilding => false;
    public event EventHandler? Changed { add { } remove { } }
}
