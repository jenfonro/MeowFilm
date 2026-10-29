// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Accounts;

internal sealed record StoredAccountDetails(
    long Revision, long AccountGeneration, string AccountScope, string AccountFingerprint,
    string UserId, MusicAccountProfile Profile, long ProfileFetchedAt, long ProfileExpiresAt,
    MusicAccountEntitlements Rights, string? Fingerprint = null)
{
    internal bool BelongsTo(StoredAccount account) => AccountGeneration == account.Generation &&
        AccountScope == account.Scope && UserId == account.Profile?.UserId && AccountFingerprint == account.ValueFingerprint;
    public override string ToString() => "StoredAccountDetails (private)";
}

internal sealed partial class AccountDocument
{
    private static string DetailsKey(string platform) => $"[\"meowfilm\",\"music\",1,\"account-details\",\"{platform}\"]";
    internal StoredAccountDetails? GetDetails(string platform)
    {
        try
        {
            var entry = root["entries"]!.AsArray().FirstOrDefault(n => n!["key"]!.GetValue<string>() == DetailsKey(platform));
            if (entry is null) return null;
            return ReadDetails(JsonSerializer.SerializeToElement(entry["value"], Encoding), platform);
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
    private static StoredAccountDetails ReadDetails(JsonElement raw, string platform)
    {
        string[] fields = ["version", "revision", "accountGeneration", "accountScope", "accountFingerprint",
            "userId", "profile", "profileFetchedAt", "profileExpiresAt", "rights"];
        AccountRules.Fields(raw, fields, fields, "CORRUPT_STORAGE");
        AccountRules.Require(AccountRules.Integer(raw.GetProperty("version")) == 1, "CORRUPT_STORAGE");
        long revision = AccountRules.Integer(raw.GetProperty("revision")), generation = AccountRules.Integer(raw.GetProperty("accountGeneration"));
        var scope = AccountRules.String(raw, "accountScope", 128);
        var binding = AccountRules.String(raw, "accountFingerprint", 64);
        var user = AccountRules.Id(AccountRules.String(raw, "userId", 20));
        AccountRules.Require(revision > 0 && generation > 0 && scope.Trim() == scope &&
            AccountRules.Match(binding, "^[a-f0-9]{64}$"), "CORRUPT_STORAGE");
        var p = raw.GetProperty("profile");
        AccountRules.Fields(p, ["platform", "userId", "displayName", "avatar"], ["platform", "userId", "displayName"], "CORRUPT_STORAGE");
        if (p.TryGetProperty("avatar", out var presentAvatar))
            AccountRules.Require(presentAvatar.ValueKind == JsonValueKind.String, "CORRUPT_STORAGE");
        var profile = AccountRules.Profile(new(AccountRules.String(p, "platform", 8), AccountRules.String(p, "userId", 20),
            AccountRules.String(p, "displayName", 200, empty: true),
            p.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null), platform);
        AccountRules.Require(profile.UserId == user, "CORRUPT_STORAGE");
        long fetched = AccountRules.Integer(raw.GetProperty("profileFetchedAt")), expires = AccountRules.Integer(raw.GetProperty("profileExpiresAt"));
        AccountRules.Require(fetched > 0 && expires == fetched + AccountDetailsSnapshot.ProfileTtl, "CORRUPT_STORAGE");
        var r = raw.GetProperty("rights");
        string[] names = ["vip", "tier", "expiresAt", "checkedAt", "recheckAt", "audioLevels", "verificationDeferred", "errorCode", "retryAt"];
        AccountRules.Fields(r, names, names, "CORRUPT_STORAGE");
        bool? vip = r.GetProperty("vip").ValueKind == JsonValueKind.Null ? null : r.GetProperty("vip").GetBoolean();
        var absolute = r.GetProperty("expiresAt");
        var levels = r.GetProperty("audioLevels");
        AccountRules.Require(levels.ValueKind == JsonValueKind.Array && levels.GetArrayLength() <= 10, "CORRUPT_STORAGE");
        var rights = AccountDetailsSnapshot.Validate(new(new(vip, AccountRules.String(r, "tier", 32),
            absolute.ValueKind == JsonValueKind.Null ? null : AccountRules.Integer(absolute)),
            AccountRules.Integer(r.GetProperty("checkedAt")), AccountRules.Integer(r.GetProperty("recheckAt")),
            levels.EnumerateArray().Select(x => x.GetString()!).ToArray(), r.GetProperty("verificationDeferred").GetBoolean(),
            r.GetProperty("errorCode").GetString(), AccountRules.Integer(r.GetProperty("retryAt"))), platform);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw, Encoding);
        try { return new(revision, generation, scope, binding, user, profile, fetched, expires, rights,
            Convert.ToHexStringLower(SHA256.HashData(bytes))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal byte[] ReplaceDetails(string platform, StoredAccount expectedAccount,
        StoredAccountDetails? expected, StoredAccountDetails next)
    {
        var account = Get(platform);
        AccountRules.Require(account.ValueFingerprint == expectedAccount.ValueFingerprint &&
            account.Generation == expectedAccount.Generation && account.Cookie is not null &&
            next.BelongsTo(account), "STORAGE_CONFLICT");
        var current = GetDetails(platform);
        AccountRules.Require(current?.Fingerprint == expected?.Fingerprint && current?.Revision == expected?.Revision, "STORAGE_CONFLICT");
        AccountRules.Require(next.Revision == (current?.Revision ?? 0) + 1 && next.Revision <= AccountRules.MaxInteger, "LIMIT");
        var rights = next.Rights;
        var value = new JsonObject {
            ["version"] = 1, ["revision"] = next.Revision, ["accountGeneration"] = next.AccountGeneration,
            ["accountScope"] = next.AccountScope, ["accountFingerprint"] = next.AccountFingerprint, ["userId"] = next.UserId,
            ["profile"] = ProfileNode(next.Profile), ["profileFetchedAt"] = next.ProfileFetchedAt, ["profileExpiresAt"] = next.ProfileExpiresAt,
            ["rights"] = new JsonObject {
                ["vip"] = rights.Membership.Vip, ["tier"] = rights.Membership.Tier, ["expiresAt"] = rights.Membership.ExpiresAt,
                ["checkedAt"] = rights.CheckedAt, ["recheckAt"] = rights.RecheckAt,
                ["audioLevels"] = new JsonArray(rights.AudioLevels.Select(x => JsonValue.Create(x)).ToArray()),
                ["verificationDeferred"] = rights.VerificationDeferred, ["errorCode"] = rights.ErrorCode, ["retryAt"] = rights.RetryAt
            }
        };
        var entries = root["entries"]!.AsArray();
        var row = entries.FirstOrDefault(n => n!["key"]!.GetValue<string>() == DetailsKey(platform));
        if (row is null)
        {
            AccountRules.Require(entries.Count < CurrentEntryCount, "LIMIT");
            entries.Add(new JsonObject { ["key"] = DetailsKey(platform), ["value"] = value });
        }
        else row["value"] = value;
        _ = GetDetails(platform);
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(root, Encoding);
        if (result.Length <= MaximumBytes) return result;
        CryptographicOperations.ZeroMemory(result);
        throw new MusicAccountException("LIMIT");
    }
}
