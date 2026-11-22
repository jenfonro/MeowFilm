using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;
using static MeowFilm.Capabilities.Library.LibraryStorageHelpers;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal sealed record MusicSongSourceRecord(
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
    JsonObject? WireExtras)
{
    private static readonly HashSet<string> PureScalarFields = new(StringComparer.Ordinal)
    {
        "source", "hash", "name", "singer", "albumName", "duration", "img", "qualityKnown", "artistNames"
    };

    private static readonly HashSet<string> IndexedIdFields = new(StringComparer.Ordinal)
    {
        "songmid", "mid", "id"
    };

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
            JsonValuesEqual(WireExtras, other.WireExtras);
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
        if (WireExtras is not null) bytes += EstimateJsonBytes(WireExtras);
        return bytes;
    }

    internal JsonObject ToJson()
    {
        var mi = new JsonObject { ["source"] = InfoSource };
        SetNonNull(mi, "songmid", SongMid);
        SetNonNull(mi, "mid", Mid);
        SetNonNull(mi, "id", NativeId);
        SetNonNull(mi, "hash", Hash);
        SetNonNull(mi, "name", Name);
        SetNonNull(mi, "singer", Singer);
        SetNonNull(mi, "albumName", AlbumName);
        mi["duration"] = Duration;
        SetNonNull(mi, "img", Img);
        SetNonNull(mi, "qualityKnown", QualityKnown);
        if (ArtistNames.Length > 0)
            mi["artistNames"] = new JsonArray(ArtistNames.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (WireExtras is not null)
        {
            foreach (var pair in WireExtras)
                mi[pair.Key] = pair.Value?.DeepClone();
        }
        return new JsonObject
        {
            ["sourceKey"] = SourceKey,
            ["source"] = Platform,
            ["musicInfo"] = mi
        };
    }

    internal static MusicSongSourceRecord FromJson(JsonObject src)
    {
        string sourceKey = Str(src, "sourceKey")!;
        string platform = Str(src, "source")!;
        var mi = src["musicInfo"] as JsonObject ?? new JsonObject();
        string infoSource = Str(mi, "source") ?? platform;

        string? songMid = ReadIdAsText(mi, "songmid", out bool songMidNeedsExtra);
        string? mid = ReadIdAsText(mi, "mid", out bool midNeedsExtra);
        string? nativeId = ReadIdAsText(mi, "id", out bool nativeIdNeedsExtra);

        var artists = new List<string>();
        if (mi["artistNames"] is JsonArray sa)
        {
            foreach (var a in sa)
                if (a is JsonValue av && av.TryGetValue<string>(out var name))
                    artists.Add(name);
        }

        JsonObject? extras = null;
        foreach (var pair in mi)
        {
            if (PureScalarFields.Contains(pair.Key)) continue;
            if (IndexedIdFields.Contains(pair.Key))
            {
                bool keepExtra = pair.Key switch
                {
                    "songmid" => songMidNeedsExtra,
                    "mid" => midNeedsExtra,
                    "id" => nativeIdNeedsExtra,
                    _ => false
                };
                if (!keepExtra) continue;
            }
            extras ??= new JsonObject();
            extras[pair.Key] = pair.Value?.DeepClone();
        }

        return new MusicSongSourceRecord(
            sourceKey,
            platform,
            infoSource,
            songMid,
            mid,
            nativeId,
            Str(mi, "hash"),
            Str(mi, "name"),
            Str(mi, "singer"),
            Str(mi, "albumName"),
            NativeMusicSong.ValidDuration(mi["duration"]),
            Str(mi, "img"),
            Bool(mi, "qualityKnown"),
            artists.ToArray(),
            extras);
    }

    private static string? ReadIdAsText(JsonObject obj, string key, out bool needsTypedExtra)
    {
        needsTypedExtra = false;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is not JsonValue v)
        {
            needsTypedExtra = true;
            return null;
        }
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<JsonElement>(out var el))
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Number)
            {
                needsTypedExtra = true;
                return el.GetRawText();
            }
            needsTypedExtra = true;
            return null;
        }
        if (v.GetValueKind() == JsonValueKind.Number)
        {
            needsTypedExtra = true;
            return v.ToJsonString();
        }
        needsTypedExtra = true;
        return null;
    }
}

