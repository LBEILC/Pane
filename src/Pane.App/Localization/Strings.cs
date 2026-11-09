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

    // 资源里名字两边按「中英文之间加空格」写好空格（「用 {0} 搜索」）；名字本身以中文开头或结尾时，去掉那一侧的空格（「用百度搜索」）。
    // 只处理 {0} 紧挨着的空格，其余占位符（用户输入的搜索词等）原样。
    public static string FormatName(string template, string name, params object?[] rest)
    {
        if (name.Length > 0)
        {
            if (IsWide(name[0])) template = template.Replace(" {0}", "{0}");
            if (IsWide(name[^1])) template = template.Replace("{0} ", "{0}");
        }
        return string.Format(template, [name, .. rest]);
    }

    static bool IsWide(char c) => c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    public static string PseudoLocalize(string text) => "［" + text + new string('～', (int)Math.Ceiling(text.Length * .4)) + "］";

    sealed class PseudoResourceManager() : ResourceManager("Pane.Localization.Strings", typeof(Strings).Assembly)
    {
        readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> cache = new();
        public override string? GetString(string name, CultureInfo? culture) =>
            cache.GetOrAdd((name, (culture ?? Culture).Name), key => PseudoLocalize(base.GetString(key.Item1, CultureInfo.GetCultureInfo(key.Item2))!));
    }
}
