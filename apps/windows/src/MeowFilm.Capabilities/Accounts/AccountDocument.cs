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

internal sealed partial class AccountDocument
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private const int CurrentEntryCount = 8;
    private static readonly JsonSerializerOptions Encoding = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly JsonObject root;
    internal DocumentFingerprint Fingerprint { get; }
    private AccountDocument(JsonObject root, DocumentFingerprint fingerprint)
    { this.root = root; Fingerprint = fingerprint; }
    internal static string Key(string platform) => $"[\"meowfilm\",\"music\",1,\"platform-account\",\"{platform}\"]";
    internal static AccountDocument Decode(ProtectedDocument document)
    {
        try
        {
            if (document.Data is null)
            {
                AccountRules.Require(document.Fingerprint == DocumentFingerprint.Missing, "CORRUPT_STORAGE");
                return new(new JsonObject { ["version"] = 1, ["entries"] = new JsonArray() }, document.Fingerprint);
            }
            AccountRules.Require(document.Data.Length <= MaximumBytes &&
                DocumentFingerprint.FromBytes(document.Data) == document.Fingerprint, "CORRUPT_STORAGE");
            using var json = JsonDocument.Parse(document.Data, new JsonDocumentOptions { MaxDepth = 64 });
            // Reject ambiguous duplicate properties, rather than normalizing a corrupt document on save.
            CheckJson(json.RootElement);
            AccountRules.Fields(json.RootElement, ["version", "entries"], ["version", "entries"], "CORRUPT_STORAGE");
            AccountRules.Require(AccountRules.Integer(json.RootElement.GetProperty("version")) == 1, "CORRUPT_STORAGE");
            var entries = json.RootElement.GetProperty("entries");
            AccountRules.Require(entries.ValueKind == JsonValueKind.Array &&
                entries.GetArrayLength() <= CurrentEntryCount, "CORRUPT_STORAGE");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                AccountRules.Fields(entry, ["key", "value"], ["key", "value"], "CORRUPT_STORAGE");
                var key = entry.GetProperty("key");
                AccountRules.Require(key.ValueKind == JsonValueKind.String, "CORRUPT_STORAGE");
                string name = key.GetString()!;
                AccountRules.Require(name.Length is > 0 and <= 8192 && keys.Add(name), "CORRUPT_STORAGE");
                ValidateEntry(name, entry.GetProperty("value"));
            }
            return new AccountDocument(JsonNode.Parse(document.Data)!.AsObject(), document.Fingerprint);
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
    }
    private static void ValidateEntry(string key, JsonElement value)
    {
        foreach (string platform in AccountRules.Platforms)
        {
            if (key == Key(platform))
            {
                _ = ReadAccount(value, platform);
                return;
            }
            if (key == DetailsKey(platform))
            {
                _ = ReadDetails(value, platform);
                return;
            }
        }
        if (key == KgDeviceKey)
        {
            _ = ReadKgDevice(value);
            return;
        }
        if (key == QqFeedbackDeviceKey)
        {
            _ = ReadQqFeedbackDevice(value);
            return;
        }
        throw new MusicAccountException("CORRUPT_STORAGE");
    }
    private static void CheckJson(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            { AccountRules.Require(seen.Add(p.Name), "CORRUPT_STORAGE"); CheckJson(p.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var value in node.EnumerateArray()) CheckJson(value);
    }
    internal StoredAccount Get(string platform)
    {
        try
        {
            var row = root["entries"]!.AsArray().FirstOrDefault(n => n!["key"]!.GetValue<string>() == Key(platform));
            if (row is null) return new();
            return ReadAccount(JsonSerializer.SerializeToElement(row["value"], Encoding), platform);
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
    private static StoredAccount ReadAccount(JsonElement raw, string platform)
    {
        AccountRules.Fields(raw, ["version", "generation", "cookie", "profile", "scope", "validatedAt"],
            ["version", "generation", "cookie", "profile", "scope", "validatedAt"], "CORRUPT_STORAGE");
        AccountRules.Require(AccountRules.Integer(raw.GetProperty("version")) == 1, "CORRUPT_STORAGE");
        long generation = AccountRules.Integer(raw.GetProperty("generation"));
        AccountRules.Require(generation > 0, "CORRUPT_STORAGE");
        string? cookie = raw.GetProperty("cookie").GetString();
        MusicAccountProfile? profile = null;
        string? scope = null;
        long? validatedAt = null;
        if (cookie is null)
            AccountRules.Require(raw.GetProperty("profile").ValueKind == JsonValueKind.Null &&
                raw.GetProperty("scope").ValueKind == JsonValueKind.Null && raw.GetProperty("validatedAt").ValueKind == JsonValueKind.Null,
                "CORRUPT_STORAGE");
        else
        {
            AccountRules.Text(cookie, 16384, "CORRUPT_STORAGE");
            var p = raw.GetProperty("profile");
            AccountRules.Fields(p, ["platform", "userId", "displayName", "avatar"], ["platform", "userId", "displayName"], "CORRUPT_STORAGE");
            if (p.TryGetProperty("avatar", out var presentAvatar))
                AccountRules.Require(presentAvatar.ValueKind == JsonValueKind.String, "CORRUPT_STORAGE");
            profile = AccountRules.Profile(new(
                AccountRules.String(p, "platform", 8),
                AccountRules.String(p, "userId", 256),
                AccountRules.String(p, "displayName", 200, empty: true),
                p.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null), platform);
            scope = AccountRules.String(raw, "scope", 128);
            AccountRules.Require(scope.Trim() == scope, "CORRUPT_STORAGE");
            validatedAt = AccountRules.Integer(raw.GetProperty("validatedAt"));
        }
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(raw, Encoding);
        string fingerprint;
        try { fingerprint = Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
        return new() { Generation = generation, Cookie = cookie, Profile = profile, Scope = scope,
            ValidatedAt = validatedAt, ValueFingerprint = fingerprint };
    }
    internal byte[] Replace(string platform, StoredAccount expected, StoredAccount next)
    {
        var current = Get(platform);
        AccountRules.Require(current.Generation == expected.Generation &&
            current.ValueFingerprint == expected.ValueFingerprint, "STORAGE_CONFLICT");
        AccountRules.Require(next.Generation == current.Generation + 1 && next.Generation <= AccountRules.MaxInteger, "LIMIT");
        var entries = root["entries"]!.AsArray();
        var existing = entries.FirstOrDefault(n => n!["key"]!.GetValue<string>() == Key(platform));
        var value = new JsonObject { ["version"] = 1, ["generation"] = next.Generation, ["cookie"] = next.Cookie,
            ["profile"] = ProfileNode(next.Profile), ["scope"] = next.Scope, ["validatedAt"] = next.ValidatedAt };
        if (existing is null)
        {
            AccountRules.Require(entries.Count < CurrentEntryCount, "LIMIT");
            entries.Add(new JsonObject { ["key"] = Key(platform), ["value"] = value });
        }
        else existing["value"] = value;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, Encoding);
        if (bytes.Length <= MaximumBytes) return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw new MusicAccountException("LIMIT");
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
