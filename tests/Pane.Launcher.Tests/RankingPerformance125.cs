using System.Diagnostics;
using Pane.Contracts;
using Pane.Services;
using Pane.Services.Launcher;

static partial class Program
{
    static async Task RankingPerf125()
    {
        Environment.SetEnvironmentVariable("PANE_DATA_DIR",TestResults.NewDirectory("125-perf-"));
        var ipc = new EverythingIpc();
        var version = new int[4];
        if (LauncherNative.PaneEverythingVersion(version) != 1) throw new Exception("Everything IPC 不可用");
        Console.WriteLine("Everything " + string.Join('.',version));
        foreach (var modifier in new[] { "wfn:", "startwith:" })
        {
            var page = await ipc.QueryAsync(modifier + "\"Windows\"",0,50,1,default);
            Check(page.Items.Count > 0 && page.Items.All(h => modifier == "wfn:" ?
                h.Name.Equals("Windows",StringComparison.OrdinalIgnoreCase) : h.Name.StartsWith("Windows",StringComparison.OrdinalIgnoreCase)),modifier);
            Console.WriteLine($"{modifier} total={page.Total} checked={page.Items.Count}");
        }
        string[] queries = ["ai","AIPro","图片","qzxpv125nomatch"];
        if (Environment.GetCommandLineArgs().Contains("--ipc"))
        {
            foreach (var q in queries)
            {
                Task<EverythingPage> Read(string text, int count, int sort) => ipc.QueryAsync(text,0,count,sort,default);
                Task<EverythingPage> A() => Read($"wfn:\"{q}\"",50,1);
                Task<EverythingPage> B() => Read($"startwith:\"{q}\"",300,1);
                Task<EverythingPage> C() => Read(q,300,14);
                var cases = new List<(string Name, Func<Task<EverythingPage[]>> Run)>();
                foreach (int sort in new[] { 1, 14 })
                    foreach (int count in new[] { 8, 50, 300 })
                        cases.Add(($"{sort}/{count}", async () => [await Read(q,count,sort)]));
                cases.Add(("A", async () => [await A()]));
                cases.Add(("B", async () => [await B()]));
                cases.Add(("AB", () => Task.WhenAll(A(),B())));
                cases.Add(("ABC", () => Task.WhenAll(A(),B(),C())));
                var times = cases.ToDictionary(c => c.Name, _ => new List<double>());
                var totals = new Dictionary<string,string>();
                for (int round=0; round<=10; round++)
                    foreach (var (name,run) in cases)
                    {
                        var sw = Stopwatch.StartNew();
                        var pages = await run();
                        if (round>0) times[name].Add(sw.Elapsed.TotalMilliseconds);
                        totals[name] = string.Join("/",pages.Select(p=>p.Total));
                    }
                foreach (var (name,ms) in times)
                {
                    ms.Sort();
                    Console.WriteLine($"{q} {name} median={(ms[4]+ms[5])/2:F1} p90={ms[8]:F1} total={totals[name]}");
                }
            }
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--recall"))
        {
            foreach (var q in queries)
            {
                var dialog = await new DialogSearchService(ipc).SearchAsync(q,null,12,default);
                async Task Compare(string group, Func<string,string> filter, int[] limits)
                {
                    var recent = await ipc.QueryAsync(filter(q),0,300,14,default);
                    IEnumerable<FileHit> previous = recent.Items;
                    if (recent.Total > recent.Items.Count)
                    {
                        var exact = await ipc.QueryAsync(filter($"wfn:\"{q}\""),0,50,1,default);
                        var prefix = await ipc.QueryAsync(filter($"startwith:\"{q}\""),0,300,1,default);
                        previous = previous.Concat(exact.Items).Concat(prefix.Items);
                    }
                    var old = FileRanking.Rank(previous,q,[]);
                    var head = await FileRanking.HeadAsync(ipc,q,filter,[],default,3000,false);
                    var candidates = head.Items.Select(h=>h.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (int limit in limits)
                    {
                        IEnumerable<FileHit> shown = group switch
                        {
                            "folder" => dialog.Items.Where(h=>h.Category==FileCategory.Folder),
                            "file" => dialog.Items.Where(h=>h.Category!=FileCategory.Folder),
                            _ => head.Items
                        };
                        var top = shown.Take(limit).Select(h=>h.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var before = old.Take(limit).ToArray();
                        Console.WriteLine($"{q} {group}/{limit} kept={before.Count(h=>top.Contains(h.Path))}/{before.Length} absent={before.Count(h=>!candidates.Contains(h.Path))}");
                    }
                }
                await Compare("all",s=>s,[8,50]);
                await Compare("folder",s=>FileSearch.Filter(s,FileCategory.Folder),[8]);
                await Compare("file",s=>$"<{s}> file:",[8,12]);
            }
            return;
        }
        foreach (string entry in new[] { "Launcher", "Dialog", "Window" })
        {
            var window = new FileSearch(ipc,new FakeExecutor());
            var dialog = new DialogSearchService(ipc);
            var launcher = new EverythingSearch(ipc);
            var samples = queries.ToDictionary(q => q,q => new List<double>());
            for (int i=0; i<=20; i++)
                foreach (string q in queries)
                {
                    var sw = Stopwatch.StartNew();
                    if (entry == "Launcher") await launcher.SearchAsync(q,8,default);
                    else if (entry == "Dialog") await dialog.SearchAsync(q,null,8,default);
                    else await window.SearchAsync(new(q,FileCategory.All,FileSort.Relevance),0,50,default);
                    if (i>0) samples[q].Add(sw.Elapsed.TotalMilliseconds);
                    if (entry == "Window") await window.CountsAsync(q,FileCategory.All,0);
                }
            foreach (var (q,ms) in samples)
            {
                ms.Sort();
                Console.WriteLine($"{entry} {q} median={(ms[9]+ms[10])/2:F1} p90={ms[17]:F1}");
            }
        }
    }
}
