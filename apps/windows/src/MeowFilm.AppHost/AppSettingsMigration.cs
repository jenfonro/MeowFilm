using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

/// <summary>One-way adoption of the former Windows runtime settings, before either owner starts.</summary>
internal static class AppSettingsMigration
{
    internal static async Task ApplyAsync(IDocumentStore store, string root,
        IReadOnlyDictionary<string, string> namespaces, CancellationToken token)
    {
        foreach (var (block, documentNamespace) in namespaces)
        {
            string? relative = block switch
            {
                "cat.sources" => "settings.json", "pan.accounts" => "pan/accounts.json",
                "tmdb" => "tmdb/settings.json", "douban" => "douban/settings.json",
                "film.rules" => "film-rules/rules.json", "film.preferences" => "film-playback/library.json",
                _ => null
            };
            if (relative is null) continue;
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            byte[] bytes = await ReadLegacyAsync(path, token).ConfigureAwait(false);
            try
            {
                var original = JsonNode.Parse(bytes) as JsonObject ??
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                if (block == "cat.sources" && original["version"]?.GetValue<int>() == 2)
                {
                    Require(await new SettingsDocument(store, documentNamespace).ReadAsync(token).ConfigureAwait(false) is not null);
                    continue;
                }
                // An already split library remains owned by the runtime.
                if (block == "film.preferences" && !original.ContainsKey("preferences")) continue;
                ValidateLegacy(block, original);
                JsonObject adopted = block switch
                {
                    "cat.sources" => CatConfiguration(original),
                    "film.preferences" => new() { ["version"] = 1, ["preferences"] = original["preferences"]?.DeepClone() },
                    "film.rules" => new() { ["version"] = 1, ["rules"] = original["rules"]!.DeepClone() },
                    _ => (JsonObject)original.DeepClone()
                };
                var document = new SettingsDocument(store, documentNamespace);
                var actual = await document.UpdateAsync(current => current ??
                    JsonSerializer.SerializeToElement(adopted), token).ConfigureAwait(false);
                // Facts must be upgraded even if a later native edit followed an interrupted adoption.
                if (block != "cat.sources" && !JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), adopted)) continue;
                // Only retire bytes proved to be the migration input. Unknown edits are preserved.
                byte[] latest = await ReadLegacyAsync(path, token).ConfigureAwait(false);
                try { if (!bytes.AsSpan().SequenceEqual(latest)) continue; }
                finally { CryptographicOperations.ZeroMemory(latest); }
                if (block == "cat.sources")
                {
                    var facts = new JsonObject { ["version"] = 2, ["scripts"] = new JsonArray() };
                    foreach (var node in original["scripts"]?.AsArray() ?? [])
                    {
                        if (node is not JsonObject script) throw new DocumentStoreException(DocumentStoreError.Corrupt);
                        facts["scripts"]!.AsArray().Add(CatFacts(script));
                    }
                    await ReplaceAsync(path, facts, token).ConfigureAwait(false);
                }
                else if (block == "film.preferences")
                {
                    original.Remove("preferences");
                    await ReplaceAsync(path, original, token).ConfigureAwait(false);
                }
                else File.Delete(path);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
            { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private static async Task<byte[]> ReadLegacyAsync(string path, CancellationToken token)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new DocumentStoreException(DocumentStoreError.UnsafePath);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (file.Length > 8 * 1024 * 1024) throw new DocumentStoreException(DocumentStoreError.Limit);
        byte[] bytes = new byte[(int)file.Length];
        try { await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static void Require(bool valid)
    {
        if (!valid) throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }

    private static bool Text(JsonNode? value) => value is JsonValue item && item.TryGetValue<string>(out _);
    private static bool Flag(JsonNode? value) => value is JsonValue item && item.TryGetValue<bool>(out _);
    private static bool Integer(JsonNode? value, long minimum, long maximum) =>
        value is JsonValue item && item.TryGetValue<long>(out long number) && number >= minimum && number <= maximum;
    private static bool Texts(JsonNode? value) => value is JsonArray rows && rows.All(Text);

    private static void ValidateLegacy(string block, JsonObject value)
    {
        // The former rules writer used {rules}, without a document version.
        Require(block == "film.rules" ? value.Count == 1 && value["rules"] is JsonObject :
            Integer(value["version"], 1, 1));
        switch (block)
        {
            case "cat.sources":
                Require(value.Count == 5 && Integer(value["port"], 0, 65535) && value["providers"] is JsonObject &&
                    value["mappings"] is JsonArray && value["scripts"] is JsonArray);
                foreach (var node in value["scripts"]!.AsArray())
                {
                    Require(node is JsonObject);
                    var script = node!.AsObject();
                    string[] fields = ["installationId", "sourceHash", "enabled", "version", "config", "name",
                        "sourceType", "sourceUrl", "importedAt", "updatedAt", "updateState", "updateError", "sites"];
                    Require(script.All(item => fields.Contains(item.Key)) &&
                        Text(script["installationId"]) && Regex.IsMatch(script["installationId"]!.GetValue<string>(),
                        @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") && Text(script["sourceHash"]) &&
                        Regex.IsMatch(script["sourceHash"]!.GetValue<string>(), @"\A[a-f0-9]{64}\z") &&
                        Flag(script["enabled"]) && Text(script["version"]) && script["config"] is JsonObject);
                    if (script.ContainsKey("sourceType"))
                    {
                        Require(Text(script["name"]) && !string.IsNullOrWhiteSpace(script["name"]!.GetValue<string>()) &&
                            Text(script["sourceType"]));
                        string type = script["sourceType"]!.GetValue<string>();
                        Require(type == "local" && script["sourceUrl"] is null ||
                            type == "network" && Text(script["sourceUrl"]) &&
                            Uri.TryCreate(script["sourceUrl"]!.GetValue<string>(), UriKind.Absolute, out var url) &&
                            url.Scheme is "http" or "https");
                    }
                }
                Require(value["scripts"]!.AsArray().Select(script => script!["installationId"]!.GetValue<string>())
                    .Distinct(StringComparer.Ordinal).Count() == value["scripts"]!.AsArray().Count);
                break;
            case "pan.accounts":
                Require(value.Count == 2 && value["accounts"] is JsonObject);
                foreach (var (provider, node) in value["accounts"]!.AsObject())
                {
                    string[] fields = provider switch
                    {
                        "baidu" or "quark" or "uc" => ["cookie"],
                        "tianyi" => ["cookie", "username", "password"],
                        "yidong" => ["authorization", "refreshedAt"],
                        "quark_tv" or "uc_tv" => ["refreshToken", "deviceId", "accessToken", "expiresAt"],
                        _ => []
                    };
                    Require(fields.Length > 0 && node is JsonObject account && account.Count == 2 &&
                        Integer(account["revision"], 1, 9007199254740991) && account["credentials"] is JsonObject);
                    foreach (var (field, credential) in node!["credentials"]!.AsObject())
                        Require(fields.Contains(field) && (field is "refreshedAt" or "expiresAt"
                            ? Integer(credential, 0, 9007199254740991) : Text(credential)));
                }
                break;
            case "tmdb":
            case "douban":
                Require(value.Count == 2 && value["settings"] is JsonObject);
                var settings = value["settings"]!.AsObject();
                string[] names = block == "tmdb" ?
                    ["apiBase", "imageBase", "apiKey", "accessToken", "language", "region", "includeAdult"] :
                    ["apiBase", "searchBase", "imageBase", "apiProxy", "imageProxy", "cookie"];
                Require(settings.Count == names.Length && names.All(name =>
                    name == "includeAdult" ? Flag(settings[name]) : Text(settings[name])));
                break;
            case "film.rules":
                var rules = value["rules"]!.AsObject();
                Require(rules.Count == 4 && Texts(rules["episodeCleanRegexRules"]) && Texts(rules["aggregateRegexRules"]));
                foreach (string group in new[] { "episodeRules", "movieRules" })
                {
                    Require(rules[group] is JsonArray);
                    foreach (var row in rules[group]!.AsArray())
                        Require(row is JsonObject match && Text(match["pattern"]) &&
                            (!match.ContainsKey("replace") || Text(match["replace"])) &&
                            (!match.ContainsKey("flags") || Text(match["flags"])));
                }
                break;
            case "film.preferences":
                Require(value.Count == 6 && value["preferences"] is JsonObject &&
                    value["history"] is JsonArray && value["manual"] is JsonArray &&
                    value["blocks"] is JsonArray && value["previewTargets"] is JsonArray);
                var preferences = value["preferences"]!.AsObject();
                Require(preferences.Count == 5 && Flag(preferences["iosAutoPrepare"]));
                foreach (string field in new[] { "ruleOrder", "panOrder", "keywords", "siteCleanKeywords" })
                    Require(Texts(preferences[field]));
                break;
        }
    }

    private static JsonObject CatConfiguration(JsonObject original)
    {
        if (original["version"]?.GetValue<int>() != 1) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        var scripts = new JsonArray();
        foreach (var node in original["scripts"]?.AsArray() ?? [])
        {
            if (node is not JsonObject script) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            string id = script["installationId"]!.GetValue<string>();
            string type = script["sourceType"]?.GetValue<string>() ?? "local";
            scripts.Add(new JsonObject
            {
                ["installationId"] = id, ["name"] = script["name"]?.DeepClone() ?? JsonValue.Create(id),
                ["sourceType"] = type, ["sourceUrl"] = script["sourceUrl"]?.DeepClone(),
                ["localPath"] = null, ["localHash"] = type == "local" ? script["sourceHash"]?.DeepClone() : null,
                ["enabled"] = script["enabled"]?.DeepClone() ?? JsonValue.Create(true), ["inputRevision"] = 1
            });
        }
        return new() { ["version"] = 1, ["port"] = original["port"]?.DeepClone() ?? JsonValue.Create(0),
            ["providers"] = original["providers"]?.DeepClone() ?? new JsonObject(),
            ["mappings"] = original["mappings"]?.DeepClone() ?? new JsonArray(), ["scripts"] = scripts };
    }

    private static JsonObject CatFacts(JsonObject script)
    {
        var result = new JsonObject();
        foreach (string key in new[] { "installationId", "sourceHash", "version", "config", "importedAt",
            "updatedAt", "updateState", "updateError", "sites" }) result[key] = script[key]?.DeepClone();
        result["version"] ??= JsonValue.Create("0.0.0");
        result["config"] ??= new JsonObject();
        result["updateState"] ??= JsonValue.Create("idle");
        result["sites"] ??= new JsonArray();
        // Network input has not yet been verified against the adopted address.
        result["loadedIdentity"] = script["sourceType"]?.GetValue<string>() == "network" ? null :
            JsonValue.Create("local:1:" + script["sourceHash"]?.GetValue<string>());
        return result;
    }

    private static async Task ReplaceAsync(string path, JsonObject value, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
