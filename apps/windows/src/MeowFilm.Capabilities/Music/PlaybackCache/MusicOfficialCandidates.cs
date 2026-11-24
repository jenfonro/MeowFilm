using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record MusicOfficialCandidateSlot(string Platform, string SourceKey);
internal sealed record MusicOfficialCandidatePlan(MusicSongRecord Song, MusicOfficialCandidateSlot[] Candidates);

/// <summary>Recording matching only: no account authority, media URL, queue mutation or script runtime.</summary>
internal sealed class MusicOfficialCandidates
{
    private readonly NativeMusicSong.Recordings recordings;
    private readonly HashSet<string> candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> remembered = new(StringComparer.Ordinal);
    private readonly string primary;

    internal MusicOfficialCandidates(MusicSongRecord normalized)
    {
        recordings = new(normalized);
        primary = NativeMusicSong.SourceKey(normalized);
        foreach (var row in recordings.Sources) Select(row);
    }

    private void Select(MusicSongSourceRecord row)
    {
        string key = row.SourceKey;
        if (key.Length == 0 ||
            row.Platform is not ("wy" or "tx" or "kg")) return;
        if (recordings.TryAddSource(row)) candidates.Add(key);
    }

    internal void Remember(PlaybackSourceEntry info, string platform)
    {
        if (info.Platform != platform || info.Recording is not { } rec) return;
        var row = MusicSourceCache.ToSongSourceRecord(rec);
        if (row.Platform != platform || row.SourceKey != info.SourceKey) return;
        Select(row);
        if (candidates.Contains(row.SourceKey)) remembered[row.SourceKey] = info.At;
    }

    internal MusicOfficialCandidatePlan Snapshot(IEnumerable<string> platforms)
    {
        var enabled = platforms.ToHashSet(StringComparer.Ordinal);
        var ordered = recordings.Sources.Where(row =>
            candidates.Contains(row.SourceKey) && enabled.Contains(row.Platform))
            .OrderByDescending(row => remembered.GetValueOrDefault(row.SourceKey))
            .ThenBy(row => row.SourceKey == primary ? 0 : 1)
            .ThenBy(row => row.SourceKey, StringComparer.Ordinal)
            .Select(row => new MusicOfficialCandidateSlot(row.Platform, row.SourceKey))
            .ToArray();
        return new MusicOfficialCandidatePlan(recordings.ToSong(), ordered);
    }
}
