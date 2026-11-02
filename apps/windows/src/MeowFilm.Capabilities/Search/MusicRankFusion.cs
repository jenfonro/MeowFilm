// Native translation of iWebPlus b900c259 static/rank-fusion.js and song-identity merge.
// Same Music module's pure identity helpers only; no Library owner or persistence.
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

internal sealed record SearchCandidate(string Platform, string Key, JsonObject Item, double Rank);

internal static class MusicRankFusion
{
    private sealed class Row(SearchCandidate candidate, MusicSearchKind kind, CancellationToken token)
    {
        internal readonly SearchCandidate Candidate = candidate;
        internal readonly string Id = kind == MusicSearchKind.Songs ? LJ.S(candidate.Item["songId"]) :
            PlaylistKey(candidate.Item);
        internal readonly NativeMusicSong.Recordings? Song = kind == MusicSearchKind.Songs ? new(candidate.Item, token) : null;
        internal readonly string Representation = LJ.Stringify(new JsonArray(candidate.Item.DeepClone(), candidate.Rank));
    }
    private sealed record Ranked(Group Group, double Score, string Tie, int Priority);

    private sealed class Group(string key, string platform, JsonObject item, NativeMusicSong.Recordings? song, bool retained)
    {
        internal readonly string Key = key;
        internal readonly string Platform = platform;
        internal readonly bool Retained = retained;
        internal readonly Dictionary<string, double> Ranks = new(StringComparer.Ordinal);
        internal JsonObject ToItem(CancellationToken token) => song?.ToSong(token) ?? (JsonObject)item.DeepClone();
    }
    private static string PlaylistKey(JsonObject item) =>
        LJ.Stringify(new JsonArray(LJ.S(item["source"]), LJ.Scalar(item["id"])));

    /// <summary>
    /// Equal-weight reciprocal rank fusion over the given candidates. <paramref name="order"/> is the
    /// saved platform priority: it picks each group's representative (display metadata and primary
    /// recording) and breaks score ties. Retained rows keep position, identity and representation.
    /// Both initial and later rounds group their raw candidates through the same song identity owner.
    /// </summary>
    internal static JsonArray Fuse(IReadOnlyList<SearchCandidate> candidates, MusicSearchKind kind,
        IReadOnlyList<string> order, CancellationToken token, JsonArray? retained = null)
    {
        int Priority(string platform)
        {
            for (int index = 0; index < order.Count; index++) if (order[index] == platform) return index;
            return order.Count;
        }
        var rows = new List<Row>(candidates.Count);
        foreach (var candidate in candidates) { token.ThrowIfCancellationRequested(); rows.Add(new(candidate, kind, token)); }
        // Hash is the reference's two unsigned FNV accumulators, not process-random GetHashCode.
        var ordered = rows.OrderBy(r => Priority(r.Candidate.Platform)).ThenBy(r => r.Candidate.Rank)
            .ThenBy(r => r.Id + ":" + r.Candidate.Key, StringComparer.Ordinal)
            .ThenBy(r => NativeMusicSong.Hash(r.Representation), StringComparer.Ordinal)
            .ThenBy(r => r.Representation, StringComparer.Ordinal);
        var identities = new NativeMusicSongGroups(token);
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var pinned = new List<Group>();
        foreach (var item in (retained ?? []).OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            var song = kind == MusicSearchKind.Songs ? identities.Import(item) : null;
            string key = song?.Id ?? PlaylistKey(item);
            string platform = song is null ? LJ.S(item["source"]) : LJ.S(NativeMusicSong.SourceData(item)["source"]);
            var group = new Group(key, platform, item, song, true);
            LJ.Require(groups.TryAdd(key, group), "IDENTITY_MISMATCH");
            pinned.Add(group);
        }
        foreach (var row in ordered)
        {
            token.ThrowIfCancellationRequested();
            var song = row.Song is null ? null : identities.Add(row.Song);
            string key = song?.Id ?? row.Id;
            if (!groups.TryGetValue(key, out var group))
            {
                // The first row is the highest-priority platform's best-ranked candidate.
                group = new(key, row.Candidate.Platform, row.Candidate.Item, song, false);
                groups.Add(key, group);
            }
            if (group.Retained) continue;
            if (!group.Ranks.TryGetValue(row.Candidate.Platform, out var rank) || row.Candidate.Rank < rank)
                group.Ranks[row.Candidate.Platform] = row.Candidate.Rank;
        }
        var result = new List<Ranked>(groups.Count - pinned.Count);
        foreach (var group in groups.Values.Where(group => !group.Retained))
        {
            token.ThrowIfCancellationRequested();
            var platforms = group.Ranks.Keys.Order(StringComparer.Ordinal).ToArray();
            result.Add(new(group, platforms.Sum(p => 1 / (5 + group.Ranks[p])),
                NativeMusicSong.Hash(group.Key), Priority(group.Platform)));
        }
        return LJ.Arr(pinned.Concat(result.OrderByDescending(r => r.Score).ThenBy(r => r.Priority)
            .ThenBy(r => r.Tie, StringComparer.Ordinal).ThenBy(r => r.Group.Key, StringComparer.Ordinal)
            .Select(r => r.Group)).Select(group => group.ToItem(token)));
    }
}
