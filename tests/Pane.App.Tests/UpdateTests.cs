using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using Pane.Contracts;
using Pane.Services.Updates;
using Pane.Services.Setup;

internal static class UpdateTests
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("023: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-update-unit-" + Guid.NewGuid().ToString("N"));
        await using var server = new UpdateServer();
        bool auto = false;
        UpdateService Service(string suffix = "main", Action<string>? install = null) => new("0.1.99", Path.Combine(root, suffix), () => auto, install ?? (_ => { }), server.Url);
        try
        {
            using var service = Service();
            int changes = 0; service.Changed += (_, _) => changes++;
            await service.CheckAsync(default);
            Check(service.State == UpdateState.Available && service.Latest?.Version == "0.1.100", "99 < 100，新版可下载");
            Check(service.Latest?.Notes == "标题\n更新内容" && service.LastChecked != null, "说明转纯文本，检查时间");
            Check(server.UserAgent && !server.Credentials, "User-Agent，无凭据");
            await service.DownloadAsync(default);
            Check(service.State == UpdateState.Ready && service.DownloadProgress == 1 && changes > 3, "下载核对及状态事件");
            int downloads = server.Downloads;
            await service.CheckAsync(default);
            Check(service.State == UpdateState.Ready && server.Downloads == downloads, "已下载的版本直接 Ready");
            using (var restart = Service()) { await restart.CheckAsync(default); Check(restart.State == UpdateState.Ready && server.Downloads == downloads, "服务重建后复用并核对缓存"); }
            server.Version = "0.1.99"; await service.CheckAsync(default); Check(service.State == UpdateState.UpToDate && service.Latest == null, "已是最新");
            server.Version = "0.1.100"; server.Draft = true; await service.CheckAsync(default); Check(service.State == UpdateState.UpToDate, "忽略草稿");
            server.Draft = false; server.Preview = true; await service.CheckAsync(default); Check(service.State == UpdateState.UpToDate, "忽略预发布"); server.Preview = false;
            foreach (int status in new[] { 403, 429 })
            {
                using var limited = Service("limit" + status); server.Status = status;
                await limited.CheckAsync(default); int requests = server.Requests;
                await limited.CheckAsync(default); Check(limited.State == UpdateState.Failed && server.Requests == requests, status + " 限流不重试");
            }
            server.Status = 200;
            using (var bad = Service("bad"))
            {
                server.BadHash = true; await bad.CheckAsync(default); await bad.DownloadAsync(default);
                Check(bad.State == UpdateState.Failed && !Directory.EnumerateFiles(Path.Combine(root, "bad", "updates")).Any(), "核对不符删除临时包");
                server.BadHash = false; await bad.DownloadAsync(default); Check(bad.State == UpdateState.Ready, "失败后可重试");
            }
            using (var offline = Service("offline"))
            {
                server.Disconnect = true; await offline.CheckAsync(default); Check(offline.State == UpdateState.Failed, "断网失败");
                server.Disconnect = false; await offline.CheckAsync(default); server.BreakDownload = true; await offline.DownloadAsync(default);
                Check(offline.State == UpdateState.Failed, "下载中断失败"); server.BreakDownload = false;
                await offline.DownloadAsync(default); Check(offline.State == UpdateState.Ready, "断网恢复下载");
            }
            using (var cancelled = Service("cancel"))
            {
                await cancelled.CheckAsync(default); server.Slow = true;
                using var token = new CancellationTokenSource(100); await cancelled.DownloadAsync(token.Token);
                Check(cancelled.State == UpdateState.Available && !Directory.EnumerateFiles(Path.Combine(root, "cancel", "updates")).Any(), "下载取消清理"); server.Slow = false;
            }
            auto = true;
            using (var automatic = Service("auto")) { await automatic.CheckAsync(default); Check(automatic.State == UpdateState.Ready, "自动设置打开时连续下载"); }
            auto = false;
            string? launched = null;
            using (var installer = Service("install", path => launched = path))
            {
                installer.InstallAndRestart(); Check(launched == null, "非 Ready 不启动");
                await installer.CheckAsync(default); await installer.DownloadAsync(default); installer.InstallAndRestart();
                Check(launched != null && File.Exists(launched), "Ready 启动核对过的包");
                server.Version = "0.1.101"; await installer.CheckAsync(default); await installer.DownloadAsync(default);
                Check(Directory.GetFiles(Path.Combine(root, "install", "updates"), "*.exe").Length == 1, "只保留最新安装包");
            }
            Check(UpdateService.PlainNotes(new string('a', 2100)).Length == 2000, "说明长度限制");
            // 预热共享 Timer 队列后测服务本身，不把运行时首次启动的共享线程算成服务常驻线程。
            using (var warm = Service("warm")) { }
            GC.Collect(); GC.WaitForPendingFinalizers(); long before = GC.GetTotalMemory(true);
            int threads = Process.GetCurrentProcess().Threads.Count;
            using (var idle = Service("idle"))
            {
                long delta = GC.GetTotalMemory(true) - before;
                int after = Process.GetCurrentProcess().Threads.Count;
                Check(delta < 1024 * 1024 && after <= threads, "空闲服务小于 1 MiB，无专用线程");
                Console.WriteLine($"023 idle: managedDelta={delta} bytes; threads={threads}->{after}; timer=System.Threading.Timer; due=60s; period=24h");
            }
        }
        finally { SetupFiles.Delete(root); }
        Console.WriteLine($"PASS update assertions={count}; server=127.0.0.1; externalRequests=0");
        return count;
    }
}

