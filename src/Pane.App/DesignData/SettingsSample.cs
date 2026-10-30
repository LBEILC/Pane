// 设置页在演示模式下的假数据（设计负责人）。演示模式不接真实服务（App.FileSearchStatus 为 null），
// Everything 状态用这里的值；「重新检测」在两种状态间切换，方便截图。其余选项都已读写真实设置（013 起）。
namespace Pane.DesignData;

public static class SettingsSample
{
    public static bool EverythingRunning = true;
}
