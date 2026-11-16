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
internal sealed class CatSourceSettings(IDocumentStore store)
{
    internal const string DocumentNamespace = "film.cat-sources.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SettingsDocument document = new(store, DocumentNamespace);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new
        { version = 1, port = 0, providers = new { }, mappings = Array.Empty<object>(), scripts = Array.Empty<object>() });

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ReadAsync(CancellationToken token) =>
        Parse(await document.ReadAsync(token).ConfigureAwait(false) ?? Empty);

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ChangeAsync(
        Func<IReadOnlyList<CatSourceConfiguration>, IEnumerable<CatSourceConfiguration>> edit, CancellationToken token)
    {
        var saved = await document.UpdateAsync(current =>
        {
            var root = current ?? Empty;
            var before = Parse(root).ToDictionary(item => item.InstallationId);
            var next = edit(before.Values.ToArray()).ToArray();
            if (next.Select(item => item.InstallationId).Distinct(StringComparer.Ordinal).Count() != next.Length)
                throw new FilmSourceException("INVALID_DOCUMENT");
            var rows = BridgeCodec.RequiredProperty(root, "scripts").EnumerateArray().ToArray();
            foreach (var script in next)
            {
                if (before.TryGetValue(script.InstallationId, out var prior) && script == prior) continue;
                if (prior is null && rows.Any(row => Ids(row).Contains(script.InstallationId)))
                    throw new FilmSourceException("STORAGE_CONFLICT");
                Validate(script);
            }
            var byId = next.ToDictionary(item => item.InstallationId);
            var scripts = FilmDocument.Write(writer =>
            {
                writer.WriteStartArray();
                foreach (var row in rows)
                {
                    var ids = Ids(row);
                    var id = ids.Length == 1 ? ids[0] : null;
                    if (id is null || !before.TryGetValue(id, out var prior))
                    { writer.WriteRawValue(row.GetRawText(), skipInputValidation: true); continue; }
                    if (byId.TryGetValue(id, out var changed))
                    {
                        var previous = JsonSerializer.SerializeToElement(prior, Json);
                        var edits = JsonSerializer.SerializeToElement(changed, Json).EnumerateObject()
                            .Where(field => !JsonElement.DeepEquals(previous.GetProperty(field.Name), field.Value))
                            .ToDictionary(field => field.Name, field => field.Value);
                        writer.WriteRawValue(SettingsDocument.Patch(row, edits).GetRawText(), skipInputValidation: true);
                    }
                }
                foreach (var script in next)
                    if (!before.ContainsKey(script.InstallationId)) JsonSerializer.SerializeToElement(script, Json).WriteTo(writer);
                writer.WriteEndArray();
            });
            return FilmDocument.Set(root, "scripts", scripts);
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

    private static string[] Ids(JsonElement row) => row.ValueKind == JsonValueKind.Object
        ? row.EnumerateObject().Where(item => item.NameEquals("installationId") && item.Value.ValueKind == JsonValueKind.String)
            .Select(item => item.Value.GetString()!).ToArray() : [];

    private static CatSourceConfiguration[] Parse(JsonElement current)
    {
        try
        {
            if (BridgeCodec.RequiredProperty(current, "version").GetInt32() != 1) throw new JsonException();
            var rows = BridgeCodec.RequiredProperty(current, "scripts").EnumerateArray().ToArray();
            var counts = rows.SelectMany(Ids).GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
            var scripts = new List<CatSourceConfiguration>();
            foreach (var row in rows)
            {
                var ids = Ids(row);
                if (ids.Length != 1 || counts[ids[0]] != 1) continue;
                try
                {
                    var revision = BridgeCodec.RequiredProperty(row, "inputRevision").GetInt64();
                    if (revision is < 1 or > BridgeCodec.MaximumSafeInteger) continue;
                    string? type = BridgeCodec.RequiredProperty(row, "sourceType").GetString();
                    if (type is not ("local" or "network")) continue;
                    string? Text(string name) => FilmJson.Member(row, name) is { ValueKind: JsonValueKind.String } text
                        ? text.GetString() : null;
                    scripts.Add(new(ids[0], Text("name") ?? "", type,
                        type == "network" ? Text("sourceUrl") : null,
                        type == "local" ? Text("localPath") : null, type == "local" ? Text("localHash") : null,
                        BridgeCodec.RequiredProperty(row, "enabled").GetBoolean(), revision));
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or BridgeException or FormatException) { }
            }
            if (rows.Length > 0 && scripts.Count == 0) throw new JsonException();
            return scripts.ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or BridgeException or FormatException)
        { throw new FilmSourceException("INVALID_DOCUMENT"); }
    }

    private static void Validate(CatSourceConfiguration script)
    {
        if (!Regex.IsMatch(script.InstallationId ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
            script.InputRevision is < 1 or > BridgeCodec.MaximumSafeInteger ||
            script.SourceType == "local" && (!Regex.IsMatch(script.LocalHash ?? "", @"\A[a-f0-9]{64}\z") ||
                script.LocalPath is not null && (!System.IO.Path.IsPathFullyQualified(script.LocalPath) || script.LocalPath.Any(char.IsControl))))
            throw new FilmSourceException("INVALID_DOCUMENT");
        ValidateSource(script.Name, script.SourceType, script.SourceType == "network" ? script.SourceUrl : null);
    }
}
