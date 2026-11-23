using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native strongly-typed projections of the shared song model. Permanent identity is assigned by the repository.</summary>
internal static partial class NativeMusicSong
{
    internal static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const string Whitespace = @"[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+";

    private static readonly HashSet<string> ExtraTextFields = new(StringComparer.Ordinal)
    {
        "md5", "androidMd5"
    };

    private static readonly HashSet<string> ExtraIdentifierFields = new(StringComparer.Ordinal)
    {
        "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId", "strMediaMid",
        "albumId", "album_id", "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId", "size", "contentId"
    };

    private static readonly string[] FormatTextFields = ["hash", "md5", "androidMd5"];

    private static readonly string[] FormatIdentifierFields =
    [
        "formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
        "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
        "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel",
        "fileId", "contentId"
    ];

    internal static string NormalText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var english = new string(normalized.Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch).ToArray());
        return Regex.Replace(english, Whitespace, " ", RegexOptions.CultureInvariant, RegexTimeout).Trim(' ');
    }

    // Cache/observation keys only. This function never assigns a song ID.
    internal static string Hash(string value)
    {
        uint a = 2166136261, b = 3339675911;
        foreach (char ch in value) { a = unchecked((a ^ ch) * 16777619); b = unchecked((b ^ ch) * 2246822519); }
        return a.ToString("x8", CultureInfo.InvariantCulture) + b.ToString("x8", CultureInfo.InvariantCulture);
    }

    internal static double? ValidDuration(double? value) =>
        value is { } d && double.IsFinite(d) && d > 0 ? d : null;

    internal static MusicSongSourceRecord? Primary(MusicSongRecord song) =>
        song.Sources.Length > 0 ? song.Sources[0] : null;

    internal static string InfoId(MusicSongSourceRecord source) =>
        source.SongMid ?? StorageValueJson.ScalarText(StorageValueJson.Field(source.WireExtras, "songmid")) ?? "";

    internal static string InfoKey(MusicSongSourceRecord source)
    {
        string platform = source.InfoSource;
        string id = InfoId(source);
        if (id.Length == 0) return "";
        string variant = platform == "kg"
            ? (source.Hash ?? StorageValueJson.ScalarText(StorageValueJson.Field(source.WireExtras, "albumAudioId")) ?? "").ToLowerInvariant()
            : "";
        return MusicRules.CanonicalArray(platform, id, variant);
    }

    internal static string SourceKey(MusicSongRecord song) =>
        Primary(song)?.SourceKey ?? "";

    internal static (string Title, string Artist, string Album, double Duration, string Cover) Display(MusicSongRecord song) =>
        (song.Title, song.Artist, song.Album, song.Duration ?? 0, song.CoverUrl);

    internal static (string Title, string Artist, string Album, double Duration, string Cover) Display(MusicSongSourceRecord source) =>
        (source.Name ?? "", source.Singer ?? "", source.AlbumName ?? "", source.Duration ?? 0, source.Img ?? "");

    internal static string PublicUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";
    }

    private static bool IsText(StorageValue value) => value.Kind == StorageValueKind.Text;

    private static bool IsIdentifier(StorageValue value) => value.Kind switch
    {
        StorageValueKind.Text => true,
        StorageValueKind.Integer => true,
        StorageValueKind.Real => double.IsFinite(value.RealValue),
        StorageValueKind.ExactNumber => double.TryParse(value.TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number),
        _ => false
    };

    private static void CopyText(List<StorageValueField> target, StorageValue source, string field)
    {
        if (StorageValueJson.Field(source, field) is { } value && IsText(value))
            target.Add(new StorageValueField(field, value));
    }

    private static void CopyIdentifier(List<StorageValueField> target, StorageValue source, string field)
    {
        if (StorageValueJson.Field(source, field) is { } value && IsIdentifier(value))
            target.Add(new StorageValueField(field, value));
    }

    private static StorageValue CleanFormatRow(StorageValue row)
    {
        var clean = new List<StorageValueField>();
        foreach (string field in FormatIdentifierFields) CopyIdentifier(clean, row, field);
        foreach (string field in FormatTextFields) CopyText(clean, row, field);
        return StorageValue.FromObject(clean);
    }

    private static StorageValue CleanTypes(StorageValue rawTypes)
    {
        var types = new List<StorageValue>();
        if (rawTypes.ArrayItems is { } items)
        {
            foreach (var item in items)
            {
                if (item.Kind == StorageValueKind.Text)
                {
                    types.Add(StorageValue.FromObject([new StorageValueField("type", item)]));
                }
                else if (item.Kind == StorageValueKind.Object)
                {
                    var clean = new List<StorageValueField>();
                    CopyIdentifier(clean, item, "type");
                    CopyText(clean, item, "hash");
                    CopyIdentifier(clean, item, "size");
                    types.Add(StorageValue.FromObject(clean));
                }
            }
        }
        return StorageValue.FromArray(types);
    }

    private static StorageValue CleanTypeMap(StorageValue rawTypeMap)
    {
        var typeMap = new List<StorageValueField>();
        if (rawTypeMap.ObjectFields is { } pairs)
        {
            foreach (var pair in pairs)
            {
                var clean = new List<StorageValueField>();
                if (pair.Value.Kind == StorageValueKind.Object)
                {
                    CopyText(clean, pair.Value, "hash");
                    CopyIdentifier(clean, pair.Value, "size");
                }
                typeMap.Add(new StorageValueField(pair.Name, StorageValue.FromObject(clean)));
            }
        }
        return StorageValue.FromObject(typeMap);
    }

    private static StorageValue CleanFormatRows(StorageValue rawRows)
    {
        var cleaned = new List<StorageValue>();
        if (rawRows.ArrayItems is { } rows)
        {
            foreach (var row in rows)
                if (row.Kind == StorageValueKind.Object)
                    cleaned.Add(CleanFormatRow(row));
        }
        return StorageValue.FromArray(cleaned);
    }

    internal static MusicSongSourceRecord DurableSource(MusicSongSourceRecord source)
    {
        string? songMid = source.SongMid;
        string? mid = source.Mid;
        string? nativeId = source.NativeId;
        List<StorageValueField>? cleanedExtras = null;
        if (source.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } fields })
        {
            foreach (var field in fields)
            {
                if (ExtraTextFields.Contains(field.Name))
                {
                    if (IsText(field.Value))
                    {
                        cleanedExtras ??= [];
                        cleanedExtras.Add(field);
                    }
                }
                else if (ExtraIdentifierFields.Contains(field.Name))
                {
                    if (IsIdentifier(field.Value))
                    {
                        cleanedExtras ??= [];
                        cleanedExtras.Add(field);
                    }
                    else if (field.Name == "songmid") songMid = null;
                    else if (field.Name == "mid") mid = null;
                    else if (field.Name == "id") nativeId = null;
                }
                else if (field.Name == "types" && field.Value.Kind == StorageValueKind.Array)
                {
                    cleanedExtras ??= [];
                    cleanedExtras.Add(new StorageValueField("types", CleanTypes(field.Value)));
                }
                else if (field.Name == "_types" && field.Value.Kind == StorageValueKind.Object)
                {
                    cleanedExtras ??= [];
                    cleanedExtras.Add(new StorageValueField("_types", CleanTypeMap(field.Value)));
                }
                else if (field.Name is "audioFormats" or "newRateFormats" && field.Value.Kind == StorageValueKind.Array)
                {
                    cleanedExtras ??= [];
                    cleanedExtras.Add(new StorageValueField(field.Name, CleanFormatRows(field.Value)));
                }
            }
        }
        return source with
        {
            SongMid = songMid,
            Mid = mid,
            NativeId = nativeId,
            Duration = ValidDuration(source.Duration),
            Img = PublicUrl(source.Img),
            WireExtras = cleanedExtras is null ? null : StorageValue.FromObject(cleanedExtras)
        };
    }

    internal static bool IsOnline(MusicSongRecord song) => song.Sources.Length > 0;

    internal static MusicSongSourceRecord CreateSource(string platform, MusicSongSourceRecord rawInfo,
        string title, string artist, string album, double duration, string cover)
    {
        var updated = DurableSource(rawInfo with
        {
            Platform = platform,
            InfoSource = platform,
            Name = title,
            Singer = artist,
            AlbumName = album,
            Duration = duration > 0 ? duration : null,
            Img = cover
        });
        return updated with { SourceKey = InfoKey(updated) };
    }

    internal static MusicSongRecord CreateOnline(string platform, MusicSongSourceRecord rawInfo,
        string title, string artist, string album, double duration, string cover)
    {
        var source = CreateSource(platform, rawInfo, title, artist, album, duration, cover);
        double? validDuration = duration > 0 ? duration : null;
        return Normalize(new MusicSongRecord(
            "",
            title,
            artist,
            album,
            validDuration,
            validDuration,
            PublicUrl(cover),
            null,
            null,
            null,
            rawInfo.ArtistNames,
            [source]));
    }

    internal static MusicSongRecord CreateLocal(string fileId, string title, string artist = "", string album = "",
        double duration = 0, string cover = "") => Normalize(new MusicSongRecord(
            "",
            title,
            artist,
            album,
            ValidDuration(duration),
            ValidDuration(duration),
            PublicUrl(cover),
            fileId,
            null,
            null,
            [],
            []));

    internal static MusicSongRecord Normalize(MusicSongRecord raw)
    {
        double? duration = ValidDuration(raw.Duration);
        double? baseline = ValidDuration(raw.BaselineDuration) ?? duration;
        var sources = new MusicSongSourceRecord[raw.Sources.Length];
        for (int i = 0; i < raw.Sources.Length; i++)
            sources[i] = DurableSource(raw.Sources[i]);
        return new MusicSongRecord(
            raw.SongId,
            raw.Title,
            raw.Artist,
            raw.Album,
            duration,
            baseline,
            PublicUrl(raw.CoverUrl),
            raw.LocalFileId,
            raw.ArtworkSourceKey,
            raw.ArtworkSourceImage,
            raw.ArtistNames,
            sources);
    }

    internal static bool HasRecordingQuality(MusicSongSourceRecord source, string quality)
    {
        var types = StorageValueJson.Field(source.WireExtras, "types");
        bool unknown = types is null && source.QualityKnown != true;
        if (unknown) return true;
        if (types is not { Kind: StorageValueKind.Array, ArrayItems: { } items }) return false;
        foreach (var item in items)
            if (StorageValueJson.ScalarText(StorageValueJson.Field(item, "type")) == quality)
                return true;
        return false;
    }
}
