// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeAccountDetailsTests
{
    private static IEnumerable<TestCase> LoginCases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            var p = platform;
            yield return Case(p + "-login-profile-rights-one-CAS-cold-local-reopen", () => LoginDetails(p));
            yield return Case(p + "-login-awaits-rights-before-saving-account", () => LoginAwaitRights(p));
            yield return Case(p + "-login-non-VIP-24h-cache-changes-only-after-manual-refresh", () => LoginNegativeCache(p));
            yield return Case(p + "-login-rights-outage-remains-unknown-not-non-VIP", () => LoginUnknown(p));
            yield return Case(p + "-login-rights-auth-failure-never-saves-account", () => LoginRightsAuth(p));
            yield return Case(p + "-login-cancel-rights-never-saves-account-or-details", () => LoginCancelRights(p));
        }
        yield return Case("login-replacement-fences-late-rights-and-keeps-new-cache", LoginReplacement);
        yield return Case("login-success-does-not-await-first-cache-disk-read", LoginSlowCache);
        yield return Case("default-display-cache-never-borrows-credential-store", LoginDefaultCache);
        yield return Case("login-CAS-conflict-never-saves-partial-account-or-retries", LoginDetailsConflict);
        yield return Case("login-lost-CAS-receipt-local-observation-recovers-account-without-cache-proof", LoginDetailsReceipt);
    }
    private static Task<JsonElement> Login(MusicAccountDetailsFixture f, CancellationToken token = default, string? cookie = null) =>
        f.Owner.CallAsync("music.accounts.import",
            new { platform = f.Platform, cookie = cookie ?? MusicAccountDetailsFixture.Cookie(f.Platform) }, token);
    private static void LoginOk(JsonElement result) =>
        Check(result.GetProperty("ok").GetBoolean(), result.TryGetProperty("code", out var code) ? code.GetString()! : "Login failed");
    private static void LoginError(JsonElement result, params string[] codes) =>
        Check(!result.GetProperty("ok").GetBoolean() && codes.Contains(result.GetProperty("code").GetString()),
            "Expected finite login error " + string.Join("/", codes));
    private static async Task MissingLoginDocument(MusicAccountDetailsFixture f)
    {
        var document = await f.Wrapper.Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
        try { Check(document.Data is null && !document.Fingerprint.Exists, "A partial login document was saved"); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
    }
    private static async Task LoginDetails(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        LoginOk(await Login(f));
        var local = await f.Owner.ReadDetailsAsync(platform);
        Check(local.State == "verified" && local.Generation == 1 && local.Profile?.DisplayName == "Fresh name" &&
            local.Profile.UserId == UserId && local.Profile.Avatar == "https://images.invalid/avatar" &&
            local.Membership.Vip == true && local.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
        Check(f.Steps.SequenceEqual(platform == "tx" ? ["auth", "profile", "rights"] : new[] { "profile", "rights" }) &&
            f.Wrapper.Writes == 1, "Login must authenticate once, fetch rights once and commit once");
        var root = await f.Document();
        var account = MusicAccountDetailsFixture.AccountRow(root);
        var cache = MusicAccountDetailsFixture.DetailsRow(await f.CacheDocument());
        Check(root["entries"]!.AsArray().Count == 1 &&
            cache["accountGeneration"]!.GetValue<long>() == account["generation"]!.GetValue<long>() &&
            cache["accountScope"]!.GetValue<string>() == account["scope"]!.GetValue<string>());
        await f.Cache.DrainAsync();
        await using var reopen = new MusicAccountDetailsFixture(platform, f.Root);
        var restored = await reopen.Owner.ReadDetailsAsync(platform);
        Check(restored.State == "saved" && restored.Profile == local.Profile && restored.Membership == local.Membership &&
            restored.MembershipCheckedAt == local.MembershipCheckedAt && restored.MembershipRecheckAt == local.MembershipRecheckAt &&
            restored.MembershipFreshness == MusicAccountDetailsFreshness.Fresh && reopen.Steps.Count == 0);
        for (int i = 0; i < 3; i++) _ = await f.Owner.ReadDetailsAsync(platform);
        Check(f.Steps.Count(step => step == "rights") == 1 && f.Wrapper.Writes == 1, "A local read refreshed login rights");
    }
    private static async Task LoginSlowCache()
    {
        await using var f = new MusicAccountDetailsFixture("wy");
        f.CacheBacking.HoldRead = true;
        LoginOk(await Login(f).WaitAsync(TimeSpan.FromSeconds(4)));
        await Wait(f.CacheBacking.Entered.Task);
        Check(f.Wrapper.Writes == 1);
        var local = await f.Owner.ReadDetailsAsync("wy").WaitAsync(TimeSpan.FromSeconds(4));
        Check(local.State == "verified" && local.Membership.Vip == true);
        var draining = f.Cache.DrainAsync(); await Pending(draining);
        f.CacheBacking.Release.TrySetResult(); await Wait(draining);
    }
    private static async Task LoginDefaultCache()
    {
        await using var f = new MusicAccountDetailsFixture("wy");
        LoginOk(await Login(f));
        await using var independent = f.Clients.CreateAccountOwner(f.Wrapper);
        var snapshot = await independent.ReadDetailsAsync("wy");
        Check(snapshot.State == "saved" && snapshot.Profile?.UserId == UserId && snapshot.Membership.Vip is null &&
            snapshot.MembershipFreshness == MusicAccountDetailsFreshness.Missing && f.Wrapper.Writes == 1);
    }
    private static async Task LoginAwaitRights(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        f.HoldStep = "rights";
        var login = Login(f);
        await Wait(f.Entered.Task);
        await Pending(login);
        Check(f.Wrapper.Writes == 0 && await f.Owner.AcquireAsync(platform) is null);
        await MissingLoginDocument(f);
        f.Release.TrySetResult();
        LoginOk(await login);
        Check((await f.Owner.ReadDetailsAsync(platform)).Membership.Vip == true && f.Wrapper.Writes == 1);
    }
    private static JsonObject LoginRights(JsonObject response, string platform, bool vip)
    {
        if (platform == "wy")
            response["data"]!["associator"]!["expireTime"] = vip
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30 * AccountDetailsSnapshot.Day : 0;
        else if (platform == "tx")
            response["req_0"]!["data"]!["identity"]!["HugeVip"] = vip ? 1 : 0;
        else
        {
            response["data"]!["busi_vip"]![0]!["is_vip"] = vip ? 1 : 0;
            response["data"]!["busi_vip"]![0]!["vip_end_time"] = vip
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30 * AccountDetailsSnapshot.Day : 0;
        }
        return response;
    }
    private static async Task LoginNegativeCache(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        bool vip = false;
        f.Change = (step, value) => step == "rights" ? LoginRights(value, platform, vip) : value;
        LoginOk(await Login(f));
        var negative = await f.Owner.ReadDetailsAsync(platform);
        Check(negative.Membership.Vip == false && negative.MembershipFreshness == MusicAccountDetailsFreshness.Fresh &&
            negative.MembershipRecheckAt == negative.MembershipCheckedAt + AccountDetailsSnapshot.Day);
        vip = true;
        var cached = await f.Owner.ReadDetailsAsync(platform);
        Check(cached.Membership.Vip == false && cached.MembershipCheckedAt == negative.MembershipCheckedAt &&
            f.Steps.Count(step => step == "rights") == 1 && f.Wrapper.Writes == 1);
        var refreshed = await f.Owner.RefreshDetailsAsync(platform, cached.Generation);
        Check(refreshed.Membership.Vip == true && refreshed.Generation == cached.Generation &&
            f.Steps.Count(step => step == "rights") == 2 && f.Wrapper.Writes == 1);
        Check((await f.Owner.ReadDetailsAsync(platform)).Membership.Vip == true);
    }
    private static async Task LoginUnknown(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        f.Fault = "network";
        LoginOk(await Login(f));
        var local = await f.Owner.ReadDetailsAsync(platform);
        Check(local.State == "verified" && local.Profile?.UserId == UserId &&
            local.Membership.Vip is null && local.VerificationDeferred &&
            local.MembershipFreshness == MusicAccountDetailsFreshness.Deferred && local.ErrorCode == "NETWORK_ERROR" &&
            f.Steps.Count(step => step == "rights") == 1 && f.Wrapper.Writes == 1);
    }
    private static async Task LoginRightsAuth(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        f.Fault = "auth";
        LoginError(await Login(f), "AUTH_EXPIRED");
        Check(f.Steps.Count(step => step == "rights") == 1 && f.Wrapper.Writes == 0);
        await MissingLoginDocument(f);
        Check((await f.Owner.ReadDetailsAsync(platform)).State == "none");
    }
    private static async Task LoginCancelRights(string platform)
    {
        await using var f = new MusicAccountDetailsFixture(platform);
        f.HoldStep = "rights";
        using var cancellation = new CancellationTokenSource();
        var login = Login(f, cancellation.Token);
        await Wait(f.Entered.Task);
        cancellation.Cancel();
        await Wait(f.Cancelled.Task);
        f.Release.TrySetResult();
        LoginError(await login, "CANCELLED");
        await f.Owner.DisposeAsync();
        Check(f.Wrapper.Writes == 0);
        await MissingLoginDocument(f);
    }
    private static async Task LoginReplacement()
    {
        await using var f = new MusicAccountDetailsFixture("wy");
        f.HoldStep = "rights";
        var old = Login(f);
        await Wait(f.Entered.Task);
        f.HoldStep = null;
        f.Change = (step, value) => step == "rights" ? LoginRights(value, "wy", false) : value;
        LoginOk(await Login(f, cookie: "MUSIC_U=SYNTHETIC_REPLACEMENT_SESSION; __csrf=SYNTHETIC_CSRF"));
        var current = await f.Owner.ReadDetailsAsync("wy");
        Check(current.Generation == 1 && current.Membership.Vip == false && f.Wrapper.Writes == 1);
        f.Release.TrySetResult();
        LoginError(await old, "CANCELLED", "SUPERSEDED", "STALE_SESSION");
        var after = await f.Owner.ReadDetailsAsync("wy");
        Check(after.Generation == current.Generation && after.Membership == current.Membership &&
            after.MembershipCheckedAt == current.MembershipCheckedAt && f.Wrapper.Writes == 1);
    }
    private static async Task LoginDetailsConflict()
    {
        await using var f = new MusicAccountDetailsFixture("wy");
        f.Wrapper.Conflict = true;
        LoginError(await Login(f), "STORAGE_CONFLICT");
        Check(f.Wrapper.Writes == 1 && f.Steps.Count == 2);
        await MissingLoginDocument(f);
        Check((await f.Owner.ReadDetailsAsync("wy")).State == "none" && f.Steps.Count == 2);
        f.Wrapper.Conflict = false;
        LoginOk(await Login(f));
        Check(f.Wrapper.Writes == 2 && f.Steps.Count == 4 &&
            (await f.Owner.ReadDetailsAsync("wy")).Membership.Vip == true);
    }
    private static async Task LoginDetailsReceipt()
    {
        await using var f = new MusicAccountDetailsFixture("wy");
        f.Wrapper.LoseReceipt = true;
        LoginError(await Login(f), "COMMIT_UNKNOWN");
        LoginError(await Login(f), "COMMIT_UNKNOWN");
        Check(f.Wrapper.Writes == 1 && f.Steps.Count == 2);
        LoginOk(await f.Owner.CallAsync("music.accounts.list"));
        var observed = await f.Owner.ReadDetailsAsync("wy");
        Check(observed.State == "saved" && observed.Profile?.DisplayName == "Fresh name" &&
            observed.Membership.Vip is null && f.Wrapper.Writes == 1 && f.Steps.Count == 2);
    }
}
