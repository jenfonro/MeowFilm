using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities;

/// <summary>The six declared native settings capabilities on the private runtime channel.</summary>
public sealed class FilmRuntimeSettings : IRuntimeSettingsStore
{
    private readonly PanCredentialRepository pan;
    private readonly IReadOnlyDictionary<string, TypedRecordRepository> blocks;
    public FilmRuntimeSettings(IStorageDatabase database)
    {
        pan = new(database);
        blocks = new Dictionary<string, TypedRecordRepository>(StringComparer.Ordinal)
        {
            ["cat.sources"] = new(database, "film.cat-sources.v1", FilmStorageRecords.Cat),
            ["tmdb"] = new(database, "film.tmdb.v1", FilmStorageRecords.TMDB),
            ["douban"] = new(database, "film.douban.v1", FilmStorageRecords.Douban),
            ["film.rules"] = new(database, "film.rules.v1", FilmStorageRecords.Rules),
            ["film.preferences"] = new(database, "film.preferences.v1", FilmStorageRecords.Preferences)
        };
    }
    public async Task<RuntimeSettingsSnapshot> ReadAsync(string block, CancellationToken token = default)
    {
        if (block == "pan.accounts") return await pan.SnapshotAsync(token).ConfigureAwait(false);
        if (!blocks.TryGetValue(block, out var repository)) throw new BridgeException("INVALID_REQUEST");
        var snapshot = await repository.ReadAsync(token).ConfigureAwait(false);
        using var json = snapshot.Data is null ? null : JsonDocument.Parse(snapshot.Data);
        return new(json?.RootElement.Clone(), snapshot.Revision.Revision);
    }
    public Task<RuntimeSettingsSnapshot> UpdatePanAsync(JsonElement command, CancellationToken token = default) => pan.UpdateAsync(command, token);
}
