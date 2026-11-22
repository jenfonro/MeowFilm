using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities;

/// <summary>The six declared native settings capabilities on the private runtime channel.</summary>
public sealed class FilmRuntimeSettings : IRuntimeSettingsStore
{
    private readonly IStorageDatabase database;
    private readonly PanCredentialRepository pan;
    public FilmRuntimeSettings(IStorageDatabase database)
    {
        this.database = database;
        pan = new(database);
    }
    public Task<RuntimeSettingsSnapshot> ReadAsync(string block, CancellationToken token = default) => block switch
    {
        "pan.accounts" => pan.SnapshotAsync(token),
        "cat.sources" => CatSourceSettingsStore.ReadRuntimeSnapshotAsync(database, token),
        "tmdb" => FilmMetadataSettingsStore.ReadTmdbRuntimeSnapshotAsync(database, token),
        "douban" => FilmMetadataSettingsStore.ReadDoubanRuntimeSnapshotAsync(database, token),
        "film.rules" => FilmRulesStore.ReadRuntimeSnapshotAsync(database, token),
        "film.preferences" => FilmPreferencesStore.ReadRuntimeSnapshotAsync(database, token),
        _ => throw new BridgeException("INVALID_REQUEST")
    };
    public Task<RuntimeSettingsSnapshot> UpdatePanAsync(JsonElement command, CancellationToken token = default) => pan.UpdateAsync(command, token);
}
