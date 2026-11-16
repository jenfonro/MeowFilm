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
    private sealed record Mapping(string Provider, string[] Aliases);
    private sealed record Configuration(int Version, int Port, Dictionary<string, bool> Providers,
        Mapping[] Mappings, CatSourceConfiguration[] Scripts);
    private static Configuration Empty => new(1, 0, [], [], []);

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ReadAsync(CancellationToken token) =>
        Parse(await document.ReadAsync(token).ConfigureAwait(false)).Scripts;

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ChangeAsync(
        Func<IReadOnlyList<CatSourceConfiguration>, IEnumerable<CatSourceConfiguration>> edit, CancellationToken token)
    {
        var saved = await document.UpdateAsync(current =>
        {
            var before = Parse(current);
            var next = before with { Scripts = edit(before.Scripts).ToArray() };
            Validate(next);
            return JsonSerializer.SerializeToElement(next, Json);
        }, token).ConfigureAwait(false);
        return Parse(saved).Scripts;
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

    private static Configuration Parse(JsonElement? current)
    {
        if (current is null) return Empty;
        try
        {
            var parsed = current.Value.Deserialize<Configuration>(Json) ?? throw new FilmSourceException("INVALID_DOCUMENT");
            Validate(parsed);
            return parsed;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new FilmSourceException("INVALID_DOCUMENT"); }
    }

    private static void Validate(Configuration value)
    {
        string[] providers = ["baidu", "quark", "uc", "tianyi", "yidong"];
        if (value.Version != 1 || value.Port is < 0 or > 65535 || value.Providers is null ||
            value.Mappings is null || value.Scripts is null || value.Providers.Keys.Except(providers).Any() ||
            value.Scripts.Select(script => script?.InstallationId).Distinct(StringComparer.Ordinal).Count() != value.Scripts.Length ||
            value.Mappings.Any(mapping => mapping is null || !providers.Contains(mapping.Provider) || mapping.Aliases is null ||
                mapping.Aliases.Any(alias => string.IsNullOrWhiteSpace(alias) || alias.Length > 256 || alias.Any(char.IsControl))) ||
            value.Mappings.SelectMany(mapping => mapping.Aliases).Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Mappings.Sum(mapping => mapping.Aliases.Length))
            throw new FilmSourceException("INVALID_DOCUMENT");
        foreach (var script in value.Scripts)
        {
            if (script is null || !Regex.IsMatch(script.InstallationId ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
                script.InputRevision is < 1 or > BridgeCodec.MaximumSafeInteger ||
                script.SourceType == "network" && (script.LocalPath is not null || script.LocalHash is not null) ||
                script.SourceType == "local" && (!Regex.IsMatch(script.LocalHash ?? "", @"\A[a-f0-9]{64}\z") ||
                    script.LocalPath is not null && (!System.IO.Path.IsPathFullyQualified(script.LocalPath) ||
                        script.LocalPath.Any(char.IsControl))))
                throw new FilmSourceException("INVALID_DOCUMENT");
            ValidateSource(script.Name, script.SourceType, script.SourceUrl);
        }
    }
}
