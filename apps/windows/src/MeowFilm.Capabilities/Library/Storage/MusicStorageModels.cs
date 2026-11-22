using System.Text;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

public sealed record MusicSongSourceRecord(
    string SourceKey,
    string Platform,
    string InfoSource,
    string? SongMid,
    string? Mid,
    string? NativeId,
    string? Hash,
    string? Name,
    string? Singer,
    string? AlbumName,
    double? Duration,
    string? Img,
    bool? QualityKnown,
    string[] ArtistNames,
    StorageValue? WireExtras)
{
    internal bool ContentEquals(MusicSongSourceRecord? other)
    {
        if (other is null) return false;
        return SourceKey == other.SourceKey &&
            Platform == other.Platform &&
            InfoSource == other.InfoSource &&
            SongMid == other.SongMid &&
            Mid == other.Mid &&
            NativeId == other.NativeId &&
            Hash == other.Hash &&
            Name == other.Name &&
            Singer == other.Singer &&
            AlbumName == other.AlbumName &&
            NullableDoubleEqual(Duration, other.Duration) &&
            Img == other.Img &&
            QualityKnown == other.QualityKnown &&
            ArtistNames.SequenceEqual(other.ArtistNames, StringComparer.Ordinal) &&
            StorageValue.StructuralEquals(WireExtras, other.WireExtras);
    }

    internal bool ScalarEquals(MusicSongSourceRecord other) =>
        Platform == other.Platform &&
        InfoSource == other.InfoSource &&
        SongMid == other.SongMid &&
        Mid == other.Mid &&
        NativeId == other.NativeId &&
        Hash == other.Hash &&
        Name == other.Name &&
        Singer == other.Singer &&
        AlbumName == other.AlbumName &&
        NullableDoubleEqual(Duration, other.Duration) &&
        Img == other.Img &&
        QualityKnown == other.QualityKnown;

    internal int EstimateBytes()
    {
        int bytes = 128 + Utf8Len(SourceKey) + Utf8Len(Platform) + Utf8Len(InfoSource) +
            Utf8Len(SongMid) + Utf8Len(Mid) + Utf8Len(NativeId) + Utf8Len(Hash) +
            Utf8Len(Name) + Utf8Len(Singer) + Utf8Len(AlbumName) + Utf8Len(Img);
        foreach (string a in ArtistNames) bytes += 8 + Utf8Len(a);
        if (WireExtras is not null) bytes += EstimateStorageValueBytes(WireExtras);
        return bytes;
    }
}

public sealed record MusicSongRecord(
    string SongId,
    string Title,
    string Artist,
    string Album,
    double? Duration,
    double? BaselineDuration,
    string CoverUrl,
    string? LocalFileId,
    string? ArtworkSourceKey,
    string? ArtworkSourceImage,
    string[] ArtistNames,
    MusicSongSourceRecord[] Sources)
{
    internal bool ContentEquals(MusicSongRecord? other)
    {
        if (other is null || !ScalarEquals(other)) return false;
        if (!ArtistNames.SequenceEqual(other.ArtistNames, StringComparer.Ordinal)) return false;
        if (Sources.Length != other.Sources.Length) return false;
        for (int i = 0; i < Sources.Length; i++)
            if (!Sources[i].ContentEquals(other.Sources[i]))
                return false;
        return true;
    }

    internal bool ScalarEquals(MusicSongRecord other) =>
        SongId == other.SongId &&
        Title == other.Title &&
        Artist == other.Artist &&
        Album == other.Album &&
        NullableDoubleEqual(Duration, other.Duration) &&
        NullableDoubleEqual(BaselineDuration, other.BaselineDuration) &&
        CoverUrl == other.CoverUrl &&
        LocalFileId == other.LocalFileId &&
        ArtworkSourceKey == other.ArtworkSourceKey &&
        ArtworkSourceImage == other.ArtworkSourceImage;

    internal int EstimateBytes()
    {
        int bytes = 128 + Utf8Len(SongId) + Utf8Len(Title) + Utf8Len(Artist) + Utf8Len(Album) +
            Utf8Len(CoverUrl) + Utf8Len(LocalFileId) + Utf8Len(ArtworkSourceKey) + Utf8Len(ArtworkSourceImage);
        foreach (string a in ArtistNames) bytes += 8 + Utf8Len(a);
        foreach (var s in Sources) bytes += s.EstimateBytes();
        return bytes;
    }
}

