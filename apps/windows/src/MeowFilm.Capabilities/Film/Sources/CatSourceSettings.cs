using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record CatSourceConfiguration(string InstallationId, string Name, string SourceType,
    string? SourceUrl, string? LocalPath, string? LocalHash, bool Enabled, long InputRevision);

/// <summary>User source intent only. The runtime owns downloaded/installed source and execution facts.</summary>
internal sealed class CatSourceSettings(IStorageDatabase database)
{
    internal const string StorageOwner = "film.cat-sources.v1";

    internal async Task<IReadOnlyList<CatSourceConfiguration>> ReadAsync(CancellationToken token) =>
        (await CatSourceSettingsStore.ReadOrEmptyAsync(database, token).ConfigureAwait(false)).Scripts;

    internal async Task<IReadOnlyDictionary<string, bool>> ReadPanInterceptionAsync(CancellationToken token)
    {
        var state = await CatSourceSettingsStore.ReadOrEmptyAsync(database, token).ConfigureAwait(false);
        return PanInterception(state);
    }

    internal async Task<IReadOnlyDictionary<string, bool>> SavePanInterceptionAsync(string provider, bool enabled,
        CancellationToken token)
    {
        if (PanSettingsStore.FindProvider(provider) is null)
            throw new FilmSourceException("INVALID_ARGUMENT");
        var saved = await CatSourceSettingsStore.UpdateAsync(database, current =>
        {
            var providers = current.Providers.ToList();
            int idx = providers.FindIndex(item => string.Equals(item.Provider, provider, StringComparison.Ordinal));
            if (idx >= 0) providers[idx] = (provider, enabled);
            else providers.Add((provider, enabled));
            return current with { Providers = providers };
        }, token).ConfigureAwait(false);
        return PanInterception(saved);
    }

    private static IReadOnlyDictionary<string, bool> PanInterception(CatSettingsState state)
    {
        var providers = state.Providers.ToDictionary(item => item.Provider, item => item.Enabled, StringComparer.Ordinal);
        return PanSettingsStore.Providers.Select(value => value.Provider)
            .ToDictionary(provider => provider, provider => !providers.TryGetValue(provider, out bool value) || value,
                StringComparer.Ordinal);
    }

    internal Task<IReadOnlyList<CatSourceConfiguration>> AddAsync(CatSourceConfiguration script, CancellationToken token) =>
        UpdateAsync(script.InstallationId, current => current is not null
            ? throw new FilmSourceException("STORAGE_CONFLICT") : script, token);

    internal Task<IReadOnlyList<CatSourceConfiguration>> EditAsync(string installationId, string name, string type,
        string? url, (string Path, string Hash)? input, CancellationToken token) => UpdateAsync(installationId, current =>
    {
        var row = current ?? throw new FilmSourceException("NOT_FOUND");
        bool reuseLocal = type == "local" && input is null && row.SourceType == "local";
        string? path = type == "local" ? input?.Path ?? (reuseLocal ? row.LocalPath : null) : null;
        string? hash = type == "local" ? input?.Hash ?? (reuseLocal ? row.LocalHash : null) : null;
        if (type == "local" && hash is null) throw new FilmSourceException("INVALID_ARGUMENT");
        string? nextUrl = type == "network" ? url : null;
        bool changed = row.SourceType != type ||
            (type == "network" ? row.SourceUrl != nextUrl : row.LocalPath != path || row.LocalHash != hash);
        return row with
        {
            Name = name,
            SourceType = type,
            SourceUrl = nextUrl,
            LocalPath = path,
            LocalHash = hash,
            InputRevision = row.InputRevision + (changed ? 1 : 0)
        };
    }, token);

    internal Task<IReadOnlyList<CatSourceConfiguration>> EnableAsync(string installationId, bool enabled, CancellationToken token) =>
        UpdateAsync(installationId, row => (row ?? throw new FilmSourceException("NOT_FOUND")) with { Enabled = enabled }, token);

    internal Task<IReadOnlyList<CatSourceConfiguration>> RemoveAsync(string installationId, CancellationToken token) =>
        UpdateAsync(installationId, row => row is null ? throw new FilmSourceException("NOT_FOUND") : null, token);

    private async Task<IReadOnlyList<CatSourceConfiguration>> UpdateAsync(string installationId,
        Func<CatSourceConfiguration?, CatSourceConfiguration?> edit, CancellationToken token)
    {
        if (!ValidIdentity(installationId)) throw new FilmSourceException("INVALID_DOCUMENT");
        var saved = await CatSourceSettingsStore.UpdateAsync(database, current =>
        {
            var rows = current.Scripts;
            var selected = rows.FirstOrDefault(row => row.InstallationId == installationId);
            var next = edit(selected);
            if (next is not null)
            {
                if (next.InstallationId != installationId) throw new FilmSourceException("INVALID_DOCUMENT");
                Validate(next);
            }
            var scripts = new List<CatSourceConfiguration>();
            foreach (var row in rows)
            {
                if (row.InstallationId != installationId) scripts.Add(row);
                else if (next is not null) scripts.Add(next);
            }
            if (selected is null && next is not null) scripts.Add(next);
            return current with { Scripts = scripts };
        }, token).ConfigureAwait(false);
        return saved.Scripts;
    }

    internal static async Task<(string Path, string Hash)> SelectLocalAsync(string path, CancellationToken token)
    {
        var input = await SelectedScriptSnapshot.ReadAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return (System.IO.Path.GetFullPath(path), Convert.ToHexStringLower(SHA256.HashData(input.Bytes)));
    }

    internal static void ValidateSource(string name, string type, string? url)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || type is not ("local" or "network") ||
            type == "network" && (url is null || url.Any(char.IsControl) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) ||
            type == "local" && url is not null)
            throw new FilmSourceException("INVALID_ARGUMENT");
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
