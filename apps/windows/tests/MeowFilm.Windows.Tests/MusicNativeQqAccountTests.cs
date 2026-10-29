using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeQqAccountTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("fixed-request-chain-validated-handoff-transient-no-secret-dto", Flow);
        yield return Case("fixed-host-parent-cookie-scopes-are-not-new-origins", ParentCookies);
        foreach (var pair in new[] { (66, "waiting"), (67, "confirming"), (65, "expired"), (68, "refused") })
        { var p = pair; yield return Case("state-" + p.Item1, () => State(p.Item1, p.Item2)); }
        foreach (var fault in new[] { "image-mime", "image-bad-crc", "image-huge", "image-bomb", "image-trailing",
            "cookie-duplicate", "cookie-domain", "cookie-domain-lookalike", "cookie-expired", "cookie-missing", "callback-script", "callback-state",
            "callback-host", "callback-port", "callback-userinfo", "callback-duplicate", "signature-domain",
            "signature-identity", "signature-location", "authorize-host", "authorize-state", "authorize-duplicate",
            "credential-identity", "credential-type", "credential-inferred-type", "profile-identity", "profile-ticket", "login-auth", "login-device",
            "login-verification", "login-invalid-code", "login-restricted", "login-rate", "image-302", "poll-302", "signature-307", "authorize-303" })
        { var f = fault; yield return Case("reject-" + f, () => Reject(f)); }
        foreach (string host in new[] { "ssl.ptlogin2.qq.com", "ssl.ptlogin2.graph.qq.com", "graph.qq.com" })
        { string h = host; yield return Case("ordinary-http-denies-" + h, () => NormalHttp(h)); }
        yield return Case("ttl-cooldown-local-id-poll-cap-no-replay", Lifetime);
        yield return Case("caller-cancel-after-authorization-no-handoff", Cancel);
        yield return Case("old-poll-cannot-overwrite-new-challenge", Replace);
        yield return Case("single-poll-admission-noncooperative-physical-http-slots", Budget);
        yield return Case("real-dpapi-save-restart-validate-local-logout", Storage);
        yield return Case("lost-cas-receipt-reconcile-no-auth-replay", UnknownSave);
        yield return Case("failed-validation-never-saves-grant", InvalidSave);
        yield return Case("admitted-cancel-unknown-save-drains-before-explicit-read", AdmittedCancel);
        yield return Case("logout-during-authorization-fences-old-session-and-grant", LogoutDuringPoll);
        yield return Case("single-call-deadline-covers-all-authorization-hops", Deadline);
        yield return Case("shared-http-and-store-not-disposed-by-owner", Borrowed);
        yield return Case("kg-without-native-device-owner-remains-rejected", Unsupported);
    }
    private static TestCase Case(string name, Func<Task> action) => new("music.native.accounts.qq." + name, action);
    internal static void Check(bool value) { if (!value) throw new InvalidOperationException("QQ account assertion"); }
    private static async Task Error(Func<Task> action, params string[] codes)
    {
        try { await action(); throw new InvalidOperationException("Expected finite QQ rejection"); }
        catch (MusicAccountException e) { Check(codes.Length == 0 || codes.Contains(e.Code)); Check(!e.ToString().Contains("SYNTHETIC")); }
        catch (OperationCanceledException) { Check(codes.Length == 0 || codes.Contains("CANCELLED")); }
    }
    private static async Task Flow()
    {
        var handler = new QqAccountFixtureHandler();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        var challenge = await protocol.BeginAsync("tx");
        Check(challenge.DisplayKind == "png" && challenge.Url == "" && challenge.ImageBase64 is not null);
        Check(!JsonSerializer.Serialize(challenge).Contains("ImageBase64") && !challenge.ToString().Contains("SYNTHETIC"));
        int handoffs = 0;
        var poll = await protocol.PollAsync("tx", challenge.ChallengeId, (value, _) =>
        {
            handoffs++; Check(value.Profile.UserId == "42" && value.Profile.DisplayName == "合成账号" &&
                value.Cookie.Contains("musickey=SYNTHETIC_MUSICKEY") && !value.Cookie.Contains("QRSIG") &&
                !value.Cookie.Contains("PSKEY") && JsonSerializer.Serialize(value) == "{}" &&
                value.Details.Entitlements.Membership.Vip == true && value.Details.Entitlements.Membership.Tier == "svip");
            return Task.FromResult(true);
        });
        Check(poll.Status == "authenticated" && handoffs == 1 && handler.Calls == 8);
        Check((await protocol.PollAsync("tx", challenge.ChallengeId, (_, _) => Task.FromResult(true))).Status == "expired");
        Check(handler.Calls == 8);
    }
    private static async Task State(int state, string expected)
    {
        var handler = new QqAccountFixtureHandler { Status = state };
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        var qr = await protocol.BeginAsync("tx");
        Check((await protocol.PollAsync("tx", qr.ChallengeId, (_, _) => throw new InvalidOperationException("Unexpected handoff"))).Status == expected);
        Check(handler.Calls == 2);
    }
    private static async Task ParentCookies()
    {
        var handler = new QqAccountFixtureHandler { ParentCookieDomains = true };
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        var qr = await protocol.BeginAsync("tx");
        Check((await protocol.PollAsync("tx", qr.ChallengeId, (_, _) => Task.FromResult(true))).Status == "authenticated");
        Check(handler.Calls == 8);
    }
    private static async Task Reject(string fault)
    {
        var handler = new QqAccountFixtureHandler { Fault = fault };
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        int accepted = 0;
        string expected = fault switch
        {
            "image-huge" or "image-bomb" => "LIMIT",
            "cookie-domain" or "cookie-domain-lookalike" or "signature-domain" => "ORIGIN_DENIED",
            "cookie-expired" or "login-auth" => "AUTH_EXPIRED",
            "signature-identity" or "credential-identity" or "credential-type" or "credential-inferred-type" or "profile-identity" => "IDENTITY_MISMATCH",
            "callback-host" or "callback-port" or "callback-userinfo" or "callback-duplicate" or "signature-location" or
                "authorize-host" or "authorize-state" or "authorize-duplicate" or "image-302" or "poll-302" or
                "signature-307" or "authorize-303" => "REDIRECT_DENIED",
            "login-device" => "DEVICE_LIMIT", "login-verification" or "login-invalid-code" => "VERIFICATION_REQUIRED",
            "login-restricted" => "FORBIDDEN", "login-rate" => "RATE_LIMITED",
            _ => "INVALID_RESPONSE"
        };
        await Error(async () =>
        {
            var qr = await protocol.BeginAsync("tx");
            await protocol.PollAsync("tx", qr.ChallengeId, (_, _) => { accepted++; return Task.FromResult(true); });
        }, expected);
        Check(accepted == 0);
    }
    private static async Task NormalHttp(string host)
    {
        var handler = new QqAccountFixtureHandler();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            var uri = new Uri("https://" + host + "/ptqrshow");
            Check(!MusicPlatformHttp.IsAllowedOrigin(platform, uri));
            try { await http.SendAsync(platform, new(HttpMethod.Get, uri), default); throw new InvalidOperationException(); }
            catch (MusicPlatformException e) { Check(e.Code == "ORIGIN_DENIED"); }
        }
        Check(handler.Calls == 0);
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow; // Owner validates wall-clock display TTL.
    }
    private static async Task Lifetime()
    {
        var handler = new QqAccountFixtureHandler { Status = 66 }; var clock = new Clock();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http, clock: clock);
        var first = await protocol.BeginAsync("tx");
        Check((await protocol.PollAsync("tx", Guid.NewGuid().ToString(), (_, _) => Task.FromResult(true))).Status == "expired");
        clock.Advance(181);
        Check((await protocol.PollAsync("tx", first.ChallengeId, (_, _) => Task.FromResult(true))).Status == "expired" && handler.Calls == 1);
        _ = await protocol.BeginAsync("tx");
        await Error(() => protocol.BeginAsync("tx"), "RATE_LIMITED");
        clock.Advance(3);
        var qr = await protocol.BeginAsync("tx");
        for (int i = 0; i < 100; i++) Check((await protocol.PollAsync("tx", qr.ChallengeId, (_, _) => Task.FromResult(true))).Status == "waiting");
        int calls = handler.Calls;
        await Error(() => protocol.PollAsync("tx", qr.ChallengeId, (_, _) => Task.FromResult(true)), "RATE_LIMITED");
        Check(calls == handler.Calls);
    }
    private static async Task Cancel()
    {
        var handler = new QqAccountFixtureHandler { HoldAt = "QQLogin" };
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        var qr = await protocol.BeginAsync("tx"); int accepted = 0;
        using var cancel = new CancellationTokenSource();
        var task = protocol.PollAsync("tx", qr.ChallengeId, (_, _) => { accepted++; return Task.FromResult(true); }, cancel.Token);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        await Error(() => task, "CANCELLED"); handler.Release.TrySetResult();
        await handler.Settled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(accepted == 0 && (await protocol.PollAsync("tx", qr.ChallengeId, (_, _) => Task.FromResult(true))).Status == "expired");
    }
    private static async Task Replace()
    {
        var handler = new QqAccountFixtureHandler { HoldAt = "ptqrlogin" }; var clock = new Clock();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http, clock: clock);
        var old = await protocol.BeginAsync("tx");
        var pending = protocol.PollAsync("tx", old.ChallengeId, (_, _) => throw new InvalidOperationException());
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(3); var current = await protocol.BeginAsync("tx");
        handler.Release.TrySetResult(); await Error(() => pending, "SUPERSEDED");
        Check((await protocol.PollAsync("tx", current.ChallengeId, (_, _) => Task.FromResult(true))).Status == "authenticated");
    }
    private static async Task Budget()
    {
        var handler = new QqAccountFixtureHandler { HoldAt = "ptqrshow", HoldEveryImage = true };
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        var clock = new Clock();
        await using var protocol = new MusicAccountProtocol(http, timeout: TimeSpan.FromMilliseconds(100), clock: clock);
        await Error(() => protocol.BeginAsync("tx"), "TIMEOUT");
        clock.Advance(3);
        await Error(() => protocol.BeginAsync("tx"), "TIMEOUT");
        clock.Advance(3);
        await Error(() => protocol.BeginAsync("tx"), "RATE_LIMITED");
        Check(handler.Calls == 2); handler.Release.TrySetResult();
    }
    internal static string DataPath()
    {
        string path = TestWorkspace.DataPath("qq-account-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static async Task Storage()
    {
        string path = DataPath();
        using var http = MusicPlatformHttp.CreateWithHandler(new QqAccountFixtureHandler());
        await using var store = new DpapiDocumentStore(path, ["music.accounts.v1"]);
        await using (var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http)))
        {
            var result = await Login(owner); Check(Ok(result) && AccountState(result) == "verified");
            var details = await owner.ReadDetailsAsync("tx");
            Check(details.Generation == 1 && details.Membership.Vip == true && details.Membership.Tier == "svip" &&
                details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
            var session = await owner.AcquireAsync("tx"); Check(session is not null && owner.IsCurrent(session));
            Check(owner.Revoke(session!) && !owner.IsCurrent(session!));
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("SYNTHETIC_MUSICKEY"));
        await using var reopened = new MusicAccountOwner(store, new MusicAccountProtocol(http));
        Check(RowState(await reopened.CallAsync("music.accounts.list")) == "saved");
        Check((await reopened.ReadDetailsAsync("tx")).Membership.Vip == true);
        Check(AccountState(await reopened.CallAsync("music.accounts.validate", new { platform = "tx" })) == "verified");
        Check(AccountState(await reopened.CallAsync("music.accounts.logout", new { platform = "tx" })) == "none");
        Check(await reopened.AcquireAsync("tx") is null);
    }
    private static async Task UnknownSave()
    {
        var handler = new QqAccountFixtureHandler();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var store = new QqAccountStore(new DpapiDocumentStore(DataPath(), ["music.accounts.v1"])) { LoseReceipt = true };
        await using var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http));
        var begin = await owner.CallAsync("music.accounts.qr.begin", new { platform = "tx" });
        string id = begin.GetProperty("challenge").GetProperty("challengeId").GetString()!;
        var result = await owner.CallAsync("music.accounts.qr.poll", new { platform = "tx", challengeId = id });
        Check(!Ok(result) && result.GetProperty("code").GetString() == "HANDOFF_UNCONFIRMED");
        Check(await owner.AcquireAsync("tx") is null);
        int calls = handler.Calls;
        Check((await owner.CallAsync("music.accounts.qr.poll", new { platform = "tx", challengeId = id })).GetProperty("status").GetString() == "expired");
        Check(handler.Calls == calls && store.Writes == 1);
        Check(RowState(await owner.CallAsync("music.accounts.list")) == "saved" && await owner.AcquireAsync("tx") is not null);
    }
    private static async Task InvalidSave()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new QqAccountFixtureHandler { Fault = "profile-identity" });
        await using var store = new QqAccountStore(new DpapiDocumentStore(DataPath(), ["music.accounts.v1"]));
        await using var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http));
        Check(!Ok(await Login(owner)) && store.Writes == 0 && await owner.AcquireAsync("tx") is null);
    }
    private static async Task AdmittedCancel()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new QqAccountFixtureHandler());
        await using var store = new QqAccountStore(new DpapiDocumentStore(DataPath(), ["music.accounts.v1"])) { HoldWrite = true };
        await using var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http));
        var pending = Login(owner);
        try
        {
            await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(Ok(await owner.CallAsync("music.accounts.cancel", new { platform = "tx" })));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!Ok(result) && result.GetProperty("code").GetString() == "HANDOFF_UNCONFIRMED");
            Check(await owner.AcquireAsync("tx") is null);
            var observation = owner.CallAsync("music.accounts.list");
            Check(!observation.IsCompleted);
            store.Release.TrySetResult();
            Check(RowState(await observation) == "saved" && store.Writes == 1);
        }
        finally { store.Release.TrySetResult(); }
    }
    private static async Task LogoutDuringPoll()
    {
        var handler = new QqAccountFixtureHandler(); var clock = new Clock();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var store = new QqAccountStore(new DpapiDocumentStore(DataPath(), ["music.accounts.v1"]));
        await using var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http, clock: clock));
        int changes = 0; owner.SessionChanged += p => { Check(p == "tx"); changes++; };
        Check(Ok(await Login(owner)));
        var previous = await owner.AcquireAsync("tx"); Check(previous is not null);
        clock.Advance(3); handler.HoldAt = "QQLogin";
        var pending = Login(owner);
        try
        {
            await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!owner.IsCurrent(previous!));
            Check(AccountState(await owner.CallAsync("music.accounts.logout", new { platform = "tx" })) == "none");
            handler.Release.TrySetResult();
            Check(!Ok(await pending) && store.Writes == 2 && await owner.AcquireAsync("tx") is null && changes >= 3);
            Check(!owner.Revoke(previous!));
        }
        finally { handler.Release.TrySetResult(); }
    }
    private static async Task Deadline()
    {
        var clock = new Clock();
        var handler = new QqAccountFixtureHandler { OnStep = step => { if (step != "ptqrshow") clock.Advance(9); } };
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http, clock: clock);
        var qr = await protocol.BeginAsync("tx"); int accepted = 0;
        await Error(() => protocol.PollAsync("tx", qr.ChallengeId,
            (_, _) => { accepted++; return Task.FromResult(true); }), "TIMEOUT");
        Check(accepted == 0 && handler.Calls == 4);
    }
    private static async Task Borrowed()
    {
        var handler = new QqAccountFixtureHandler();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var store = new QqAccountStore(new DpapiDocumentStore(DataPath(), ["music.accounts.v1"]));
        var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http));
        Check(Ok(await Login(owner))); await owner.DisposeAsync();
        Check(handler.Disposals == 0 && store.Disposals == 0);
        Check((await store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
    }
    private static async Task Unsupported()
    {
        var handler = new QqAccountFixtureHandler();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        await Error(() => protocol.BeginAsync("kg"), "DEVICE_CONTEXT_REQUIRED");
        Check(handler.Calls == 0);
    }
    internal static bool Ok(JsonElement value) => value.GetProperty("ok").GetBoolean();
    internal static string AccountState(JsonElement value) => value.GetProperty("account").GetProperty("state").GetString()!;
    internal static string RowState(JsonElement value) => value.GetProperty("items").EnumerateArray()
        .Single(p => p.GetProperty("platform").GetString() == "tx").GetProperty("state").GetString()!;
    internal static async Task<JsonElement> Login(MusicAccountOwner owner)
    {
        var qr = await owner.CallAsync("music.accounts.qr.begin", new { platform = "tx" });
        Check(Ok(qr));
        return await owner.CallAsync("music.accounts.qr.poll", new { platform = "tx",
            challengeId = qr.GetProperty("challenge").GetProperty("challengeId").GetString()! });
    }
}

