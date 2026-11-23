// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Accounts;

internal sealed record StoredAccountDetails(
    long AccountGeneration, string AccountScope, string AccountFingerprint,
    string UserId, MusicAccountProfile Profile, long ProfileFetchedAt, long ProfileExpiresAt,
    MusicAccountEntitlements Rights)
{
    internal bool BelongsTo(StoredAccount account) => AccountGeneration == account.Generation &&
        AccountScope == account.Scope && UserId == account.Profile?.UserId && AccountFingerprint == account.ValueFingerprint;
    public override string ToString() => "StoredAccountDetails (private)";
}

internal static class AccountDetailsDocument
{
    private static long ReadInteger(JsonNode? node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var number))
            {
                AccountRules.Require(number is >= 0 and <= AccountRules.MaxInteger, "CORRUPT_STORAGE");
                return number;
            }
            if (val.TryGetValue<decimal>(out var dec) && dec is >= 0 and <= AccountRules.MaxInteger && decimal.Truncate(dec) == dec)
                return (long)dec;
        }
        throw new MusicAccountException("CORRUPT_STORAGE");
    }
    private static string ReadText(JsonNode? node)
    {
        AccountRules.Require(node is JsonValue val && val.TryGetValue<string>(out var text) && text is not null, "CORRUPT_STORAGE");
        return ((JsonValue)node!).GetValue<string>();
    }
    private static bool ReadBool(JsonNode? node)
    {
        AccountRules.Require(node is JsonValue val && val.TryGetValue<bool>(out _), "CORRUPT_STORAGE");
        return ((JsonValue)node!).GetValue<bool>();
    }
    internal static StoredAccountDetails ReadDetails(JsonObject raw, string platform)
    {
        long generation = ReadInteger(raw["accountGeneration"]);
        var scope = AccountRules.Text(ReadText(raw["accountScope"]), 128, "CORRUPT_STORAGE");
        var binding = AccountRules.Text(ReadText(raw["accountFingerprint"]), 64, "CORRUPT_STORAGE");
        var user = ReadText(raw["userId"]);
        var p = raw["profile"] as JsonObject ?? throw new MusicAccountException("CORRUPT_STORAGE");
        var profile = new MusicAccountProfile(ReadText(p["platform"]), ReadText(p["userId"]),
            ReadText(p["displayName"]), p["avatar"] is null ? null : ReadText(p["avatar"]));
        AccountRules.Require(profile.Platform == platform, "CORRUPT_STORAGE");
        long fetched = ReadInteger(raw["profileFetchedAt"]), expires = ReadInteger(raw["profileExpiresAt"]);
        AccountRules.Require(fetched > 0 && expires == fetched + AccountDetailsSnapshot.ProfileTtl, "CORRUPT_STORAGE");
        var r = raw["rights"] as JsonObject ?? throw new MusicAccountException("CORRUPT_STORAGE");
        bool? vip = r["vip"] is null ? null : ReadBool(r["vip"]);
        long? expiresAt = r["expiresAt"] is null ? null : ReadInteger(r["expiresAt"]);
        var levels = r["audioLevels"] as JsonArray ?? throw new MusicAccountException("CORRUPT_STORAGE");
        var rights = AccountDetailsSnapshot.Freeze(new(new(vip, ReadText(r["tier"]), expiresAt),
            ReadInteger(r["checkedAt"]), ReadInteger(r["recheckAt"]),
            levels.Select(ReadText).ToArray(), ReadBool(r["verificationDeferred"]),
            r["errorCode"] is null ? null : ReadText(r["errorCode"]), ReadInteger(r["retryAt"])));
        return new(generation, scope, binding, user, profile, fetched, expires, rights);
    }
    internal static JsonObject Encode(StoredAccountDetails next)
    {
        var rights = next.Rights;
        var value = new JsonObject {
            ["accountGeneration"] = next.AccountGeneration,
            ["accountScope"] = next.AccountScope, ["accountFingerprint"] = next.AccountFingerprint, ["userId"] = next.UserId,
            ["profile"] = AccountDocument.ProfileNode(next.Profile), ["profileFetchedAt"] = next.ProfileFetchedAt, ["profileExpiresAt"] = next.ProfileExpiresAt,
            ["rights"] = new JsonObject {
                ["vip"] = rights.Membership.Vip, ["tier"] = rights.Membership.Tier, ["expiresAt"] = rights.Membership.ExpiresAt,
                ["checkedAt"] = rights.CheckedAt, ["recheckAt"] = rights.RecheckAt,
                ["audioLevels"] = new JsonArray(rights.AudioLevels.Select(x => JsonValue.Create(x)).ToArray()),
                ["verificationDeferred"] = rights.VerificationDeferred, ["errorCode"] = rights.ErrorCode, ["retryAt"] = rights.RetryAt
            }
        };
        return value;
    }
}
