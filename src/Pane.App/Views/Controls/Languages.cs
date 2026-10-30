// 翻译用的语言判断（翻译窗口、超级面板自动翻译共用）：原文是中文译成英文，其余译成设置里的「翻译成」（自动 = 中文）。
namespace Pane.Views.Controls;

public static class Languages
{
    public static readonly (string Code, string Name)[] All =
        [("zh", "中文"), ("en", "英文"), ("ja", "日文"), ("ko", "韩文"), ("fr", "法文"), ("de", "德文"), ("es", "西班牙文"), ("ru", "俄文")];

    /// <summary>中文为主（夹几个英文单词也算中文）。</summary>
    public static bool IsChinese(string s)
    {
        int han = 0, letters = 0;
        foreach (var c in s)
        {
            if (c is >= '一' and <= '鿿') han++;
            else if (char.IsLetter(c)) letters++;
        }
        return han > 0 && han >= letters / 4;
    }

    /// <summary>粗略识别原文语言，给界面显示用（「自动识别：英文」）。</summary>
    public static string Detect(string s) =>
        IsChinese(s) ? "中文"
        : s.Any(c => c is >= '぀' and <= 'ヿ') ? "日文"
        : s.Any(c => c is >= '가' and <= '힯') ? "韩文"
        : s.Count(char.IsAsciiLetter) * 2 >= s.Count(char.IsLetter) ? "英文" : "外文";

    /// <summary>自动的目标语言：原文是中文译成英文，否则按设置（auto = 中文）。</summary>
    public static string AutoTarget(string text, string setting) => IsChinese(text) ? "en" : setting is "auto" or "" ? "zh" : setting;

    public static string NameOf(string code) => All.FirstOrDefault(l => l.Code == code).Name ?? code;
}