internal sealed class QqAccountStore(DpapiDocumentStore inner) : IProtectedDocumentStore
{
    internal bool LoseReceipt, HoldWrite;
    internal int Writes, Disposals;
    internal TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default) => inner.ReadAsync(ns, token);
    public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
    {
        Writes++;
        if (HoldWrite)
        {
            Arrived.TrySetResult(); await Release.Task;
            token = default; // Simulates an already-admitted physical CAS, not a rollback on caller cancellation.
        }
        var result = await inner.WriteAsync(ns, data, expected, token);
        if (LoseReceipt && ns == "music.accounts.v1") { LoseReceipt = false; throw new ProtectedDocumentException(DocumentStoreError.Storage); }
        return result;
    }
    public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
}

internal sealed class QqAccountFixtureHandler : HttpMessageHandler
{
    internal string Fault = "", HoldAt = "";
    internal bool HoldEveryImage, ParentCookieDomains;
    internal Action<string>? OnStep;
    internal int Calls, Disposals, Status, Polls;
    internal TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static byte[] Image = Png();
    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var uri = request.RequestUri!; string step = uri.AbsolutePath.Split('/').Last();
        JsonObject? payload = null;
        if (uri.Host == "u.y.qq.com")
        {
            payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            step = payload["req_0"]!["method"]!.GetValue<string>();
            MusicNativeQqAccountTests.Check(!request.Headers.Contains("Cookie"));
        }
        OnStep?.Invoke(step);
        if (step == HoldAt || step == "ptqrshow" && HoldEveryImage)
        {
            HoldAt = ""; Arrived.TrySetResult();
            await Release.Task; Settled.TrySetResult(); // Deliberately ignores cancellation, tests real slot retention.
        }
        HttpResponseMessage Text(string text, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(text) };
        HttpResponseMessage Redirect(string location, int status = 302)
        { var r = Text("", status); r.Headers.Location = new(location); return r; }
        if (step == "ptqrshow")
        {
            MusicNativeQqAccountTests.Check(uri.Host == "ssl.ptlogin2.qq.com" && request.Method == HttpMethod.Get &&
                !request.Headers.Contains("Cookie") && uri.Query.Contains("appid=716027609") && uri.Query.Contains("pt_3rd_aid=100497308"));
            if (Fault == "image-302") return Redirect("https://attacker.invalid");
            byte[] png = Fault switch { "image-huge" => Png(width: 2048), "image-bomb" => Png(inflated: 1024 * 1024),
                "image-trailing" => [.. Image, 0], _ => (byte[])Image.Clone() };
            if (Fault == "image-bad-crc") png[^1] ^= 1;
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            r.Content.Headers.ContentType = new(Fault == "image-mime" ? "text/html" : "image/png");
            if (Fault != "cookie-missing") r.Headers.TryAddWithoutValidation("Set-Cookie",
                "qrsig=SYNTHETIC_QRSIG; Path=/; Domain=" + (Fault == "cookie-domain" ? ".kugou.com" :
                    Fault == "cookie-domain-lookalike" ? "ptlogin2.qq.com.attacker.invalid" :
                    ParentCookieDomains ? ".ptlogin2.qq.com" : ".qq.com") +
                (Fault == "cookie-expired" ? "; Max-Age=0" : ""));
            if (Fault == "cookie-duplicate") r.Headers.TryAddWithoutValidation("Set-Cookie", "qrsig=OTHER_QRSIG; Path=/; Domain=.qq.com");
            return r;
        }
        if (step == "ptqrlogin")
        {
            Interlocked.Increment(ref Polls);
            MusicNativeQqAccountTests.Check(uri.Host == "ssl.ptlogin2.qq.com" && request.Headers.GetValues("Cookie").Single() == "qrsig=SYNTHETIC_QRSIG" &&
                uri.Query.Contains("ptqrtoken=30614848") && uri.Query.Contains("ptredirect=0") &&
                uri.Query.Contains("u1=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump"));
            if (Fault == "poll-302") return Redirect("https://graph.qq.com/oauth2.0/login_jump");
            string callback = "https://ssl.ptlogin2.graph.qq.com/check_sig?uin=42&service=ptqrlogin&ptsigx=SYNTHETIC_SIGX&s_url=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump";
            callback = Fault switch {
                "callback-host" => callback.Replace("ssl.ptlogin2.graph.qq.com", "attacker.invalid"),
                "callback-port" => callback.Replace(".com/check", ".com:444/check"),
                "callback-userinfo" => callback.Replace("https://", "https://user@"),
                "callback-duplicate" => callback + "&uin=43", _ => callback };
            return Text($"ptuiCB('{(Fault == "callback-state" ? 99 : Status)}','0','{callback}','0','合成消息','合成昵称');" +
                (Fault == "callback-script" ? "eval('SYNTHETIC_SECRET');" : ""));
        }
        if (step == "check_sig")
        {
            MusicNativeQqAccountTests.Check(uri.Host == "ssl.ptlogin2.graph.qq.com" && !request.Headers.Contains("Cookie") &&
                uri.Query.Contains("ptsigx=SYNTHETIC_SIGX") && uri.Query.Contains("uin=42"));
            var r = Redirect(Fault == "signature-location" ? "https://attacker.invalid/" : "https://graph.qq.com/oauth2.0/login_jump",
                Fault == "signature-307" ? 307 : 302);
            r.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
                "p_skey=SYNTHETIC_PSKEY; Path=/; Domain=" + (Fault == "signature-domain" ? ".kugou.com" :
                    ParentCookieDomains ? ".graph.qq.com" : ".qq.com"),
                "p_uin=o00000000" + (Fault == "signature-identity" ? "43" : "42") + "; Path=/; Domain=.qq.com",
                "unrelated=DO_NOT_FORWARD; Path=/; Domain=.qq.com" });
            return r;
        }
        if (step == "authorize")
        {
            MusicNativeQqAccountTests.Check(uri.Host == "graph.qq.com" && request.Method == HttpMethod.Post);
            string cookie = request.Headers.GetValues("Cookie").Single();
            string form = await request.Content!.ReadAsStringAsync();
            MusicNativeQqAccountTests.Check(cookie.Contains("p_skey=SYNTHETIC_PSKEY") && !cookie.Contains("qrsig") &&
                !cookie.Contains("unrelated") && form.Contains("g_tk=451324171") &&
                form.Contains("client_id=100497308") && form.Contains("state=state"));
            return Redirect("https://" + (Fault == "authorize-host" ? "attacker.invalid" : "y.qq.com") +
                "/portal/wx_redirect.html?code=SYNTHETIC_CODE&state=" + (Fault == "authorize-state" ? "wrong" : "state") +
                (Fault == "authorize-duplicate" ? "&code=OTHER" : ""), Fault == "authorize-303" ? 303 : 302);
        }
        if (step == "QQLogin")
        {
            MusicNativeQqAccountTests.Check(payload!["comm"]!["tmeLoginType"]!.GetValue<int>() == 2 &&
                payload["req_0"]!["module"]!.GetValue<string>() == "QQConnectLogin.LoginServer" &&
                payload["req_0"]!["param"]!["code"]!.GetValue<string>() == "SYNTHETIC_CODE" && payload["comm"]!["authst"] is null);
            int code = Fault switch { "login-auth" => 1000, "login-device" => 20279, "login-verification" => 20276,
                "login-invalid-code" => 20271, "login-restricted" => 20277, "login-rate" => 104604, _ => 0 };
            return Json(new { code = 0, req_0 = new { code = 0, data = new { code, musicid = 42,
                str_musicid = Fault == "credential-identity" ? "43" : "42",
                musickey = Fault == "credential-inferred-type" ? "W_X_SYNTHETIC_KEY" : "SYNTHETIC_MUSICKEY",
                encryptUin = "SYNTHETIC_EUIN", loginType = Fault == "credential-inferred-type" ? (int?)null : Fault == "credential-type" ? 1 : 2 } } });
        }
        if (step == "GetLoginUserInfo") return Json(new { code = 0, req_0 = new { code = 0, data = new { code = 0 } } });
        if (step == "GetHomepageHeader") return Json(new { code = 0, req_0 = new { code = 0, data = new { Info = new { BaseInfo = new {
            EncryptedUin = Fault == "profile-identity" ? "WRONG_EUIN" : "SYNTHETIC_EUIN",
            Name = Fault == "profile-ticket" ? "SYNTHETIC_QRSIG" : "合成账号" } } } } });
        if (step == "vip_login_base") return Json(new { code = 0, req_0 = new { code = 0,
            data = new { svip = 1, userinfo = new { expire = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds() } } } });
        throw new InvalidOperationException("Unexpected synthetic QQ step");
    }
    protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    internal static byte[] Png(int width = 16, int inflated = 0)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string kind, byte[] data)
        {
            byte[] len = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, data.Length); output.Write(len);
            byte[] content = [.. Encoding.ASCII.GetBytes(kind), .. data]; output.Write(content);
            uint crc = 0xffffffff;
            foreach (byte b in content) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(len, ~crc); output.Write(len);
        }
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 16); header[8] = 8; Chunk("IHDR", header);
        byte[] pixels = new byte[inflated > 0 ? inflated : 16 * (width + 1)];
        for (int y = 0; y < 16 && inflated == 0; y++)
            for (int x = 0; x < width; x++) pixels[y * (width + 1) + 1 + x] = (byte)((x + y) % 2 == 0 ? 0 : 255);
        using var packed = new MemoryStream();
        using (var zip = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) zip.Write(pixels);
        Chunk("IDAT", packed.ToArray()); Chunk("IEND", []); return output.ToArray();
    }
}
