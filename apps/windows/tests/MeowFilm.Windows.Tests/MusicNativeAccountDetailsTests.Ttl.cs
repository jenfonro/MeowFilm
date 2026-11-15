// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeAccountDetailsTests
{
    // Provider-specific calendar, product precedence and upstream clock vectors live in Node accounts tests.
    private static IEnumerable<TestCase> TtlCases()
    {
        yield return Case("long-canonical-tier-current-cache-reopen-preserves-observation", LongTier);
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            string p = platform;
            foreach (bool? vip in new bool?[] { true, false, null })
            {
                bool? value = vip;
                yield return Case(p + "-canonical-rights-time-and-unknown-" + value, () => CanonicalTtl(p, value));
            }
            yield return Case(p + "-canonical-expiry-is-descriptive-not-authentication", async () => {
                var clock = new DetailsClock();
                await using var f = new MusicAccountDetailsFixture(p, clock: clock);
                long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                f.Change = observation => SetRights(observation, now, new(new(true, "provider-tier", null), now, now + 2000, ["provider-quality"]));
                LoginOk(await Login(f));
                var fresh = await f.Owner.ReadDetailsAsync(p);
                Check(fresh.Membership.Vip == true && fresh.Membership.ExpiresAt is null && fresh.AudioLevels.Single() == "provider-quality");
                clock.Advance(2001);
                var stale = await f.Owner.ReadDetailsAsync(p);
                Check(stale.Membership.Vip is null && stale.Membership.ExpiresAt is null && stale.AudioLevels.Count == 0 &&
                    stale.MembershipFreshness == MusicAccountDetailsFreshness.Stale && stale.State == "verified" && f.Steps.Count == 1);
                Check(await f.Owner.AcquireAsync(p) is not null);
            });
        }
    }
    private static async Task LongTier()
    {
        var clock = new DetailsClock(); long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var rights = new MusicAccountEntitlements(new(true, "provider-" + new string('t', 80), null), now, now + 120000, ["standard"]);
        string root;
        await using (var f = new MusicAccountDetailsFixture("wy", clock: clock))
        {
            root = f.Root; f.Change = observation => SetRights(observation, now, rights);
            LoginOk(await Login(f));
            var live = await f.Owner.ReadDetailsAsync("wy");
            Check(live.State == "verified" && live.Membership == rights.Membership &&
                live.AudioLevels.SequenceEqual(rights.AudioLevels) && f.Steps.SequenceEqual(["account.verify"]) && f.Wrapper.Writes == 1);
            await f.Cache.DrainAsync();
        }
        await using var cold = new MusicAccountDetailsFixture("wy", root, clock);
        var restored = await cold.Owner.ReadDetailsAsync("wy");
        Check(restored.State == "saved" && restored.Membership == rights.Membership &&
            restored.MembershipCheckedAt == now && restored.MembershipRecheckAt == rights.RecheckAt &&
            restored.AudioLevels.SequenceEqual(rights.AudioLevels) &&
            restored.MembershipFreshness == MusicAccountDetailsFreshness.Fresh && cold.Steps.Count == 0 && cold.Wrapper.Writes == 0);
    }
    private static async Task CanonicalTtl(string platform, bool? vip)
    {
        var clock = new DetailsClock();
        await using var f = new MusicAccountDetailsFixture(platform, clock: clock);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds(), expiry = now + 123456789;
        var rights = new MusicAccountEntitlements(new(vip, vip is true ? "actual-tier" : vip is false ? "none" : "unknown", expiry),
            now, vip is null ? 0 : now + 4321, vip is true ? ["actual-quality"] : [], vip is null,
            vip is null ? "NETWORK_ERROR" : null, vip is null && platform == "kg" ? now + 60000 : 0);
        f.Change = observation => SetRights(observation, now, rights);
        LoginOk(await Login(f));
        var result = await f.Owner.ReadDetailsAsync(platform);
        Check(result.Membership == rights.Membership && result.MembershipCheckedAt == now && result.MembershipRecheckAt == rights.RecheckAt &&
            result.RetryAt == rights.RetryAt && result.VerificationDeferred == rights.VerificationDeferred && result.ErrorCode == rights.ErrorCode &&
            result.AudioLevels.SequenceEqual(rights.AudioLevels) && result.ProfileFetchedAt == now && result.ProfileExpiresAt == now + 7 * 86400000L);
        Check(result.MembershipFreshness == (vip is null ? MusicAccountDetailsFreshness.Deferred : MusicAccountDetailsFreshness.Fresh));
        Check(f.Steps.SequenceEqual(["account.verify"]) && f.Wrapper.Writes == 1);
    }
    private static JsonObject SetRights(JsonObject observation, long fetched, MusicAccountEntitlements rights)
    {
        observation["profileFetchedAt"] = fetched;
        observation["entitlements"] = JsonNode.Parse(MusicPlatformRuntimeFixture.Json(rights).GetRawText());
        return observation;
    }
    private sealed class DetailsClock : TimeProvider
    {
        private long now = 1800000000000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(now);
        internal void Advance(long milliseconds) => now += milliseconds;
    }
}
