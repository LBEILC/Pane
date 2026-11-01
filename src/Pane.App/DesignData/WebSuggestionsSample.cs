// 网页搜索建议的演示数据（设计负责人，037）：不联网，按输入编几条像样的建议。
using Pane.Contracts;
using Pane.Settings;

namespace Pane.DesignData;

public sealed class WebSuggestionsSample : IWebSuggestions
{
    public async Task<IReadOnlyList<string>> SuggestAsync(SearchEngine engine, string query, CancellationToken cancellationToken)
    {
        await Task.Delay(120, cancellationToken);
        return query.Contains("天气") ? [query + "预报", query + " 15 天", "明天" + query, query + "怎么样"]
            : [query + " 是什么", query + " 教程", query + " 下载", query + " 官网"];
    }
}
