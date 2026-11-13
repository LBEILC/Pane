using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Ai;
using Pane.Services.Setup;
using Pane.Services.Updates;

internal static class MirrorUpdateTests
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("097: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "Pane-mirror-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string cdk = "fixture+&=中文-cdk-097";
        await using var server = new UpdateServer();
        string host = server.Url.GetLeftPart(UriPartial.Authority);
        var mirror = new Uri(host + "/mirror");
        bool auto = false, offer = false, badHash = false;
        int code = 0, mirrorRequests = 0, mirrorDownloads = 0, failures = 0;
        string version = "v0.1.100";
        long expires = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds();
        UpdateSource source = UpdateSource.MirrorChyan;
        string? raw = null; int delay = 0;
        Action<JsonObject>? changeData = null;
        string? cdnJson = null;
        TaskCompletionSource? downloadStarted = null, finishDownload = null;
        Dictionary<string,string> Query(string path) => path.Split('?', 2).ElementAtOrDefault(1)?.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.ElementAtOrDefault(1) ?? "")) ?? [];
        UpdateServer.Reply Json(JsonObject obj, int status = 200) => new(status, Encoding.UTF8.GetBytes(obj.ToJsonString()), "application/json");
        server.Respond = async path =>
        {
            if (path.StartsWith("/mirror?"))
            {
                Interlocked.Increment(ref mirrorRequests);
                if (delay > 0) await Task.Delay(delay);
                if (raw != null) return new(200, Encoding.UTF8.GetBytes(raw), "text/plain");
                var query = Query(path);
                if (code != 0 && (query.ContainsKey("cdk") || code < 0 || code >= 8000)) return Json(new() { ["code"] = code }, 403);
                var data = new JsonObject { ["version_name"] = version, ["release_note"] = "## 修复\n- 更新测试", ["cdk_expired_time"] = expires };
                if (offer && query.ContainsKey("cdk"))
                {
                    data["url"] = host + "/cdn/" + mirrorRequests;
                    data["sha256"] = badHash ? new string('1', 64) : Convert.ToHexString(SHA256.HashData(server.Payload));
                    data["filesize"] = server.Payload.Length;
                }
                changeData?.Invoke(data);
                return Json(new() { ["code"] = 0, ["data"] = data });
            }
            if (path.StartsWith("/cdn/"))
            {
                Interlocked.Increment(ref mirrorDownloads);
                if (downloadStarted != null) { downloadStarted.TrySetResult(); await finishDownload!.Task; }
                if (cdnJson != null) { string body = cdnJson; cdnJson = null; return new(200, Encoding.UTF8.GetBytes(body)); }
                if (failures-- > 0) return new(403, []);
                return new(200, server.Payload);
            }
            return null;
        };
        UpdateService Service(string suffix, string current = "0.1.99", Uri? endpoint = null) => new(current, Path.Combine(root, suffix), () => auto, _ => { }, server.Url, () => source, endpoint ?? mirror);
        try
        {
            using (var service = Service("anonymous"))
            {
                await service.CheckAsync(default);
                var query = Query(server.Paths.Last());
                Check(query.Count == 4 && query["os"] == "win" && query["arch"] == "x64" && query["channel"] == "stable" && query["user_agent"] == "Pane", "匿名请求参数准确");
                Check(service.CheckedFrom == UpdateOrigin.MirrorChyan && service.DownloadFrom == UpdateOrigin.GitHub && service.CdkStatus == MirrorCdkStatus.None, "匿名来源");
                Check(service.Latest is { PublishedAt: null, SizeBytes: 0, Details: not null } && service.Latest.Notes.Contains("修复"), "说明与未知日期大小");
                await service.DownloadAsync(default);
                Check(service.State == UpdateState.Ready && server.Paths.Any(p => p == "/tags/v0.1.100") && server.Paths.Any(p => p == "/SHA256SUMS.txt"), "按标签 GitHub 下载并核对");
            }
            offer = true;
            using (var service = Service("valid"))
            {
                int requests = server.Requests, changes = 0;
                service.Changed += (_, _) => changes++;
                service.SaveMirrorCdk("  " + cdk + "  ");
                Check(service.CdkStatus == MirrorCdkStatus.Unknown && changes == 1 && server.Requests == requests, "保存修剪且不联网");
                string path = Path.Combine(root, "valid", UpdateService.MirrorSecretFile);
                byte[] encrypted = File.ReadAllBytes(path);
                service.SaveMirrorCdk(cdk);
                Check(changes == 1 && File.ReadAllBytes(path).SequenceEqual(encrypted), "相同 CDK 不重写");
                Check(!Encoding.UTF8.GetString(encrypted).Contains(cdk) && new SecretStore(Path.Combine(root, "valid")).Read(UpdateService.MirrorSecretFile)!.Contains("cdk"), "DPAPI 可解密且密文不含 CDK");
                await service.CheckAsync(default);
                Check(Query(server.Paths.Last())["cdk"] == cdk && !Query(server.Paths.Last()).ContainsKey("current_version"), "特殊字符正确编码");
                Check(service.CdkStatus == MirrorCdkStatus.Valid && service.CdkExpires == DateTimeOffset.FromUnixTimeSeconds(expires), "有效 CDK 到期日");
                await service.DownloadAsync(default);
                Check(service.State == UpdateState.Ready && service.DownloadFrom == UpdateOrigin.MirrorChyan && mirrorDownloads == 1, "Mirror 下载哈希校验");
                string record = Path.Combine(root, "valid", "updates", "ready.json");
                Check(!File.ReadAllText(record).Contains("/cdn/") && !File.ReadAllText(record).Contains(cdk), "缓存不存秘密地址");
                using var restored = Service("valid");
                Check(restored.State == UpdateState.Ready && restored.DownloadFrom == UpdateOrigin.MirrorChyan && restored.CdkStatus == MirrorCdkStatus.Valid, "重启恢复来源及 CDK");
                int downloads = mirrorDownloads;
                offer = false; await restored.CheckAsync(default);
                Check(restored.State == UpdateState.Ready && mirrorDownloads == downloads && restored.DownloadFrom == UpdateOrigin.MirrorChyan, "跨源复用已验证同版本");
                var old = JsonNode.Parse(File.ReadAllText(record))!.AsObject(); old.Remove("origin"); old["release"]!["publishedAt"] = "2026-10-30T00:00:00Z";
                File.WriteAllText(record, old.ToJsonString());
                using var legacy = Service("valid");
                Check(legacy.State == UpdateState.Ready && legacy.DownloadFrom == UpdateOrigin.GitHub && legacy.Latest!.PublishedAt != null, "旧缓存兼容");
                service.SaveMirrorCdk("");
                Check(!File.Exists(path) && service.CdkStatus == MirrorCdkStatus.None && service.CdkExpires == null, "清空删除文件");
            }
            offer = true; auto = true;
            foreach (string current in new[] { "0.1.100", "0.1.101" })
            {
                using var service = Service("version-" + current, current); service.SaveMirrorCdk(cdk);
                int downloads = mirrorDownloads; await service.CheckAsync(default);
                Check(service.State == UpdateState.UpToDate && service.Latest == null && mirrorDownloads == downloads, "当前或更高版本不下载");
            }
            foreach (var pair in new[] { (7001, MirrorCdkStatus.Expired), (7002, MirrorCdkStatus.Invalid), (7003, MirrorCdkStatus.QuotaExhausted), (7004, MirrorCdkStatus.Mismatched), (7005, MirrorCdkStatus.Blocked) })
            {
                code = pair.Item1;
                using var service = Service("code-" + code); service.SaveMirrorCdk(cdk);
                int before = mirrorRequests; await service.CheckAsync(default);
                var paths = server.Paths.Where(p => p.StartsWith("/mirror?")).TakeLast(2).ToArray();
                Check(mirrorRequests == before + 2 && Query(paths[0]).ContainsKey("cdk") && !Query(paths[1]).ContainsKey("cdk"), "错误后匿名重试 " + code);
                Check(service.CdkStatus == pair.Item2 && service.CdkExpires == null && service.CheckedFrom == UpdateOrigin.MirrorChyan && service.DownloadFrom == UpdateOrigin.GitHub, "错误码状态 " + code);
                Check(service.State == (code is 7001 or 7003 ? UpdateState.Ready : UpdateState.Available), "自动下载规则 " + code);
                if (code == 7003)
                {
                    before = mirrorRequests; await service.CheckAsync(default);
                    Check(mirrorRequests == before + 1 && !Query(server.Paths.Last(p => p.StartsWith("/mirror?"))).ContainsKey("cdk") && service.CdkStatus == MirrorCdkStatus.QuotaExhausted, "当天不再带 CDK");
                    service.SaveMirrorCdk(cdk + "changed"); await service.CheckAsync(default);
                    Check(mirrorRequests == before + 3, "换 CDK 恢复请求");
                }
                if (code is 7002 or 7004 or 7005) { await service.DownloadAsync(default); Check(service.State == UpdateState.Ready, "手动仍可下载 " + code); }
            }
            auto = false; code = 0;
            foreach (string failure in new[] { "8001", "negative", "unknown", "non-json", "empty", "timeout", "connect" })
            {
                code = failure == "8001" ? 8001 : failure == "negative" ? -1 : failure == "unknown" ? 8999 : 0;
                raw = failure == "non-json" ? "not json" : failure == "empty" ? "" : null;
                delay = failure == "timeout" ? 200 : 0;
                // 端口 0 不会连接到任何外部服务。
                using var service = Service("fallback-" + failure, endpoint: failure == "connect" ? new Uri("http://127.0.0.1:0/mirror") : null);
                service.MirrorTimeout = TimeSpan.FromMilliseconds(50); service.SaveMirrorCdk(cdk);
                await service.CheckAsync(default);
                Check(service.CheckedFrom == UpdateOrigin.GitHub && service.State == UpdateState.Available && service.CdkStatus == MirrorCdkStatus.Unknown, "回退 " + failure);
            }
            code = 0; raw = null; delay = 0;
            using (var bad = Service("bad"))
            {
                badHash = true; bad.SaveMirrorCdk(cdk); await bad.CheckAsync(default); await bad.DownloadAsync(default);
                Check(bad.State == UpdateState.Failed && bad.FailedStep == UpdateStep.Download && !Directory.EnumerateFiles(Path.Combine(root, "bad", "updates")).Any(), "篡改包清理");
                badHash = false;
            }
            foreach (int failuresToSet in new[] { 1, 2 })
            {
                using var service = Service("expired-" + failuresToSet); service.SaveMirrorCdk(cdk); await service.CheckAsync(default);
                failures = failuresToSet; int requests = mirrorRequests, downloads = mirrorDownloads;
                await service.DownloadAsync(default);
                Check(mirrorRequests == requests + 1 && mirrorDownloads == downloads + 2, "过期只重试一次");
                Check(service.State == (failuresToSet == 1 ? UpdateState.Ready : UpdateState.Failed), "重试结果");
                if (failuresToSet == 2) Check(service.Error == Strings.Update_MirrorDownloadFailed, "专用错误文案");
            }
            failures = 0;
            using (var service = Service("json-expired"))
            {
                service.SaveMirrorCdk(cdk); await service.CheckAsync(default); cdnJson = "{\"code\":7001}";
                int downloads = mirrorDownloads; await service.DownloadAsync(default);
                Check(service.State == UpdateState.Ready && mirrorDownloads == downloads + 2, "无 Content-Type 的 JSON 错误体重取一次");
            }
            using (var service = Service("refresh-cdk-error"))
            {
                service.SaveMirrorCdk(cdk); await service.CheckAsync(default); code = 7001; failures = 1;
                await service.DownloadAsync(default);
                Check(service.State == UpdateState.Failed && service.CdkStatus == MirrorCdkStatus.Expired && service.CdkExpires == null && service.Error == Strings.Update_MirrorDownloadFailed, "重取时 CDK 错误更新状态");
                code = 0; failures = 0;
            }
            foreach (string missing in new[] { "sha256", "filesize", "url" })
            {
                changeData = data => data[missing] = null;
                using var service = Service("missing-" + missing); service.SaveMirrorCdk(cdk); await service.CheckAsync(default);
                Check(service.CheckedFrom == UpdateOrigin.MirrorChyan && service.DownloadFrom == UpdateOrigin.GitHub && service.CdkStatus == MirrorCdkStatus.Valid, "缺少下载元数据退回 GitHub " + missing);
            }
            changeData = null;
            using (var service = Service("download-queue"))
            {
                service.SaveMirrorCdk(cdk); await service.CheckAsync(default);
                downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); finishDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var download = service.DownloadAsync(default); await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                int requests = mirrorRequests; service.SaveMirrorCdk(cdk + "queued"); var check = service.CheckAsync(default);
                var lastCheck = service.CheckAsync(default);
                Check(!check.IsCompleted && mirrorRequests == requests && service.State == UpdateState.Downloading, "下载中检查排队不打断");
                finishDownload.SetResult(); await Task.WhenAll(download, check, lastCheck);
                Check(service.State == UpdateState.Ready && mirrorRequests == requests + 1 && Query(server.Paths.Last())["cdk"] == cdk + "queued", "下载结束只检查一次新 CDK 并复用缓存");
                downloadStarted = finishDownload = null;
            }
            using (var service = Service("concurrent"))
            {
                delay = 200; var first = service.CheckAsync(default);
                while (!server.Paths.Last().StartsWith("/mirror?")) await Task.Delay(5);
                await Task.Delay(20); service.SaveMirrorCdk(cdk); var second = service.CheckAsync(default);
                await Task.WhenAll(first, second); delay = 0;
                Check(Query(server.Paths.Last())["cdk"] == cdk && service.CdkStatus == MirrorCdkStatus.Valid && service.DownloadFrom == UpdateOrigin.MirrorChyan, "取消旧检查并带新 CDK");
            }
            source = UpdateSource.GitHub;
            using (var service = Service("github"))
            {
                service.SaveMirrorCdk(cdk); int requests = mirrorRequests;
                await service.CheckAsync(default);
                Check(mirrorRequests == requests && service.CheckedFrom == UpdateOrigin.GitHub && service.CdkStatus == MirrorCdkStatus.Unknown, "GitHub 源不碰 Mirror");
            }
            using (var service = Service("rate-limit"))
            {
                server.Status = 429; await service.CheckAsync(default); server.Status = 200;
                source = UpdateSource.MirrorChyan; await service.CheckAsync(default);
                Check(service.State == UpdateState.Available && service.CheckedFrom == UpdateOrigin.MirrorChyan, "GitHub 冷却不拦 Mirror 检查");
            }
            source = UpdateSource.MirrorChyan;
            string corrupt = Path.Combine(root, "corrupt"); Directory.CreateDirectory(corrupt); File.WriteAllText(Path.Combine(corrupt, UpdateService.MirrorSecretFile), "broken");
            using (var service = Service("corrupt")) Check(service.MirrorCdk == "" && service.CdkStatus == MirrorCdkStatus.None, "坏密文当未填写");
            var logs = Directory.GetFiles(root, "*.log", SearchOption.AllDirectories);
            Check(logs.Length > 0 && logs.All(p => !File.ReadAllText(p).Contains(cdk) && !File.ReadAllText(p).Contains(host)), "所有日志无 CDK 或完整地址");
            Check(server.UserAgent && !server.Credentials, "请求无凭据且有 Pane User-Agent");
        }
        finally { SetupFiles.Delete(root); }
        Console.WriteLine($"PASS mirror update assertions={count}; server=127.0.0.1; externalRequests=0");
        return count;
    }
}