public sealed record MusicQueueSourceRecord(
    string? Id,
    string? Title,
    string? Kind,
    string? Platform,
    string? SourceType,
    string? SourceTitle,
    string? SourceId,
    string? Playlist,
    string? SeedEntryId,
    string? RemoteSession,
    string? DailyBranch,
    string? SearchKeyword)
{
    internal static MusicQueueSourceRecord Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null);

    internal bool IsEmpty =>
        Id is null && Title is null && Kind is null && Platform is null &&
        SourceType is null && SourceTitle is null && SourceId is null && Playlist is null &&
        SeedEntryId is null && RemoteSession is null && DailyBranch is null && SearchKeyword is null;

    internal (string Name, object? Value)[] ToParams(string prefix) =>
    [
        ($"${prefix}id", Id),
        ($"${prefix}title", Title),
        ($"${prefix}kind", Kind),
        ($"${prefix}plat", Platform),
        ($"${prefix}stype", SourceType),
        ($"${prefix}stitle", SourceTitle),
        ($"${prefix}sid", SourceId),
        ($"${prefix}pl", Playlist),
        ($"${prefix}seed", SeedEntryId),
        ($"${prefix}rs", RemoteSession),
        ($"${prefix}db", DailyBranch),
        ($"${prefix}sq", SearchKeyword)
    ];

    internal static MusicQueueSourceRecord? ReadFrom(SqliteDataReader row, int offset)
    {
        var rec = new MusicQueueSourceRecord(
            row.GetNullableString(offset),
            row.GetNullableString(offset + 1),
            row.GetNullableString(offset + 2),
            row.GetNullableString(offset + 3),
            row.GetNullableString(offset + 4),
            row.GetNullableString(offset + 5),
            row.GetNullableString(offset + 6),
            row.GetNullableString(offset + 7),
            row.GetNullableString(offset + 8),
            row.GetNullableString(offset + 9),
            row.GetNullableString(offset + 10),
            row.GetNullableString(offset + 11));
        return rec.IsEmpty ? null : rec;
    }
}

public sealed record MusicPlayContextRecord(
    string? CollectionId,
    string? PlaylistId,
    string? SourceTitle,
    string? Playlist,
    string? SourceType,
    string? SourceId,
    string? SourcePlatform,
    string? Platform,
    string? SourceLabel,
    string? SearchKeyword,
    string? DailyBranch,
    string? HeartbeatPlaylistId,
    string? HeartbeatAccountId,
    string? Provider,
    string? RemoteSession,
    string? QueueSourceId,
    string? QueueSourceTitle,
    string? QueueSourceKind,
    string? QueueSourcePlatform,
    string? SimilarBatchId,
    string? SimilarBasedOnSongId,
    string? SimilarProvider)
{
    public static MusicPlayContextRecord Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null);

    internal bool IsEmpty =>
        CollectionId is null && PlaylistId is null && SourceTitle is null && Playlist is null &&
        SourceType is null && SourceId is null && SourcePlatform is null && Platform is null &&
        SourceLabel is null && SearchKeyword is null && DailyBranch is null &&
        HeartbeatPlaylistId is null && HeartbeatAccountId is null && Provider is null &&
        RemoteSession is null && QueueSourceId is null && QueueSourceTitle is null &&
        QueueSourceKind is null && QueueSourcePlatform is null && SimilarBatchId is null &&
        SimilarBasedOnSongId is null && SimilarProvider is null;

    internal static MusicPlayContextRecord ReadFrom(SqliteDataReader row, int offset)
    {
        var rec = new MusicPlayContextRecord(
            row.GetNullableString(offset),
            row.GetNullableString(offset + 1),
            row.GetNullableString(offset + 2),
            row.GetNullableString(offset + 3),
            row.GetNullableString(offset + 4),
            row.GetNullableString(offset + 5),
            row.GetNullableString(offset + 6),
            row.GetNullableString(offset + 7),
            row.GetNullableString(offset + 8),
            row.GetNullableString(offset + 9),
            row.GetNullableString(offset + 10),
            row.GetNullableString(offset + 11),
            row.GetNullableString(offset + 12),
            row.GetNullableString(offset + 13),
            row.GetNullableString(offset + 14),
            row.GetNullableString(offset + 15),
            row.GetNullableString(offset + 16),
            row.GetNullableString(offset + 17),
            row.GetNullableString(offset + 18),
            row.GetNullableString(offset + 19),
            row.GetNullableString(offset + 20),
            row.GetNullableString(offset + 21));
        return rec.IsEmpty ? Empty : rec;
    }
}

