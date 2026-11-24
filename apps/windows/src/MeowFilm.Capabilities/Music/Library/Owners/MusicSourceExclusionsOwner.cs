using System.Text.RegularExpressions;

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

/// <summary>
/// Unified 2-tier reversible source exclusion rules matching iOS MusicNativeSourceExclusions:
///   Tier 1: Provider × Platform (official or script installation UUID on a platform across all qualities)
///   Tier 2: Per-recording sourceKey (stored durably with "recording:" provider prefix; reversible via restore,
///           never lost on cache clear, never excluding primary recording when sole source).
/// </summary>
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

    internal static bool IsProvider(string value) =>
        value == OfficialProvider ||
        IsRecordingProvider(value) ||
        Regex.IsMatch(value, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout);

    internal static void ValidateIdentity(string songId, string provider, string platform, string code = "CORRUPT_STORAGE")
    {
        MusicRules.Require(
            MusicRules.IsValidId(songId, null) &&
            IsProvider(provider) &&
            NativeMusicSong.Platforms.Contains(platform),
            code);
        if (IsRecordingProvider(provider))
        {
            string sourceKey = provider[RecordingProviderPrefix.Length..];
            MusicRules.Require(MusicRules.IsValidId(sourceKey, null), code);
        }
    }

    private static bool IsValidTitle(string value) =>
        value.Length > 0 && !value.Any(c => c < 32 || c == 127);

    internal static void ValidateRecord(MusicSourceExclusionRecord item, string code = "CORRUPT_STORAGE")
    {
        ValidateIdentity(item.SongId, item.Provider, item.Platform, code);
        MusicRules.Require(MusicRules.IsValidId(item.ProviderName, null) && IsValidTitle(item.Title), code);
        MusicRules.Require(item.ExcludedAtMs is >= 0 and <= MusicRules.MaxInteger, code);
    }

    internal static string ExclusionKey(string songId, string provider, string platform) =>
        MusicRules.CanonicalArray(songId, provider, platform);

    internal static MusicSourceExclusionListResult List(
        MusicLibrarySession doc,
        string? songId = null,
        MusicSourceExclusionTier? tier = null)
    {
        long savedRev = doc.ReadSourceExclusionsRevision() ?? 0;
        MusicRules.Require(savedRev is >= 0 and <= MusicRules.MaxInteger, "CORRUPT_STORAGE");
        var rows = doc.ReadSourceExclusions();
        var selected = new List<MusicSourceExclusionRecord>(rows.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            if (songId is not null && item.SongId != songId) continue;
            if (tier is { } filter && TierOf(item) != filter) continue;
            ValidateIdentity(item.SongId, item.Provider, item.Platform);
            if (songId is null) ValidateRecord(item);
            MusicRules.Require(identities.Add(ExclusionKey(item.SongId, item.Provider, item.Platform)), "CORRUPT_STORAGE");
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
        long savedRev = doc.ReadSourceExclusionsRevision() ?? 0;
        MusicRules.Require(savedRev is >= 0 and <= MusicRules.MaxInteger, "CORRUPT_STORAGE");
        ValidateIdentity(songId, provider, platform, "INVALID_REQUEST");
        string key = ExclusionKey(songId, provider, platform);
        var rows = doc.ReadSourceExclusions();
        var matches = rows.Where(row =>
        {
            if (row.SongId != songId) return false;
            ValidateIdentity(row.SongId, row.Provider, row.Platform);
            return ExclusionKey(row.SongId, row.Provider, row.Platform) == key;
        }).ToArray();
        MusicRules.Require(matches.Length <= 1, "CORRUPT_STORAGE");

        if (add == (matches.Length != 0))
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

        int nextOrdinal = matches.Length > 0 ? matches[0].Ordinal : (rows.Count == 0 ? 0 : rows.Max(r => r.Ordinal) + 1);
        string exclusionId = matches.Length > 0 ? matches[0].ExclusionId : Guid.NewGuid().ToString("D");

        MusicSourceExclusionRecord? next = null;
        if (add)
        {
            next = new MusicSourceExclusionRecord(
                exclusionId,
                nextOrdinal,
                songId,
                provider,
                platform,
                providerName,
                title,
                artist,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ValidateRecord(next, "INVALID_REQUEST");
        }

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
