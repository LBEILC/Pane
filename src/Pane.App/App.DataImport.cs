// 导入后重启（设计负责人，2026-11-08，083）：启动完成时如果有上次导入的结果，自动打开设置的「数据」页，
// 顶部显示导入完成（备份位置）或失败原因；用户关掉那一行后才清除结果。docs/design.md「数据分区」。
using Pane.Contracts;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    void ShowImportResult()
    {
        if (SettingsView.DataStorage is not IDataTransfer transfer) return;
        try { if (transfer.LastImport is null) return; }
        catch (Exception) { return; }   // 结果文件读不出：不打扰
        OpenSettings("data");
    }
}
