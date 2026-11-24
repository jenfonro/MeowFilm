// Native reciprocal-rank projection of songs already adopted by the unique repository.
using System.Globalization;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Search;

internal sealed record SearchCandidate(
    string Platform,
    string Key,
    MusicSongRecord? Song,
    MusicSearchPlaylistRecord? Playlist,
    double Rank);

internal static class MusicRankFusion
{
    private sealed class Row<T>(SearchCandidate candidate, string id, T item, string representation)
    {
        internal readonly SearchCandidate Candidate = candidate;
        internal readonly string Id = id;
        internal readonly T Item = item;
        internal readonly string Representation = representation;
    }

    private sealed record Ranked<T>(Group<T> Group, double Score, string Tie, int Priority);

    private sealed class Group<T>(string key, string platform, T item, bool retained)
    {
        internal readonly string Key = key;
        internal readonly string Platform = platform;
        internal readonly bool Retained = retained;
        internal readonly Dictionary<string, double> Ranks = new(StringComparer.Ordinal);
        internal T Item { get; } = item;
    }

    private static string PlaylistKey(MusicSearchPlaylistRecord item) =>
        MusicRules.CanonicalArray(item.Source, item.Id);

    private static string SongPlatform(MusicSongRecord item) =>
        item.Sources.Length > 0 ? item.Sources[0].Platform : "";

    /// <summary>
    /// Equal-weight reciprocal rank fusion over song candidates.
    /// Repository metadata and permanent identities are not rewritten by ranking.
    /// Retained rows keep position and identity.
    /// </summary>
    internal static MusicSongRecord[] FuseSongs(
        IReadOnlyList<SearchCandidate> candidates,
        IReadOnlyList<string> order,
        CancellationToken token,
        IReadOnlyList<MusicSongRecord>? retained = null) =>
        FuseCore(
            candidates,
            order,
            token,
            retained,
            extract: c => c.Song!,
            getId: song => song.SongId,
            getPlatform: SongPlatform,
            getRepresentation: (c, song) => MusicRules.CanonicalArray(
                song.SongId, song.Title, song.Artist, song.Album, c.Key, c.Rank.ToString("R", CultureInfo.InvariantCulture)),
            getTieInput: (song, key) => song.Sources.Length > 0
                ? string.Join("\n", song.Sources.Select(s => s.SourceKey).Order(StringComparer.Ordinal))
                : key);

    internal static MusicSearchPlaylistRecord[] FusePlaylists(
        IReadOnlyList<SearchCandidate> candidates,
        IReadOnlyList<string> order,
        CancellationToken token,
        IReadOnlyList<MusicSearchPlaylistRecord>? retained = null) =>
        FuseCore(
            candidates,
            order,
            token,
            retained,
            extract: c => c.Playlist!,
            getId: PlaylistKey,
            getPlatform: item => item.Source,
            getRepresentation: (c, pl) => MusicRules.CanonicalArray(
                pl.Source, pl.Id, pl.Name, pl.Creator, c.Key, c.Rank.ToString("R", CultureInfo.InvariantCulture)),
            getTieInput: (_, key) => key);

    private static T[] FuseCore<T>(
        IReadOnlyList<SearchCandidate> candidates,
        IReadOnlyList<string> order,
        CancellationToken token,
        IReadOnlyList<T>? retained,
        Func<SearchCandidate, T> extract,
        Func<T, string> getId,
        Func<T, string> getPlatform,
        Func<SearchCandidate, T, string> getRepresentation,
        Func<T, string, string> getTieInput)
    {
        int Priority(string platform)
        {
            for (int index = 0; index < order.Count; index++) if (order[index] == platform) return index;
            return order.Count;
        }
        var rows = new List<Row<T>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var item = extract(candidate);
            rows.Add(new(candidate, getId(item), item, getRepresentation(candidate, item)));
        }
        // Hash is the reference's two unsigned FNV accumulators, not process-random GetHashCode.
        var ordered = rows.OrderBy(r => Priority(r.Candidate.Platform)).ThenBy(r => r.Candidate.Rank)
            .ThenBy(r => r.Id + ":" + r.Candidate.Key, StringComparer.Ordinal)
            .ThenBy(r => NativeMusicSong.Hash(r.Representation), StringComparer.Ordinal)
            .ThenBy(r => r.Representation, StringComparer.Ordinal);
        var groups = new Dictionary<string, Group<T>>(StringComparer.Ordinal);
        var pinned = new List<Group<T>>();
        if (retained is not null)
        {
            foreach (var item in retained)
            {
                token.ThrowIfCancellationRequested();
                string key = getId(item);
                string platform = getPlatform(item);
                var group = new Group<T>(key, platform, item, true);
                MusicRules.Require(groups.TryAdd(key, group), "IDENTITY_MISMATCH");
                pinned.Add(group);
            }
        }
        foreach (var row in ordered)
        {
            token.ThrowIfCancellationRequested();
            string key = row.Id;
            if (!groups.TryGetValue(key, out var group))
            {
                // The first row supplies ranking priority, not a new metadata owner.
                group = new(key, row.Candidate.Platform, row.Item, false);
                groups.Add(key, group);
            }
            if (group.Retained) continue;
            if (!group.Ranks.TryGetValue(row.Candidate.Platform, out var rank) || row.Candidate.Rank < rank)
                group.Ranks[row.Candidate.Platform] = row.Candidate.Rank;
        }
        var result = new List<Ranked<T>>(groups.Count - pinned.Count);
        foreach (var group in groups.Values.Where(group => !group.Retained))
        {
            token.ThrowIfCancellationRequested();
            var platforms = group.Ranks.Keys.Order(StringComparer.Ordinal).ToArray();
            string tieInput = getTieInput(group.Item, group.Key);
            result.Add(new(group, platforms.Sum(p => 1 / (5 + group.Ranks[p])),
                NativeMusicSong.Hash(tieInput), Priority(group.Platform)));
        }
        return pinned.Concat(result.OrderByDescending(r => r.Score).ThenBy(r => r.Priority)
            .ThenBy(r => r.Tie, StringComparer.Ordinal).ThenBy(r => r.Group.Key, StringComparer.Ordinal)
            .Select(r => r.Group)).Select(group => { token.ThrowIfCancellationRequested(); return group.Item; }).ToArray();
    }
}
