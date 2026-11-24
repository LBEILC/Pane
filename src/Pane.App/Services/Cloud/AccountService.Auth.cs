using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pane.Contracts;
using static Pane.Services.Cloud.CloudKeys;

namespace Pane.Services.Cloud;

internal sealed partial class AccountService
{
    sealed record Login(string Email, string Auth, byte[] Wrap, string? Ticket = null);
    Login? pending;

    static Login Derive(string email, string password, byte[] salt, int iterations)
    {
        if (iterations is < 100000 or > 2000000 || salt.Length != 16) throw new Failure("bad_request");
        var master = Master(password, salt, iterations);
        try { return new(email.Trim().ToLowerInvariant(), Encode(Expand(master, "pane-auth-v1")), Expand(master, "pane-wrap-v1")); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }
    static Login Derive(string email, string password, JsonObject keys) =>
        Derive(email, password, Decode((string)keys["salt"]!), (int)keys["kdf_iterations"]!);

    JsonObject NewKeys(string email, string password, byte[] key, out Login login)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        login = Derive(email, password, salt, Iterations);
        return new() { ["salt"] = Encode(salt), ["kdf_iterations"] = Iterations, ["auth_key"] = login.Auth,
            ["wrapped_key"] = Wrap(login.Wrap, key), ["key_check"] = Check(key) };
    }
    async Task Accept(Login login, JsonObject response, CancellationToken ct)
    {
        var key = Unwrap(login.Wrap, (string)response["wrapped_key"]!);
        var me = response["me"]!;
        var next = new JsonObject { ["token"] = (string)response["token"]!, ["key"] = Encode(key),
            ["email"] = (string)me["email"]!, ["id"] = (string)me["id"]!, ["me"] = me.DeepClone() };
        pending = null;
        paid.Clear();
        Save(next);
        // 会话已经落盘；头像临时不可用时，下次刷新再取。
        try { await UpdateAvatar(me, ct, true); }
        catch (HttpRequestException) { }
        catch (IOException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Failure e) when (e.Code != "unauthorized") { }
    }
    async Task<CloudResult<DeviceLimitHit>> LoginRequest(Login login, string path, JsonObject body, CancellationToken ct)
    {
        try
        {
            var reply = await Send(HttpMethod.Post, path, ct, body, false, login.Ticket);
            await Accept(login, reply, ct);
            return new(null);
        }
        catch (Failure e) when (e.Code == "device_limit")
        {
            pending = login with { Ticket = (string)e.Detail!["pending_token"]! };
            return new(new(ReadDevices(e.Detail["devices"]!)), e.Code);
        }
    }
    async Task<CloudResult<DeviceLimitHit>> Authenticate(CancellationToken ct, Func<CancellationToken,Task<CloudResult<DeviceLimitHit>>> operation)
    {
        var result = await Run<CloudResult<DeviceLimitHit>>(ct, async t => { pending = null; return await operation(t); });
        return result.Value ?? new(null, result.Error, result.RetryAfter, result.AttemptsLeft);
    }
    public async Task<CloudResult> SendCodeAsync(string email, CodePurpose purpose, CancellationToken ct)
    {
        var r = await Run<JsonObject>(ct, async t => await Send(HttpMethod.Post, "auth/code", t,
            new() { ["email"] = email.Trim(), ["purpose"] = purpose == CodePurpose.Register ? "register" : "reset" }, false));
        return new(r.Error, r.RetryAfter ?? (int?)r.Value?["retry_after"], r.AttemptsLeft);
    }
    public Task<CloudResult<DeviceLimitHit>> SignInAsync(string email, string password, CancellationToken ct) =>
        Authenticate(ct, async t =>
        {
            var keys = await Send(HttpMethod.Post, "auth/prelogin", t, new() { ["email"] = email.Trim() }, false);
            var login = Derive(email, password, keys);
            return await LoginRequest(login, "auth/login", LoginBody(login), t);
        });
    JsonObject LoginBody(Login login) => new() { ["email"] = login.Email, ["auth_key"] = login.Auth, ["device"] = Device() };

    public Task<CloudResult<DeviceLimitHit>> RegisterAsync(string email, string code, string password, string? nickname, CloudConsents consents, CancellationToken ct) =>
        Authenticate(ct, async t =>
        {
            var body = NewKeys(email, password, RandomNumberGenerator.GetBytes(32), out var login);
            body["email"] = login.Email;
            body["code"] = code;
            if (nickname != null) body["nickname"] = nickname;
            body["consents"] = new JsonObject { ["terms"] = consents.Terms, ["privacy"] = consents.Privacy, ["cross_border"] = consents.CrossBorder };
            body["device"] = Device();
            return await LoginRequest(login, "auth/register", body, t);
        });

