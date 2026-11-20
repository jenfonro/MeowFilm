// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real owner/protocol and SQLite; only canonical runtime results are synthetic.
public static partial class MusicNativeAccountDetailsTests
{
    private const string UserId = MusicAccountDetailsFixture.UserId;
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            var p = platform;
            yield return Case(p + "-local-empty-saved-read-never-HTTP", () => Local(p));
            yield return Case(p + "-explicit-live-profile-rights-CAS-cold-reopen", () => Reopen(p));
            yield return Case(p + "-stale-generation-zero-HTTP-zero-write", () => WrongGeneration(p));
            yield return Case(p + "-membership-auth-failure-revokes-exact-account", () => AuthFailure(p));
            yield return Case(p + "-explicit-auth-required-revokes-exact-account", () => AuthFailure(p, "AUTH_REQUIRED", "AUTH_EXPIRED"));
            yield return Case(p + "-explicit-invalid-credentials-revokes-exact-account", () => AuthFailure(p, "INVALID_CREDENTIALS"));
            yield return Case(p + "-profile-identity-mismatch-never-commits", () => IdentityFailure(p));
            yield return Case(p + "-ordinary-refusal-verification-unknown-keeps-current-details-and-session", () => OrdinaryFailure(p));
            yield return Case(p + "-rights-network-unknown-no-logout", () => Unknown(p));
            yield return Case(p + "-last-unsubscribe-retains-cache-refresh", () => CancelHttp(p));
        }
        yield return Case("WY-QQ-unknown-refresh-keeps-known-persistent-rights", PreserveKnown);
        yield return Case("expired-local-rights-unknown-keeps-absolute-expiry", Stale);
        yield return Case("profile-network-does-not-revoke-or-write", ProfileOutage);
        yield return Case("coalesced-refresh-one-cancel-does-not-cancel-other", Coalesce);
        yield return Case("close-awaits-physical-and-borrows-store-runtime", Close);
        yield return Case("unsubscribe-during-runtime-cleanup-retains-owner-refresh", DisposeStream);
        yield return Case("owner-total-deadline-awaits-noncooperative-runtime", Deadline);
        yield return Case("local-read-cancel-awaits-real-store", CancelRead);
        yield return Case("external-account-replace-old-auth-cannot-revoke-new", ExternalReplace);
        foreach (string code in new[] { "AUTH_REQUIRED", "INVALID_CREDENTIALS", "IDENTITY_MISMATCH" })
        {
            string failure = code;
            yield return Case("external-account-replace-old-" + failure + "-cannot-revoke-new", () => ExternalReplace(failure));
        }
        yield return Case("logout-fences-refresh-retained-cache-never-reappears", Logout);
        yield return Case("cache-CAS-conflict-never-blocks-account", Conflict);
        yield return Case("cache-disk-failure-never-hides-account", LostReceipt);
        yield return Case("slow-cache-write-keeps-refresh-and-business-independent", CancelWrite);
        yield return Case("two-real-owners-display-cache-never-causes-business-conflict", TwoOwners);
        yield return Case("new-account-cache-binding-no-cross-session-display", CacheBinding);
        yield return Case("undecodable-cache-misses-and-refresh-rebuilds", Corrupt);
        yield return Case("safe-DTO-no-credential-device-or-upstream-error", SafeDto);
        foreach (var test in HttpCases()) yield return test;
        foreach (var test in TtlCases()) yield return test;
        foreach (var test in LoginCases()) yield return test;
    }
    private static TestCase Case(string name, Func<Task> action) => new("music.accountDetails." + name, action);
    private static void Check(bool value, string reason = "Account details invariant")
    { if (!value) throw new InvalidOperationException(reason); }
    private static Task Wait(Task signal) => signal.WaitAsync(TimeSpan.FromSeconds(4));
    private static async Task Pending(Task task)
    { await Task.Delay(55); Check(!task.IsCompleted, "Returned before physical settle"); }
    private static async Task Until(Func<bool> predicate)
    {
        for (int i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(5); }
        Check(false, "Synthetic boundary not reached");
    }
    private static async Task Error(Func<Task> action, params string[] codes)
    {
        try { await action(); }
        catch (MusicAccountException e)
        {
            Check(codes.Contains(e.Code), "Expected " + string.Join("/", codes) + ", got " + e.Code);
            Check(e.Message == e.Code && e.InnerException is null && !e.ToString().Contains("SYNTHETIC"), "Finite diagnostics");
            return;
        }
        catch (MusicPlatformException e)
        {
            Check(codes.Contains(e.Code), "Expected " + string.Join("/", codes) + ", got " + e.Code);
            Check(e.Message == e.Code && e.InnerException is null, "Finite transport diagnostics"); return;
        }
        catch (OperationCanceledException) { Check(codes.Contains("CANCELLED")); return; }
        throw new InvalidOperationException("Expected finite error " + string.Join("/", codes));
    }
    private static async Task Local(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        Check(f.Steps.Count == 0 && f.Wrapper.Reads == 0, "Construction I/O");
        var empty = await f.Owner.ReadDetailsAsync(platform);
        Check(empty.Generation == 0 && empty.State == "none" && empty.Profile is null && empty.Membership.Vip is null);
        await f.Seed();
        var local = await f.Owner.ReadDetailsAsync(platform);
        Check(local.State == "saved" && local.Generation == 1 && local.Profile?.UserId == UserId &&
            local.Profile.DisplayName == "Saved name" && local.ProfileFreshness == MusicAccountDetailsFreshness.Fresh &&
            local.MembershipFreshness == MusicAccountDetailsFreshness.Missing && local.Membership.Vip is null);
        Check(f.Steps.Count == 0 && f.Wrapper.Writes == 0);
    }
    private static async Task Reopen(string platform)
    {
        string root;
        await using (var f = new MusicAccountDetailsFixture(platform))
        {
            root = f.Root; await f.Seed();
            string accountBefore = MusicAccountDetailsFixture.AccountRow(await f.Document()).ToJsonString();
            var session = await f.Owner.AcquireAsync(platform);
            var result = await f.Refresh();
            Check(result.Generation == 1 && result.State == "saved" && result.Profile?.DisplayName == "Fresh name" &&
                result.Profile.UserId == UserId && result.Membership.Vip == true && result.Membership.ExpiresAt > 0 &&
                result.ProfileFreshness == MusicAccountDetailsFreshness.Fresh &&
                result.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
            Check(result.Membership.Tier == (platform == "wy" ? "vip" : platform == "tx" ? "huge_vip" : "concept:svip"));
            Check(result.AudioLevels.Count == (platform == "wy" ? 6 : 0) && f.Owner.IsCurrent(session!));
            Check(f.Steps.SequenceEqual(["account.profile"]));
            var doc = await f.Document();
            Check(MusicAccountDetailsFixture.AccountRow(doc).ToJsonString() == accountBefore, "Account row changed");
            Check(doc["entries"]!.AsArray().Count == 1, "Unexpected account document entry");
            var cache = MusicAccountDetailsFixture.DetailsRow(await f.CacheDocument());
            Check(cache["accountGeneration"]!.GetValue<int>() == 1);
            await f.Cache.DrainAsync();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
                Check(!bytes.Contains("SYNTHETIC") && !bytes.Contains("Fresh name"), "DPAPI plaintext leaked");
            }
            await f.Owner.DisposeAsync();
            Check(f.Wrapper.Disposals == 0, "Owner disposed borrowed store");
        }
        await using var reopened = new MusicAccountDetailsFixture(platform, root);
        var restored = await reopened.Owner.ReadDetailsAsync(platform);
        Check(restored.State == "saved" && restored.Profile?.DisplayName == "Fresh name" &&
            restored.Membership.Vip == true && reopened.Steps.Count == 0 && reopened.Wrapper.Writes == 0);
        await reopened.Refresh();
        Check(reopened.Steps.SequenceEqual(["account.profile"]), "Explicit refresh was a cache hit");
    }
    private static async Task WrongGeneration(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        await Error(() => f.Owner.RefreshDetailsAsync(platform, 0), "SESSION_CHANGED");
        await Error(() => f.Owner.RefreshDetailsAsync(platform, 2), "SESSION_CHANGED");
        Check(f.Steps.Count == 0 && f.Wrapper.Writes == 0);
    }
    private static async Task AuthFailure(string platform, string code = "AUTH_EXPIRED", string? expected = null)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        var session = await f.Owner.AcquireAsync(platform);
        int events = 0; f.Owner.SessionChanged += _ => events++;
        f.FailureCode = code;
        await Error(() => f.Refresh(), expected ?? code);
        Check(!f.Owner.IsCurrent(session!) && await f.Owner.AcquireAsync(platform) is null && events > 0);
        var hidden = await f.Owner.ReadDetailsAsync(platform);
        Check(hidden.State == "revoked" && hidden.Profile is null && hidden.Membership.Vip is null && f.Wrapper.Writes == 0);
        Check(MusicAccountDetailsFixture.AccountRow(await f.Document())["cookie"] is not null, "Failure invented durable logout");
    }
    private static async Task IdentityFailure(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        var session = await f.Owner.AcquireAsync(platform);
        f.FailureCode = "IDENTITY_MISMATCH";
        await Error(() => f.Refresh(), "IDENTITY_MISMATCH");
        Check(f.Steps.SequenceEqual(["account.profile"]) && f.Wrapper.Writes == 0);
        Check(!f.Owner.IsCurrent(session!) && await f.Owner.AcquireAsync(platform) is null);
        Check((await f.Owner.ReadDetailsAsync(platform)) is { State: "revoked", Profile: null });
        Check(MusicAccountDetailsFixture.AccountRow(await f.Document())["cookie"] is not null);
    }
    private static async Task OrdinaryFailure(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        var known = await f.Refresh();
        await f.Cache.DrainAsync();
        var session = await f.Owner.AcquireAsync(platform);
        string document = (await f.Document()).ToJsonString();
        int events = 0; f.Owner.SessionChanged += _ => events++;
        int publications = f.Runtime.Calls.Count(call => call.Method == "music.platform.session.update");
        foreach (string code in new[] { "FORBIDDEN", "VERIFICATION_REQUIRED", "UPSTREAM_ERROR" })
        {
            f.FailureCode = code;
            await Error(() => f.Refresh(), code);
            var retained = await f.Owner.ReadDetailsAsync(platform);
            Check(retained.State == known.State && retained.Generation == known.Generation &&
                retained.Profile == known.Profile && retained.Membership == known.Membership &&
                retained.AudioLevels.SequenceEqual(known.AudioLevels));
            Check(f.Owner.IsCurrent(session!) && (await f.Owner.AcquireAsync(platform))?.Cookie == session!.Cookie);
            Check(events == 0 && f.Wrapper.Writes == 0 && (await f.Document()).ToJsonString() == document);
            Check(f.Runtime.Calls.Count(call => call.Method == "music.platform.session.update") == publications,
                "Ordinary details failure published or revoked a session");
        }
    }
    private static async Task Unknown(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed(); f.UnknownRights = true;
        var result = await f.Refresh();
        Check(result.State == "saved" && result.Profile?.DisplayName == "Fresh name" && result.Membership.Vip is null &&
            result.VerificationDeferred && result.ErrorCode == "NETWORK_ERROR" && result.MembershipRecheckAt == 0 &&
            result.MembershipFreshness == MusicAccountDetailsFreshness.Deferred);
        Check(platform == "kg" ? result.RetryAt == result.MembershipCheckedAt + 60000 : result.RetryAt == 0);
        Check(await f.Owner.AcquireAsync(platform) is not null);
        int calls = f.Steps.Count;
        Check((await f.Owner.ReadDetailsAsync(platform)).Membership.Vip is null && f.Steps.Count == calls);
    }
    private static async Task CancelHttp(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed(); f.HoldResponse = true;
        using var cancellation = new CancellationTokenSource();
        var refresh = f.Refresh(cancellation.Token);
        await Wait(f.Entered.Task); cancellation.Cancel();
        await Error(() => refresh.WaitAsync(TimeSpan.FromSeconds(2)), "CANCELLED");
        Check(!f.Cancelled.Task.IsCompleted);
        var joined = f.Refresh();
        f.Release.TrySetResult(); Check((await joined).Membership.Vip == true);
        Check(f.Wrapper.Writes == 0 && (await f.Owner.ReadDetailsAsync(platform)).Profile?.DisplayName != "Saved name");
    }
    private static async Task PreserveKnown()
    {
        foreach (string platform in new[] { "wy", "tx" })
        {
            await using var f = new MusicAccountDetailsFixture(platform); await f.Seed(); await f.Refresh();
            string rights = MusicAccountDetailsFixture.DetailsRow(await f.CacheDocument())["rights"]!.ToJsonString();
            f.UnknownRights = true;
            Check((await f.Refresh()).Membership.Vip is null);
            Check(MusicAccountDetailsFixture.DetailsRow(await f.CacheDocument())["rights"]!.ToJsonString() == rights);
            Check((await f.Owner.ReadDetailsAsync(platform)).Membership.Vip == true);
        }
    }
    private static async Task Stale()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh();
        long expiry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
        await f.MutateCache(doc => {
            var row = MusicAccountDetailsFixture.DetailsRow(doc);
            row["profileFetchedAt"] = expiry - AccountDetailsSnapshot.ProfileTtl; row["profileExpiresAt"] = expiry;
            row["rights"]!["checkedAt"] = expiry - 5000; row["rights"]!["recheckAt"] = expiry; row["rights"]!["expiresAt"] = expiry;
        });
        var value = await f.Owner.ReadDetailsAsync("wy");
        Check(value.ProfileFreshness == MusicAccountDetailsFreshness.Stale && value.MembershipFreshness == MusicAccountDetailsFreshness.Stale &&
            value.Membership.Vip is null && value.Membership.ExpiresAt == expiry && value.AudioLevels.Count == 0 && f.Steps.Count == 1);
    }
    private static async Task ProfileOutage()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.FailureCode = "NETWORK_ERROR";
        await Error(() => f.Refresh(), "NETWORK_ERROR");
        Check((await f.Owner.ReadDetailsAsync("wy")).State == "saved" && f.Wrapper.Writes == 0 && f.Steps.Count == 1);
    }
    private static async Task Coalesce()
    {
        await using var f = new MusicAccountDetailsFixture("tx"); await f.Seed(); f.HoldResponse = true;
        using var firstCancel = new CancellationTokenSource();
        var first = f.Refresh(firstCancel.Token); await Wait(f.Entered.Task);
        int reads = f.Wrapper.Reads;
        var second = f.Refresh(); await Until(() => f.Wrapper.Reads > reads); await Task.Delay(20);
        firstCancel.Cancel(); await Error(() => first, "CANCELLED");
        Check(!f.Cancelled.Task.IsCompleted && !second.IsCompleted);
        f.Release.TrySetResult(); Check((await second).Membership.Vip == true && f.Wrapper.Writes == 0 && f.Steps.Count == 1);
    }
    private static async Task Close()
    {
        await using var f = new MusicAccountDetailsFixture("kg"); await f.Seed(); f.HoldResponse = true;
        var refresh = f.Refresh(); await Wait(f.Entered.Task);
        var close = f.Owner.DisposeAsync().AsTask(); await Wait(f.Cancelled.Task); await Pending(close); await Pending(refresh);
        f.Release.TrySetResult(); await Error(() => refresh, "CLOSED"); await Wait(close);
        Check(f.Wrapper.Disposals == 0 && f.Wrapper.Writes == 0);
        var response = await f.Runtime.Client.CandidateAsync("wy", "song", new { id = "still-borrowed" }, null, "", default);
        Check(response.ValueKind == JsonValueKind.Object, "Owner disposed borrowed runtime");
    }
    private static async Task DisposeStream()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.HoldResponse = true;
        using var cancellation = new CancellationTokenSource();
        var task = f.Refresh(cancellation.Token); await Wait(f.Entered.Task); cancellation.Cancel();
        await Error(() => task.WaitAsync(TimeSpan.FromSeconds(2)), "CANCELLED");
        var joined = f.Refresh();
        f.Release.TrySetResult(); await joined; Check(f.Wrapper.Writes == 0);
    }
    private static async Task Deadline()
    {
        await using var f = new MusicAccountDetailsFixture("wy");
        await f.Seed(); f.HoldResponse = true;
        var task = f.Refresh();
        Task close = Task.CompletedTask;
        try
        {
            await Wait(f.Entered.Task);
            await f.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Error(() => task, "TIMEOUT");
            close = f.Owner.DisposeAsync().AsTask();
            await Pending(close);
            Check(f.Wrapper.Disposals == 0 && f.Wrapper.Writes == 0,
                "the timed-out subscriber does not release the physical owner or its borrowed store");
        }
        finally { f.Release.TrySetResult(); await close; }
        Check(f.Steps.Count == 1 && f.Wrapper.Writes == 0);
    }
    private static async Task CancelRead()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.Wrapper.HoldRead = true;
        using var cancellation = new CancellationTokenSource();
        var task = f.Owner.ReadDetailsAsync("wy", cancellation.Token); await Wait(f.Wrapper.Entered.Task);
        cancellation.Cancel(); await Pending(task); f.Wrapper.Release.TrySetResult();
        await Error(() => task, "CANCELLED"); Check(f.Steps.Count == 0);
    }
    private static Task ExternalReplace() => ExternalReplace("AUTH_EXPIRED");
    private static async Task ExternalReplace(string code)
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.HoldResponse = true; f.FailureCode = code;
        var old = f.Refresh(); await Wait(f.Entered.Task);
        await f.Mutate(doc => {
            var account = MusicAccountDetailsFixture.AccountRow(doc);
            account["generation"] = 2; account["scope"] = "replacement-scope";
            account["profile"]!["displayName"] = "Replacement";
        });
        var replacement = await f.Owner.ReadDetailsAsync("wy"); Check(replacement.Generation == 2 && replacement.State == "saved");
        f.Release.TrySetResult(); await Error(() => old, "SESSION_CHANGED");
        Check((await f.Owner.ReadDetailsAsync("wy")).Profile?.DisplayName == "Replacement" && f.Wrapper.Writes == 0);
        Check(await f.Owner.AcquireAsync("wy") is not null, "Old auth failure revoked replacement");
    }
    private static async Task Logout()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh(); f.HoldResponse = true;
        var task = f.Refresh(); await Wait(f.Entered.Task);
        var result = await f.Owner.CallAsync("music.accounts.logout", new { platform = "wy" });
        Check(result.GetProperty("ok").GetBoolean());
        Check((await f.Owner.ReadDetailsAsync("wy")).Profile is null);
        f.Release.TrySetResult(); await Error(() => task, "SESSION_CHANGED");
        await using var reopen = new MusicAccountDetailsFixture("wy", f.Root);
        Check((await reopen.Owner.ReadDetailsAsync("wy")).Profile is null && reopen.Steps.Count == 0);
    }
    private static async Task Conflict()
    {
        await using var f = new MusicAccountDetailsFixture("tx"); await f.Seed();
        f.CacheBacking.Conflict = true;
        Check((await f.Refresh()).Membership.Vip == true);
        await f.Cache.DrainAsync();
        Check(f.Wrapper.Writes == 0 && (await f.Document())["entries"]!.AsArray().Count == 1);
        Check((await f.Owner.ReadDetailsAsync("tx")).State == "saved");
        Check((await f.Refresh()).Membership.Vip == true);
        await f.Cache.DrainAsync();
        Check(f.Wrapper.Writes == 0);
    }
    private static async Task LostReceipt()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed();
        var accountBefore = await f.Wrapper.Inner.ReadAccountsAsync();
        f.CacheBacking.LoseReceipt = true;
        Check((await f.Refresh()).Membership.Vip == true);
        await f.Cache.DrainAsync();
        var read = await f.Owner.ReadDetailsAsync("wy");
        Check(read.State == "saved" && read.Profile?.DisplayName == "Fresh name");
        Check((await f.Refresh()).Membership.Vip == true && f.Steps.Count == 2);
        Check(await f.Owner.AcquireAsync("wy") is not null);
        var accountAfter = await f.Wrapper.Inner.ReadAccountsAsync();
        Check(accountBefore.Revision == accountAfter.Revision &&
            JsonNode.DeepEquals(JsonNode.Parse(accountBefore.Data!), JsonNode.Parse(accountAfter.Data!)),
            "Display cache writes changed the authoritative account.");
    }
    private static async Task CancelWrite()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed();
        f.CacheBacking.Hold = true;
        var result = await f.Refresh().WaitAsync(TimeSpan.FromSeconds(4));
        await Wait(f.CacheBacking.Entered.Task);
        Check(result.Membership.Vip == true && result.State == "saved");
        var logout = await f.Owner.CallAsync("music.accounts.logout", new { platform = "wy" }).WaitAsync(TimeSpan.FromSeconds(4));
        Check(logout.GetProperty("ok").GetBoolean());
        Check((await f.Owner.ReadDetailsAsync("wy")).Profile is null);
        // Stopping readers does not claim that accepted cache write receipts
        // have settled. Final owner retirement still waits for those receipts.
        await f.Owner.PrepareReadShutdownAsync().WaitAsync(TimeSpan.FromSeconds(4));
        var retirement = f.Owner.DisposeAsync().AsTask(); await Pending(retirement);
        f.CacheBacking.Release.TrySetResult(); await Wait(retirement);
        Check(f.CacheBacking.Disposals == 0 && f.Wrapper.Disposals == 0);
        await using var reopened = new MusicAccountDetailsFixture("wy", f.Root);
        Check((await reopened.Owner.ReadDetailsAsync("wy")).Profile is null);
    }
    private static async Task TwoOwners()
    {
        await using var a = new MusicAccountDetailsFixture("kg"); await a.Seed();
        await using var b = new MusicAccountDetailsFixture("kg", a.Root);
        a.HoldResponse = b.HoldResponse = true;
        var first = a.Refresh(); var second = b.Refresh(); await Wait(Task.WhenAll(a.Entered.Task, b.Entered.Task));
        a.Release.TrySetResult(); Check((await first).Membership.Vip == true);
        b.Release.TrySetResult(); Check((await second).Membership.Vip == true);
        await Task.WhenAll(a.Cache.DrainAsync(), b.Cache.DrainAsync());
        Check(a.Wrapper.Writes == 0 && b.Wrapper.Writes == 0);
        Check((await b.Owner.ReadDetailsAsync("kg")).Membership.Vip == true);
    }
    private static async Task CacheBinding()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh();
        await f.Mutate(doc => {
            var row = MusicAccountDetailsFixture.AccountRow(doc);
            row["generation"] = 2; row["scope"] = "NEW_SCOPE"; row["profile"]!["displayName"] = "New account";
        });
        var value = await f.Owner.ReadDetailsAsync("wy");
        Check(value.Generation == 2 && value.Profile?.DisplayName == "New account" && value.Membership.Vip is null &&
            value.MembershipFreshness == MusicAccountDetailsFreshness.Missing && f.Steps.Count == 1);
    }
    private static async Task Corrupt()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh();
        await f.MutateCache(doc => MusicAccountDetailsFixture.DetailsRow(doc)["profile"] = "unusable");
        var miss = await f.Owner.ReadDetailsAsync("wy");
        Check(miss.Profile?.DisplayName == "Saved name" && miss.Membership.Vip is null);
        Check((await f.Refresh()).Membership.Vip == true && f.Steps.Count == 2 && f.Wrapper.Writes == 0);
        Check((await f.Owner.ReadDetailsAsync("wy")).Profile?.DisplayName == "Fresh name");
        await using var noRead = new MusicAccountDetailsFixture("wy", f.Root);
        noRead.CacheBacking.FailRead = true;
        Check((await noRead.Owner.ReadDetailsAsync("wy")).Membership.Vip is null);
        Check((await noRead.Refresh()).Membership.Vip == true && noRead.Wrapper.Writes == 0);
    }
    private static async Task SafeDto()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); var result = await f.Refresh();
        string text = JsonSerializer.Serialize(result);
        foreach (string field in new[] { "cookie", "token", "scope", "device", "qimei", "SYNTHETIC" })
            Check(!text.Contains(field, StringComparison.OrdinalIgnoreCase), "Sensitive display field");
        Check(result.RecheckAt == Math.Min(result.ProfileExpiresAt, result.MembershipRecheckAt));
        f.FailureCode = "INVALID_RESPONSE";
        await Error(() => f.Refresh(), "INVALID_RESPONSE");
        Check(f.Wrapper.Writes == 0);
    }
}
