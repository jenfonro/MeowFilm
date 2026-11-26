using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>
/// External Node HTTP/JSON wire codec and Debug raw projection for music songs and sources.
/// </summary>
internal static class MusicNodeSongCodec
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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

    internal static string HashJsonObject(Action<Utf8JsonWriter> writeFields)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writeFields(writer);
            writer.WriteEndObject();
            writer.Flush();
        }
        return MusicRules.HashUtf8(buffer.WrittenSpan);
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

    internal static MusicSongRecord ReadSong(JsonElement input, string errorCode = "INVALID_SONG")
    {
        MusicRules.Require(input.ValueKind == JsonValueKind.Object, errorCode);
        if (!input.TryGetProperty("sources", out var sourcesEl) || sourcesEl.ValueKind != JsonValueKind.Array)
            throw new MusicLibraryException("INVALID_SOURCE");
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
            sourceKey, platform, infoSource, songMid, mid, nativeId,
            OptionalStr(mi, "hash") is { Length: > 0 } h ? h : null,
            OptionalStr(mi, "name"), OptionalStr(mi, "singer"), OptionalStr(mi, "albumName"),
            duration, OptionalStr(mi, "img"), qualityKnown, artists,
            extraEntries is null ? null : StorageValue.FromObject(extraEntries));
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
        if (src.Duration is { } dur) w.WriteNumber("duration", clampNonNegativeDuration ? Math.Max(0, dur) : dur);
        else w.WriteNull("duration");
        WriteOptionalString(w, "img", src.Img);
        if (src.QualityKnown is { } qk) w.WriteBoolean("qualityKnown", qk);
        if (src.ArtistNames.Length > 0) WriteStringArray(w, "artistNames", src.ArtistNames);
        if (src.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } extraFields })
            foreach (var field in extraFields)
            {
                w.WritePropertyName(field.Name);
                StorageValueJson.WriteJson(w, field.Value);
            }
        w.WriteEndObject();
    }

    internal static JsonElement MusicInfoToElement(MusicSongSourceRecord src, bool clampNonNegativeDuration = false) =>
        Build(w => WriteMusicInfo(w, src, clampNonNegativeDuration));

    private static MusicSongSourceRecord ReadSource(JsonElement input, string errorCode = "INVALID_SOURCE")
    {
        MusicRules.Require(input.ValueKind == JsonValueKind.Object, errorCode);
        if (!input.TryGetProperty("musicInfo", out var mi) || mi.ValueKind != JsonValueKind.Object)
            throw new MusicLibraryException(errorCode);
        return ReadSourceFromMusicInfo(Str(input, "sourceKey"), Str(input, "source"), mi);
    }

    private static string? ReadIdField(JsonElement obj, string key, out bool needsTypedExtra)
    {
        needsTypedExtra = false;
        if (!obj.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        needsTypedExtra = true;
        return el.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? el.GetRawText() : null;
    }

    private static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value)) return true;
        value = default;
        return false;
    }

    private static string Str(JsonElement obj, string key, string fallback = "") =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    private static string? OptionalStr(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double Num(JsonElement value, double fallback = 0) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d
        : fallback;

    private static string[] StringArray(JsonElement arr)
    {
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list.ToArray();
    }
}
