// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real owner/protocol and Windows DPAPI. Only the HTTP peer is synthetic.
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
            yield return Case(p + "-profile-identity-mismatch-never-commits", () => IdentityFailure(p));
            yield return Case(p + "-rights-network-unknown-no-logout", () => Unknown(p));
            yield return Case(p + "-last-cancel-awaits-real-HTTP-settle", () => CancelHttp(p));
        }
        yield return Case("WY-upstream-clock-absolute-expiry-levels-and-unknown", WySnapshots);
        yield return Case("QQ-native-wire-aliases-precedence-flags-and-boundaries", QqSnapshots);
        yield return Case("KG-union-products-identity-start-and-unknown-retry", KgSnapshots);
        yield return Case("calendar-dates-China-time-no-local-time-guess", Dates);
        yield return Case("WY-QQ-unknown-refresh-keeps-known-persistent-rights", PreserveKnown);
        yield return Case("expired-local-rights-unknown-keeps-absolute-expiry", Stale);
        yield return Case("profile-network-does-not-revoke-or-write", ProfileOutage);
        yield return Case("coalesced-refresh-one-cancel-does-not-cancel-other", Coalesce);
        yield return Case("close-awaits-physical-and-borrows-store-http", Close);
        yield return Case("cancel-during-async-stream-disposal-cannot-publish", DisposeStream);
        yield return Case("protocol-total-deadline-awaits-noncooperative-handler", Deadline);
        yield return Case("local-read-cancel-awaits-real-store", CancelRead);
        yield return Case("external-account-replace-old-auth-cannot-revoke-new", ExternalReplace);
        yield return Case("logout-fences-refresh-retained-cache-never-reappears", Logout);
        yield return Case("CAS-conflict-no-replay-no-overwrite", Conflict);
        yield return Case("lost-CAS-receipt-explicit-local-readback-only", LostReceipt);
        yield return Case("cancel-admitted-CAS-drains-unknown-and-owner-closes", CancelWrite);
        yield return Case("two-real-owners-same-cache-revision-one-winner", TwoOwners);
        yield return Case("new-account-cache-binding-no-cross-session-display", CacheBinding);
        yield return Case("corrupt-new-row-not-overwritten-no-HTTP", Corrupt);
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
            Check(result.Generation == 1 && result.State == "verified" && result.Profile?.DisplayName == "Fresh name" &&
                result.Profile.UserId == UserId && result.Membership.Vip == true && result.Membership.ExpiresAt > 0 &&
                result.ProfileFreshness == MusicAccountDetailsFreshness.Fresh &&
                result.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
            Check(result.Membership.Tier == (platform == "wy" ? "vip" : platform == "tx" ? "huge_vip" : "concept:svip"));
            Check(result.AudioLevels.Count == (platform == "wy" ? 6 : 0) && f.Owner.IsCurrent(session!));
            Check(f.Steps.SequenceEqual(platform == "tx" ? ["auth", "profile", "rights"] : new[] { "profile", "rights" }));
            var doc = await f.Document();
            Check(MusicAccountDetailsFixture.AccountRow(doc).ToJsonString() == accountBefore, "Account row changed");
            Check(doc["entries"]!.AsArray().Count == 2, "Unexpected account document entry");
            var cache = MusicAccountDetailsFixture.DetailsRow(doc);
            Check(cache["revision"]!.GetValue<int>() == 1 && cache["accountGeneration"]!.GetValue<int>() == 1);
            foreach (var file in Directory.EnumerateFiles(root))
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
        Check(reopened.Steps.Count == (platform == "tx" ? 3 : 2), "Explicit refresh was a cache hit");
    }
    private static async Task WrongGeneration(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        await Error(() => f.Owner.RefreshDetailsAsync(platform, 0), "SESSION_CHANGED");
        await Error(() => f.Owner.RefreshDetailsAsync(platform, 2), "SESSION_CHANGED");
        Check(f.Steps.Count == 0 && f.Wrapper.Writes == 0);
    }
    private static async Task AuthFailure(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        var session = await f.Owner.AcquireAsync(platform);
        int events = 0; f.Owner.SessionChanged += _ => events++;
        f.Fault = "auth";
        await Error(() => f.Refresh(), "AUTH_EXPIRED");
        Check(!f.Owner.IsCurrent(session!) && await f.Owner.AcquireAsync(platform) is null && events > 0);
        var hidden = await f.Owner.ReadDetailsAsync(platform);
        Check(hidden.State == "revoked" && hidden.Profile is null && hidden.Membership.Vip is null && f.Wrapper.Writes == 0);
        Check(MusicAccountDetailsFixture.AccountRow(await f.Document())["cookie"] is not null, "Failure invented durable logout");
    }
    private static async Task IdentityFailure(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed();
        f.Change = (step, data) => {
            if (step == "profile")
            {
                if (platform == "wy") data["profile"]!["userId"] = "7654321";
                else if (platform == "tx") data["req_0"]!["data"]!["Info"]!["BaseInfo"]!["EncryptedUin"] = "WRONG_EUIN";
                else data["data"]!["userid"] = "7654321";
            }
            return data;
        };
        await Error(() => f.Refresh(), "IDENTITY_MISMATCH");
        Check(!f.Steps.Contains("rights") && f.Wrapper.Writes == 0);
        Check((await f.Owner.ReadDetailsAsync(platform)).Profile is null);
    }
    private static async Task Unknown(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed(); f.Fault = "network";
        var result = await f.Refresh();
        Check(result.State == "verified" && result.Profile?.DisplayName == "Fresh name" && result.Membership.Vip is null &&
            result.VerificationDeferred && result.ErrorCode == "NETWORK_ERROR" && result.MembershipRecheckAt == 0 &&
            result.MembershipFreshness == MusicAccountDetailsFreshness.Deferred);
        Check(platform == "kg" ? result.RetryAt == result.MembershipCheckedAt + 60000 : result.RetryAt == 0);
        Check(await f.Owner.AcquireAsync(platform) is not null);
        int calls = f.Steps.Count;
        Check((await f.Owner.ReadDetailsAsync(platform)).Membership.Vip is null && f.Steps.Count == calls);
    }
    private static async Task CancelHttp(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform); await f.Seed(); f.HoldStep = "rights";
        using var cancellation = new CancellationTokenSource();
        var refresh = f.Refresh(cancellation.Token);
        await Wait(f.Entered.Task); cancellation.Cancel(); await Wait(f.Cancelled.Task); await Pending(refresh);
        f.Release.TrySetResult(); await Error(() => refresh, "CANCELLED");
        Check(f.Wrapper.Writes == 0 && (await f.Owner.ReadDetailsAsync(platform)).Profile?.DisplayName == "Saved name");
    }
    private static Task WySnapshots()
    {
        const long now = 1800000000000, upstream = now - 10000;
        var data = new JsonObject { ["userId"] = UserId, ["now"] = upstream,
            ["associator"] = new JsonObject { ["expireTime"] = upstream + 100000 },
            ["redplus"] = new JsonObject { ["expireTime"] = upstream + 50000 } };
        var svip = AccountDetailsSnapshot.Wy(data, UserId, now);
        Check(svip.Membership.Tier == "svip" && svip.Membership.ExpiresAt == upstream + 50000 &&
            svip.RecheckAt == now + 50000 && svip.AudioLevels.Count == 10);
        data["redplus"]!["expireTime"] = upstream - 1;
        var vip = AccountDetailsSnapshot.Wy(data, UserId, now);
        Check(vip.Membership.Tier == "vip" && vip.AudioLevels.Count == 6);
        data["associator"]!["expireTime"] = upstream - 2;
        var none = AccountDetailsSnapshot.Wy(data, UserId, now);
        Check(none.Membership.Vip == false && none.Membership.ExpiresAt == upstream - 1 && none.RecheckAt == now + 86400000);
        data["associator"] = "invalid"; data["redplus"] = new JsonObject { ["expireTime"] = "1800000000000" };
        var unknown = AccountDetailsSnapshot.Wy(data, UserId, now);
        Check(unknown.Membership.Vip is null && unknown.RecheckAt == 0 && unknown.AudioLevels.Count == 0);
        return Error(() => Task.FromResult(AccountDetailsSnapshot.Wy(data, "7654321", now)), "IDENTITY_MISMATCH");
    }
    private static Task QqSnapshots()
    {
        const long now = 1800000000000;
        var data = new JsonObject { ["svip"] = 1, ["userinfo"] = new JsonObject { ["expire"] = now + 900000 },
            ["identity"] = new JsonObject { ["HugeVip"] = 1, ["HugeVipEnd"] = now + 50000 } };
        var result = AccountDetailsSnapshot.Qq(data, now);
        Check(result.Membership.Tier == "svip" && result.Membership.ExpiresAt == now + 900000 && result.RecheckAt == now + 50000);
        data["svip"] = 0; data["identity"]!["HugeVipStart"] = now + 20000;
        result = AccountDetailsSnapshot.Qq(data, now);
        Check(result.Membership.Vip == false && result.RecheckAt == now + 20000);
        Check(AccountDetailsSnapshot.Qq(new(), now).Membership.Vip is null, "Python model defaults leaked");
        Check(AccountDetailsSnapshot.Qq(new() { ["identity"] = new JsonObject { ["huge_vip"] = 1 } }, now).Membership.Vip is null);
        Check(AccountDetailsSnapshot.Qq(new() { ["star"] = 0, ["ystar"] = "1" }, now).Membership.Vip is null);
        Check(AccountDetailsSnapshot.Qq(new() { ["star"] = 0, ["ystar"] = true }, now).Membership.Vip is null);
        foreach (var field in new[] { "HugeVip", "twelve", "eight", "GroupVipFlag", "CPLoverFlag", "ChildVip", "ExpVip", "AdVipFlag" })
            Check(AccountDetailsSnapshot.Qq(new() { ["identity"] = new JsonObject { [field] = 1 } }, now).Membership.Vip == true, field);
        var star = AccountDetailsSnapshot.Qq(new() { ["star"] = 1, ["starend"] = now - 1 }, now);
        Check(star.Membership.Vip == false && star.Membership.ExpiresAt == now - 1 && star.RecheckAt == now + 86400000);
        return Task.CompletedTask;
    }
    private static async Task KgSnapshots()
    {
        const long now = 1800000000000;
        JsonObject Row(string type, int flag, long end) => new() { ["busi_type"] = "concept",
            ["product_type"] = type, ["is_vip"] = flag, ["vip_end_time"] = end };
        var row = Row("qvip", 1, now + 120000);
        var data = new JsonObject { ["userid"] = UserId, ["is_vip"] = 0, ["busi_vip"] = new JsonArray(row) };
        Check(AccountDetailsSnapshot.Kg(data, UserId, now).Membership.Tier == "concept:qvip");
        row["vip_begin_time"] = now + 1000;
        var later = AccountDetailsSnapshot.Kg(data, UserId, now);
        Check(later.Membership.Vip == false && later.RecheckAt == now + 1000);
        row["is_vip"] = "1";
        Check(AccountDetailsSnapshot.Kg(data, UserId, now).Membership.Vip is null);
        row["userid"] = "7654321";
        await Error(() => Task.FromResult(AccountDetailsSnapshot.Kg(data, UserId, now)), "IDENTITY_MISMATCH");
        var unknown = AccountDetailsSnapshot.Kg(new() { ["vip_type"] = 9, ["level"] = 10 }, UserId, now);
        Check(unknown.Membership.Vip is null && unknown.RecheckAt == 0 && unknown.RetryAt == now + 60000);
        var noExpiry = AccountDetailsSnapshot.Kg(new() { ["is_vip"] = 1 }, UserId, now);
        Check(noExpiry.Membership.Vip == true && noExpiry.RecheckAt == now + 86400000 &&
            noExpiry.Membership.ExpiresAt is null && !noExpiry.VerificationDeferred && noExpiry.RetryAt == 0);
        var expired = AccountDetailsSnapshot.Kg(new() { ["is_vip"] = 1, ["vip_end_time"] = now - 1000 }, UserId, now);
        Check(expired.Membership.Vip == false && expired.Membership.ExpiresAt == now - 1000);
    }
    private static Task Dates()
    {
        long date = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        foreach (string value in new[] { "2026-10-23", "2026-10-23 00:00:00", "2026-10-23T00:00:00+08:00", "2026-10-22T16:00:00Z" })
            Check(AccountDetailsSnapshot.Expiry(JsonValue.Create(value)) == date);
        Check(AccountDetailsSnapshot.Expiry(JsonValue.Create("2026-10-23T00:00:00")) is null);
        Check(AccountDetailsSnapshot.Expiry(JsonValue.Create(date / 1000)) == date);
        Check(AccountDetailsSnapshot.Expiry(JsonValue.Create(date.ToString())) == date);
        Check(AccountDetailsSnapshot.Expiry(JsonValue.Create("invalid")) is null);
        return Task.CompletedTask;
    }
    private static async Task PreserveKnown()
    {
        foreach (string platform in new[] { "wy", "tx" })
        {
            await using var f = new MusicAccountDetailsFixture(platform); await f.Seed(); await f.Refresh();
            string rights = MusicAccountDetailsFixture.DetailsRow(await f.Document())["rights"]!.ToJsonString();
            f.Fault = "network";
            Check((await f.Refresh()).Membership.Vip is null);
            Check(MusicAccountDetailsFixture.DetailsRow(await f.Document())["rights"]!.ToJsonString() == rights);
            Check((await f.Owner.ReadDetailsAsync(platform)).Membership.Vip == true);
        }
    }
    private static async Task Stale()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh();
        long expiry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
        await f.Mutate(doc => {
            var row = MusicAccountDetailsFixture.DetailsRow(doc);
            row["profileFetchedAt"] = expiry - AccountDetailsSnapshot.ProfileTtl; row["profileExpiresAt"] = expiry;
            row["rights"]!["checkedAt"] = expiry - 5000; row["rights"]!["recheckAt"] = expiry; row["rights"]!["expiresAt"] = expiry;
        });
        var value = await f.Owner.ReadDetailsAsync("wy");
        Check(value.ProfileFreshness == MusicAccountDetailsFreshness.Stale && value.MembershipFreshness == MusicAccountDetailsFreshness.Stale &&
            value.Membership.Vip is null && value.Membership.ExpiresAt == expiry && value.AudioLevels.Count == 0 && f.Steps.Count == 2);
    }
    private static async Task ProfileOutage()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.Fault = "network"; f.FaultStep = "profile";
        await Error(() => f.Refresh(), "NETWORK_ERROR");
        Check((await f.Owner.ReadDetailsAsync("wy")).State == "saved" && f.Wrapper.Writes == 0 && !f.Steps.Contains("rights"));
    }
    private static async Task Coalesce()
    {
        await using var f = new MusicAccountDetailsFixture("tx"); await f.Seed(); f.HoldStep = "rights";
        using var firstCancel = new CancellationTokenSource();
        var first = f.Refresh(firstCancel.Token); await Wait(f.Entered.Task);
        int reads = f.Wrapper.Reads;
        var second = f.Refresh(); await Until(() => f.Wrapper.Reads > reads); await Task.Delay(20);
        firstCancel.Cancel(); await Error(() => first, "CANCELLED");
        Check(!f.Cancelled.Task.IsCompleted && !second.IsCompleted);
        f.Release.TrySetResult(); Check((await second).Membership.Vip == true && f.Wrapper.Writes == 1 && f.Steps.Count == 3);
    }
    private static async Task Close()
    {
        await using var f = new MusicAccountDetailsFixture("kg"); await f.Seed(); f.HoldStep = "rights";
        var refresh = f.Refresh(); await Wait(f.Entered.Task);
        var close = f.Owner.DisposeAsync().AsTask(); await Wait(f.Cancelled.Task); await Pending(close); await Pending(refresh);
        f.Release.TrySetResult(); await Error(() => refresh, "CLOSED"); await Wait(close);
        Check(f.Wrapper.Disposals == 0 && f.Wrapper.Writes == 0);
        using var req = Request("wy");
        var response = await f.Http.SendAccountDetailsAsync("wy", req, default, TimeSpan.FromSeconds(1));
        Check(response.Body is not null, "Owner disposed borrowed HTTP");
    }
    private static async Task DisposeStream()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.HoldDisposal = true;
        using var cancellation = new CancellationTokenSource();
        var task = f.Refresh(cancellation.Token); await Wait(f.Entered.Task); cancellation.Cancel(); await Pending(task);
        f.Release.TrySetResult(); await Error(() => task, "CANCELLED"); Check(f.Wrapper.Writes == 0);
    }
    private static async Task Deadline()
    {
        await using var f = new MusicAccountDetailsFixture("wy", timeout: TimeSpan.FromMilliseconds(70));
        await f.Seed(); f.HoldStep = "profile";
        var task = f.Refresh(); await Wait(f.Entered.Task); await Wait(f.Cancelled.Task); await Pending(task);
        f.Release.TrySetResult(); await Error(() => task, "TIMEOUT");
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
    private static async Task ExternalReplace()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.HoldStep = "rights"; f.Fault = "auth";
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
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh(); f.HoldStep = "rights";
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
        await using var f = new MusicAccountDetailsFixture("tx"); await f.Seed(); f.Wrapper.Conflict = true;
        await Error(() => f.Refresh(), "STORAGE_CONFLICT");
        Check(f.Wrapper.Writes == 1 && (await f.Document())["entries"]!.AsArray().Count == 1);
        Check((await f.Owner.ReadDetailsAsync("tx")).State == "saved");
        f.Wrapper.Conflict = false; await f.Refresh(); Check(f.Wrapper.Writes == 2);
    }
    private static async Task LostReceipt()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.Wrapper.LoseReceipt = true;
        await Error(() => f.Refresh(), "COMMIT_UNKNOWN");
        await Error(() => f.Refresh(), "COMMIT_UNKNOWN");
        Check(f.Wrapper.Writes == 1 && f.Steps.Count == 2);
        var read = await f.Owner.ReadDetailsAsync("wy");
        Check(read.State == "saved" && read.Profile?.DisplayName == "Fresh name" && f.Steps.Count == 2);
        f.Wrapper.LoseReceipt = false; await f.Refresh(); Check(f.Wrapper.Writes == 2);
    }
    private static async Task CancelWrite()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); f.Wrapper.Hold = true;
        using var cancellation = new CancellationTokenSource();
        var task = f.Refresh(cancellation.Token); await Wait(f.Wrapper.Entered.Task); cancellation.Cancel(); await Pending(task);
        f.Wrapper.Release.TrySetResult(); await Error(() => task, "COMMIT_UNKNOWN");
        var observed = await f.Owner.ReadDetailsAsync("wy"); Check(observed.Profile?.DisplayName == "Fresh name");
        await Wait(f.Owner.DisposeAsync().AsTask()); Check(f.Wrapper.Disposals == 0);
    }
    private static async Task TwoOwners()
    {
        await using var a = new MusicAccountDetailsFixture("kg"); await a.Seed();
        await using var b = new MusicAccountDetailsFixture("kg", a.Root);
        a.HoldStep = b.HoldStep = "rights";
        var first = a.Refresh(); var second = b.Refresh(); await Wait(Task.WhenAll(a.Entered.Task, b.Entered.Task));
        a.Release.TrySetResult(); await first;
        b.Release.TrySetResult(); await Error(() => second, "STORAGE_CONFLICT");
        Check(a.Wrapper.Writes == 1 && b.Wrapper.Writes == 0);
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
            value.MembershipFreshness == MusicAccountDetailsFreshness.Missing && f.Steps.Count == 2);
    }
    private static async Task Corrupt()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); await f.Refresh();
        await f.Mutate(doc => MusicAccountDetailsFixture.DetailsRow(doc)["version"] = 2);
        await Error(() => f.Owner.ReadDetailsAsync("wy"), "CORRUPT_STORAGE");
        await Error(() => f.Refresh(), "CORRUPT_STORAGE");
        Check(f.Steps.Count == 2 && f.Wrapper.Writes == 1);
    }
    private static async Task SafeDto()
    {
        await using var f = new MusicAccountDetailsFixture("wy"); await f.Seed(); var result = await f.Refresh();
        string text = JsonSerializer.Serialize(result);
        foreach (string field in new[] { "cookie", "token", "scope", "device", "qimei", "SYNTHETIC" })
            Check(!text.Contains(field, StringComparison.OrdinalIgnoreCase), "Sensitive display field");
        Check(result.RecheckAt == Math.Min(result.ProfileExpiresAt, result.MembershipRecheckAt));
        f.Change = (step, value) => { if (step == "profile") value["profile"]!["nickname"] = "SYNTHETIC_WY_DETAILS_TOKEN"; return value; };
        await Error(() => f.Refresh(), "INVALID_RESPONSE");
        Check(f.Wrapper.Writes == 1);
    }
}
