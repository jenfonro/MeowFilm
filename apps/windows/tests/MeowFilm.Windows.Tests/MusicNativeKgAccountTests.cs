using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeKgAccountTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("device-register-qr-identity-CAS-private-dpapi-reopen-logout", Flow);
        yield return Case("legal-empty-registration-no-invented-dfid", EmptyRegistration);
        yield return Case("fixed-seed-full-128bit-mid-web-android-signatures", FixedDevice);
        foreach (var pair in new[] { (0, "expired"), (1, "waiting"), (2, "confirming") })
        { var p = pair; yield return Case("qr-state-" + p.Item1, () => State(p.Item1, p.Item2)); }
        foreach (string fault in new[] { "register-length", "register-padding", "register-plain", "register-utf8",
            "register-duplicate", "register-dfid", "register-data", "register-status", "register-large",
            "register-ssa", "register-302", "key-empty", "key-injection", "key-data", "key-duplicate", "key-large",
            "key-302", "poll-status", "poll-302", "poll-ssa", "poll-auth", "poll-token", "poll-user",
            "poll-t1", "profile-missing-id", "profile-id", "profile-secret", "profile-302", "profile-403" })
        { var f = fault; yield return Case("reject-" + f, () => Reject(f)); }
        foreach (int write in new[] { 1, 2, 3 })
        {
            int w = write;
            yield return Case("lost-durable-receipt-" + w + "-explicit-read-no-replay", () => Unknown(w));
            yield return Case("real-CAS-conflict-" + w, () => Conflict(w));
        }
        foreach (string step in new[] { "register", "key", "poll", "profile" })
        { string s = step; yield return Case("cancel-" + s + "-no-late-session", () => Cancel(s)); }
        yield return Case("client-save-account-cancel-client-drain-before-observation", () => CancelWrite(1));
        yield return Case("dfid-save-account-cancel-client-drain-before-observation", () => CancelWrite(2));
        yield return Case("device-revision-change-rejects-challenge-before-request", DeviceChanged);
        yield return Case("observed-device-change-during-identity-fences-before-handoff", DeviceChangedAtIdentity);
        yield return Case("other-owner-logout-rejects-old-authorization", OtherOwner);
        yield return Case("cooldown-ttl-cap-and-local-challenge-identity", Lifetime);
        yield return Case("deadline-covers-registration-device-save-and-key", Deadline);
        yield return Case("concurrent-poll-busy-and-unthrottled-physical-http", Budget);
        yield return Case("two-native-owners-cannot-mint-competing-installation-seeds", CompetingSeeds);
        yield return Case("unknown-entry-rejected-without-network-or-write", UnknownEntry);
        yield return Case("corrupt-device-rejects-whole-document-without-write", CorruptDeviceDocument);
        yield return Case("ordinary-content-lyrics-no-new-origin-authority", Origins);
        yield return Case("direct-protocol-uses-explicit-client-owner-no-sms-or-token-operation", Direct);
        yield return Case("borrowed-http-store-and-finite-errors-no-secrets", Borrowed);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.native.accounts.kg." + name, run);
    internal static void Check(bool test) { if (!test) throw new InvalidOperationException("KG account assertion"); }
    internal static bool Ok(JsonElement reply) => reply.GetProperty("ok").GetBoolean();
    internal static string Code(JsonElement reply) => reply.GetProperty("code").GetString()!;
    internal static string Id(JsonElement reply) => reply.GetProperty("challenge").GetProperty("challengeId").GetString()!;
    internal static string StateOf(JsonElement reply) => reply.GetProperty("account").GetProperty("state").GetString()!;
    internal static JsonElement Row(JsonElement reply, string platform = "kg") =>
        reply.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("platform").GetString() == platform);
    internal static string DataPath()
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ??
            throw new InvalidOperationException("Explicit synthetic test root required");
        string path = Path.Combine(root, "kg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    internal static Task<JsonElement> Begin(MusicAccountOwner owner) => owner.CallAsync("music.accounts.qr.begin", new { platform = "kg" });
    internal static Task<JsonElement> Poll(MusicAccountOwner owner, string id) =>
        owner.CallAsync("music.accounts.qr.poll", new { platform = "kg", challengeId = id });
    private static async Task<JsonElement> Login(KgAccountFixture f)
    { var b = await Begin(f.Owner); return Ok(b) ? await Poll(f.Owner, Id(b)) : b; }
    private static async Task Flow()
    {
        string path = DataPath(), seed;
        await using (var f = new KgAccountFixture(path))
        {
            var begin = await Begin(f.Owner);
            Check(Ok(begin) && f.Store.Writes == 2 && f.Transport.Steps.SequenceEqual(["register", "key"]));
            Check(begin.GetProperty("challenge").GetProperty("displayKind").GetString() == "url" &&
                !begin.GetRawText().Contains("SYNTHETIC_DFID") && !begin.GetRawText().Contains(f.Transport.Seed!));
            seed = f.Transport.Seed!;
            Check(Id(begin) != KgAccountHandler.QrKey);
            Check(Ok(await Poll(f.Owner, Id(begin))) && f.Store.Writes == 3 && f.Transport.Calls == 5);
            var details = await f.Owner.ReadDetailsAsync("kg");
            Check(details.Generation == 1 && details.Membership.Vip == false &&
                details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh &&
                f.Transport.Steps.SequenceEqual(["register", "key", "poll", "profile", "rights"]));
            var lease = await f.Owner.AcquireAsync("kg");
            Check(lease is not null && f.Owner.IsCurrent(lease) && lease.UserId == "42" &&
                lease.Cookie.Contains("token=SYNTHETIC_KG_TOKEN") && lease.Cookie.Contains("t1=SYNTHETIC_T1"));
            Check(f.Owner.Revoke(lease!) && !f.Owner.IsCurrent(lease!) && await f.Owner.AcquireAsync("kg") is null);
            Check(!f.Owner.Revoke(lease!));
            Check((await Poll(f.Owner, Id(begin))).GetProperty("status").GetString() == "expired");
        }
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            string text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Check(!text.Contains(seed) && !text.Contains("SYNTHETIC_KG_TOKEN"));
        }
        await using var reopen = new KgAccountFixture(path);
        Check(Row(await reopen.Owner.CallAsync("music.accounts.list")).GetProperty("state").GetString() == "saved");
        Check((await reopen.Owner.ReadDetailsAsync("kg")).Membership.Vip == false && reopen.Transport.Calls == 0);
        Check(StateOf(await reopen.Owner.CallAsync("music.accounts.validate", new { platform = "kg" })) == "verified");
        var device = await KgAccountFixture.ReadDevice(reopen.Store);
        var lease2 = await reopen.Owner.AcquireAsync("kg");
        Check(StateOf(await reopen.Owner.CallAsync("music.accounts.logout", new { platform = "kg" })) == "none");
        Check(!reopen.Owner.IsCurrent(lease2!) && await reopen.Owner.AcquireAsync("kg") is null);
        Check(JsonNode.DeepEquals(device, await KgAccountFixture.ReadDevice(reopen.Store)));
        Check(Ok(await Begin(reopen.Owner)) && reopen.Transport.Registrations == 0);
    }
    private static async Task EmptyRegistration()
    {
        await using var f = new KgAccountFixture(); f.Transport.EmptyDevice = true;
        var first = await Begin(f.Owner); Check(Ok(first) && f.Store.Writes == 1);
        Check((await KgAccountFixture.ReadDevice(f.Store))["dfid"] is null);
        Check(Ok(await Poll(f.Owner, Id(first))) && f.Store.Writes == 2);
        var lease = await f.Owner.AcquireAsync("kg");
        Check(lease is not null && !lease.Cookie.Contains("dfid=") && !lease.Cookie.Contains("undefined"));
        int calls = f.Transport.Calls; await f.Owner.CallAsync("music.accounts.list");
        Check(f.Transport.Calls == calls); f.Clock.Advance(5);
        Check(Ok(await Begin(f.Owner)) && f.Transport.Registrations == 2); // Explicit user retry, same seed.
    }
    private static async Task FixedDevice()
    {
        await using var f = new KgAccountFixture();
        await KgAccountFixture.SeedDevice(f.Store.Inner, dfid: null);
        Check(Ok(await Login(f)));
        Check(f.Transport.Seed == KgAccountFixture.Seed &&
            f.Transport.Mid == "176905054734845483393488264506404844848");
        Check(f.Transport.RegistrationPlain?["imei"]?.GetValue<string>() == KgAccountFixture.Seed);
        Check(f.Transport.RegistrationPlain?["accelerometer"]?.GetValue<bool>() == false);
    }
    private static async Task State(int state, string expected)
    {
        await using var f = new KgAccountFixture(); f.Transport.Status = state;
        var b = await Begin(f.Owner);
        var p = await Poll(f.Owner, Id(b)); Check(Ok(p) && p.GetProperty("status").GetString() == expected &&
            f.Store.Writes == 2 && f.Transport.Profiles == 0);
    }
    private static async Task Reject(string fault)
    {
        await using var f = new KgAccountFixture(); f.Transport.Fault = fault;
        var result = await Login(f);
        Check(!Ok(result));
        string code = fault switch
        {
            "register-large" or "key-large" => "LIMIT",
            "register-ssa" or "poll-ssa" => "VERIFICATION_REQUIRED",
            "poll-auth" => "AUTH_EXPIRED",
            "profile-403" => "FORBIDDEN",
            "profile-id" or "profile-missing-id" => "IDENTITY_MISMATCH",
            "register-status" => "UPSTREAM_ERROR",
            _ when fault.EndsWith("-302", StringComparison.Ordinal) => "REDIRECT_DENIED",
            _ => "INVALID_RESPONSE"
        };
        Check(Code(result) == code && !result.GetRawText().Contains("SYNTHETIC"));
        Check(await f.Owner.AcquireAsync("kg") is null && f.Store.Writes <= 2);
    }
    private static async Task Unknown(int write)
    {
        await using var f = new KgAccountFixture(); f.Store.LoseReceiptAt = write;
        var result = await Login(f);
        Check(!Ok(result) && Code(result) == (write == 3 ? "HANDOFF_UNCONFIRMED" : "COMMIT_UNKNOWN"));
        Check(f.Store.Writes == write && await f.Owner.AcquireAsync("kg") is null);
        int calls = f.Transport.Calls;
        var blocked = await Begin(f.Owner); Check(!Ok(blocked) && Code(blocked) == "COMMIT_UNKNOWN");
        Check(f.Transport.Calls == calls);
        var list = await f.Owner.CallAsync("music.accounts.list");
        Check(Ok(list) && f.Transport.Calls == calls);
        if (write <= 2)
        {
            Check(f.Clients.Owner.NeedsReconciliation);
            await f.Clients.Owner.ReloadAsync(); // Account readback cannot reconcile a different writer.
            Check(f.Transport.Calls == calls);
        }
        string seed = (await KgAccountFixture.ReadDevice(f.Store))["seed"]!.GetValue<string>();
        f.Clock.Advance(5);
        Check(Ok(await Begin(f.Owner)) && (await KgAccountFixture.ReadDevice(f.Store))["seed"]!.GetValue<string>() == seed);
        Check(f.Transport.Registrations == 1); // Acknowledged dfid is never registered again.
        if (write == 3) Check(Row(list).GetProperty("state").GetString() == "saved");
    }
    private static async Task Conflict(int write)
    {
        await using var f = new KgAccountFixture(); f.Store.ConflictAt = write;
        var result = await Login(f);
        Check(!Ok(result) && Code(result) == (write == 3 ? "HANDOFF_UNCONFIRMED" : "STORAGE_CONFLICT"));
        Check(await f.Owner.AcquireAsync("kg") is null);
        Check(f.Transport.Calls == (write == 1 ? 0 : write == 2 ? 1 : 5));
        if (write <= 2)
            Check((await MusicClientContextFixture.ReadDocument(f.Store))["wy"]!["nmtid"]!.GetValue<string>() == "SYNTHETIC_CONCURRENT");
        else
            Check((await KgAccountFixture.ReadRoot(f.Store))["entries"]!.AsArray()
                .Any(n => n!["key"]!.GetValue<string>() == KgAccountFixture.ConcurrentAccountKey));
    }
    private static async Task Cancel(string step)
    {
        await using var f = new KgAccountFixture(); f.Transport.HoldAt = step;
        var pending = Login(f);
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(Ok(await f.Owner.CallAsync("music.accounts.cancel", new { platform = "kg" })));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(4));
        Check(!Ok(result) && Code(result) is "CANCELLED" or "SUPERSEDED");
        int calls = f.Transport.Calls;
        var clientWork = step == "register" ? f.Clients.Owner.EnsureKgAsync(f.Clients.Protocol, default) : null;
        f.Transport.Release.TrySetResult();
        await f.Transport.Settled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        if (clientWork is not null) await clientWork.WaitAsync(TimeSpan.FromSeconds(4));
        Check(f.Transport.Calls == calls && f.Store.Writes <= 2 && await f.Owner.AcquireAsync("kg") is null);
    }
    private static async Task CancelWrite(int write)
    {
        await using var f = new KgAccountFixture(); f.Store.HoldAt = write;
        var pending = Begin(f.Owner);
        await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await f.Owner.CallAsync("music.accounts.cancel", new { platform = "kg" });
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(4));
        Check(!Ok(result) && Code(result) is "CANCELLED" or "SUPERSEDED" && f.Transport.Calls == write - 1);
        var read = f.Owner.CallAsync("music.accounts.list");
        Check(Ok(await read)); // Account storage does not own this anonymous client write.
        var drain = f.Clients.Owner.PrepareShutdownAsync();
        Check(!drain.IsCompleted);
        f.Store.Release.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(4));
        await f.Clients.Owner.CancelShutdownPreparationAsync();
        Check(f.Store.Writes == write && f.Transport.Calls == write - 1 && !f.Clients.Owner.NeedsReconciliation);
        Check((await KgAccountFixture.ReadDevice(f.Store))["seed"]!.GetValue<string>().Length == 32);
    }
    private static async Task DeviceChanged()
    {
        await using var f = new KgAccountFixture(); var b = await Begin(f.Owner);
        await KgAccountFixture.MutateDevice(f.Store.Inner);
        await f.Clients.Owner.ReloadAsync();
        int calls = f.Transport.Calls;
        var result = await Poll(f.Owner, Id(b));
        Check(!Ok(result) && Code(result) == "STORAGE_CONFLICT" && f.Transport.Calls == calls);
    }
    private static async Task DeviceChangedAtIdentity()
    {
        await using var f = new KgAccountFixture(); var b = await Begin(f.Owner);
        f.Transport.HoldAt = "profile";
        var pending = Poll(f.Owner, Id(b)); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await KgAccountFixture.MutateDevice(f.Store.Inner); await f.Clients.Owner.ReloadAsync();
        f.Transport.Release.TrySetResult();
        var result = await pending;
        Check(!Ok(result) && Code(result) == "STORAGE_CONFLICT" && await f.Owner.AcquireAsync("kg") is null);
    }
    private static async Task OtherOwner()
    {
        await using var f = new KgAccountFixture(); var b = await Begin(f.Owner);
        f.Transport.HoldAt = "profile";
        var pending = Poll(f.Owner, Id(b)); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        using var http = MusicPlatformHttp.CreateWithHandler(new KgAccountHandler());
        await using var otherStore = new LocalDocumentStore(f.Path, ["music.accounts.v1", MusicPlatformClientContextOwner.DocumentNamespace], protectedNamespaces: ["music.accounts.v1", MusicPlatformClientContextOwner.DocumentNamespace]);
        await using var clientContext1 = new MusicClientContextFixture(http);
        await using var other = clientContext1.CreateAccountOwner(otherStore);
        Check(Ok(await other.CallAsync("music.accounts.logout", new { platform = "kg" })));
        f.Transport.Release.TrySetResult();
        Check(!Ok(await pending) && await f.Owner.AcquireAsync("kg") is null);
        Check((await KgAccountFixture.ReadDevice(otherStore))["dfid"]!.GetValue<string>() == KgAccountHandler.Dfid);
    }
    private static async Task Lifetime()
    {
        await using var f = new KgAccountFixture(); f.Transport.Status = 1;
        var first = await Begin(f.Owner); int calls = f.Transport.Calls;
        Check((await Poll(f.Owner, Guid.NewGuid().ToString("D"))).GetProperty("status").GetString() == "expired");
        Check(f.Transport.Calls == calls);
        var limited = await Begin(f.Owner); Check(!Ok(limited) && Code(limited) == "RATE_LIMITED");
        f.Clock.Advance(5); var second = await Begin(f.Owner);
        Check(Ok(second) && Id(second) != Id(first));
        f.Clock.Advance(181);
        Check((await Poll(f.Owner, Id(second))).GetProperty("status").GetString() == "expired");
        var third = await Begin(f.Owner);
        for (int i = 0; i < 100; i++) Check(Ok(await Poll(f.Owner, Id(third))));
        Check(Ok(await Poll(f.Owner, Id(third))));
        f.Clock.Advance(181);
        Check((await Poll(f.Owner, Id(third))).GetProperty("status").GetString() == "expired");
    }
    private static async Task Deadline()
    {
        await using var f = new KgAccountFixture();
        f.Transport.OnStep = _ => f.Clock.Advance(14);
        var b = await Begin(f.Owner);
        Check(!Ok(b) && Code(b) == "TIMEOUT" && f.Transport.Calls == 2 && f.Store.Writes == 2);
    }
    private static async Task Budget()
    {
        await using var f = new KgAccountFixture(); var b = await Begin(f.Owner); f.Transport.HoldAt = "poll";
        var first = Poll(f.Owner, Id(b)); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var second = await Poll(f.Owner, Id(b)); Check(!Ok(second) && Code(second) == "BUSY");
        await f.Owner.CallAsync("music.accounts.cancel", new { platform = "kg" }); await first;
        f.Transport.HoldAllKeys = true; f.Clock.Advance(5);
        var next = Begin(f.Owner);
        await Until(() => f.Transport.Calls == 4);
        await f.Owner.CallAsync("music.accounts.cancel", new { platform = "kg" }); await next;
        f.Clock.Advance(5);
        // Cancelled requests that are still draining do not block a new login attempt.
        var third = Begin(f.Owner);
        await Until(() => f.Transport.Calls > 4);
        f.Transport.Release.TrySetResult();
        await third;
    }
    private static async Task CompetingSeeds()
    {
        await using var f = new KgAccountFixture(); f.Store.HoldAt = 1;
        var first = Begin(f.Owner); await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await using var other = new KgAccountFixture(f.Path);
        Check(Ok(await Begin(other.Owner)));
        string seed = (await KgAccountFixture.ReadDevice(other.Store))["seed"]!.GetValue<string>();
        f.Store.Release.TrySetResult();
        var rejected = await first;
        Check(!Ok(rejected) && Code(rejected) == "STORAGE_CONFLICT" && f.Transport.Calls == 0);
        Check((await KgAccountFixture.ReadDevice(f.Store))["seed"]!.GetValue<string>() == seed);
    }
    private static async Task UnknownEntry()
    {
        await using var f = new KgAccountFixture();
        await KgAccountFixture.Edit(f.Store.Inner, root => root["entries"]!.AsArray().Add(new JsonObject
        { ["key"] = "unsupported-entry", ["value"] = new JsonObject { ["nested"] = new JsonArray(1, "unchanged") } }));
        string original = (await KgAccountFixture.ReadRoot(f.Store)).ToJsonString();
        var bad = await Begin(f.Owner);
        Check(!Ok(bad) && Code(bad) == "CORRUPT_STORAGE" && f.Transport.Calls == 0 && f.Store.Writes == 0);
        Check((await KgAccountFixture.ReadRoot(f.Store)).ToJsonString() == original);
    }
    private static async Task CorruptDeviceDocument()
    {
        await using var f = new KgAccountFixture();
        await KgAccountFixture.SeedDevice(f.Store.Inner);
        await KgAccountFixture.Edit(f.Store.Inner, r =>
            r["entries"]!.AsArray().Single(n => n!["key"]!.GetValue<string>() == KgAccountFixture.DeviceKey)!["value"]!["seed"] = "corrupt");
        string original = (await KgAccountFixture.ReadRoot(f.Store)).ToJsonString();
        var bad = await Begin(f.Owner); Check(!Ok(bad) && Code(bad) == "CORRUPT_STORAGE" && f.Transport.Calls == 0);
        var unrelated = await f.Owner.CallAsync("music.accounts.logout", new { platform = "wy" });
        Check(!Ok(unrelated) && Code(unrelated) == "CORRUPT_STORAGE" &&
            f.Transport.Calls == 0 && f.Store.Writes == 0 &&
            (await KgAccountFixture.ReadRoot(f.Store)).ToJsonString() == original);
    }
    private static async Task Origins()
    {
        await using var f = new KgAccountFixture();
        foreach (string platform in new[] { "wy", "tx", "kg" })
        foreach (string host in new[] { "userservice.kugou.com", "login-user.kugou.com", "login.user.kugou.com", "h5.kugou.com" })
        {
            var uri = new Uri("https://" + host + "/v2/qrcode");
            Check(!MusicPlatformHttp.IsAllowedOrigin(platform, uri));
            try { await f.Http.SendAsync(platform, new(HttpMethod.Get, uri), default); throw new InvalidOperationException(); }
            catch (MusicPlatformException e) { Check(e.Code == "ORIGIN_DENIED"); }
        }
        Check(f.Transport.Calls == 0);
    }
    private static async Task Direct()
    {
        await using var f = new KgAccountFixture();
        var challenge = await f.Protocol.BeginAsync("kg");
        Check(challenge.Platform == "kg" && challenge.KgClient is not null && f.Clients.Owner.IsCurrent(challenge.KgClient));
        Check(await f.Owner.AcquireAsync("kg") is null);
        foreach (string method in new[] { "sms.send", "sms.login", "refresh", "device.reset" })
            Check(Code(await f.Owner.CallAsync("music.accounts." + method, new { platform = "kg" })) == "INVALID_REQUEST");
        Check(f.Transport.Calls == 2 && f.Store.Writes == 2);
        var kg = f.Protocol.Describe().GetProperty("platforms").EnumerateArray().Single(v => v.GetProperty("platform").GetString() == "kg");
        Check(kg.GetProperty("qr").GetBoolean() && !kg.GetProperty("sms").GetBoolean() &&
            !kg.GetProperty("credentialRefresh").GetBoolean());
    }
    private static async Task Borrowed()
    {
        await using var f = new KgAccountFixture(); Check(Ok(await Login(f)));
        await f.Owner.DisposeAsync();
        Check(f.Transport.Disposals == 0 && f.Store.Disposals == 0);
        Check((await f.Store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
        Check(new MusicAccountException("SYNTHETIC_TOKEN").ToString() == "MusicAccountException: ACCOUNT_FAILED");
    }
    internal static async Task Until(Func<bool> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!check()) await Task.Delay(15, deadline.Token);
    }
}

