using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Settings;

namespace Pane.Services.Updates;

internal interface ICloudUpdateAccount
{
    AccountState State { get; }
    CloudProfile? Profile { get; }
    Task<CloudResult> RefreshAsync(CancellationToken token);
    Task<HttpResponseMessage> GetReleaseAsync(string path, long offset, CancellationToken token);
}

internal sealed class CloudUnavailable(string reason) : Exception(reason);

internal sealed partial class UpdateService
{
    // 云模块的生命周期决定账号是否可用，包括运行中关闭、再打开模块。
    internal static volatile ICloudUpdateAccount? ActiveCloudAccount = null;
    public bool CloudFallback => CloudFallbackReason != null;
    public string? CloudFallbackReason { get; private set; }
    DateTimeOffset availableAt;
    Dictionary<string, (PatchAsset Asset, string Hash)>? cloudFiles;

    static bool Member(CloudSubscription? s) => s != null && s.Status is SubscriptionStatus.Trial or SubscriptionStatus.Active
        && (s.Lifetime || s.ExpiresAt > DateTimeOffset.UtcNow);

    internal static void ApplyMemberDefault(SettingsStore store, ICloudUpdateAccount account)
    {
        if (account.State != AccountState.SignedIn || !Member(account.Profile?.Subscription)) return;
        string marker = Path.Combine(store.DirectoryPath, "updates", "member-source");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        store.UpdateFromSync(s => s.Update.Source == UpdateSource.GitHub
            ? s with { Update = s.Update with { Source = UpdateSource.PaneCloud } } : s);
        File.WriteAllText(marker, "");
    }

    async Task<ICloudUpdateAccount> Eligible(CancellationToken token)
    {
        // TODO(design): 能力停用时没有账号服务，暂与未登录共用 signed_out。
        var account = ActiveCloudAccount;
        if (account?.State != AccountState.SignedIn) throw new CloudUnavailable("signed_out");
        var refreshed = await account.RefreshAsync(token).ConfigureAwait(false);
        if (refreshed.Error != null) throw new CloudUnavailable(refreshed.Error == "unauthorized" ? "signed_out" : "network");
        if (account != ActiveCloudAccount || account.State != AccountState.SignedIn) throw new CloudUnavailable("signed_out");
        var subscription = account.Profile?.Subscription;
        if (!Member(subscription)) throw new CloudUnavailable("subscription_required");
        if (subscription!.Quota is not { LimitBytes: > 0 } quota || quota.UsedBytes > quota.LimitBytes * 0.8m)
            throw new CloudUnavailable("low_quota");
        return account;
    }

    async Task<bool> CheckCloud(CancellationToken token)
    {
        try
        {
            var account = await Eligible(token).ConfigureAwait(false);
            using var response = await account.GetReleaseAsync("releases/latest/files", 0, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = json.RootElement;
            var version = root.GetProperty("version").GetString() ?? "";
            if (!Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z")) throw new InvalidDataException();
            if (SetupFiles.CompareVersions(version, CurrentVersion) <= 0) return true;
            availableAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("available_at").GetInt64());
            var published = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("published_at").GetInt64());
            string setup = $"Pane-Setup-{version}.exe", patch = $"Pane-Patch-{CurrentVersion}-{version}.zip", manifest = $"Pane-Manifest-{version}.json";
            var files = new Dictionary<string, (PatchAsset Asset, string Hash)>();
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                if (name != setup && name != patch && name != manifest) continue;
                long size = item.GetProperty("size").GetInt64();
                string hash = item.GetProperty("sha256").GetString() ?? "";
                if (size <= 0 || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z")) throw new InvalidDataException();
                files.Add(name, (new(name, new Uri($"releases/{version}/files/{name}", UriKind.Relative), size), hash.ToLowerInvariant()));
            }
            var installer = files[setup].Asset;
            Latest = new(version, "", published, installer.Size, $"https://github.com/LBEILC/Pane/releases/tag/v{version}");
            if (files.TryGetValue(patch, out var delta) && files.TryGetValue(manifest, out var list))
            {
                incremental = new(delta.Asset, list.Asset, installer.Size);
                Latest = Latest with { Incremental = true, SizeBytes = delta.Asset.Size };
            }
            cloudFiles = files;
            return true;
        }
        catch (CloudUnavailable e) { CloudFallbackReason = e.Message; }
        catch (Exception e) when (Expected(e) || e is OperationCanceledException && !token.IsCancellationRequested)
        { CloudFallbackReason = "network"; }
        token.ThrowIfCancellationRequested();
        return false;
    }

    async Task DownloadCloud(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        DownloadProgress = 0;
        Set(UpdateState.Downloading);
        var files = cloudFiles!;
        if (incremental is {} assets)
        {
            try
            {
                var hashes = string.Join('\n', files.Select(pair => pair.Value.Hash + "  " + pair.Key));
                await PrepareIncremental(assets, hashes, token).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (Expected(e))
            {
                LogPatchFailure(e);
                incremental = null;
                Latest = Latest! with { Incremental = false, SizeBytes = assets.InstallerSize };
                DownloadProgress = 0;
                Set(UpdateState.Downloading);
            }
        }
        var file = files[$"Pane-Setup-{Latest!.Version}.exe"];
        string target = Path.Combine(directory, file.Asset.Name);
        await FetchCloud(file.Asset, file.Hash, target, true, token).ConfigureAwait(false);
        CacheReady(target, file.Hash);
    }

    async Task FetchCloud(PatchAsset file, string hash, string destination, bool progress, CancellationToken token)
    {
        string partial = Path.Combine(directory, file.Name + "." + hash + ".partial");
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > file.Size) { File.Delete(partial); offset = 0; }
        if (offset < file.Size)
        {
            try
            {
                var account = await Eligible(token).ConfigureAwait(false);
                using var response = await account.GetReleaseAsync(file.Url.OriginalString, offset, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.Unit != "bytes" || range.From != offset || range.To != file.Size - 1 || range.Length != file.Size)
                        throw new InvalidDataException();
                }
                else offset = 0;
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536];
                int read;
                while (true)
                {
                    try { read = await input.ReadAsync(buffer, token).ConfigureAwait(false); }
                    catch (IOException) { throw new CloudUnavailable("network"); }
                    if (read == 0) break;
                    if (read > file.Size - output.Position) throw new InvalidDataException();
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (progress && (double)output.Position / file.Size - DownloadProgress >= .01)
                    {
                        DownloadProgress = (double)output.Position / file.Size;
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                }
                if (output.Position != file.Size) throw new CloudUnavailable("network");
            }
            catch (Exception e) when (e is HttpRequestException or ObjectDisposedException || e is OperationCanceledException && !token.IsCancellationRequested)
            { throw new CloudUnavailable("network"); }
        }
        try { new PayloadPatch.Stamp(file.Size, hash).Verify(partial); }
        catch (InvalidDataException) { File.Delete(partial); throw; }
        File.Move(partial, destination, true);
    }
}
