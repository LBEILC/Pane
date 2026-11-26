using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Pane.Contracts;
using Pane.Services.Updates;

internal static class ReleaseVerification
{
    static async Task VerifyPortable(string root, string archive, CancellationToken token)
    {
        string extracted = Path.Combine(root, "extracted"), app = Path.Combine(extracted, "Pane");
        ZipFile.ExtractToDirectory(archive, extracted);
        if (!Directory.GetFileSystemEntries(extracted).SequenceEqual([app]) || !File.Exists(Path.Combine(app, "portable"))
            || Directory.Exists(Path.Combine(app, "data"))) throw new InvalidDataException("便携包结构无效");
        var start = new ProcessStartInfo(Path.Combine(app, "Pane.App.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = app };
        start.ArgumentList.Add("--portable-probe");
        start.Environment.Remove("PANE_DATA_DIR");
        start.Environment["PANE_INSTANCE"] = Guid.NewGuid().ToString("N");
        string roaming = Path.Combine(root, "Roaming"), local = Path.Combine(root, "Local");
        Directory.CreateDirectory(roaming); Directory.CreateDirectory(local);
        start.Environment["APPDATA"] = roaming;
        start.Environment["LOCALAPPDATA"] = local;
        var before = Directory.GetFiles(app, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var child = Process.Start(start) ?? throw new IOException("便携版启动失败");
        try { await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token); }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
        if (child.ExitCode != 0 || !File.Exists(Path.Combine(app, "data", "settings.json"))
            || Directory.GetFiles(app, "*", SearchOption.AllDirectories).Except(before).Any(path =>
                !path.StartsWith(Path.Combine(app,"data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || Directory.Exists(Path.Combine(roaming,"Pane")) || Directory.Exists(Path.Combine(local,"Pane")))
            throw new InvalidDataException("便携版启动或数据隔离失败：" + child.ExitCode);
        Console.WriteLine("PASS portable Pane/data/settings.json; Roaming/Pane=absent; Local/Pane=absent; exit=0");
    }

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
                if (File.Exists(Path.Combine(artifacts, "SHA256SUMS.txt")))
                {
                    var names = File.ReadLines(Path.Combine(artifacts, "SHA256SUMS.txt"))
                        .Select(line => line.Split("  ")[1]).Append("SHA256SUMS.txt").ToArray();
                    var fixtureAssets = new System.Text.Json.Nodes.JsonArray();
                    foreach (string file in names)
                    {
                        if (file != Path.GetFileName(file)) throw new InvalidDataException();
                        fixtureAssets.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = file,
                            ["size"] = new FileInfo(Path.Combine(artifacts, file)).Length,
                            ["browser_download_url"] = new Uri(server.Url, file).AbsoluteUri });
                    }
                    var fixtureRelease = new System.Text.Json.Nodes.JsonObject { ["tag_name"] = "v" + version,
                        ["draft"] = false, ["prerelease"] = false, ["body"] = "", ["published_at"] = "2026-11-01T00:00:00Z",
                        ["html_url"] = server.Url.AbsoluteUri, ["assets"] = fixtureAssets };
                    server.Respond = async path => path == "/latest" ? new(200, System.Text.Encoding.UTF8.GetBytes(fixtureRelease.ToJsonString()))
                        : names.Contains(path.TrimStart('/')) ? new(200, await File.ReadAllBytesAsync(Path.Combine(artifacts, path[1..]))) : new(404, []);
                }
                else
                {
                    artifacts = Path.Combine(root, "expected");
                    Directory.CreateDirectory(artifacts);
                    await File.WriteAllBytesAsync(Path.Combine(artifacts, name), server.Payload);
                    await File.WriteAllTextAsync(Path.Combine(artifacts, "SHA256SUMS.txt"), Convert.ToHexString(SHA256.HashData(server.Payload)) + "  " + name + "\n");
                }
            }
            Uri source = server?.Url ?? UpdateService.ProductionSource;
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false });
            http.Timeout = TimeSpan.FromMinutes(15);
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
            var expectedNames = File.ReadLines(Path.Combine(artifacts, "SHA256SUMS.txt"))
                .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[1].TrimStart('*')).Append("SHA256SUMS.txt").ToArray();
            Require(expectedNames.Contains(name) && assets.Length == expectedNames.Length, "附件列表与本地校验表不符");
            foreach (string file in expectedNames)
            {
                Require(file == Path.GetFileName(file), "附件名称无效");
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
            string portable = $"Pane-Portable-{version}.zip";
            if (!fake || server?.Respond != null)
            {
                Require(expectedNames.Contains(portable), "缺少便携包");
                string archive = Path.Combine(root, portable);
                string checksum = File.ReadLines(Path.Combine(root, "SHA256SUMS.txt")).Single(line => line.EndsWith("  " + portable, StringComparison.Ordinal)).Split(' ')[0];
                await using (var stream = File.OpenRead(archive))
                    Require(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(checksum, StringComparison.OrdinalIgnoreCase), "便携包校验和不符");
                await VerifyPortable(root, archive, token);
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
            if (server != null) Require(!server.Credentials && server.Paths.Count(path => path == "/" + name) == 2, "假服务请求记录不符");
            Console.WriteLine($"PASS release={version}; state=Ready; install=0; hooks=0; credentials=0; fake={fake}; requests={server?.Requests}; downloads={server?.Paths.Count(path => path == "/" + name)}");
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
