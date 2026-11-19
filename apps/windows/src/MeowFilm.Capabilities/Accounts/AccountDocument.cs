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
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Encoding = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly JsonObject root;
    internal StorageRevision Revision { get; }
    private AccountDocument(JsonObject root, StorageRevision revision)
    { this.root = root; Revision = revision; }
    internal static string Key(string platform) => $"[\"meowfilm\",\"music\",1,\"platform-account\",\"{platform}\"]";
    internal static AccountDocument Decode(RecordSnapshot document)
    {
        try
        {
            if (document.Data is null)
            {
                AccountRules.Require(document.Revision == StorageRevision.Empty, "CORRUPT_STORAGE");
                return new(new JsonObject { ["version"] = 1, ["entries"] = new JsonArray() }, document.Revision);
            }
            AccountRules.Require(document.Data.Length <= MaximumBytes &&
                document.Revision.Revision >= 0, "CORRUPT_STORAGE");
            using var json = JsonDocument.Parse(document.Data, new JsonDocumentOptions { MaxDepth = 64 });
            // Reject ambiguous duplicate properties, rather than normalizing a corrupt document on save.
            CheckJson(json.RootElement);
            AccountRules.Required(json.RootElement, ["version", "entries"], "CORRUPT_STORAGE");
            AccountRules.Require(AccountRules.Integer(json.RootElement.GetProperty("version")) == 1, "CORRUPT_STORAGE");
            var entries = json.RootElement.GetProperty("entries");
            AccountRules.Require(entries.ValueKind == JsonValueKind.Array, "CORRUPT_STORAGE");
            return new AccountDocument(JsonNode.Parse(document.Data)!.AsObject(), document.Revision);
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
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
            var row = Entry(platform);
            if (row is null) return new();
            return ReadAccount(JsonSerializer.SerializeToElement(row["value"], Encoding), platform);
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
    private JsonObject? Entry(string platform)
    {
        var rows = root["entries"]!.AsArray().OfType<JsonObject>()
            .Where(n => n["key"] is JsonValue key && key.TryGetValue<string>(out var value) && value == Key(platform)).ToArray();
        AccountRules.Require(rows.Length <= 1, "CORRUPT_STORAGE");
        return rows.SingleOrDefault();
    }
    private static StoredAccount ReadAccount(JsonElement raw, string platform)
    {
        AccountRules.Required(raw, ["version", "generation", "cookie", "profile", "scope", "validatedAt"], "CORRUPT_STORAGE");
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
            AccountRules.Require(cookie.Length > 0, "CORRUPT_STORAGE");
            var p = raw.GetProperty("profile");
            AccountRules.Required(p, ["platform", "userId", "displayName"], "CORRUPT_STORAGE");
            if (p.TryGetProperty("avatar", out var presentAvatar))
                AccountRules.Require(presentAvatar.ValueKind == JsonValueKind.String, "CORRUPT_STORAGE");
            profile = new(p.GetProperty("platform").GetString()!, p.GetProperty("userId").GetString()!,
                p.GetProperty("displayName").GetString()!, p.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null);
            AccountRules.Require(profile.Platform == platform && profile.UserId.Length > 0, "CORRUPT_STORAGE");
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
        var existing = Entry(platform);
        var value = existing?["value"]?.DeepClone().AsObject() ?? new JsonObject();
        value["version"] = 1; value["generation"] = next.Generation; value["cookie"] = next.Cookie;
        var profile = ProfileNode(next.Profile);
        if (profile is not null && value["profile"] is JsonObject previous)
            foreach (var field in previous)
                if (field.Key is not ("platform" or "userId" or "displayName" or "avatar"))
                    profile[field.Key] = field.Value?.DeepClone();
        value["profile"] = profile; value["scope"] = next.Scope; value["validatedAt"] = next.ValidatedAt;
        if (existing is null)
        {
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