internal sealed class KgAccountClock : TimeProvider
{
    private long ticks;
    internal void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

internal sealed class KgAccountFixture : IAsyncDisposable
{
    internal const string DeviceKey = "[\"meowfilm\",\"music\",1,\"platform-device\",\"kg\"]";
    internal const string AccountKey = "[\"meowfilm\",\"music\",1,\"platform-account\",\"kg\"]";
    internal const string ConcurrentAccountKey = "[\"meowfilm\",\"music\",1,\"platform-account\",\"wy\"]";
    internal const string Seed = "0123456789abcdef0123456789abcdef";
    internal readonly string Path;
    internal readonly KgAccountStore Store;
    internal readonly KgAccountHandler Transport = new();
    internal readonly KgAccountClock Clock = new();
    internal readonly MusicPlatformHttp Http;
    internal readonly MusicAccountProtocol Protocol;
    internal readonly MusicAccountOwner Owner;
    internal readonly MusicClientContextFixture Clients;
    internal KgAccountFixture(string? path = null)
    {
        Path = path ?? MusicNativeKgAccountTests.DataPath();
        Store = new(new LocalDocumentStore(Path, ["music.accounts.v1", MusicPlatformClientContextOwner.DocumentNamespace], protectedNamespaces: ["music.accounts.v1", MusicPlatformClientContextOwner.DocumentNamespace]));
        Http = MusicPlatformHttp.CreateWithHandler(Transport);
        Clients = new(Http, Store, importAccounts: true, clock: Clock, registrationEntropy: () => "test01");
        Protocol = Clients.CreateAccountProtocol(); Owner = Clients.CreateAccountOwner(Store, Protocol);
        Transport.BeforeRegister = async () =>
        {
            var device = await ReadDevice(Store);
            MusicNativeKgAccountTests.Check(device["generation"]!.GetValue<long>() > 0);
            Transport.DurableSeed = device["seed"]!.GetValue<string>();
        };
    }
    internal static async Task<JsonObject> ReadRoot(IDocumentStore store)
    {
        var doc = await store.ReadAsync("music.accounts.v1");
        try { return doc.Data is null ? new JsonObject { ["version"] = 1, ["entries"] = new JsonArray() } :
            JsonNode.Parse(doc.Data)!.AsObject(); }
        finally { if (doc.Data is not null) CryptographicOperations.ZeroMemory(doc.Data); }
    }
    internal static async Task<JsonObject> ReadDevice(IDocumentStore store) =>
        (await MusicClientContextFixture.ReadDocument(store))["kg"]!.AsObject();
    internal static async Task Edit(IDocumentStore store, Action<JsonObject> action)
    {
        var doc = await store.ReadAsync("music.accounts.v1");
        try
        {
            var root = doc.Data is null ? new JsonObject { ["version"] = 1, ["entries"] = new JsonArray() } : JsonNode.Parse(doc.Data)!.AsObject();
            action(root);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await store.WriteAsync("music.accounts.v1", data, doc.Fingerprint); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        finally { if (doc.Data is not null) CryptographicOperations.ZeroMemory(doc.Data); }
    }
    internal static Task SeedDevice(IDocumentStore store, string? dfid = KgAccountHandler.Dfid) =>
        Edit(store, root => root["entries"]!.AsArray().Add(new JsonObject { ["key"] = DeviceKey,
            ["value"] = new JsonObject { ["version"] = 1, ["generation"] = 1, ["seed"] = Seed, ["dfid"] = dfid } }));
    internal static Task MutateDevice(IDocumentStore store) => MusicClientContextFixture.MutateDocument(store, root =>
    {
        var value = root["kg"]!;
        value["generation"] = value["generation"]!.GetValue<long>() + 1; value["dfid"] = "SYNTHETIC_NEW_DFID";
    });
    public async ValueTask DisposeAsync()
    {
        Transport.Release.TrySetResult(); Store.Release.TrySetResult();
        try { await Owner.DisposeAsync(); }
        finally { await Clients.DisposeAsync(); Http.Dispose(); await Store.DisposeAsync(); }
    }
}

internal sealed class KgAccountStore(LocalDocumentStore inner) : IDocumentStore
{
    internal LocalDocumentStore Inner => inner;
    internal int Writes, AccountWrites, Disposals, LoseReceiptAt, ConflictAt, HoldAt;
    internal TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) => inner.ReadAsync(ns, token);
    public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken token = default)
    {
        if (ns is "music.accounts.v1" or MusicPlatformClientContextOwner.DocumentNamespace)
        {
            int write = Interlocked.Increment(ref Writes);
            if (ns == "music.accounts.v1") Interlocked.Increment(ref AccountWrites);
            if (write == HoldAt) { Arrived.TrySetResult(); await Release.Task; token = default; }
            if (write == ConflictAt)
            {
                if (ns == MusicPlatformClientContextOwner.DocumentNamespace)
                    await MusicClientContextFixture.ConcurrentClientWrite(inner);
                else
                    await KgAccountFixture.Edit(inner, root => root["entries"]!.AsArray().Add(new JsonObject
                    { ["key"] = KgAccountFixture.ConcurrentAccountKey, ["value"] = new JsonObject {
                        ["version"] = 1, ["generation"] = 1, ["cookie"] = null, ["profile"] = null,
                        ["scope"] = null, ["validatedAt"] = null } }));
            }
            var result = await inner.WriteAsync(ns, data, expected, token);
            if (write == LoseReceiptAt) throw new DocumentStoreException(DocumentStoreError.Storage);
            return result;
        }
        return await inner.WriteAsync(ns, data, expected, token);
    }
    public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
}

