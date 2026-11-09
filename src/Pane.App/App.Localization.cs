using Pane.Localization;

namespace Pane;

public sealed partial class App
{
    public string UiLanguage { get; private set; } = "zh-Hans";

    // 繁体翻译完成前（设计，2026-11-02）跟随系统一律用简体，免得繁体系统的用户看到简繁混杂；
    // 2026-11-03 贡献者的完整繁体（issue 9）合进来，改回「繁体系统显示繁体」。以后加字串要同时重新生成繁体（scripts/generate-zh-hant.py）
    const bool TraditionalReady = true;

    public static string ResolveUiLanguage(string? configured) => TraditionalReady || configured == "zh-Hant"
        ? UiLanguageResolver.Resolve(configured, UiLanguageResolver.SystemLanguage())
        : "zh-Hans";

    public void ApplyUiLanguage(string? configured = null)
    {
        UiLanguage = ResolveUiLanguage(configured ?? Store?.Current.General.UiLanguage);
        Strings.Initialize(UiLanguage);
    }
}
