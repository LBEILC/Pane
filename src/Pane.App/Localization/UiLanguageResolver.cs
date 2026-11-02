using System.Globalization;
using System.Runtime.InteropServices;

namespace Pane.Localization;

public static partial class UiLanguageResolver
{
    public static string Resolve(string? configured, string systemLanguage) => configured switch
    {
        "zh-Hans" or "zh-Hant" => configured,
        _ => systemLanguage.ToLowerInvariant() switch
        {
            "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-Hant",
            _ => "zh-Hans"
        }
    };

    public static string SystemLanguage() => SystemLanguage(() => CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).Name);

    // 将读取过程作为测试接缝；自定义区域和系统 API 异常不能阻止启动。
    internal static string SystemLanguage(Func<string> read)
    {
        try { return read(); }
        catch (Exception) { return ""; }
    }

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
