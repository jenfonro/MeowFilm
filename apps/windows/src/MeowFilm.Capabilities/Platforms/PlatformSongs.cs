// SPDX-License-Identifier: GPL-3.0-only
// Metadata maps: iWebPlus Apache-2.0. Identity: music-core/song-identity.js (Apache-2.0).
// Native translation, no runtime import of JS. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

internal static partial class PlatformSongs
{
    private static bool Positive(JsonNode? n) => double.TryParse(PJ.Scalar(n), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0;
    internal static string IdentityHash(string value)
    {
        uint a = 2166136261, b = 3339675911;
        unchecked { foreach (var c in value) { a = (a ^ c) * 16777619; b = (b ^ c) * 2246822519; } }
        return a.ToString("x8", CultureInfo.InvariantCulture) + b.ToString("x8", CultureInfo.InvariantCulture);
    }
    private static JsonObject Song(string platform, string id, string title, string artist, string album,
        double duration, string cover, JsonObject? native = null)
    {
        PJ.Require(double.IsFinite(duration) && duration >= 0, "INVALID_RESPONSE");
        title = title.Length == 0 ? "未知歌曲" : title;
        var originalArtist = artist;
        artist = artist.Length == 0 ? "未知歌手" : artist;
        // Match music-core's durable metadata policy: never archive signed/query URLs.
        if (cover.Length > 0 && (!Uri.TryCreate(cover, UriKind.Absolute, out var artwork) ||
            artwork.Query.Length > 0 || artwork.Fragment.Length > 0 ||
            artwork.Host is "local" or "localhost" || artwork.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            artwork.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))) cover = "";
        native ??= new JsonObject();
        foreach (var (key, value) in new[] { ("source", platform), ("musicId", id), ("songmid", id),
            ("name", title), ("singer", originalArtist), ("album", album), ("albumName", album), ("img", cover) }) native[key] = value;
        native["duration"] = duration;
        if (cover.Length == 0) native.Remove("img");
        return NativeMusicSong.CreateOnline(platform, native, title, artist, album, duration, cover);
    }
    private static string M(JsonNode? value) => PJ.Metadata(value);
    // Discovery returns public CDN thumbnails with rendering parameters. Store the
    // same original artwork, never a signed/query URL or a guessed alternate host.
    private static string WyCover(JsonNode? value)
    {
        var address = PJ.Image(value);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Query.Length == 0) return address;
        if (uri.Fragment.Length > 0 || !uri.IsDefaultPort ||
            !Regex.IsMatch(uri.Host, @"^p[1-4]\.music\.126\.net$", RegexOptions.CultureInvariant)) return "";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in uri.Query[1..].Split('&'))
        {
            var pair = part.Split('=', 2);
            var key = pair[0]; var content = pair.Length == 2 ? pair[1] : "";
            if (!keys.Add(key)) return "";
            if (!(key switch
            {
                "imageView" => content is "" or "1",
                "type" => content is "webp" or "jpg" or "jpeg" or "png",
                "thumbnail" => Regex.IsMatch(content, @"^[1-9][0-9]{0,3}y[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant),
                _ => false
            })) return "";
        }
        return keys.Contains("imageView") ? uri.GetLeftPart(UriPartial.Path) : "";
    }
    internal static JsonObject Wy(JsonNode raw)
    {
        var s = PJ.First(PJ.At(raw, "songInfo"), PJ.At(raw, "songData"), PJ.At(raw, "song"), raw)!;
        var album = PJ.First(PJ.At(s, "al"), PJ.At(s, "album"));
        var artists = PJ.First(PJ.At(s, "ar"), PJ.At(s, "artists"));
        var artist = artists is JsonArray ? string.Join(" / ", PJ.Rows(artists, 100).Select(a => M(a["name"])))
            : M(PJ.First(artists, PJ.At(s, "artistName"), PJ.At(s, "artist")));
        var native = new JsonObject();
        if (PJ.At(album, "id") is { } aid) native["albumId"] = PJ.Id(aid);
        return Song("wy", PJ.Id(PJ.First(PJ.At(s, "id"), PJ.At(s, "songId"))),
            M(PJ.First(PJ.At(s, "name"), PJ.At(s, "title"))), artist,
            album is JsonValue ? M(album) : M(PJ.At(album, "name")),
            PJ.Number(PJ.First(PJ.At(s, "dt"), PJ.At(s, "duration"))) / 1000,
            WyCover(new[] { PJ.At(album, "picUrl"), PJ.At(s, "picUrl"), PJ.At(s, "coverUrl") }
                .FirstOrDefault(value => PJ.Text(value).Length > 0)), native);
    }
    internal static JsonObject Tx(JsonNode raw)
    {
        var id = PJ.Id(PJ.At(raw, "mid"), "^[A-Za-z0-9]{1,64}$");
        var numeric = PJ.Id(PJ.At(raw, "id"));
        var type = PJ.Count(PJ.At(raw, "type"));
        var album = PJ.At(raw, "album");
        var albumMid = PJ.At(album, "mid") is { } am && PJ.Text(am).Length > 0 ? PJ.Id(am, "^[A-Za-z0-9]{1,64}$") : "";
        var types = new JsonArray();
        foreach (var (quality, field) in new[] { ("128k", "size_128mp3"), ("320k", "size_320mp3"), ("flac", "size_flac"), ("flac24bit", "size_hires") })
            if (Positive(PJ.At(raw, "file", field))) types.Add(PJ.Obj(("type", quality)));
        return Song("tx", id, M(PJ.First(PJ.At(raw, "title"), PJ.At(raw, "name"))),
            string.Join("、", PJ.Rows(PJ.At(raw, "singer") ?? new JsonArray(), 100).Select(a => M(a["name"]))),
            M(PJ.At(album, "name")), PJ.Number(PJ.At(raw, "interval")),
            albumMid.Length == 0 ? "" : $"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg",
            PJ.Obj(("qqSongId", numeric), ("songId", numeric), ("songType", type), ("albumId", albumMid), ("albumMid", albumMid),
                ("strMediaMid", M(PJ.At(raw, "file", "media_mid"))), ("types", types)));
    }
    internal static string KgHash(JsonNode? value, string code = "INVALID_RESPONSE")
    {
        var hash = PJ.Id(value, "^[A-Fa-f0-9]{32}$", code).ToUpperInvariant();
        PJ.Require(hash.Any(c => c != '0'), code);
        return hash;
    }
    internal static JsonObject Kg(JsonNode raw)
    {
        var audio = PJ.At(raw, "audio_info");
        var album = PJ.First(PJ.At(raw, "albuminfo"), PJ.At(raw, "album_info"));
        var id = KgHash(PJ.First(PJ.At(raw, "hash"), PJ.At(raw, "FileHash"), PJ.At(audio, "hash")));
        var full = Regex.Replace(M(PJ.First(PJ.At(raw, "name"), PJ.At(raw, "FileName"))), @"\.(mp3|flac|m4a|wav|ape|ogg|aac|wma)$", "", RegexOptions.IgnoreCase);
        var at = full.IndexOf(" - ", StringComparison.Ordinal);
        var singers = PJ.First(PJ.At(raw, "singerinfo"), PJ.At(raw, "Singers"));
        var artist = singers is JsonArray ? string.Join("、", PJ.Rows(singers, 100).Select(a => M(PJ.First(a["name"], a["author_name"]))))
            : M(PJ.First(PJ.At(raw, "SingerName"), PJ.At(raw, "author_name"), PJ.At(raw, "base", "author_name")));
        if (artist.Length == 0 && at >= 0) artist = full[..at];
        var title = PJ.S(raw, "OriSongName") is { Length: > 0 } name ? name + (PJ.S(raw, "Suffix") is { Length: > 0 } suffix ? " " + suffix : "")
            : M(PJ.First(PJ.At(raw, "songname"), PJ.At(raw, "audio_name"), PJ.At(raw, "base", "audio_name")));
        if (title.Length == 0) title = at < 0 ? full : full[(at + 3)..];
        // Public search legitimately returns "" for an absent album; hash remains
        // mandatory. Do not discard the entire page or manufacture a release ID.
        static string OptionalId(params JsonNode?[] values) =>
            PJ.Id(values.FirstOrDefault(value => value is not null &&
                !(value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length == 0))
                ?? JsonValue.Create("0"));
        var albumId = OptionalId(PJ.At(raw, "album_id"), PJ.At(raw, "AlbumID"), PJ.At(album, "id"), PJ.At(album, "album_id"));
        var mixId = OptionalId(PJ.At(raw, "mixsongid"), PJ.At(raw, "add_mixsongid"), PJ.At(raw, "album_audio_id"), PJ.At(raw, "MixSongID"));
        var types = new JsonArray(); var nativeTypes = new JsonObject();
        foreach (var (quality, h, size) in new (string, JsonNode?, JsonNode?)[] {
            ("128k", JsonValue.Create(id), PJ.First(PJ.At(raw,"filesize"),PJ.At(raw,"FileSize"),PJ.At(raw,"size"),PJ.At(audio,"filesize"))),
            ("320k",PJ.First(PJ.At(raw,"hash_320"),PJ.At(raw,"HQFileHash"),PJ.At(audio,"hash_320")),PJ.First(PJ.At(raw,"filesize_320"),PJ.At(raw,"HQFileSize"),PJ.At(audio,"filesize_320"))),
            ("flac",PJ.First(PJ.At(raw,"hash_flac"),PJ.At(raw,"SQFileHash"),PJ.At(audio,"hash_flac")),PJ.First(PJ.At(raw,"filesize_flac"),PJ.At(raw,"SQFileSize"),PJ.At(audio,"filesize_flac"))),
            ("flac24bit",PJ.First(PJ.At(raw,"hash_high"),PJ.At(raw,"ResFileHash"),PJ.At(audio,"hash_high")),PJ.First(PJ.At(raw,"filesize_high"),PJ.At(raw,"ResFileSize"),PJ.At(audio,"filesize_high"))) })
        {
            if (!Positive(size) || !Regex.IsMatch(PJ.Text(h), "^[A-Fa-f0-9]{32}$") || PJ.Text(h).All(c => c == '0')) continue;
            var hash = KgHash(h); var sizeText = PJ.Scalar(size);
            types.Add(PJ.Obj(("type", quality), ("hash", hash), ("size", sizeText)));
            nativeTypes[quality] = PJ.Obj(("hash", hash), ("size", sizeText));
        }
        return Song("kg", id, title, artist, M(PJ.First(PJ.At(raw, "AlbumName"), PJ.At(album, "name"), PJ.At(album, "album_name"))),
            PJ.At(raw, "Duration") is { } d ? PJ.Number(d) : PJ.Number(PJ.First(PJ.At(raw, "timelen"), PJ.At(raw, "timelength"), PJ.At(audio, "timelength"))) / 1000,
            PJ.Image(PJ.First(PJ.At(raw, "cover"), PJ.At(raw, "Image"), PJ.At(raw, "trans_param", "union_cover"))),
            PJ.Obj(("hash", id), ("albumId", albumId), ("album_id", albumId), ("albumAudioId", mixId), ("types", types), ("_types", nativeTypes)));
    }
    internal static JsonObject Playlist(string platform, JsonNode raw)
    {
        JsonNode? Pick(params string[] keys) => PJ.First(keys.Select(k => PJ.At(raw, k)).ToArray());
        var id = platform switch {
            "wy" => PJ.Id(Pick("id")), "tx" => PJ.Id(Pick("id", "tid", "dissid")),
            _ => PJ.Id(Pick("global_collection_id"), @"^[\w-]{1,200}$") };
        PJ.Require(platform != "kg" || !id.StartsWith("id_", StringComparison.Ordinal), "IDENTITY_MISMATCH");
        return PJ.Obj(("id", id), ("name", M(platform == "wy" ? Pick("name", "title") : platform == "tx" ? Pick("title", "dissname", "name", "dirName") : Pick("specialname", "name"))),
            ("cover", PJ.Image(platform == "wy" ? Pick("picUrl", "coverImgUrl", "cover") : platform == "tx" ? Pick("picurl", "cover", "logo", "picUrl") : Pick("imgurl", "cover"))),
            ("count", PJ.OptionalCount(platform == "wy" ? Pick("trackCount", "songCount") : platform == "tx" ? Pick("songnum", "songNum", "song_cnt") : Pick("songcount", "count"))),
            ("creator", M(platform == "wy" ? PJ.At(raw, "creator", "nickname") : Pick("nickname", "creator_nick"))),
            ("source", platform), ("engine", platform == "wy" ? "Netease" : platform == "tx" ? "QQ" : "Kugou"));
    }
}
