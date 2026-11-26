using System.Diagnostics;
using Pane.Contracts;
using Pane.Services;
using Pane.Services.Launcher;

static partial class Program
{
    sealed class DialogEndpoint : IEverythingIpc
    {
        public readonly List<(string Query, int Count)> Calls = [];
        public bool WaitFiles;
        public bool Unavailable;
        public async Task<EverythingPage> QueryAsync(string query, int offset, int count, int sort, CancellationToken token, int timeoutMs)
        {
            Calls.Add((query, count));
            if (Unavailable) throw new FileSearchUnavailableException("fixture");
            bool folder = query.EndsWith("folder:");
            if (WaitFiles && !folder) await Task.Delay(Timeout.Infinite, token);
            FileHit hit = new(@"D:\fixture\match", "Match", folder ? FileCategory.Folder : FileCategory.Text, null, null);
            return new(Enumerable.Range(0,Math.Min(20,count)).Select(i => hit with { Path = hit.Path + i }).ToList(), 20);
        }
    }
    static async Task DialogSearchTests()
    {
        int before = checks;
        var endpoint = new DialogEndpoint();
        var service = new DialogSearchService(endpoint);
        Check((await service.SearchAsync(" ", null, 3, default)).Items.Count == 0 && endpoint.Calls.Count == 0, "空输入跳过 IPC");
        var hits = (await service.SearchAsync(" at ", new(1,0,0,0,0,true,false,false,["png","jpg"],null), 3, default)).Items;
        Check(endpoint.Calls.SequenceEqual([("<at> folder:",512),("<at> file: ext:png;jpg",512)]), "分组查询与扩展名条件");
        Check(hits.Count == 11 && hits.Take(8).All(x => x.Category == FileCategory.Folder) && hits.Skip(8).All(x => x.Category == FileCategory.Text), "组顺序和数量");
        Check(hits.All(x => x.Highlights!.SequenceEqual([new TextSpan(1,2)])), "匹配高亮");
        endpoint.WaitFiles = true;
        var watch = Stopwatch.StartNew();
        var partial = await service.SearchAsync("at", null, 3, default);
        Check(partial.Items.Count == 8 && partial.Total == 8 && watch.ElapsedMilliseconds is >= 850 and < 1600, "一秒后保留已取得的组");
        using var cancel = new CancellationTokenSource(50);
        watch.Restart();
        try { await service.SearchAsync("at", null, 3, cancel.Token); Check(false, "取消"); }
        catch (OperationCanceledException) { Check(watch.ElapsedMilliseconds < 500, "取消及时返回"); }
        endpoint.Unavailable = true;
        try { await service.SearchAsync("at", null, 3, default); Check(false, "不可用"); }
        catch (FileSearchUnavailableException) { Check(true, "IPC 不可用"); }
        foreach (var (label, name, extensions) in new (string, string?, string[]?)[] {
            ("所有文件 (*.*)", "所有文件", null), ("文档 (*.TXT)", "文档", ["txt"]),
            ("图像 (*.png;*.JPG)", "图像", ["png","jpg"]), ("", null, null), ("自定义", "自定义", null) })
        {
            var parsed = DialogSearchService.ParseType(label);
            Check(parsed.Name == name && (extensions == null ? parsed.Extensions == null : parsed.Extensions!.SequenceEqual(extensions)), "文件类型 " + label);
        }
        Console.WriteLine($"dialog-search assertions={checks-before}");
    }
}