public sealed record MusicQueueEntryRecord(string EntryId, string SongId, MusicPlayContextRecord Context);

public sealed record MusicQueueStateRecord(
    string? CurrentId,
    long Serial,
    long Revision,
    string? RunId,
    MusicQueueSourceRecord? Source,
    MusicQueueEntryRecord[] Entries,
    string[] Priority)
{
    internal bool HeaderEquals(MusicQueueStateRecord other) =>
        CurrentId == other.CurrentId &&
        Serial == other.Serial &&
        Revision == other.Revision &&
        RunId == other.RunId &&
        Source == other.Source;

    internal bool ContentEquals(MusicQueueStateRecord? other) =>
        other is not null &&
        HeaderEquals(other) &&
        Entries.SequenceEqual(other.Entries) &&
        Priority.SequenceEqual(other.Priority, StringComparer.Ordinal);
}

public sealed record MusicCollectionBindingRecord(
    string Platform,
    string AccountId,
    string RemoteId,
    bool Writable,
    string Name,
    string[] Baseline,
    IReadOnlyDictionary<string, string> Map)
{
    internal bool ContentEquals(MusicCollectionBindingRecord? other)
    {
        if (other is null || Platform != other.Platform || AccountId != other.AccountId ||
            RemoteId != other.RemoteId || Writable != other.Writable || Name != other.Name ||
            !Baseline.SequenceEqual(other.Baseline, StringComparer.Ordinal) ||
            Map.Count != other.Map.Count)
            return false;
        foreach (var (k, v) in Map)
            if (!other.Map.TryGetValue(k, out var ov) || v != ov)
                return false;
        return true;
    }
}

public sealed record MusicCollectionIntentRecord(
    bool Present,
    bool Pending,
    string Origin,
    string[]? Acknowledged)
{
    internal bool ContentEquals(MusicCollectionIntentRecord? other) =>
        other is not null &&
        Present == other.Present &&
        Pending == other.Pending &&
        Origin == other.Origin &&
        (Acknowledged is null
            ? other.Acknowledged is null
            : other.Acknowledged is not null && Acknowledged.SequenceEqual(other.Acknowledged, StringComparer.Ordinal));
}

public sealed record MusicCollectionRecord(
    string CollectionId,
    string Name,
    string Kind,
    long Revision,
    bool ManualOrder,
    bool Deleted,
    bool LocallyCreated,
    string[] Order,
    MusicCollectionBindingRecord[] Bindings,
    IReadOnlyDictionary<string, MusicCollectionIntentRecord> Intents)
{
    internal bool HeaderEquals(MusicCollectionRecord other) =>
        CollectionId == other.CollectionId &&
        Name == other.Name &&
        Kind == other.Kind &&
        Revision == other.Revision &&
        ManualOrder == other.ManualOrder &&
        Deleted == other.Deleted &&
        LocallyCreated == other.LocallyCreated;

    internal bool BindingsEqual(MusicCollectionRecord other)
    {
        if (Bindings.Length != other.Bindings.Length) return false;
        for (int i = 0; i < Bindings.Length; i++)
            if (!Bindings[i].ContentEquals(other.Bindings[i])) return false;
        return true;
    }

    internal bool IntentsEqual(MusicCollectionRecord other)
    {
        if (Intents.Count != other.Intents.Count) return false;
        foreach (var (k, v) in Intents)
            if (!other.Intents.TryGetValue(k, out var ov) || !v.ContentEquals(ov)) return false;
        return true;
    }
}

