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

    private static readonly HashSet<string> AllowedExtraScalarFields = new(StringComparer.Ordinal)
    {
        "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId", "strMediaMid",
        "albumId", "album_id", "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId",
        "contentId", "md5", "androidMd5", "size"
    };

    private static readonly HashSet<string> AllowedExtraStructuredFields = new(StringComparer.Ordinal)
    {
        "types", "_types", "audioFormats", "newRateFormats"
    };

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

    internal static MusicSongSourceRecord DurableSource(MusicSongSourceRecord source)
    {
        List<StorageValueField>? cleanedExtras = null;
        if (source.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } fields })
        {
            foreach (var field in fields)
            {
                if (AllowedExtraScalarFields.Contains(field.Name) && IsScalar(field.Value))
                {
                    cleanedExtras ??= [];
                    cleanedExtras.Add(field);
                }
                else if (AllowedExtraStructuredFields.Contains(field.Name) && field.Value.Kind != StorageValueKind.Null)
                {
                    cleanedExtras ??= [];
                    cleanedExtras.Add(field);
                }
            }
        }
        return source with
        {
            Duration = ValidDuration(source.Duration),
            Img = PublicUrl(source.Img),
            WireExtras = cleanedExtras is null ? null : StorageValue.FromObject(cleanedExtras)
        };
    }

    private static bool IsScalar(StorageValue value) =>
        value.Kind is StorageValueKind.Boolean or StorageValueKind.Integer or StorageValueKind.Real or StorageValueKind.ExactNumber or StorageValueKind.Text;

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
