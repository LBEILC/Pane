using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Contracts;

namespace Pane.Services.Cloud;

internal sealed partial class AccountService
{
    static DateTimeOffset Time(JsonNode node) => DateTimeOffset.FromUnixTimeSeconds((long)node);
    static DateTimeOffset? MaybeTime(JsonNode? node) => node == null ? null : Time(node);
    static T EnumValue<T>(JsonNode node) where T : struct, Enum => Enum.Parse<T>((string)node!, true);
    static CloudSubscription Subscription(JsonNode s) => new(EnumValue<SubscriptionStatus>(s["status"]!),
        s["tier"] is {} tier ? EnumValue<SubscriptionTier>(tier) : null, MaybeTime(s["expires_at"]), (bool)s["lifetime"]!,
        s["quota"] is {} q ? new((long)q["limit_bytes"]!, (long)q["used_bytes"]!, Time(q["resets_at"]!)) : null,
        s["storage"] is {} store ? new((long)store["limit_bytes"]!, (long)store["used_bytes"]!) : null,
        (bool)s["trial_available"]!, MaybeTime(s["retain_until"]));
    static CloudProfile ReadProfile(JsonNode me, string? avatar) =>
        new((string)me["id"]!, (string)me["email"]!, (string)me["nickname"]!, avatar,
            me["badges"]?.AsArray().Select(b => new CloudBadge((string)b!["id"]!, (int?)b["number"], Time(b["granted_at"]!), (bool?)b["public"] ?? false, (bool?)b["hidden"] ?? false)).ToArray() ?? [],
            Time(me["created_at"]!), Subscription(me["subscription"]!), (int)me["devices"]!["count"]!, (int)me["devices"]!["limit"]!,
            MaybeTime(me["recovery"]?["created_at"]));
    string AvatarFile(JsonNode me) => Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((string)me["id"]!))) + ".jpg");
    string? CachedPath(JsonNode me) => me["avatar"] != null && File.Exists(AvatarFile(me)) ? AvatarFile(me) : null;
    async Task UpdateAvatar(JsonNode me, CancellationToken ct, bool login)
    {
        if (me["avatar"] == null) return;
        string path = AvatarFile(me), meta = path + ".etag";
        bool cached = File.Exists(path) && File.Exists(meta);
        if (cached && !login && DateTime.UtcNow - File.GetLastWriteTimeUtc(meta) < TimeSpan.FromDays(1)) return;
        using var req = Request(HttpMethod.Get, "me/avatar", true);
        if (cached) req.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(meta, ct));
        using var response = await http.SendAsync(req, ct);
        if (cached && response.StatusCode == HttpStatusCode.NotModified) { File.SetLastWriteTimeUtc(meta, DateTime.UtcNow); return; }
        if (!response.IsSuccessStatusCode) throw await Error(response, ct);
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(path + ".tmp", await response.Content.ReadAsByteArrayAsync(ct), ct);
        File.Delete(meta);
        File.Move(path + ".tmp", path, true);
        await File.WriteAllTextAsync(meta, response.Headers.ETag?.ToString() ?? (string)me["avatar"]!["etag"]!, ct);
        Profile = ReadProfile(me, path);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    async Task Refresh(CancellationToken ct)
    {
        var me = await Send(HttpMethod.Get, "me", ct);
        var next = session.DeepClone().AsObject();
        next["me"] = me;
        next["email"] = (string)me["email"]!;
        Save(next);
        await UpdateAvatar(me, ct, false);
    }
    public Task<CloudResult> RefreshAsync(CancellationToken ct) => Action(ct, Refresh);
    public Task<CloudResult> SetFounderPublicAsync(bool isPublic, CancellationToken ct) => Action(ct, async t =>
    {
        var me = await Send(HttpMethod.Patch, "me", t, new() { ["founder_public"] = isPublic });
        var next = session.DeepClone().AsObject();
        next["me"] = me;
        Save(next);
    });
    public Task<CloudResult<CloudMemberGroup>> GetMemberGroupAsync(CancellationToken ct) => Run<CloudMemberGroup>(ct, async t =>
    {
        var group = await Send(HttpMethod.Get, "member/group", t);
        return new((string)group["number"]!, (string?)group["url"], (string)group["code"]!);
    });
    static IReadOnlyList<CloudDevice> ReadDevices(JsonNode devices) => devices.AsArray().Select(d =>
        new CloudDevice((string)d!["id"]!, (string)d["name"]!, (bool)d["current"]!, Time(d["first_seen_at"]!), Time(d["last_seen_at"]!))).ToArray();
    static string DevicePath(string id) => "devices/" + Uri.EscapeDataString(id);
    public Task<CloudResult<IReadOnlyList<CloudDevice>>> GetDevicesAsync(CancellationToken ct) =>
        Run<IReadOnlyList<CloudDevice>>(ct, async t => ReadDevices((await Send(HttpMethod.Get, "devices", t))["devices"]!));
    public Task<CloudResult> RenameDeviceAsync(string deviceId, string name, CancellationToken ct) =>
        Action(ct, t => Send(HttpMethod.Patch, DevicePath(deviceId), t, new() { ["name"] = name }));
    public Task<CloudResult> RemoveDeviceAsync(string deviceId, CancellationToken ct) => Action(ct, async t =>
    {
        await Send(HttpMethod.Delete, DevicePath(deviceId), t);
        if (deviceId == secrets.Read("device.dpapi")) Forget();
        else await Refresh(t);
    });
    public Task<CloudResult<CloudPlans>> GetPlansAsync(CancellationToken ct) => Run<CloudPlans>(ct, async t =>
    {
        var r = await Send(HttpMethod.Get, "plans", t);
        return new(r["plans"]!.AsArray().Select(p => new CloudPlan((string)p!["id"]!, EnumValue<SubscriptionTier>(p["tier"]!),
            (string)p["price"]!, (int)p["days"]!, (long)p["quota_bytes"]!)).ToArray(), (int)r["trial"]!["days"]!,
            (long)r["trial"]!["quota_bytes"]!, (int)r["founder"]!["total"]!, (int)r["founder"]!["remaining"]!);
    });
    public Task<CloudResult<CloudOrder>> CreateOrderAsync(string planId, CancellationToken ct) => Run<CloudOrder>(ct, async t =>
    {
        var r = await Send(HttpMethod.Post, "orders", t, new() { ["plan_id"] = planId });
        return new((string)r["order_no"]!, planId, CloudOrderStatus.Pending, (string)r["amount"]!, DateTimeOffset.UtcNow,
            null, (string?)r["pay_url"], MaybeTime(r["expires_at"]));
    });
    public Task<CloudResult<CloudOrder>> GetOrderAsync(string orderNo, CancellationToken ct) => Run<CloudOrder>(ct, async t =>
    {
        var r = await Send(HttpMethod.Get, "orders/" + Uri.EscapeDataString(orderNo), t);
        var status = EnumValue<CloudOrderStatus>(r["status"]!);
        if (status == CloudOrderStatus.Paid && !paid.Contains(orderNo)) { await Refresh(t); paid.Add(orderNo); }
        return new((string)r["order_no"]!, (string)r["plan_id"]!, status, (string)r["amount"]!, Time(r["created_at"]!),
            MaybeTime(r["paid_at"]), null, null);
    });
    async Task Subscribe(string path, JsonObject body, CancellationToken ct)
    {
        var subscription = await Send(HttpMethod.Post, path, ct, body);
        var next = session.DeepClone().AsObject();
        next["me"]!["subscription"] = subscription;
        Save(next);
    }
    public Task<CloudResult> RedeemAsync(string code, CancellationToken ct) =>
        Action(ct, t => Subscribe("redeem", new() { ["code"] = code }, t));
    public Task<CloudResult> ClaimTrialAsync(CancellationToken ct) => Action(ct, t =>
    {
        if (State != AccountState.SignedIn) throw new Failure("unauthorized");
        var hashes = hardware();
        return Subscribe("trial", new() { ["hw"] = new JsonArray(hashes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) }, t);
    });
}
