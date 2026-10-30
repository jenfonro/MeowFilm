// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicPlatformClientContextTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("constructor-and-invalid-platform-have-zero-IO", NoConstructorIo);
        yield return Case("single-local-initialization-without-account-or-network", Local);
        yield return Case("stable-identities-across-owner-reopen", Reopen);
        yield return Case("cancelled-initialization-waiter-does-not-cancel-shared-load", CancelLoad);
        yield return Case("first-missing-client-imports-published-fields-once-without-account-write", Import);
        yield return Case("partial-WY-client-fields-retained-missing-identity-generated-normally", PartialImport);
        yield return Case("existing-client-does-not-read-or-reimport-corrupt-account-source", NoReimport);
        yield return Case("invalid-account-import-fails-with-finite-storage-code", ImportFailure);
        yield return Case("unexpected-storage-read-failure-is-finite-and-redacted", UnexpectedRead);
        foreach (string fault in new[] { "json", "duplicate", "version", "field", "array", "size", "fingerprint",
            "missing-fingerprint", "kg-seed", "qq-imei", "generation", "device-text", "session-binding" })
        { var value = fault; yield return Case("corrupt-or-incompatible-" + value + "-never-regenerates", () => Corrupt(value)); }
        foreach (var pair in new[] {
            (DocumentStoreError.Unavailable, "STORAGE_UNAVAILABLE"), (DocumentStoreError.Storage, "STORAGE"),
            (DocumentStoreError.Corrupt, "CORRUPT_STORAGE"), (DocumentStoreError.Protection, "CORRUPT_STORAGE"),
            (DocumentStoreError.UnsupportedVersion, "CORRUPT_STORAGE"), (DocumentStoreError.Closed, "CLOSED") })
        { var value = pair; yield return Case("unreadable-" + value.Item1 + "-no-import-or-write", () => Unreadable(value.Item1, value.Item2)); }
        yield return Case("independent-platform-updates-retain-unaffected-snapshot-references", Independent);
        yield return Case("real-client-CAS-conflict-preserves-competitor-no-write-replay", Conflict);
        foreach (string fault in new[] { "lost-after", "lost-before", "wrong-receipt" })
        { var value = fault; yield return Case("unknown-" + value + "-explicit-authority-read-no-replay", () => Unknown(value)); }
        yield return Case("reload-fences-old-snapshot-and-shares-with-initialization", Reload);
        yield return Case("queued-trusted-response-rechecks-account-before-client-write", AccountFence);
        yield return Case("confirmed-client-write-outlives-cancelled-waiter-without-false-unknown", CancelWrite);
        yield return Case("shutdown-awaits-physical-client-write-and-borrows-store", Close);
        yield return Case("cancelled-shutdown-preparation-does-not-revive-old-snapshots", Resume);
        yield return Case("WY-wire-composes-local-client-with-optional-account-no-credential-persistence", WyFields);
        yield return Case("empty-WY-device-field-uses-local-client-and-optional-cookie-clearing-is-not-account-expiry", WyOptional);
        yield return Case("foreign-WY-account-association-never-mixes-local-device-fields", ForeignWy);
        yield return Case("KG-request-identity-binding-and-query-body-headers-agree", KgFields);
        yield return Case("KG-invalid-credentials-and-conflicting-identity-reject-before-dispatch", KgInvalid);
        foreach (var (name, body, code) in new (string, string, string?)[]
        {
            ("whole-decimal", """{"status":1.0}""", null),
            ("empty-flags", """{"status":1,"error":"","ssaCode":"","ssa-code":null,"error_code":0}""", null),
            ("string-status", """{"status":"1"}""", "INVALID_RESPONSE"),
            ("boolean-status", """{"status":true}""", "INVALID_RESPONSE"),
            ("failure-status", """{"status":0}""", "UPSTREAM_ERROR"),
            ("string-error", """{"status":1,"error":"false"}""", "UPSTREAM_ERROR"),
            ("string-verification", """{"status":1,"ssaCode":"false"}""", "VERIFICATION_REQUIRED")
        })
        {
            var b = body; var c = code;
            yield return Case("KG-account-status-preserves-" + name, async () =>
            {
                Task Read()
                { PlatformProtocols.KgAccountStatus(JsonNode.Parse(b)!.AsObject()); return Task.CompletedTask; }
                if (c is null) await Read(); else await Error(Read, c);
            });
        }
        foreach (string fault in new[] { "identity", "duplicate", "domain", "control", "expired-auth", "row-limit" })
        { var value = fault; yield return Case("untrusted-WY-response-" + value + "-no-client-write", () => WyResponseFailure(value)); }
        yield return Case("catalog-cache-uses-post-response-client-generation", CacheGeneration);
        yield return Case("discovery-client-failures-retain-finite-code-without-HTTP", DiscoveryFailures);
    }

    private static TestCase Case(string name, Func<Task> run) => new("music.platform.client." + name, run);
    private static void Check(bool condition, string message = "Client context invariant")
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Error(Func<Task> action, string code)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (MusicPlatformException error)
        {
            Check(error.Code == code, "Expected " + code + ", got " + error.Code);
            Check(error.InnerException is null && error.Message == code && !error.ToString().Contains("SYNTHETIC"));
            return;
        }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancelled waiter");
    }
    private static Task<MusicWyClientContext> ChangeWy(Fixture f, MusicWyClientContext expected,
        string value = "SYNTHETIC_NEXT", Action? guard = null, CancellationToken token = default) =>
        f.Owner.AcceptWyAsync(expected, null, ["NMTID=" + value + "; Domain=music.163.com"], guard ?? (() => { }), token);

    private static async Task NoConstructorIo()
    {
        await using var f = new Fixture(importAccounts: true);
        Check(f.Store.ClientReads == 0 && f.Store.ClientWrites == 0 && f.Imports == 0);
        await Error(async () => await f.Owner.AcquireAsync("kw"), "INVALID_PLATFORM");
        Check(f.Store.ClientReads == 0 && f.Store.AccountReads == 0 && f.Store.ClientWrites == 0);
    }
    private static async Task Local()
    {
        await using var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => f.Owner.InitializeAsync()));
        var wy = await f.Owner.AcquireWyAsync(); var kg = await f.Owner.AcquireKgAsync(); var qq = await f.Owner.AcquireQqAsync();
        Check(f.Store.ClientReads == 1 && f.Store.ClientWrites == 1 && f.Store.AccountReads == 0 && f.Store.AccountWrites == 0);
        Check(ReferenceEquals(wy, await f.Owner.AcquireWyAsync()) && ReferenceEquals(kg, await f.Owner.AcquireKgAsync()));
        Check(wy.DeviceId.Length == 52 && wy.DeviceId.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterLower(c)));
        Check(wy.Nuid.Length == 64 && wy.Nnid.StartsWith(wy.Nuid + ",", StringComparison.Ordinal) &&
            wy.Wnmcid.Split('.')[0].Length == 6);
        Check(kg.Seed.Length == 32 && kg.Dfid is null && qq.Session is null && qq.Qimei is null);
        Check(JsonSerializer.Serialize(wy) == "{}" && JsonSerializer.Serialize(kg) == "{}" && JsonSerializer.Serialize(qq) == "{}");
        Check(!wy.ToString().Contains(wy.DeviceId) && !kg.ToString().Contains(kg.Seed));
    }
    private static async Task Reopen()
    {
        await using var f = new Fixture();
        var wy = await f.Owner.AcquireWyAsync(); var kg = await f.Owner.AcquireKgAsync(); var qq = await f.Owner.AcquireQqAsync();
        await f.Owner.DisposeAsync();
        Check(!f.Owner.IsCurrent(wy) && f.Store.Disposals == 0);
        await using var next = f.CreateOwner();
        var w = await next.AcquireWyAsync(); var k = await next.AcquireKgAsync(); var q = await next.AcquireQqAsync();
        Check(w == wy && k == kg && q == qq && !ReferenceEquals(wy, w));
        Check(f.Store.ClientWrites == 1 && f.Store.ClientReads == 2);
    }
    private static async Task CancelLoad()
    {
        await using var f = new Fixture(); f.Store.HoldRead = true;
        using var cancel = new CancellationTokenSource();
        var cancelled = f.Owner.InitializeAsync(cancel.Token);
        await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var live = f.Owner.AcquireWyAsync().AsTask();
        cancel.Cancel(); await Cancelled(cancelled);
        Check(!live.IsCompleted && f.Store.ClientWrites == 0);
        f.Store.Release.TrySetResult();
        Check(f.Owner.IsCurrent(await live) && f.Store.ClientReads == 1 && f.Store.ClientWrites == 1);
    }
    private static JsonObject PublishedAccount(string cookie) => new()
    {
        ["version"] = 1, ["entries"] = new JsonArray(new JsonObject {
            ["key"] = AccountDocument.Key("wy"), ["value"] = new JsonObject {
                ["version"] = 1, ["generation"] = 7, ["cookie"] = cookie, ["scope"] = "synthetic-import-scope",
                ["validatedAt"] = 1000, ["profile"] = new JsonObject {
                    ["platform"] = "wy", ["userId"] = "42", ["displayName"] = "Synthetic" }
            } })
    };
    private static async Task Import()
    {
        await using var f = new Fixture(importAccounts: true);
        var account = PublishedAccount("MUSIC_U=SYNTHETIC_AUTH; deviceId=SYNTHETIC_IMPORTED_ID; _ntes_nuid=SYNTHETIC_NUID; WNMCID=SYNTHETIC_WNMCID");
        byte[] clientBytes = MusicClientContextDocument.Create(new()).Encode();
        JsonObject legacy;
        try { legacy = JsonNode.Parse(clientBytes)!["tx"]!.DeepClone().AsObject(); }
        finally { CryptographicOperations.ZeroMemory(clientBytes); }
        legacy["version"] = 1; legacy["profile"] = "vivo-v1";
        account["entries"]!.AsArray().Add(new JsonObject { ["key"] = AccountDocument.QqFeedbackDeviceKey, ["value"] = legacy });
        account["entries"]!.AsArray().Add(new JsonObject { ["key"] = AccountDocument.KgDeviceKey, ["value"] = new JsonObject {
            ["version"] = 1, ["generation"] = 9, ["seed"] = "0123456789abcdef0123456789abcdef", ["dfid"] = "SYNTHETIC_IMPORTED_DFID" } });
        await f.Seed(MusicAccountOwner.DocumentNamespace, Encoding.UTF8.GetBytes(account.ToJsonString()));
        var before = await f.Store.Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
        try
        {
            var wy = await f.Owner.AcquireWyAsync(); var kg = await f.Owner.AcquireKgAsync(); var qq = await f.Owner.AcquireQqAsync();
            Check(wy.DeviceId == "SYNTHETIC_IMPORTED_ID" && wy.Nuid == "SYNTHETIC_NUID" && wy.Nnid.StartsWith(wy.Nuid + ",", StringComparison.Ordinal));
            Check(kg.Generation == 9 && kg.Dfid == "SYNTHETIC_IMPORTED_DFID" &&
                qq.InstallationId == legacy["installationId"]!.GetValue<string>());
            var after = await f.Store.Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
            try { Check(before.Fingerprint == after.Fingerprint && before.Data!.SequenceEqual(after.Data!)); }
            finally { CryptographicOperations.ZeroMemory(after.Data!); }
            Check(f.Imports == 1 && f.Store.AccountWrites == 0 && f.Store.ClientWrites == 1);
        }
        finally { CryptographicOperations.ZeroMemory(before.Data!); }
    }
    private static async Task PartialImport()
    {
        await using var f = new Fixture(importAccounts: true);
        var account = PublishedAccount("MUSIC_U=SYNTHETIC_AUTH; _ntes_nuid=SYNTHETIC_NUID; WNMCID=SYNTHETIC_WNMCID; NMTID=");
        await f.Seed(MusicAccountOwner.DocumentNamespace, Encoding.UTF8.GetBytes(account.ToJsonString()));
        var wy = await f.Owner.AcquireWyAsync();
        Check(wy.DeviceId.Length == 52 && wy.Nuid == "SYNTHETIC_NUID" && wy.Wnmcid == "SYNTHETIC_WNMCID" &&
            wy.Nnid.StartsWith(wy.Nuid + ",", StringComparison.Ordinal) && wy.Nmtid is null);
        Check(f.Store.AccountWrites == 0);
    }
    private static async Task NoReimport()
    {
        await using var f = new Fixture(importAccounts: true);
        var first = await f.Owner.AcquireWyAsync();
        await f.Owner.DisposeAsync();
        await f.Seed(MusicAccountOwner.DocumentNamespace, "not-json"u8.ToArray());
        await using var next = f.CreateOwner(importAccounts: true);
        Check((await next.AcquireWyAsync()).DeviceId == first.DeviceId && f.Imports == 1 && f.Store.AccountReads == 1);
        Check(f.Store.ClientWrites == 1);
    }
    private static async Task ImportFailure()
    {
        await using var f = new Fixture(importAccounts: true);
        await f.Seed(MusicAccountOwner.DocumentNamespace, "not-json"u8.ToArray());
        await Error(() => f.Owner.InitializeAsync(), "CORRUPT_STORAGE");
        Check(f.Store.ClientWrites == 0 && f.Store.AccountWrites == 0 && f.Imports == 1);
    }
    private static async Task Corrupt(string fault)
    {
        await using var f = new Fixture(importAccounts: true);
        byte[] bytes = MusicClientContextDocument.Create(new()).Encode();
        var json = JsonNode.Parse(bytes)!.AsObject();
        switch (fault)
        {
            case "json": bytes = "not-json"u8.ToArray(); break;
            case "duplicate": bytes = Encoding.UTF8.GetBytes("{\"version\":1," + Encoding.UTF8.GetString(bytes)[1..]); break;
            case "version": json["version"] = 2; break;
            case "field": json["wy"]!.AsObject().Remove("nnid"); break;
            case "array": json["kg"] = new JsonArray(); break;
            case "size": bytes = new byte[MusicClientContextDocument.MaximumBytes + 1]; break;
            case "kg-seed": json["kg"]!["seed"] = "bad"; break;
            case "qq-imei": json["tx"]!["imei"] = "123456789012345"; break;
            case "generation": json["wy"]!["generation"] = 0; break;
            case "device-text": json["wy"]!["deviceId"] = "bad;field"; break;
            case "session-binding": json["tx"]!["session"] = new JsonObject { ["generation"] = 1, ["uid"] = "0",
                ["sid"] = "SYNTHETIC_SESSION", ["savedAt"] = 1000, ["qimeiGeneration"] = 1 }; break;
        }
        if (fault is not ("json" or "duplicate" or "size")) bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        if (fault == "fingerprint") f.Store.ReadOverride = new(bytes, DocumentFingerprint.FromBytes("other"u8));
        else if (fault == "missing-fingerprint") f.Store.ReadOverride = new(null, DocumentFingerprint.FromBytes(bytes));
        else await f.Seed(MusicPlatformClientContextOwner.DocumentNamespace, bytes);
        await Error(() => f.Owner.InitializeAsync(), "CORRUPT_STORAGE");
        Check(f.Store.ClientWrites == 0 && f.Imports == 0 && f.Store.AccountReads == 0);
        await Error(() => f.Owner.ReloadAsync(), "CORRUPT_STORAGE");
        Check(f.Store.ClientWrites == 0 && f.Imports == 0);
    }
    private static async Task Unreadable(DocumentStoreError error, string code)
    {
        await using var f = new Fixture(importAccounts: true); f.Store.ReadError = error;
        await Error(() => f.Owner.InitializeAsync(), code);
        Check(f.Store.ClientWrites == 0 && f.Store.AccountReads == 0 && f.Imports == 0);
    }
    private static async Task Independent()
    {
        await using var f = new Fixture();
        var wy = await f.Owner.AcquireWyAsync(); var kg = await f.Owner.AcquireKgAsync(); var qq = await f.Owner.AcquireQqAsync();
        var events = new List<string>(); f.Owner.ContextChanged += events.Add;
        var changed = await f.Owner.SaveKgAsync(kg, "SYNTHETIC_DFID");
        Check(changed.Generation == 2 && !f.Owner.IsCurrent(kg) && f.Owner.IsCurrent(changed));
        Check(ReferenceEquals(wy, await f.Owner.AcquireWyAsync()) && ReferenceEquals(qq, await f.Owner.AcquireQqAsync()));
        Check(events.SequenceEqual(["kg"]));
        var w = await ChangeWy(f, wy);
        Check(w.Generation == 2 && f.Owner.IsCurrent(changed) && f.Owner.IsCurrent(qq) &&
            events.SequenceEqual(["kg", "wy"]));
    }
    private static async Task UnexpectedRead()
    {
        await using var f = new Fixture(importAccounts: true); f.Store.UnexpectedRead = true;
        await Error(() => f.Owner.InitializeAsync(), "STORAGE");
        Check(f.Store.ClientWrites == 0 && f.Store.AccountReads == 0 && f.Imports == 0);
    }
    private static async Task Conflict()
    {
        await using var f = new Fixture(); var wy = await f.Owner.AcquireWyAsync(); f.Store.ConflictAt = 2;
        await Error(async () => await ChangeWy(f, wy), "STORAGE_CONFLICT");
        Check(!f.Owner.IsCurrent(wy) && !f.Owner.NeedsReconciliation && f.Store.ClientWrites == 2);
        var observed = await f.Owner.AcquireWyAsync();
        Check(observed.Nmtid == "SYNTHETIC_CONCURRENT" && f.Store.ClientWrites == 2);
    }
    private static async Task Unknown(string fault)
    {
        await using var f = new Fixture(importAccounts: true);
        if (fault == "lost-after") f.Store.DropReceiptAt = 1;
        else if (fault == "lost-before") f.Store.FailWriteAt = 1;
        else f.Store.WrongReceiptAt = 1;
        await Error(() => f.Owner.InitializeAsync(), "COMMIT_UNKNOWN");
        int reads = f.Store.ClientReads;
        await Error(async () => await f.Owner.AcquireWyAsync(), "COMMIT_UNKNOWN");
        Check(f.Owner.NeedsReconciliation && f.Store.ClientWrites == 1 && f.Store.ClientReads == reads && f.Imports == 1);
        if (fault == "lost-before")
        {
            await Error(() => f.Owner.ReloadAsync(), "DEVICE_CONTEXT_REQUIRED");
            Check(!f.Owner.NeedsReconciliation && f.Store.ClientWrites == 1 && f.Imports == 1);
            await Error(() => f.Owner.InitializeAsync(), "DEVICE_CONTEXT_REQUIRED");
            Check(f.Store.ClientWrites == 1 && f.Imports == 1);
        }
        else
        {
            await f.Owner.ReloadAsync();
            var value = await f.Owner.AcquireWyAsync();
            Check(f.Owner.IsCurrent(value) && !f.Owner.NeedsReconciliation && f.Store.ClientWrites == 1 && f.Imports == 1);
        }
    }
    private static async Task Reload()
    {
        await using var f = new Fixture(); var old = await f.Owner.AcquireWyAsync();
        f.Store.HoldRead = true;
        var reload = f.Owner.ReloadAsync();
        await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initialize = f.Owner.InitializeAsync();
        Check(!f.Owner.IsCurrent(old) && !initialize.IsCompleted);
        f.Store.Release.TrySetResult(); await Task.WhenAll(reload, initialize);
        var current = await f.Owner.AcquireWyAsync();
        Check(current == old && !ReferenceEquals(current, old) && !f.Owner.IsCurrent(old) &&
            f.Store.ClientReads == 2 && f.Store.ClientWrites == 1);
    }
    private static async Task AccountFence()
    {
        await using var f = new Fixture();
        var wy = await f.Owner.AcquireWyAsync(); var kg = await f.Owner.AcquireKgAsync();
        f.Store.HoldWriteAt = 2;
        var blocker = f.Owner.SaveKgAsync(kg, "SYNTHETIC_DFID");
        await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool accountCurrent = true;
        void Guard() { if (!accountCurrent) throw new MusicPlatformException("STALE_SESSION"); }
        var delayed = ChangeWy(f, wy, guard: Guard);
        accountCurrent = false; f.Store.Release.TrySetResult();
        await blocker; await Error(async () => await delayed, "STALE_SESSION");
        Check(f.Owner.IsCurrent(wy) && (await f.Owner.AcquireWyAsync()).Nmtid is null && f.Store.ClientWrites == 2);
    }
    private static async Task CancelWrite()
    {
        await using var f = new Fixture(); var kg = await f.Owner.AcquireKgAsync();
        f.Store.HoldWriteAt = 2;
        using var cancel = new CancellationTokenSource();
        var pending = f.Owner.SaveKgAsync(kg, "SYNTHETIC_DFID", cancel.Token);
        await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel(); Check(!pending.IsCompleted);
        f.Store.Release.TrySetResult(); await Cancelled(pending);
        var current = await f.Owner.AcquireKgAsync();
        Check(current.Dfid == "SYNTHETIC_DFID" && current.Generation == 2 && !f.Owner.NeedsReconciliation &&
            !f.Owner.IsCurrent(kg) && f.Store.ClientWrites == 2);
    }
    private static async Task Close()
    {
        await using var f = new Fixture(); f.Store.HoldWriteAt = 1;
        var pending = f.Owner.AcquireWyAsync().AsTask();
        await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = f.Owner.DisposeAsync().AsTask();
        Check(!close.IsCompleted && f.Store.Disposals == 0);
        await Error(async () => await f.Owner.AcquireWyAsync(), "CLOSED");
        f.Store.Release.TrySetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(5)); await Error(async () => await pending, "CLOSED");
        await Error(() => f.Owner.PrepareShutdownAsync(), "CLOSED");
        Check(f.Store.Disposals == 0 && f.Store.ClientWrites == 1);
    }
    private static async Task Resume()
    {
        await using var f = new Fixture(); var old = await f.Owner.AcquireWyAsync();
        await f.Owner.PrepareShutdownAsync(); Check(!f.Owner.IsCurrent(old));
        await f.Owner.CancelShutdownPreparationAsync();
        var current = await f.Owner.AcquireWyAsync();
        Check(current == old && !ReferenceEquals(current, old) && !f.Owner.IsCurrent(old) && f.Store.ClientWrites == 1);
    }
    private static async Task WyFields()
    {
        await using var f = new Fixture(); var wy = await f.Owner.AcquireWyAsync();
        const string auth = "MUSIC_U=SYNTHETIC_AUTH; __csrf=SYNTHETIC_CSRF";
        var cookies = MusicWyRequestContext.Cookies(wy, auth);
        Check(cookies["deviceId"] == wy.DeviceId && cookies["MUSIC_U"] == "SYNTHETIC_AUTH" &&
            cookies["_ntes_nuid"] == wy.Nuid);
        var updated = await f.Owner.AcceptWyAsync(wy, auth,
            ["NMTID=SYNTHETIC_NMTID; Domain=.music.163.com", "_ntes_nuid=SYNTHETIC_NEW_NUID; Domain=music.163.com"],
            () => { }, default);
        Check(updated.Generation == 2 && updated.Nmtid == "SYNTHETIC_NMTID" && updated.Nuid == "SYNTHETIC_NEW_NUID");
        var root = await MusicClientContextFixture.ReadDocument(f.Store.Inner);
        Check(!root.ToJsonString().Contains("SYNTHETIC_AUTH") && f.Store.AccountWrites == 0);
        Check(ReferenceEquals(updated, await f.Owner.AcceptWyAsync(updated, auth, [], () => { }, default)) &&
            f.Store.ClientWrites == 2);
    }
    private static async Task WyOptional()
    {
        await using var f = new Fixture(); var wy = await f.Owner.AcquireWyAsync();
        var cookies = MusicWyRequestContext.Cookies(wy, "MUSIC_U=SYNTHETIC_AUTH; deviceId=");
        Check(cookies["deviceId"] == wy.DeviceId && cookies["_ntes_nuid"] == wy.Nuid);
        string[] cleared = ["MUSIC_A=; Max-Age=0", "__csrf=; Max-Age=0"];
        Check(ReferenceEquals(wy, await f.Owner.AcceptWyAsync(wy, null, cleared, () => { }, default)) &&
            MusicWyRequestContext.Authorization(["MUSIC_U=SYNTHETIC_AUTH", .. cleared], DateTimeOffset.UtcNow) == "MUSIC_U=SYNTHETIC_AUTH");
        Check(f.Store.ClientWrites == 1 && f.Store.AccountWrites == 0);
        await Error(() =>
        {
            _ = MusicWyRequestContext.Cookies(wy, "MUSIC_U=SYNTHETIC_AUTH; deviceId=" + new string('A', 257));
            return Task.CompletedTask;
        }, "INVALID_CREDENTIALS");
    }
    private static async Task ForeignWy()
    {
        await using var f = new Fixture(); var wy = await f.Owner.AcquireWyAsync();
        const string auth = "MUSIC_U=SYNTHETIC_AUTH; deviceId=SYNTHETIC_FOREIGN_ID";
        var cookies = MusicWyRequestContext.Cookies(wy, auth);
        Check(cookies["deviceId"] == "SYNTHETIC_FOREIGN_ID" && !cookies.ContainsKey("_ntes_nuid") && !cookies.ContainsKey("NMTID"));
        var after = await f.Owner.AcceptWyAsync(wy, auth, ["deviceId=SYNTHETIC_FOREIGN_ID", "NMTID=SYNTHETIC_FOREIGN_NMTID"], () => { }, default);
        Check(ReferenceEquals(wy, after) && f.Store.ClientWrites == 1 && f.Store.AccountWrites == 0);
    }
    private static async Task KgFields()
    {
        var local = MusicClientContextFixture.Kg with { Dfid = "SYNTHETIC_LOCAL_DFID" };
        foreach (var (client, cookie, mid, dfid) in new[] {
            (local, (string?)null, local.Mid, local.Dfid!),
            (local, "userid=42; token=SYNTHETIC_AUTH", local.Mid, local.Dfid!),
            (local, "userid=42; token=SYNTHETIC_AUTH; dfid=SYNTHETIC_UNBOUND", local.Mid, local.Dfid!),
            (local, $"userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID={local.Mid}; dfid=SYNTHETIC_OLD_DFID", local.Mid, local.Dfid!),
            (MusicClientContextFixture.Kg, $"userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID={local.Mid}; dfid=SYNTHETIC_OLD_DFID", local.Mid, "SYNTHETIC_OLD_DFID"),
            (local, "userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID=123456789; dfid=SYNTHETIC_ACCOUNT_DFID", "123456789", "SYNTHETIC_ACCOUNT_DFID"),
            (local, "userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID=123456789", "123456789", "-") })
        {
            MusicPlatformSession? account = cookie is null ? null : new("kg", "synthetic", 1, "42", cookie);
            using var request = PlatformProtocols.Kg("/v1/audio/audio", new() { ["mid"] = mid }, account, client,
                body: new() { ["mid"] = mid, ["dfid"] = dfid });
            var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Check(query["mid"] == mid && query["dfid"] == dfid &&
                request.Headers.GetValues("mid").Single() == mid && request.Headers.GetValues("dfid").Single() == dfid);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            Check(body["mid"]!.GetValue<string>() == mid && body["dfid"]!.GetValue<string>() == dfid &&
                !request.Headers.Contains("Cookie"));
        }
    }
    private static async Task KgInvalid()
    {
        foreach (var (cookie, code) in new[] {
            ("userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID=bad", "DEVICE_CONTEXT_REQUIRED"),
            ("userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID=", "DEVICE_CONTEXT_REQUIRED"),
            ("userid=42; token=SYNTHETIC_AUTH; KUGOU_API_MID=123; dfid=bad", "DEVICE_CONTEXT_REQUIRED"),
            ("userid=77; token=SYNTHETIC_AUTH", "INVALID_CREDENTIALS"),
            ("userid=42", "INVALID_CREDENTIALS") })
            await Error(() =>
            {
                using var request = PlatformProtocols.Kg("/v1/audio/audio", new(),
                    new("kg", "synthetic", 1, "42", cookie), MusicClientContextFixture.Kg);
                return Task.CompletedTask;
            }, code);
        foreach (bool body in new[] { false, true })
            await Error(() =>
            {
                var fields = new JsonObject { ["mid"] = "123456789" };
                using var request = PlatformProtocols.Kg("/v1/audio/audio", body ? new() : fields, null,
                    MusicClientContextFixture.Kg, body: body ? fields : null);
                return Task.CompletedTask;
            }, "INVALID_REQUEST");
    }
    private static async Task WyResponseFailure(string fault)
    {
        await using var f = new Fixture(); var wy = await f.Owner.AcquireWyAsync();
        string[] rows = fault switch
        {
            "identity" => ["deviceId=SYNTHETIC_OTHER_ID"],
            "duplicate" => ["NMTID=SYNTHETIC_ONE", "NMTID=SYNTHETIC_TWO"],
            "domain" => ["NMTID=SYNTHETIC_VALUE; Domain=untrusted.invalid"],
            "control" => ["NMTID=SYNTHETIC_VALUE\r\n"],
            "expired-auth" => ["MUSIC_U=; Max-Age=0"],
            _ => Enumerable.Repeat("unrelated=value", 33).ToArray()
        };
        await Error(async () => await f.Owner.AcceptWyAsync(wy, null, rows, () => { }, default),
            fault == "identity" ? "IDENTITY_MISMATCH" : fault == "expired-auth" ? "AUTH_EXPIRED" : "INVALID_RESPONSE");
        Check(f.Owner.IsCurrent(wy) && f.Store.ClientWrites == 1);
    }
    private static async Task CacheGeneration()
    {
        await using var f = new Fixture(); var handler = new CacheHandler();
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        using var catalog = new MusicPlatformCatalog(new Anonymous(), http, f.Owner);
        var first = await catalog.HomeAsync(new("wy"));
        var cached = await catalog.HomeAsync(new("wy"));
        Check(handler.Calls == 1 && f.Store.ClientWrites == 2 && first.GetRawText() == cached.GetRawText());
    }
    private static async Task DiscoveryFailures()
    {
        foreach (var (error, unknown, code) in new[] {
            ((DocumentStoreError?)DocumentStoreError.Unavailable, false, "STORAGE_UNAVAILABLE"),
            ((DocumentStoreError?)DocumentStoreError.Corrupt, false, "CORRUPT_STORAGE"),
            ((DocumentStoreError?)null, true, "COMMIT_UNKNOWN") })
        {
            await using var f = new Fixture(); f.Store.ReadError = error;
            if (unknown)
            {
                f.Store.DropReceiptAt = 1;
                await Error(() => f.Owner.InitializeAsync(), "COMMIT_UNKNOWN");
            }
            var handler = new CacheHandler();
            using var http = MusicPlatformHttp.CreateWithHandler(handler);
            await using var discovery = new NativeMusicSearchDiscovery(new Anonymous(), http, f.Owner);
            await Error(async () => await discovery.ReadAsync(), code);
            Check(handler.Calls == 0);
        }
    }
    private sealed class Anonymous : IMusicPlatformSessionSource
    {
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token) =>
            ValueTask.FromResult<MusicPlatformSession?>(null);
        public bool IsCurrent(MusicPlatformSession session) => false;
    }
    private sealed class CacheHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Check(request.RequestUri!.AbsolutePath == "/eapi/link/page/rcmd/resource/show");
            var response = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"code\":200,\"data\":{\"cursor\":\"0\",\"hasMore\":false,\"blocks\":[]}}", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_CACHE_CONTEXT; Domain=music.163.com");
            return Task.FromResult(response);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FaultStore Store = new();
        internal readonly MusicPlatformClientContextOwner Owner;
        internal int Imports;
        internal Fixture(bool importAccounts = false) => Owner = CreateOwner(importAccounts);
        internal MusicPlatformClientContextOwner CreateOwner(bool importAccounts = false) =>
            new(Store, importAccounts ? async token => { Imports++; return await AccountClientContextImport.ReadAsync(Store, token); } : null);
        internal async Task Seed(string ns, byte[] bytes)
        {
            var before = await Store.Inner.ReadAsync(ns);
            try { await Store.Inner.WriteAsync(ns, bytes, before.Fingerprint); }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (before.Data is not null) CryptographicOperations.ZeroMemory(before.Data);
            }
        }
        public async ValueTask DisposeAsync()
        { Store.Release.TrySetResult(); await Owner.DisposeAsync(); await Store.DisposeAsync(); }
    }
    private sealed class FaultStore : IProtectedDocumentStore
    {
        internal readonly MusicClientContextFixture.MemoryStore Inner = new();
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal();
        internal int ClientReads, ClientWrites, AccountReads, AccountWrites, Disposals;
        internal int HoldWriteAt, ConflictAt, DropReceiptAt, FailWriteAt, WrongReceiptAt;
        internal bool HoldRead, UnexpectedRead;
        internal DocumentStoreError? ReadError;
        internal ProtectedDocument? ReadOverride;
        public async Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default)
        {
            if (ns != MusicPlatformClientContextOwner.DocumentNamespace)
            { AccountReads++; return await Inner.ReadAsync(ns, token); }
            Interlocked.Increment(ref ClientReads);
            if (HoldRead) { Arrived.TrySetResult(); await Release.Task; token = default; }
            if (UnexpectedRead) throw new InvalidOperationException("SYNTHETIC_READ_SECRET");
            if (ReadError is { } error) throw new ProtectedDocumentException(error);
            if (ReadOverride is { } value) return new(value.Data?.ToArray(), value.Fingerprint);
            return await Inner.ReadAsync(ns, token);
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            if (ns != MusicPlatformClientContextOwner.DocumentNamespace)
            { AccountWrites++; return await Inner.WriteAsync(ns, bytes, expected, token); }
            int write = Interlocked.Increment(ref ClientWrites);
            if (write == HoldWriteAt) { Arrived.TrySetResult(); await Release.Task; token = default; }
            if (write == ConflictAt) await MusicClientContextFixture.ConcurrentClientWrite(Inner);
            if (write == FailWriteAt) throw new ProtectedDocumentException(DocumentStoreError.Storage);
            var receipt = await Inner.WriteAsync(ns, bytes, expected, token);
            if (write == DropReceiptAt) throw new ProtectedDocumentException(DocumentStoreError.Storage);
            return write == WrongReceiptAt ? DocumentFingerprint.Missing : receipt;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (ReadOverride?.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes);
            await Inner.DisposeAsync();
        }
    }
}