    public Task<CloudResult> RemoveDeviceAndSignInAsync(string deviceId, CancellationToken ct) => Action(ct, async t =>
    {
        var login = pending ?? throw new Failure("unauthorized");
        await Send(HttpMethod.Delete, DevicePath(deviceId), t, bearer: login.Ticket);
        var r = await LoginRequest(login, "auth/login", LoginBody(login), t);
        if (!r.Ok) throw new Failure(r.Error!);
    });

    public Task<CloudResult<DeviceLimitHit>> ResetPasswordAsync(string email, string code, string newPassword, ResetMode mode, string? recoveryCode, CancellationToken ct) =>
        Authenticate(ct, async t =>
        {
            byte[] key;
            switch (mode)
            {
                case ResetMode.LocalKey:
                    if (!HasLocalKey || !string.Equals(email.Trim(), LastEmail, StringComparison.OrdinalIgnoreCase))
                        throw new Failure("key_mismatch");
                    key = Decode((string)session["key"]!);
                    break;
                case ResetMode.RecoveryCode:
                    var bytes = Parse(recoveryCode) ?? throw new Failure("bad_recovery");
                    var recovery = await Send(HttpMethod.Post, "auth/reset/recovery", t, new() { ["email"] = email.Trim(), ["code"] = code }, false);
                    try { key = Unwrap(Expand(bytes, "pane-recovery-v1"), (string)recovery["recovery_wrapped_key"]!, true); }
                    catch (CryptographicException) { throw new Failure("bad_recovery"); }
                    break;
                case ResetMode.Fresh: key = RandomNumberGenerator.GetBytes(32); break;
                default: throw new Failure("bad_request");
            }
            var body = NewKeys(email, newPassword, key, out var login);
            body["email"] = login.Email;
            body["code"] = code;
            body["device"] = Device();
            if (mode == ResetMode.Fresh) body["fresh"] = true;
            return await LoginRequest(login, "auth/reset", body, t);
        });
    public Task<CloudResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct) => Action(ct, async t =>
    {
        var keys = await Send(HttpMethod.Get, "account/keys", t);
        var old = Derive(LastEmail!, currentPassword, keys);
        byte[] key;
        try { key = Unwrap(old.Wrap, (string)keys["wrapped_key"]!); }
        catch (CryptographicException) { throw new Failure("bad_credentials"); }
        var body = NewKeys(LastEmail!, newPassword, key, out _);
        body.Remove("key_check");
        body["old_auth_key"] = old.Auth;
        await Send(HttpMethod.Post, "account/password", t, body);
    });
    public Task<CloudResult<string>> CreateRecoveryCodeAsync(CancellationToken ct) => Run<string>(ct, async t =>
    {
        if (!HasLocalKey) throw new Failure("unauthorized");
        var bytes = RandomNumberGenerator.GetBytes(20);
        var key = Decode((string)session["key"]!);
        await Send(HttpMethod.Put, "account/recovery", t, new() { ["key_check"] = Check(key),
            ["recovery_wrapped_key"] = Wrap(Expand(bytes, "pane-recovery-v1"), key, true) });
        SetRecovery(DateTimeOffset.UtcNow);
        return Display(bytes);
    });
    public Task<CloudResult> DeleteRecoveryCodeAsync(CancellationToken ct) => Action(ct, async t =>
    {
        await Send(HttpMethod.Delete, "account/recovery", t);
        SetRecovery(null);
    });
    void SetRecovery(DateTimeOffset? when)
    {
        var next = session.DeepClone().AsObject();
        next["me"]!["recovery"] = when == null ? null : new JsonObject { ["created_at"] = when.Value.ToUnixTimeSeconds() };
        Save(next);
    }
    void Forget()
    {
        paid.Clear();
        pending = null;
        Save(new() { ["email"] = LastEmail });
    }
    public Task<CloudResult> SignOutAsync(CancellationToken ct) => Action(ct, async t =>
    {
        try { if (session["token"] != null) await Send(HttpMethod.Post, "auth/logout", t); }
        finally { Forget(); }
    });
    public Task<CloudResult> DeleteAccountAsync(string password, CancellationToken ct) => Action(ct, async t =>
    {
        var keys = await Send(HttpMethod.Get, "account/keys", t);
        await Send(HttpMethod.Delete, "me", t, new() { ["auth_key"] = Derive(LastEmail!, password, keys).Auth });
        Forget();
    });
}
