using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>Record-sized working set. SQL owns the persistent indexes; a command owns only its changes.</summary>
internal sealed class MusicSongSession(IMusicLibraryStore store,
    IReadOnlyDictionary<string, JsonObject?>? before = null, bool cacheReads = true)
{
    internal IMusicLibraryStore Store { get; } = store;
    private readonly Dictionary<string, JsonObject?> original = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> changed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> stagedSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> stagedFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> stagedCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> knownSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> existence = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, JsonObject> Changes => changed;
    internal IReadOnlyDictionary<string, JsonObject?> BeforeChanges => changed.Keys
        .ToDictionary(id => id, id => Original(id), StringComparer.Ordinal);

    private JsonObject? Original(string id)
    {
        if (before is not null && before.TryGetValue(id, out var saved)) return saved;
        if (original.TryGetValue(id, out saved)) return saved;
        saved = Store.ReadSong(id);
        if (cacheReads) original[id] = saved;
        return saved;
    }
    internal JsonObject? Read(string id) =>
        (changed.TryGetValue(id, out var song) ? song : Original(id))?.DeepClone().AsObject();
    internal bool Contains(string id)
    {
        if (changed.ContainsKey(id)) return true;
        if (before is not null && before.TryGetValue(id, out var previous)) return previous is not null;
        if (original.TryGetValue(id, out var song)) return song is not null;
        if (existence.TryGetValue(id, out bool found)) return found;
        found = Store.ContainsSong(id);
        if (cacheReads) existence[id] = found;
        return found;
    }
    internal void Set(JsonObject song)
    {
        string id = LJ.S(song["songId"]);
        if (changed.Remove(id, out var previous))
        {
            foreach (var source in Sources(previous)) stagedSources.Remove(LJ.S(source["sourceKey"]));
            stagedFiles.Remove(LJ.S(previous["localFileId"]));
            string fingerprint = NativeMusicSong.Fingerprint(previous);
            if (stagedCandidates.TryGetValue(fingerprint, out var values))
            { values.Remove(id); if (values.Count == 0) stagedCandidates.Remove(fingerprint); }
        }
        if (JsonNode.DeepEquals(Original(id), song)) changed.Remove(id);
        else
        {
            changed[id] = song.DeepClone().AsObject();
            foreach (var source in Sources(song)) stagedSources[LJ.S(source["sourceKey"])] = id;
            string file = LJ.S(song["localFileId"]);
            if (file.Length > 0) stagedFiles[file] = id;
            string fingerprint = NativeMusicSong.Fingerprint(song);
            if (fingerprint.Length > 0 && NativeMusicSong.IsOnline(song))
            {
                if (!stagedCandidates.TryGetValue(fingerprint, out var values)) stagedCandidates[fingerprint] = values = [];
                values.Add(id);
            }
        }
    }
    internal string? SourceOwner(string key)
    {
        if (key.Length == 0) return null;
        if (stagedSources.TryGetValue(key, out var staged)) return staged;
        if (before is not null)
            foreach (var pair in before)
                if (pair.Value is { } song && Sources(song).Any(source => LJ.S(source["sourceKey"]) == key))
                    return pair.Key;
        if (knownSources.TryGetValue(key, out var id)) return id;
        id = Store.FindSource(key);
        if (id is not null && before is not null && before.ContainsKey(id)) id = null;
        if (cacheReads) knownSources[key] = id;
        return id;
    }
    internal string? Registered(JsonObject song)
    {
        foreach (var source in Sources(song))
            if (SourceOwner(LJ.S(source["sourceKey"])) is { } id) return id;
        string file = LJ.S(song["localFileId"]);
        if (file.Length > 0)
        {
            if (stagedFiles.TryGetValue(file, out var staged)) return staged;
            if (before is not null)
                foreach (var pair in before)
                    if (LJ.S(pair.Value?["localFileId"]) == file) return pair.Key;
            if (Store.FindLocalFile(file) is { } id && Contains(id)) return id;
        }
        string explicitId = LJ.S(song["songId"]);
        return explicitId.Length > 0 && Contains(explicitId) ? explicitId : null;
    }
    internal string? Find(JsonObject song, IReadOnlySet<string>? allowed = null)
    {
        if (Registered(song) is { } known) return allowed is null || allowed.Contains(known) ? known : null;
        if (!NativeMusicSong.IsOnline(song)) return null;
        var identity = NativeMusicSong.ReadIdentity(song);
        if (identity.Fingerprint.Length == 0 || identity.Duration <= 0) return null;
        var ids = Store.FindCandidates(identity.Fingerprint, identity.Duration)
            .Where(id => before is null || !before.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        if (before is not null)
            foreach (var pair in before)
                if (pair.Value is { } previous && NativeMusicSong.IsOnline(previous) &&
                    NativeMusicSong.ReadIdentity(previous).Compatible(identity)) ids.Add(pair.Key);
        ids.RemoveWhere(changed.ContainsKey);
        if (stagedCandidates.TryGetValue(identity.Fingerprint, out var staged)) ids.UnionWith(staged);
        string? match = null;
        foreach (string id in ids)
        {
            if (allowed is not null && !allowed.Contains(id)) continue;
            if (Read(id) is not { } candidate || !NativeMusicSong.IsOnline(candidate) ||
                !NativeMusicSong.ReadIdentity(candidate).Compatible(identity)) continue;
            if (match is not null) return null;
            match = id;
        }
        return match;
    }
    internal JsonObject Register(JsonObject normalized, string? existingId, bool authoritative)
    {
        var incoming = new NativeMusicSong.Recordings(normalized);
        if (existingId is null)
        {
            incoming.Id = Guid.NewGuid().ToString("D");
            return incoming.ToSong();
        }
        var previous = Read(existingId) ?? throw new MusicLibraryException("SONG_NOT_FOUND");
        if (JsonNode.DeepEquals(previous, normalized)) return previous;
        var record = new NativeMusicSong.Recordings(previous);
        var eligible = incoming.Sources.Where(source => SourceOwner(LJ.S(source["sourceKey"])) is not { } owner ||
            owner == existingId).ToArray();
        var update = new NativeMusicSong.Recordings(normalized, eligible) { Id = existingId };
        record.TryMerge(update, authoritative);
        return record.ToSong();
    }
    internal IReadOnlyList<string> Artwork(string address)
    {
        var ids = Store.FindArtwork(address).Where(id => before is null || !before.ContainsKey(id))
            .ToHashSet(StringComparer.Ordinal);
        if (before is not null)
            foreach (var pair in before)
                if (LJ.S(pair.Value?["cover_url"]) == address) ids.Add(pair.Key);
        foreach (var pair in changed)
        {
            ids.Remove(pair.Key);
            if (LJ.S(pair.Value["cover_url"]) == address) ids.Add(pair.Key);
        }
        return ids.ToArray();
    }
    internal IReadOnlyList<string> Query(string query)
    {
        var ids = Store.FindQuery(query).Where(id => before is null || !before.ContainsKey(id))
            .ToHashSet(StringComparer.Ordinal);
        if (before is not null)
            foreach (var pair in before)
                if (pair.Value is { } song && MusicSongMatcher.Query(song) == query) ids.Add(pair.Key);
        foreach (var pair in changed)
        {
            ids.Remove(pair.Key);
            if (MusicSongMatcher.Query(pair.Value) == query) ids.Add(pair.Key);
        }
        return ids.ToArray();
    }
    internal IEnumerable<string> Ids() => Store.SongIds().Concat(changed.Keys).Distinct(StringComparer.Ordinal);
    private static IEnumerable<JsonObject> Sources(JsonObject song) =>
        (song["sources"] as JsonArray ?? []).OfType<JsonObject>();
}
