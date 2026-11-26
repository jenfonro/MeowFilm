namespace MeowFilm.Capabilities.Library;

public enum MusicSourceExclusionTier
{
    ProviderPlatform,
    Recording
}

public sealed record MusicSourceExclusionListResult(
    long Revision,
    IReadOnlyList<MusicSourceExclusionRecord> Items);

public sealed record MusicSourceExclusionChangeResult(
    long Revision,
    bool Changed);

internal static class MusicSourceExclusionsOwner
{
    internal const string OfficialProvider = "official";
    internal const string RecordingProviderPrefix = "recording:";

    internal static bool IsRecordingProvider(string value) =>
        value.StartsWith(RecordingProviderPrefix, StringComparison.Ordinal) &&
        value.Length > RecordingProviderPrefix.Length;

    internal static string MakeRecordingProvider(string sourceKey) =>
        RecordingProviderPrefix + sourceKey;

    internal static string? TryExtractRecordingSourceKey(string provider) =>
        IsRecordingProvider(provider) ? provider[RecordingProviderPrefix.Length..] : null;

    internal static MusicSourceExclusionTier TierOf(MusicSourceExclusionRecord record) =>
        IsRecordingProvider(record.Provider) ? MusicSourceExclusionTier.Recording : MusicSourceExclusionTier.ProviderPlatform;

    internal static MusicSourceExclusionListResult List(
        MusicLibrarySession doc,
        string? songId = null,
        MusicSourceExclusionTier? tier = null)
    {
        long savedRev = doc.ReadSourceExclusionsRevision() ?? 0;
        var rows = doc.ReadSourceExclusions();
        var selected = new List<MusicSourceExclusionRecord>(rows.Count);
        foreach (var item in rows)
        {
            if (songId is not null && item.SongId != songId) continue;
            if (tier is { } filter && TierOf(item) != filter) continue;
            selected.Add(item);
        }
        return new MusicSourceExclusionListResult(savedRev, selected);
    }

    internal static (MusicSourceExclusionChangeResult Result, bool Mutate) Mutate(
        MusicLibrarySession doc,
        bool add,
        string songId,
        string provider,
        string platform,
        string providerName = "",
        string title = "",
        string artist = "")
    {
        MusicRules.Require(
            MusicRules.IsValidId(songId, null) &&
            provider.Length > 0 &&
            NativeMusicSong.Platforms.Contains(platform),
            "INVALID_REQUEST");
        long savedRev = doc.ReadSourceExclusionsRevision() ?? 0;
        var rows = doc.ReadSourceExclusions();
        var existing = rows.FirstOrDefault(row => row.SongId == songId && row.Provider == provider && row.Platform == platform);

        if (add == (existing is not null))
            return (new MusicSourceExclusionChangeResult(savedRev, false), false);

        if (add && TryExtractRecordingSourceKey(provider) is { } recordingKey &&
            doc.Songs.Read(songId) is { } song)
        {
            var excludedRecordings = rows
                .Where(row => row.SongId == songId)
                .Select(row => TryExtractRecordingSourceKey(row.Provider))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            bool hasOtherUnexcluded = song.Sources.Any(src =>
                src.SourceKey != recordingKey && !excludedRecordings.Contains(src.SourceKey));
            MusicRules.Require(hasOtherUnexcluded, "INVALID_REQUEST");
        }

        int nextOrdinal = existing?.Ordinal ?? (rows.Count == 0 ? 0 : rows.Max(r => r.Ordinal) + 1);
        string exclusionId = existing?.ExclusionId ?? Guid.NewGuid().ToString("D");

        MusicSourceExclusionRecord? next = add
            ? new MusicSourceExclusionRecord(
                exclusionId,
                nextOrdinal,
                songId,
                provider,
                platform,
                providerName,
                title,
                artist,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : null;

        long nextRev = checked(savedRev + 1);
        doc.SetSourceExclusionsRevision(nextRev);
        doc.StageSourceExclusion(new MusicSourceExclusionMutation(exclusionId, next, nextOrdinal));
        return (new MusicSourceExclusionChangeResult(nextRev, true), true);
    }

    internal static (HashSet<(string SongId, string Provider, string Platform)> ProviderPlatforms,
        HashSet<(string SongId, string SourceKey)> RecordingKeys) ReadSyncSnapshot(MusicLibrarySession doc)
    {
        var providerPlatforms = new HashSet<(string SongId, string Provider, string Platform)>();
        var recordingKeys = new HashSet<(string SongId, string SourceKey)>();
        foreach (var row in doc.ReadSourceExclusions())
        {
            if (TryExtractRecordingSourceKey(row.Provider) is { } sourceKey)
                recordingKeys.Add((row.SongId, sourceKey));
            else
                providerPlatforms.Add((row.SongId, row.Provider, row.Platform));
        }
        return (providerPlatforms, recordingKeys);
    }
}
