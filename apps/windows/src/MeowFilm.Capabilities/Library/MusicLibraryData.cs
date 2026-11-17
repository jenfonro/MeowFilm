using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>One command's copy-on-write control records and isolated song changes.</summary>
internal sealed class MusicLibraryData
{
    private readonly IReadOnlyDictionary<string, JsonNode?> original;
    private readonly Dictionary<string, JsonNode?> accessed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> assigned = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownSongs;
    private readonly bool writable;
    internal MusicSongSession Songs { get; }
    internal IEnumerable<string> Areas => original.Keys.Concat(assigned.Keys).Distinct(StringComparer.Ordinal);

    internal MusicLibraryData(IMusicLibraryStore store, IReadOnlyDictionary<string, JsonNode?> areas, bool writable = false,
        HashSet<string>? knownSongs = null)
    {
        original = areas; this.writable = writable; this.knownSongs = knownSongs ?? new(StringComparer.Ordinal);
        Songs = new(store, knownSongs: this.knownSongs);
    }

    internal MusicLibraryData Candidate(bool write) => new(Songs.Store, PublishedAreas(), write, knownSongs);
    internal MusicLibraryData Published()
    {
        knownSongs.UnionWith(Songs.Changes.Keys);
        return Candidate(false);
    }
    internal JsonNode? Value(string area)
    {
        if (assigned.TryGetValue(area, out var assignedValue)) return assignedValue;
        if (accessed.TryGetValue(area, out var value)) return value;
        if (!original.TryGetValue(area, out value)) return null;
        if (!writable) return value;
        return accessed[area] = value?.DeepClone();
    }
    internal bool HasValue(string area) => assigned.ContainsKey(area) || original.ContainsKey(area);
    internal void Set(string area, JsonObject value)
    {
        LJ.Require(writable, "INVALID_REQUEST");
        assigned[area] = Value(area) is JsonObject previous
            ? LJ.Merge(previous.DeepClone().AsObject(), value)
            : value.Parent is null ? value : value.DeepClone().AsObject();
    }
    internal IReadOnlyDictionary<string, JsonObject> AreaChanges() => assigned
        .Where(pair => !original.TryGetValue(pair.Key, out var previous) || !JsonNode.DeepEquals(previous, pair.Value))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    internal MusicLibraryChanges Changes() => new(AreaChanges(), Songs.Changes);
    internal IReadOnlyDictionary<string, JsonNode?> PublishedAreas()
    {
        var values = new Dictionary<string, JsonNode?>(original, StringComparer.Ordinal);
        foreach (var pair in assigned) values[pair.Key] = pair.Value;
        return values;
    }
}
