// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
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
    internal static StoredAccountDetails ReadDetails(JsonElement raw, string platform)
    {
        long generation = AccountRules.Integer(raw.GetProperty("accountGeneration"));
        var scope = AccountRules.String(raw, "accountScope", 128);
        var binding = AccountRules.String(raw, "accountFingerprint", 64);
        var user = AccountRules.Id(AccountRules.String(raw, "userId", 20));
        var p = raw.GetProperty("profile");
        var profile = AccountRules.Profile(new(AccountRules.String(p, "platform", 8), AccountRules.String(p, "userId", 20),
            AccountRules.String(p, "displayName", 200, empty: true),
            p.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null), platform);
        AccountRules.Require(profile.UserId == user, "CORRUPT_STORAGE");
        long fetched = AccountRules.Integer(raw.GetProperty("profileFetchedAt")), expires = AccountRules.Integer(raw.GetProperty("profileExpiresAt"));
        AccountRules.Require(fetched > 0 && expires == fetched + AccountDetailsSnapshot.ProfileTtl, "CORRUPT_STORAGE");
        var r = raw.GetProperty("rights");
        bool? vip = r.GetProperty("vip").ValueKind == JsonValueKind.Null ? null : r.GetProperty("vip").GetBoolean();
        var absolute = r.GetProperty("expiresAt");
        var levels = r.GetProperty("audioLevels");
        var rights = AccountDetailsSnapshot.Validate(new(new(vip, AccountRules.String(r, "tier", 32),
            absolute.ValueKind == JsonValueKind.Null ? null : AccountRules.Integer(absolute)),
            AccountRules.Integer(r.GetProperty("checkedAt")), AccountRules.Integer(r.GetProperty("recheckAt")),
            levels.EnumerateArray().Select(x => x.GetString()!).ToArray(), r.GetProperty("verificationDeferred").GetBoolean(),
            r.GetProperty("errorCode").GetString(), AccountRules.Integer(r.GetProperty("retryAt"))), platform);
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