public sealed record MusicNetworkPlaylistRecord(
    string PlaylistId,
    string Platform,
    string RemoteId,
    string Name,
    string Cover,
    long SongCount,
    long Revision,
    bool Present,
    string? SubAccountId,
    bool? SubPresent,
    string? SubListId,
    string? SubCreatorId,
    string? SubCreatorListId,
    string? IntentAccountId,
    bool? IntentPresent);

public sealed record MusicLibraryAreaRecord(
    long StateRevision,
    string OwnerId,
    long LibraryRevision,
    long NextId,
    IReadOnlyDictionary<string, string> Accounts,
    MusicCollectionRecord[] Collections,
    MusicNetworkPlaylistRecord[] NetworkPlaylists);

public sealed record MusicHistoryPlaylistSummaryRecord(
    string PlaylistId,
    int Ordinal,
    long PlayedAtMs,
    string Title,
    string? CurrentId,
    MusicQueueSourceRecord? Source,
    int EntryCount);

public sealed record MusicHistoryPlaylistRecord(
    string PlaylistId,
    long PlayedAtMs,
    string Title,
    string? CurrentId,
    MusicQueueSourceRecord? Source,
    MusicPlayContextRecord Context,
    MusicQueueEntryRecord[] Entries,
    string[] Priority,
    string[] History);

public sealed record MusicHistoryDaySummaryRecord(
    string DayDate,
    int Ordinal,
    int SongCount);

public sealed record MusicHistoryDaySongRecord(
    string SongId,
    long PlayedAtMs,
    MusicPlayContextRecord Context);

public sealed record MusicHistoryDayRecord(
    string DayDate,
    string DayId,
    long UpdatedAtMs,
    MusicHistoryDaySongRecord[] Songs);

public sealed record MusicHistoryAreaRecord(
    MusicHistoryPlaylistRecord[] Playlists,
    MusicHistoryDayRecord[] Days);

public sealed record MusicPlaybackModeRecord(long Mode);

public sealed record MusicOnlineSettingsRecord(long Revision, bool LyricsEnabled);

public sealed record MusicDeviceSettingsRecord(long Revision, long Volume);

public sealed record MusicAppearanceRecord(
    long Revision,
    string PlaylistView,
    string PlayerTheme,
    string ColorMode,
    string ColorPalette);

public sealed record MusicCatalogSourceRecord(long Revision, string Platform);

