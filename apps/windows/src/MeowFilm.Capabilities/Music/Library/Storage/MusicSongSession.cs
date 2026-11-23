using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Strongly-typed record-sized working set. SQL owns persistent indexes; a command owns only its changes.</summary>
internal sealed class MusicSongSession(MusicLibraryDatabase store,
    IReadOnlyDictionary<string, MusicSongRecord?>? before = null, bool cacheReads = true, HashSet<string>? knownSongs = null)
{
    internal MusicLibraryDatabase Store { get; } = store;
    private readonly Dictionary<string, MusicSongRecord?> original = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MusicSongRecord> changed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> stagedSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> stagedFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> stagedCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> knownSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> existence = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, MusicSongRecord> Changes => changed;
    internal IReadOnlyDictionary<string, MusicSongRecord?> BeforeChanges => changed.Keys
        .ToDictionary(id => id, id => Original(id), StringComparer.Ordinal);

    internal IReadOnlyList<MusicSongMutation> BuildMutations()
    {
        if (changed.Count == 0) return [];
        var list = new List<MusicSongMutation>(changed.Count);
        foreach (var (id, record) in changed)
        {
            ValidateSong(id, record, StorageError.InvalidRequest);
            original.TryGetValue(id, out var prevRecord);
            list.Add(new MusicSongMutation(
                record,
                prevRecord,
                NativeMusicSong.Fingerprint(record),
                MusicSongMatcher.Query(record)));
        }
        return list;
    }

    private MusicSongRecord? Original(string id)
    {
        if (before is not null && before.TryGetValue(id, out var saved)) return saved;
        if (original.TryGetValue(id, out saved)) return saved;
        saved = Store.ReadSong(id);
        if (saved is not null) ValidateSong(id, saved, StorageError.Corrupt);
        if (cacheReads)
        {
            original[id] = saved;
            if (saved is not null) knownSongs?.Add(id);
        }
        return saved;
    }

    internal MusicSongRecord? Read(string id) =>
        changed.TryGetValue(id, out var song) ? song : Original(id);

    internal bool Contains(string id)
    {
        if (changed.ContainsKey(id)) return true;
        if (before is not null && before.TryGetValue(id, out var previous)) return previous is not null;
        if (original.TryGetValue(id, out var song)) return song is not null;
        if (cacheReads && knownSongs?.Contains(id) == true) return true;
        if (existence.TryGetValue(id, out bool found)) return found;
        found = Store.ContainsSong(id);
        if (cacheReads)
        {
            existence[id] = found;
            if (found) knownSongs?.Add(id);
        }
        return found;
    }

    internal void Set(MusicSongRecord song)
    {
        string id = song.SongId;
        var prev = Original(id);
        if (changed.Remove(id, out var previous))
        {
            foreach (var source in previous.Sources) stagedSources.Remove(source.SourceKey);
            if (!string.IsNullOrEmpty(previous.LocalFileId)) stagedFiles.Remove(previous.LocalFileId);
            string fingerprint = NativeMusicSong.Fingerprint(previous);
            if (stagedCandidates.TryGetValue(fingerprint, out var values))
            { values.Remove(id); if (values.Count == 0) stagedCandidates.Remove(fingerprint); }
        }
        if (song.ContentEquals(prev))
        {
            changed.Remove(id);
        }
        else
        {
            changed[id] = song;
            foreach (var source in song.Sources) stagedSources[source.SourceKey] = id;
            if (!string.IsNullOrEmpty(song.LocalFileId)) stagedFiles[song.LocalFileId] = id;
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
                if (pair.Value is { } song && song.Sources.Any(source => source.SourceKey == key))
                    return pair.Key;
        if (knownSources.TryGetValue(key, out var id)) return id;
        id = Store.FindSource(key);
        if (id is not null && before is not null && before.ContainsKey(id)) id = null;
        if (cacheReads) knownSources[key] = id;
        return id;
    }

    internal string? Registered(MusicSongRecord song)
    {
        foreach (var source in song.Sources)
            if (SourceOwner(source.SourceKey) is { } id) return id;
        if (!string.IsNullOrEmpty(song.LocalFileId))
        {
            string file = song.LocalFileId;
            if (stagedFiles.TryGetValue(file, out var staged)) return staged;
            if (before is not null)
                foreach (var pair in before)
                    if (pair.Value?.LocalFileId == file) return pair.Key;
            if (Store.FindLocalFile(file) is { } id && Contains(id)) return id;
        }
        return song.SongId.Length > 0 && Contains(song.SongId) ? song.SongId : null;
    }

    internal string? Find(MusicSongRecord song, IReadOnlySet<string>? allowed = null)
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
            try
            {
                if (Read(id) is not { } candidate || !NativeMusicSong.IsOnline(candidate) ||
                    !NativeMusicSong.ReadIdentity(candidate).Compatible(identity)) continue;
            }
            catch (MusicLibraryException) { continue; }
            catch (StorageException error) when (error.Error == StorageError.Corrupt) { continue; }
            if (match is not null) return null;
            match = id;
        }
        return match;
    }

    internal MusicSongRecord Register(MusicSongRecord normalized, string? existingId, bool authoritative)
    {
        var incoming = new NativeMusicSong.Recordings(normalized);
        if (existingId is null)
        {
            incoming.Id = Guid.NewGuid().ToString("D");
            return incoming.ToSong();
        }
        var previous = Read(existingId) ?? throw new MusicLibraryException("SONG_NOT_FOUND");
        if (previous.ContentEquals(normalized)) return previous;
        var record = new NativeMusicSong.Recordings(previous);
        var eligible = incoming.Sources.Where(source => SourceOwner(source.SourceKey) is not { } owner ||
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
                if (pair.Value?.CoverUrl == address) ids.Add(pair.Key);
        foreach (var pair in changed)
        {
            ids.Remove(pair.Key);
            if (pair.Value.CoverUrl == address) ids.Add(pair.Key);
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

    private static void ValidateSong(string id, MusicSongRecord song, StorageError failure)
    {
        try
        {
            if (!Guid.TryParseExact(id, "D", out var parsed) || id != parsed.ToString("D") ||
                song.SongId != id)
                throw new StorageException(failure);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in song.Sources)
            {
                if (!NativeMusicSong.Platforms.Contains(source.Platform) || source.InfoSource != source.Platform ||
                    NativeMusicSong.InfoId(source).Length == 0 || source.SourceKey != NativeMusicSong.InfoKey(source) ||
                    !keys.Add(source.SourceKey))
                    throw new StorageException(failure);
            }
        }
        catch (MusicLibraryException) { throw new StorageException(failure); }
    }
}
