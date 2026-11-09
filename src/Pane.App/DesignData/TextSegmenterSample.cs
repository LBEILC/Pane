using System.Text.RegularExpressions;
using Pane.Contracts;
// 分词的演示服务（设计负责人，093）：演示模式和工程实现做好之前用。照原型 prototype/words.html 的做法凑效果：
// 先认整块（网址、邮箱、电话、日期时间、金额、订单号），再把汉字串按一份小词表从左往右取最长的词，取不到就单字；字母数字连成一块。
// 不代表产品效果，产品用系统分词（docs/design.md「分词」）。
namespace Pane.DesignData;

public sealed partial class TextSegmenterSample : ITextSegmenter
{
    static readonly HashSet<string> Dict = [.. ("收货 地址 上海市 浦东新区 张江路 电话 邮编 收件人 王小明 工作日 明天 下午 三点 会议室 产品 评审会 记得 带上 笔记本电脑 " + // 不翻译：演示词表
        "会议 链接 有问题 张伟 发邮件 到账 订单号 折扣 如需 开票 请在 天内 联系 客服 小程序 快速启动 剪贴板 历史 鼠标手势 工具 界面 半透明 磨砂玻璃 " +
        "大圆角 阴影 跟着 系统 深浅色 能力 可以 设置 单独 开关 停用 热键 钩子 安装包 只有 下载 选中 文字 长按 翻译 总结 润色 打开 分词 复制 其中").Split(' ')];

    [GeneratedRegex(@"\G(?:(?<link>https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'*+,;=%]*[A-Za-z0-9/#=_\-])|(?<mail>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})|(?<phone>(?:\+?86[ \-]?)?1[3-9]\d(?:[ \-]?\d{4}){2}(?!\d)|0\d{2,3}-\d{7,8}(?!\d)|400-\d{3}-\d{4}(?!\d))|(?<num>\d{4}-\d{1,2}-\d{1,2}(?!\d)|\d{1,2}:\d{2}(?:-\d{1,2}:\d{2})?(?!\d)|[A-Z]{1,4}\d[\dA-Z]*(?:-\d+)*|[¥$€]?\d{1,3}(?:,\d{3})+(?:\.\d+)?%?|[¥$€]?\d+(?:\.\d+)?%?)|(?<word>[A-Za-z][A-Za-z0-9]*(?:#|\+\+)?))")]
    private static partial Regex Entity();

    public Task<WordSplit> SplitAsync(string text, CancellationToken cancellationToken)
    {
        bool truncated = text.Length > ITextSegmenter.MaxLength;
        if (truncated) text = text[..ITextSegmenter.MaxLength];
        var pieces = new List<WordPiece>();
        int i = 0;
        while (i < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var m = Entity().Match(text, i);
            if (m.Success && m.Length > 0)
            {
                var kind = m.Groups["link"].Success ? WordKind.Link : m.Groups["mail"].Success ? WordKind.Email : m.Groups["phone"].Success ? WordKind.Phone
                    : m.Groups["num"].Success ? WordKind.Number : WordKind.Word;
                if (kind == WordKind.Number && m.Length <= 4 && m.Value.All(char.IsAsciiDigit)) kind = WordKind.Word;   // 门牌、件数当普通词
                pieces.Add(new(m.Value, kind, i, m.Length)); i += m.Length; continue;
            }
            if (IsCjk(c))
            {
                int len = 1;
                for (int n = Math.Min(6, text.Length - i); n >= 2; n--)
                    if (Dict.Contains(text.Substring(i, n))) { len = n; break; }
                pieces.Add(new(text.Substring(i, len), WordKind.Word, i, len)); i += len; continue;
            }
            int step = char.IsSurrogatePair(text, i) ? 2 : 1;
            var kindOf = char.IsLetterOrDigit(c) || char.IsSurrogatePair(text, i) ? WordKind.Word : WordKind.Punctuation;
            pieces.Add(new(text.Substring(i, step), kindOf, i, step)); i += step;
        }
        return Task.FromResult(new WordSplit(text, pieces, truncated));
    }

    static bool IsCjk(char c) => c is >= '㐀' and <= '鿿' or >= '豈' and <= '﫿';
}
