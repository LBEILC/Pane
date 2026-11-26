// 导入后重启（设计负责人，2026-11-08，083）：启动完成时如果有上次导入的结果，自动打开设置的「数据」页，
// 顶部显示导入完成（备份位置）或失败原因。docs/design.md「数据分区」。
// 2026-11-26 改：只提示一次。读到就清掉结果文件，这一行只在本次运行里留着；
// 原来要用户点关闭才清，没点的话每次开机都弹出设置「数据」页。
using Pane.Contracts;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    void ShowImportResult()
    {
        if (SettingsView.DataStorage is not IDataTransfer transfer) return;
        DataImportOutcome? outcome;
        try { outcome = transfer.LastImport; if (outcome is null) return; transfer.AcknowledgeImport(); }
        catch (Exception) { return; }   // 结果文件读不出或删不掉：不打扰
        SettingsView.ShownImport = outcome;
        OpenSettings("data");
    }
}
