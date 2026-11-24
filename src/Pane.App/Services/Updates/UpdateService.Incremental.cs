using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Services.Setup;

namespace Pane.Services.Updates;

internal sealed partial class UpdateService
{
    sealed record PatchAsset(string Name, Uri Url, long Size);
    sealed record IncrementalAssets(PatchAsset Patch, PatchAsset Manifest, long InstallerSize);
    IncrementalAssets? incremental;
    readonly string baseline;

    void SelectIncremental(JsonElement[] assets, string version)
    {
        PatchAsset? Find(string name)
        {
            var found = assets.Where(f => f.GetProperty("name").GetString() == name).ToArray();
            if (found.Length != 1) return null;
            var file = found[0];
            return new(name, ValidateUrl(file.GetProperty("browser_download_url").GetString()!), file.GetProperty("size").GetInt64());
        }
        try
        {
            var patch = Find($"Pane-Patch-{CurrentVersion}-{version}.zip");
            var manifest = Find($"Pane-Manifest-{version}.json");
            if (patch is not { Size: > 0 } || manifest is not { Size: > 0 }) return;
            incremental = new(patch, manifest, Latest!.SizeBytes);
            Latest = Latest with { SizeBytes = patch.Size, Incremental = true };
        }
        catch (Exception error) when (Expected(error)) { LogPatchFailure(error); }
    }

    void LogPatchFailure(Exception error) =>
        new SelectionLog(Path.GetDirectoryName(directory)!).Write("update patch skipped: " + error.GetType().Name);

    static string Checksum(string text, string name)
    {
        var matches = text.Split('\n').Select(line => Regex.Match(line.Trim(), @"^([a-fA-F0-9]{64})\s+\*?(.+)$"))
            .Where(m => m.Success && m.Groups[2].Value == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException();
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }

    async Task FetchPatchFile(PatchAsset file, string hash, string destination, bool progress, CancellationToken token)
    {
        if (cloudFiles != null)
        {
            await FetchCloud(file, hash, destination, progress, token).ConfigureAwait(false);
            return;
        }
        using var response = await GetAsync(file.Url, token).ConfigureAwait(false);
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using (var output = File.Create(destination))
        {
            byte[] buffer = new byte[65536];
            var watch = Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (read > file.Size - output.Position) throw new InvalidDataException();
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (progress && watch.ElapsedMilliseconds >= 200)
                {
                    DownloadProgress = (double)output.Position / file.Size;
                    Changed?.Invoke(this, EventArgs.Empty);
                    watch.Restart();
                }
            }
        }
        new PayloadPatch.Stamp(file.Size, hash).Verify(destination);
    }

    async Task PrepareIncremental(IncrementalAssets assets, string checksums, CancellationToken token)
    {
        var id = Guid.NewGuid();
        string work = Path.Combine(directory, "patch-" + id.ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string manifest = Path.Combine(work, "manifest.json"), patch = Path.Combine(work, "patch.zip");
            string manifestHash = Checksum(checksums, assets.Manifest.Name);
            await FetchPatchFile(assets.Manifest, manifestHash, manifest, false, token).ConfigureAwait(false);
            await FetchPatchFile(assets.Patch, Checksum(checksums, assets.Patch.Name), patch, true, token).ConfigureAwait(false);
            string payload = Path.Combine(work, "payload");
            PayloadPatch.Apply(baseline, patch, File.ReadAllBytes(manifest), CurrentVersion, Latest!.Version, payload, token);
            string helper = Path.Combine(payload, "Pane.Maintenance.exe");
            PayloadPatch.Require(File.Exists(helper) && File.Exists(Path.Combine(payload, "Pane.App.exe")));
            File.Delete(patch);
            CacheReady(helper, manifestHash, id);
        }
        catch { SetupFiles.Delete(work); throw; }
    }

    string RestoreIncremental(CachedUpdate cache)
    {
        PayloadPatch.Require(cache.DirectoryId.HasValue && cache.BaseVersion == CurrentVersion && cache.Origin is null or UpdateOrigin.GitHub);
        string work = Path.Combine(directory, "patch-" + cache.DirectoryId.GetValueOrDefault().ToString("N"));
        SetupFiles.Check(work);
        string manifest = Path.Combine(work, "manifest.json");
        SetupFiles.Check(manifest);
        byte[] bytes = File.ReadAllBytes(manifest);
        PayloadPatch.Require(PayloadPatch.Hash(bytes) == cache.Hash);
        string payload = Path.Combine(work, "payload");
        PayloadPatch.VerifyDirectory(payload, PayloadPatch.Manifest(bytes, cache.Release.Version));
        string helper = Path.Combine(payload, "Pane.Maintenance.exe");
        PayloadPatch.Require(File.Exists(helper));
        return helper;
    }
}
