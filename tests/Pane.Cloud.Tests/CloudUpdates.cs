using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Ai;
using Pane.Services.Cloud;
using Pane.Services.Setup;
using Pane.Services.Updates;
using Pane.Settings;

internal static partial class Program
{
    const string Full = "Pane-Setup-0.1.100.exe", Patch = "Pane-Patch-0.1.99-0.1.100.zip", Manifest = "Pane-Manifest-0.1.100.json";
    sealed class ReleaseHandler : HttpMessageHandler
    {
        internal JsonObject Profile = Me.DeepClone().AsObject();
        internal Dictionary<string, byte[]> Files = new() { [Full] = [1, 2, 3, 4] };
        internal DateTimeOffset Available = DateTimeOffset.UtcNow;
        internal string? Notes;
        internal bool Offline, Conflict;
        internal List<(string Name, long Offset)> Transfers = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Headers.Authorization?.Parameter == "test-session" && request.Headers.GetValues("X-Pane-Client").Single() == "pane/0.1.99", "账号会话与客户端版本");
            string path = request.RequestUri!.AbsolutePath;
            if (Offline) throw new HttpRequestException();
            if (path == "/api/v1/me") return Task.FromResult(Json(Profile));
            if (path == "/api/v1/releases/latest/files")
            {
                var files = new JsonArray();
                foreach (var (name, bytes) in Files)
                    files.Add((JsonNode)new JsonObject { ["name"] = name, ["size"] = bytes.Length, ["sha256"] = PayloadPatch.Hash(bytes) });
                var release = new JsonObject { ["version"] = "0.1.100", ["published_at"] = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                    ["available_at"] = Available.ToUnixTimeSeconds(), ["files"] = files };
                if (Notes != null) release["notes"] = Notes;
                return Task.FromResult(Json(release));
            }
            string key = Path.GetFileName(path);
            Check(path == "/api/v1/releases/0.1.100/files/" + key, "版本文件路径");
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            Transfers.Add((key, offset));
            if (Conflict) return Task.FromResult(Json(new JsonObject { ["error"] = "quota_insufficient" }, 409));
            var data = Files[key];
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(data[(int)offset..]) };
            if (offset != 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    static void AddPatch(ReleaseHandler handler, string baseline)
    {
        Directory.CreateDirectory(baseline);
        var files = new JsonArray();
        foreach (var name in new[] { "Pane.App.exe", "Pane.Maintenance.exe" })
        {
            byte[] bytes = [5, 6];
            File.WriteAllBytes(Path.Combine(baseline, name), bytes);
            files.Add((JsonNode)new JsonObject { ["path"] = name, ["size"] = bytes.Length, ["sha256"] = PayloadPatch.Hash(bytes) });
        }
        var manifest = new JsonObject { ["format"] = 1, ["product"] = "Pane", ["arch"] = "x64", ["files"] = files, ["version"] = "0.1.99" };
        var old = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        manifest["version"] = "0.1.100";
        var next = handler.Files[Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var meta = new JsonObject { ["format"] = 1, ["product"] = "Pane", ["arch"] = "x64", ["from"] = "0.1.99", ["to"] = "0.1.100",
            ["fromManifestSha256"] = PayloadPatch.Hash(old), ["toManifestSha256"] = PayloadPatch.Hash(next), ["ops"] = new JsonArray() };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (key, bytes) in new[] { ("base.json", old), ("patch.json", Encoding.UTF8.GetBytes(meta.ToJsonString())) })
            {
                using var entry = zip.CreateEntry(key).Open();
                entry.Write(bytes);
            }
        handler.Files[Patch] = buffer.ToArray();
    }

    static void CheckNotes(UpdateRelease? release, UpdateRelease expected, string scenario)
    {
        Check(release?.Notes == expected.Notes && release?.Details is {} details && expected.Details is {} reference &&
            details.Version == reference.Version && details.Date == reference.Date && details.Items.SequenceEqual(reference.Items), scenario);
    }

    static async Task CloudUpdates()
    {
        string root = Path.Combine(Path.GetTempPath(), "Pane-116-" + Guid.NewGuid().ToString("N"));
        await using var github = new UpdateServer { Body = "## 新增\n- 快速搜索\n## 改进\n- 下载提示\n## 修复\n- 设置保存" };
        try
        {
            using var reference = new UpdateService("0.1.99", Path.Combine(root, "github"), () => false, _ => {}, github.Url);
            await reference.CheckAsync(default);
            var expected = reference.Latest!;
            Check(expected.Details?.Items.Select(item => item.Kind).SequenceEqual(
                [ReleaseNoteKind.New, ReleaseNoteKind.Improved, ReleaseNoteKind.Fixed]) == true, "GitHub 正文三组");
            foreach (var scenario in new[] { "patch", "broken", "low_quota", "subscription_required", "signed_out", "network", "409", "range", "manual", "automatic", "mirror" })
            {
                string dir = Path.Combine(root, scenario), baseline = Path.Combine(dir, "installed");
                var handler = new ReleaseHandler();
                handler.Notes = scenario switch { "patch" => github.Body, "broken" => "", "409" => "## 修复\n- 云源条目", _ => null };
                handler.Profile.Remove("avatar");
                var sub = handler.Profile["subscription"]!;
                sub["status"] = scenario == "subscription_required" ? "expired" : "active";
                sub["expires_at"] = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
                sub["quota"] = new JsonObject { ["limit_bytes"] = 100, ["used_bytes"] = scenario == "low_quota" ? 81 : 80, ["resets_at"] = 100 };
                if (scenario != "signed_out")
                {
                    var session = new JsonObject { ["token"] = "test-session", ["me"] = handler.Profile.DeepClone() };
                    new SecretStore(Path.Combine(dir, "cloud")).Write("session.dpapi", session.ToJsonString());
                }
                var store = new SettingsStore(dir, Defaults());
                store.Load();
                string path = Path.Combine(dir, "settings.json");
                var old = JsonNode.Parse(File.ReadAllText(path))!;
                old["Update"]!["Source"] = scenario == "mirror" ? 1 : 0;
                File.WriteAllText(path, old.ToJsonString());
                store.Load();
                Check(store.Current.Update.Source == (scenario == "mirror" ? UpdateSource.MirrorChyan : UpdateSource.GitHub), "旧数值设置");
                var module = new CloudModule(() => new AccountService(dir, "0.1.99", handler), store);
                await module.StartAsync(default);
                try
                {
                    if (scenario == "mirror") { Check(store.Current.Update.Source == UpdateSource.MirrorChyan, "会员保留 Mirror酱"); continue; }
                    if (scenario == "subscription_required")
                    {
                        Check(store.Current.Update.Source == UpdateSource.GitHub, "未订阅时保留 GitHub");
                        sub["status"] = "active";
                        await module.Account!.RefreshAsync(default);
                        Check(store.Current.Update.Source == UpdateSource.PaneCloud, "已登录账号成为会员");
                        sub["status"] = "expired";
                        store.Update(s => s with { Update = s.Update with { Source = UpdateSource.GitHub } });
                    }
                    bool member = scenario is not ("signed_out" or "subscription_required");
                    Check(store.Current.Update.Source == (member ? UpdateSource.PaneCloud : UpdateSource.GitHub), "第一次成为会员");
                    if (member)
                    {
                        store.Update(s => s with { Update = s.Update with { Source = UpdateSource.GitHub } });
                        await module.Account!.RefreshAsync(default);
                        Check(store.Current.Update.Source == UpdateSource.GitHub, "尊重改回 GitHub");
                    }
                    if (scenario is "patch" or "broken")
                    {
                        AddPatch(handler, baseline);
                        if (scenario == "broken") File.Delete(Path.Combine(baseline, "Pane.App.exe"));
                    }
                    handler.Offline = scenario == "network";
                    handler.Conflict = scenario == "409";
                    if (scenario is "manual" or "automatic") handler.Available = DateTimeOffset.UtcNow.AddSeconds(scenario == "manual" ? 300 : 2);
                    int requests = github.Requests;
                    using var service = new UpdateService("0.1.99", dir, () => scenario is "manual" or "automatic", _ => {},
                        github.Url, () => UpdateSource.PaneCloud, testBaseline: baseline);
                    await service.CheckAsync(default);
                    Check(service.Latest != null && service.State == UpdateState.Available && handler.Transfers.Count == 0, scenario + " 检查");
                    if (scenario == "patch") CheckNotes(service.Latest, expected, "云源与 GitHub 正文一致");
                    if (scenario is "broken" or "range") Check(service.Latest is { Notes: "", Details: null }, scenario + " 空或缺失正文");
                    if (scenario == "range")
                    {
                        byte[] data = handler.Files[Full];
                        Directory.CreateDirectory(Path.Combine(dir, "updates"));
                        File.WriteAllBytes(Path.Combine(dir, "updates", Full + "." + PayloadPatch.Hash(data) + ".partial"), data[..2]);
                    }
                    if (scenario == "automatic")
                    {
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (service.State != UpdateState.Ready) await Task.Delay(30, wait.Token);
                    }
                    else await service.DownloadAsync(default);
                    string? reason = scenario switch { "low_quota" or "409" => "low_quota", "signed_out" or "subscription_required" or "network" => scenario, _ => null };
                    Check(service.State == UpdateState.Ready && service.CloudFallback == (reason != null) && service.CloudFallbackReason == reason, scenario + " 下载和原因");
                    Check((github.Requests > requests) == (reason != null) && !github.Credentials, scenario + " GitHub 请求边界");
                    if (reason != null) CheckNotes(service.Latest, expected, scenario + " 回退后使用 GitHub 正文");
                    if (scenario == "patch")
                    {
                        using var restored = new UpdateService("0.1.99", dir, () => false, _ => {}, github.Url,
                            () => UpdateSource.PaneCloud, testBaseline: baseline);
                        Check(restored.State == UpdateState.Ready, "重启恢复云更新");
                        CheckNotes(restored.Latest, expected, "重启保留云更新正文");
                    }
                    if (scenario == "patch") Check(service.Latest?.Incremental == true && handler.Transfers.Select(t => t.Name).SequenceEqual([Manifest, Patch]), "云补丁就绪");
                    if (scenario == "broken") Check(service.Latest?.Incremental == false && handler.Transfers.Select(t => t.Name).SequenceEqual([Manifest, Patch, Full]), "重建失败后从云下全量");
                    if (scenario == "range") Check(handler.Transfers.Single() == (Full, 2L), "HTTP Range 续传");
                }
                finally { await module.StopAsync(default); }
            }
            Console.WriteLine($"PASS cloud updates assertions={checks}");
        }
        finally { SetupFiles.Delete(root); }
    }
}