public sealed record MusicDesktopLyricsGeometryRecord(
    string Monitor,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record MusicDesktopLyricsRecord(
    long Revision,
    string Layout,
    string NormalColor,
    string HighlightColor,
    MusicDesktopLyricsGeometryRecord? Geometry);

public sealed record MusicQualityPolicyRecord(
    long Revision,
    string Request,
    string[] QualityOrder,
    string[] QualityEnabled,
    string[] SpatialOrder,
    string[] SpatialEnabled);

public sealed record MusicOfficialSourcesRecord(
    long Revision,
    string[] Platforms,
    string[] Enabled);

public sealed record MusicMetadataPolicyRecord(
    long Revision,
    string[] Platforms,
    string[] Enabled);

public sealed record MusicSearchKindRecord(
    string[] Order,
    string[] Enabled,
    long Pages,
    long Concurrency,
    long CacheMinutes);

public sealed record MusicSearchPolicyRecord(
    long Revision,
    MusicSearchKindRecord Songs,
    MusicSearchKindRecord Playlists);

public sealed record MusicSearchHistoryItemRecord(string Text, string Kind, long UsedAtMs);

public sealed record MusicSearchHistoryRecord(
    long Revision,
    MusicSearchHistoryItemRecord[] Items);

public sealed record MusicSourceExclusionRecord(
    string ExclusionId,
    int Ordinal,
    string SongId,
    string Provider,
    string Platform,
    string ProviderName,
    string Title,
    string Artist,
    long ExcludedAtMs);

public sealed record MusicLyricWordRecord(
    double TimeSec,
    string Text,
    double DurationSec,
    double? OffsetSec);

public sealed record MusicLyricLineRecord(
    double TimeSec,
    string Text,
    string? Translation,
    string? Romanization,
    double? EndSec,
    MusicLyricWordRecord[] Words);

public sealed record MusicLocalLyricSummaryRecord(
    string SongId,
    int Ordinal,
    string ContentHash);

public sealed record MusicLocalLyricRecord(
    string SongId,
    int Ordinal,
    string ContentHash,
    MusicLyricLineRecord[] Lines);

public sealed record MusicSyncPlatformRecord(
    bool Favorites,
    bool Playlists,
    string AccountId);

public sealed record MusicSyncPendingRecord(
    string Platform,
    string AccountId,
    string CollectionId,
    string RemoteId,
    string Operation,
    bool? BeforePresent,
    bool? DesiredPresent,
    string Name,
    string? LocatorListId,
    string? LocatorCreatorId,
    string? LocatorCreatorListId,
    string[] Before,
    string[] Desired,
    string[] Ids,
    string[] Directory);

public sealed record MusicSyncConfirmationRecord(
    string Token,
    string Code,
    long Count);

public sealed record MusicSyncIssueRecord(
    string Platform,
    string CollectionId,
    string Code);

public sealed record MusicLibrarySyncStateRecord(
    long Revision,
    IReadOnlyDictionary<string, MusicSyncPlatformRecord> Platforms,
    MusicSyncPendingRecord? Pending,
    MusicSyncConfirmationRecord? Confirmation,
    MusicSyncConfirmationRecord? Approved,
    string StatusPhase,
    long StatusAtMs,
    string StatusCode,
    MusicSyncIssueRecord[] Issues);

internal static class MusicStorageModelHelpers
{
    internal static int Utf8Len(string? s) => s is null ? 0 : Encoding.UTF8.GetByteCount(s);

    internal static bool NullableDoubleEqual(double? a, double? b) =>
        a is null ? b is null : b is { } bv && BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(bv);

    internal static int CommonPrefixLength<T>(IReadOnlyList<T> previous, IReadOnlyList<T> next, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        int limit = Math.Min(previous.Count, next.Count);
        int i = 0;
        while (i < limit && comparer.Equals(previous[i], next[i])) i++;
        return i;
    }

    internal static int EstimateStorageValueBytes(StorageValue? value) =>
        value is null ? 4 : value.EstimateBytes();

    internal static int EstimateCommitBytes(MusicLibraryCommit commit)
    {
        int bytes = 256;
        foreach (var song in commit.Songs) bytes = checked(bytes + song.Record.EstimateBytes());
        if (commit.Library is { Next: var lib })
        {
            bytes = checked(bytes + 256 + lib.Accounts.Count * 64 + lib.NetworkPlaylists.Length * 256);
            foreach (var col in lib.Collections)
                bytes = checked(bytes + 128 + col.Order.Length * 40 + col.Bindings.Length * 192 + col.Intents.Count * 96);
        }
        if (commit.Queue is { Next: var q })
            bytes = checked(bytes + 256 + q.Entries.Length * 128 + q.Priority.Length * 32);
        foreach (var pl in commit.HistoryPlaylists)
            if (pl.Next is { } next)
                bytes = checked(bytes + 256 + next.Entries.Length * 128 + (next.Priority.Length + next.History.Length) * 32);
        foreach (var day in commit.HistoryDays)
            if (day.Next is { } next)
                bytes = checked(bytes + 128 + next.Songs.Length * 96);
        if (commit.HistoryArea is { } hist)
        {
            foreach (var pl in hist.Playlists)
                bytes = checked(bytes + 256 + pl.Entries.Length * 128);
            foreach (var d in hist.Days)
                bytes = checked(bytes + 128 + d.Songs.Length * 96);
        }
        foreach (var lyr in commit.LocalLyrics)
            if (lyr.Next is { } next)
                bytes = checked(bytes + 128 + next.Lines.Length * 96);
        if (commit.ReplaceLocalLyrics is { } allLyrics)
            foreach (var item in allLyrics)
                bytes = checked(bytes + 128 + item.Lines.Length * 96);
        if (commit.LibrarySync is { } sync)
            bytes = checked(bytes + 512 + sync.Issues.Length * 64);
        return bytes;
    }
}
