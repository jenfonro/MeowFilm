using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>A durable generation and exact receipt, independent of business revisions.</summary>
public sealed record MusicLibraryRevision(long Generation, string Receipt)
{
    public static MusicLibraryRevision Empty { get; } = new(0, "");
}

/// <summary>Owner receipt and any explicitly supplied controls. Ordinary reads select controls on demand.</summary>
public sealed record MusicLibrarySnapshot(MusicLibraryRevision Revision, IReadOnlyDictionary<string, JsonNode?> Areas);

public sealed record MusicLibrarySelectedRow(object?[] Key, int Ordinal, JsonObject Value);
public sealed record MusicLibraryRowChange(string Area, string[] Relations, object?[] Key, JsonObject? Value, int Ordinal, bool Insert = false);
public sealed record MusicLibraryChanges(IReadOnlyDictionary<string, JsonObject> Areas,
    IReadOnlyDictionary<string, JsonObject> Songs)
{
    public IReadOnlyList<MusicLibraryRowChange> Rows { get; init; } = [];
    public IReadOnlySet<string> PartialAreas { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// The native music owner's record store. One commit atomically writes the supplied
/// songs, source indexes, control records and receipt; unchanged records are untouched.
/// </summary>
public interface IMusicLibraryStore : IAsyncDisposable
{
    Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken cancellationToken = default);
    JsonObject? ReadArea(string scope, MusicLibraryRevision revision, string area, string[]? members = null);
    IReadOnlyList<MusicLibrarySelectedRow> ReadRows(string scope, MusicLibraryRevision revision, string area,
        string[] relations, object?[] prefix, string[] members);
    JsonObject? ReadRow(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key);
    bool HasRelation(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, string relation);
    bool Matches(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, JsonObject value);
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
