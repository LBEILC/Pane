using Pane.Localization;

namespace Pane;

public sealed partial class App
{
    public string UiLanguage { get; private set; } = "zh-Hans";

    // 繁体翻译完成前（设计，2026-11-02）：跟随系统一律用简体，免得繁体系统的用户看到简繁混杂；只有手动选了繁体才用繁体。翻完改成 true
    const bool TraditionalReady = false;

    public static string ResolveUiLanguage(string? configured) => TraditionalReady || configured == "zh-Hant"
        ? UiLanguageResolver.Resolve(configured, UiLanguageResolver.SystemLanguage())
        : "zh-Hans";

    void ApplyUiLanguage()
    {
        UiLanguage = ResolveUiLanguage(Store.Current.General.UiLanguage);
        Strings.Initialize(UiLanguage);
    }
}
