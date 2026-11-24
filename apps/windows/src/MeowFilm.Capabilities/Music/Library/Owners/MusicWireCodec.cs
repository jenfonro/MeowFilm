using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>
/// Thin JSON wire boundary codec for RPC methods (ExecuteAsync / Node runtime).
/// Operates directly on JsonElement and Utf8JsonWriter with zero mutable DOM allocations.
/// </summary>
internal static class MusicWireCodec
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32
    };

    private static readonly HashSet<string> PureSourceScalarFields = new(StringComparer.Ordinal)
    {
        "source", "hash", "name", "singer", "albumName", "duration", "img", "qualityKnown", "artistNames"
    };

    private static readonly HashSet<string> IndexedSourceIdFields = new(StringComparer.Ordinal)
    {
        "songmid", "mid", "id"
    };

    internal static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
            writer.Flush();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    internal static JsonElement BuildObject(Action<Utf8JsonWriter> writeFields) =>
        Build(w => { w.WriteStartObject(); writeFields(w); w.WriteEndObject(); });

    internal static JsonElement Ok(Action<Utf8JsonWriter>? writeFields = null) =>
        BuildObject(w =>
        {
            w.WriteBoolean("ok", true);
            writeFields?.Invoke(w);
        });

    internal static JsonElement Fail(string code) =>
        BuildObject(w =>
        {
            w.WriteBoolean("ok", false);
            w.WriteString("code", code);
        });

    internal static JsonElement ParseParameters(object? value)
    {
        if (value is JsonElement existing)
        {
            ValidateNoDuplicateKeys(existing, "INVALID_REQUEST");
            MusicRules.Require(existing.ValueKind == JsonValueKind.Object);
            return existing.Clone();
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value ?? new { }, SerializeOptions);
        return ParseStrictObject(bytes, "INVALID_REQUEST");
    }

    internal static JsonElement ParseStrictObject(ReadOnlyMemory<byte> bytes, string errorCode = "INVALID_REQUEST")
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            ValidateNoDuplicateKeys(doc.RootElement, errorCode);
            MusicRules.Require(doc.RootElement.ValueKind == JsonValueKind.Object, errorCode);
            return doc.RootElement.Clone();
        }
        catch (MusicLibraryException) { throw; }
        catch { throw new MusicLibraryException(errorCode); }
    }

    private static void ValidateNoDuplicateKeys(JsonElement element, string errorCode)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                MusicRules.Require(seen.Add(p.Name), errorCode);
                ValidateNoDuplicateKeys(p.Value, errorCode);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                ValidateNoDuplicateKeys(child, errorCode);
        }
    }

    // ---- JsonElement readers ----

    internal static bool Has(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out _);

    internal static bool HasNonNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;

    internal static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value))
            return true;
        value = default;
        return false;
    }

    internal static void RequireFields(JsonElement obj, string required)
    {
        MusicRules.Require(obj.ValueKind == JsonValueKind.Object);
        foreach (string key in required.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            MusicRules.Require(obj.TryGetProperty(key, out _));
    }

    internal static JsonElement RequireObject(JsonElement value, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(value.ValueKind == JsonValueKind.Object, code);
        return value;
    }

    internal static JsonElement RequireObjectProp(JsonElement obj, string key, string code = "INVALID_REQUEST")
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Object)
            throw new MusicLibraryException(code);
        return v;
    }

    internal static JsonElement RequireArray(JsonElement value, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(value.ValueKind == JsonValueKind.Array, code);
        return value;
    }

    internal static JsonElement RequireArrayProp(JsonElement obj, string key, string code = "INVALID_REQUEST")
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new MusicLibraryException(code);
        return v;
    }

    internal static string Str(JsonElement obj, string key, string fallback = "") =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    internal static string? OptionalStr(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static string RequireStr(JsonElement value, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(value.ValueKind == JsonValueKind.String && value.GetString() is not null, code);
        return value.GetString()!;
    }

    internal static string RequireStrProp(JsonElement obj, string key, string code = "INVALID_REQUEST")
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
            throw new MusicLibraryException(code);
        return v.GetString()!;
    }

    internal static string RequireId(JsonElement value, int? max = 2048, string code = "INVALID_REQUEST")
    {
        string text = RequireStr(value, code);
        MusicRules.Require(MusicRules.IsValidId(text, max), code);
        return text;
    }

    internal static string RequireIdProp(JsonElement obj, string key, int? max = 2048, string code = "INVALID_REQUEST")
    {
        string text = RequireStrProp(obj, key, code);
        MusicRules.Require(MusicRules.IsValidId(text, max), code);
        return text;
    }

    internal static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => ""
    };

    internal static string ScalarProp(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? Scalar(v) : "";

    internal static bool Bool(JsonElement obj, string key, bool fallback = false) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    internal static bool RequireBool(JsonElement value, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, code);
        return value.GetBoolean();
    }

    internal static bool RequireBoolProp(JsonElement obj, string key, string code = "INVALID_REQUEST")
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new MusicLibraryException(code);
        return v.GetBoolean();
    }

    internal static bool OptionalBoolProp(JsonElement obj, string key, bool fallback = false, string code = "INVALID_REQUEST")
    {
        if (!TryGet(obj, key, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        return RequireBool(v, code);
    }

    internal static double Num(JsonElement value, double fallback = 0)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return fallback;
    }

    internal static double NumProp(JsonElement obj, string key, double fallback = 0) =>
        TryGet(obj, key, out var v) ? Num(v, fallback) : fallback;

    internal static double RequireFiniteDouble(JsonElement value, string code = "INVALID_REQUEST")
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d) || !double.IsFinite(d))
            throw new MusicLibraryException(code);
        return d;
    }

    internal static long RequireInt(JsonElement value, long min = 0, long max = MusicRules.MaxInteger, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(value.ValueKind == JsonValueKind.Number, code);
        decimal parsed = 0;
        MusicRules.Require(
            decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
            decimal.Truncate(parsed) == parsed && parsed >= min && parsed <= max, code);
        return (long)parsed;
    }

    internal static long RequireIntProp(JsonElement obj, string key, long min = 0, long max = MusicRules.MaxInteger, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(TryGet(obj, key, out var v), code);
        return RequireInt(v, min, max, code);
    }

    internal static string[] RequireIdArrayProp(JsonElement obj, string key, int? maxItemLength = 2048, string code = "INVALID_REQUEST")
    {
        var arr = RequireArrayProp(obj, key, code);
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
            list.Add(RequireId(item, maxItemLength, code));
        return list.ToArray();
    }

    internal static string[] StringArray(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list.ToArray();
    }

    internal static (long Offset, int Limit) ReadPageArgs(JsonElement p)
    {
        long offset = HasNonNull(p, "offset") ? RequireIntProp(p, "offset") : 0;
        int limit = HasNonNull(p, "limit") ? (int)RequireIntProp(p, "limit", 1, 100) : 50;
        return (offset, limit);
    }

    internal static void WritePage<T>(
        Utf8JsonWriter w,
        IReadOnlyList<T> values,
        JsonElement p,
        Action<Utf8JsonWriter, T> writeItem,
        string field = "items")
    {
        var (offset, limit) = ReadPageArgs(p);
        w.WritePropertyName(field);
        w.WriteStartArray();
        if (offset < values.Count)
        {
            int start = (int)offset;
            int end = Math.Min(values.Count, start + limit);
            for (int i = start; i < end; i++)
                writeItem(w, values[i]);
        }
        w.WriteEndArray();
        w.WriteNumber("total", values.Count);
        w.WriteNumber("offset", offset);
        w.WriteNumber("limit", limit);
    }

    internal static void WriteStringArray(Utf8JsonWriter w, string propertyName, IReadOnlyList<string> items)
    {
        w.WritePropertyName(propertyName);
        w.WriteStartArray();
        for (int i = 0; i < items.Count; i++) w.WriteStringValue(items[i]);
        w.WriteEndArray();
    }

    internal static void WriteOptionalString(Utf8JsonWriter w, string propertyName, string? value)
    {
        if (value is not null) w.WriteString(propertyName, value);
    }

    // ---- Songs & Sources ----

    internal static MusicSongRecord ReadSong(JsonElement input, string errorCode = "INVALID_SONG")
    {
        MusicRules.Require(input.ValueKind == JsonValueKind.Object, errorCode);
        var sourcesEl = RequireArrayProp(input, "sources", "INVALID_SOURCE");
        var sources = new List<MusicSongSourceRecord>(sourcesEl.GetArrayLength());
        foreach (var item in sourcesEl.EnumerateArray())
            sources.Add(NativeMusicSong.DurableSource(ReadSource(item)));

        string[] artists = TryGet(input, "artistNames", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array
            ? StringArray(artistsEl) : [];

        double? duration = NativeMusicSong.ValidDuration(TryGet(input, "duration", out var dEl) ? Num(dEl, double.NaN) : null);
        double? baseline = NativeMusicSong.ValidDuration(TryGet(input, "baselineDuration", out var bEl) ? Num(bEl, double.NaN) : null) ?? duration;

        return new MusicSongRecord(
            Str(input, "songId"),
            Str(input, "title"),
            Str(input, "artist"),
            Str(input, "album"),
            duration,
            baseline,
            NativeMusicSong.PublicUrl(Str(input, "cover_url")),
            TryGet(input, "localFileId", out var lf) && lf.ValueKind != JsonValueKind.Null ? Str(input, "localFileId") : null,
            TryGet(input, "artworkSourceKey", out var ak) && ak.ValueKind != JsonValueKind.Null ? Str(input, "artworkSourceKey") : null,
            TryGet(input, "artworkSourceImage", out var ai) && ai.ValueKind != JsonValueKind.Null ? Str(input, "artworkSourceImage") : null,
            artists,
            sources.ToArray());
    }

    internal static MusicSongSourceRecord ReadSource(JsonElement input, string errorCode = "INVALID_SOURCE")
    {
        MusicRules.Require(input.ValueKind == JsonValueKind.Object, errorCode);
        var mi = RequireObjectProp(input, "musicInfo", errorCode);
        string sourceKey = Str(input, "sourceKey");
        string platform = Str(input, "source");
        return ReadSourceFromMusicInfo(sourceKey, platform, mi);
    }

    internal static MusicSongSourceRecord ReadSourceFromMusicInfo(string sourceKey, string platform, JsonElement mi)
    {
        string infoSource = Str(mi, "source", platform);
        string? songMid = ReadIdField(mi, "songmid", out bool songMidNeedsExtra);
        string? mid = ReadIdField(mi, "mid", out bool midNeedsExtra);
        string? nativeId = ReadIdField(mi, "id", out bool nativeIdNeedsExtra);
        string[] artists = TryGet(mi, "artistNames", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array
            ? StringArray(artistsEl) : [];

        List<StorageValueField>? extraEntries = null;
        foreach (var prop in mi.EnumerateObject())
        {
            if (PureSourceScalarFields.Contains(prop.Name)) continue;
            if (IndexedSourceIdFields.Contains(prop.Name))
            {
                bool keepExtra = prop.Name switch
                {
                    "songmid" => songMidNeedsExtra,
                    "mid" => midNeedsExtra,
                    "id" => nativeIdNeedsExtra,
                    _ => false
                };
                if (!keepExtra) continue;
            }
            extraEntries ??= [];
            extraEntries.Add(new StorageValueField(prop.Name, StorageValueJson.FromJsonElement(prop.Value)));
        }

        double? duration = TryGet(mi, "duration", out var durEl) ? NativeMusicSong.ValidDuration(Num(durEl, double.NaN)) : null;
        bool? qualityKnown = TryGet(mi, "qualityKnown", out var qkEl) && qkEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? qkEl.GetBoolean() : null;

        return new MusicSongSourceRecord(
            sourceKey,
            platform,
            infoSource,
            songMid,
            mid,
            nativeId,
            OptionalStr(mi, "hash"),
            OptionalStr(mi, "name"),
            OptionalStr(mi, "singer"),
            OptionalStr(mi, "albumName"),
            duration,
            OptionalStr(mi, "img"),
            qualityKnown,
            artists,
            extraEntries is null ? null : StorageValue.FromObject(extraEntries));
    }

    private static string? ReadIdField(JsonElement obj, string key, out bool needsTypedExtra)
    {
        needsTypedExtra = false;
        if (!obj.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        needsTypedExtra = true;
        return el.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? el.GetRawText() : null;
    }

    internal static void WriteSong(Utf8JsonWriter w, MusicSongRecord song)
    {
        w.WriteStartObject();
        w.WriteString("songId", song.SongId);
        w.WriteString("title", song.Title);
        w.WriteString("artist", song.Artist);
        w.WriteString("album", song.Album);
        if (song.Duration is { } dur) w.WriteNumber("duration", dur); else w.WriteNull("duration");
        if (song.BaselineDuration is { } bdur) w.WriteNumber("baselineDuration", bdur); else w.WriteNull("baselineDuration");
        w.WriteString("cover_url", song.CoverUrl);
        WriteOptionalString(w, "localFileId", song.LocalFileId);
        WriteOptionalString(w, "artworkSourceKey", song.ArtworkSourceKey);
        WriteOptionalString(w, "artworkSourceImage", song.ArtworkSourceImage);
        if (song.ArtistNames.Length > 0) WriteStringArray(w, "artistNames", song.ArtistNames);
        w.WritePropertyName("sources");
        w.WriteStartArray();
        foreach (var s in song.Sources) WriteSource(w, s);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static JsonElement SongToElement(MusicSongRecord song) => Build(w => WriteSong(w, song));

    internal static void WriteSource(Utf8JsonWriter w, MusicSongSourceRecord src)
    {
        w.WriteStartObject();
        w.WriteString("sourceKey", src.SourceKey);
        w.WriteString("source", src.Platform);
        w.WritePropertyName("musicInfo");
        WriteMusicInfo(w, src);
        w.WriteEndObject();
    }

    internal static void WriteMusicInfo(Utf8JsonWriter w, MusicSongSourceRecord src, bool clampNonNegativeDuration = false)
    {
        var extraNames = new HashSet<string>(StringComparer.Ordinal);
        if (src.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } ef })
            foreach (var f in ef) extraNames.Add(f.Name);

        w.WriteStartObject();
        w.WriteString("source", src.InfoSource);
        if (src.SongMid is not null && !extraNames.Contains("songmid")) w.WriteString("songmid", src.SongMid);
        if (src.Mid is not null && !extraNames.Contains("mid")) w.WriteString("mid", src.Mid);
        if (src.NativeId is not null && !extraNames.Contains("id")) w.WriteString("id", src.NativeId);
        WriteOptionalString(w, "hash", src.Hash);
        WriteOptionalString(w, "name", src.Name);
        WriteOptionalString(w, "singer", src.Singer);
        WriteOptionalString(w, "albumName", src.AlbumName);
        if (src.Duration is { } dur)
            w.WriteNumber("duration", clampNonNegativeDuration ? Math.Max(0, dur) : dur);
        else
            w.WriteNull("duration");
        WriteOptionalString(w, "img", src.Img);
        if (src.QualityKnown is { } qk) w.WriteBoolean("qualityKnown", qk);
        if (src.ArtistNames.Length > 0) WriteStringArray(w, "artistNames", src.ArtistNames);
        if (src.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } extraFields })
        {
            foreach (var field in extraFields)
            {
                w.WritePropertyName(field.Name);
                StorageValueJson.WriteJson(w, field.Value);
            }
        }
        w.WriteEndObject();
    }

    internal static JsonElement MusicInfoToElement(MusicSongSourceRecord src, bool clampNonNegativeDuration = false) =>
        Build(w => WriteMusicInfo(w, src, clampNonNegativeDuration));

    internal static JsonElement SourceToElement(MusicSongSourceRecord src) => Build(w => WriteSource(w, src));

    // ---- Context & QueueSource ----

    internal static MusicPlayContextRecord ReadContext(JsonElement input, out MusicQueueSourceRecord? queueSource)
    {
        queueSource = null;
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return MusicPlayContextRecord.Empty;
        MusicRules.Require(input.ValueKind == JsonValueKind.Object);
        static string? TextOrNull(JsonElement obj, string key) =>
            TryGet(obj, key, out var v) && v.ValueKind != JsonValueKind.Null ? RequireStr(v) : null;

        if (TryGet(input, "queueSource", out var qsEl) && qsEl.ValueKind != JsonValueKind.Null)
            queueSource = ReadQueueSource(qsEl, "INVALID_REQUEST");

        return new MusicPlayContextRecord(
            PlaylistId: TextOrNull(input, "playlistId"),
            SourceTitle: TextOrNull(input, "sourceTitle"),
            Playlist: TextOrNull(input, "playlist"),
            SourceType: TextOrNull(input, "sourceType"),
            SourceId: TextOrNull(input, "sourceId"),
            SourcePlatform: TextOrNull(input, "sourcePlatform"),
            SearchKeyword: TextOrNull(input, "searchKeyword"),
            DailyBranch: TextOrNull(input, "dailyBranch"),
            HeartbeatPlaylistId: TextOrNull(input, "heartbeatPlaylistId"),
            HeartbeatAccountId: TextOrNull(input, "heartbeatAccountId"));
    }

    internal static MusicQueueSourceRecord? ReadQueueSource(JsonElement input, string errorCode = "CORRUPT_QUEUE")
    {
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        MusicRules.Require(input.ValueKind == JsonValueKind.Object, errorCode);
        RequireFields(input, "id title");
        string id = RequireStrProp(input, "id", errorCode);
        string title = RequireStrProp(input, "title", errorCode);
        string? kind = TryGet(input, "kind", out var k) && k.ValueKind != JsonValueKind.Null ? RequireStr(k, errorCode) : null;
        string? platform = TryGet(input, "platform", out var p) && p.ValueKind != JsonValueKind.Null ? RequireStr(p, errorCode) : null;
        return new MusicQueueSourceRecord(id, title, kind, platform);
    }

    internal static void WriteContextView(Utf8JsonWriter w, MusicPlayContextRecord ctx)
    {
        w.WriteStartObject();
        WriteOptionalString(w, "playlistId", ctx.PlaylistId);
        WriteOptionalString(w, "sourceTitle", ctx.SourceTitle);
        WriteOptionalString(w, "playlist", ctx.Playlist);
        WriteOptionalString(w, "sourcePlatform", ctx.SourcePlatform);
        WriteOptionalString(w, "sourceType", ctx.SourceType);
        WriteOptionalString(w, "sourceId", ctx.SourceId);
        WriteOptionalString(w, "searchKeyword", ctx.SearchKeyword);
        WriteOptionalString(w, "dailyBranch", ctx.DailyBranch);
        WriteOptionalString(w, "heartbeatPlaylistId", ctx.HeartbeatPlaylistId);
        WriteOptionalString(w, "heartbeatAccountId", ctx.HeartbeatAccountId);
        w.WriteEndObject();
    }

    internal static void WriteQueueSourceView(Utf8JsonWriter w, MusicQueueSourceRecord? source)
    {
        if (source is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        WriteOptionalString(w, "id", source.Id);
        WriteOptionalString(w, "title", source.Title);
        WriteOptionalString(w, "kind", source.Kind);
        WriteOptionalString(w, "platform", source.Platform);
        w.WriteEndObject();
    }

    internal static void WriteEntryView(Utf8JsonWriter w, MusicQueueEntryRecord entry, MusicSongRecord song)
    {
        w.WriteStartObject();
        w.WriteString("id", entry.EntryId);
        w.WriteString("key", entry.SongId);
        w.WritePropertyName("song");
        WriteSong(w, song);
        w.WritePropertyName("context");
        WriteContextView(w, entry.Context);
        w.WriteEndObject();
    }

    // ---- Views & Settings ----

    internal static void WriteCollectionView(Utf8JsonWriter w, MusicCollectionRecord c)
    {
        w.WriteStartObject();
        w.WriteString("id", c.CollectionId);
        w.WriteString("name", c.Name);
        w.WriteString("kind", c.Kind);
        w.WriteNumber("revision", c.Revision);
        w.WriteNumber("count", c.Order.Length);
        w.WriteBoolean("manualOrder", c.ManualOrder);
        w.WriteEndObject();
    }

    internal static void WriteNetworkPlaylistView(Utf8JsonWriter w, MusicNetworkPlaylistRecord r)
    {
        w.WriteStartObject();
        w.WriteString("id", r.PlaylistId);
        w.WriteString("kind", "network");
        w.WriteString("platform", r.Platform);
        w.WriteString("remoteId", r.RemoteId);
        w.WriteString("name", r.Name);
        w.WriteString("cover", r.Cover);
        w.WriteNumber("count", r.SongCount);
        w.WriteNumber("revision", r.Revision);
        w.WriteBoolean("editable", false);
        w.WriteEndObject();
    }

    internal static void WriteAppearance(Utf8JsonWriter w, MusicAppearanceRecord a)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", a.Revision);
        w.WriteString("playlistView", a.PlaylistView);
        w.WriteString("playerTheme", a.PlayerTheme);
        w.WriteString("colorMode", a.ColorMode);
        w.WriteString("colorPalette", a.ColorPalette);
        w.WriteEndObject();
    }

    internal static void WriteCatalogSource(Utf8JsonWriter w, MusicCatalogSourceRecord s)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", s.Revision);
        w.WriteString("platform", s.Platform);
        w.WriteEndObject();
    }

    internal static void WriteOnlineSettings(Utf8JsonWriter w, MusicOnlineSettingsRecord s)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", s.Revision);
        w.WriteBoolean("lyrics", s.LyricsEnabled);
        w.WriteEndObject();
    }

    internal static void WriteDeviceSettings(Utf8JsonWriter w, MusicDeviceSettingsRecord s)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", s.Revision);
        w.WriteNumber("volume", s.Volume);
        w.WriteEndObject();
    }

    internal static void WriteDesktopLyrics(Utf8JsonWriter w, MusicDesktopLyricsRecord s)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", s.Revision);
        w.WriteString("layout", s.Layout);
        w.WriteString("normalColor", s.NormalColor);
        w.WriteString("highlightColor", s.HighlightColor);
        if (s.Geometry is { } g)
        {
            w.WritePropertyName("geometry");
            w.WriteStartObject();
            w.WriteString("monitor", g.Monitor);
            w.WriteNumber("x", g.X);
            w.WriteNumber("y", g.Y);
            w.WriteNumber("width", g.Width);
            w.WriteNumber("height", g.Height);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    internal static void WriteQualityPolicy(Utf8JsonWriter w, MusicQualityPolicyRecord p)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", p.Revision);
        w.WriteString("request", p.Request);
        WriteStringArray(w, "qualityOrder", p.QualityOrder);
        WriteStringArray(w, "qualityEnabled", p.QualityEnabled);
        WriteStringArray(w, "spatialOrder", p.SpatialOrder);
        WriteStringArray(w, "spatialEnabled", p.SpatialEnabled);
        w.WriteEndObject();
    }

    internal static MusicQualityPolicyRecord ReadQualityPolicy(JsonElement el, string code = "INVALID_REQUEST")
    {
        MusicRules.Require(el.ValueKind == JsonValueKind.Object, code);
        RequireFields(el, "schema revision request qualityOrder qualityEnabled spatialOrder spatialEnabled");
        MusicRules.Require(RequireIntProp(el, "schema", code: code) == 1, code);
        long rev = RequireIntProp(el, "revision", code: code);
        string request = RequireStrProp(el, "request", code);
        var rec = new MusicQualityPolicyRecord(
            rev,
            request,
            RequireIdArrayProp(el, "qualityOrder", 32, code),
            RequireIdArrayProp(el, "qualityEnabled", 32, code),
            RequireIdArrayProp(el, "spatialOrder", 32, code),
            RequireIdArrayProp(el, "spatialEnabled", 32, code));
        MusicQualityPolicy.Validate(rec);
        return rec;
    }

    internal static void WriteOfficialSources(Utf8JsonWriter w, MusicOfficialSourcesRecord s)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", s.Revision);
        WriteStringArray(w, "enabled", s.Enabled);
        w.WriteEndObject();
    }

    internal static void WriteMetadataPolicy(Utf8JsonWriter w, MusicMetadataPolicyRecord p)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", p.Revision);
        WriteStringArray(w, "platforms", p.Platforms);
        WriteStringArray(w, "enabled", p.Enabled);
        w.WriteEndObject();
    }

    internal static void WriteSearchKind(Utf8JsonWriter w, MusicSearchKindRecord k)
    {
        w.WriteStartObject();
        WriteStringArray(w, "order", k.Order);
        WriteStringArray(w, "enabled", k.Enabled);
        w.WriteNumber("pages", k.Pages);
        w.WriteNumber("concurrency", k.Concurrency);
        w.WriteNumber("cacheMinutes", k.CacheMinutes);
        w.WriteEndObject();
    }

    internal static void WriteSearchPolicy(Utf8JsonWriter w, MusicSearchPolicyRecord p, string? onlyKind = null)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", p.Revision);
        if (onlyKind is null or "songs")
        {
            w.WritePropertyName("songs");
            WriteSearchKind(w, p.Songs);
        }
        if (onlyKind is null or "playlists")
        {
            w.WritePropertyName("playlists");
            WriteSearchKind(w, p.Playlists);
        }
        w.WriteEndObject();
    }

    internal static void WriteSearchHistory(Utf8JsonWriter w, MusicSearchHistoryRecord h)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", h.Revision);
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var item in h.Items)
        {
            w.WriteStartObject();
            w.WriteString("text", item.Text);
            w.WriteString("kind", item.Kind);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static void WriteSourceExclusion(Utf8JsonWriter w, MusicSourceExclusionRecord e, bool identityOnly = false)
    {
        w.WriteStartObject();
        w.WriteString("songId", e.SongId);
        w.WriteString("provider", e.Provider);
        w.WriteString("platform", e.Platform);
        if (!identityOnly)
        {
            w.WriteString("providerName", e.ProviderName);
            w.WriteString("title", e.Title);
            w.WriteString("artist", e.Artist);
            w.WriteNumber("at", e.ExcludedAtMs);
        }
        w.WriteEndObject();
    }

    // ---- Lyrics ----

    internal static void WriteLyricLines(Utf8JsonWriter w, IReadOnlyList<MusicLyricLineRecord> lines)
    {
        w.WriteStartArray();
        foreach (var line in lines)
        {
            w.WriteStartObject();
            w.WriteNumber("time", line.TimeSec);
            if (line.EndSec is { } end) w.WriteNumber("end", end);
            w.WriteString("text", line.Text);
            WriteOptionalString(w, "translation", line.Translation);
            WriteOptionalString(w, "romanization", line.Romanization);
            w.WritePropertyName("words");
            w.WriteStartArray();
            foreach (var word in line.Words)
            {
                w.WriteStartObject();
                w.WriteNumber("time", word.TimeSec);
                w.WriteNumber("duration", word.DurationSec);
                w.WriteString("text", word.Text);
                if (word.OffsetSec is { } off) w.WriteNumber("offset", off);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    internal static void WriteCachedLyricLines(Utf8JsonWriter w, IReadOnlyList<CachedLyricLine> lines)
    {
        w.WriteStartArray();
        foreach (var line in lines)
        {
            w.WriteStartObject();
            w.WriteNumber("time", line.TimeSec);
            if (line.EndSec is { } end) w.WriteNumber("end", end);
            w.WriteString("text", line.Text);
            WriteOptionalString(w, "translation", line.Translation);
            WriteOptionalString(w, "romanization", line.Romanization);
            if (line.Words is { Count: > 0 } words)
            {
                w.WritePropertyName("words");
                w.WriteStartArray();
                foreach (var word in words)
                {
                    w.WriteStartObject();
                    w.WriteNumber("time", word.TimeSec);
                    w.WriteNumber("duration", word.DurationSec);
                    w.WriteString("text", word.Text);
                    if (word.OffsetSec is { } off) w.WriteNumber("offset", off);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // ---- Sync ----

    internal static void WriteSyncView(Utf8JsonWriter w, MusicLibrarySyncStateRecord s)
    {
        w.WriteBoolean("ok", true);
        w.WriteNumber("revision", s.Revision);
        w.WritePropertyName("platforms");
        w.WriteStartObject();
        foreach (string plat in MusicLibraryOwner.SyncPlatforms)
        {
            var p = s.Platforms.TryGetValue(plat, out var found) ? found : new MusicSyncPlatformRecord(false, false, "");
            w.WritePropertyName(plat);
            w.WriteStartObject();
            w.WriteBoolean("favorites", p.Favorites);
            w.WriteBoolean("playlists", p.Playlists);
            w.WriteString("accountId", p.AccountId);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WritePropertyName("status");
        w.WriteStartObject();
        w.WriteString("phase", s.StatusPhase);
        w.WriteString("code", s.StatusCode);
        w.WriteNumber("at", s.StatusAtMs);
        w.WritePropertyName("issues");
        w.WriteStartArray();
        foreach (var issue in s.Issues)
        {
            w.WriteStartObject();
            w.WriteString("platform", issue.Platform);
            w.WriteString("collectionId", issue.CollectionId);
            w.WriteString("code", issue.Code);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WritePropertyName("confirmation");
        WriteSyncConfirmation(w, s.Confirmation);
        w.WritePropertyName("pending");
        if (s.Pending is { } pending)
        {
            w.WriteStartObject();
            w.WriteString("platform", pending.Platform);
            w.WriteString("collectionId", pending.CollectionId);
            w.WriteString("operation", pending.Operation);
            w.WriteEndObject();
        }
        else w.WriteNullValue();
    }

    internal static void WriteSyncConfirmation(Utf8JsonWriter w, MusicSyncConfirmationRecord? conf)
    {
        if (conf is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        w.WriteString("token", conf.Token);
        w.WriteString("code", conf.Code);
        w.WriteNumber("count", conf.Count);
        w.WriteEndObject();
    }

    // ---- Playback cache wire codecs ----

    internal static JsonElement PlaybackSourceToElement(PlaybackSourceEntry e, string songId) => BuildObject(w =>
    {
        if (e.Ok is { } ok) w.WriteBoolean("ok", ok);
        WriteOptionalString(w, "kind", e.Kind);
        WriteOptionalString(w, "platform", e.Platform);
        w.WriteString("songId", songId);
        WriteOptionalString(w, "sourceKey", e.SourceKey);
        WriteOptionalString(w, "scope", e.Scope);
        if (e.Generation is { } gen) w.WriteNumber("generation", gen);
        WriteOptionalString(w, "quality", e.Quality);
        WriteOptionalString(w, "requestedQuality", e.RequestedQuality);
        WriteOptionalString(w, "actualQuality", e.ActualQuality);
        if (e.QualityVerified is { } qv) w.WriteBoolean("qualityVerified", qv);
        if (e.PlaybackVerified is { } pv) w.WriteBoolean("playbackVerified", pv);
        WriteOptionalString(w, "qualityPolicySignature", e.QualityPolicySignature);
        WriteOptionalString(w, "qualityCacheSignature", e.QualityCacheSignature);
        WriteOptionalString(w, "url", e.Url);
        w.WritePropertyName("headers");
        w.WriteStartObject();
        w.WriteEndObject();
        if (e.ExpiresAt is { } exp) w.WriteNumber("expiresAt", exp); else w.WriteNull("expiresAt");
        if (e.Duration is { } dur) w.WriteNumber("duration", dur);
        if (e.Size is { } sz) w.WriteNumber("size", sz);
        if (e.Full is { } full) w.WriteBoolean("full", full);
        WriteOptionalString(w, "delivery", e.Delivery);
    });

    internal static JsonElement AudioFileInfoToElement(AudioFileInfoRecord r) => BuildObject(w =>
    {
        WriteOptionalString(w, "kind", r.Kind);
        WriteOptionalString(w, "platform", r.Platform);
        WriteOptionalString(w, "songId", r.SongId);
        WriteOptionalString(w, "sourceKey", r.SourceKey);
        WriteOptionalString(w, "scope", r.Scope);
        if (r.Generation is { } gen) w.WriteNumber("generation", gen);
        WriteOptionalString(w, "installationId", r.InstallationId);
        WriteOptionalString(w, "providerName", r.ProviderName);
        WriteOptionalString(w, "quality", r.Quality);
        WriteOptionalString(w, "requestedQuality", r.RequestedQuality);
        WriteOptionalString(w, "actualQuality", r.ActualQuality);
        if (r.QualityVerified is { } qv) w.WriteBoolean("qualityVerified", qv);
        if (r.PlaybackVerified is { } pv) w.WriteBoolean("playbackVerified", pv);
        WriteOptionalString(w, "qualityPolicySignature", r.QualityPolicySignature);
        WriteOptionalString(w, "qualityCacheSignature", r.QualityCacheSignature);
        if (r.Duration is { } dur) w.WriteNumber("duration", dur);
        if (r.Size is { } sz) w.WriteNumber("size", sz);
        if (r.Full is { } full) w.WriteBoolean("full", full);
        WriteOptionalString(w, "delivery", r.Delivery);
        if (r.QualityEvidence is not null) w.WriteString("qualityEvidence", r.QualityEvidence); else w.WriteNull("qualityEvidence");
        if (r.Codec is not null) w.WriteString("codec", r.Codec); else w.WriteNull("codec");
        if (r.SampleRate is { } sr) w.WriteNumber("sampleRate", sr); else w.WriteNull("sampleRate");
        if (r.BitDepth is { } bd) w.WriteNumber("bitDepth", bd); else w.WriteNull("bitDepth");
        if (r.Channels is { } ch) w.WriteNumber("channels", ch); else w.WriteNull("channels");
        if (r.Bitrate is { } br) w.WriteNumber("bitrate", br); else w.WriteNull("bitrate");
    });

    internal static AudioFileReceipt AudioFileReceiptFromElement(JsonElement el, double expectedDuration)
    {
        bool isScript = TryGet(el, "kind", out var kEl) && kEl.ValueKind == JsonValueKind.String && kEl.GetString() == "script";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGet(el, "headers", out var hEl) && hEl.ValueKind == JsonValueKind.Object)
            foreach (var p in hEl.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } hv)
                    headers[p.Name] = hv;

        long? OptInt64(string key) => TryGet(el, key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
        bool? OptBool(string key) => TryGet(el, key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        var info = new AudioFileInfoRecord(
            OptionalStr(el, "kind"),
            OptionalStr(el, "platform"),
            OptionalStr(el, "songId"),
            OptionalStr(el, "sourceKey"),
            OptionalStr(el, "scope"),
            OptInt64("generation"),
            OptionalStr(el, "installationId"),
            OptionalStr(el, "providerName"),
            OptionalStr(el, "quality"),
            OptionalStr(el, "requestedQuality"),
            OptionalStr(el, "actualQuality"),
            OptBool("qualityVerified"),
            OptBool("playbackVerified"),
            OptionalStr(el, "qualityPolicySignature"),
            OptionalStr(el, "qualityCacheSignature"),
            expectedDuration,
            OptInt64("size") ?? 0L,
            OptBool("full"),
            OptionalStr(el, "delivery"),
            OptionalStr(el, "qualityEvidence"),
            OptionalStr(el, "codec"),
            OptInt64("sampleRate"),
            OptInt64("bitDepth"),
            OptInt64("channels"),
            OptInt64("bitrate"));

        return new AudioFileReceipt(
            Ok: OptBool("ok") ?? isScript,
            Url: OptionalStr(el, "url") ?? "",
            Headers: headers,
            ExpiresAt: OptInt64("expiresAt"),
            Info: info);
    }
}
