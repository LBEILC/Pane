using System.Diagnostics;
using Pane.Contracts;
using Pane.Services.Launcher;

static partial class Program
{
    static async Task OpenTargets141Tests()
    {
        int before = checks;
        var executor = new FakeExecutor();
        var targets = new OpenTargetService(executor);
        var directory = Directory.CreateTempSubdirectory("pane 141-");
        try
        {
            string folder = directory.FullName;
            string file = Path.Combine(folder, "测试 #1.txt");
            File.WriteAllText(file, "141");
            var folderTarget = new OpenTarget(OpenTargetKind.Folder, folder);
            var fileTarget = new OpenTarget(OpenTargetKind.File, file);
            var url = new OpenTarget(OpenTargetKind.Url, "https://example.com/a?b=1");

            Check(await targets.DetectAsync(url.Value, default) == url, "网址保留查询参数");
            Check(await targets.DetectAsync("www.example.com", default) == new OpenTarget(OpenTargetKind.Url, "https://www.example.com/"), "补全 https");
            foreach (string input in new[] { "example.com", "报告.docx", "https://exa mple.com", url.Value + "\n" + url.Value,
                         url.Value + "\r\n", Path.Combine(folder, "不存在"), @"a\b" })
                Check(await targets.DetectAsync(input, default) == null, "忽略无效输入 " + input);
            foreach (string input in new[] { folder, $"  \"{folder}\"  ", $"'{folder}'", $"<{folder}>", folder.Replace('\\', '/'),
                         Path.Combine("%TEMP%", Path.GetRelativePath(Environment.ExpandEnvironmentVariables("%TEMP%"), folder)), new Uri(folder).AbsoluteUri })
                Check(await targets.DetectAsync(input, default) == folderTarget, "识别文件夹 " + input);
            Check(await targets.DetectAsync(file, default) == fileTarget, "识别文件");
            Check(await targets.DetectAsync(new Uri(file).AbsoluteUri, default) == fileTarget, "解码文件 URI");

            var clip = new ClipEntry("141", ClipKind.Files, null, [folder], null, null, null, "", null, default, false);
            Check(await targets.DetectAsync(clip, default) == folderTarget, "单个文件夹条目");
            Check(await targets.DetectAsync(clip with { Files = [file, file] }, default) == null, "多个文件");
            Check(await targets.DetectAsync(clip with { Files = [url.Value] }, default) == null, "文件条目只认路径");
            Check(await targets.DetectAsync(clip with { Kind = ClipKind.Image, Text = folder }, default) == null, "图片说明不参与识别");
            foreach (var kind in new[] { ClipKind.Text, ClipKind.Code, ClipKind.Link, ClipKind.Color })
                Check(await targets.DetectAsync(clip with { Kind = kind, Text = url.Value }, default) == url, "文字条目 " + kind);

            foreach (var target in new[] { url, folderTarget, fileTarget }) await targets.OpenAsync(target, false, default);
            await targets.OpenAsync(fileTarget, true, default);
            Check(executor.Calls.SequenceEqual(["open:" + url.Value + ":False", "open:" + folder + ":False",
                "open:" + file + ":False", "reveal:" + file]), "打开和选中逐次传给假执行器");

            const string unc = @"\\server\share\fixture";
            targets.Probe = path => path == unc ? OpenTargetKind.Folder : null;
            var direct = new DirectSource(targets);
            var context = new SourceContext("", LauncherSnapshot.Empty, Defaults().Launcher, new Dictionary<string, AppUse>());
            foreach (string input in new[] { $"\"{unc}\"", unc })
                Check((await direct.SearchAsync(context with { Query = input }, default)).Single().Id == "direct:path:" + unc, "启动器识别 UNC");

            using var release = new ManualResetEventSlim();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int probes = 0;
            targets.Probe = _ =>
            {
                Interlocked.Increment(ref probes);
                try { release.Wait(); return OpenTargetKind.Folder; }
                finally { finished.TrySetResult(); }
            };
            var timings = new List<long>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var watch = Stopwatch.StartNew();
                    Check(await targets.DetectAsync(folder, default) == null, "探测超时");
                    timings.Add(watch.ElapsedMilliseconds);
                }
                Check(timings.All(ms => ms is >= 250 and < 700) && probes == 1, "超时后复用同一路径的探测");
            }
            finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            Console.WriteLine($"PASS 141 assertions={checks - before} timeoutMs={string.Join(',', timings)} probes={probes} realExecutions=0");
        }
        finally { directory.Delete(true); }
    }
}
