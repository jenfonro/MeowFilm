using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Recording matching only: no account authority, media URL, queue mutation or script runtime.</summary>
internal sealed class MusicOfficialCandidates
{
    private readonly JsonObject song;
    private readonly Dictionary<string, JsonObject> recordings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> remembered = new(StringComparer.Ordinal);
    private readonly HashSet<string> quarantined;
    private readonly string primary;
    internal MusicOfficialCandidates(JsonObject normalized)
    {
        song = normalized.DeepClone().AsObject();
        primary = NativeMusicSong.SourceKey(song);
        quarantined = (song["quarantinedSources"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(row => LJ.S(row["sourceKey"])).ToHashSet(StringComparer.Ordinal);
        foreach (var row in (song["sources"] as JsonArray ?? []).OfType<JsonObject>()) Add(row);
    }
    private void Add(JsonObject row)
    {
        string key = LJ.S(row["sourceKey"]);
        if (recordings.Count >= 24 || key.Length == 0 || quarantined.Contains(key) ||
            LJ.S(row["source"]) is not ("wy" or "tx" or "kg") ||
            key != NativeMusicSong.SourceKey(row) || !NativeMusicSong.Compatible(song, row)) return;
        if (!recordings.ContainsKey(key) && recordings.Values.Count(value =>
            LJ.S(value["source"]) == LJ.S(row["source"])) >= 8) return;
        recordings.TryAdd(key, row.DeepClone().AsObject());
    }
    internal void Remember(JsonObject info, string platform)
    {
        if (LJ.S(info["platform"]) != platform || info["recording"] is not JsonObject row ||
            LJ.S(row["source"]) != platform || LJ.S(row["sourceKey"]) != LJ.S(info["sourceKey"])) return;
        Add(row);
        string key = LJ.S(row["sourceKey"]);
        if (recordings.ContainsKey(key)) remembered[key] = LJ.I(info["confirmedAt"]);
    }
    internal void Discover(JsonObject candidate, string platform)
    {
        // The search provider is fixed. Do not import a foreign platform embedded in its response.
        var normalized = NativeMusicSong.Normalize(candidate);
        if (!NativeMusicSong.Compatible(song, normalized)) return;
        foreach (var row in (normalized["sources"] as JsonArray ?? []).OfType<JsonObject>())
            if (LJ.S(row["source"]) == platform) Add(row);
    }
    internal bool HasPlatform(string platform) => recordings.Values.Any(row => LJ.S(row["source"]) == platform);
    internal JsonObject Snapshot(IEnumerable<string> platforms)
    {
        var enabled = platforms.ToHashSet(StringComparer.Ordinal);
        var ordered = recordings.Values.Where(row => enabled.Contains(LJ.S(row["source"])))
            .OrderByDescending(row => remembered.GetValueOrDefault(LJ.S(row["sourceKey"])))
            .ThenBy(row => LJ.S(row["sourceKey"]) == primary ? 0 : 1)
            .ThenBy(row => LJ.S(row["sourceKey"]), StringComparer.Ordinal).ToArray();
        var decorated = song.DeepClone().AsObject();
        // Preserve non-official mappings, stable song identity and the original queue occurrence.
        var sources = (decorated["sources"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(row => LJ.S(row["source"]) is not ("wy" or "tx" or "kg")).Select(row => row.DeepClone()).ToList();
        sources.AddRange(recordings.Values.Select(row => row.DeepClone()));
        decorated["sources"] = new JsonArray(sources.ToArray());
        return LJ.Obj(("song", NativeMusicSong.Normalize(decorated)), ("candidates", LJ.Arr(ordered.Select(row =>
            LJ.Obj(("platform", LJ.S(row["source"])), ("sourceKey", LJ.S(row["sourceKey"])))))));
    }
}
