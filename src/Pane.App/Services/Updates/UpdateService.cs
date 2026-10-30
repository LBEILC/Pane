using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Pane.Services.Setup;

namespace Pane.Services.Updates;

internal sealed class UpdateService : IUpdateService, IDisposable
{
    internal static readonly Uri ProductionSource = new("https://api.github.com/repos/LBEILC/Pane/releases/latest");
    readonly HttpClient http;
    readonly Uri source;
    readonly string directory;
    readonly Func<bool> automatic;
    readonly Action<string> install;
    readonly SemaphoreSlim gate = new(1);
    readonly CancellationTokenSource lifetime = new();
    readonly Timer timer;
    DateTimeOffset retryAfter;
    Uri? asset, sums;
    string? readyPath;
    bool disposed;
    public string CurrentVersion { get; }
    public UpdateState State { get; private set; }
    public UpdateRelease? Latest { get; private set; }
    public double DownloadProgress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public event EventHandler? Changed;

    // 测试源只由测试宿主显式注入；产品接线不读取任何更新源环境变量。
    public UpdateService(string version, string data, Func<bool> automatic, Action<string> install, Uri? testSource = null)
    {
        CurrentVersion = version; directory = Path.Combine(data, "updates");
        this.automatic = automatic; this.install = install; source = testSource ?? ProductionSource;
        if (testSource != null && (testSource.Scheme != "http" || testSource.Host != "127.0.0.1"))
            throw new ArgumentException("测试源必须是本机 HTTP 服务");
        http = new(new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Pane/" + version);
        http.Timeout = TimeSpan.FromMinutes(10);
        timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
    }
    async Task TickAsync() { if (!disposed && automatic()) await CheckAsync(lifetime.Token).ConfigureAwait(false); }
    void Set(UpdateState state, string? error = null) { State = state; Error = error; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (disposed || !await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            if (DateTimeOffset.UtcNow < retryAfter) return;
            Set(UpdateState.Checking); LastChecked = DateTimeOffset.UtcNow;
            using var response = await GetAsync(source, cancel.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false));
            var root = json.RootElement;
            Latest = null; readyPath = null; asset = sums = null;
            string version = root.GetProperty("tag_name").GetString()!.TrimStart('v');
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
                !Regex.IsMatch(version, @"^\d+\.\d+\.\d+(\.\d+)?$") || SetupFiles.CompareVersions(version, CurrentVersion) <= 0)
            { Set(UpdateState.UpToDate); return; }
            string name = $"Pane-Setup-{version}.exe";
            var files = root.GetProperty("assets").EnumerateArray().ToArray();
            var package = files.Single(f => f.GetProperty("name").GetString() == name);
            asset = ValidateUrl(package.GetProperty("browser_download_url").GetString()!);
            sums = ValidateUrl(files.Single(f => f.GetProperty("name").GetString() == "SHA256SUMS.txt").GetProperty("browser_download_url").GetString()!);
            string notes = PlainNotes(root.GetProperty("body").GetString() ?? "");
            Latest = new(version, notes, root.GetProperty("published_at").GetDateTimeOffset(), package.GetProperty("size").GetInt64(),
                ValidateUrl(root.GetProperty("html_url").GetString()!).AbsoluteUri);
            Set(UpdateState.Available);
            if (automatic() || File.Exists(Path.Combine(directory, name))) await DownloadCore(cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Set(cancel.IsCancellationRequested ? (Latest == null ? UpdateState.Idle : UpdateState.Available) : UpdateState.Failed, cancel.IsCancellationRequested ? null : "连 GitHub 超时了，稍后再试"); }
        catch (Exception e) when (Expected(e)) { Set(UpdateState.Failed, Message(e)); }
        finally { gate.Release(); }
    }
    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (disposed || !await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try { if (Latest != null && asset != null && sums != null) await DownloadCore(cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Set(cancel.IsCancellationRequested ? UpdateState.Available : UpdateState.Failed, cancel.IsCancellationRequested ? null : "下载超时了，稍后再试"); }
        catch (Exception e) when (Expected(e)) { Set(UpdateState.Failed, Message(e)); }
        finally { gate.Release(); }
    }
    async Task DownloadCore(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow < retryAfter) { Set(UpdateState.Failed, "GitHub 暂时限制了检查次数，过一会儿再试"); return; }
        Directory.CreateDirectory(directory);
        string name = $"Pane-Setup-{Latest!.Version}.exe", target = Path.Combine(directory, name), partial = target + ".partial";
        readyPath = null; DownloadProgress = 0; Set(UpdateState.Downloading);
        try
        {
            using var checksum = await GetAsync(sums!, token).ConfigureAwait(false);
            string text = await checksum.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var entries = text.Split('\n').Select(line => Regex.Match(line.Trim(), @"^([a-fA-F0-9]{64})\s+\*?(.+)$"))
                .Where(m => m.Success && m.Groups[2].Value == name).ToArray();
            if (entries.Length != 1) throw new InvalidDataException();
            string hash = entries[0].Groups[1].Value;
            if (File.Exists(target) && await Valid(target, hash, token).ConfigureAwait(false)) { Ready(target); return; }
            File.Delete(target);
            using var response = await GetAsync(asset!, token).ConfigureAwait(false);
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536]; long received = 0; double notified = 0; var watch = Stopwatch.StartNew();
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); received += read;
                    double progress = Latest.SizeBytes > 0 ? Math.Min(1, (double)received / Latest.SizeBytes) : 0;
                    if (progress - notified >= .01 || watch.ElapsedMilliseconds >= 200)
                    { DownloadProgress = progress; notified = progress; watch.Restart(); Changed?.Invoke(this, EventArgs.Empty); }
                }
            }
            if (!await Valid(partial, hash, token).ConfigureAwait(false)) throw new InvalidDataException();
            File.Move(partial, target, true); Ready(target);
        }
        finally { File.Delete(partial); }
    }
    async Task<bool> Valid(string path, string hash, CancellationToken token)
    {
        if (new FileInfo(path).Length != Latest!.SizeBytes) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    void Ready(string path)
    {
        foreach (string old in Directory.EnumerateFiles(directory, "Pane-Setup-*.exe")) if (old != path) File.Delete(old);
        readyPath = path; DownloadProgress = 1; Set(UpdateState.Ready);
    }
    Uri ValidateUrl(string text)
    {
        var url = new Uri(text);
        bool allowed = source != ProductionSource ? url.Scheme == "http" && url.Host == "127.0.0.1" && url.Port == source.Port :
            url.Scheme == "https" && (url.Host == "github.com" || url.Host == "api.github.com" || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
        if (!allowed || url.UserInfo.Length != 0) throw new InvalidDataException();
        return url;
    }
    async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken token)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 403 or 429)
            {
                retryAfter = DateTimeOffset.UtcNow.AddHours(1);
                if (response.Headers.RetryAfter?.Date is {} date && date > retryAfter) retryAfter = date;
                if (response.Headers.RetryAfter?.Delta is {} delay && DateTimeOffset.UtcNow + delay > retryAfter) retryAfter = DateTimeOffset.UtcNow + delay;
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out long seconds))
                { var reset = DateTimeOffset.FromUnixTimeSeconds(seconds); if (reset > retryAfter) retryAfter = reset; }
                response.Dispose(); throw new HttpRequestException("限流", null, HttpStatusCode.TooManyRequests);
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                url = ValidateUrl(new Uri(url, location ?? throw new InvalidDataException()).AbsoluteUri); continue;
            }
            if (!response.IsSuccessStatusCode) { var status = response.StatusCode; response.Dispose(); throw new HttpRequestException("请求失败", null, status); }
            return response;
        }
        throw new HttpRequestException("重定向过多");
    }
    internal static string PlainNotes(string notes)
    {
        notes = Regex.Replace(notes, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        notes = Regex.Replace(notes, @"(?m)^\s{0,3}(#{1,6}\s*|>\s*|[-*+]\s+)", "");
        notes = Regex.Replace(notes, @"<[^>]+>|[`*_~]", "").Trim();
        return notes[..Math.Min(2000, notes.Length)];
    }
    static bool Expected(Exception e) => e is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException;
    // TODO(design): 请确认网络、限流、磁盘和启动失败的短句；校验失败沿用任务说明。
    static string Message(Exception e) => e switch
    {
        InvalidDataException => "下载的文件不完整，已删除，稍后再试",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "GitHub 暂时限制了检查次数，过一会儿再试",
        HttpRequestException => "连不上 GitHub，检查网络后再试",
        UnauthorizedAccessException or IOException => "更新包存不下来，看看磁盘空间够不够",
        _ => "看不懂 GitHub 返回的更新信息，稍后再试"
    };
    public void InstallAndRestart()
    {
        if (disposed || State != UpdateState.Ready || readyPath == null) return;
        try { install(readyPath); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { Set(UpdateState.Failed, "安装程序没能启动，稍后再试"); }
    }
    public void Dispose() { if (disposed) return; disposed = true; timer.Dispose(); lifetime.Cancel(); http.Dispose(); }
}
