using System.Globalization;
using System.Resources;

namespace Pane.Localization;

/// <summary>仅控制界面资源，不改变线程的日期、数字或排序 culture。</summary>
public static partial class Strings
{
    static readonly ResourceManager source = new("Pane.Localization.Strings", typeof(Strings).Assembly);
    static ResourceManager manager = source;
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("zh-Hans");

    public static void Initialize(string language)
    {
        Culture = CultureInfo.GetCultureInfo(language);
        // 环境变量只在启动时读一次；普通模式直接使用标准 ResourceManager。
        manager = Environment.GetEnvironmentVariable("PANE_PSEUDO_LOC") == "1"
            ? new PseudoResourceManager() : source;
    }

    // 搜索拼音始终从简体原文生成；调用方用 nameof(强类型属性) 指定键。
    internal static string Simplified(string key) => source.GetString(key, CultureInfo.InvariantCulture)!;

    public static string PseudoLocalize(string text) => "［" + text + new string('～', (int)Math.Ceiling(text.Length * .4)) + "］";

    sealed class PseudoResourceManager() : ResourceManager("Pane.Localization.Strings", typeof(Strings).Assembly)
    {
        readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> cache = new();
        public override string? GetString(string name, CultureInfo? culture) =>
            cache.GetOrAdd((name, (culture ?? Culture).Name), key => PseudoLocalize(base.GetString(key.Item1, CultureInfo.GetCultureInfo(key.Item2))!));
    }
}
