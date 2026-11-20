using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record CatSourceConfiguration(string InstallationId, string Name, string SourceType,
    string? SourceUrl, string? LocalPath, string? LocalHash, bool Enabled, long InputRevision);

/// <summary>User source intent only. The runtime owns downloaded/installed source and execution facts.</summary>
internal sealed class CatSourceSettings(IStorageDatabase database)
{
    internal const string DocumentNamespace = "film.cat-sources.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SettingsDocument document = new(new(database, DocumentNamespace, FilmStorageRecords.Cat));
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new
        { version = 1, port = 0, providers = new { }, mappings = Array.Empty<object>(), scripts = Array.Empty<object>() });

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ReadAsync(CancellationToken token) =>
        Parse(await document.ReadAsync(token).ConfigureAwait(false) ?? Empty);

    internal async Task<IReadOnlyDictionary<string, bool>> ReadPanInterceptionAsync(CancellationToken token)
    {
        var root = await document.ReadAsync(token).ConfigureAwait(false) ?? Empty;
        _ = Parse(root);
        return PanInterception(root);
    }

    internal async Task<IReadOnlyDictionary<string, bool>> SavePanInterceptionAsync(string provider, bool enabled,
        CancellationToken token)
    {
        if (!PanSettingsStore.Providers.Any(value => value.GetProperty("provider").GetString() == provider))
            throw new FilmSourceException("INVALID_ARGUMENT");
        var saved = await document.UpdateAsync(current =>
        {
            var root = current ?? Empty;
            _ = Parse(root);
            var providers = FilmDocument.Set(root.GetProperty("providers"), provider, JsonSerializer.SerializeToElement(enabled));
            return FilmDocument.Set(root, "providers", providers);
        }, token).ConfigureAwait(false);
        return PanInterception(saved);
    }

    private static IReadOnlyDictionary<string, bool> PanInterception(JsonElement root)
    {
        var providers = root.GetProperty("providers");
        return PanSettingsStore.Providers.Select(value => value.GetProperty("provider").GetString()!)
            .ToDictionary(provider => provider, provider => !providers.TryGetProperty(provider, out var value) || value.GetBoolean(),
                StringComparer.Ordinal);
    }

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ChangeAsync(
        Func<IReadOnlyList<CatSourceConfiguration>, IEnumerable<CatSourceConfiguration>> edit, CancellationToken token)
    {
        var saved = await document.UpdateAsync(current =>
        {
            var root = current ?? Empty;
            var next = edit(Parse(root)).ToArray();
            if (next.Select(item => item.InstallationId).Distinct(StringComparer.Ordinal).Count() != next.Length)
                throw new FilmSourceException("INVALID_DOCUMENT");
            foreach (var script in next) Validate(script);
            return FilmDocument.Set(root, "scripts", JsonSerializer.SerializeToElement(next, Json));
        }, token).ConfigureAwait(false);
        return Parse(saved);
    }

    internal static async Task<(string Path, string Hash)> SelectLocalAsync(string path, CancellationToken token)
    {
        var input = await SelectedScriptSnapshot.ReadAsync(path, token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            return (System.IO.Path.GetFullPath(path), Convert.ToHexStringLower(SHA256.HashData(input.Bytes)));
        }
        finally { CryptographicOperations.ZeroMemory(input.Bytes); }
    }

    internal static void ValidateSource(string name, string type, string? url)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || type is not ("local" or "network") ||
            type == "network" && (url is null || url.Any(char.IsControl) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) ||
            type == "local" && url is not null)
            throw new FilmSourceException("INVALID_ARGUMENT");
    }

    private static CatSourceConfiguration[] Parse(JsonElement current)
    {
        try
        {
            static void Fields(JsonElement value, params string[] names)
            {
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(names.Order(StringComparer.Ordinal)))
                    throw new JsonException();
            }
            Fields(current, "version", "port", "providers", "mappings", "scripts");
            if (BridgeCodec.RequiredProperty(current, "version").GetInt32() != 1 ||
                BridgeCodec.RequiredProperty(current, "port").GetInt32() is < 0 or > 65535) throw new JsonException();
            foreach (var provider in current.GetProperty("providers").EnumerateObject())
                if (provider.Name.Length == 0 || provider.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException();
            foreach (var mapping in current.GetProperty("mappings").EnumerateArray())
            {
                Fields(mapping, "provider", "aliases");
                if (mapping.GetProperty("provider").GetString() is not { Length: > 0 } ||
                    mapping.GetProperty("aliases").EnumerateArray().Any(alias => alias.ValueKind != JsonValueKind.String))
                    throw new JsonException();
            }
            var rows = BridgeCodec.RequiredProperty(current, "scripts").EnumerateArray().ToArray();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var scripts = new List<CatSourceConfiguration>();
            foreach (var row in rows)
            {
                Fields(row, "installationId", "name", "sourceType", "sourceUrl", "localPath", "localHash", "enabled", "inputRevision");
                var id = row.GetProperty("installationId").GetString()!;
                if (!identities.Add(id)) throw new JsonException();
                string? Text(string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null
                    ? null : row.GetProperty(name).GetString();
                var script = new CatSourceConfiguration(id, Text("name")!, Text("sourceType")!,
                    Text("sourceUrl"), Text("localPath"), Text("localHash"),
                    row.GetProperty("enabled").GetBoolean(), row.GetProperty("inputRevision").GetInt64());
                try { Validate(script); }
                catch (FilmSourceException) { throw new JsonException(); }
                scripts.Add(script);
            }
            return scripts.ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or BridgeException or FormatException)
        { throw new FilmSourceException("INVALID_DOCUMENT"); }
    }

    private static void Validate(CatSourceConfiguration script)
    {
        if (!Regex.IsMatch(script.InstallationId ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
            script.InputRevision is < 1 or > BridgeCodec.MaximumSafeInteger ||
            script.SourceType == "network" && (script.LocalPath is not null || script.LocalHash is not null) ||
            script.SourceType == "local" && (!Regex.IsMatch(script.LocalHash ?? "", @"\A[a-f0-9]{64}\z") ||
                script.LocalPath is not null && (!System.IO.Path.IsPathFullyQualified(script.LocalPath) || script.LocalPath.Any(char.IsControl))))
            throw new FilmSourceException("INVALID_DOCUMENT");
        ValidateSource(script.Name, script.SourceType, script.SourceUrl);
    }
}
