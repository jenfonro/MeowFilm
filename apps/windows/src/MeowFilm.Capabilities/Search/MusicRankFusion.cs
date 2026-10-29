// Native translation of iWebPlus b900c259 static/rank-fusion.js and song-identity merge.
// Same Music module's pure identity helpers only; no Library owner or persistence.
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

internal sealed record SearchCandidate(string Platform, string Key, JsonObject Item, double Rank);

internal static class MusicRankFusion
{
    private sealed record Evidence(string Key, string Title, string Artist, string Version, string Fingerprint, double Duration)
    {
        internal static Evidence Read(JsonObject song)
        {
            var d = NativeMusicSong.Descriptor(song);
            return new(NativeMusicSong.SourceKey(song), LJ.S(d["title"]), LJ.S(d["artist"]), LJ.S(d["version"]),
                NativeMusicSong.Fingerprint(song), LJ.N(d["duration"]));
        }
        private static bool Known(string text) => text.Length > 0 &&
            !text.StartsWith("unknown", StringComparison.Ordinal) && !text.StartsWith("未知", StringComparison.Ordinal);
        internal bool Compatible(Evidence other)
        {
            bool durations = !(Duration > 0 && other.Duration > 0) ||
                Math.Abs(Duration - other.Duration) <= Math.Max(3, Math.Min(Duration, other.Duration) * .02);
            if (Key.Length > 0 && Key == other.Key)
                return (!Known(Artist) || !Known(other.Artist) || Artist == other.Artist) &&
                    (!Known(Title) || !Known(other.Title) || Version == other.Version) && durations;
            return Fingerprint.Length > 0 && Fingerprint == other.Fingerprint &&
                Duration > 0 && other.Duration > 0 && durations;
        }
    }

    private sealed class Row(SearchCandidate candidate, MusicSearchKind kind)
    {
        internal readonly SearchCandidate Candidate = candidate;
        internal readonly string Id = kind == MusicSearchKind.Songs ? LJ.S(candidate.Item["songId"]) :
            LJ.Stringify(new JsonArray(candidate.Platform, LJ.Scalar(candidate.Item["id"])));
        internal readonly Evidence? Identity = kind == MusicSearchKind.Songs ? Evidence.Read(candidate.Item) : null;
        internal readonly string Representation = LJ.Stringify(new JsonArray(candidate.Item.DeepClone(), candidate.Rank));
    }
    private sealed record Ranked(JsonObject Item, double Score, string Tie);

    private sealed class Group(string key, Row row)
    {
        internal readonly string Key = key;
        internal readonly JsonObject Item = (JsonObject)row.Candidate.Item.DeepClone();
        internal readonly List<Evidence> Evidence = [];
        internal readonly Dictionary<string, JsonObject> Sources = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, double> Ranks = new(StringComparer.Ordinal);
    }

    internal static JsonArray Fuse(IReadOnlyList<SearchCandidate> candidates, MusicSearchKind kind, CancellationToken token)
    {
        var rows = new List<Row>(candidates.Count);
        foreach (var candidate in candidates) { token.ThrowIfCancellationRequested(); rows.Add(new(candidate, kind)); }
        // Hash is the reference's two unsigned FNV accumulators, not process-random GetHashCode.
        var representatives = rows.OrderBy(r => NativeMusicSong.Hash(r.Representation), StringComparer.Ordinal)
            .ThenBy(r => r.Representation, StringComparer.Ordinal);
        var ordered = kind == MusicSearchKind.Songs
            ? representatives.OrderBy(r => r.Id + ":" + r.Candidate.Key, StringComparer.Ordinal)
            : representatives.AsEnumerable();
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var row in ordered)
        {
            token.ThrowIfCancellationRequested();
            string key = row.Id;
            groups.TryGetValue(key, out var group);
            if (kind == MusicSearchKind.Songs && group is not null &&
                group.Evidence.Any(e => !e.Compatible(row.Identity!)))
            {
                key += ":" + NativeMusicSong.Hash(LJ.Stringify(new JsonArray(
                    row.Candidate.Key, NativeMusicSong.Descriptor(row.Candidate.Item))));
                groups.TryGetValue(key, out group);
                // Never collapse a hash collision or an incompatible same-native-id response.
                LJ.Require(group is null || group.Evidence.All(e => e.Compatible(row.Identity!)), "IDENTITY_MISMATCH");
            }
            if (group is null)
            {
                group = new(key, row);
                if (kind == MusicSearchKind.Songs) group.Item["songId"] = key;
                groups.Add(key, group);
            }
            if (row.Identity is { } identity)
            {
                group.Evidence.Add(identity);
                foreach (var source in (row.Candidate.Item["sources"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    token.ThrowIfCancellationRequested();
                    var evidence = Evidence.Read(source);
                    LJ.Require(identity.Compatible(evidence), "IDENTITY_MISMATCH");
                    group.Evidence.Add(evidence);
                    group.Sources[LJ.S(source["sourceKey"])] = source;
                }
            }
            if (!group.Ranks.TryGetValue(row.Candidate.Platform, out var rank) || row.Candidate.Rank < rank)
                group.Ranks[row.Candidate.Platform] = row.Candidate.Rank;
        }
        var result = new List<Ranked>(groups.Count);
        foreach (var group in groups.Values)
        {
            token.ThrowIfCancellationRequested();
            var platforms = group.Ranks.Keys.Order(StringComparer.Ordinal).ToArray();
            if (kind == MusicSearchKind.Songs)
                group.Item["sources"] = LJ.Arr(group.Sources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value));
            result.Add(new(group.Item, platforms.Sum(p => 1 / (5 + group.Ranks[p])),
                NativeMusicSong.Hash(group.Key)));
        }
        return LJ.Arr(result.OrderByDescending(r => r.Score).ThenBy(r => r.Tie, StringComparer.Ordinal)
            .ThenBy(r => kind == MusicSearchKind.Songs ? LJ.S(r.Item["songId"]) :
                LJ.S(r.Item["source"]) + ":" + LJ.Scalar(r.Item["id"]), StringComparer.Ordinal)
            .Select(r => r.Item));
    }
}
