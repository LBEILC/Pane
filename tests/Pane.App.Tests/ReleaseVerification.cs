using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Updates;

internal static class ReleaseVerification
{
    public static async Task Run(string version, string artifacts, bool fake)
    {
        string root = Path.Combine(Path.GetTempPath(), "Pane-release-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        UpdateServer? server = null;
        try
        {
            string name = $"Pane-Setup-{version}.exe";
            if (fake)
            {
                server = new UpdateServer { Version = version };
                artifacts = Path.Combine(root, "expected");
                Directory.CreateDirectory(artifacts);
                await File.WriteAllBytesAsync(Path.Combine(artifacts, name), server.Payload);
                await File.WriteAllTextAsync(Path.Combine(artifacts, "SHA256SUMS.txt"), Convert.ToHexString(SHA256.HashData(server.Payload)) + "  " + name + "\n");
            }
            Uri source = server?.Url ?? UpdateService.ProductionSource;
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pane/" + version);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
            Uri Validate(Uri uri)
            {
                Require(uri.UserInfo.Length == 0 && (fake
                    ? uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == source.Port
                    : uri.Scheme == "https" && (uri.Host == "github.com" || uri.Host == "api.github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))), "附件地址不允许");
                return uri;
            }
            async Task<byte[]> Get(Uri uri)
            {
                for (int redirects = 0; redirects < 6; redirects++)
                {
                    using var response = await http.GetAsync(Validate(uri), token);
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    { uri = new Uri(uri, response.Headers.Location ?? throw new InvalidDataException("缺少重定向地址")); continue; }
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync(token);
                }
                throw new InvalidDataException("重定向过多");
            }
            using var json = JsonDocument.Parse(await Get(source));
            var release = json.RootElement;
            Require(release.GetProperty("tag_name").GetString() == "v" + version && !release.GetProperty("draft").GetBoolean() && !release.GetProperty("prerelease").GetBoolean(), "最新正式版本不符");
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            Require(assets.Length == 2, "应恰有两个附件");
            foreach (string file in new[] { name, "SHA256SUMS.txt" })
            {
                var asset = assets.Single(a => a.GetProperty("name").GetString() == file);
                string expected = Path.Combine(artifacts, file);
                long size = asset.GetProperty("size").GetInt64();
                Require(new FileInfo(expected).Length == size, "本地附件大小不符：" + file);
                byte[] bytes = await Get(new Uri(asset.GetProperty("browser_download_url").GetString()!));
                Require(bytes.LongLength == size, "下载大小不符：" + file);
                await using var local = File.OpenRead(expected);
                byte[] localHash = await SHA256.HashDataAsync(local, token);
                Require(SHA256.HashData(bytes).SequenceEqual(localHash), "本地与下载哈希不符：" + file);
                await File.WriteAllBytesAsync(Path.Combine(root, file), bytes, token);
                Console.WriteLine($"PASS asset={file}; bytes={size}; sha256={Convert.ToHexString(SHA256.HashData(bytes))}");
            }
            // 使用实际更新服务完成校验；旧版本仅用于触发更新，不接入用户数据或安装入口。
            using (var service = new UpdateService("0.0.0", root, () => false, _ => throw new InvalidOperationException("禁止安装"), server?.Url))
            {
                await service.CheckAsync(token);
                Require(service.State == UpdateState.Available && service.Latest?.Version == version, service.Error ?? "更新检查失败");
                await service.DownloadAsync(token);
                Require(service.State == UpdateState.Ready, service.Error ?? "未到 Ready");
                string downloaded = Path.Combine(root, "updates", name);
                byte[] serviceBytes = await File.ReadAllBytesAsync(downloaded, token);
                byte[] directBytes = await File.ReadAllBytesAsync(Path.Combine(root, name), token);
                Require(SHA256.HashData(serviceBytes).SequenceEqual(SHA256.HashData(directBytes)), "两次下载不一致");
            }
            if (server != null) Require(!server.Credentials && server.Downloads == 2, "假服务请求记录不符");
            Console.WriteLine($"PASS release={version}; state=Ready; install=0; hooks=0; credentials=0; fake={fake}; requests={server?.Requests}; downloads={server?.Downloads}");
        }
        finally
        {
            if (server != null) await server.DisposeAsync();
            // 只清理本次创建的随机临时目录。
            Directory.Delete(root, true);
            Console.WriteLine("cleanup=passed; server=stopped");
        }
    }
}
