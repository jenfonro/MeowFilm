using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>One command's copy-on-write control records and isolated song changes.</summary>
internal sealed class MusicLibraryData
{
    private readonly IReadOnlyDictionary<string, JsonNode?> original;
    private readonly Dictionary<string, JsonNode?> fetched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode?> accessed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> assigned = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownSongs;
    private readonly bool writable;
    private readonly string scope;
    private readonly MusicLibraryRevision revision;
    private readonly List<MusicLibraryRowChange> rows = [];
    private readonly HashSet<string> partialAreas = new(StringComparer.Ordinal);
    internal MusicSongSession Songs { get; }

    internal MusicLibraryData(IMusicLibraryStore store, IReadOnlyDictionary<string, JsonNode?> areas, bool writable = false,
        HashSet<string>? knownSongs = null, string scope = "local", MusicLibraryRevision? revision = null)
    {
        this.scope = scope; this.revision = revision ?? MusicLibraryRevision.Empty;
        original = areas; this.writable = writable; this.knownSongs = knownSongs ?? new(StringComparer.Ordinal);
        Songs = new(store, knownSongs: this.knownSongs);
    }

    internal MusicLibraryData Candidate(bool write) => new(Songs.Store, PublishedAreas(), write, knownSongs, scope, revision);
    internal MusicLibraryData Publish(MusicLibraryRevision next)
    {
        knownSongs.UnionWith(Songs.Changes.Keys);
        return new(Songs.Store, PublishedAreas(), knownSongs: knownSongs, scope: scope, revision: next);
    }
    internal JsonNode? Value(string area)
    {
        if (assigned.TryGetValue(area, out var assignedValue)) return assignedValue;
        if (accessed.TryGetValue(area, out var value)) return value;
        if (!original.TryGetValue(area, out value))
        {
            value = Songs.Store.ReadArea(scope, revision, area);
            fetched[area] = value;
        }
        if (!writable) return accessed[area] = value;
        return accessed[area] = value?.DeepClone();
    }
    internal bool HasValue(string area) => Value(area) is not null;
    internal bool IsLoaded(string area) => assigned.ContainsKey(area) || accessed.ContainsKey(area) || original.ContainsKey(area);
    internal JsonObject? Select(string area, params string[] members)
    {
        if (!IsLoaded(area)) return Songs.Store.ReadArea(scope, revision, area, members);
        var value = Value(area) as JsonObject;
        return value is null ? null : new JsonObject(value.Where(pair => members.Contains(pair.Key))
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
    }
    internal IReadOnlyList<RelationalRecord.SelectedRow> Rows(string area, string[] relations, object?[] prefix, params string[] members) =>
        Songs.Store.ReadRows(scope, revision, area, relations, prefix, members);
    internal JsonObject Row(string area, string[] relations, object?[] key) =>
        Songs.Store.ReadRow(scope, revision, area, relations, key) ?? throw new MusicLibraryException("CORRUPT_STORAGE");
    internal bool HasRelation(string area, string[] relations, object?[] key, string relation) =>
        Songs.Store.HasRelation(scope, revision, area, relations, key, relation);
    internal JsonObject SelectRow(string area, string[] relations, object?[] key, params string[] members) =>
        Rows(area, relations, key, members).SingleOrDefault()?.Value ?? throw new MusicLibraryException("CORRUPT_STORAGE");
    internal bool Matches(string area, string[] relations, object?[] key, JsonObject value) =>
        Songs.Store.Matches(scope, revision, area, relations, key, value);
    internal string Scope => scope;
    internal void ChangeRow(string area, string[] relations, object?[] key, JsonObject? value, int ordinal, bool insert = false) =>
        rows.Add(new(area, relations, key, value, ordinal, insert));
    internal void Set(string area, JsonObject value, bool partial = false)
    {
        LJ.Require(writable, "INVALID_REQUEST");
        if (partial) partialAreas.Add(area); else partialAreas.Remove(area);
        assigned[area] = IsLoaded(area) && Value(area) is JsonObject previous
            ? LJ.Merge(previous.DeepClone().AsObject(), value)
            : value.Parent is null ? value : value.DeepClone().AsObject();
    }
    internal IReadOnlyDictionary<string, JsonObject> AreaChanges() => assigned
        .Where(pair => !(original.TryGetValue(pair.Key, out var previous) || fetched.TryGetValue(pair.Key, out previous)) || !JsonNode.DeepEquals(previous, pair.Value))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    internal MusicLibraryChanges Changes() => new(AreaChanges(), Songs.Changes) { Rows = rows.ToArray(), PartialAreas = partialAreas.ToHashSet(StringComparer.Ordinal) };
    internal IReadOnlyDictionary<string, JsonNode?> PublishedAreas()
    {
        var values = new Dictionary<string, JsonNode?>(original, StringComparer.Ordinal);
        foreach (var pair in accessed) values[pair.Key] = pair.Value;
        foreach (var pair in assigned)
            if (partialAreas.Contains(pair.Key)) values.Remove(pair.Key);
            else values[pair.Key] = pair.Value;
        foreach (var row in rows) values.Remove(row.Area);
        return values;
    }
}