internal sealed record MusicSongRecord(
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

    internal JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["songId"] = SongId,
            ["title"] = Title,
            ["artist"] = Artist,
            ["album"] = Album,
            ["duration"] = Duration,
            ["baselineDuration"] = BaselineDuration,
            ["cover_url"] = CoverUrl
        };
        SetNonNull(obj, "localFileId", LocalFileId);
        SetNonNull(obj, "artworkSourceKey", ArtworkSourceKey);
        SetNonNull(obj, "artworkSourceImage", ArtworkSourceImage);
        if (ArtistNames.Length > 0)
            obj["artistNames"] = new JsonArray(ArtistNames.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        var sources = new JsonArray();
        foreach (var s in Sources) sources.Add(s.ToJson());
        obj["sources"] = sources;
        return obj;
    }

    internal static MusicSongRecord FromJson(string songId, JsonObject value)
    {
        var artists = new List<string>();
        if (value["artistNames"] is JsonArray arr)
        {
            foreach (var a in arr)
                if (a is JsonValue av && av.TryGetValue<string>(out var name))
                    artists.Add(name);
        }

        var sources = new List<MusicSongSourceRecord>();
        if (value["sources"] is JsonArray srcArr)
        {
            foreach (var item in srcArr)
                if (item is JsonObject src)
                    sources.Add(MusicSongSourceRecord.FromJson(src));
        }

        return new MusicSongRecord(
            songId,
            Str(value, "title") ?? "",
            Str(value, "artist") ?? "",
            Str(value, "album") ?? "",
            NativeMusicSong.ValidDuration(value["duration"]),
            NativeMusicSong.ValidDuration(value["baselineDuration"]),
            Str(value, "cover_url") ?? "",
            Str(value, "localFileId"),
            Str(value, "artworkSourceKey"),
            Str(value, "artworkSourceImage"),
            artists.ToArray(),
            sources.ToArray());
    }
}

internal sealed record MusicQueueSourceRecord(
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
    internal bool IsEmpty =>
        Id is null && Title is null && Kind is null && Platform is null &&
        SourceType is null && SourceTitle is null && SourceId is null && Playlist is null &&
        SeedEntryId is null && RemoteSession is null && DailyBranch is null && SearchKeyword is null;

    internal JsonObject? ToJson()
    {
        if (IsEmpty) return null;
        var src = new JsonObject();
        SetNonNull(src, "id", Id);
        SetNonNull(src, "title", Title);
        SetNonNull(src, "kind", Kind);
        SetNonNull(src, "platform", Platform);
        SetNonNull(src, "sourceType", SourceType);
        SetNonNull(src, "sourceTitle", SourceTitle);
        SetNonNull(src, "sourceId", SourceId);
        SetNonNull(src, "playlist", Playlist);
        SetNonNull(src, "seedEntryId", SeedEntryId);
        SetNonNull(src, "remoteSession", RemoteSession);
        SetNonNull(src, "dailyBranch", DailyBranch);
        SetNonNull(src, "searchKeyword", SearchKeyword);
        return src;
    }

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

    internal static MusicQueueSourceRecord? FromJson(JsonObject? src)
    {
        if (src is null) return null;
        var rec = new MusicQueueSourceRecord(
            Str(src, "id"), Str(src, "title"), Str(src, "kind"), Str(src, "platform"),
            Str(src, "sourceType"), Str(src, "sourceTitle"), Str(src, "sourceId"),
            Str(src, "playlist"), Str(src, "seedEntryId"), Str(src, "remoteSession"),
            Str(src, "dailyBranch"), Str(src, "searchKeyword"));
        return rec.IsEmpty ? null : rec;
    }
}

