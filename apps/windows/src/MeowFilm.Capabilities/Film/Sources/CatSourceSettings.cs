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
        return PanInterception(root);
    }

    internal async Task<IReadOnlyDictionary<string, bool>> SavePanInterceptionAsync(string provider, bool enabled,
        CancellationToken token)
    {
        if (PanSettingsStore.FindProvider(provider) is null)
            throw new FilmSourceException("INVALID_ARGUMENT");
        var saved = await document.UpdateAsync(current =>
        {
            var root = current ?? Empty;
            var providers = SettingsDocument.SetMember(root.GetProperty("providers"), provider, JsonSerializer.SerializeToElement(enabled));
            return SettingsDocument.SetMember(root, "providers", providers);
        }, token).ConfigureAwait(false);
        return PanInterception(saved);
    }

    private static IReadOnlyDictionary<string, bool> PanInterception(JsonElement root)
    {
        var providers = root.GetProperty("providers");
        return PanSettingsStore.Providers.Select(value => value.Provider)
            .ToDictionary(provider => provider, provider => !providers.TryGetProperty(provider, out var value) || value.GetBoolean(),
                StringComparer.Ordinal);
    }

    internal Task<IReadOnlyList<CatSourceConfiguration>> AddAsync(CatSourceConfiguration script, CancellationToken token) =>
        UpdateAsync(script.InstallationId, current => current is not null
            ? throw new FilmSourceException("STORAGE_CONFLICT") : JsonSerializer.SerializeToElement(script, Json), token);

    internal Task<IReadOnlyList<CatSourceConfiguration>> EditAsync(string installationId, string name, string type,
        string? url, (string Path, string Hash)? input, CancellationToken token) => UpdateAsync(installationId, current =>
    {
        var row = current ?? throw new FilmSourceException("NOT_FOUND");
        bool reuseLocal = type == "local" && input is null && FilmJson.Text(row, "sourceType") == "local";
        string? path = type == "local" ? input?.Path ?? (reuseLocal ? Text(row, "localPath") : null) : null;
        string? hash = type == "local" ? input?.Hash ?? (reuseLocal ? Text(row, "localHash") : null) : null;
        if (type == "local" && hash is null) throw new FilmSourceException("INVALID_ARGUMENT");
        var next = SettingsDocument.Patch(row, new Dictionary<string, JsonElement>
        {
            ["name"] = FilmJson.Encode(name), ["sourceType"] = FilmJson.Encode(type),
            ["sourceUrl"] = JsonSerializer.SerializeToElement(type == "network" ? url : null, Json),
            ["localPath"] = JsonSerializer.SerializeToElement(path, Json), ["localHash"] = JsonSerializer.SerializeToElement(hash, Json)
        });
        bool changed = !FilmJson.Equal(FilmJson.Member(row, "sourceType"), FilmJson.Member(next, "sourceType")) ||
            (type == "network" ? new[] { "sourceUrl" } : new[] { "localPath", "localHash" })
                .Any(field => !FilmJson.Equal(FilmJson.Member(row, field), FilmJson.Member(next, field)));
        long revision;
        try { revision = row.GetProperty("inputRevision").GetInt64(); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new FilmSourceException("INVALID_DOCUMENT"); }
        if (revision is < 1 or > BridgeCodec.MaximumSafeInteger) throw new FilmSourceException("INVALID_DOCUMENT");
        return SettingsDocument.SetMember(next, "inputRevision", FilmJson.Encode(revision + (changed ? 1 : 0)));
    }, token);

    internal Task<IReadOnlyList<CatSourceConfiguration>> EnableAsync(string installationId, bool enabled, CancellationToken token) =>
        UpdateAsync(installationId, row => SettingsDocument.SetMember(row ?? throw new FilmSourceException("NOT_FOUND"),
            "enabled", FilmJson.Encode(enabled)), token);

    internal Task<IReadOnlyList<CatSourceConfiguration>> RemoveAsync(string installationId, CancellationToken token) =>
        UpdateAsync(installationId, row => row is null ? throw new FilmSourceException("NOT_FOUND") : null, token);

    private async Task<IReadOnlyList<CatSourceConfiguration>> UpdateAsync(string installationId,
        Func<JsonElement?, JsonElement?> edit, CancellationToken token)
    {
        if (!ValidIdentity(installationId)) throw new FilmSourceException("INVALID_DOCUMENT");
        var saved = await document.UpdateAsync(current =>
        {
            var root = current ?? Empty;
            var rows = Rows(root);
            var selected = rows.Where(row => Identity(row) == installationId).ToArray();
            if (selected.Length > 1) throw new FilmSourceException("INVALID_DOCUMENT");
            var next = edit(selected.Length == 0 ? null : selected[0]);
            if (next is not null && ReadScript(next.Value).InstallationId != installationId)
                throw new FilmSourceException("INVALID_DOCUMENT");
            var scripts = new List<JsonElement>();
            foreach (var row in rows)
            {
                if (Identity(row) != installationId) scripts.Add(row);
                else if (next is not null) scripts.Add(next.Value);
            }
            if (selected.Length == 0 && next is not null) scripts.Add(next.Value);
            return SettingsDocument.SetMember(root, "scripts", JsonSerializer.SerializeToElement(scripts));
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

    private static JsonElement[] Rows(JsonElement current)
    {
        if (current.ValueKind != JsonValueKind.Object ||
            FilmJson.Member(current, "version") is not { ValueKind: JsonValueKind.Number } version ||
            !version.TryGetInt32(out int number) || number != 1 ||
            FilmJson.Member(current, "scripts").ValueKind != JsonValueKind.Array)
            throw new FilmSourceException("INVALID_DOCUMENT");
        return current.GetProperty("scripts").EnumerateArray().ToArray();
    }

    private static string? Identity(JsonElement row) =>
        FilmJson.Member(row, "installationId") is { ValueKind: JsonValueKind.String } id ? id.GetString() : null;

    private static CatSourceConfiguration[] Parse(JsonElement current)
    {
        var rows = Rows(current);
        var counts = rows.Select(Identity).OfType<string>().GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var scripts = new List<CatSourceConfiguration>();
        foreach (var row in rows)
        {
            if (Identity(row) is not { } id || counts[id] != 1) continue;
            try { scripts.Add(ReadScript(row)); }
            catch (FilmSourceException) { /* Unreadable business rows remain stored outside the management projection. */ }
        }
        return scripts.ToArray();
    }

    private static CatSourceConfiguration ReadScript(JsonElement row)
    {
        try
        {
            string type = Text(row, "sourceType")!;
            var script = new CatSourceConfiguration(Identity(row)!, Text(row, "name")!, type,
                Text(row, "sourceUrl"), type == "local" ? Text(row, "localPath") : null,
                type == "local" ? Text(row, "localHash") : null,
                row.GetProperty("enabled").GetBoolean(), row.GetProperty("inputRevision").GetInt64());
            Validate(script);
            return script;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or
            KeyNotFoundException or FormatException)
        { throw new FilmSourceException("INVALID_DOCUMENT"); }
    }

    private static string? Text(JsonElement row, string name)
    {
        var value = FilmJson.Member(row, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null, JsonValueKind.String => value.GetString(),
            _ => throw new FilmSourceException("INVALID_DOCUMENT")
        };
    }

    private static bool ValidIdentity(string id) => Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z");

    private static void Validate(CatSourceConfiguration script)
    {
        if (!ValidIdentity(script.InstallationId ?? "") ||
            script.InputRevision is < 1 or > BridgeCodec.MaximumSafeInteger ||
            script.SourceType == "local" && (!Regex.IsMatch(script.LocalHash ?? "", @"\A[a-f0-9]{64}\z") ||
                script.LocalPath is not null && (!System.IO.Path.IsPathFullyQualified(script.LocalPath) || script.LocalPath.Any(char.IsControl))))
            throw new FilmSourceException("INVALID_DOCUMENT");
        ValidateSource(script.Name, script.SourceType, script.SourceUrl);
    }
}
