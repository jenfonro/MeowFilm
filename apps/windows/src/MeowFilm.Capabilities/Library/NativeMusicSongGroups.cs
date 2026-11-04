using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>
/// Operation-local identity index shared by search, library upserts and played history.
/// Import preserves established groups; Add selects a compatible group or creates a qualified
/// recording group. Presentation order and incoming/display priority remain the caller's policy.
/// </summary>
internal sealed class NativeMusicSongGroups(CancellationToken token = default)
{
    private sealed class Entry(NativeMusicSong.Recordings value, int position)
    {
        internal readonly NativeMusicSong.Recordings Value = value;
        internal readonly int Position = position;
        internal HashSet<string> Keys = [], Fingerprints = [];
    }
    private readonly Dictionary<string, Entry> ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> keys = new(StringComparer.Ordinal), fingerprints = new(StringComparer.Ordinal);

    internal NativeMusicSong.Recordings Import(JsonObject song) => Insert(new(song, token)).Value;
    internal NativeMusicSong.Recordings? NativeOwner(string key) =>
        keys.TryGetValue(key, out var matches) ? matches.MinBy(entry => entry.Position)!.Value : null;
    internal NativeMusicSong.Recordings? Find(NativeMusicSong.Recordings candidate) => FindEntry(candidate)?.Value;
    internal NativeMusicSong.Recordings Add(NativeMusicSong.Recordings candidate, bool preferIncoming = false)
    {
        var entry = FindEntry(candidate, preferIncoming);
        if (entry is not null)
        {
            Index(entry);
            return entry.Value;
        }
        if (ids.ContainsKey(candidate.Id)) candidate.Id = candidate.QualifiedId();
        return Insert(candidate).Value;
    }
    private Entry Insert(NativeMusicSong.Recordings value)
    {
        token.ThrowIfCancellationRequested();
        var entry = new Entry(value, ids.Count);
        LJ.Require(ids.TryAdd(value.Id, entry), "SONG_ID_CONFLICT");
        Index(entry);
        return entry;
    }
    private Entry? FindEntry(NativeMusicSong.Recordings candidate, bool? preferIncoming = null)
    {
        token.ThrowIfCancellationRequested();
        var visited = new HashSet<Entry>();
        bool Accept(Entry entry) => visited.Add(entry) &&
            (!(entry.Value.IsLocal || candidate.IsLocal) || entry.Value.Id == candidate.Id) &&
            (preferIncoming is { } prefer
            ? entry.Value.TryMerge(candidate, prefer, token) : entry.Value.CanMerge(candidate, token));
        // An exact recording binding wins over an ambiguous, non-transitive duration match.
        var native = candidate.Sources.Select(source => LJ.S(source["sourceKey"]))
            .SelectMany(key => keys.GetValueOrDefault(key) ?? []);
        foreach (var entry in native.Distinct().OrderBy(entry => entry.Position))
        {
            token.ThrowIfCancellationRequested();
            if (Accept(entry)) return entry;
        }
        if (ids.TryGetValue(candidate.Id, out var same) && Accept(same)) return same;
        var described = candidate.Evidence.Select(evidence => evidence.Fingerprint).Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal).SelectMany(key => fingerprints.GetValueOrDefault(key) ?? []);
        foreach (var entry in described.Distinct().OrderBy(entry => entry.Position))
        {
            token.ThrowIfCancellationRequested();
            if (Accept(entry)) return entry;
        }
        return null;
    }
    private void Index(Entry entry)
    {
        var currentKeys = entry.Value.Sources.Select(source => LJ.S(source["sourceKey"])).ToHashSet(StringComparer.Ordinal);
        var currentFingerprints = entry.Value.Evidence.Select(evidence => evidence.Fingerprint)
            .Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        Replace(keys, entry.Keys, currentKeys, entry);
        Replace(fingerprints, entry.Fingerprints, currentFingerprints, entry);
        entry.Keys = currentKeys; entry.Fingerprints = currentFingerprints;
    }
    private void Replace(Dictionary<string, List<Entry>> index, HashSet<string> before, HashSet<string> after, Entry entry)
    {
        foreach (string key in before.Except(after))
        {
            token.ThrowIfCancellationRequested();
            var matches = index[key]; matches.Remove(entry);
            if (matches.Count == 0) index.Remove(key);
        }
        foreach (string key in after.Except(before))
        {
            token.ThrowIfCancellationRequested();
            if (!index.TryGetValue(key, out var matches)) index[key] = matches = [];
            matches.Add(entry);
        }
    }
}
