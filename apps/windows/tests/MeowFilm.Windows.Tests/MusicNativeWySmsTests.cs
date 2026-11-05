using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeWySmsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("explicit-send-code-identity-CAS-DPAPI-reopen-logout", Flow);
        yield return Case("existing-code-login-without-send-is-explicit", ExistingCode);
        yield return Case("send-keeps-old-saved-session-never-inherits-cookie", ExistingSession);
        foreach (string fault in new[] { "login-id", "status-id", "status-account", "login-no-profile", "status-no-profile",
            "login-no-account", "login-secret", "status-secret", "status-phone", "status-code", "cookie-missing",
            "cookie-domain", "cookie-expired", "cookie-duplicate", "login-duplicate", "login-malformed", "login-string-code",
            "login-large", "login-redirect", "status-redirect", "login-ssa", "status-forbidden", "login-http200-error" })
        { var f = fault; yield return Case("reject-" + f, () => Reject(f)); }
        foreach (int code in new[] { 405, 429, 509, 250, 460, 461, 400, 502, 503, 301, 403, 999 })
        { int c = code; yield return Case("fixed-error-" + c, () => BusinessError(c)); }
        yield return Case("send-cooldown-cancel-platform-and-clock-boundary", Cooldown);
        yield return Case("ledger-expiry-cannot-shorten-latest-send-cooldown", CooldownExpiry);
        yield return Case("independent-numbers-do-not-share-a-send-quota", GlobalRate);
        yield return Case("login-attempts-use-upstream-results-without-local-count-quota", AttemptRate);
        yield return Case("completed-logins-do-not-accumulate-a-phone-ledger", Ledger);
        yield return Case("cancel-send-keeps-reservation-no-automatic-resend", () => Cancel("send"));
        yield return Case("cancel-login-no-late-session", () => Cancel("login"));
        yield return Case("cancel-identity-no-late-session", () => Cancel("status"));
        yield return Case("new-operation-rejects-old-result-and-handoff", Supersede);
        yield return Case("deadline-send-is-unconfirmed-not-recalled", Deadline);
        yield return Case("one-deadline-covers-login-and-identity", LoginDeadline);
        yield return Case("held-physical-http-does-not-block-another-send", Budget);
        yield return Case("lost-durable-receipt-explicit-read-no-replay", Unknown);
        yield return Case("store-CAS-conflict-is-not-unknown-or-success", Conflict);
        yield return Case("other-owner-logout-fences-old-login-CAS", OtherOwner);
        yield return Case("cancel-admitted-CAS-retains-physical-gate-before-read", CancelWrite);
        yield return Case("storage-failure-prevents-SMS-side-effect", ReadFailure);
        yield return Case("input-platform-field-duplicate-and-number-boundaries", Inputs);
        yield return Case("redacted-input-exceptions-receipts-borrowed-resources", Privacy);
        yield return Case("ordinary-content-origins-unchanged-no-cross-platform", Origins);
        yield return Case("QR-and-SMS-share-generation-not-challenges", QrFence);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.native.accounts.wy.sms." + name, run);
    internal static void Check(bool condition) { if (!condition) throw new InvalidOperationException("WY SMS assertion"); }
    internal static bool Ok(JsonElement result) => result.GetProperty("ok").GetBoolean();
    internal static string Code(JsonElement result) => result.GetProperty("code").GetString()!;
    internal static Task<JsonElement> Send(MusicAccountOwner owner, string phone = WySmsHandler.Phone) =>
        owner.CallAsync("music.accounts.sms.send", new { platform = "wy", phone });
    internal static Task<JsonElement> Login(MusicAccountOwner owner) => owner.CallAsync("music.accounts.sms.login",
        new { platform = "wy", phone = WySmsHandler.Phone, countryCode = "+86", captcha = WySmsHandler.Captcha });
    internal static string Root()
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Synthetic root required");
        string path = Path.Combine(root, "wy-sms-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    internal static async Task Until(Func<bool> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!check()) await Task.Delay(15, deadline.Token);
    }
    private static async Task Flow()
    {
        string root = Root();
        await using (var f = new WySmsFixture(root))
        {
            int events = 0; f.Owner.SessionChanged += p => { Check(p == "wy"); events++; };
            Check(f.Transport.Calls == 0);
            var send = await Send(f.Owner);
            Check(Ok(send) && send.GetProperty("sent").GetBoolean() && send.GetProperty("retryAfter").GetInt32() == 60 &&
                f.Store.Writes == 0 && f.Transport.Steps.SequenceEqual(["send"]));
            var login = await Login(f.Owner);
            Check(Ok(login) && login.GetProperty("account").GetProperty("state").GetString() == "verified" &&
                f.Store.Writes == 1 && f.Transport.Steps.SequenceEqual(["send", "login", "status", "rights"]) && events >= 3);
            Check(!login.GetRawText().Contains(WySmsHandler.Phone) && !login.GetRawText().Contains(WySmsHandler.Captcha) &&
                !login.GetRawText().Contains("SYNTHETIC_WY_SESSION"));
            var lease = await f.Owner.AcquireAsync("wy");
            Check(lease is not null && f.Owner.IsCurrent(lease) && lease.UserId == "42");
            var details = await f.Owner.ReadDetailsAsync("wy");
            Check(details.Membership.Vip == true && details.Generation == lease!.Generation &&
                details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
            Check(f.Owner.Revoke(lease!) && !f.Owner.IsCurrent(lease!) && await f.Owner.AcquireAsync("wy") is null);
            var rootDoc = await WySmsStore.ReadRoot(f.Store.Inner);
            string text = rootDoc.ToJsonString();
            Check(!text.Contains(WySmsHandler.Phone) && !text.Contains(WySmsHandler.Captcha) && !text.Contains("retryAfter"));
            var row = rootDoc["entries"]!.AsArray().Single(r => r!["key"]!.GetValue<string>() == AccountDocument.Key("wy"))!["value"]!.AsObject();
            Check(row.Count == 6 && rootDoc["entries"]!.AsArray().Count == 2 && rootDoc["version"]!.GetValue<int>() == 1);
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("SYNTHETIC_WY_SESSION"));
        await using var reopen = new WySmsFixture(root);
        Check((await reopen.Owner.AcquireAsync("wy"))?.UserId == "42" && reopen.Transport.Calls == 0);
        Check((await reopen.Owner.ReadDetailsAsync("wy")).Membership.Vip == true && reopen.Transport.Calls == 0);
        Check(Ok(await reopen.Owner.CallAsync("music.accounts.validate", new { platform = "wy" })));
        var old = await reopen.Owner.AcquireAsync("wy");
        Check(Ok(await reopen.Owner.CallAsync("music.accounts.logout", new { platform = "wy" })) &&
            !reopen.Owner.IsCurrent(old!) && await reopen.Owner.AcquireAsync("wy") is null && reopen.Transport.Calls == 2);
    }
    private static async Task ExistingCode()
    {
        await using var f = new WySmsFixture();
        Check(Ok(await Login(f.Owner)) && f.Transport.Steps.SequenceEqual(["login", "status", "rights"]));
    }
    private static async Task ExistingSession()
    {
        await using var f = new WySmsFixture(); Check(Ok(await Login(f.Owner)));
        var lease = await f.Owner.AcquireAsync("wy");
        Check(Ok(await Send(f.Owner)) && f.Store.Writes == 1 && !f.Owner.IsCurrent(lease!));
        Check((await f.Owner.AcquireAsync("wy"))?.Generation == lease!.Generation);
    }
    private static async Task Reject(string fault)
    {
        await using var f = new WySmsFixture(); f.Transport.Fault = fault;
        var result = await Login(f.Owner); Check(!Ok(result));
        string expected = fault switch
        {
            "login-id" or "status-id" or "status-account" => "IDENTITY_MISMATCH",
            "login-no-profile" or "status-no-profile" or "cookie-expired" => "AUTH_EXPIRED",
            "login-large" => "LIMIT",
            "login-redirect" or "status-redirect" => "REDIRECT_DENIED",
            "login-ssa" => "VERIFICATION_REQUIRED", "status-forbidden" => "FORBIDDEN",
            "login-http200-error" => "UPSTREAM_ERROR", _ => "INVALID_RESPONSE"
        };
        Check(Code(result) == expected && f.Store.Writes == 0 && await f.Owner.AcquireAsync("wy") is null);
        Check(!result.GetRawText().Contains("SYNTHETIC") && !result.GetRawText().Contains(WySmsHandler.Phone));
    }
    private static async Task BusinessError(int code)
    {
        await using var f = new WySmsFixture(); f.Transport.BusinessCode = code;
        var result = await Send(f.Owner); Check(!Ok(result));
        string expected = code switch { 405 or 429 or 509 => "RATE_LIMITED", 250 or 460 or 461 => "VERIFICATION_REQUIRED",
            400 or 502 or 503 => "INVALID_CODE", 301 => "AUTH_EXPIRED", 403 => "FORBIDDEN", _ => "UPSTREAM_ERROR" };
        Check(Code(result) == expected && f.Transport.Calls == 1 && f.Store.Writes == 0);
        if (expected == "RATE_LIMITED") Check(result.GetProperty("retryAfter").GetInt32() == 60);
    }
    private static async Task Cooldown()
    {
        await using var f = new WySmsFixture();
        Check(Ok(await Send(f.Owner)));
        await f.Owner.CallAsync("music.accounts.cancel", new { platform = "wy" });
        await f.Owner.CallAsync("music.accounts.cancel", new { platform = "tx" });
        var blocked = await Send(f.Owner);
        Check(Code(blocked) == "RATE_LIMITED" && blocked.GetProperty("retryAfter").GetInt32() == 60 && f.Transport.Calls == 1);
        f.Clock.Advance(59); Check((await Send(f.Owner)).GetProperty("retryAfter").GetInt32() == 1);
        f.Clock.Advance(1); Check(Ok(await Send(f.Owner)) && f.Transport.Calls == 2);
    }
    private static async Task GlobalRate()
    {
        await using var f = new WySmsFixture();
        for (int i = 0; i < 40; i++) Check(Ok(await Send(f.Owner, (10000000000L + i).ToString())));
        Check(Ok(await Send(f.Owner, "10000000099")) && f.Transport.Calls == 41);
    }
    private static async Task CooldownExpiry()
    {
        await using var f = new WySmsFixture();
        Check(Ok(await Send(f.Owner))); f.Clock.Advance(590);
        Check(Ok(await Send(f.Owner))); f.Clock.Advance(10);
        var blocked = await Send(f.Owner);
        Check(Code(blocked) == "RATE_LIMITED" && blocked.GetProperty("retryAfter").GetInt32() == 50 && f.Transport.Calls == 2);
        f.Clock.Advance(50); Check(Ok(await Send(f.Owner)) && f.Transport.Calls == 3);
    }
    private static async Task AttemptRate()
    {
        await using var f = new WySmsFixture(); f.Transport.BusinessCode = 502;
        for (int i = 0; i < 16; i++) Check(Code(await Login(f.Owner)) == "INVALID_CODE");
        Check(f.Transport.Calls == 16);
        f.Transport.BusinessCode = 200; Check(Ok(await Login(f.Owner)));
    }
    private static async Task Ledger()
    {
        await using var f = new WySmsFixture(); f.Transport.BusinessCode = 502;
        for (int i = 0; i < 128; i++)
            Check(Code(await f.Owner.CallAsync("music.accounts.sms.login", new { platform = "wy",
                phone = (10000000000L + i).ToString(), captcha = WySmsHandler.Captcha })) == "INVALID_CODE");
        Check(f.Transport.Calls == 128);
        f.Transport.BusinessCode = 200; Check(Ok(await Send(f.Owner, "10000000999")) && f.Transport.Calls == 129);
    }
    private static async Task Cancel(string step)
    {
        await using var f = new WySmsFixture(); f.Transport.HoldAt = step;
        var pending = step == "send" ? Send(f.Owner) : Login(f.Owner);
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(Ok(await f.Owner.CallAsync("music.accounts.cancel", new { platform = "wy" })));
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(4));
        Check(!Ok(reply));
        string code = Code(reply);
        // Owner cancellation revokes the token before fencing the protocol's
        // generation. Either observation rejects publication; neither is success.
        if (!(step == "send" ? code == "SMS_SEND_UNCONFIRMED" : code is "CANCELLED" or "SUPERSEDED"))
            throw new InvalidOperationException("Unexpected WY SMS cancellation code: " + code);
        Check(!f.Transport.Settled.Task.IsCompleted);
        Check(f.Store.Writes == 0 && await f.Owner.AcquireAsync("wy") is null);
        f.Transport.Release.TrySetResult();
        await f.Transport.Settled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(f.Store.Writes == 0 && await f.Owner.AcquireAsync("wy") is null);
        Check(f.Transport.Calls == (step == "status" ? 2 : 1));
        if (step == "send") Check(Code(await Send(f.Owner)) == "RATE_LIMITED" && f.Transport.Calls == 1);
    }
    private static async Task Supersede()
    {
        await using var f = new WySmsFixture(); f.Transport.HoldAt = "status";
        var old = Login(f.Owner); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        // A different phone is a new explicit operation. The old phone's busy
        // reservation may legitimately persist until its cancelled work settles.
        Check(Ok(await Send(f.Owner, "10000000001")));
        f.Transport.Release.TrySetResult();
        Check(!Ok(await old) && f.Store.Writes == 0);
    }
    private static async Task Deadline()
    {
        await using var f = new WySmsFixture(timeout: TimeSpan.FromMilliseconds(150)); f.Transport.HoldAt = "send";
        var result = await Send(f.Owner).WaitAsync(TimeSpan.FromSeconds(4));
        Check(Code(result) == "SMS_SEND_UNCONFIRMED" && f.Store.Writes == 0);
        f.Transport.Release.TrySetResult(); await f.Transport.Settled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(Code(await Send(f.Owner)) == "RATE_LIMITED" && f.Transport.Calls == 1);
    }
    private static async Task LoginDeadline()
    {
        await using var f = new WySmsFixture(); f.Transport.BeforeStatus = () => { f.Clock.Advance(26); return Task.CompletedTask; };
        var result = await Login(f.Owner);
        Check(Code(result) == "TIMEOUT" && f.Store.Writes == 0 && f.Transport.Calls == 2);
    }
    private static async Task Budget()
    {
        await using var f = new WySmsFixture();
        f.Transport.HoldEverySend = true;
        var one = Send(f.Owner, "10000000001"); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var two = Send(f.Owner, "10000000002"); await Until(() => f.Transport.Calls == 2);
        var three = Send(f.Owner, "10000000003"); await Until(() => f.Transport.Calls == 3);
        f.Transport.Release.TrySetResult();
        Check(!Ok(await one) && !Ok(await two)); _ = await three; Check(f.Store.Writes == 0);
    }
    private static async Task Unknown()
    {
        await using var f = new WySmsFixture(); f.Store.LoseReceipt = true;
        Check(Code(await Login(f.Owner)) == "HANDOFF_UNCONFIRMED" && f.Store.Writes == 1);
        Check(await f.Owner.AcquireAsync("wy") is null);
        Check(Code(await Send(f.Owner)) == "COMMIT_UNKNOWN" && f.Transport.Calls == 3);
        Check(Ok(await f.Owner.CallAsync("music.accounts.list")) && f.Transport.Calls == 3);
        Check((await f.Owner.AcquireAsync("wy"))?.UserId == "42");
    }
    private static async Task Conflict()
    {
        await using var f = new WySmsFixture(); f.Store.Conflict = true;
        Check(Code(await Login(f.Owner)) == "STORAGE_CONFLICT" && await f.Owner.AcquireAsync("wy") is null);
        Check((await WySmsStore.ReadRoot(f.Store.Inner))["entries"]!.AsArray().Count == 1);
    }
    private static async Task OtherOwner()
    {
        await using var f = new WySmsFixture();
        await using var secondHttp = new AsyncHttp(MusicPlatformHttp.CreateWithHandler(new WySmsHandler(f.Store.Inner)));
        await using var clientContext1 = new MusicClientContextFixture(secondHttp.Value, f.Store.Inner);
        await using var other = clientContext1.CreateAccountOwner(f.Store);
        f.Transport.BeforeStatus = async () => Check(Ok(await other.CallAsync("music.accounts.logout", new { platform = "wy" })));
        Check(Code(await Login(f.Owner)) == "STORAGE_CONFLICT" && await f.Owner.AcquireAsync("wy") is null);
    }
    private static async Task CancelWrite()
    {
        await using var f = new WySmsFixture(); f.Store.HoldWrite = true;
        var login = Login(f.Owner); await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await f.Owner.CallAsync("music.accounts.cancel", new { platform = "wy" });
        Check(Code(await login) == "HANDOFF_UNCONFIRMED");
        var read = f.Owner.CallAsync("music.accounts.list"); await Task.Delay(50); Check(!read.IsCompleted);
        f.Store.Release.TrySetResult(); Check(Ok(await read) && f.Store.Writes == 1 && f.Transport.Calls == 3);
        Check((await f.Owner.AcquireAsync("wy"))?.UserId == "42");
    }
    private static async Task ReadFailure()
    {
        await using var f = new WySmsFixture(); f.Store.FailRead = true;
        Check(Code(await Send(f.Owner)) == "STORAGE_UNAVAILABLE" && f.Transport.Calls == 0);
    }
    private static async Task Inputs()
    {
        await using var f = new WySmsFixture();
        foreach (var input in new object[] {
            new { platform = "kg", phone = WySmsHandler.Phone }, new { platform = "tx", phone = WySmsHandler.Phone },
            new { platform = "wy", phone = "12345" }, new { platform = "wy", phone = "１0000000000" },
            new { platform = "wy", phone = WySmsHandler.Phone, countryCode = "++86" },
            new { platform = "wy", phone = WySmsHandler.Phone, countryCode = "086" },
            new { platform = "wy", phone = WySmsHandler.Phone, url = "https://attacker.invalid" },
            new { platform = "wy", phone = 10000000000L }, new { platform = "wy", phone = WySmsHandler.Phone, captcha = "1234" }
        }) Check(!Ok(await f.Owner.CallAsync("music.accounts.sms.send", input)));
        using var duplicate = JsonDocument.Parse("{\"platform\":\"wy\",\"phone\":\"10000000000\",\"phone\":\"10000000001\"}");
        Check(Code(await f.Owner.ExecuteAsync("music.accounts.sms.send", duplicate.RootElement)) == "INVALID_REQUEST");
        Check(Code(await f.Owner.CallAsync("music.accounts.sms.login", new { platform = "wy", phone = WySmsHandler.Phone, captcha = "123" })) == "INVALID_REQUEST");
        Check(f.Transport.Calls == 0);
        Check(Ok(await f.Owner.CallAsync("music.accounts.sms.send", new { platform = "wy", phone = " 5550100 ", countryCode = "1" })));
    }
    private static async Task Privacy()
    {
        await using var f = new WySmsFixture();
        var input = new MusicAccountSmsInput("wy", WySmsHandler.Phone, "86", WySmsHandler.Captcha);
        Check(JsonSerializer.Serialize(input) == "{}" && !input.ToString().Contains(WySmsHandler.Phone));
        Check(new MusicAccountException("secret" + WySmsHandler.Phone, int.MaxValue).ToString() == "MusicAccountException: ACCOUNT_FAILED");
        Check(Ok(await Login(f.Owner))); await f.Owner.DisposeAsync();
        Check(f.Transport.Disposals == 0 && f.Store.Disposals == 0);
        Check((await f.Store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
    }
    private static Task Origins()
    {
        Check(!MusicPlatformHttp.IsAllowedOrigin("kg", new("https://music.163.com/weapi/sms/captcha/sent")));
        Check(!MusicPlatformHttp.IsAllowedOrigin("tx", new("https://music.163.com/weapi/w/login/cellphone")));
        Check(!MusicPlatformHttp.IsAllowedOrigin("wy", new("http://music.163.com/weapi/w/login/cellphone")));
        Check(!MusicPlatformHttp.IsAllowedOrigin("wy", new("https://attacker.invalid/weapi/sms/captcha/sent")));
        return Task.CompletedTask;
    }
    private static async Task QrFence()
    {
        await using var f = new WySmsFixture();
        var qr = await f.Owner.CallAsync("music.accounts.qr.begin", new { platform = "wy" });
        Check(Ok(qr) && Ok(await Send(f.Owner)));
        var result = await f.Owner.CallAsync("music.accounts.qr.poll", new { platform = "wy",
            challengeId = qr.GetProperty("challenge").GetProperty("challengeId").GetString() });
        Check(result.GetProperty("status").GetString() == "expired" && f.Transport.Calls == 2);
    }
    private sealed class AsyncHttp(MusicPlatformHttp value) : IAsyncDisposable
    { internal MusicPlatformHttp Value => value; public ValueTask DisposeAsync() { value.Dispose(); return ValueTask.CompletedTask; } }
}

internal sealed class WySmsClock : TimeProvider
{
    private long ticks;
    internal void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}
internal sealed class WySmsFixture : IAsyncDisposable
{
    internal readonly WySmsStore Store;
    internal readonly WySmsHandler Transport;
    internal readonly WySmsClock Clock = new();
    internal readonly MusicPlatformHttp Http;
    internal readonly MusicAccountProtocol Protocol;
    internal readonly MusicAccountOwner Owner;
    internal readonly MusicClientContextFixture Clients;
    internal WySmsFixture(string? root = null, TimeSpan? timeout = null)
    {
        Store = new(new LocalDocumentStore(root ?? MusicNativeWySmsTests.Root(), ["music.accounts.v1", MusicPlatformClientContextOwner.DocumentNamespace], protectedNamespaces: ["music.accounts.v1", MusicPlatformClientContextOwner.DocumentNamespace]));
        Transport = new(Store.Inner);
        Http = MusicPlatformHttp.CreateWithHandler(Transport);
        Clients = new(Http, Store.Inner, clock: Clock);
        Protocol = Clients.CreateAccountProtocol(timeout: timeout); Owner = Clients.CreateAccountOwner(Store, Protocol);
    }
    public async ValueTask DisposeAsync()
    {
        Transport.Release.TrySetResult(); Store.Release.TrySetResult();
        try { await Owner.DisposeAsync(); }
        finally { await Clients.DisposeAsync(); Http.Dispose(); await Store.DisposeAsync(); }
    }
}
internal sealed class WySmsStore(LocalDocumentStore inner) : IDocumentStore
{
    internal LocalDocumentStore Inner => inner;
    internal int Writes, Disposals;
    internal bool LoseReceipt, Conflict, HoldWrite, FailRead;
    internal TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task<JsonObject> ReadRoot(IDocumentStore store)
    {
        var document = await store.ReadAsync("music.accounts.v1");
        try { return document.Data is null ? new() { ["version"] = 1, ["entries"] = new JsonArray() } : JsonNode.Parse(document.Data)!.AsObject(); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
    }
    public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) =>
        FailRead ? throw new DocumentStoreException(DocumentStoreError.Unavailable) : inner.ReadAsync(ns, token);
    public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
    {
        if (ns == "music.accounts.v1")
        {
            Writes++;
            if (HoldWrite) { Arrived.TrySetResult(); await Release.Task; token = default; }
            if (Conflict)
            {
                Conflict = false;
                var document = await inner.ReadAsync(ns);
                var root = await ReadRoot(inner);
                root["entries"]!.AsArray().Add(new JsonObject {
                    ["key"] = AccountDocument.Key("tx"),
                    ["value"] = new JsonObject { ["version"] = 1, ["generation"] = 1, ["cookie"] = null,
                        ["profile"] = null, ["scope"] = null, ["validatedAt"] = null } });
                byte[] other = JsonSerializer.SerializeToUtf8Bytes(root);
                try { await inner.WriteAsync(ns, other, document.Fingerprint); }
                finally { CryptographicOperations.ZeroMemory(other); if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
            }
        }
        var result = await inner.WriteAsync(ns, data, expected, token);
        if (ns == "music.accounts.v1" && LoseReceipt) throw new DocumentStoreException(DocumentStoreError.Storage);
        return result;
    }
    public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
}
internal sealed class WySmsHandler(IDocumentStore store) : HttpMessageHandler
{
    // Deliberately synthetic, never sent to a network. Every request is intercepted.
    internal const string Phone = "10000000000", Captcha = "864209", Cookie = "MUSIC_U=SYNTHETIC_WY_SESSION";
    internal string Fault = "", HoldAt = "";
    internal int Calls, Disposals, BusinessCode = 200;
    internal bool HoldEverySend;
    internal readonly List<string> Steps = [];
    internal Func<Task>? BeforeStatus;
    internal TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static HttpResponseMessage Raw(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var uri = request.RequestUri!;
        string step = uri.AbsolutePath switch {
            "/weapi/sms/captcha/sent" => "send", "/weapi/w/login/cellphone" => "login",
            "/weapi/w/nuser/account/get" => "status", "/eapi/login/qrcode/unikey" => "qr",
            "/weapi/music-vip-membership/client/vip/info" => "rights", _ => "unknown" };
        lock (Steps) Steps.Add(step);
        MusicNativeWySmsTests.Check(step != "unknown" && uri.Scheme == "https" && uri.IsDefaultPort &&
            uri.Query.Length == 0 && request.Method == HttpMethod.Post);
        if (step == "qr") return Raw("{\"code\":200,\"unikey\":\"SYNTHETIC_WY_QR\"}");
        MusicNativeWySmsTests.Check(uri.Host == "music.163.com");
        var client = MusicClientContextDocument.Decode(await store.ReadAsync(MusicPlatformClientContextOwner.DocumentNamespace)).Wy;
        MusicClientContextFixture.VerifyLocalWyCookie(request, client, step is "status" or "rights" ? Cookie : null);
        string body = await request.Content!.ReadAsStringAsync();
        var fields = body.Split('&').ToDictionary(p => p.Split('=', 2)[0], p => Uri.UnescapeDataString(p.Split('=', 2)[1]));
        MusicNativeWySmsTests.Check(fields.Count == 2 && fields.ContainsKey("params") && fields["encSecKey"].Length == 256 &&
            Convert.FromBase64String(fields["params"]).Length % 16 == 0 && !body.Contains(Phone) && !body.Contains(Captcha));
        if (HoldAt == step || HoldEverySend && step == "send")
        { HoldAt = ""; Arrived.TrySetResult(); await Release.Task; Settled.TrySetResult(); }
        if (step == "status" && BeforeStatus is not null) await BeforeStatus();
        if (Fault == step + "-redirect")
        { var r = Raw("{}" , HttpStatusCode.Found); r.Headers.Location = new("https://attacker.invalid"); return r; }
        if (Fault == step + "-ssa")
        { var r = Raw("{}"); r.Headers.Add("ssa-code", "SYNTHETIC_VERIFICATION"); return r; }
        if (Fault == step + "-forbidden") return Raw("{}", HttpStatusCode.Forbidden);
        if (Fault == step + "-duplicate") return Raw("{\"code\":200,\"code\":200}");
        if (Fault == step + "-malformed") return Raw("invalid SYNTHETIC_SECRET");
        if (Fault == step + "-string-code") return Raw("{\"code\":\"200\"}");
        if (Fault == step + "-large") return Raw(new string(' ', 65537));
        if (step == "send" || BusinessCode != 200) return Raw(JsonSerializer.Serialize(new { code = BusinessCode,
            message = Phone + Captcha + "SYNTHETIC_UPSTREAM_DIAGNOSTIC" }),
            BusinessCode == 429 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        if (step == "rights") return Raw(JsonSerializer.Serialize(new { code = 200, data = new {
            userId = "42", associator = new { expireTime = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds() },
            musicPackage = new { expireTime = 0 }, redplus = new { expireTime = 0 } } }));
        string userId = Fault == "status-id" && step == "status" ? "43" : "42";
        string accountId = Fault == step + "-account" || Fault == step + "-id" && step == "login" ? "99" : userId;
        string name = Fault == step + "-secret" ? "SYNTHETIC_WY_SESSION" :
            Fault == step + "-phone" ? Phone : Fault == step + "-code" ? Captcha : "合成用户";
        var json = new JsonObject { ["code"] = 200,
            ["account"] = new JsonObject { ["id"] = accountId },
            ["profile"] = new JsonObject { ["userId"] = userId, ["nickname"] = name, ["avatarUrl"] = "https://p1.music.126.net/synthetic.jpg" } };
        if (Fault == step + "-no-profile") json["profile"] = null;
        if (Fault == step + "-no-account") json.Remove("account");
        var response = Raw(json.ToJsonString(), Fault == "login-http200-error" && step == "login" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        if (step == "login" && Fault != "cookie-missing")
        {
            string cookie = Cookie + "; Path=/; Secure; HttpOnly; Domain=" + (Fault == "cookie-domain" ? "attacker.invalid" : ".music.163.com");
            if (Fault == "cookie-expired") cookie += "; Max-Age=0";
            response.Headers.TryAddWithoutValidation("Set-Cookie", Fault == "cookie-duplicate" ? new[] { cookie, cookie } : new[] { cookie });
        }
        return response;
    }
    protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
}
