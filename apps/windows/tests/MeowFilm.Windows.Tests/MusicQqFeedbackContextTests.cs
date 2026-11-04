// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// All traffic is intercepted. DPAPI uses a new dedicated synthetic directory per case.
public static class MusicQqFeedbackContextTests
{
    private const string Cookie = "musicid=9007199254740997; str_musicid=9007199254740997; musickey=SYNTHETIC_QQ_CONTEXT_KEY; encrypt_uin=SYNTHETIC_CONTEXT_EUIN";
    private const string UserId = "9007199254740997";
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("durable-device-QIMEI-session-live-account-and-DPAPI-reopen", Reopen);
        yield return Case("warm-cache-always-live-verifies-no-generation-churn", Warm);
        yield return Case("source-QIMEI-crypto-vectors-and-exact-vivo-recipe", QimeiVector);
        yield return Case("anonymous-session-first-and-refresh-no-account-fields", Anonymous);
        yield return Case("same-account-serial-acquire-one-device-chain", Concurrent);
        yield return Case("exact-issued-object-only-zero-IO-for-clone-or-wrong-platform", ExactObject);
        yield return Case("saved-account-without-live-proof-never-grants-device", () => Reject("auth-expired", "AUTH_REQUIRED"));
        yield return Case("cached-device-does-not-bypass-Finish-live-auth", CachedAuthFailure);
        foreach (var pair in new[] {
            ("profile-id", "IDENTITY_MISMATCH"), ("profile-secret", "INVALID_RESPONSE"),
            ("qimei-empty", "INVALID_RESPONSE"), ("qimei-number", "INVALID_RESPONSE"),
            ("qimei-missing", "INVALID_RESPONSE"), ("qimei-control", "INVALID_RESPONSE"),
            ("qimei-duplicate", "INVALID_RESPONSE"), ("qimei-status201", "UPSTREAM_ERROR"),
            ("session-bool", "INVALID_RESPONSE"), ("session-empty", "INVALID_RESPONSE"),
            ("session-float", "INVALID_RESPONSE"), ("auth-ssa", "VERIFICATION_REQUIRED"),
            ("session-redirect", "REDIRECT_DENIED"), ("session-large", "LIMIT") })
        { var p = pair; yield return Case("reject-" + p.Item1, () => Reject(p.Item1, p.Item2)); }
        yield return Case("session-source-integer-zero-is-string-not-bool", NumericSession);
        yield return Case("local-midnight-revocation-and-caller1-refresh", Midnight);
        yield return Case("24hour-QIMEI-refresh-and-session-generation-binding", QimeiExpiry);
        yield return Case("future-cache-is-not-current-or-auth-proof", Future);
        yield return Case("lease-caller-cancel-dispose-and-JSON-redaction", Redaction);
        yield return Case("local-logout-revokes-context-retains-anonymous-device", Logout);
        yield return Case("account-switch-fences-old-acquisition-before-device-network", Switch);
        yield return Case("other-owner-account-change-observed-and-revoked", OtherOwner);
        yield return Case("other-owner-device-fingerprint-change-observed-and-revoked", OtherDevice);
        yield return Case("device-CAS-conflict-preserves-concurrent-current-entry-no-retry", Conflict);
        yield return Case("lost-local-client-receipt-blocks-network-until-client-readback", Unknown);
        yield return Case("lost-QIMEI-receipt-never-requests-session-before-client-readback", () => UnknownStage(1));
        yield return Case("lost-session-receipt-never-publishes-lease-before-client-readback", () => UnknownStage(2));
        yield return Case("account-switch-during-QIMEI-retains-anonymous-work-rejects-old-lease", () => SwitchStage("qimei"));
        yield return Case("account-switch-during-GetSession-retains-anonymous-work-rejects-old-lease", () => SwitchStage("session"));
        yield return Case("corrupt-device-is-not-regenerated-or-overwritten", Corrupt);
        yield return Case("cancel-account-waiter-does-not-own-admitted-client-CAS", CancelWrite);
        foreach (string step in new[] { "auth", "qimei", "session" })
        { var s = step; yield return Case("cancel-" + s + "-physical-work-drains-at-real-owner", () => CancelHttp(s)); }
        yield return Case("one-25second-deadline-covers-chain-and-awaits-handler", Deadline);
        yield return Case("account-close-fences-lease-client-close-drains-physical-borrowed-dependencies", Close);
        yield return Case("owner-eight-admission-and-cancel-during-physical-hold", Admission);
        yield return Case("lease-budget32-and-dispose-releases-admission", LeaseBudget);
        yield return Case("storage-read-failure-no-HTTP-or-memory-fallback", ReadFailure);
        yield return Case("new-HTTP-exact-QQ-steps-and-track-vkey-report-pairs", HttpRoutes);
        yield return Case("new-HTTP-no-ordinary-origin-expansion", HttpOrdinary);
        foreach (var fault in new[] { "uri", "http", "port", "fragment", "userinfo", "method", "query", "host", "module", "batch", "pair", "duplicate" })
        { var f = fault; yield return Case("new-HTTP-denies-" + f, () => HttpDenied(f)); }
        yield return Case("device-HTTP-no-cookie-or-auth-in-anonymous-step", HttpAnonymous);
        yield return Case("QIMEI-HTTP-exact-headers-and-envelope", HttpQimei);
        yield return Case("report-HTTP-gzip-status200-nonempty-and-bound", HttpReport);
        yield return Case("new-HTTP-request-body-headers-response-bounded", HttpLimits);
        yield return Case("new-HTTP-stream-dispose-cancel-retains-physical-work-without-blocking-requests", HttpCleanup);
        yield return Case("new-HTTP-cancelled-request-producer-retains-slot", HttpProducer);
        yield return Case("new-HTTP-awaiting-producer-cannot-retarget-before-send", () => HttpRetarget(false));
        yield return Case("new-HTTP-awaiting-producer-cannot-replace-host-before-send", () => HttpRetarget(true));
        yield return Case("new-HTTP-fixed20second-deadline-waits-physical", HttpDeadline);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.qq.context." + name, run);
    private static void Check(bool condition, string reason = "QQ context invariant")
    { if (!condition) throw new InvalidOperationException(reason); }
    private static async Task Error(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (MusicPlatformException e)
        {
            Check(e.Code == expected, "Expected " + expected + ", got " + e.Code);
            Check(e.Message == e.Code && e.InnerException is null && !e.ToString().Contains("SYNTHETIC"), "Finite diagnostics");
            return;
        }
        throw new InvalidOperationException("Expected " + expected);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }
    private static async Task Stopped(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        catch (MusicPlatformException error) when (error.Code is "CANCELLED" or "CLOSED") { return; }
        throw new InvalidOperationException("Expected stopped client work");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task NotDone(Task task)
    { await Task.Delay(30); Check(!task.IsCompleted, "Must retain physical work"); }

    private static async Task Reopen()
    {
        string root; MusicQqFeedbackDeviceSnapshot before;
        await using (var f = new Fixture())
        {
            root = f.Root;
            var account = await f.Login();
            await using var lease = await f.Acquire(account);
            before = lease.Device;
            Check(ReferenceEquals(account, lease.Account) && lease.IsCurrent);
            Check(f.Handler.Steps.SequenceEqual(["auth", "profile", "qimei", "session"]));
            Check(f.Store.Writes == 2 && before.DeviceGeneration == 3 && before.SessionGeneration == 1);
            var document = await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace);
            var raw = JsonNode.Parse(document.Data!)!.AsObject(); CryptographicOperations.ZeroMemory(document.Data!);
            var entries = raw["entries"]!.AsArray();
            Check(entries.Count == 2 && entries.Single(e => e!["key"]!.GetValue<string>() == AccountDocument.Key("tx"))!["value"]!.AsObject().Count == 6);
            var accounts = AccountDocument.Decode(await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace));
            var details = accounts.GetDetails("tx");
            Check(details is not null && details.BelongsTo(accounts.Get("tx")) &&
                details.Rights.Membership.Vip == false, "Login rights remain bound alongside the feedback device");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                Check(!Encoding.UTF8.GetString(bytes).Contains("SYNTHETIC") &&
                    !Encoding.UTF8.GetString(bytes).Contains(before.OpenUdid), "DPAPI ciphertext not plaintext");
            }
        }
        await using var reopened = new Fixture(root);
        var saved = (await reopened.Owner.AcquireAsync("tx"))!;
        Check(saved is not null);
        await using var after = await reopened.Acquire(saved!);
        Check(after.Device.InstallationId == before.InstallationId && after.Device.Qimei36 == before.Qimei36 &&
            after.Device.DeviceGeneration == before.DeviceGeneration && after.Device.SessionGeneration == before.SessionGeneration);
        Check(reopened.Handler.Steps.SequenceEqual(["auth", "profile"]) && reopened.Store.Writes == 0);
        var list = await reopened.Owner.CallAsync("music.accounts.list");
        Check(!list.GetRawText().Contains("SYNTHETIC") && !list.GetRawText().Contains(before.OpenUdid));
    }
    private static async Task Warm()
    {
        await using var f = new Fixture(); var account = await f.Login();
        int events = 0; f.Owner.SessionChanged += _ => events++;
        await using var first = await f.Acquire(account);
        f.Handler.Steps.Clear(); int writes = f.Store.Writes;
        await using var second = await f.Acquire(account);
        Check(first.IsCurrent && second.IsCurrent && events == 0 && f.Store.Writes == writes);
        Check(first.Device.DeviceGeneration == second.Device.DeviceGeneration &&
            first.Device.SessionGeneration == second.Device.SessionGeneration && f.Handler.Steps.SequenceEqual(["auth", "profile"]));
    }
    private static async Task QimeiVector()
    {
        var device = MusicQqClientContext.Create() with { AndroidId = "47801b8a67701e41" };
        var now = new DateTimeOffset(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
        const string key = "0123456789abcdef", nonce = "fedcba9876543210";
        using var request = (HttpRequestMessage)typeof(QqQimeiRecipe).GetMethod("RequestCore", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [device, now, key, nonce, "SYNTHETIC_BEACON", 15])!;
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        var parameters = body["qimeiParams"]!;
        using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(key);
        byte[] plain = aes.DecryptCbc(Convert.FromBase64String(parameters["params"]!.GetValue<string>()), Encoding.ASCII.GetBytes(key), PaddingMode.PKCS7);
        var payload = JsonNode.Parse(plain)!; CryptographicOperations.ZeroMemory(plain);
        var reserved = JsonNode.Parse(payload["reserved"]!.GetValue<string>())!;
        Check(reserved["oz"]!.GetValue<string>() == "NNz6NWlsWoBxTgFbAfWWkqu9Uwu+ha3Zhq6++d6gMR4=");
        Check(reserved["oo"]!.GetValue<string>() == "AB3Bkaa2vuN47x/MquvfUw==");
        Check(payload["qimei"]!.GetValue<string>() == "" && payload["qimei36"]!.GetValue<string>() == "" &&
            payload["imei"]!.GetValue<string>() == device.Imei && payload["sdkVersion"]!.GetValue<string>() == "1.2.13.6" &&
            payload["appVersion"]!.GetValue<string>() == "20.9.0.8" && payload["model"]!.GetValue<string>() == "V2408A");
        Check(reserved["uptimes"]!.GetValue<string>() == "2026-10-23 01:59:45");
        string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
        Check(parameters["sign"]!.GetValue<string>() == Md5(parameters["key"]!.GetValue<string>() +
            parameters["params"]!.GetValue<string>() + now.ToUnixTimeSeconds() * 1000 + nonce + "ZdJqM15EeO2zWc08" +
            """{"appKey":"0AND0HD6FE4HY80F"}"""));
        Check(request.Headers.GetValues("sign").Single() == Md5("qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH" + now.ToUnixTimeSeconds()));
        using var random = QqQimeiRecipe.Request(device, now);
        Check((await random.Content!.ReadAsStringAsync()).Length < 65536);
    }
    private static async Task Anonymous()
    {
        await using var f = new Fixture(); var account = await f.Login();
        await using var a = await f.Acquire(account);
        var first = f.Handler.SessionRequests.Single();
        Check(first["req_0"]!["param"]!["caller"]!.GetValue<int>() == 2 && first["req_0"]!["param"]!["uid"]!.GetValue<string>() == "");
        f.Clock.Advance(TimeSpan.FromHours(24));
        await using var b = await f.Acquire(account);
        var next = f.Handler.SessionRequests.Last();
        Check(next["req_0"]!["param"]!["caller"]!.GetValue<int>() == 1 &&
            next["req_0"]!["param"]!["uid"]!.GetValue<string>() == a.Device.Uid);
        foreach (var body in f.Handler.SessionRequests)
        {
            var comm = body["comm"]!.AsObject();
            Check(!comm.ContainsKey("qq") && !comm.ContainsKey("authst") && !comm.ContainsKey("uid") &&
                !comm.ContainsKey("sid") && !comm.ContainsKey("tmeLoginType") && comm["ct"]!.GetValue<string>() == "11");
            Check(comm["udid"]!.GetValue<string>() == a.Device.OpenUdid && comm["aid"]!.GetValue<string>() == a.Device.AndroidId);
        }
    }
    private static async Task Concurrent()
    {
        await using var f = new Fixture(); var account = await f.Login();
        var tasks = Enumerable.Range(0, 4).Select(_ => f.Acquire(account)).ToArray();
        var leases = await Task.WhenAll(tasks);
        Check(f.Handler.Steps.Count(s => s == "auth") == 4 && f.Handler.Steps.Count(s => s == "qimei") == 1 &&
            f.Handler.Steps.Count(s => s == "session") == 1 && f.Store.Writes == 2);
        foreach (var lease in leases) { Check(lease.IsCurrent && lease.Device.DeviceGeneration == 3); await lease.DisposeAsync(); }
    }
    private static async Task ExactObject()
    {
        await using var f = new Fixture(); var a = await f.Login();
        var clone = new MusicPlatformSession(a.Platform, a.Scope, a.Generation, a.UserId, a.Cookie);
        await Error(async () => await f.Acquire(clone), "SESSION_CHANGED");
        await Error(async () => await f.Acquire(new("wy", a.Scope, a.Generation, a.UserId, a.Cookie)), "INVALID_REQUEST");
        Check(f.Handler.Steps.Count == 0 && f.Store.Writes == 0);
    }
    private static async Task Reject(string fault, string code)
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Handler.Fault = fault;
        await Error(async () => await f.Acquire(a), code);
        var device = await f.Device();
        Check(device.Session is null && !f.Handler.Steps.Contains("report"));
        if (fault.StartsWith("auth") || fault.StartsWith("profile")) Check(f.Store.Writes == 0 && !f.Handler.Steps.Contains("qimei"));
        if (fault.StartsWith("qimei")) Check(device.Qimei is null && !f.Handler.Steps.Contains("session"));
    }
    private static async Task CachedAuthFailure()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a); int writes = f.Store.Writes;
        f.Handler.Fault = "auth-expired";
        await Error(async () => await f.Acquire(a), "AUTH_REQUIRED");
        Check(!old.IsCurrent && old.Revoked.IsCancellationRequested && !f.Owner.IsCurrent(a) && f.Store.Writes == writes);
    }
    private static async Task NumericSession()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Handler.Fault = "session-zero";
        await using var l = await f.Acquire(a); Check(l.Device.Uid == "0");
    }
    private static async Task Midnight()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Clock.Set(new DateTimeOffset(new DateTime(2026, 10, 23, 23, 59, 59), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 23))).ToUniversalTime());
        await using var old = await f.Acquire(a);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Check(old.Revoked.IsCancellationRequested && !old.IsCurrent);
        await using var next = await f.Acquire(a);
        Check(next.Device.SessionGeneration == 2 && next.Device.QimeiRetrievedAt == old.Device.QimeiRetrievedAt &&
            f.Handler.Steps.Count(s => s == "qimei") == 1);
    }
    private static async Task QimeiExpiry()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a);
        f.Clock.Advance(TimeSpan.FromDays(1));
        await using var next = await f.Acquire(a);
        Check(!old.IsCurrent && next.IsCurrent && next.Device.SessionGeneration == 2 && next.Device.DeviceGeneration == 5);
        Check(f.Handler.Steps.Count(s => s == "qimei") == 2 && next.Device.QimeiRetrievedAt > old.Device.QimeiRetrievedAt);
    }
    private static async Task Future()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a);
        f.Clock.Advance(TimeSpan.FromSeconds(-1));
        Check(!old.IsCurrent && old.Revoked.IsCancellationRequested);
        await using var next = await f.Acquire(a);
        Check(next.IsCurrent && next.Device.DeviceGeneration == 5);
    }
    private static async Task Redaction()
    {
        await using var f = new Fixture(); var a = await f.Login();
        using var cancel = new CancellationTokenSource();
        var lease = await f.Acquire(a, cancel.Token);
        Check(JsonSerializer.Serialize<IMusicQqFeedbackContextLease>(lease) == "{}");
        Check(JsonSerializer.Serialize(lease, lease.GetType()) == "{}" && JsonSerializer.Serialize(lease.Device) == "{}");
        Check(!lease.ToString()!.Contains(lease.Device.OpenUdid) && !lease.Device.ToString().Contains(lease.Device.Sid));
        cancel.Cancel(); Check(!lease.IsCurrent && lease.Revoked.IsCancellationRequested);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Check(lease.Revoked.IsCancellationRequested && !lease.IsCurrent);
    }
    private static async Task Logout()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a);
        var result = await f.Owner.CallAsync("music.accounts.logout", new { platform = "tx" });
        Check(result.GetProperty("ok").GetBoolean() && old.Revoked.IsCancellationRequested && !old.IsCurrent);
        Check((await f.Device()).InstallationId == old.Device.InstallationId);
        await Error(async () => await f.Acquire(a), "SESSION_CHANGED");
        var replacement = await f.Login();
        await using var next = await f.Acquire(replacement);
        Check(next.Device.InstallationId == old.Device.InstallationId && next.Device.DeviceGeneration == old.Device.DeviceGeneration);
    }
    private static async Task Switch()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Handler.Hold = "auth";
        var pending = f.Acquire(a); await f.Handler.Arrived.Task;
        var replacement = await f.Login(); // Same identity, new scope/generation; old work has no authority.
        await NotDone(pending); f.Handler.Release.TrySetResult();
        await Error(async () => await pending, "SESSION_CHANGED");
        Check(f.Owner.IsCurrent(replacement) && !f.Handler.Steps.Contains("qimei"));
    }
    private static async Task OtherOwner()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a);
        await using var other = new Fixture(f.Root);
        var result = await other.Owner.CallAsync("music.accounts.logout", new { platform = "tx" });
        Check(result.GetProperty("ok").GetBoolean());
        await Error(async () => await f.Acquire(a), "SESSION_CHANGED");
        Check(!old.IsCurrent && old.Revoked.IsCancellationRequested);
    }
    private static async Task OtherDevice()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a);
        await f.MutateClient(root =>
        {
            var device = root["tx"]!;
            device["generation"] = device["generation"]!.GetValue<long>() + 1;
            device["session"]!["generation"] = device["session"]!["generation"]!.GetValue<long>() + 1;
            device["session"]!["sid"] = "SYNTHETIC_OTHER_SID";
        });
        await f.Clients.Owner.ReloadAsync();
        Check(f.Owner.IsCurrent(a), "Anonymous client observation cannot revoke an account authorization");
        await using var next = await f.Acquire(a);
        Check(old.Revoked.IsCancellationRequested && !old.IsCurrent && next.Device.Sid == "SYNTHETIC_OTHER_SID");
    }
    private static async Task Conflict()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Store.Conflict = true;
        await Error(async () => await f.Acquire(a), "STORAGE_CONFLICT");
        Check(f.Store.Writes == 1 && f.Handler.Steps.Contains("qimei") && !f.Handler.Steps.Contains("session"));
        Check(f.Owner.IsCurrent(a));
        await using var next = await f.Acquire(a);
        Check(next.IsCurrent && (await MusicClientContextFixture.ReadDocument(f.Store.Inner))["wy"]!["nmtid"]!.GetValue<string>() == "SYNTHETIC_CONCURRENT");
    }
    private static async Task Unknown()
    {
        await using var f = new Fixture();
        f.Store.LoseReceipt = true;
        await Error(() => f.Clients.Owner.InitializeAsync(), "COMMIT_UNKNOWN");
        int calls = f.Handler.Steps.Count;
        await Error(() => f.Clients.Owner.InitializeAsync(), "COMMIT_UNKNOWN");
        Check(f.Handler.Steps.Count == calls && calls == 0 && f.Store.Writes == 1);
        f.Store.LoseReceipt = false;
        var listed = await f.Owner.CallAsync("music.accounts.list"); Check(listed.GetProperty("ok").GetBoolean());
        Check(f.Clients.Owner.NeedsReconciliation);
        await f.Clients.Owner.ReloadAsync();
        var a = await f.Login();
        await using var next = await f.Acquire(a);
        Check(next.IsCurrent && f.Store.Writes == 2 && next.Device.DeviceGeneration == 3);
    }
    private static async Task UnknownStage(int stage)
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Store.LoseAt = stage;
        await Error(async () => await f.Acquire(a), "COMMIT_UNKNOWN");
        Check(f.Store.Writes == stage && (stage != 1 || !f.Handler.Steps.Contains("session")));
        int calls = f.Handler.Steps.Count;
        await Error(async () => await f.Acquire(a), "COMMIT_UNKNOWN");
        Check(f.Handler.Steps.Count == calls);
        Check((await f.Owner.CallAsync("music.accounts.list")).GetProperty("ok").GetBoolean());
        Check(f.Owner.IsCurrent(a) && f.Clients.Owner.NeedsReconciliation);
        await Error(async () => await f.Acquire(a), "COMMIT_UNKNOWN");
        Check(f.Handler.Steps.Count == calls);
        f.Store.LoseAt = 0;
        await f.Clients.Owner.ReloadAsync();
        await using var recovered = await f.Acquire(a);
        Check(recovered.IsCurrent && f.Store.Writes == 2 && recovered.Device.DeviceGeneration == 3);
        Check(f.Handler.Steps.Count(s => s == "qimei") == 1 && f.Handler.Steps.Count(s => s == "session") == 1);
    }
    private static async Task SwitchStage(string step)
    {
        await using var f = new Fixture(); var a = await f.Login();
        var before = await f.Device();
        f.Handler.Hold = step;
        var pending = f.Acquire(a); await f.Handler.Arrived.Task;
        var replacement = await f.Login(); // Login resets write counter, not device state.
        f.Handler.Release.TrySetResult();
        await Error(async () => await pending, "SESSION_CHANGED");
        Check(f.Owner.IsCurrent(replacement));
        await using var next = await f.Acquire(replacement);
        Check(next.IsCurrent && next.Device.DeviceGeneration == 3 && next.Device.InstallationId == before.InstallationId &&
            f.Handler.SessionRequests.Count == 1 && f.Store.Writes == (step == "qimei" ? 2 : 1));
    }
    private static async Task Corrupt()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a); int writes = f.Store.Writes;
        await f.MutateClient(root => root["tx"]!["imei"] = "not-an-imei");
        await Error(() => f.Clients.Owner.ReloadAsync(), "CORRUPT_STORAGE");
        await Error(async () => await f.Acquire(a), "CORRUPT_STORAGE");
        Check(old.Revoked.IsCancellationRequested && !old.IsCurrent && f.Store.Writes == writes);
        Check((await f.Owner.CallAsync("music.accounts.list")).GetProperty("ok").GetBoolean() && f.Owner.IsCurrent(a));
    }
    private static async Task CancelWrite()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Store.Hold = true; using var cancel = new CancellationTokenSource();
        var pending = f.Acquire(a, cancel.Token); await f.Store.Arrived.Task;
        var shared = f.Clients.Owner.EnsureQqAsync(f.Clients.Protocol, default);
        cancel.Cancel(); await Cancelled(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await NotDone(shared);
        var read = f.Owner.CallAsync("music.accounts.list");
        Check((await read).GetProperty("ok").GetBoolean() && f.Owner.IsCurrent(a));
        f.Store.Release.TrySetResult();
        await shared.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Store.Writes == 2 && !f.Clients.Owner.NeedsReconciliation && f.Handler.SessionRequests.Count == 1);
        f.Store.Hold = false;
        await using var next = await f.Acquire(a); Check(next.IsCurrent);
    }
    private static async Task CancelHttp(string step)
    {
        await using var f = new Fixture(); var a = await f.Login();
        using var cancel = new CancellationTokenSource(); f.Handler.Hold = step;
        var pending = f.Acquire(a, cancel.Token); await f.Handler.Arrived.Task;
        int writes = f.Store.Writes;
        Task? drain = null;
        Task<MusicQqClientContext>? shared = step == "auth" ? null : f.Clients.Owner.EnsureQqAsync(f.Clients.Protocol, default);
        cancel.Cancel();
        if (shared is null) await NotDone(pending);
        else
        {
            await Cancelled(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            drain = f.Clients.Owner.PrepareShutdownAsync(); await NotDone(drain);
        }
        f.Handler.Release.TrySetResult();
        if (shared is null) await Cancelled(async () => await pending);
        else
        {
            await Stopped(shared); await drain!;
            await f.Clients.Owner.CancelShutdownPreparationAsync();
        }
        Check(f.Store.Writes == writes && (await f.Device()).Session is null);
    }
    private static async Task Deadline()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Handler.Hold = "session";
        var pending = f.Acquire(a); await f.Handler.Arrived.Task;
        var shared = f.Clients.Owner.EnsureQqAsync(f.Clients.Protocol, default);
        f.Clock.Advance(TimeSpan.FromSeconds(25));
        await Error(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)), "TIMEOUT");
        await NotDone(shared);
        f.Handler.Release.TrySetResult();
        await Error(async () => await shared, "TIMEOUT");
        Check((await f.Device()).Session is null);
    }
    private static async Task Close()
    {
        await using var f = new Fixture(); var a = await f.Login();
        f.Handler.Hold = "qimei"; var pending = f.Acquire(a); await f.Handler.Arrived.Task;
        var shared = f.Clients.Owner.EnsureQqAsync(f.Clients.Protocol, default);
        var close = f.Owner.PrepareCloseAsync();
        await Error(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)), "CLOSED");
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        await NotDone(shared);
        var clientClose = f.Clients.Owner.PrepareShutdownAsync(); await NotDone(clientClose);
        f.Handler.Release.TrySetResult(); await Stopped(shared); await clientClose;
        await f.Owner.DisposeAsync();
        Check(f.Store.Disposals == 0 && f.Handler.Disposals == 0);
        using var request = FeedbackRequest(); await f.Http.SendQqFeedbackJsonAsync(request, default);
        Check(f.Handler.Steps.Last() == "track");
    }
    private static async Task Admission()
    {
        await using var f = new Fixture(); var a = await f.Login();
        using var cancel = new CancellationTokenSource(); f.Handler.Hold = "auth";
        var first = f.Acquire(a, cancel.Token); await f.Handler.Arrived.Task;
        var rest = Enumerable.Range(0, 16).Select(_ => f.Acquire(a, cancel.Token)).ToArray();
        cancel.Cancel(); await NotDone(first);
        f.Handler.Release.TrySetResult(); await Cancelled(async () => await first);
        foreach (var t in rest) await Cancelled(async () => await t);
    }
    private static async Task LeaseBudget()
    {
        await using var f = new Fixture(); var a = await f.Login(); var leases = new List<IMusicQqFeedbackContextLease>();
        try
        {
            for (int i = 0; i < 48; i++) leases.Add(await f.Acquire(a));
            await leases[0].DisposeAsync();
            await using var resumed = await f.Acquire(a); Check(resumed.IsCurrent);
        }
        finally { foreach (var l in leases) await l.DisposeAsync(); }
    }
    private static async Task ReadFailure()
    {
        await using var f = new Fixture(); var a = await f.Login();
        await using var old = await f.Acquire(a); f.Handler.Steps.Clear(); f.Store.FailRead = true;
        await Error(async () => await f.Acquire(a), "STORAGE_UNAVAILABLE");
        Check(f.Handler.Steps.Count == 0);
    }

    private static HttpRequestMessage FeedbackRequest(string module = "music.trackInfo.UniformRuleCtrl", string method = "CgiGetTrackInfo") =>
        JsonRequest(new JsonObject { ["comm"] = new JsonObject(), ["req_0"] = new JsonObject {
            ["module"] = module, ["method"] = method, ["param"] = new JsonObject() } });
    private static HttpRequestMessage JsonRequest(JsonObject body) => new(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    private static HttpRequestMessage SessionRequest()
    {
        var comm = new JsonObject();
        foreach (string key in new[] { "ct", "cv", "v", "chid", "tmeAppID", "traceid", "QIMEI36", "OpenUDID", "udid", "OpenUDID2", "aid", "os_ver", "phonetype" })
            comm[key] = key == "ct" ? "11" : "SYNTHETIC_VALUE";
        return JsonRequest(new JsonObject { ["comm"] = comm, ["req_0"] = new JsonObject {
            ["module"] = "music.getSession.session", ["method"] = "GetSession",
            ["param"] = new JsonObject { ["uid"] = "", ["vkey"] = 0, ["caller"] = 2 } } });
    }
    private static HttpRequestMessage ReportRequest()
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write("<synthetic/>"u8);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj") {
            Content = new ByteArrayContent(memory.ToArray()) };
        request.Content.Headers.ContentEncoding.Add("gzip"); return request;
    }
    private static async Task HttpRoutes()
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        _ = await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Qimei, QqQimeiRecipe.Request(MusicQqClientContext.Create(), DateTimeOffset.UtcNow), default);
        _ = await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Account,
            MusicPlatformRequests.TxWithCookie("music.UserInfo.userInfoServer", "GetLoginUserInfo", new(), Cookie), default);
        _ = await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Profile,
            MusicPlatformRequests.TxWithCookie("music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader", new(), Cookie), default);
        _ = await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Session, SessionRequest(), default);
        _ = await http.SendQqFeedbackJsonAsync(FeedbackRequest(), default);
        _ = await http.SendQqFeedbackJsonAsync(FeedbackRequest("music.vkey.GetVkey", "UrlGetVkey"), default);
        Check(Encoding.UTF8.GetString(await http.SendQqFeedbackReportAsync(ReportRequest(), default)) == "{}");
        Check(handler.Calls == 7);
    }
    private static async Task HttpOrdinary()
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        foreach (string platform in new[] { "wy", "tx", "kg" })
        foreach (string url in new[] { "https://api.tencentmusic.com/tme/trpc/proxy", "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj" })
        {
            Check(!MusicPlatformHttp.IsAllowedOrigin(platform, new Uri(url)));
            await Error(async () => await http.SendAsync(platform, new HttpRequestMessage(HttpMethod.Post, url), default), "ORIGIN_DENIED");
        }
        Check(handler.Calls == 0);
    }
    private static async Task HttpDenied(string fault)
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        using var request = FeedbackRequest();
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        string code = "ORIGIN_DENIED";
        switch (fault)
        {
            case "uri": request.RequestUri = new("https://attacker.invalid/cgi-bin/musicu.fcg"); break;
            case "http": request.RequestUri = new("http://u.y.qq.com/cgi-bin/musicu.fcg"); break;
            case "port": request.RequestUri = new("https://u.y.qq.com:444/cgi-bin/musicu.fcg"); break;
            case "fragment": request.RequestUri = new("https://u.y.qq.com/cgi-bin/musicu.fcg#x"); break;
            case "userinfo": request.RequestUri = new("https://synthetic@u.y.qq.com/cgi-bin/musicu.fcg"); break;
            case "method": request.Method = HttpMethod.Get; break;
            case "query": request.RequestUri = new("https://u.y.qq.com/cgi-bin/musicu.fcg?other=1"); break;
            case "host": request.Headers.Host = "attacker.invalid"; break;
            case "module": body["req_0"]!["module"] = "music.pf_song_detail_svr"; break;
            case "pair": body["req_0"]!["method"] = "UrlGetVkey"; break;
            case "batch": body["req_1"] = body["req_0"]!.DeepClone(); code = "INVALID_REQUEST"; break;
            case "duplicate": code = "INVALID_RESPONSE"; break;
        }
        request.Content = new StringContent(fault == "duplicate"
            ? """{"comm":{},"comm":{},"req_0":{}}""" : body.ToJsonString(), Encoding.UTF8, "application/json");
        await Error(async () => await http.SendQqFeedbackJsonAsync(request, default), code);
        Check(handler.Calls == 0);
    }
    private static async Task HttpAnonymous()
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        foreach (string header in new[] { "Cookie", "Authorization" })
        {
            var request = SessionRequest(); request.Headers.TryAddWithoutValidation(header, "SYNTHETIC");
            await Error(async () => await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Session, request, default), "ORIGIN_DENIED");
        }
        foreach (string field in new[] { "qq", "authst", "uid", "sid", "tmeLoginType" })
        {
            using var original = SessionRequest();
            var body = JsonNode.Parse(await original.Content!.ReadAsStringAsync())!.AsObject();
            body["comm"]![field] = "SYNTHETIC";
            await Error(async () => await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Session, JsonRequest(body), default), "INVALID_REQUEST");
        }
        var wrongStep = FeedbackRequest("music.getSession.session", "GetSession");
        await Error(async () => await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Account, wrongStep, default), "ORIGIN_DENIED");
        Check(handler.Calls == 0);
    }
    private static async Task HttpQimei()
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        foreach (string field in new[] { "method", "service", "appid" })
        {
            var request = QqQimeiRecipe.Request(MusicQqClientContext.Create(), DateTimeOffset.UtcNow);
            request.Headers.Add(field, "SYNTHETIC_OTHER");
            await Error(async () => await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Qimei, request, default), "ORIGIN_DENIED");
        }
        var auth = QqQimeiRecipe.Request(MusicQqClientContext.Create(), DateTimeOffset.UtcNow);
        auth.Headers.Add("Cookie", "SYNTHETIC");
        await Error(async () => await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Qimei, auth, default), "ORIGIN_DENIED");
        var extra = QqQimeiRecipe.Request(MusicQqClientContext.Create(), DateTimeOffset.UtcNow);
        var body = JsonNode.Parse(await extra.Content!.ReadAsStringAsync())!.AsObject(); body["extra"] = "SYNTHETIC";
        extra.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        await Error(async () => await http.SendQqDeviceJsonAsync(MusicQqDeviceHttpStep.Qimei, extra, default), "INVALID_REQUEST");
        Check(handler.Calls == 0);
    }
    private static async Task HttpReport()
    {
        foreach (var pair in new[] { (201, "UPSTREAM_ERROR"), (204, "UPSTREAM_ERROR"), (302, "REDIRECT_DENIED"),
            (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"), (429, "RATE_LIMITED"), (500, "UPSTREAM_ERROR") })
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new RawHandler((_, _) => Task.FromResult(Raw("{}", (HttpStatusCode)pair.Item1))));
            await Error(async () => await http.SendQqFeedbackReportAsync(ReportRequest(), default), pair.Item2);
        }
        using var empty = MusicPlatformHttp.CreateWithHandler(new RawHandler((_, _) => Task.FromResult(Raw(""))));
        await Error(async () => await empty.SendQqFeedbackReportAsync(ReportRequest(), default), "INVALID_RESPONSE");
        var missing = ReportRequest(); missing.Content!.Headers.ContentEncoding.Clear();
        await Error(async () => await empty.SendQqFeedbackReportAsync(missing, default), "ORIGIN_DENIED");
        var duplicate = ReportRequest(); duplicate.Content!.Headers.ContentEncoding.Add("gzip");
        await Error(async () => await empty.SendQqFeedbackReportAsync(duplicate, default), "ORIGIN_DENIED");
        using var redirect = MusicPlatformHttp.CreateWithHandler(new RawHandler((r, _) => {
            r.RequestUri = new("https://attacker.invalid/");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = r, Content = new StringContent("{}") }); }));
        await Error(async () => await redirect.SendQqFeedbackReportAsync(ReportRequest(), default), "REDIRECT_DENIED");
    }
    private static async Task HttpLimits()
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        var tooBig = FeedbackRequest(); tooBig.Content = new ByteArrayContent(new byte[65537]);
        await Error(async () => await http.SendQqFeedbackJsonAsync(tooBig, default), "LIMIT");
        var header = FeedbackRequest(); header.Headers.Add("x-synthetic", new string('a', 33000));
        await Error(async () => await http.SendQqFeedbackJsonAsync(header, default), "LIMIT");
        var liar = FeedbackRequest(); liar.Content = new LyingContent();
        await Error(async () => await http.SendQqFeedbackJsonAsync(liar, default), "LIMIT");
        Check(handler.Calls == 0);
        using var response = MusicPlatformHttp.CreateWithHandler(new RawHandler((_, _) => Task.FromResult(Raw(new string(' ', 65537)))));
        await Error(async () => await response.SendQqFeedbackJsonAsync(FeedbackRequest(), default), "LIMIT");
        await Error(async () => await response.SendQqFeedbackReportAsync(ReportRequest(), default), "LIMIT");
    }
    private static async Task HttpCleanup()
    {
        var streams = new List<CleanupStream>();
        using var http = MusicPlatformHttp.CreateWithHandler(new RawHandler((_, _) => {
            var stream = new CleanupStream(); streams.Add(stream);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }); }));
        using var cancel = new CancellationTokenSource();
        var first = http.SendQqFeedbackJsonAsync(FeedbackRequest(), cancel.Token);
        await streams[0].Arrived.Task;
        var second = http.SendQqFeedbackReportAsync(ReportRequest(), cancel.Token);
        await streams[1].Arrived.Task;
        cancel.Cancel(); await NotDone(first); await NotDone(second);
        var third = http.SendAsync("tx", FeedbackRequest(), default);
        for (int i = 0; i < 200 && streams.Count < 3; i++) await Task.Delay(10);
        Check(streams.Count == 3, "a draining request does not block a new one");
        foreach (var s in streams) s.Release.TrySetResult();
        await third;
        await Cancelled(async () => await first); await Cancelled(async () => await second);
    }
    private static async Task HttpProducer()
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        using var cancel = new CancellationTokenSource();
        var producer = new HeldContent(); var request = FeedbackRequest(); request.Content = producer;
        var pending = http.SendQqFeedbackJsonAsync(request, cancel.Token); await producer.Arrived.Task;
        cancel.Cancel(); await NotDone(pending);
        Check(handler.Calls == 0); producer.Release.TrySetResult();
        await Cancelled(async () => await pending);
        Check(handler.Calls == 0 && producer.Disposed);
    }
    private static async Task HttpDeadline()
    {
        var arrived = Signal(); var release = Signal(); CancellationToken physical = default;
        using var http = MusicPlatformHttp.CreateWithHandler(new RawHandler(async (_, token) => {
            physical = token; arrived.TrySetResult(); await release.Task; return Raw("{}"); }));
        var pending = http.SendQqFeedbackReportAsync(ReportRequest(), default); await arrived.Task;
        await Task.Delay(20300);
        Check(physical.IsCancellationRequested); await NotDone(pending); release.TrySetResult();
        await Error(async () => await pending, "TIMEOUT");
    }
    private static async Task HttpRetarget(bool header)
    {
        var handler = new RawHandler((_, _) => Task.FromResult(Raw("{}"))); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        var request = FeedbackRequest(); byte[] bytes = await request.Content!.ReadAsByteArrayAsync();
        request.Content.Dispose();
        request.Content = new MutatingContent(bytes, () => {
            if (header) request.Headers.Host = "attacker.invalid";
            else request.RequestUri = new("https://attacker.invalid/cgi-bin/musicu.fcg");
        });
        await Error(async () => await http.SendQqFeedbackJsonAsync(request, default), "ORIGIN_DENIED");
        Check(handler.Calls == 0, "Retarget denied BEFORE physical network");
    }
    private sealed class MutatingContent(byte[] bytes, Action mutate) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await Task.Yield(); mutate();
            Headers.ContentType = new("application/json");
            await stream.WriteAsync(bytes);
        }
    }
    private sealed class RawHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return action(request, token); }
    }
    private sealed class LyingContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 2; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[65537]).AsTask();
    }
    private sealed class HeldContent : HttpContent
    {
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal();
        internal bool Disposed;
        protected override bool TryComputeLength(out long length) { length = 2; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Arrived.TrySetResult(); await Release.Task; await stream.WriteAsync("{}"u8.ToArray()); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CleanupStream : MemoryStream
    {
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal();
        internal CleanupStream() : base("{}"u8.ToArray()) { }
        public override async ValueTask DisposeAsync()
        { Arrived.TrySetResult(); await Release.Task; await base.DisposeAsync(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root;
        internal readonly TestClock Clock = new();
        internal readonly TestStore Store;
        internal readonly TestHandler Handler;
        internal readonly MusicPlatformHttp Http;
        internal readonly MusicAccountOwner Owner;
        internal readonly MusicClientContextFixture Clients;
        internal Fixture(string? root = null)
        {
            Root = root ?? TestWorkspace.DataPath("qq-context-fixtures", Guid.NewGuid().ToString("N"));
            TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
            Directory.CreateDirectory(Root);
            Store = new(new LocalDocumentStore(Root, [MusicAccountOwner.DocumentNamespace, MusicPlatformClientContextOwner.DocumentNamespace], protectedNamespaces: [MusicAccountOwner.DocumentNamespace, MusicPlatformClientContextOwner.DocumentNamespace]));
            Handler = new(this); Http = MusicPlatformHttp.CreateWithHandler(Handler);
            Clients = new(Http, Store, importAccounts: true, clock: Clock);
            Owner = Clients.CreateAccountOwner(Store);
        }
        internal async Task<MusicPlatformSession> Login()
        {
            await Clients.Owner.InitializeAsync();
            var result = await Owner.CallAsync("music.accounts.import", new { platform = "tx", cookie = Cookie });
            Check(result.GetProperty("ok").GetBoolean(), "Synthetic import");
            var account = await Owner.AcquireAsync("tx"); Check(account is not null);
            Handler.Steps.Clear(); Store.Writes = 0; return account!;
        }
        internal Task<IMusicQqFeedbackContextLease> Acquire(MusicPlatformSession a, CancellationToken token = default) =>
            ((IMusicQqFeedbackContextSource)Owner).AcquireForAccountAsync(a, token).AsTask();
        internal async Task<MusicQqClientContext> Device() => await Clients.Owner.AcquireQqAsync();
        internal async Task<JsonObject> RootJson()
        {
            var doc = await Store.Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
            try { return JsonNode.Parse(doc.Data!)!.AsObject(); }
            finally { CryptographicOperations.ZeroMemory(doc.Data!); }
        }
        internal Task MutateClient(Action<JsonObject> change) => MusicClientContextFixture.MutateDocument(Store.Inner, change);
        public async ValueTask DisposeAsync()
        {
            Handler.Release.TrySetResult(); Store.Release.TrySetResult();
            try { await Owner.DisposeAsync(); }
            finally { await Clients.DisposeAsync(); Http.Dispose(); await Store.DisposeAsync(); }
        }
    }
    private sealed class TestStore(LocalDocumentStore inner) : IDocumentStore
    {
        internal LocalDocumentStore Inner => inner;
        internal int Writes, Disposals, LoseAt;
        internal bool Conflict, LoseReceipt, Hold, FailRead;
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) =>
            FailRead ? throw new DocumentStoreException(DocumentStoreError.Unavailable) : inner.ReadAsync(ns, token);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes);
            if (Hold) { Arrived.TrySetResult(); await Release.Task; token = default; }
            if (Conflict)
            {
                Conflict = false;
                if (ns == MusicPlatformClientContextOwner.DocumentNamespace)
                    await MusicClientContextFixture.ConcurrentClientWrite(inner);
                else
                {
                    var document = await inner.ReadAsync(ns);
                    try
                    {
                        var root = JsonNode.Parse(document.Data!)!;
                        root["entries"]!.AsArray().Add(new JsonObject { ["key"] = AccountDocument.Key("wy"),
                            ["value"] = new JsonObject { ["version"] = 1, ["generation"] = 1, ["cookie"] = null,
                                ["profile"] = null, ["scope"] = null, ["validatedAt"] = null } });
                        byte[] other = JsonSerializer.SerializeToUtf8Bytes(root);
                        try { await inner.WriteAsync(ns, other, document.Fingerprint); }
                        finally { CryptographicOperations.ZeroMemory(other); }
                    }
                    finally { CryptographicOperations.ZeroMemory(document.Data!); }
                }
            }
            var receipt = await inner.WriteAsync(ns, bytes, expected, token);
            if (LoseReceipt || LoseAt != 0 && Writes == LoseAt) throw new DocumentStoreException(DocumentStoreError.Storage);
            return receipt;
        }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    private sealed class TestHandler(Fixture fixture) : HttpMessageHandler
    {
        internal readonly List<string> Steps = [];
        internal readonly List<JsonObject> SessionRequests = [];
        internal string Hold = "", Fault = "";
        internal int Disposals;
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            string method = body["req_0"]?["method"]?.GetValue<string>() ?? "";
            string step = request.RequestUri!.Host == "api.tencentmusic.com" ? "qimei" : method switch {
                "GetLoginUserInfo" => "auth", "GetHomepageHeader" => "profile", "GetSession" => "session",
                "vip_login_base" => "rights",
                "CgiGetTrackInfo" => "track", "UrlGetVkey" => "vkey", _ => throw new InvalidOperationException("Unexpected synthetic route") };
            lock (Steps) Steps.Add(step);
            if (step == Hold) { Hold = ""; Arrived.TrySetResult(); await Release.Task; }
            if (step == "rights")
            {
                Check(body["req_0"]!["module"]!.GetValue<string>() == "VipLogin.VipLoginInter" &&
                    body["comm"]!["qq"]!.GetValue<string>() == UserId &&
                    body["comm"]!["authst"]!.GetValue<string>() == "SYNTHETIC_QQ_CONTEXT_KEY");
                return Envelope(new JsonObject { ["svip"] = 0, ["identity"] = new JsonObject { ["vip"] = 0 } });
            }
            if (step is "auth" or "profile")
            {
                Check(body["comm"]!["qq"]!.GetValue<string>() == UserId &&
                    body["comm"]!["authst"]!.GetValue<string>() == "SYNTHETIC_QQ_CONTEXT_KEY");
                if (step == "auth" && Fault == "auth-expired") return Envelope(new(), 1000);
                if (step == "auth" && Fault == "auth-ssa") { var v = Envelope(new()); v.Headers.Add("ssa-code", "synthetic"); return v; }
                return Envelope(step == "auth" ? new JsonObject { ["code"] = 0 } :
                    new JsonObject { ["Info"] = new JsonObject { ["BaseInfo"] = new JsonObject {
                        ["EncryptedUin"] = Fault == "profile-id" ? "SYNTHETIC_OTHER_EUIN" : "SYNTHETIC_CONTEXT_EUIN",
                        ["Name"] = Fault == "profile-secret" ? "SYNTHETIC_QQ_CONTEXT_KEY" : "合成账号",
                        ["Avatar"] = "https://y.qq.com/synthetic.png" } } });
            }
            if (step is "qimei" or "session")
            {
                var device = await fixture.Device();
                Check(device.Generation > 0 && device.InstallationId.Length > 0, "Device persisted before device HTTP");
                Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"));
                if (step == "qimei")
                {
                    if (Fault == "qimei-duplicate") return Raw("""{"data":"{}","data":"{}"}""");
                    var data = new JsonObject { ["q16"] = "SYNTHETIC_Q16", ["q36"] = "SYNTHETIC_Q36" };
                    if (Fault == "qimei-empty") data["q36"] = "";
                    if (Fault == "qimei-number") data["q36"] = 123;
                    if (Fault == "qimei-missing") data.Remove("q16");
                    if (Fault == "qimei-control") data["q36"] = "bad\nvalue";
                    return Raw(JsonSerializer.Serialize(new { data = new JsonObject { ["data"] = data }.ToJsonString() }),
                        Fault == "qimei-status201" ? HttpStatusCode.Created : HttpStatusCode.OK);
                }
                Check(device.Qimei is not null && body["comm"]!["QIMEI36"]!.GetValue<string>() == device.Qimei.Q36,
                    "QIMEI persisted before GetSession");
                SessionRequests.Add(body.DeepClone().AsObject());
                if (Fault == "session-redirect") return Raw("{}", HttpStatusCode.Found);
                if (Fault == "session-large") return Raw(new string(' ', 65537));
                var session = new JsonObject { ["uid"] = "SYNTHETIC_DEVICE_UID", ["sid"] = "SYNTHETIC_DEVICE_SID" };
                if (Fault == "session-bool") session["uid"] = true;
                if (Fault == "session-float") session["uid"] = 1.5;
                if (Fault == "session-zero") session["uid"] = 0;
                if (Fault == "session-empty") session["sid"] = "";
                return Envelope(new JsonObject { ["session"] = session });
            }
            return Envelope(new());
        }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }
    private static HttpResponseMessage Raw(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Envelope(JsonObject data, int code = 0) => Raw(new JsonObject { ["code"] = 0,
        ["req_0"] = new JsonObject { ["code"] = code, ["data"] = data } }.ToJsonString());

    private sealed class TestClock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
        private readonly List<ClockTimer> timers = [];
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => GetUtcNow().Ticks;
        internal void Set(DateTimeOffset value) => Advance(value - GetUtcNow());
        internal void Advance(TimeSpan span)
        {
            ClockTimer[] due;
            lock (gate) { now += span; due = timers.Where(t => !t.Closed && t.Due <= now).ToArray(); }
            foreach (var t in due) { t.Dispose(); t.Callback(t.State); }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (gate) { timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        private sealed class ClockTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback => callback;
            internal object? State => state;
            internal DateTimeOffset Due;
            internal bool Closed;
            public bool Change(TimeSpan due, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Closed) return false;
                    Due = due == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + due; return true;
                }
            }
            public void Dispose() { lock (owner.gate) { Closed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
