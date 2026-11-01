using System.Collections.Concurrent;
using System.Diagnostics;
using Pane.Services.Icons;

static partial class Program
{
    sealed class FileIconExtractor : IFileIconExtractor
    {
        public readonly ConcurrentDictionary<string, int> Calls = new(StringComparer.OrdinalIgnoreCase);
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public bool Block;
        public bool Sta = true;
        public bool Extract(string path, string output)
        {
            Sta &= Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
            Calls.AddOrUpdate(path, 1, (_, count) => count + 1);
            Entered.Set();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            if (path.EndsWith(".fail", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.EndsWith(".throw", StringComparison.OrdinalIgnoreCase)) throw new IOException("测试失败");
            File.WriteAllBytes(output, [1]);
            return true;
        }
    }

    static async Task<string> WaitIcon(FileIcons service, string path, bool folder = false)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (service.TryGet(path, folder) is string result) return result;
            await Task.Delay(5);
        }
        throw new TimeoutException("图标未就绪：" + Path.GetFileName(path));
    }

    static async Task FileIconTests()
    {
        int start = checks;
        string root = Path.Combine(AppContext.BaseDirectory, "results", "file-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Fixture(string name) => Path.Combine(root, name);
        var fake = new FileIconExtractor { Block = true };
        using (var service = new FileIcons(Path.Combine(root, "fake"), fake, 2))
        {
            var events = new ConcurrentDictionary<string, byte>();
            service.Ready += (_, path) => events.TryAdd(path, 0);
            var first = Stopwatch.StartNew();
            Check(service.TryGet(Fixture("first.txt"), false) == null, "首次立即返回空");
            first.Stop();
            Check(fake.Entered.Wait(TimeSpan.FromSeconds(5)), "后台开始取图");
            var times = new List<double>();
            for (int i = 0; i < 10000; i++)
            {
                var watch = Stopwatch.StartNew();
                service.TryGet(Fixture("row" + i % 100 + ".TXT"), false);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            Parallel.For(0, 100, i => service.TryGet(Fixture("row" + i + ".TXT"), false));
            times.Sort();
            Check(times[^1] < 16 && first.Elapsed.TotalMilliseconds < 100, "阻塞提取时调用不等待 Shell");
            Check(fake.Calls.Values.Sum() == 1, "并发同扩展名仅提取一次");
            Console.WriteLine($"TryGet pending samples=10000 firstMs={first.Elapsed.TotalMilliseconds:F4} p50Ms={times[5000]:F4} p99Ms={times[9900]:F4} maxMs={times[^1]:F4}");
            fake.Release.Set();
            string text = await WaitIcon(service, Fixture("first.txt"));
            var deadline = Stopwatch.StartNew();
            while (events.Count < 101 && deadline.ElapsedMilliseconds < 5000) await Task.Delay(5);
            Check(events.Count == 101, "共享图标就绪通知所有请求路径");
            Check(await WaitIcon(service, Fixture("other.txt")) == text, "同扩展名共享缓存");
            times.Clear();
            for (int i = 0; i < 10000; i++)
            {
                var watch = Stopwatch.StartNew();service.TryGet(Fixture("row" + i % 100 + ".txt"), false);times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Check(times[^1] < 16, "缓存命中小于一帧");
            Console.WriteLine($"TryGet cached samples=10000 p50Ms={times[5000]:F4} p99Ms={times[9900]:F4} maxMs={times[^1]:F4}");

            foreach (string extension in new[] { "fail", "throw" })
            {
                service.TryGet(Fixture("bad." + extension), false);
                await WaitIcon(service, Fixture("barrier." + extension + "ok"));
                for (int i = 0; i < 100; i++) service.TryGet(Fixture("bad." + extension), false);
                await WaitIcon(service, Fixture("after." + extension + "ok"));
                Check(fake.Calls[Fixture("bad." + extension)] == 1, "失败不重试 " + extension);
                Check(!events.ContainsKey(Fixture("bad." + extension)), "失败不发 Ready " + extension);
            }
            string a = await WaitIcon(service, Fixture("a.exe"));
            string b = await WaitIcon(service, Fixture("b.exe"));
            Check(a != b, "exe 按路径取图");
            service.TryGet(Fixture("a.exe"), false);
            string c = await WaitIcon(service, Fixture("c.exe"));
            Check(File.Exists(a) && !File.Exists(b) && File.Exists(c), "淘汰最近最少使用路径");
            Check(Directory.GetFiles(Path.Combine(root, "fake", "file-icons"), "p-*.png").Length == 2, "路径磁盘缓存上限");
            Check(File.Exists(text) && service.TryGet(Fixture("first.txt"), false) == text, "扩展名缓存不清理");
            foreach (string extension in new[] { "lnk", "ico", "url", "appref-ms" })
                Check(await WaitIcon(service, Fixture("a." + extension)) != await WaitIcon(service, Fixture("b." + extension)), extension + " 按路径缓存");
            Check(await WaitIcon(service, Fixture("folder-a"), true) != await WaitIcon(service, Fixture("folder-b"), true), "文件夹逐路径缓存");
            Check(fake.Sta, "所有提取在 STA 线程");
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.TryGet(Fixture("after.txt"), false) == null, "释放后不再调度");
        }
        var reload = new FileIconExtractor();
        using (var service = new FileIcons(Path.Combine(root, "fake"), reload, 1))
        {
            await WaitIcon(service, Fixture("first.txt"));
            Check(reload.Calls.Count == 0, "重启复用磁盘缓存");
            Check(Directory.GetFiles(Path.Combine(root, "fake", "file-icons"), "p-*.png").Length == 1, "启动时清理历史超限缓存");
            service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var service = new FileIcons(Path.Combine(root, "native")))
        {
            foreach (string extension in new[] { "txt", "pdf", "png", "zip", "docx" })
            {
                string path = Fixture("native." + extension);File.WriteAllText(path, "Pane icon fixture");
                string png = await WaitIcon(service, path);
                byte[] bytes = File.ReadAllBytes(png);
                Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), extension + " PNG 签名");
                Check(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) == 64 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) == 64, extension + " 64px");
                var image = IconRenderer.Load(png);
                Check(!image.Empty && Enumerable.Range(0, image.Pixels.Length / 4).Any(i => image.Pixels[i * 4 + 3] == 0), extension + " 可解码且透明底");
                Console.WriteLine($"native extension=.{extension} size=64x64 bytes={bytes.Length} transparent=true");
            }
            string folder = Fixture("native-folder");Directory.CreateDirectory(folder);
            Check(File.Exists(await WaitIcon(service, folder, true)), "真实文件夹生成 PNG");
            service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(new EmptyFileIcons().TryGet("demo.txt", false) == null, "演示实现返回空");
        Console.WriteLine($"PASS file-icons assertions={checks - start}");
    }
}