internal sealed class KgAccountHandler : HttpMessageHandler
{
    internal const string Dfid = "SYNTHETIC_DFID", QrKey = "SYNTHETIC_QR_KEY";
    internal string Fault = "", HoldAt = "";
    internal string? Seed, Mid, DurableSeed;
    internal JsonObject? RegistrationPlain;
    internal bool EmptyDevice, HoldAllKeys;
    internal int Status = 4, Calls, Registrations, Profiles, Polls, Disposals;
    internal readonly List<string> Steps = [];
    internal Action<string>? OnStep;
    internal Func<Task>? BeforeRegister;
    internal TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static HttpResponseMessage Json(object value) => Raw(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Raw(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    internal static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').ToDictionary(
        p => Uri.UnescapeDataString(p.Split('=', 2)[0].Replace("+", " ")),
        p => Uri.UnescapeDataString(p.Split('=', 2)[1].Replace("+", " ")), StringComparer.Ordinal);
    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    private static byte[] Cipher(byte[] plaintext)
    {
        string md5 = Md5("test01");
        using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(md5[..16]);
        return aes.EncryptCbc(plaintext, Encoding.ASCII.GetBytes(md5[16..]), PaddingMode.PKCS7);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var uri = request.RequestUri!;
        string step = uri.AbsolutePath switch { "/risk/v2/r_register_dev" => "register",
            "/v2/qrcode" => "key", "/v2/get_userinfo_qrcode" => "poll", "/v3/get_my_info" => "profile",
            "/v1/get_union_vip" => "rights", _ => "unknown" };
        lock (Steps) Steps.Add(step);
        MusicNativeKgAccountTests.Check(step != "unknown" && uri.Scheme == "https" && uri.IsDefaultPort &&
            !request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Forwarded-For"));
        OnStep?.Invoke(step);
        if (HoldAt == step || HoldAllKeys && step == "key")
        { HoldAt = ""; Arrived.TrySetResult(); await Release.Task; Settled.TrySetResult(); }
        if (Fault == step + "-302")
        { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new("https://attacker.invalid"); return r; }
        if (Fault == step + "-ssa")
        { var r = Json(new { status = 1 }); r.Headers.TryAddWithoutValidation("ssa-code", "SYNTHETIC_VERIFICATION"); return r; }
        if (Fault == step + "-403") return new(HttpStatusCode.Forbidden);
        var q = Query(uri);
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        if (step == "rights")
        {
            MusicNativeKgAccountTests.Check(uri.Host == "kugouvip.kugou.com" && request.Method == HttpMethod.Get &&
                q["userid"] == "42" && q["token"] == "SYNTHETIC_KG_TOKEN" && q["busi_type"] == "concept" &&
                !request.Headers.Contains("x-router"));
            return Json(new { status = 1, data = new { userid = "42", is_vip = 0 } });
        }
        string salt = step is "key" or "poll" ? "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt" : "OIlwieks28dk2k092lksi2UIkp";
        MusicNativeKgAccountTests.Check(q["signature"] == Md5(salt + string.Concat(q.Where(p => p.Key != "signature")
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + body + salt));
        MusicNativeKgAccountTests.Check(request.Headers.GetValues("mid").Single() == q["mid"] &&
            request.Headers.GetValues("dfid").Single() == q["dfid"] && q["clientver"] == "20489" && q["uuid"] == "-");
        Mid = q["mid"];
        if (step == "register")
        {
            Registrations++;
            if (BeforeRegister is not null) await BeforeRegister();
            MusicNativeKgAccountTests.Check(uri.Host == "userservice.kugou.com" && request.Method == HttpMethod.Post &&
                q["appid"] == "1005" && q["part"] == "1" && q["platid"] == "1" && q["p"].Length == 256 &&
                q["p"].All(c => char.IsAsciiHexDigitLower(c)) && !q.ContainsKey("token") && !q.ContainsKey("userid"));
            string digest = Md5("test01");
            using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(digest[..16]);
            byte[] plain = aes.DecryptCbc(Convert.FromBase64String(body), Encoding.ASCII.GetBytes(digest[16..]), PaddingMode.PKCS7);
            RegistrationPlain = JsonNode.Parse(plain)!.AsObject(); Seed = RegistrationPlain["imei"]!.GetValue<string>();
            MusicNativeKgAccountTests.Check(Seed == DurableSeed && Seed == RegistrationPlain["uuid"]!.GetValue<string>() &&
                q["mid"] == new BigInteger(MD5.HashData(Encoding.ASCII.GetBytes(Seed)), true, true).ToString(CultureInfo.InvariantCulture) &&
                q["dfid"] == "-" && RegistrationPlain["availableRamSize"]!.GetValue<long>() == 4983533568L &&
                RegistrationPlain["imsi"]!.GetValue<string>() == "" && RegistrationPlain.Count == 31);
            string text = Fault switch
            {
                "register-duplicate" => "{\"status\":1,\"status\":1,\"data\":[]}",
                "register-dfid" => "{\"status\":1,\"data\":{\"dfid\":\"bad\"}}",
                "register-data" => "{\"status\":1,\"data\":{}}",
                "register-status" => "{\"status\":0,\"data\":[]}",
                _ => EmptyDevice ? "{\"status\":1,\"data\":[]}" : JsonSerializer.Serialize(new { status = 1, data = new { dfid = Dfid } })
            };
            byte[] bytes = Fault switch
            {
                "register-length" => new byte[17], "register-padding" => new byte[32],
                "register-plain" => Encoding.UTF8.GetBytes(text), "register-large" => new byte[1048592],
                "register-utf8" => Cipher([0xff, 0xfe]), _ => Cipher(Encoding.UTF8.GetBytes(text))
            };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        if (step == "key")
        {
            MusicNativeKgAccountTests.Check(uri.Host == "login-user.kugou.com" && request.Method == HttpMethod.Get &&
                q["appid"] == "1001" && q["type"] == "1" && q["plat"] == "4" && q["srcappid"] == "2919" &&
                q["qrcode_txt"] == "https://h5.kugou.com/apps/loginQRCode/html/index.html?appid=1005&" &&
                !q.ContainsKey("token") && !q.ContainsKey("userid"));
            if (Fault == "key-data") return Json(new { status = 1, data = new { other = QrKey } });
            if (Fault == "key-duplicate") return Raw("{\"status\":1,\"data\":{\"qrcode\":\"a\",\"qrcode\":\"b\"}}");
            if (Fault == "key-large") return Raw(new string('a', 65537));
            return Json(new { status = 1, data = new { qrcode = Fault == "key-empty" ? "" :
                Fault == "key-injection" ? "key&url=https://attacker.invalid" : QrKey } });
        }
        if (step == "poll")
        {
            Polls++;
            MusicNativeKgAccountTests.Check(uri.Host == "login-user.kugou.com" && request.Method == HttpMethod.Get &&
                q["appid"] == "1005" && q["plat"] == "4" && q["srcappid"] == "2919" &&
                q["qrcode"] == QrKey && q["dev"].Length == 10 && q["dev"] == q["dev"].ToUpperInvariant());
            if (Seed is not null) MusicNativeKgAccountTests.Check(q["dev"] == Seed[..10].ToUpperInvariant());
            if (Fault == "poll-auth") return Json(new { status = 0, error_code = 20018 });
            return Json(new { status = 1, data = new { status = Fault == "poll-status" ? 3 : Status,
                userid = Fault == "poll-user" ? "0" : "42", token = Fault == "poll-token" ? "bad;token=bad" : "SYNTHETIC_KG_TOKEN",
                t1 = Fault == "poll-t1" ? "bad value" : "SYNTHETIC_T1", vip = 1 } });
        }
        Profiles++;
        MusicNativeKgAccountTests.Check(uri.Host == "gateway.kugou.com" && request.Method == HttpMethod.Post &&
            request.Headers.GetValues("x-router").Single() == "usercenter.kugou.com" && q["userid"] == "42" &&
            q["token"] == "SYNTHETIC_KG_TOKEN" && JsonNode.Parse(body)!["p"]!.GetValue<string>().Length == 256);
        if (Fault == "profile-missing-id") return Json(new { status = 1, data = new { nickname = "合成酷狗账号", pic = "" } });
        return Json(new { status = 1, data = new { userid = Fault == "profile-id" ? "43" : "42",
            nickname = Fault == "profile-secret" ? "SYNTHETIC_KG_TOKEN" : "合成酷狗账号", pic = "" } });
    }
    protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
}
