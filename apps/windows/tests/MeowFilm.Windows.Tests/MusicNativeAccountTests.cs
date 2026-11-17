// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Windows.Tests;
public static class MusicNativeAccountTests
{
    private const string Wy = "MUSIC_U=SYNTHETIC_WY_SESSION; __csrf=SYNTHETIC_CSRF";
    private const string Tx = "musicid=9007199254740997; musickey=SYNTHETIC_TX_SESSION; encrypt_uin=SYNTHETIC_EUIN";
    private const string Kg = "userid=9007199254740997; token=SYNTHETIC_KG_TOKEN; KUGOU_API_MID=123456789";
    private const string LargeId = "9007199254740997";
    private static readonly string Root = Path.GetFullPath(TestWorkspace.DataPath("native-account-fixtures"));
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("owner.canonical-import-all-revalidate-restart-session", AllPlatforms);
        yield return Case("owner.dpapi.real-CAS-reopen-logout-no-plaintext", () => Dpapi(Wy));
        yield return Case("owner.long-canonical-cookie-DPAPI-current-reopen-preserves-value", () => Dpapi(Wy + new string('x', 20_000)));
        yield return Case("owner.authenticated-cookie-not-input-is-DPAPI-saved-and-cold-restored",
            () => Dpapi(Wy, Wy + "; deviceId=SYNTHETIC_NODE_DEVICE"));
        yield return Case("owner.concurrent-platforms-merge-current-schema", ConcurrentPlatforms);
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
        foreach (string code in new[] { "AUTH_EXPIRED", "INVALID_CREDENTIALS", "IDENTITY_MISMATCH" })
        {
            string failure = code;
            yield return Case("owner.validation-" + failure + "-revokes-current-not-durable-cookie", () => ExpiredSession(failure, failure));
        }
        yield return Case("owner.ordinary-validation-failure-keeps-saved-authority-without-credential-write", OrdinaryValidation);
        yield return Case("owner.failed-candidate-does-not-revoke-or-replace-existing-account", CandidateFailure);
        yield return Case("storage.unknown-entry-survives-account-write", () => UnknownEntry("unsupported-entry", new JsonObject { ["kept"] = true }));
        yield return Case("storage.account-document-beyond-eight-MiB-preserves-unedited-entry",
            () => UnknownEntry("large-entry", new JsonObject { ["text"] = new string('x', 8 * 1024 * 1024 + 1) }));
        yield return Case("credential-cookie-preserves-more-than-sixty-four-pairs", () =>
        {
            string cookie = string.Join("; ", Enumerable.Range(0, 65).Select(i => $"part{i}={new string('x', 256)}"));
            var fields = AccountRules.Cookies(cookie);
            Check(fields.Count == 65 && fields["part64"] == new string('x', 256));
            return Task.CompletedTask;
        });
        yield return Case("owner.no-memory-fallback-on-store-read-error", StorageFailure);
        yield return Case("owner.canonical-profile-rejection-never-saves", ProfileReflection);
        yield return Case("owner.observer-failure-cannot-turn-commit-into-error", ObserverFailure);
        yield return Case("owner.invalid-commands-pre-cancel-and-constructor-are-passive", Passive);
        yield return Case("owner.verified-stamp-bound-to-session-not-generation-alone", VerifiedStamp);
        yield return Case("owner.observed-durable-tombstone-fences-issued-lease", DurableTombstone);
        yield return Case("storage.equivalent-json-number-forms-consumed", NoncanonicalNumbers);
        yield return Case("storage.unrelated-account-corruption-does-not-block-selected-account", CorruptDocument);
        foreach (string variant in new[] { "null", "duplicate-root", "duplicate-key", "version", "null-avatar", "bad-profile", "mixed-null", "fraction", "overflow", "fingerprint" })
        { string v = variant; yield return Case("storage.current-corruption-" + v, () => Corrupt(v)); }
        foreach (string input in new[] { "MUSIC_U=x\r\nHost:x", "Cookie: MUSIC_U=x", "MUSIC_U=x; MUSIC_U=y", "MUSIC_U=x; Domain=music.163.com",
            "MUSIC_U=中", "MUSIC_U=x; constructor=y", "MUSIC_U=a,b", "MUSIC_U=x;; k=y" })
        { string value = input; yield return Case("owner.canonical-credential-rejection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8], () => InvalidCookie(value)); }
        yield return Case("owner.import-ignores-unconsumed-parameters-without-changing-candidate", async () => {
            await using var f = new Fixture((call, token) => {
                Check(call.Operation == "account.verify" && call.Payload.GetProperty("candidate").GetProperty("cookie").GetString() == Wy);
                Check(!call.Parameters.EnumerateObject().Any());
                return Success(call, token);
            });
            Ok(await f.Owner.CallAsync("music.accounts.import", new { platform = "wy", cookie = Wy,
                extra = new { url = "https://unused.invalid", headers = new { cookie = "unused" } } }));
            Check(f.Store.Writes == 1 && f.Runtime.Requests.Count(call => call.Operation == "account.verify") == 1);
        });
        yield return Case("owner.redacted-canonical-errors-and-private-DTOs", Redaction);
        yield return Case("qr.authenticated-without-trusted-handoff-rejected", ForgedQr);
        yield return Case("qr.canonical-state-not-native-display-TTL-controls-handoff", QrExpiry);
        yield return Case("qr.admitted-cancel-handoff-unknown-no-replay", QrUnknown);
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
    private static MusicAccountDetailsObservation Observation(MusicAccountProfile profile)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new(profile, now, new(new(false, "none", null), now, now + 86400000L, []));
    }
    private static Task<JsonElement> Success(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
    {
        if (call.Operation == "account.verify")
        {
            var verified = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(call.Platform, LargeId, displayName: "Fixture user").GetRawText())!.AsObject();
            verified["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(verified));
        }
        if (call.Operation == "account.profile")
            return Task.FromResult(MusicPlatformRuntimeFixture.Observation(call.Platform, LargeId, displayName: "Fixture user"));
        if (call.Operation == "qr.create") return Task.FromResult(MusicPlatformRuntimeFixture.Json(new {
            challengeId = call.Parameters.GetProperty("challengeId").GetString(), url = "https://qr.invalid/synthetic",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeMilliseconds(), imageBase64 = (string?)null }));
        if (call.Operation == "qr.cancel") return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { cancelled = true }));
        if (call.Operation == "qr.poll")
        {
            var account = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(call.Platform, LargeId).GetRawText())!.AsObject();
            account["cookie"] = Wy;
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status = "authenticated", account }));
        }
        throw new InvalidOperationException("Unexpected account operation: " + call.Operation);
    }
    private sealed class MemoryStore : IDocumentStore
    {
        private readonly object gate = new();
        internal byte[]? Bytes;
        internal int Reads, Writes, Disposals;
        internal bool BadFingerprint;
        internal Func<byte[], DocumentFingerprint, CancellationToken, Task<DocumentFingerprint>>? OnWrite;
        internal Func<Task>? OnRead;
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default)
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
                if (Fingerprint() != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
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
        public async Task<MusicAccountVerifiedCredential> ValidateAsync(string p, string c, CancellationToken token = default)
        { Validations++; return new(c, Observation(await Validate(p, c, token))); }
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
        public Task CancelAsync(string platform) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryStore Store { get; }
        internal MusicPlatformRuntimeFixture Runtime { get; } = new();
        internal MusicAccountProtocol Protocol { get; }
        internal MusicAccountOwner Owner { get; }
        internal Fixture(Func<MusicPlatformRuntimeFixture.Call, CancellationToken, Task<JsonElement>>? send = null, MemoryStore? store = null)
        {
            Store = store ?? new();
            Runtime.OnRequest = (call, token) => call.Operation == "qr.cancel" ? Success(call, token) : (send ?? Success)(call, token);
            Protocol = Runtime.CreateAccountProtocol(); Owner = new(Store, Protocol); Runtime.Client.BindSessions(Owner);
        }
        public async ValueTask DisposeAsync() { await Owner.DisposeAsync(); await Runtime.DisposeAsync(); }
    }
    private static string Key(string platform) => AccountDocument.Key(platform);
    private static string CurrentJson(long generation = 7, string cookie = Wy)
    {
        var store = new MemoryStore(); var owner = new MusicAccountOwner(store, new Port());
        try
        {
            Ok(Import(owner, cookie: cookie).GetAwaiter().GetResult());
            var document = store.Document(); document["entries"]![0]!["value"]!["generation"] = generation;
            return document.ToJsonString();
        }
        finally { owner.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static async Task AllPlatforms()
    {
        await using var f = new Fixture();
        Check(f.Runtime.Requests.Count == 0 && f.Store.Reads == 0);
        foreach (var platform in new[] { "wy", "tx", "kg" }) Ok(await Import(f.Owner, platform));
        Check(f.Runtime.Requests.Count(call => call.Operation == "account.verify") == 3 && f.Store.Writes == 3);
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
        f.Store.OnWrite = (_, _, _) => Task.FromException<DocumentFingerprint>(new DocumentStoreException(DocumentStoreError.Conflict));
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
    private static async Task UnknownEntry(string key, JsonObject value)
    {
        var store = new MemoryStore(); store.Seed(CurrentJson());
        var doc = store.Document();
        doc["entries"]!.AsArray().Add(new JsonObject { ["key"] = key, ["value"] = value });
        byte[] original = Encoding.UTF8.GetBytes(doc.ToJsonString());
        store.Bytes = original.ToArray();
        await using var f = new Fixture(store: store);
        Ok(await List(f.Owner));
        Ok(await Import(f.Owner));
        Ok(await Command(f.Owner, "logout"));
        var retained = store.Document()["entries"]!.AsArray().OfType<JsonObject>().Single(e => e["key"]!.GetValue<string>() == key);
        Check(JsonNode.DeepEquals(retained["value"], value) && store.Writes == 2);
        CryptographicOperations.ZeroMemory(original);
    }
    private static async Task StorageFailure()
    {
        await using var f = new Fixture();
        f.Store.OnRead = () => Task.FromException(new DocumentStoreException(DocumentStoreError.Unavailable));
        Error(await Import(f.Owner), "STORAGE_UNAVAILABLE"); Error(await List(f.Owner), "STORAGE_UNAVAILABLE");
        Check(f.Runtime.Requests.All(call => call.Operation == "qr.cancel") && f.Store.Writes == 0);
    }
    private static async Task ProfileReflection()
    {
        await using var f = new Fixture();
        f.Runtime.OnRequest = (call, token) => call.Operation == "qr.cancel" ? Success(call, token) : throw new MusicPlatformException("INVALID_RESPONSE");
        Error(await Import(f.Owner), "INVALID_RESPONSE"); Check(f.Store.Writes == 0);
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
        Error(await f.Owner.CallAsync("music.accounts.remoteLogout", new { platform = "wy" }), "INVALID_REQUEST");
        Check(f.Runtime.Requests.Count == 0 && f.Store.Reads == 0);
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
        Ok(await List(f.Owner));
        Ok(await Import(f.Owner));
        Check(store.Writes == 1);
        CryptographicOperations.ZeroMemory(original);
    }
    private static async Task CorruptDocument()
    {
        var store = new MemoryStore(); store.Seed(CurrentJson());
        var doc = store.Document(); doc["entries"]!.AsArray().Add(new JsonObject { ["key"] = Key("kg"), ["value"] = null });
        byte[] original = Encoding.UTF8.GetBytes(doc.ToJsonString());
        store.Bytes = original.ToArray();
        await using var f = new Fixture(store: store);
        Ok(await Import(f.Owner));
        Error(await List(f.Owner), "CORRUPT_STORAGE");
        Check(store.Writes == 1 && store.Document()["entries"]!.AsArray().OfType<JsonObject>()
            .Single(e => e["key"]!.GetValue<string>() == Key("kg"))["value"] is null);
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
        Check(f.Runtime.Requests.All(call => call.Operation == "qr.cancel") && store.Writes == 0);
    }
    private static async Task InvalidCookie(string value)
    {
        await using var f = new Fixture((call, _) => {
            Check(call.Operation == "account.verify" && call.Payload.GetProperty("candidate").GetProperty("cookie").GetString() == value);
            throw new MusicPlatformException("INVALID_CREDENTIALS");
        });
        Error(await Import(f.Owner, cookie: value), "INVALID_CREDENTIALS");
        Check(f.Runtime.Requests.Count(call => call.Operation == "account.verify") == 1 && f.Store.Writes == 0);
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
    private static async Task QrExpiry()
    {
        foreach (string status in new[] { "authenticated", "expired" })
        {
            long displayedExpiry = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            await using var f = new Fixture((call, token) => call.Operation switch {
                "qr.create" => Task.FromResult(MusicPlatformRuntimeFixture.Json(new {
                    challengeId = call.Parameters.GetProperty("challengeId").GetString(), url = "https://qr.invalid/synthetic",
                    expiresAt = displayedExpiry, imageBase64 = (string?)null })),
                "qr.poll" when status == "expired" => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status, account = (object?)null })),
                _ => Success(call, token)
            });
            var begin = await Command(f.Owner, "qr.begin");
            string id = Challenge(begin);
            Check(begin.GetProperty("challenge").GetProperty("expiresAt").GetInt64() == displayedExpiry);
            var result = await Poll(f.Owner, id); Ok(result);
            Check(result.GetProperty("status").GetString() == status &&
                f.Store.Writes == (status == "authenticated" ? 1 : 0) &&
                (await f.Owner.AcquireAsync("wy") is not null) == (status == "authenticated"));
            Check(f.Runtime.Requests.Count(call => call.Operation == "qr.create") == 1 &&
                f.Runtime.Requests.Count(call => call.Operation == "qr.poll") == 1);
        }
    }
    private static async Task Dpapi(string cookie, string? verifiedCookie = null)
    {
        string savedCookie = verifiedCookie ?? cookie;
        string path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        await using var runtime = new MusicPlatformRuntimeFixture();
        runtime.OnRequest = async (call, token) =>
        {
            var value = await Success(call, token);
            if (call.Operation != "account.verify") return value;
            var verified = JsonNode.Parse(value.GetRawText())!.AsObject();
            verified["cookie"] = savedCookie;
            return MusicPlatformRuntimeFixture.Json(verified);
        };
        await using (var store = new LocalDocumentStore(path, [MusicAccountOwner.DocumentNamespace], protectedNamespaces: [MusicAccountOwner.DocumentNamespace]))
        {
            await using var owner = runtime.CreateAccountOwner(store); Ok(await Import(owner, cookie: cookie));
            Check(runtime.Requests.Single(call => call.Operation == "account.verify").Payload.GetProperty("candidate").GetProperty("cookie").GetString() == cookie &&
                (await owner.AcquireAsync("wy"))?.Cookie == savedCookie);
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("SYNTHETIC_WY_SESSION", StringComparison.Ordinal));
        }
        await using (var store = new LocalDocumentStore(path, [MusicAccountOwner.DocumentNamespace], protectedNamespaces: [MusicAccountOwner.DocumentNamespace]))
        {
            await using var owner = new MusicAccountOwner(store, runtime.CreateAccountProtocol());
            Check(State(await List(owner)) == "saved" && (await owner.AcquireAsync("wy"))?.Cookie == savedCookie &&
                runtime.Requests.Count(call => call.Operation == "account.verify") == 1);
            Ok(await Command(owner, "logout")); Check(State(await List(owner)) == "none");
            await owner.PrepareCloseAsync(); Check((await store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint.Exists);
        }
        Check(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); Directory.Delete(path, true);
    }
    private static Task ExpiredSession() => ExpiredSession("AUTH_REQUIRED", "AUTH_EXPIRED");
    private static async Task ExpiredSession(string code, string expected)
    {
        bool expired = false;
        await using var f = new Fixture((call, token) => expired ? throw new MusicPlatformException(code) : Success(call, token));
        Ok(await Import(f.Owner)); var session = (await f.Owner.AcquireAsync("wy", default))!;
        string document = f.Store.Document().ToJsonString();
        expired = true; Error(await Command(f.Owner, "validate"), expected);
        Check(!f.Owner.IsCurrent(session) && await f.Owner.AcquireAsync("wy") is null &&
            f.Store.Writes == 1 && State(await List(f.Owner)) == "revoked");
        Check(f.Store.Document().ToJsonString() == document, "Authentication rejection invented a durable logout");
    }
    private static async Task OrdinaryValidation()
    {
        string? failure = null;
        await using var f = new Fixture((call, token) => failure is null ? Success(call, token) : throw new MusicPlatformException(failure));
        Ok(await Import(f.Owner));
        var original = (await f.Owner.AcquireAsync("wy"))!;
        string document = f.Store.Document().ToJsonString();
        int events = 0; f.Owner.SessionChanged += _ => events++;
        foreach (string code in new[] { "FORBIDDEN", "VERIFICATION_REQUIRED", "UPSTREAM_ERROR" })
        {
            failure = code;
            int before = events;
            Error(await Command(f.Owner, "validate"), code);
            var retained = await f.Owner.AcquireAsync("wy");
            Check(retained is not null && f.Owner.IsCurrent(retained) && retained.Scope == original.Scope &&
                retained.Generation == original.Generation && retained.Cookie == original.Cookie);
            Check(State(await List(f.Owner)) == "saved" && f.Store.Writes == 1 && f.Store.Document().ToJsonString() == document);
            // Start retires the old operation/lease; an ordinary failure adds no authentication revocation.
            Check(events == before + 1);
        }
        await f.Runtime.Client.DrainAsync();
        Check(f.Runtime.Calls.Where(call => call.Method == "music.platform.session.update")
            .All(call => call.Payload.GetProperty("session").ValueKind == JsonValueKind.Null));
    }
    private static async Task CandidateFailure()
    {
        string? failure = null;
        await using var f = new Fixture((call, token) => failure is null ? Success(call, token) : throw new MusicPlatformException(failure));
        Ok(await Import(f.Owner));
        var original = (await f.Owner.AcquireAsync("wy"))!;
        string document = f.Store.Document().ToJsonString();
        foreach (string code in new[] { "AUTH_REQUIRED", "AUTH_EXPIRED", "INVALID_CREDENTIALS", "IDENTITY_MISMATCH" })
        {
            failure = code;
            Error(await Import(f.Owner, cookie: "MUSIC_U=SYNTHETIC_REJECTED_CANDIDATE"), code == "AUTH_REQUIRED" ? "AUTH_EXPIRED" : code);
            var retained = await f.Owner.AcquireAsync("wy");
            Check(retained is not null && f.Owner.IsCurrent(retained) && retained.Scope == original.Scope &&
                retained.Generation == original.Generation && retained.Cookie == original.Cookie);
            Check(State(await List(f.Owner)) == "saved" && f.Store.Writes == 1 && f.Store.Document().ToJsonString() == document);
        }
        await f.Runtime.Client.DrainAsync();
        Check(f.Runtime.Calls.Where(call => call.Method == "music.platform.session.update")
            .All(call => call.Payload.GetProperty("session").ValueKind == JsonValueKind.Null), "Rejected candidate was published");
    }
    private static async Task Redaction()
    {
        await using var f = new Fixture((_, _) => throw new MusicPlatformException("NETWORK_ERROR"));
        var result = await Import(f.Owner); Error(result, "NETWORK_ERROR"); Check(!result.GetRawText().Contains("SYNTHETIC", StringComparison.Ordinal));
        var handoff = new MusicAccountHandoff("wy", Guid.NewGuid().ToString(), Wy, Observation(new("wy", LargeId, "Fixture")));
        var session = new MusicPlatformSession("wy", "scope", 1, LargeId, Wy);
        var verified = new MusicAccountVerifiedCredential(Wy, handoff.Details);
        Check(JsonSerializer.Serialize(handoff) == "{}" && JsonSerializer.Serialize(session) == "{}" &&
            JsonSerializer.Serialize(verified) == "{}");
        Check(!handoff.ToString().Contains("SYNTHETIC") && !session.ToString().Contains("SYNTHETIC") &&
            !verified.ToString().Contains("SYNTHETIC"));
        Check(new MusicAccountException("SYNTHETIC_ERROR").Message == "ACCOUNT_FAILED");
    }
}