internal sealed class UpdateServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    readonly List<Task> clients = [];
    public Uri Url { get; }
    public byte[] Payload = new byte[512 * 1024];
    public string Version = "0.1.100";
    public bool Draft, Preview, BadHash, Disconnect, BreakDownload, Slow, UserAgent, Credentials;
    public int Status = 200, Requests, Downloads;
    public UpdateServer() { listener.Start(); Url = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/latest"); loop = Accept(); }
    async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(Serve(client)); } }
        catch (OperationCanceledException) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            string first = await reader.ReadLineAsync(stop.Token) ?? "";
            string? line; while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
            { if (line.StartsWith("User-Agent: Pane/")) UserAgent = true; if (line.StartsWith("Authorization:") || line.StartsWith("Cookie:")) Credentials = true; }
            Interlocked.Increment(ref Requests);
            if (Disconnect) return;
            string name = $"Pane-Setup-{Version}.exe", host = Url.GetLeftPart(UriPartial.Authority);
            byte[] body;
            bool package = first.Contains("/" + name + " ");
            if (package) { Interlocked.Increment(ref Downloads); body = Payload; }
            else if (first.Contains("/SHA256SUMS.txt ")) body = Encoding.UTF8.GetBytes((BadHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Payload))) + "  " + name + "\n");
            else body = Encoding.UTF8.GetBytes($$"""
                {"tag_name":"v{{Version}}","draft":{{Draft.ToString().ToLowerInvariant()}},"prerelease":{{Preview.ToString().ToLowerInvariant()}},"body":"# 标题\n**更新内容**","published_at":"2026-10-30T00:00:00Z","html_url":"{{host}}/release","assets":[{"name":"{{name}}","size":{{Payload.Length}},"browser_download_url":"{{host}}/{{name}}"},{"name":"SHA256SUMS.txt","size":{{Encoding.UTF8.GetByteCount(Convert.ToHexString(SHA256.HashData(Payload)) + "  " + name + "\n")}},"browser_download_url":"{{host}}/SHA256SUMS.txt"}]}
                """);
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\nRetry-After: 3600\r\n\r\n");
            await stream.WriteAsync(headers, stop.Token);
            if (package && BreakDownload) { await stream.WriteAsync(body.AsMemory(0, 10), stop.Token); return; }
            if (package && Slow) { await stream.WriteAsync(body.AsMemory(0, 10), stop.Token); await Task.Delay(500, stop.Token); await stream.WriteAsync(body.AsMemory(10), stop.Token); }
            else await stream.WriteAsync(body, stop.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(clients); stop.Dispose(); }
}