internal sealed record MusicPlayContextRecord(
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
    internal static MusicPlayContextRecord Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null);

    internal JsonObject ToJson()
    {
        var ctx = new JsonObject();
        SetNonNull(ctx, "collectionId", CollectionId);
        SetNonNull(ctx, "playlistId", PlaylistId);
        SetNonNull(ctx, "sourceTitle", SourceTitle);
        SetNonNull(ctx, "playlist", Playlist);
        SetNonNull(ctx, "sourceType", SourceType);
        SetNonNull(ctx, "sourceId", SourceId);
        SetNonNull(ctx, "sourcePlatform", SourcePlatform);
        SetNonNull(ctx, "platform", Platform);
        SetNonNull(ctx, "sourceLabel", SourceLabel);
        SetNonNull(ctx, "searchKeyword", SearchKeyword);
        SetNonNull(ctx, "dailyBranch", DailyBranch);
        SetNonNull(ctx, "heartbeatPlaylistId", HeartbeatPlaylistId);
        SetNonNull(ctx, "heartbeatAccountId", HeartbeatAccountId);
        SetNonNull(ctx, "provider", Provider);
        SetNonNull(ctx, "remoteSession", RemoteSession);

        if (QueueSourceId is not null || QueueSourceTitle is not null || QueueSourceKind is not null || QueueSourcePlatform is not null)
        {
            var qs = new JsonObject();
            SetNonNull(qs, "id", QueueSourceId);
            SetNonNull(qs, "title", QueueSourceTitle);
            SetNonNull(qs, "kind", QueueSourceKind);
            SetNonNull(qs, "platform", QueueSourcePlatform);
            ctx["queueSource"] = qs;
        }
        if (SimilarBatchId is not null || SimilarBasedOnSongId is not null || SimilarProvider is not null)
        {
            var sim = new JsonObject();
            SetNonNull(sim, "batchId", SimilarBatchId);
            SetNonNull(sim, "basedOnSongId", SimilarBasedOnSongId);
            SetNonNull(sim, "provider", SimilarProvider);
            ctx["similar"] = sim;
        }
        return ctx;
    }

    internal (string Name, object? Value)[] ToParams() =>
    [
        ("$ccid", CollectionId),
        ("$cplid", PlaylistId),
        ("$cstitle", SourceTitle),
        ("$cpl", Playlist),
        ("$cstype", SourceType),
        ("$csid", SourceId),
        ("$csplat", SourcePlatform),
        ("$cplat", Platform),
        ("$cslbl", SourceLabel),
        ("$csq", SearchKeyword),
        ("$cdb", DailyBranch),
        ("$chpid", HeartbeatPlaylistId),
        ("$chaid", HeartbeatAccountId),
        ("$cprov", Provider),
        ("$crs", RemoteSession),
        ("$cqsid", QueueSourceId),
        ("$cqst", QueueSourceTitle),
        ("$cqsk", QueueSourceKind),
        ("$cqsp", QueueSourcePlatform),
        ("$csbid", SimilarBatchId),
        ("$csbos", SimilarBasedOnSongId),
        ("$csprov", SimilarProvider)
    ];

    internal static MusicPlayContextRecord ReadFrom(SqliteDataReader row, int offset) => new(
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

    internal static MusicPlayContextRecord FromJson(JsonObject? ctx)
    {
        if (ctx is null) return Empty;
        var qs = ctx["queueSource"] as JsonObject;
        var sim = ctx["similar"] as JsonObject;
        return new MusicPlayContextRecord(
            Str(ctx, "collectionId"),
            Str(ctx, "playlistId"),
            Str(ctx, "sourceTitle"),
            Str(ctx, "playlist"),
            Str(ctx, "sourceType"),
            Str(ctx, "sourceId"),
            Str(ctx, "sourcePlatform"),
            Str(ctx, "platform"),
            Str(ctx, "sourceLabel"),
            Str(ctx, "searchKeyword"),
            Str(ctx, "dailyBranch"),
            Str(ctx, "heartbeatPlaylistId"),
            Str(ctx, "heartbeatAccountId"),
            Str(ctx, "provider"),
            Str(ctx, "remoteSession"),
            Str(qs, "id"),
            Str(qs, "title"),
            Str(qs, "kind"),
            Str(qs, "platform"),
            Str(sim, "batchId"),
            Str(sim, "basedOnSongId"),
            Str(sim, "provider"));
    }
}

internal sealed record MusicQueueEntryRecord(string EntryId, string SongId, MusicPlayContextRecord Context);

internal sealed record MusicQueueStateRecord(
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
}

internal sealed record MusicCollectionBindingRecord(
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

internal sealed record MusicCollectionIntentRecord(
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

internal sealed record MusicCollectionRecord(
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

internal sealed record MusicNetworkPlaylistRecord(
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

internal sealed record MusicLibraryAreaRecord(
    long StateRevision,
    string OwnerId,
    long LibraryRevision,
    long NextId,
    IReadOnlyDictionary<string, string> Accounts,
    MusicCollectionRecord[] Collections,
    MusicNetworkPlaylistRecord[] NetworkPlaylists);

internal static class MusicStorageModelHelpers
{
    internal static int Utf8Len(string? s) => s is null ? 0 : Encoding.UTF8.GetByteCount(s);

    internal static bool NullableDoubleEqual(double? a, double? b) =>
        a is null ? b is null : b is { } bv && BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(bv);

    internal static int EstimateJsonBytes(JsonNode? node)
    {
        if (node is null) return 4;
        if (node is JsonObject obj)
        {
            int total = 2;
            foreach (var pair in obj)
                total += 4 + Encoding.UTF8.GetByteCount(pair.Key) + EstimateJsonBytes(pair.Value);
            return total;
        }
        if (node is JsonArray arr)
        {
            int total = 2;
            foreach (var item in arr)
                total += 1 + EstimateJsonBytes(item);
            return total;
        }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return 2 + Encoding.UTF8.GetByteCount(s);
            if (v.TryGetValue<JsonElement>(out var el))
            {
                return el.ValueKind switch
                {
                    JsonValueKind.String => 2 + Encoding.UTF8.GetByteCount(el.GetString()!),
                    JsonValueKind.True => 4,
                    JsonValueKind.False => 5,
                    JsonValueKind.Null => 4,
                    _ => el.GetRawText().Length
                };
            }
            return 16;
        }
        return 8;
    }
}
