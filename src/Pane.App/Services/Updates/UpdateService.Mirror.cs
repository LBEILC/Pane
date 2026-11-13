using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Ai;
using Pane.Services.Setup;

namespace Pane.Services.Updates;

internal sealed record MirrorSecret(string Cdk, DateTimeOffset? Expires);
[JsonSerializable(typeof(MirrorSecret))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class MirrorJson : JsonSerializerContext { }

internal sealed partial class UpdateService
{
    const string MirrorOs = "win", MirrorArch = "x64";
    internal const string MirrorSecretFile = "mirror-cdk.bin";
    static readonly Uri MirrorSource = new("https://mirrorchyan.com/api/resources/Pane/latest");
    readonly object checkSync = new();
    CancellationTokenSource? checking;
    long checkGeneration;
    Func<UpdateSource> selectedSource = () => UpdateSource.GitHub;
    Uri mirrorSource = MirrorSource;
    SecretStore secrets = null!;
    DateOnly? quotaDay;
    MirrorTicket? mirrorTicket;
    sealed record MirrorTicket(Uri Url, string Hash, long Size, DateTimeOffset Acquired);
    internal TimeSpan MirrorTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public UpdateOrigin? CheckedFrom { get; private set; }
    public UpdateOrigin? DownloadFrom { get; private set; }
    public string MirrorCdk { get; private set; } = "";
    public MirrorCdkStatus CdkStatus { get; private set; }
    public DateTimeOffset? CdkExpires { get; private set; }

    void InitializeMirror(string data, Func<UpdateSource>? selection, Uri? testSource)
    {
        selectedSource = selection ?? (() => UpdateSource.GitHub);
        if (testSource != null && (testSource.Scheme != "http" || testSource.Host != "127.0.0.1"))
            throw new ArgumentException("测试源必须是本机 HTTP 服务");
        mirrorSource = testSource ?? MirrorSource;
        secrets = new(data);
        try
        {
            var text = secrets.Read(MirrorSecretFile);
            if (text == null) return;
            var saved = JsonSerializer.Deserialize(text, MirrorJson.Default.MirrorSecret) ?? throw new JsonException();
            MirrorCdk = saved.Cdk?.Trim() ?? ""; CdkExpires = saved.Expires;
            CdkStatus = MirrorCdk.Length == 0 ? MirrorCdkStatus.None : CdkExpires > DateTimeOffset.UtcNow ? MirrorCdkStatus.Valid : MirrorCdkStatus.Unknown;
        }
        catch (Exception e) when (e is AiException || Expected(e))
        { MirrorCdk = ""; CdkExpires = null; CdkStatus = MirrorCdkStatus.None; LogMirror("read", e); }
    }
    void LogMirror(string operation, Exception e) => new SelectionLog(Path.GetDirectoryName(directory)!).Write("update-cdk " + operation + ": " + e.GetType().Name);
    void PersistCdk() => secrets.Write(MirrorSecretFile, MirrorCdk.Length == 0 ? null : JsonSerializer.Serialize(new MirrorSecret(MirrorCdk, CdkExpires), MirrorJson.Default.MirrorSecret));
    public void SaveMirrorCdk(string cdk)
    {
        lock (checkSync)
        {
            cdk = cdk.Trim(); if (cdk == MirrorCdk) return;
            // 先写成功再替换内存，保存失败不会假装已经生效。
            secrets.Write(MirrorSecretFile, cdk.Length == 0 ? null : JsonSerializer.Serialize(new MirrorSecret(cdk, null), MirrorJson.Default.MirrorSecret));
            MirrorCdk = cdk; CdkExpires = null; quotaDay = null;
            CdkStatus = cdk.Length == 0 ? MirrorCdkStatus.None : MirrorCdkStatus.Unknown;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    async Task CheckOperation(CancellationToken token, bool scheduled)
    {
        long generation;
        lock (checkSync)
        {
            if (disposed) return;
            // 计时器只尝试立即取得门闩；手动请求则合并成最后一次排队检查。
            if (scheduled && !gate.Wait(0)) return;
            generation = ++checkGeneration;
            // 自动下载也持有 gate，但下载阶段不取消；手动检查等待它完成。
            if (!scheduled && activeStep == UpdateStep.Check) checking?.Cancel();
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try { if (!scheduled) await gate.WaitAsync(cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        lock (checkSync)
        {
            if (disposed || generation != checkGeneration) { gate.Release(); return; }
            checking = cancel; activeStep = UpdateStep.Check;
        }
        try { await CheckCore(cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { Set(cancel.IsCancellationRequested ? (Latest == null ? UpdateState.Idle : UpdateState.Available) : UpdateState.Failed, cancel.IsCancellationRequested ? null : Strings.Update_CheckTimeout); }
        catch (Exception e) when (Expected(e)) { Set(UpdateState.Failed, Message(e)); }
        finally { lock (checkSync) { checking = null; gate.Release(); } }
    }
    void CdkResult(string cdk, MirrorCdkStatus status, DateTimeOffset? expires, bool clear = false)
    {
        lock (checkSync)
        {
            if (cdk != MirrorCdk) return;
            CdkStatus = status;
            var previousExpiry = CdkExpires;
            if (clear || expires != null) CdkExpires = expires;
            if (status == MirrorCdkStatus.QuotaExhausted) quotaDay = DateOnly.FromDateTime(DateTime.Now);
            if (previousExpiry != CdkExpires)
                try { PersistCdk(); } catch (AiException e) { LogMirror("save", e); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    static MirrorCdkStatus? CdkError(int code) => code switch
    {
        7001 => MirrorCdkStatus.Expired, 7002 => MirrorCdkStatus.Invalid, 7003 => MirrorCdkStatus.QuotaExhausted,
        7004 => MirrorCdkStatus.Mismatched, 7005 => MirrorCdkStatus.Blocked, _ => null
    };
    async Task<JsonDocument> RequestMirror(string cdk, CancellationToken token)
    {
        var query = new Dictionary<string, string> { ["os"] = MirrorOs, ["arch"] = MirrorArch, ["channel"] = "stable", ["user_agent"] = "Pane" };
        if (cdk.Length > 0) query["cdk"] = cdk;
        using var encoded = new FormUrlEncodedContent(query);
        var url = new UriBuilder(mirrorSource) { Query = await encoded.ReadAsStringAsync(token).ConfigureAwait(false) }.Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(MirrorTimeout);
        using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        // 错误响应也读取业务码；非成功 HTTP 的 code=0 不能当成功。
        if (!response.IsSuccessStatusCode && document.RootElement.GetProperty("code").GetInt32() == 0)
        { document.Dispose(); throw new InvalidDataException(); }
        return document;
    }
    async Task<bool> CheckMirror(CancellationToken token)
    {
        string cdk; bool skip;
        lock (checkSync)
        {
            cdk = MirrorCdk; skip = quotaDay == DateOnly.FromDateTime(DateTime.Now);
            if (skip) CdkStatus = MirrorCdkStatus.QuotaExhausted;
            if (cdk.Length > 0 && !skip) CdkStatus = MirrorCdkStatus.Checking;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            using var first = await RequestMirror(skip ? "" : cdk, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            int code = first.RootElement.GetProperty("code").GetInt32();
            if (CdkError(code) is {} error)
            {
                CdkResult(cdk, error, null, true);
                using var retry = await RequestMirror("", token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ApplyMirror(retry.RootElement, false);
            }
            else
            {
                ApplyMirror(first.RootElement, !skip);
                if (!skip) CdkResult(cdk, cdk.Length == 0 ? MirrorCdkStatus.None : MirrorCdkStatus.Valid, Expiry(first.RootElement));
            }
            CheckedFrom = UpdateOrigin.MirrorChyan;
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception e) when (Expected(e)) { }
        token.ThrowIfCancellationRequested();
        CdkResult(cdk, cdk.Length == 0 ? MirrorCdkStatus.None : MirrorCdkStatus.Unknown, null);
        Latest = null; mirrorTicket = null; DownloadFrom = null;
        return false;
    }
    static DateTimeOffset? Expiry(JsonElement root) => root.TryGetProperty("data", out var data) && data.TryGetProperty("cdk_expired_time", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    static string MirrorVersion(JsonElement root)
    {
        if (root.GetProperty("code").GetInt32() != 0) throw new InvalidDataException();
        string version = root.GetProperty("data").GetProperty("version_name").GetString() ?? "";
        if (version.StartsWith('v')) version = version[1..];
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(\.\d+)?$")) throw new InvalidDataException();
        return version;
    }
    MirrorTicket? Ticket(JsonElement data)
    {
        if (!data.TryGetProperty("url", out var url) || string.IsNullOrEmpty(url.GetString()) ||
            !data.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String || !Regex.IsMatch(hash.GetString() ?? "", @"\A[a-fA-F0-9]{64}\z") ||
            !data.TryGetProperty("filesize", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes <= 0) return null;
        return new(ValidateMirrorUrl(url.GetString()!), hash.GetString()!, bytes, DateTimeOffset.UtcNow);
    }
    void ApplyMirror(JsonElement root, bool allowTicket)
    {
        string version = MirrorVersion(root);
        var data = root.GetProperty("data");
        if (SetupFiles.CompareVersions(version, CurrentVersion) <= 0) return;
        mirrorTicket = allowTicket ? Ticket(data) : null;
        string body = data.TryGetProperty("release_note", out var notes) ? notes.GetString() ?? "" : "";
        long size = data.TryGetProperty("filesize", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out long value) ? Math.Max(0, value) : 0;
        Latest = new(version, PlainNotes(body), null, size, "https://github.com/LBEILC/Pane/releases/tag/v" + version,
            ReleaseNotesCatalog.ParseMarkdown(body, version, LastChecked!.Value));
        DownloadFrom = mirrorTicket == null ? UpdateOrigin.GitHub : UpdateOrigin.MirrorChyan;
    }
    Uri ValidateMirrorUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.UserInfo.Length != 0 ||
            !(mirrorSource != MirrorSource ? url.Scheme == "http" && url.Host == "127.0.0.1" && url.Port == mirrorSource.Port : url.Scheme == "https"))
            throw new InvalidDataException();
        return url;
    }
    async Task LoadGitHubAssets(CancellationToken token)
    {
        var url = new Uri(source, "tags/v" + Latest!.Version);
        HttpResponseMessage response;
        try { response = await GetAsync(url, token).ConfigureAwait(false); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { throw new JsonException(); }
        using var responseScope = response;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || root.GetProperty("tag_name").GetString() != "v" + Latest.Version)
            throw new JsonException();
        var files = root.GetProperty("assets").EnumerateArray().ToArray();
        var package = files.Single(f => f.GetProperty("name").GetString() == $"Pane-Setup-{Latest.Version}.exe");
        asset = ValidateUrl(package.GetProperty("browser_download_url").GetString()!);
        sums = ValidateUrl(files.Single(f => f.GetProperty("name").GetString() == "SHA256SUMS.txt").GetProperty("browser_download_url").GetString()!);
        Latest = Latest with { SizeBytes = package.GetProperty("size").GetInt64() };
    }
    bool TryReuseReady()
    {
        if (Latest == null) return false;
        var current = Latest; var origin = DownloadFrom; var state = State;
        RestoreReady();
        if (State == UpdateState.Ready && Latest?.Version == current.Version) { Changed?.Invoke(this, EventArgs.Empty); return true; }
        Latest = current; DownloadFrom = origin; readyPath = null; State = state;
        return false;
    }
    async Task RefreshMirror(CancellationToken token)
    {
        string cdk = MirrorCdk;
        using var json = await RequestMirror(cdk, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        int code = json.RootElement.GetProperty("code").GetInt32();
        if (CdkError(code) is {} error) { CdkResult(cdk, error, null, true); throw new InvalidDataException(); }
        if (MirrorVersion(json.RootElement) != Latest!.Version) throw new InvalidDataException();
        mirrorTicket = Ticket(json.RootElement.GetProperty("data")) ?? throw new InvalidDataException();
        CdkResult(cdk, cdk.Length == 0 ? MirrorCdkStatus.None : MirrorCdkStatus.Valid, Expiry(json.RootElement));
        Latest = Latest with { SizeBytes = mirrorTicket.Size };
    }
    async Task<HttpResponseMessage> GetMirrorDownload(Uri url, CancellationToken token)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ValidateMirrorUrl(url.AbsoluteUri));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                url = ValidateMirrorUrl(new Uri(url, location ?? throw new InvalidDataException()).AbsoluteUri); continue;
            }
            return response;
        }
        throw new InvalidDataException();
    }
    async Task DownloadMirror(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, $"Pane-Setup-{Latest!.Version}.exe"), partial = target + ".partial";
        readyPath = null; DownloadProgress = 0; Set(UpdateState.Downloading);
        try
        {
            File.Delete(target);
            if (mirrorTicket == null || DateTimeOffset.UtcNow - mirrorTicket.Acquired > TimeSpan.FromMinutes(10)) await RefreshMirror(token).ConfigureAwait(false);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var ticket = mirrorTicket!;
                using var response = await GetMirrorDownload(ticket.Url, token).ConfigureAwait(false);
                bool expired = (int)response.StatusCode is 403 or 404 or 410 || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
                if (expired)
                {
                    if (attempt == 1) throw new InvalidDataException();
                    await RefreshMirror(token).ConfigureAwait(false); continue;
                }
                if (!response.IsSuccessStatusCode) throw new InvalidDataException();
                bool jsonBody = false;
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    byte[] buffer = new byte[65536]; long received = 0; int read;
                    while ((read = received == 0
                        ? await input.ReadAtLeastAsync(buffer, 512, false, token).ConfigureAwait(false)
                        : await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        // 有些 CDN 不标 application/json；EXE 不会以 JSON 对象或数组开头。
                        if (received == 0 && LooksLikeJson(buffer.AsSpan(0, read))) { jsonBody = true; break; }
                        received += read; if (received > ticket.Size) throw new InvalidDataException();
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        DownloadProgress = (double)received / ticket.Size; Changed?.Invoke(this, EventArgs.Empty);
                    }
                }
                if (jsonBody)
                {
                    File.Delete(partial);
                    if (attempt == 1) throw new InvalidDataException();
                    await RefreshMirror(token).ConfigureAwait(false); continue;
                }
                Latest = Latest with { SizeBytes = ticket.Size };
                if (!await Valid(partial, ticket.Hash, token).ConfigureAwait(false)) throw new InvalidDataException();
                File.Move(partial, target, true); CacheReady(target, ticket.Hash); return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { Set(UpdateState.Failed, Strings.Update_MirrorDownloadFailed); }
        catch (Exception e) when (Expected(e)) { Set(UpdateState.Failed, Strings.Update_MirrorDownloadFailed); }
        finally { File.Delete(partial); }
    }
    static bool LooksLikeJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        foreach (byte value in bytes)
        {
            if (value is 9 or 10 or 13 or 32) continue;
            return value is (byte)'{' or (byte)'[';
        }
        return false;
    }
}
