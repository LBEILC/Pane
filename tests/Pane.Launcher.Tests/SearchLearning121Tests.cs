using System.Diagnostics;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    sealed class Files121(string path) : ILauncherFiles
    {
        public bool Include;
        public TaskCompletionSource? Gate;
        public async Task<IReadOnlyList<LauncherItem>> SearchAsync(string query, int limit, CancellationToken token)
        {
            if (Gate != null) await Gate.Task.WaitAsync(token);
            return Include ? [new("file:" + path, LauncherItemKind.File, LauncherGroups.Files, Path.GetFileName(path),
                Path.GetDirectoryName(path), new("doc"), FileSearch.Highlights(Path.GetFileName(path), query))] : [];
        }
    }

    static async Task SearchLearning121Tests()
    {
        string root = TestResults.NewDirectory("121-");
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Defaults());
        var path = Path.Combine(root, "名字.psd");
        File.WriteAllText(path, "");
        var files = new Files121(path);
        var table = new PinyinTable();
        string[] names = ["抖音","dymail","dyalpha","Steam","bd 天气","f 天气","fy 天气","ai 天气","微信","计算器","Chrome","设置","notepad.exe"];
        var catalog = names.Concat(Enumerable.Range(1, 1000 - names.Length).Select(i => $"应用 {i}"));
        var index = new Index076(catalog.Select(name => new IndexedApp("app:" + name, table.Create(name), name, false, null)).ToArray());
        LauncherSearch Open() => new(store, index, files, new FakeExecutor(), new Environment037(), new Process037(), actionPlatform:new ActionPlatform());
        using var search = Open();
        await search.Indexing;
        Task<LauncherResult> Find(string query) => search.SearchAsync(query, default);
        async Task Pick(string query, string id) => await search.ExecuteAsync((await Find(query)).Items.Single(i => i.Id == id), default);
        static string? Best(LauncherResult result) => result.Items.SingleOrDefault(i => i.Group == LauncherGroups.Best)?.Id;

        var original = (await Find(" DY ")).Items.Single(i => i.Title == "抖音");
        await Find("steam");
        await search.ExecuteAsync(original, default);
        await search.ExecuteElevatedAsync(original, default);
        files.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LauncherResult? partial = null;
        var result = await search.SearchAsync("dy", r => { partial = r; files.Gate.SetResult(); }, default);
        files.Gate = null;
        Check(partial?.Items[0].Id == original.Id && partial.Items[0].Group == LauncherGroups.Best, "本地首批最佳");
        var promoted = result.Items[0];
        Check((promoted.Id == original.Id && promoted.Title == original.Title &&
            promoted.Subtitle == original.Subtitle && promoted.Kind == original.Kind && promoted.Icon == original.Icon &&
            promoted.Highlights!.SequenceEqual(original.Highlights!)), "最佳保留字段");
        Check(Best(result) == original.Id && result.Items.Count(i => i.Id == original.Id) == 1 &&
            result.Items.Any(i => i.Title == "dymail" && i.Group == LauncherGroups.Applications), "抖音提升且去重");
        await Pick("dy", original.Id);
        await Pick("dy", "app:dymail");
        result = await Find("dy");
        Check(Best(result) == original.Id && result.Items.First(i => i.Group == LauncherGroups.Applications).Title == "dymail", "三比一及同组前移");
        await Pick("dy","app:dymail"); await Pick("dy","app:dymail");
        Check(Best(await Find("dy")) == "app:dymail", "平票按最近一次");
        await Pick("dy","app:dymail");
        Check(Best(await Find("dy")) == "app:dymail", "四比三");
        await Pick("steam","app:Steam");
        Check(Best(await Find("st")) == "app:Steam" && Best(await Find("s")) == null, "前缀限两个字");
        await Pick("st","app:Steam");
        await search.RunAsync((await Find("st")).Items[0], LauncherItemAction.HideFromSearch, default);
        Check(!(await Find("st")).Items.Any(i => i.Id == "app:Steam"), "隐藏立即生效");
        search.Unhide("app:Steam");
        Check(Best(await Find("st")) == null && Best(await Find("steam")) == null, "隐藏删除各输入记录");
        await Pick("steam","app:Steam");
        await search.RunAsync((await Find("steam")).Items[0], LauncherItemAction.RemoveFromRecent, default);
        Check(Best(await Find("steam")) == null, "移除最近使用");
        files.Include = true;
        await Pick("名字","file:" + path);
        Check(Best(await Find("名字")) == "file:" + path, "文件候选");
        files.Include = false;
        Check(Best(await Find("名字")) == "file:" + path, "补出仍存在的文件");
        File.Delete(path);
        files.Include = true;
        Check(Best(await Find("名字")) == null, "过时的文件候选不能提升");
        files.Include = false;
        void Enabled(bool value) => store.Update(s => s with { Launcher = s.Launcher with { LearnSelections = value } });
        Enabled(false);
        Check(Best(await Find("dy")) == null, "关闭后不提升");
        await Pick("steam","app:Steam");
        Enabled(true);
        Check(Best(await Find("steam")) == null, "关闭时未记录");
        search.SearchMemory.Clear();
        Check(search.SearchMemory.Count == 0 && Best(await Find("dy")) == null, "清除立即生效");
        foreach (var q in names.Skip(4).Take(4)) await Pick(q,"app:" + q);
        await search.ExecuteAsync((await Find(">")).Items[0], default);
        await search.ExecuteAsync((await Find("1+2")).Items[0], default);
        await search.ExecuteAsync((await Find("")).Items[0], default);
        Check(search.SearchMemory.Count == 0, "关键字、终端、算式和首页均不记");
        await Pick("dy", original.Id);
        search.Dispose();
        using var restarted = Open();
        await restarted.Indexing;
        Check(Best(await restarted.SearchAsync("dy", default)) == original.Id, "重启恢复");
        StorageLimits121(root);
        Console.WriteLine($"PASS 121 assertions={checks}");
        await SearchTiming121(restarted, files, Enabled);
    }

    static void StorageLimits121(string root)
    {
        string dir = Path.Combine(root, "limits");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "search-memory.json");
        File.WriteAllText(path, "{");
        using (var empty = new SearchMemory(dir))
            Check(empty.Count == 0 && File.Exists(Path.ChangeExtension(path, ".log")), "坏文件按空处理并留日志");
        var now = DateTimeOffset.UtcNow;
        Dictionary<string,Selection[]> seed = Enumerable.Range(0, 2000).ToDictionary(i => $"key{i}", i => new[]{new Selection("app:a", now.AddSeconds(-2001+i))});
        seed["expired"] = [new("app:b", now.AddDays(-91))];
        File.WriteAllText(path, JsonSerializer.Serialize(seed, SearchMemoryJson.Default.DictionaryStringSelectionArray));
        using (var memory = new SearchMemory(dir))
        {
            Check(memory.Count == 2000 && memory.Ranked("expired").Length == 0, "九十天过期");
            memory.Record("额外","app:b");
            Check(memory.Count == 2000 && memory.Ranked("key0").Length == 0, "淘汰最早使用的输入");
            for (int i = 0; i < 10; i++) memory.Record("dy", "app:a");
            for (int i = 0; i < 6; i++) memory.Record("dy", "app:b");
            Check(memory.Ranked("dy")[0] == "app:b", "仅最近十次投票");
            memory.Record("dyin", "app:c");
            memory.Record("dying", "app:d");
            Check(memory.Ranked("dy")[0] == "app:b" && memory.Ranked("dyi")[0] == "app:d", "完全匹配优先，前缀取最近输入");
        }
        using var restored = new SearchMemory(dir);
        Check(restored.Count == 2000 && restored.Ranked("dy")[0] == "app:b", "落盘后保留限制和排序");
    }

    static async Task SearchTiming121(LauncherSearch search, Files121 files, Action<bool> enabled)
    {
        string[] queries = ["wx","计算器","chrome","设置","notepad.exe"];
        foreach (var query in queries)
        {
            var item = (await search.SearchAsync(query, default)).Items.First(i => i.Kind == LauncherItemKind.App);
            await search.ExecuteAsync(item, default);
        }
        foreach (var query in queries)
        {
            List<double>[] samples = [[], []];
            for (int round = -4; round < 20; round++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int mode = (round + side) & 1;
                    enabled(mode == 1);
                    files.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    long start = Stopwatch.GetTimestamp();
                    double first = -1;
                    await search.SearchAsync(query, r =>
                    {
                        first = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        files.Gate.SetResult();
                    }, default);
                    files.Gate = null;
                    if (first < 0) throw new Exception("首批未到达");
                    if (round >= 0) samples[mode].Add(first);
                }
            }
            foreach (var sample in samples) sample.Sort();
            string Stats(List<double> s) => $"{(s[9]+s[10])/2:F3}/{s[17]:F3}";
            Console.WriteLine($"{query} off={Stats(samples[0])} on={Stats(samples[1])} ms");
        }
    }
}
