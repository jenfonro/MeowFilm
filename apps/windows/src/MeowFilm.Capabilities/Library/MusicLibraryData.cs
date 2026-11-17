using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>One command's copy-on-write control records and isolated song changes.</summary>
internal sealed class MusicLibraryData
{
    private readonly IReadOnlyDictionary<string, JsonObject> original;
    private readonly Dictionary<string, JsonObject> accessed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> assigned = new(StringComparer.Ordinal);
    private readonly bool writable;
    internal MusicSongSession Songs { get; }
    internal IEnumerable<string> Areas => original.Keys.Concat(assigned.Keys).Distinct(StringComparer.Ordinal);

    internal MusicLibraryData(IMusicLibraryStore store, IReadOnlyDictionary<string, JsonObject> areas, bool writable = false)
    {
        original = areas; this.writable = writable; Songs = new(store);
    }

    internal MusicLibraryData Candidate(bool write) => new(Songs.Store, PublishedAreas(), write);
    internal JsonObject? Value(string area)
    {
        if (assigned.TryGetValue(area, out var value) || accessed.TryGetValue(area, out value)) return value;
        if (!original.TryGetValue(area, out value)) return null;
        if (!writable) return value;
        return accessed[area] = value.DeepClone().AsObject();
    }
    internal bool HasValue(string area) => assigned.ContainsKey(area) || original.ContainsKey(area);
    internal void Set(string area, JsonObject value)
    {
        LJ.Require(writable, "INVALID_REQUEST");
        assigned[area] = value.Parent is null ? value : value.DeepClone().AsObject();
    }
    internal IReadOnlyDictionary<string, JsonObject> AreaChanges() => assigned
        .Where(pair => !original.TryGetValue(pair.Key, out var previous) || !JsonNode.DeepEquals(previous, pair.Value))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    internal MusicLibraryChanges Changes() => new(AreaChanges(), Songs.Changes);
    internal IReadOnlyDictionary<string, JsonObject> PublishedAreas()
    {
        var values = new Dictionary<string, JsonObject>(original, StringComparer.Ordinal);
        foreach (var pair in assigned) values[pair.Key] = pair.Value;
        return values;
    }
}
