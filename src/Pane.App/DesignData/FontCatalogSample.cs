// 本机字体列表的假数据（不枚举真实字体）：几款常见的中英文字体，看选择界面的排版用。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class FontCatalogSample : IFontCatalog
{
    public Task<IReadOnlyList<FontFamilyInfo>> GetFamiliesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FontFamilyInfo>>(
    [
        new("HarmonyOS Sans SC", "HarmonyOS Sans SC", true),
        new("MiSans VF", "MiSans VF", true),
        new("Microsoft YaHei UI", "微软雅黑 UI", true),
        new("SimHei", "黑体", true),
        new("Noto Sans SC", "思源黑体 SC", true),
        new("Segoe UI Variable Text", "Segoe UI Variable Text", false),
        new("Inter", "Inter", false),
        new("Cascadia Mono", "Cascadia Mono", false),
    ]);
}
