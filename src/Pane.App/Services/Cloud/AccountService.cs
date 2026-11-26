using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pane.Contracts;
using Pane.Services.Ai;

namespace Pane.Services.Cloud;

[JsonSerializable(typeof(JsonObject))]
internal partial class CloudJson : JsonSerializerContext { }

internal sealed partial class AccountService : IAccountService, IAsyncDisposable
{
    readonly SecretStore secrets;
    readonly string devicePath;
    readonly object deviceLock = new();
    readonly HttpClient http;
    readonly string cache;
    readonly Func<string?[]> hardware;
    readonly CancellationTokenSource lifetime = new();
    readonly CancellationToken stopped;
    readonly SemaphoreSlim serial = new(1);
    readonly HashSet<string> paid = [];
    JsonObject session;
    const string SessionFile = "session.dpapi";
    public string AccountCenterUrl { get; }
    public string? LastEmail => (string?)session["email"];
    public bool HasLocalKey => session["key"] != null;
    public AccountState State => session["token"] != null ? AccountState.SignedIn :
        HasLocalKey ? AccountState.NeedsSignIn : AccountState.SignedOut;
    public CloudProfile? Profile { get; private set; }
    public event EventHandler? Changed;

    internal AccountService(string data, string version, HttpMessageHandler? handler = null, Func<string?[]>? hardware = null, string? machine = null)
    {
        stopped = lifetime.Token;
        string folder = Path.Combine(data, "cloud");
        secrets = new(folder);
        string hash = Pane.Settings.WindowsMachine.Key(machine);
        devicePath = Path.Combine(folder, "device-" + hash + ".dpapi");
        cache = Path.Combine(data, "file-icons", "cloud");
        this.hardware = hardware ?? TrialHardware.Read;
        var address = new Uri((Environment.GetEnvironmentVariable("PANE_CLOUD_BASE") ??
            "https://pane.lubeiluchen.com/api/v1").TrimEnd('/') + "/");
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = address };
        AccountCenterUrl = new Uri(address, "/account/#profile").AbsoluteUri;
        http.DefaultRequestHeaders.Add("X-Pane-Client", "pane/" + version);
        session = secrets.Read(SessionFile) is {} json ? JsonNode.Parse(json)!.AsObject() : new();
        if (session["me"] is {} me) Profile = ReadProfile(me, CachedPath(me));
    }

    void Save(JsonObject next)
    {
        var profile = next["me"] is {} me ? ReadProfile(me, CachedPath(me)) : null;
        secrets.Write(SessionFile, JsonSerializer.Serialize(next, CloudJson.Default.JsonObject));
        Volatile.Write(ref session,next);
        Profile = profile;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    JsonObject Device()
    {
        lock (deviceLock)
        {
            string file = Path.GetFileName(devicePath);
            var id = secrets.Read(file);
            if (id == null)
            {
                // 首次升级时将旧设备记录归到本机，其他电脑继续保留各自的记录。
                string legacy = Path.Combine(Path.GetDirectoryName(devicePath)!, "device.dpapi");
                if (!File.Exists(devicePath) && secrets.Read("device.dpapi") is {} previous)
                {
                    File.Move(legacy, devicePath);
                    id = previous;
                }
                else
                {
                    id = CloudKeys.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                    secrets.Write(file, id);
                }
            }
            return new() { ["id"] = id, ["name"] = Environment.MachineName };
        }
    }
    internal (string? Token,string? Key,string? Owner) SyncIdentity()
    {
        var current = Volatile.Read(ref session);
        return ((string?)current["token"],(string?)current["key"],(string?)current["id"]);
    }
    internal string? SyncToken => (string?)session["token"];
    internal byte[] DataKey => CloudKeys.Decode((string)session["key"]!);
    internal string DeviceId => (string)Device()["id"]!;
    internal Task<JsonObject> SyncRequest(string path, JsonObject? body, string token, CancellationToken ct) =>
        Send(body == null ? HttpMethod.Get : HttpMethod.Post, path, ct, body, false, token);
    internal Task ExpireAsync(string token, CancellationToken ct) => Action(ct, t =>
    {
        if (token == SyncToken) throw new Failure("unauthorized");
        return Task.CompletedTask;
    });
    internal sealed class Failure(string code, JsonNode? detail = null, int status = 0) : Exception
    {
        internal readonly int Status = status;
        internal readonly string Code = code;
        internal readonly JsonNode? Detail = detail;
    }
    async Task<CloudResult<T>> Run<T>(CancellationToken caller, Func<CancellationToken,Task<T?>> work)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, stopped);
        var ct = linked.Token;
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 派生密码和 DPAPI 会阻塞，统一从后台执行。
            return new(await Task.Run(() => work(ct), ct).ConfigureAwait(false));
        }
        catch (Failure f)
        {
            if (f.Code == "unauthorized" && session["token"] != null)
                Save(new() { ["email"] = LastEmail, ["id"] = (string?)session["id"], ["key"] = (string?)session["key"] });
            return new(default, f.Code, (int?)f.Detail?["retry_after"], (int?)f.Detail?["attempts_left"]);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return new(default, "network"); }
        finally { serial.Release(); }
    }
    async Task<CloudResult> Action(CancellationToken ct, Func<CancellationToken,Task> work) =>
        await Run<object>(ct, async t => { await work(t); return null; });
    HttpRequestMessage Request(HttpMethod method, string path, bool authenticated, string? bearer = null)
    {
        if (authenticated) bearer ??= (string?)session["token"] ?? throw new Failure("unauthorized");
        var req = new HttpRequestMessage(method, path);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }
    async Task<JsonObject> Send(HttpMethod method, string path, CancellationToken ct, JsonObject? body = null, bool auth = true, string? bearer = null)
    {
        using var req = Request(method, path, auth, bearer);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, CloudJson.Default.JsonObject), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode) throw await Error(response, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return new();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
    }
    static async Task<Failure> Error(HttpResponseMessage response, CancellationToken ct)
    {
        JsonNode? detail = null;
        try { detail = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); } catch (JsonException) { }
        return new((string?)detail?["error"] ?? "network", detail, (int)response.StatusCode);
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await serial.WaitAsync().ConfigureAwait(false);
        try { http.Dispose(); lifetime.Dispose(); pending = null; } finally { serial.Release(); }
    }
}
