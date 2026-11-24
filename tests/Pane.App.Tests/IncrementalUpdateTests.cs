using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Services.Updates;

internal static class IncrementalUpdateTests
{
    static byte[] Json(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString());
    static JsonObject Header() => new() { ["format"] = 1, ["product"] = "Pane", ["arch"] = "x64" };
    static byte[] Manifest(string version, params (string Path, byte[] Bytes)[] files)
    {
        var json = Header();
        json["version"] = version;
        var list = new JsonArray();
        foreach (var (path, bytes) in files)
            list.Add((JsonNode)new JsonObject { ["path"] = path, ["size"] = bytes.Length, ["sha256"] = PayloadPatch.Hash(bytes) });
        json["files"] = list;
        return Json(json);
    }

    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("111: " + description);
            count++;
        }
        const string from = "0.1.99", to = "0.1.100";
        byte[] app = [1, 2], helper = [3, 4], data = [5], obsolete = [6];
        var original = Manifest(from, ("Pane.App.exe", app), ("Pane.Maintenance.exe", helper), ("old.dll", obsolete));
        var manifest = Manifest(to, ("Pane.App.exe", app), ("Pane.Maintenance.exe", helper), ("new.dll", data));
        foreach (var path in new[] { "../x", @"C:\x", "/x", @"a\b", "NUL", "a/../b", "a." })
        {
            try { PayloadPatch.Manifest(Manifest(to, (path, app)), to); throw new Exception("接受非法路径 " + path); }
            catch (InvalidDataException) { count++; }
        }
        foreach (var duplicate in new[] { "A", "a" })
        {
            try { PayloadPatch.Manifest(Manifest(to, ("A", app), (duplicate, app)), to); throw new Exception("接受重复路径"); }
            catch (InvalidDataException) { count++; }
        }
        var metadata = Header();
        metadata["from"] = from;
        metadata["to"] = to;
        metadata["fromManifestSha256"] = PayloadPatch.Hash(original);
        metadata["toManifestSha256"] = PayloadPatch.Hash(manifest);
        metadata["ops"] = new JsonArray(
            new JsonObject { ["path"] = "new.dll", ["op"] = "replace", ["entry"] = "f/0", ["size"] = 1, ["sha256"] = PayloadPatch.Hash(data) },
            new JsonObject { ["path"] = "old.dll", ["op"] = "delete" });
        byte[] Package()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                foreach (var (name, bytes) in new[] { ("patch.json", Json(metadata)), ("base.json", original), ("f/0", data) })
                {
                    using var output = zip.CreateEntry(name).Open();
                    output.Write(bytes);
                }
            return buffer.ToArray();
        }

        await using var server = new UpdateServer();
        string baseline = Path.Combine(root, "installed");
        Directory.CreateDirectory(baseline);
        File.WriteAllBytes(Path.Combine(baseline, "Pane.Maintenance.exe"), helper);
        File.WriteAllBytes(Path.Combine(baseline, "old.dll"), obsolete);
        foreach (string scenario in new[] { "valid", "missing", "other-version", "wrong-from", "bad-hash", "changed-base", "bad-output" })
        {
            File.WriteAllBytes(Path.Combine(baseline, "Pane.App.exe"), scenario == "changed-base" ? [9, 9] : app);
            metadata["from"] = scenario == "wrong-from" ? "0.1.98" : from;
            data[0] = scenario == "bad-output" ? (byte)8 : (byte)5;
            byte[] patch = Package();
            string patchName = $"Pane-Patch-{(scenario == "other-version" ? "0.1.98" : from)}-{to}.zip";
            string newName = $"Pane-Manifest-{to}.json", setup = $"Pane-Setup-{to}.exe";
            var attachments = new Dictionary<string, byte[]> { [setup] = server.Payload, [newName] = manifest };
            if (scenario != "missing") attachments[patchName] = patch;
            string sums = string.Join('\n', attachments.Select(pair =>
                (scenario == "bad-hash" && pair.Key == patchName ? new string('0', 64) : PayloadPatch.Hash(pair.Value)) + "  " + pair.Key));
            attachments["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(sums);
            var assets = new JsonArray();
            foreach (var (key, value) in attachments)
                assets.Add((JsonNode)new JsonObject { ["name"] = key, ["size"] = value.Length, ["browser_download_url"] = new Uri(server.Url, key).AbsoluteUri });
            byte[] release = Json(new JsonObject { ["tag_name"] = "v" + to, ["draft"] = false, ["prerelease"] = false,
                ["body"] = "", ["published_at"] = "2026-01-01T00:00:00Z", ["html_url"] = server.Url.AbsoluteUri, ["assets"] = assets });
            server.Paths.Clear();
            server.Respond = path => Task.FromResult<UpdateServer.Reply?>(new(200, path == "/latest" ? release : attachments[path[1..]]));
            string? launched = null;
            string directory = Path.Combine(root, "incremental-" + scenario);
            UpdateService Service() => new(from, directory, () => false, path => launched = path, server.Url, testBaseline: baseline);
            using var service = Service();
            await service.CheckAsync(default);
            Check(service.Latest?.Incremental == (scenario is not ("missing" or "other-version")), scenario + " 选择附件");
            await service.DownloadAsync(default);
            bool success = scenario == "valid";
            Check(service.State == UpdateState.Ready && service.Latest?.Incremental == success, scenario + " 下载就绪");
            Check(server.Paths.Count(p => p == "/" + setup) == (success ? 0 : 1), scenario + " 全量请求次数");
            using var restart = Service();
            restart.InstallAndRestart();
            Check(File.Exists(launched), scenario + " 启动缓存");
            if (success)
            {
                PayloadPatch.VerifyDirectory(Path.GetDirectoryName(launched!)!, PayloadPatch.Manifest(manifest, to));
                count++;
            }
            else Check(!Directory.EnumerateDirectories(Path.Combine(directory, "updates"), "patch-*").Any(), scenario + " 清掉重建目录");
        }
        Console.WriteLine($"PASS incremental update assertions={count}");
        return count;
    }
}
