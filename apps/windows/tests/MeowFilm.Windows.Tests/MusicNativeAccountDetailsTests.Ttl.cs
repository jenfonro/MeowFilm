// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeAccountDetailsTests
{
    private const long TtlNow = 1800000000000, TtlDay = 86400000;
    private static IEnumerable<TestCase> TtlCases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            var p = platform;
            yield return Case(p + "-rights-TTL-VIP-one-day-before-expiry", () => VipTtl(p));
            yield return Case(p + "-rights-TTL-final-day-keeps-fresh-result", () => FinalDayTtl(p));
            yield return Case(p + "-rights-TTL-no-VIP-24-hours", () => NoneTtl(p));
            yield return Case(p + "-rights-TTL-unknown-and-failure-stay-unknown", () => UnknownTtl(p));
            yield return Case(p + "-rights-TTL-missing-expiry-never-invented", () => MissingExpiryTtl(p));
        }
        yield return Case("WY-rights-TTL-upstream-and-local-clock-offsets", WyClockTtl);
        foreach (string platform in new[] { "tx", "kg" })
        {
            var p = platform;
            yield return Case(p + "-rights-TTL-earlier-product-start-and-end", () => ProductBoundaryTtl(p));
        }
        yield return Case("rights-TTL-preserves-legal-integers-and-invalid-response", IntegerTtl);
    }
    private static MusicAccountEntitlements TtlMap(string platform, JsonObject data) => platform switch
    {
        "wy" => AccountDetailsSnapshot.Wy(data, UserId, TtlNow),
        "tx" => AccountDetailsSnapshot.Qq(data, TtlNow),
        "kg" => AccountDetailsSnapshot.Kg(data, UserId, TtlNow),
        _ => throw new ArgumentException(nameof(platform))
    };
    private static MusicAccountEntitlements TtlSnapshot(string platform, long? expiry, int? flag = 1)
    {
        long? wyExpiry = expiry ?? (flag == 0 ? (long?)0 : null);
        var data = platform switch
        {
            "wy" => new JsonObject { ["userId"] = UserId, ["now"] = TtlNow,
                ["associator"] = new JsonObject { ["expireTime"] = JsonValue.Create(wyExpiry) } },
            "tx" => new JsonObject { ["identity"] = new JsonObject { ["vip"] = JsonValue.Create(flag) },
                ["userinfo"] = new JsonObject { ["expire"] = JsonValue.Create(expiry) } },
            "kg" => new JsonObject { ["userid"] = UserId, ["is_vip"] = JsonValue.Create(flag),
                ["vip_end_time"] = JsonValue.Create(expiry) },
            _ => throw new ArgumentException(nameof(platform))
        };
        return TtlMap(platform, data);
    }
    private static void CheckTtl(MusicAccountEntitlements value, string platform, bool? vip,
        long? expiry, long deadline, string reason)
    {
        AccountDetailsSnapshot.Validate(value, platform);
        Check(value.Membership.Vip == vip && value.Membership.ExpiresAt == expiry &&
            value.CheckedAt == TtlNow && value.RecheckAt == deadline, reason);
        Check(vip is null ? value.Membership.Tier == "unknown" : vip == false ? value.Membership.Tier == "none" :
            value.Membership.Tier is not ("none" or "unknown"), reason);
        Check(value.VerificationDeferred == (vip is null) && value.ErrorCode is null &&
            value.RetryAt == (vip is null && platform == "kg" ? TtlNow + 60000 : 0), reason);
        Check(value.AudioLevels.Count == (vip == true && platform == "wy" ? 6 : 0), reason);
        if (vip is not null) Check(value.RecheckAt > value.CheckedAt, "Fresh rights were immediately stale: " + reason);
    }
    private static Task VipTtl(string platform)
    {
        foreach (long remaining in new[] { TtlDay + 1, 2 * TtlDay, 30 * TtlDay })
            CheckTtl(TtlSnapshot(platform, TtlNow + remaining), platform, true, TtlNow + remaining,
                TtlNow + remaining - TtlDay, platform + ": VIP remaining " + remaining);
        return Task.CompletedTask;
    }
    private static Task FinalDayTtl(string platform)
    {
        foreach (long remaining in new[] { 1L, 60000, TtlDay - 1, TtlDay })
            CheckTtl(TtlSnapshot(platform, TtlNow + remaining), platform, true, TtlNow + remaining,
                TtlNow + remaining, platform + ": final-day remaining " + remaining);
        return Task.CompletedTask;
    }
    private static Task NoneTtl(string platform)
    {
        foreach (long? expiry in new long?[] { null, TtlNow - 1, TtlNow - 3 * TtlDay })
            CheckTtl(TtlSnapshot(platform, expiry, flag: 0), platform, false, expiry,
                TtlNow + TtlDay, platform + ": no VIP, expiry " + expiry);
        return Task.CompletedTask;
    }
    private static Task UnknownTtl(string platform)
    {
        var unknown = TtlSnapshot(platform, null, flag: null);
        CheckTtl(unknown, platform, null, null, 0, platform + ": unknown is not a cached negative");
        var failure = AccountDetailsSnapshot.Unknown(TtlNow, "NETWORK_ERROR", kugou: platform == "kg");
        AccountDetailsSnapshot.Validate(failure, platform);
        Check(failure.Membership == unknown.Membership && failure.CheckedAt == TtlNow && failure.RecheckAt == 0 &&
            failure.AudioLevels.Count == 0 && failure.VerificationDeferred && failure.ErrorCode == "NETWORK_ERROR" &&
            failure.RetryAt == unknown.RetryAt, platform + ": failure semantics changed");
        return Task.CompletedTask;
    }
    private static Task MissingExpiryTtl(string platform)
    {
        bool? vip = platform == "wy" ? null : true;
        long deadline = vip == true ? TtlNow + TtlDay : 0;
        CheckTtl(TtlSnapshot(platform, null), platform, vip, null, deadline, platform + ": missing expiry");
        if (platform != "wy")
        {
            var data = platform == "tx"
                ? new JsonObject { ["identity"] = new JsonObject { ["vip"] = 1 },
                    ["userinfo"] = new JsonObject { ["expire"] = "not-a-date" } }
                : new JsonObject { ["userid"] = UserId, ["is_vip"] = 1, ["vip_end_time"] = "not-a-date" };
            CheckTtl(TtlMap(platform, data), platform, true, null, deadline, platform + ": invalid expiry was invented");
        }
        return Task.CompletedTask;
    }
    private static Task WyClockTtl()
    {
        foreach (long offset in new[] { -4 * TtlDay, 2 * TtlDay })
        {
            long upstream = TtlNow + offset;
            foreach (long remaining in new[] { 3 * TtlDay, TtlDay / 2 })
            {
                var data = new JsonObject { ["userId"] = UserId, ["now"] = upstream,
                    ["associator"] = new JsonObject { ["expireTime"] = upstream + remaining } };
                CheckTtl(TtlMap("wy", data), "wy", true, upstream + remaining,
                    TtlNow + (remaining > TtlDay ? remaining - TtlDay : remaining),
                    "WY clock offset " + offset + ", remaining " + remaining);
            }
        }
        return Task.CompletedTask;
    }
    private static Task ProductBoundaryTtl(string platform)
    {
        MusicAccountEntitlements Snapshot(long? remaining, long? start, long? end, int flag = 1, bool vip = true)
        {
            long? expiry = remaining is { } span ? TtlNow + span : null;
            var data = platform == "tx"
                ? new JsonObject { ["svip"] = vip ? 1 : 0, ["userinfo"] = new JsonObject { ["expire"] = JsonValue.Create(expiry) },
                    ["identity"] = new JsonObject { ["HugeVip"] = flag, ["HugeVipStart"] = JsonValue.Create(start),
                        ["HugeVipEnd"] = JsonValue.Create(end) } }
                : new JsonObject { ["userid"] = UserId, ["is_vip"] = 0, ["busi_vip"] = new JsonArray(
                    new JsonObject { ["busi_type"] = "concept", ["product_type"] = "svip", ["is_vip"] = vip ? 1 : 0,
                        ["vip_end_time"] = JsonValue.Create(expiry) },
                    new JsonObject { ["busi_type"] = "concept", ["product_type"] = "qvip", ["is_vip"] = flag,
                        ["vip_begin_time"] = JsonValue.Create(start), ["vip_end_time"] = JsonValue.Create(end) }) };
            var result = TtlMap(platform, data);
            if (vip) Check(result.Membership.Tier == (platform == "tx" ? "svip" : "concept:svip"), "Product precedence changed");
            return result;
        }
        foreach (long boundary in new[] { TtlDay / 2, 3 * TtlDay })
        {
            CheckTtl(Snapshot(10 * TtlDay, null, TtlNow + boundary), platform, true,
                TtlNow + 10 * TtlDay, TtlNow + boundary, platform + ": earlier product end");
            CheckTtl(Snapshot(10 * TtlDay, TtlNow + boundary, TtlNow + 5 * TtlDay), platform, true,
                TtlNow + 10 * TtlDay, TtlNow + boundary, platform + ": earlier product start");
        }
        CheckTtl(Snapshot(3 * TtlDay, TtlNow + 4 * TtlDay, TtlNow + 6 * TtlDay), platform, true,
            TtlNow + 3 * TtlDay, TtlNow + 2 * TtlDay, platform + ": later product boundary extended TTL");
        CheckTtl(Snapshot(10 * TtlDay, TtlNow + TtlDay / 2, TtlNow + 5 * TtlDay, vip: false), platform, false,
            null, TtlNow + TtlDay / 2, platform + ": negative cache crossed product start");
        CheckTtl(Snapshot(null, TtlNow + TtlDay / 2, TtlNow + 5 * TtlDay), platform, true,
            null, TtlNow + TtlDay / 2, platform + ": missing-expiry cache crossed product start");
        CheckTtl(Snapshot(10 * TtlDay, TtlNow + TtlDay / 4, TtlNow + TtlDay / 2, flag: 0), platform, true,
            TtlNow + 10 * TtlDay, TtlNow + (platform == "kg" ? TtlDay / 4 : 9 * TtlDay),
            platform + ": existing product flag boundary semantics changed");
        CheckTtl(Snapshot(10 * TtlDay, null, TtlNow), platform, true, TtlNow + 10 * TtlDay,
            TtlNow + 9 * TtlDay, platform + ": elapsed product boundary made fresh rights stale");
        return Task.CompletedTask;
    }
    private static async Task IntegerTtl()
    {
        foreach (string platform in new[] { "wy", "tx", "kg" })
            CheckTtl(TtlSnapshot(platform, AccountRules.MaxInteger), platform, true, AccountRules.MaxInteger,
                AccountRules.MaxInteger - TtlDay, platform + ": legal integer maximum");
        foreach (string value in new[] { "\"1\"", "true", "1.5", "-1", "2", "9007199254740992" })
        {
            foreach (string platform in new[] { "tx", "kg" })
            {
                var data = platform == "tx"
                    ? new JsonObject { ["star"] = 0, ["identity"] = new JsonObject { ["vip"] = JsonNode.Parse(value) } }
                    : new JsonObject { ["userid"] = UserId, ["is_vip"] = 0, ["busi_vip"] = new JsonArray(
                        new JsonObject { ["busi_type"] = "concept", ["product_type"] = "svip", ["is_vip"] = JsonNode.Parse(value) }) };
                CheckTtl(TtlMap(platform, data), platform, null, null, 0, platform + ": invalid flag " + value);
            }
        }
        foreach (string value in new[] { "\"1800000000000\"", "true", "1800000000000.5", "-1", "9007199254740992" })
        {
            var data = new JsonObject { ["userId"] = UserId, ["now"] = TtlNow,
                ["associator"] = new JsonObject { ["expireTime"] = JsonNode.Parse(value) } };
            CheckTtl(TtlMap("wy", data), "wy", null, null, 0, "WY invalid expiry integer " + value);
            data["associator"]!["expireTime"] = TtlNow + 3 * TtlDay;
            data["now"] = JsonNode.Parse(value);
            CheckTtl(TtlMap("wy", data), "wy", true, TtlNow + 3 * TtlDay, TtlNow + 2 * TtlDay,
                "WY invalid upstream clock fallback " + value);
        }
        var overflow = new JsonObject { ["userId"] = UserId, ["now"] = TtlNow - 10000,
            ["associator"] = new JsonObject { ["expireTime"] = AccountRules.MaxInteger } };
        await Error(() => Task.FromResult(TtlMap("wy", overflow)), "INVALID_RESPONSE");
    }
}
