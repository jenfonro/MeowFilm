using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>A durable generation and exact receipt, independent of business revisions.</summary>
public sealed record MusicLibraryRevision(long Generation, string Receipt)
{
    public static MusicLibraryRevision Empty { get; } = new(0, "");
}

/// <summary>Caller-owned control records. Songs are read by identity, never embedded here.</summary>
public sealed record MusicLibrarySnapshot(MusicLibraryRevision Revision, IReadOnlyDictionary<string, JsonNode?> Areas);

public sealed record MusicLibraryChanges(IReadOnlyDictionary<string, JsonObject> Areas,
    IReadOnlyDictionary<string, JsonObject> Songs);

/// <summary>
/// The native music owner's record store. One commit atomically writes the supplied
/// songs, source indexes, control records and receipt; unchanged records are untouched.
/// </summary>
public interface IMusicLibraryStore : IAsyncDisposable
{
    Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken cancellationToken = default);
    JsonObject? ReadSong(string id);
    bool ContainsSong(string id);
    string? FindSource(string sourceKey);
    string? FindLocalFile(string localFileId);
    IReadOnlyList<string> FindCandidates(string fingerprint, double baselineDuration);
    IReadOnlyList<string> FindArtwork(string address);
    IReadOnlyList<string> FindQuery(string query);
    IReadOnlyList<string> SongIds();
    Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected, MusicLibraryRevision next,
        MusicLibraryChanges changes, CancellationToken cancellationToken = default);
}
