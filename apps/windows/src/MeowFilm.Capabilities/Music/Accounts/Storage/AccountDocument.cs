// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

// Current account value. Generation is the per-account logical revision.
internal sealed class StoredAccount
{
    internal long Generation { get; init; }
    internal string? Cookie { get; init; }
    internal MusicAccountProfile? Profile { get; init; }
    internal string? Scope { get; init; }
    internal long? ValidatedAt { get; init; }
    internal string? ValueFingerprint { get; init; }
    public override string ToString() => "StoredAccount (redacted)";
}

internal sealed class AccountDocument
{
    private static readonly JsonSerializerOptions Encoding = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly Dictionary<string, JsonObject> entries;
    internal StorageRevision Revision { get; }
    private AccountDocument(Dictionary<string, JsonObject> entries, StorageRevision revision)
    { this.entries = entries; Revision = revision; }
    internal static AccountDocument Decode(AccountSnapshot snapshot)
    {
        AccountRules.Require(snapshot.Revision.Revision >= 0, "CORRUPT_STORAGE");
        return new(new Dictionary<string, JsonObject>(snapshot.Entries, StringComparer.Ordinal), snapshot.Revision);
    }
    internal StoredAccount Get(string platform)
    {
        try
        {
            if (!entries.TryGetValue(platform, out var row)) return new();
            return ReadAccount(row, platform);
        }
        catch (MusicAccountException) { throw; }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
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
    private static string ReadString(JsonNode? node)
    {
        AccountRules.Require(node is JsonValue val && val.TryGetValue<string>(out var text) && text is not null, "CORRUPT_STORAGE");
        return ((JsonValue)node!).GetValue<string>();
    }
    private static StoredAccount ReadAccount(JsonObject raw, string platform)
    {
        AccountRules.Require(raw.ContainsKey("version") && raw.ContainsKey("generation") && raw.ContainsKey("cookie") &&
            raw.ContainsKey("profile") && raw.ContainsKey("scope") && raw.ContainsKey("validatedAt"), "CORRUPT_STORAGE");
        AccountRules.Require(ReadInteger(raw["version"]) == 1, "CORRUPT_STORAGE");
        long generation = ReadInteger(raw["generation"]);
        AccountRules.Require(generation > 0, "CORRUPT_STORAGE");
        string? cookie = raw["cookie"] is null ? null : ReadString(raw["cookie"]);
        MusicAccountProfile? profile = null;
        string? scope = null;
        long? validatedAt = null;
        if (cookie is null)
            AccountRules.Require(raw["profile"] is null && raw["scope"] is null && raw["validatedAt"] is null, "CORRUPT_STORAGE");
        else
        {
            AccountRules.Require(cookie.Length > 0, "CORRUPT_STORAGE");
            var p = raw["profile"] as JsonObject ?? throw new MusicAccountException("CORRUPT_STORAGE");
            AccountRules.Require(p.ContainsKey("platform") && p.ContainsKey("userId") && p.ContainsKey("displayName"), "CORRUPT_STORAGE");
            string? avatar = null;
            if (p.TryGetPropertyValue("avatar", out var presentAvatar))
                avatar = ReadString(presentAvatar);
            profile = new(ReadString(p["platform"]), ReadString(p["userId"]), ReadString(p["displayName"]), avatar);
            AccountRules.Require(profile.Platform == platform && profile.UserId.Length > 0, "CORRUPT_STORAGE");
            scope = AccountRules.Text(ReadString(raw["scope"]), 128, "CORRUPT_STORAGE");
            AccountRules.Require(scope.Trim() == scope, "CORRUPT_STORAGE");
            validatedAt = ReadInteger(raw["validatedAt"]);
        }
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(raw, Encoding);
        string fingerprint;
        try { fingerprint = Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
        return new() { Generation = generation, Cookie = cookie, Profile = profile, Scope = scope,
            ValidatedAt = validatedAt, ValueFingerprint = fingerprint };
    }
    internal JsonObject Replace(string platform, StoredAccount expected, StoredAccount next)
    {
        var current = Get(platform);
        AccountRules.Require(current.Generation == expected.Generation &&
            current.ValueFingerprint == expected.ValueFingerprint, "STORAGE_CONFLICT");
        AccountRules.Require(next.Generation == current.Generation + 1 && next.Generation <= AccountRules.MaxInteger, "LIMIT");
        var value = new JsonObject
        {
            ["version"] = 1,
            ["generation"] = next.Generation,
            ["cookie"] = next.Cookie,
            ["profile"] = ProfileNode(next.Profile),
            ["scope"] = next.Scope,
            ["validatedAt"] = next.ValidatedAt
        };
        entries[platform] = value;
        return value;
    }
    internal static JsonObject? ProfileNode(MusicAccountProfile? profile)
    {
        if (profile is null) return null;
        var node = new JsonObject { ["platform"] = profile.Platform, ["userId"] = profile.UserId, ["displayName"] = profile.DisplayName };
        if (profile.Avatar is not null) node["avatar"] = profile.Avatar;
        return node;
    }
    public override string ToString() => "AccountDocument (redacted)";
}
