using Pane.Localization;
using System.Globalization;

Strings.Initialize("zh-Hans");
int checks = 0;
void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
foreach (string language in new[] { "zh-TW", "zh-HK", "zh-MO", "zh-Hant", "zh-CN", "en-US", "ja-JP" })
{
    bool traditional = language is "zh-TW" or "zh-HK" or "zh-MO" or "zh-Hant";
    Check(UiLanguageResolver.Resolve(null, language) == (traditional ? "zh-Hant" : "zh-Hans"), language);
    foreach (string configured in new[] { "zh-Hans", "zh-Hant" })
        Check(UiLanguageResolver.Resolve(configured, language) == configured, "显式覆盖 " + language);
}
foreach (var failure in new Exception[] { new CultureNotFoundException(), new InvalidOperationException(), new System.Runtime.InteropServices.ExternalException() })
{
    var language = UiLanguageResolver.SystemLanguage(() => throw failure);
    Check(language == "" && UiLanguageResolver.Resolve(null, language) == "zh-Hans", "读取系统语言异常回落");
}
Check(UiLanguageResolver.SystemLanguage(() => "zh-TW") == "zh-TW", "系统语言正常返回");
var format = CultureInfo.CurrentCulture;
Strings.Initialize("zh-Hant");
Check(Strings.Tray_Settings == "設定", "繁体资源");
Check(Strings.Launcher_Group_Favorites == "常用", "繁体缺项回落");
Check(CultureInfo.CurrentCulture == format, "保留格式 culture");
string pseudo = Strings.PseudoLocalize("已复制 {0} 项");
Check(pseudo.StartsWith('［') && pseudo.EndsWith('］'), "伪本地化标记");
Check(string.Format(pseudo, 3).Contains("3") && pseudo.Contains("{0}"), "占位符");
Check(pseudo.Length >= 9 * 1.4, "扩展长度");
var previousPseudo = Environment.GetEnvironmentVariable("PANE_PSEUDO_LOC");
try
{
    Environment.SetEnvironmentVariable("PANE_PSEUDO_LOC", "1");
    Strings.Initialize("zh-Hant");
    Check(Strings.Tray_Settings.StartsWith("［設定"), "环境变量覆盖资源读取");
    Check(string.Format(Strings.Tray_UpdateReady, "1.2.3").Contains("1.2.3"), "真实整句占位符");
    Check(Strings.Launcher_Group_Favorites.StartsWith("［常用"), "伪本地化覆盖回落资源");
    Environment.SetEnvironmentVariable("PANE_PSEUDO_LOC", null);
    Strings.Initialize("zh-Hant");
    Check(Strings.Tray_Settings == "設定", "普通模式不转换");
}
finally { Environment.SetEnvironmentVariable("PANE_PSEUDO_LOC", previousPseudo); }
Console.WriteLine($"PASS localization assertions={checks}; traditional={Strings.Tray_Settings}; fallback={Strings.Launcher_Group_Favorites}");
Strings.Initialize("zh-Hans");
