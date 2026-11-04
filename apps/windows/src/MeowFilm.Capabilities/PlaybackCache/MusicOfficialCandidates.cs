using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Recording matching only: no account authority, media URL, queue mutation or script runtime.</summary>
internal sealed class MusicOfficialCandidates
{
    private readonly NativeMusicSong.Recordings recordings;
    private readonly HashSet<string> candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> remembered = new(StringComparer.Ordinal);
    private readonly string primary;
    internal MusicOfficialCandidates(JsonObject normalized)
    {
        recordings = new(normalized.DeepClone().AsObject());
        primary = NativeMusicSong.SourceKey(normalized);
        foreach (var row in recordings.Sources) Select(row);
    }
    private void Select(JsonObject row)
    {
        string key = LJ.S(row["sourceKey"]);
        if (candidates.Count >= 24 || key.Length == 0 ||
            LJ.S(row["source"]) is not ("wy" or "tx" or "kg") ||
            key != NativeMusicSong.SourceKey(row)) return;
        if (!candidates.Contains(key) && recordings.Sources.Count(value =>
            candidates.Contains(LJ.S(value["sourceKey"])) && LJ.S(value["source"]) == LJ.S(row["source"])) >= 8) return;
        if (recordings.TryAddSource(row)) candidates.Add(key);
    }
    internal void Remember(JsonObject info, string platform)
    {
        if (LJ.S(info["platform"]) != platform || info["recording"] is not JsonObject row ||
            LJ.S(row["source"]) != platform || LJ.S(row["sourceKey"]) != LJ.S(info["sourceKey"])) return;
        Select(row);
        string key = LJ.S(row["sourceKey"]);
        if (candidates.Contains(key)) remembered[key] = LJ.I(info["confirmedAt"]);
    }
    internal void Discover(JsonObject candidate, string platform)
    {
        // The search provider is fixed. Do not import a foreign platform embedded in its response.
        var normalized = NativeMusicSong.Normalize(candidate);
        if (!recordings.CanMerge(new(normalized))) return;
        foreach (var row in (normalized["sources"] as JsonArray ?? []).OfType<JsonObject>())
            if (LJ.S(row["source"]) == platform) Select(row);
    }
    internal bool HasPlatform(string platform) => recordings.Sources.Any(row =>
        candidates.Contains(LJ.S(row["sourceKey"])) && LJ.S(row["source"]) == platform);
    internal JsonObject Snapshot(IEnumerable<string> platforms)
    {
        var enabled = platforms.ToHashSet(StringComparer.Ordinal);
        var ordered = recordings.Sources.Where(row =>
            candidates.Contains(LJ.S(row["sourceKey"])) && enabled.Contains(LJ.S(row["source"])))
            .OrderByDescending(row => remembered.GetValueOrDefault(LJ.S(row["sourceKey"])))
            .ThenBy(row => LJ.S(row["sourceKey"]) == primary ? 0 : 1)
            .ThenBy(row => LJ.S(row["sourceKey"]), StringComparer.Ordinal).ToArray();
        return LJ.Obj(("song", recordings.ToSong()), ("candidates", LJ.Arr(ordered.Select(row =>
            LJ.Obj(("platform", LJ.S(row["source"])), ("sourceKey", LJ.S(row["sourceKey"])))))));
    }
}
