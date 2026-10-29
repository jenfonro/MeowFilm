// SPDX-License-Identifier: GPL-3.0-only
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

/// <summary>Native owner + production recipes + synthetic transport/store. No upstream requests or Node.</summary>
public static class MusicNativeAccountTests
{
    private const string Wy = "MUSIC_U=SYNTHETIC_WY_SESSION; __csrf=SYNTHETIC_CSRF";
    private const string Tx = "musicid=9007199254740997; musickey=SYNTHETIC_TX_SESSION; encrypt_uin=SYNTHETIC_EUIN";
    private const string Kg = "userid=9007199254740997; token=SYNTHETIC_KG_TOKEN; KUGOU_API_MID=SYNTHETIC_MID";
    private const string LargeId = "9007199254740997";
    private static readonly string Root = Path.GetFullPath(TestWorkspace.DataPath("native-account-fixtures"));
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("owner.production.import-all-revalidate-restart-session", AllPlatforms);
        yield return Case("owner.dpapi.real-CAS-reopen-logout-no-plaintext", Dpapi);
        yield return Case("owner.concurrent-platforms-merge-exact-schema", ConcurrentPlatforms);
        yield return Case("owner.logical-revision-same-generation-changed-value-conflict", ValueConflict);
        yield return Case("owner.storage-fingerprint-CAS-no-retry", FingerprintConflict);
        yield return Case("owner.old-auth-failure-cannot-revoke-new-generation", LateFailure);
        yield return Case("owner.cancel-before-commit-no-save", CancelBeforeCommit);
        yield return Case("owner.cancel-after-admission-unknown-explicit-read-waits", CancelAfterCommit);
        yield return Case("owner.logout-failure-revoked-not-durable-deletion", LogoutFailure);
        yield return Case("owner.logout-lost-ack-explicit-read-confirms-deletion", LogoutUnknown);
        yield return Case("owner.close-failure-does-not-dispose-shared-store", PrepareClose);
        yield return Case("owner.issued-session-only-revocation-fences-content", IssuedSession);
        yield return Case("owner.expired-validation-only-current-session-revoked", ExpiredSession);
        yield return Case("storage.unknown-entry-rejected-without-write", UnknownEntry);
        yield return Case("owner.no-memory-fallback-on-store-read-error", StorageFailure);
        yield return Case("owner.profile-secret-reflection-rejected", ProfileReflection);
        yield return Case("owner.observer-failure-cannot-turn-commit-into-error", ObserverFailure);
        yield return Case("owner.invalid-commands-pre-cancel-and-constructor-are-passive", Passive);
        yield return Case("owner.verified-stamp-bound-to-session-not-generation-alone", VerifiedStamp);
        yield return Case("owner.observed-durable-tombstone-fences-issued-lease", DurableTombstone);
        yield return Case("storage.noncanonical-json-number-forms-rejected", NoncanonicalNumbers);
        yield return Case("storage.any-entry-corruption-rejects-whole-document", CorruptDocument);
        foreach (string variant in new[] { "null", "duplicate-root", "duplicate-key", "version", "null-avatar", "bad-profile", "mixed-null", "fraction", "overflow", "fingerprint" })
        { var v = variant; yield return Case("storage.corruption-" + v, () => Corrupt(v)); }
        foreach (string input in new[] { "MUSIC_U=x\r\nHost:x", "Cookie: MUSIC_U=x", "MUSIC_U=x; MUSIC_U=y",
            "MUSIC_U=x; Domain=music.163.com", "MUSIC_U=中", "MUSIC_U=x; constructor=y", "MUSIC_U=a,b", "MUSIC_U=x;; k=y" })
        { var value = input; yield return Case("protocol.cookie-grammar-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8], () => InvalidCookie(value)); }
        foreach (var (status, code) in new[] { (401, "AUTH_EXPIRED"), (403, "FORBIDDEN"), (429, "RATE_LIMITED"), (302, "REDIRECT_DENIED"), (500, "UPSTREAM_ERROR") })
        { int s = status; string c = code; yield return Case("protocol.http-" + s, () => HttpError(s, c)); }
        yield return Case("protocol.wy-unwrapped-status-identity-whitespace-avatar", WyResponse);
        yield return Case("protocol.tx-auth-before-profile-and-explicit-signature-failure", TxAuthentication);
        yield return Case("protocol.tx-alias-and-identity-rejections", TxInputs);
        yield return Case("protocol.kg-device-context-RSA-signature-SSA", KgProtocol);
        yield return Case("protocol.response-budgets-malformed-duplicates-depth", ResponseBudgets);
        yield return Case("protocol.fixed-origin-and-redirect-observation", Origin);
        yield return Case("protocol.timeout-and-real-physical-transport-slots", TransportBudget);
        yield return Case("protocol.no-secret-DTOS-errors-or-ToString", Redaction);
        yield return Case("qr.owner-one-query-per-click-trusted-validate-before-CAS", QrChain);
        yield return Case("qr.kg-requires-native-device-owner-without-network", UnsupportedQr);
        yield return Case("qr.unknown-challenge-expiry-cancel-no-network", QrLifetime);
        yield return Case("qr.cookie-body-is-not-Set-Cookie", QrBodyCookie);
        foreach (string row in new[] { "MUSIC_U=SYNTHETIC_QR; Domain=untrusted.invalid", "MUSIC_U=SYNTHETIC_QR; Max-Age=0",
            "MUSIC_U=SYNTHETIC_QR; Expires=Thu, 01 Jan 1970 00:00:00 GMT", "MUSIC_U=bad,value" })
        { var value = row; yield return Case("qr.bad-cookie-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8], () => QrBadCookie(value)); }
        yield return Case("qr.duplicate-session-cookie-rejected", QrDuplicate);
        yield return Case("qr.authenticated-without-trusted-handoff-rejected", ForgedQr);
        yield return Case("qr.admitted-cancel-handoff-unknown-no-replay", QrUnknown);
        yield return Case("qr.protocol-unsettled-handoff-busy-no-unbounded-save", HandoffBudget);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.native.accounts." + name, run);
    private static void Check(bool condition, string message = "native account assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static string State(JsonElement response, string platform = "wy") =>
        response.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("platform").GetString() == platform).GetProperty("state").GetString()!;
    private static void Ok(JsonElement response) => Check(response.GetProperty("ok").GetBoolean(), "expected successful native command");
    private static void Error(JsonElement response, string code) =>
        Check(!response.GetProperty("ok").GetBoolean() && response.GetProperty("code").GetString() == code,
            "expected " + code + " got " + (response.TryGetProperty("code", out var c) ? c.GetString() : "ok"));
    private static async Task Throws(string code, Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("expected " + code); }
        catch (MusicAccountException e) { Check(e.Code == code, "expected " + code + " got " + e.Code); }
        catch (MusicPlatformException e) { Check(e.Code == code, "expected " + code + " got " + e.Code); }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));
    private static Task<JsonElement> Import(MusicAccountOwner owner, string platform = "wy", string? cookie = null, CancellationToken token = default) =>
        owner.CallAsync("music.accounts.import", new { platform, cookie = cookie ?? (platform switch { "wy" => Wy, "tx" => Tx, _ => Kg }) }, token);
    private static Task<JsonElement> Command(MusicAccountOwner owner, string command, string platform = "wy", CancellationToken token = default) =>
        owner.CallAsync("music.accounts." + command, new { platform }, token);
    private static Task<JsonElement> List(MusicAccountOwner owner) => owner.CallAsync("music.accounts.list");
    private static Task<JsonElement> Poll(MusicAccountOwner owner, string challenge, CancellationToken token = default) =>
        owner.CallAsync("music.accounts.qr.poll", new { platform = "wy", challengeId = challenge }, token);
    private static string Challenge(JsonElement begin) { Ok(begin); return begin.GetProperty("challenge").GetProperty("challengeId").GetString()!; }
    private static HttpResponseMessage Json(string body, int status = 200, params string[] cookies)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (cookies.Length > 0) response.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
        return response;
    }
    private static string WyProfile(string name = "Fixture user", string id = LargeId) =>
        "{\"code\":200,\"profile\":{\"userId\":" + id + ",\"nickname\":" + JsonSerializer.Serialize(name) + "},\"account\":{\"id\":" + id + "}}";
    private static async Task<HttpResponseMessage> Success(HttpRequestMessage request, CancellationToken _)
    {
        string host = request.RequestUri!.Host;
        if (host == "music.163.com") return Json(WyProfile());
        if (host == "interfacepc.music.163.com")
            return request.RequestUri.AbsolutePath.EndsWith("/unikey", StringComparison.Ordinal)
                ? Json("""{"code":200,"unikey":"SYNTHETIC_QR_KEY"}""")
                : Json("""{"code":803}""", 200, "MUSIC_U=SYNTHETIC_QR; Domain=.music.163.com; Path=/; HttpOnly");
        if (host == "u.y.qq.com")
        {
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            return payload["req_0"]!["method"]!.GetValue<string>() == "GetLoginUserInfo"
                ? Json("""{"code":0,"req_0":{"code":0,"data":{"code":0}}}""")
                : Json("""{"code":0,"req_0":{"code":0,"data":{"Info":{"BaseInfo":{"EncryptedUin":"SYNTHETIC_EUIN","Name":"Fixture user"}}}}}""");
        }
        Check(host == "gateway.kugou.com");
        return Json("""{"status":1,"error_code":0,"data":{"nickname":"Fixture user"}}""");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request, cancellationToken); }
    }
    private sealed class MemoryStore : IProtectedDocumentStore
    {
        private readonly object gate = new();
        internal byte[]? Bytes;
        internal int Reads, Writes, Disposals;
        internal bool BadFingerprint;
        internal Func<byte[], DocumentFingerprint, CancellationToken, Task<DocumentFingerprint>>? OnWrite;
        internal Func<Task>? OnRead;
        public async Task<ProtectedDocument> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            Check(ns == MusicAccountOwner.DocumentNamespace);
            Interlocked.Increment(ref Reads);
            if (OnRead is not null) await OnRead();
            lock (gate) return new(Bytes?.ToArray(), BadFingerprint ? DocumentFingerprint.Missing : Fingerprint());
        }
        private DocumentFingerprint Fingerprint() => Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes);
        internal DocumentFingerprint Save(byte[] data, DocumentFingerprint expected)
        {
            lock (gate)
            {
                if (Fingerprint() != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
                Bytes = data.ToArray(); return Fingerprint();
            }
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Check(ns == MusicAccountOwner.DocumentNamespace);
            Interlocked.Increment(ref Writes);
            byte[] owned = data.ToArray();
            try { return OnWrite is null ? Save(owned, expected) : await OnWrite(owned, expected, cancellationToken); }
            finally { CryptographicOperations.ZeroMemory(owned); }
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        internal void Seed(string json) { lock (gate) Bytes = Encoding.UTF8.GetBytes(json); }
        internal JsonObject Document() { lock (gate) return JsonNode.Parse(Bytes!)!.AsObject(); }
        internal JsonObject Row(string platform = "wy") => Document()["entries"]!.AsArray()
            .Single(row => row!["key"]!.GetValue<string>() == Key(platform))!["value"]!.AsObject();
    }
    private sealed class Port : IMusicAccountProtocol
    {
        internal Func<string, string, CancellationToken, Task<MusicAccountProfile>> Validate =
            (p, _, _) => Task.FromResult(new MusicAccountProfile(p, LargeId, "Fixture user"));
        internal Func<string, CancellationToken, Task<MusicAccountChallenge>>? Begin;
        internal Func<string, string, MusicAccountSessionHandoff, CancellationToken, Task<MusicAccountPoll>>? Poll;
        internal int Validations, Disposals;
        public Task<MusicAccountProfile> ValidateAsync(string p, string c, CancellationToken token = default)
        { Validations++; return Validate(p, c, token); }
        public Task<MusicAccountChallenge> BeginAsync(string p, CancellationToken token = default) =>
            Begin?.Invoke(p, token) ?? Task.FromException<MusicAccountChallenge>(new MusicAccountException("UNSUPPORTED"));
        public Task<MusicAccountPoll> PollAsync(string p, string id, MusicAccountSessionHandoff accept, CancellationToken token = default) =>
            Poll?.Invoke(p, id, accept, token) ?? Task.FromException<MusicAccountPoll>(new MusicAccountException("UNSUPPORTED"));
        public Task<MusicAccountSmsSent> SendSmsAsync(MusicAccountSmsInput input, CancellationToken cancellationToken = default) =>
            Task.FromException<MusicAccountSmsSent>(new MusicAccountException("UNSUPPORTED"));
        public Task LoginSmsAsync(MusicAccountSmsInput input, string attemptId,
            MusicAccountSessionHandoff acceptSession, CancellationToken cancellationToken = default) =>
            Task.FromException(new MusicAccountException("UNSUPPORTED"));
        public Task<MusicAccountDetailsObservation> ReadAccountDetailsAsync(
            MusicPlatformSession account, CancellationToken token = default) =>
            Task.FromException<MusicAccountDetailsObservation>(new MusicAccountException("UNSUPPORTED"));
        public Task<MusicAccountChallenge> BeginKgAsync(
            Func<CancellationToken, Task<StoredKgDevice>> acquire,
            Func<StoredKgDevice, string, CancellationToken, Task<StoredKgDevice>> save,
            CancellationToken token) =>
            Task.FromException<MusicAccountChallenge>(new MusicAccountException("UNSUPPORTED"));
        public TimeProvider QqFeedbackClock => TimeProvider.System;
        public Task<MusicAccountProfile> VerifyQqFeedbackAccountAsync(
            MusicPlatformSession account, CancellationToken token) =>
            Task.FromException<MusicAccountProfile>(new MusicAccountException("UNSUPPORTED"));
        public Task<StoredQqQimei> RequestQqQimeiAsync(StoredQqFeedbackDevice device, CancellationToken token) =>
            Task.FromException<StoredQqQimei>(new MusicAccountException("UNSUPPORTED"));
        public Task<StoredQqDeviceSession> RequestQqDeviceSessionAsync(
            StoredQqFeedbackDevice device, CancellationToken token) =>
            Task.FromException<StoredQqDeviceSession>(new MusicAccountException("UNSUPPORTED"));
        public void Cancel(string platform) { }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryStore Store { get; }
        internal Handler Handler { get; }
        internal MusicPlatformHttp Http { get; }
        internal MusicAccountProtocol Protocol { get; }
        internal MusicAccountOwner Owner { get; }
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
            MemoryStore? store = null, TimeSpan? timeout = null)
        {
            Store = store ?? new(); Handler = new(send ?? Success);
            Http = MusicPlatformHttp.CreateWithHandler(Handler);
            Protocol = new(Http, timeout: timeout); Owner = new(Store, Protocol);
        }
        public async ValueTask DisposeAsync() { await Owner.DisposeAsync(); Http.Dispose(); }
    }
    private static string Key(string platform) => $"[\"meowfilm\",\"music\",1,\"platform-account\",\"{platform}\"]";
    private static string CurrentJson(long generation = 7, string cookie = Wy) => JsonSerializer.Serialize(new
    {
        version = 1, entries = new[] { new { key = Key("wy"), value = new { version = 1, generation,
            cookie, profile = new { platform = "wy", userId = LargeId, displayName = "Fixture user" },
            scope = "current-scope", validatedAt = 123L } } }
    });

    private static async Task AllPlatforms()
    {
        await using var f = new Fixture();
        Check(f.Handler.Calls == 0 && f.Store.Reads == 0);
        foreach (var platform in new[] { "wy", "tx", "kg" }) Ok(await Import(f.Owner, platform));
        Check(f.Handler.Calls == 4 && f.Store.Writes == 3);
        var list = await List(f.Owner); Ok(list);
        foreach (var platform in new[] { "wy", "tx", "kg" }) Check(State(list, platform) == "verified");
        var session = (await f.Owner.AcquireAsync("wy", default))!;
        Check(session.UserId == LargeId && session.Cookie == Wy && f.Owner.IsCurrent(session));
        Ok(await Command(f.Owner, "validate"));
        var next = (await f.Owner.AcquireAsync("wy", default))!;
        Check(next.Scope == session.Scope && next.Generation == session.Generation + 1 && !f.Owner.IsCurrent(session));
        await f.Owner.DisposeAsync();
        Check(f.Store.Disposals == 0);
        await using var reopened = new Fixture(store: f.Store);
        Check(State(await List(reopened.Owner)) == "saved");
        var restored = (await reopened.Owner.AcquireAsync("wy", default))!;
        Check(restored.Scope == next.Scope && restored.Generation == next.Generation);
        Ok(await Command(reopened.Owner, "logout"));
        Check(!reopened.Owner.IsCurrent(restored) && await reopened.Owner.AcquireAsync("wy", default) is null);
        Check((await reopened.Owner.AcquireAsync("tx", default))?.Cookie == Tx);
    }
    private static async Task Dpapi()
    {
        string path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(Success));
        await using (var store = new DpapiDocumentStore(path, [MusicAccountOwner.DocumentNamespace]))
        {
            await using var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http));
            Ok(await Import(owner));
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("SYNTHETIC_WY_SESSION", StringComparison.Ordinal));
            var saved = await store.ReadAsync(MusicAccountOwner.DocumentNamespace);
            Check(saved.Fingerprint.Exists); CryptographicOperations.ZeroMemory(saved.Data!);
        }
        await using (var store = new DpapiDocumentStore(path, [MusicAccountOwner.DocumentNamespace]))
        {
            await using var owner = new MusicAccountOwner(store, new MusicAccountProtocol(http));
            Check(State(await List(owner)) == "saved");
            Ok(await Command(owner, "logout"));
            Check(State(await List(owner)) == "none");
            await owner.PrepareCloseAsync();
            Check((await store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint.Exists);
        }
        // Dedicated fixture directory only; no caller path or recursive shell deletion.
        Check(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.Delete(path, recursive: true);
    }
    private static async Task ConcurrentPlatforms()
    {
        await using var f = new Fixture();
        var all = await Task.WhenAll(Import(f.Owner), Import(f.Owner, "tx"), Import(f.Owner, "kg"));
        foreach (var r in all) Ok(r);
        var doc = f.Store.Document();
        Check(doc.Count == 2 && doc["entries"]!.AsArray().Count == 3);
        foreach (var platform in new[] { "wy", "tx", "kg" })
            Check(f.Store.Row(platform).Count == 6 && f.Store.Row(platform)["generation"]!.GetValue<long>() == 1);
    }
    private static async Task ValueConflict()
    {
        var store = new MemoryStore(); store.Seed(CurrentJson());
        var port = new Port(); var arrived = Signal(); var release = Signal();
        port.Validate = async (p, _, _) => { arrived.SetResult(); await release.Task; return new(p, LargeId, "Fixture user"); };
        await using var owner = new MusicAccountOwner(store, port);
        var task = Command(owner, "validate"); await Wait(arrived.Task);
        store.Seed(CurrentJson(cookie: "MUSIC_U=SYNTHETIC_OTHER"));
        release.SetResult(); Error(await task, "STORAGE_CONFLICT"); Check(store.Writes == 0);
    }
    private static async Task FingerprintConflict()
    {
        await using var f = new Fixture();
        f.Store.OnWrite = (_, _, _) => Task.FromException<DocumentFingerprint>(new ProtectedDocumentException(DocumentStoreError.Conflict));
        Error(await Import(f.Owner), "STORAGE_CONFLICT");
        Check(f.Store.Writes == 1 && f.Store.Bytes is null);
        Ok(await List(f.Owner));
    }
    private static async Task LateFailure()
    {
        var store = new MemoryStore(); var port = new Port();
        await using var owner = new MusicAccountOwner(store, port);
        Ok(await Import(owner));
        var start = Signal(); var release = Signal(); int count = 0;
        port.Validate = async (p, _, _) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            { start.SetResult(); await release.Task; throw new MusicAccountException("AUTH_EXPIRED"); }
            return new(p, LargeId, "Replacement user");
        };
        var old = Command(owner, "validate"); await Wait(start.Task);
        Ok(await Import(owner, cookie: "MUSIC_U=SYNTHETIC_NEW"));
        var current = (await owner.AcquireAsync("wy", default))!;
        release.SetResult(); Error(await old, "AUTH_EXPIRED");
        Check(owner.IsCurrent(current) && State(await List(owner)) == "verified");
    }
    private static async Task CancelBeforeCommit()
    {
        var port = new Port(); var arrived = Signal(); var release = Signal();
        port.Validate = async (p, _, _) => { arrived.SetResult(); await release.Task; return new(p, LargeId, "Fixture user"); };
        var store = new MemoryStore(); await using var owner = new MusicAccountOwner(store, port);
        using var cancel = new CancellationTokenSource();
        var task = Import(owner, token: cancel.Token); await Wait(arrived.Task);
        cancel.Cancel(); release.SetResult(); Error(await task, "CANCELLED"); Check(store.Writes == 0);
    }
    private static async Task CancelAfterCommit()
    {
        await using var f = new Fixture();
        var arrived = Signal(); var release = Signal();
        f.Store.OnWrite = async (bytes, expected, _) => { arrived.SetResult(); await release.Task; return f.Store.Save(bytes, expected); };
        using var cancel = new CancellationTokenSource();
        var task = Import(f.Owner, token: cancel.Token); await Wait(arrived.Task);
        cancel.Cancel(); Error(await task, "COMMIT_UNKNOWN");
        Check(await f.Owner.AcquireAsync("wy", default) is null);
        Error(await Import(f.Owner), "COMMIT_UNKNOWN");
        var read = List(f.Owner); Check(!read.IsCompleted);
        release.SetResult(); Check(State(await read) == "saved");
        Check(f.Store.Writes == 1 && (await f.Owner.AcquireAsync("wy", default))!.Cookie == Wy);
    }
    private static async Task LogoutFailure()
    {
        await using var f = new Fixture(); Ok(await Import(f.Owner));
        var session = (await f.Owner.AcquireAsync("wy", default))!;
        f.Store.OnWrite = (_, _, _) => Task.FromException<DocumentFingerprint>(new IOException("SYNTHETIC_SECRET_FAILURE"));
        Error(await Command(f.Owner, "logout"), "COMMIT_UNKNOWN");
        Check(!f.Owner.IsCurrent(session) && await f.Owner.AcquireAsync("wy", default) is null);
        Check(State(await List(f.Owner)) == "revoked");
        f.Store.OnWrite = null; Ok(await Command(f.Owner, "logout")); Check(State(await List(f.Owner)) == "none");
    }
    private static async Task LogoutUnknown()
    {
        await using var f = new Fixture(); Ok(await Import(f.Owner));
        f.Store.OnWrite = (bytes, expected, _) =>
        { f.Store.Save(bytes, expected); throw new IOException("SYNTHETIC_ACK_LOST"); };
        Error(await Command(f.Owner, "logout"), "COMMIT_UNKNOWN");
        Check(State(await List(f.Owner)) == "none" && f.Store.Writes == 2);
    }
    private static async Task PrepareClose()
    {
        var store = new MemoryStore(); var port = new Port();
        await using var owner = new MusicAccountOwner(store, port);
        var arrived = Signal(); var release = Signal();
        store.OnWrite = async (bytes, expected, _) => { arrived.SetResult(); await release.Task; return store.Save(bytes, expected); };
        var save = Import(owner); await Wait(arrived.Task);
        using var token = new CancellationTokenSource(); token.Cancel();
        try { await owner.PrepareCloseAsync(token.Token); throw new InvalidOperationException("expected cancelled close"); }
        catch (OperationCanceledException) { }
        Check(store.Disposals == 0 && port.Disposals == 0);
        release.SetResult(); Error(await save, "COMMIT_UNKNOWN"); await owner.PrepareCloseAsync();
        Check(store.Disposals == 0); await owner.DisposeAsync(); Check(port.Disposals == 1);
    }
    private static async Task IssuedSession()
    {
        await using var f = new Fixture(); Ok(await Import(f.Owner));
        var old = (await f.Owner.AcquireAsync("wy", default))!;
        var forged = new MusicPlatformSession(old.Platform, old.Scope, old.Generation, old.UserId, old.Cookie);
        Check(!f.Owner.IsCurrent(forged) && !f.Owner.Revoke(forged));
        Ok(await Import(f.Owner)); Check(!f.Owner.Revoke(old));
        var current = (await f.Owner.AcquireAsync("wy", default))!;
        Check(f.Owner.Revoke(current) && !f.Owner.IsCurrent(current) && State(await List(f.Owner)) == "revoked");
    }
    private static async Task ExpiredSession()
    {
        bool expired = false;
        await using var f = new Fixture((r, t) => expired ? Task.FromResult(Json("""{"code":200,"profile":null}""")) : Success(r, t));
        Ok(await Import(f.Owner)); var session = (await f.Owner.AcquireAsync("wy", default))!;
        expired = true; Error(await Command(f.Owner, "validate"), "AUTH_EXPIRED");
        Check(!f.Owner.IsCurrent(session) && f.Store.Writes == 1 && State(await List(f.Owner)) == "revoked");
    }
    private static async Task UnknownEntry()
    {
        var store = new MemoryStore(); store.Seed(CurrentJson());
        var doc = store.Document();
        doc["entries"]!.AsArray().Add(new JsonObject { ["key"] = "unsupported-entry", ["value"] = new JsonObject { ["kept"] = true } });
        byte[] original = Encoding.UTF8.GetBytes(doc.ToJsonString());
        store.Bytes = original.ToArray();
        await using var f = new Fixture(store: store);
        Error(await List(f.Owner), "CORRUPT_STORAGE");
        Error(await Import(f.Owner), "CORRUPT_STORAGE");
        Check(f.Handler.Calls == 0 && store.Writes == 0 && store.Bytes!.SequenceEqual(original));
        CryptographicOperations.ZeroMemory(original);
    }
    private static async Task StorageFailure()
    {
        await using var f = new Fixture();
        f.Store.OnRead = () => Task.FromException(new ProtectedDocumentException(DocumentStoreError.Unavailable));
        Error(await Import(f.Owner), "STORAGE_UNAVAILABLE"); Error(await List(f.Owner), "STORAGE_UNAVAILABLE");
        Check(f.Handler.Calls == 0 && f.Store.Writes == 0);
    }
    private static async Task ProfileReflection()
    {
        var port = new Port { Validate = (p, _, _) => Task.FromResult(new MusicAccountProfile(p, LargeId, "SYNTHETIC<SECRET")) };
        var store = new MemoryStore(); await using var owner = new MusicAccountOwner(store, port);
        Error(await Import(owner, cookie: "MUSIC_U=SYNTHETIC<SECRET"), "INVALID_RESPONSE"); Check(store.Writes == 0);
    }
    private static async Task ObserverFailure()
    {
        await using var f = new Fixture(); f.Owner.SessionChanged += _ => throw new Exception("SYNTHETIC_OBSERVER_SECRET");
        Ok(await Import(f.Owner)); Check(State(await List(f.Owner)) == "verified");
    }
    private static async Task Passive()
    {
        await using var f = new Fixture();
        using var token = new CancellationTokenSource(); token.Cancel();
        Error(await Import(f.Owner, token: token.Token), "CANCELLED");
        Error(await f.Owner.CallAsync("music.accounts.import", new { platform = "wy", cookie = Wy, url = "https://untrusted.invalid" }), "INVALID_REQUEST");
        Error(await f.Owner.CallAsync("music.accounts.remoteLogout", new { platform = "wy" }), "INVALID_REQUEST");
        Check(f.Handler.Calls == 0 && f.Store.Reads == 0);
        await f.Owner.PrepareCloseAsync();
        Error(await List(f.Owner), "CLOSED"); Check(f.Store.Disposals == 0);
    }
    private static async Task VerifiedStamp()
    {
        await using var f = new Fixture(); Ok(await Import(f.Owner));
        // No Acquire has been called yet: the verified stamp must still identify the exact saved session.
        var doc = f.Store.Document(); doc["entries"]![0]!["value"]!["scope"] = "different-scope";
        f.Store.Seed(doc.ToJsonString());
        Check(State(await List(f.Owner)) == "saved");
        var session = (await f.Owner.AcquireAsync("wy", default))!; Check(f.Owner.IsCurrent(session));
    }
    private static async Task DurableTombstone()
    {
        await using var f = new Fixture(); Ok(await Import(f.Owner));
        var old = (await f.Owner.AcquireAsync("wy", default))!; int changed = 0;
        f.Owner.SessionChanged += _ => changed++;
        // A cooperating second process can persist a logout. IsCurrent must stop
        // authorizing the old lease as soon as Acquire observes that tombstone.
        var doc = f.Store.Document(); var row = doc["entries"]![0]!["value"]!;
        row["generation"] = old.Generation + 1;
        foreach (string name in new[] { "cookie", "profile", "scope", "validatedAt" }) row[name] = null;
        f.Store.Seed(doc.ToJsonString());
        Check(await f.Owner.AcquireAsync("wy", default) is null);
        Check(!f.Owner.IsCurrent(old) && changed == 1 && State(await List(f.Owner)) == "none");
    }
    private static async Task NoncanonicalNumbers()
    {
        var store = new MemoryStore();
        string json = CurrentJson().Replace("\"version\":1", "\"version\":1.0", StringComparison.Ordinal)
            .Replace("\"generation\":7", "\"generation\":7e0", StringComparison.Ordinal);
        byte[] original = Encoding.UTF8.GetBytes(json);
        store.Bytes = original.ToArray();
        await using var f = new Fixture(store: store);
        Error(await List(f.Owner), "CORRUPT_STORAGE");
        Error(await Import(f.Owner), "CORRUPT_STORAGE");
        Check(f.Handler.Calls == 0 && store.Writes == 0 && store.Bytes!.SequenceEqual(original));
        CryptographicOperations.ZeroMemory(original);
    }
    private static async Task CorruptDocument()
    {
        var store = new MemoryStore(); store.Seed(CurrentJson());
        var doc = store.Document(); doc["entries"]!.AsArray().Add(new JsonObject { ["key"] = Key("kg"), ["value"] = null });
        byte[] original = Encoding.UTF8.GetBytes(doc.ToJsonString());
        store.Bytes = original.ToArray();
        await using var f = new Fixture(store: store);
        Error(await Import(f.Owner), "CORRUPT_STORAGE");
        Error(await List(f.Owner), "CORRUPT_STORAGE");
        Check(f.Handler.Calls == 0 && store.Writes == 0 && store.Bytes!.SequenceEqual(original));
        CryptographicOperations.ZeroMemory(original);
    }
    private static async Task Corrupt(string variant)
    {
        var store = new MemoryStore(); store.Seed(CurrentJson());
        var doc = store.Document(); var row = doc["entries"]![0]!["value"]!;
        switch (variant)
        {
            case "null": store.Seed("null"); break;
            case "duplicate-root": store.Seed("""{"version":1,"version":1,"entries":[]}"""); break;
            case "duplicate-key": doc["entries"]!.AsArray().Add(doc["entries"]![0]!.DeepClone()); store.Seed(doc.ToJsonString()); break;
            case "version": row["version"] = 2; store.Seed(doc.ToJsonString()); break;
            case "null-avatar": row["profile"]!["avatar"] = null; store.Seed(doc.ToJsonString()); break;
            case "bad-profile": row["profile"]!["platform"] = "kg"; store.Seed(doc.ToJsonString()); break;
            case "mixed-null": row["cookie"] = null; store.Seed(doc.ToJsonString()); break;
            case "fraction": row["generation"] = 1.5; store.Seed(doc.ToJsonString()); break;
            case "overflow": row["generation"] = 9007199254740992L; store.Seed(doc.ToJsonString()); break;
            case "fingerprint": store.BadFingerprint = true; break;
        }
        await using var f = new Fixture(store: store);
        Error(await List(f.Owner), "CORRUPT_STORAGE"); Error(await Import(f.Owner), "CORRUPT_STORAGE");
        Check(f.Handler.Calls == 0 && store.Writes == 0);
    }
    private static async Task InvalidCookie(string value)
    {
        await using var f = new Fixture();
        Error(await Import(f.Owner, cookie: value), "INVALID_CREDENTIALS");
        Check(f.Handler.Calls == 0 && f.Store.Writes == 0);
    }
    private static async Task HttpError(int status, string code)
    {
        await using var f = new Fixture((_, _) => Task.FromResult(Json("""{"secret":"SYNTHETIC_RAW_SECRET"}""", status)));
        Error(await Import(f.Owner), code); Check(f.Store.Writes == 0);
    }
    private static async Task WyResponse()
    {
        string body = """{"code":200,"profile":{"userId":18446744073709551615,"nickname":"  ","avatarUrl":"http://127.0.0.1/a"}}""";
        await using var f = new Fixture((r, _) =>
        {
            Check(r.RequestUri!.AbsolutePath == "/weapi/w/nuser/account/get" && r.Headers.GetValues("Cookie").Single() == Wy);
            return Task.FromResult(Json(body));
        });
        Ok(await Import(f.Owner));
        Check(f.Store.Row()["profile"]!["userId"]!.GetValue<string>() == "18446744073709551615" &&
            f.Store.Row()["profile"]!["displayName"]!.GetValue<string>() == "  " && f.Store.Row()["profile"]!["avatar"] is null);
        body = """{"code":200,"profile":{"userId":1,"nickname":"Fixture"},"account":{"id":2}}""";
        Error(await Import(f.Owner), "IDENTITY_MISMATCH");
        body = """{"code":200,"data":{"profile":{"userId":1,"nickname":"Fixture"}}}""";
        Error(await Import(f.Owner), "INVALID_RESPONSE");
        body = """{"code":460}"""; Error(await Import(f.Owner), "VERIFICATION_REQUIRED");
    }
    private static async Task TxAuthentication()
    {
        int calls = 0;
        await using var f = new Fixture(async (request, _) =>
        {
            calls++; var data = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Check(request.RequestUri!.Host == "u.y.qq.com" && !request.Headers.Contains("Cookie"));
            Check(data["comm"]!["qq"]!.GetValue<string>() == LargeId && data["comm"]!["authst"]!.GetValue<string>() == "SYNTHETIC_TX_SESSION");
            Check(data["req_0"]!["method"]!.GetValue<string>() == "GetLoginUserInfo");
            return Json("""{"code":2000}""");
        });
        Error(await Import(f.Owner, "tx"), "SIGNATURE_REQUIRED"); Check(calls == 1 && f.Store.Writes == 0);
    }
    private static async Task TxInputs()
    {
        await using var f = new Fixture();
        foreach (string cookie in new[] { "musicid=1; musickey=SYNTHETIC", Tx + "; str_musicid=2",
            Tx + "; encryptUin=DIFFERENT", Tx + "; login_type=1; loginType=2", Tx + "; login_type=0" })
            Error(await Import(f.Owner, "tx", cookie), "INVALID_CREDENTIALS");
        Check(f.Handler.Calls == 0);
        await using var wrong = new Fixture(async (r, t) =>
        {
            var response = await Success(r, t);
            string text = await response.Content.ReadAsStringAsync();
            response.Content = new StringContent(text.Replace("SYNTHETIC_EUIN", "WRONG", StringComparison.Ordinal));
            return response;
        });
        Error(await Import(wrong.Owner, "tx"), "IDENTITY_MISMATCH");
    }
    private static async Task KgProtocol()
    {
        string expectedToken = "SYNTHETIC_KG_TOKEN";
        await using var f = new Fixture(async (r, _) =>
        {
            Check(r.RequestUri!.Host == "gateway.kugou.com" && r.RequestUri.AbsolutePath == "/v3/get_my_info");
            Check(r.Headers.GetValues("x-router").Single() == "usercenter.kugou.com" && !r.Headers.Contains("Cookie"));
            var bodyText = await r.Content!.ReadAsStringAsync(); var body = JsonNode.Parse(bodyText)!;
            Check(body["userid"]!.ToJsonString() == LargeId && body["usertype"]!.GetValue<int>() == 1);
            var query = ParseForm(r.RequestUri.Query[1..]);
            string signature = query["signature"]; query.Remove("signature");
            const string salt = "OIlwieks28dk2k092lksi2UIkp";
            string material = salt + string.Concat(query.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + bodyText + salt;
            Check(signature == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(material))));
            Check(query["token"] == expectedToken && query["mid"] == "SYNTHETIC_MID");
            const string key = """
                -----BEGIN PUBLIC KEY-----
                MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB
                -----END PUBLIC KEY-----
                """;
            using var rsa = RSA.Create(); rsa.ImportFromPem(key); var pub = rsa.ExportParameters(false);
            var plaintext = Encoding.UTF8.GetBytes("{\"token\":\"" + expectedToken + "\",\"clienttime\":" + body["visit_time"]!.ToJsonString() + "}");
            byte[] block = new byte[128]; plaintext.CopyTo(block, 0);
            var expected = BigInteger.ModPow(new(block, true, true), new(pub.Exponent!, true, true), new(pub.Modulus!, true, true));
            Check(body["p"]!.GetValue<string>() == Convert.ToHexString(expected.ToByteArray(true, true)).PadLeft(256, '0'));
            return Json("""{"status":1,"data":{"nickname":"Fixture user"}}""");
        });
        Ok(await Import(f.Owner, "kg"));
        expectedToken = "SYNTHETIC_KG_<&>'TOKEN";
        Ok(await Import(f.Owner, "kg", "userid=" + LargeId + "; token=" + expectedToken + "; KUGOU_API_MID=SYNTHETIC_MID"));
        Error(await Import(f.Owner, "kg", "userid=1; token=SYNTHETIC"), "DEVICE_CONTEXT_REQUIRED");
        Error(await Import(f.Owner, "kg", "userid=1; token=" + new string('x', 200) + "; KUGOU_API_MID=SYNTHETIC"), "INVALID_CREDENTIALS");
        await using var ssa = new Fixture((_, _) => { var r = Json("""{"status":1,"data":{}}"""); r.Headers.Add("ssa-code", "required"); return Task.FromResult(r); });
        Error(await Import(ssa.Owner, "kg"), "VERIFICATION_REQUIRED");
    }
    private static async Task ResponseBudgets()
    {
        string body = """{"code":200,"code":200}""";
        await using var f = new Fixture((_, _) => Task.FromResult(Json(body)));
        Error(await Import(f.Owner), "INVALID_RESPONSE");
        body = new string('x', 1024 * 1024 + 1); Error(await Import(f.Owner), "LIMIT");
        body = "{"; Error(await Import(f.Owner), "INVALID_RESPONSE");
        body = "{\"code\":200,\"nested\":" + new string('[', 26) + "0" + new string(']', 26) + "}";
        Error(await Import(f.Owner), "LIMIT");
        body = """{"code":200,"__proto__":{}}"""; Error(await Import(f.Owner), "INVALID_RESPONSE");
    }
    private static async Task Origin()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("{}")));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await Throws("ORIGIN_DENIED", () => http.SendAsync("wy", new(HttpMethod.Get, "https://untrusted.invalid"), default));
        Check(handler.Calls == 0);
        await using var f = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, "https://untrusted.invalid"), Content = new StringContent(WyProfile()) }));
        Error(await Import(f.Owner), "REDIRECT_DENIED");
    }
    private static async Task TransportBudget()
    {
        var release = Signal(); var first = Signal();
        var handler = new Handler(async (_, _) => { first.TrySetResult(); await release.Task; return Json(WyProfile()); });
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http, timeout: TimeSpan.FromMilliseconds(60));
        await Throws("TIMEOUT", () => protocol.ValidateAsync("wy", Wy)); await Wait(first.Task);
        await Throws("TIMEOUT", () => protocol.ValidateAsync("wy", Wy));
        await Throws("RATE_LIMITED", () => protocol.ValidateAsync("wy", Wy));
        Check(handler.Calls == 2); release.SetResult();
    }
    private static async Task Redaction()
    {
        await using var f = new Fixture((_, _) => throw new IOException("SYNTHETIC_UPSTREAM_SECRET"));
        var result = await Import(f.Owner); Error(result, "NETWORK_ERROR");
        Check(!result.GetRawText().Contains("SYNTHETIC", StringComparison.Ordinal));
        var handoff = new MusicAccountHandoff("wy", Guid.NewGuid().ToString(), Wy, new("wy", LargeId, "Fixture"));
        var session = new MusicPlatformSession("wy", "scope", 1, LargeId, Wy);
        Check(JsonSerializer.Serialize(handoff) == "{}" && JsonSerializer.Serialize(session) == "{}");
        Check(!handoff.ToString().Contains("SYNTHETIC") && !session.ToString().Contains("SYNTHETIC"));
        Check(new MusicAccountException("SYNTHETIC_ERROR").Message == "ACCOUNT_FAILED");
    }
    private static async Task QrChain()
    {
        int status = 801, requests = 0;
        await using var f = new Fixture(async (r, t) =>
        {
            requests++;
            if (r.RequestUri!.Host == "interfacepc.music.163.com")
            {
                var form = ParseForm(await r.Content!.ReadAsStringAsync()); var wire = Convert.FromHexString(form["params"]);
                using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
                string clear = Encoding.UTF8.GetString(aes.DecryptEcb(wire, PaddingMode.PKCS7));
                string[] pieces = clear.Split("-36cd479b6b5-");
                Check(pieces.Length == 3 && pieces[2] == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("nobody" + pieces[0] + "use" + pieces[1] + "md5forencrypt"))));
                var payload = JsonNode.Parse(pieces[1])!;
                Check(payload["type"]!.GetValue<int>() == 3 && payload["header"]!["MUSIC_U"] is null);
                if (r.RequestUri.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
                    return Json("{\"code\":" + status + "}", 200, status == 803 ? ["MUSIC_U=SYNTHETIC_QR; Domain=.music.163.com; Path=/"] : []);
            }
            return await Success(r, t);
        });
        string id = Challenge(await Command(f.Owner, "qr.begin")); Check(requests == 1 && f.Store.Writes == 0);
        var waiting = await Poll(f.Owner, id); Ok(waiting); Check(waiting.GetProperty("status").GetString() == "waiting" && requests == 2);
        await Task.Delay(40); Check(requests == 2); // No background loop.
        status = 802; var confirming = await Poll(f.Owner, id); Ok(confirming); Check(confirming.GetProperty("status").GetString() == "confirming");
        status = 803; var success = await Poll(f.Owner, id); Ok(success);
        Check(success.GetProperty("status").GetString() == "authenticated" && requests == 5 && f.Store.Writes == 1);
        Check(!success.GetRawText().Contains("SYNTHETIC_QR") && (await f.Owner.AcquireAsync("wy", default))!.Cookie == "MUSIC_U=SYNTHETIC_QR");
        Check((await Poll(f.Owner, id)).GetProperty("status").GetString() == "expired" && requests == 5);
    }
    private static async Task UnsupportedQr()
    {
        await using var f = new Fixture();
        await Throws("DEVICE_CONTEXT_REQUIRED", () => f.Protocol.BeginAsync("kg"));
        Check(f.Handler.Calls == 0 && f.Store.Reads == 0 && f.Store.Writes == 0);
    }
    private sealed class Clock : TimeProvider
    {
        internal TimeSpan Offset;
        private readonly long start = TimeProvider.System.GetTimestamp();
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() => start + (long)(Offset.TotalSeconds * TimestampFrequency);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }
    private static async Task QrLifetime()
    {
        var handler = new Handler(Success); using var http = MusicPlatformHttp.CreateWithHandler(handler); var clock = new Clock();
        await using var protocol = new MusicAccountProtocol(http, clock: clock);
        var begin = await protocol.BeginAsync("wy"); Check(handler.Calls == 1);
        Check((await protocol.PollAsync("wy", Guid.NewGuid().ToString(), (_, _) => Task.FromResult(true))).Status == "expired");
        clock.Offset = TimeSpan.FromSeconds(181);
        Check((await protocol.PollAsync("wy", begin.ChallengeId, (_, _) => Task.FromResult(true))).Status == "expired" && handler.Calls == 1);
        var second = await protocol.BeginAsync("wy"); protocol.Cancel("wy");
        Check((await protocol.PollAsync("wy", second.ChallengeId, (_, _) => Task.FromResult(true))).Status == "expired" && handler.Calls == 2);
    }
    private static async Task QrBodyCookie()
    {
        await using var f = new Fixture((r, t) => r.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
            ? Task.FromResult(Json("""{"code":803,"cookie":"MUSIC_U=SYNTHETIC_WRAPPER"}""")) : Success(r, t));
        string id = Challenge(await Command(f.Owner, "qr.begin"));
        Error(await Poll(f.Owner, id), "INVALID_RESPONSE"); Check(f.Store.Writes == 0 && f.Handler.Calls == 2);
    }
    private static async Task QrBadCookie(string value)
    {
        await using var f = new Fixture((r, t) => r.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
            ? Task.FromResult(Json("""{"code":803}""", 200, value)) : Success(r, t));
        string id = Challenge(await Command(f.Owner, "qr.begin"));
        var response = await Poll(f.Owner, id);
        Check(!response.GetProperty("ok").GetBoolean() && f.Store.Writes == 0 && f.Handler.Calls == 2);
    }
    private static async Task QrDuplicate()
    {
        await using var f = new Fixture((r, t) => r.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
            ? Task.FromResult(Json("""{"code":803}""", 200, "MUSIC_U=SYNTHETIC_A", "MUSIC_U=SYNTHETIC_B")) : Success(r, t));
        Error(await Poll(f.Owner, Challenge(await Command(f.Owner, "qr.begin"))), "INVALID_RESPONSE");
        Check(f.Store.Writes == 0);
    }
    private static async Task ForgedQr()
    {
        var port = new Port
        {
            Begin = (p, _) => Task.FromResult(new MusicAccountChallenge(p, Guid.NewGuid().ToString(), "https://music.163.com/login?codekey=SYNTHETIC", DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeMilliseconds())),
            Poll = (p, id, _, _) => Task.FromResult(new MusicAccountPoll(p, id, "authenticated"))
        };
        var store = new MemoryStore(); await using var owner = new MusicAccountOwner(store, port);
        Error(await Poll(owner, Challenge(await Command(owner, "qr.begin"))), "HANDOFF_REJECTED");
        Check(store.Writes == 0);
    }
    private static async Task QrUnknown()
    {
        await using var f = new Fixture(); var arrived = Signal(); var release = Signal();
        f.Store.OnWrite = async (bytes, expected, _) => { arrived.SetResult(); await release.Task; return f.Store.Save(bytes, expected); };
        string id = Challenge(await Command(f.Owner, "qr.begin"));
        using var cancel = new CancellationTokenSource(); var poll = Poll(f.Owner, id, cancel.Token); await Wait(arrived.Task);
        cancel.Cancel(); Error(await poll, "HANDOFF_UNCONFIRMED");
        Check((await Poll(f.Owner, id)).GetProperty("status").GetString() == "expired");
        var read = List(f.Owner); Check(!read.IsCompleted); release.SetResult();
        Check(State(await read) == "saved" && f.Store.Writes == 1);
    }
    private static async Task HandoffBudget()
    {
        var handler = new Handler(Success); using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var protocol = new MusicAccountProtocol(http);
        var arrived = Signal(); var release = Signal();
        Task<bool> Accept(MusicAccountHandoff _, CancellationToken __) => Run();
        async Task<bool> Run() { arrived.TrySetResult(); await release.Task; return true; }
        string id = (await protocol.BeginAsync("wy")).ChallengeId;
        using var cancel = new CancellationTokenSource();
        var poll = protocol.PollAsync("wy", id, Accept, cancel.Token); await Wait(arrived.Task); cancel.Cancel();
        await Throws("HANDOFF_UNCONFIRMED", () => poll);
        string next = (await protocol.BeginAsync("wy")).ChallengeId;
        await Throws("BUSY", () => protocol.PollAsync("wy", next, Accept));
        release.SetResult();
    }
    private static Dictionary<string, string> ParseForm(string input) => input.Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
}
