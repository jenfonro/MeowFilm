// SPDX-License-Identifier: GPL-3.0-only
// Native translations of the fixed API recipes and iWebPlus recording checks; see LICENSES/NOTICE.md.
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

internal sealed record OfficialAudioResult(Uri Uri, string RequestQuality, string ActualQuality,
    bool QualityVerified, double Duration, long Size, long? ExpiresAt, bool Full);

/// <summary>Only official platform protocols. No LX identity, runtime, URL proxy or alternate-source retry.</summary>
internal sealed class OfficialMusicAudio
{
    private readonly MusicPlatformHttp http;
    private readonly MusicPlatformClientContextOwner clients;
    internal OfficialMusicAudio(MusicPlatformHttp http, MusicPlatformClientContextOwner clients)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
    }
    private sealed class Call(OfficialMusicAudio owner, MusicPlatformSession account,
        MusicPlatformClientContext? client, Action accountGuard)
    {
        private MusicPlatformClientContext? current = client;
        internal MusicWyClientContext WyClient => (MusicWyClientContext)current!;
        internal MusicKgClientContext KgClient => (MusicKgClientContext)current!;
        private void Guard(CancellationToken token)
        {
            accountGuard(); token.ThrowIfCancellationRequested();
            if (current is not null)
                PJ.Require(owner.clients.IsCurrent(current), owner.clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
        }
        internal async Task<(JsonObject Body, IReadOnlyList<string> Cookies)> Send(
            HttpRequestMessage request, CancellationToken token, bool xeapi = false)
        {
            try { Guard(token); } catch { request.Dispose(); throw; }
            var result = xeapi
                ? await owner.http.SendXeapiAsync(request, token, 1024 * 1024, TimeSpan.FromSeconds(20)).ConfigureAwait(false)
                : await owner.http.SendAsync(account.Platform, request, token).ConfigureAwait(false);
            Guard(token);
            var body = account.Platform switch {
                "wy" => PlatformProtocols.WySuccess(result.Body), "tx" => PlatformProtocols.TxSuccess(result.Body),
                _ => PlatformProtocols.KgSuccess(result.Body, request.RequestUri!.AbsolutePath)
            };
            return (body, result.SetCookieHeaders);
        }
        internal async Task AcceptWy(IReadOnlyList<string> cookies, CancellationToken token)
        {
            Guard(token);
            current = await owner.clients.AcceptWyAsync(WyClient, account.Cookie,
                cookies, () => Guard(token), token).ConfigureAwait(false);
            Guard(token);
        }
    }
    internal async Task<OfficialAudioResult> ResolveAsync(MusicPlatformSession session, JsonObject data,
        IReadOnlyList<string> qualities, MusicAccountEntitlements rights, CancellationToken token, Action accountGuard)
    {
        bool member = !rights.VerificationDeferred && rights.Membership.Vip == true &&
            (rights.Membership.ExpiresAt is null || rights.Membership.ExpiresAt > Now);
        if (!member)
        {
            PJ.Require(session.Platform == "wy" && qualities.Contains("128k"), "VIP_REQUIRED");
            qualities = ["128k"];
        }
        accountGuard();
        var client = session.Platform is "wy" or "kg"
            ? await clients.AcquireAsync(session.Platform, token).ConfigureAwait(false) : null;
        var c = new Call(this, session, client, accountGuard);
        return session.Platform switch {
            "wy" => await Wy(c, session, data, qualities, rights, token).ConfigureAwait(false),
            "tx" => await Tx(c, session, data, qualities, token).ConfigureAwait(false),
            "kg" => await Kg(c, session, data, qualities, token).ConfigureAwait(false),
            _ => throw new MusicPlatformException("CAPABILITY_UNAVAILABLE")
        };
    }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static Uri MediaUri(JsonNode? node)
    {
        string text = PJ.Scalar(node);
        PJ.Require(text.Length is > 0 and <= 16384 && !text.Any(char.IsWhiteSpace) && !text.Contains('\\') &&
            Uri.TryCreate(text, UriKind.Absolute, out var _) , "INVALID_RESPONSE");
        var uri = new Uri(text);
        PJ.Require(uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 &&
            uri.Fragment.Length == 0, "INVALID_RESPONSE");
        return uri;
    }
    private static double Number(JsonNode? value) =>
        double.TryParse(PJ.Scalar(value), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : 0;
    private static long Size(JsonNode? value) => Number(value) is > 0 and <= long.MaxValue and var n ? (long)n : 0;
    private static string Id(JsonObject data, string field, string pattern) =>
        PJ.Id(data[field], pattern, "INVALID_REQUEST");

    private async Task<OfficialAudioResult> Wy(Call c, MusicPlatformSession session, JsonObject data,
        IReadOnlyList<string> qualities, MusicAccountEntitlements rights, CancellationToken token)
    {
        string id = Id(data, "songmid", "^[1-9][0-9]{0,19}$");
        if (qualities.Count > 0 && qualities[0] == "128k")
            return await WyStandard(c, session, id, token).ConfigureAwait(false);
        var resourceResponse = await c.Send(PlatformProtocols.Wy("/api/song/music/detail/get",
            PJ.Obj(("songId", id)), session, c.WyClient), token).ConfigureAwait(false);
        var resources = resourceResponse.Body;
        var keys = new Dictionary<string, (string Level, string Resource)> {
            ["128k"] = ("standard", "l"), ["192k"] = ("higher", "m"), ["320k"] = ("exhigh", "h"),
            ["flac"] = ("lossless", "sq"), ["flac24bit"] = ("hires", "hr"), ["master"] = ("jymaster", "jm"),
            ["jyeffect"] = ("jyeffect", "je"), ["dolby"] = ("dolby", "db"), ["sky"] = ("sky", "sk"),
            ["vivid"] = ("vivid", "vi")
        };
        string? quality = qualities.FirstOrDefault(q => keys.TryGetValue(q, out var entry) &&
            (q == "128k" || rights.AudioLevels.Contains(entry.Level)) && PJ.At(resources, "data", entry.Resource) is not null);
        PJ.Require(quality is not null, "QUALITY_UNAVAILABLE");
        await c.AcceptWy(resourceResponse.Cookies, token).ConfigureAwait(false);
        if (quality == "128k") return await WyStandard(c, session, id, token).ConfigureAwait(false);
        string level = keys[quality!].Level;
        var (keyRequest, nonce) = PlatformXeapi.KeyRequest(c.WyClient, session);
        var keyResponse = await c.Send(keyRequest, token).ConfigureAwait(false);
        var key = PlatformXeapi.ReadKey(keyResponse.Body, nonce);
        await c.AcceptWy(keyResponse.Cookies, token).ConfigureAwait(false);
        var parameters = PJ.Obj(("ids", "[" + id + "]"), ("level", level), ("encodeType", level == "vivid" ? "mp3" : "flac"));
        if (level == "sky") parameters["immerseType"] = "c51";
        var mediaResponse = await c.Send(PlatformXeapi.Request("/api/song/enhance/player/url/v1", parameters,
            session, c.WyClient, key), token, xeapi: true).ConfigureAwait(false);
        var response = mediaResponse.Body;
        var rows = PJ.Rows(response["data"], 100).Where(r => PJ.Scalar(r["id"]) == id).ToArray();
        PJ.Require(rows.Length == 1, "IDENTITY_MISMATCH");
        var row = rows[0];
        PJ.Require(row["freeTrialInfo"] is null && (row["code"] is null || PJ.Scalar(row["code"]) == "200"), "PLAYBACK_RESTRICTED");
        var uri = MediaUri(row["url"]);
        string actualLevel = PJ.S(row, "level");
        string actual = keys.FirstOrDefault(p => p.Value.Level == actualLevel).Key ?? "";
        long expiry = Now + (long)(Math.Min(1800, Number(row["expi"])) * 1000);
        PJ.Require(expiry > Now, "MEDIA_EXPIRED");
        var result = new OfficialAudioResult(uri, quality!, actual, actual.Length > 0, Number(row["time"]) / 1000,
            Size(row["size"]), expiry, true);
        await c.AcceptWy(mediaResponse.Cookies, token).ConfigureAwait(false);
        return result;
    }

    // api-enhanced song_url.js at the fixed reference: official EAPI with br.
    // This standard-only route does not require an authenticated XEAPI device.
    private async Task<OfficialAudioResult> WyStandard(Call c, MusicPlatformSession session, string id, CancellationToken token)
    {
        var mediaResponse = await c.Send(PlatformProtocols.Wy("/api/song/enhance/player/url",
            PJ.Obj(("ids", "[" + id + "]"), ("br", 128000)), session, c.WyClient), token).ConfigureAwait(false);
        var response = mediaResponse.Body;
        var rows = PJ.Rows(response["data"], 100).Where(r => PJ.Scalar(r["id"]) == id).ToArray();
        PJ.Require(rows.Length == 1, "IDENTITY_MISMATCH");
        var row = rows[0];
        // freeTrialPrivilege describes eligibility, not the returned media segment.
        // A full response also includes it; freeTrialInfo identifies actual preview media.
        PJ.Require(row["freeTrialInfo"] is null &&
            PJ.Scalar(row["code"]) == "200", "PLAYBACK_RESTRICTED");
        PJ.Require(PJ.Text(row["url"]).Length > 0, "PLAYBACK_RESTRICTED");
        PJ.Require(PJ.S(row, "type") is "mp3" or "aac" or "m4a" or "flac", "PLAYBACK_RESTRICTED");
        var uri = MediaUri(row["url"]);
        string actual = Number(row["br"]) switch { 128000 => "128k", 192000 => "192k", 320000 => "320k", _ => "" };
        long expiry = Now + (long)(Math.Min(1800, Number(row["expi"])) * 1000);
        PJ.Require(expiry > Now, "MEDIA_EXPIRED");
        var result = new OfficialAudioResult(uri, "128k", actual, actual.Length > 0, Number(row["time"]) / 1000,
            Size(row["size"]), expiry, true);
        await c.AcceptWy(mediaResponse.Cookies, token).ConfigureAwait(false);
        return result;
    }

    private async Task<OfficialAudioResult> Tx(Call c, MusicPlatformSession session, JsonObject data,
        IReadOnlyList<string> qualities, CancellationToken token)
    {
        string mid = Id(data, "songmid", "^[A-Za-z0-9]{1,64}$");
        var detail = (await c.Send(PlatformProtocols.Tx("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo",
            PJ.Obj(("ctx", 0), ("client", 1), ("types", new[] { 0 }), ("modify_stamp", new[] { 0 }),
                ("mids", new[] { mid })), session), token).ConfigureAwait(false)).Body;
        var tracks = PJ.Rows(detail["tracks"], 1);
        PJ.Require(tracks.Count == 1 && PJ.S(tracks[0], "mid") == mid, "IDENTITY_MISMATCH");
        var track = tracks[0]; var file = track["file"] as JsonObject ?? new();
        var formats = new Dictionary<string, (string Prefix, string Extension, long Bytes)> {
            ["128k"] = ("M500", "mp3", Size(file["size_128mp3"])),
            ["320k"] = ("M800", "mp3", Size(file["size_320mp3"])),
            ["flac"] = ("F000", "flac", Size(file["size_flac"])),
            ["master"] = ("AI00", "flac", Size((file["size_new"] as JsonArray)?.FirstOrDefault()))
        };
        string? quality = qualities.FirstOrDefault(q => formats.TryGetValue(q, out var f) && f.Bytes > 0);
        PJ.Require(quality is not null, "QUALITY_UNAVAILABLE");
        var format = formats[quality!];
        string filename = format.Prefix + mid + mid + "." + format.Extension;
        var response = (await c.Send(PlatformProtocols.Tx("music.vkey.GetVkey", "UrlGetVkey",
            PJ.Obj(("uin", session.UserId), ("filename", new[] { filename }),
                ("guid", RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("songmid", new[] { mid }), ("songtype", new[] { 0 }), ("ctx", 0)), session), token).ConfigureAwait(false)).Body;
        var rows = PJ.Rows(response["midurlinfo"], 100).Where(r => PJ.S(r, "songmid") == mid).ToArray();
        PJ.Require(rows.Length == 1, "IDENTITY_MISMATCH");
        var row = rows[0]; string returned = PJ.S(row, "filename");
        PJ.Require(PJ.Scalar(row["result"]) == "0" && PJ.S(row, "ekey").Length == 0 &&
            returned == filename && PJ.S(row, "purl").Length > 0, "PLAYBACK_RESTRICTED");
        var uri = MediaUri(JsonValue.Create(new Uri(new Uri("https://isure.stream.qqmusic.qq.com/"), PJ.S(row, "purl")).AbsoluteUri));
        PJ.Require((uri.Host == "stream.qqmusic.qq.com" || uri.Host.EndsWith(".stream.qqmusic.qq.com", StringComparison.Ordinal)) &&
            uri.AbsolutePath.Split('/').Last().StartsWith(format.Prefix, StringComparison.Ordinal), "IDENTITY_MISMATCH");
        long expiry = Now + (long)(Math.Min(120, Number(response["expiration"])) * 1000);
        PJ.Require(expiry > Now, "MEDIA_EXPIRED");
        return new(uri, quality!, "", false, Number(track["interval"]), format.Bytes, expiry, true);
    }

    private async Task<OfficialAudioResult> Kg(Call c, MusicPlatformSession session, JsonObject data,
        IReadOnlyList<string> qualities, CancellationToken token)
    {
        string hash = PlatformSongs.KgHash(data["hash"], "INVALID_REQUEST");
        var (mid, dfid) = PlatformProtocols.KgIdentity(PJ.Cookies(session), c.KgClient);
        long now = Now;
        var body = PJ.Obj(("appid", 1005), ("clientver", 20489), ("clienttime", now),
            ("data", PJ.Arr([PJ.Obj(("hash", hash), ("audio_id", 0))])), ("dfid", dfid),
            ("key", PlatformProtocols.Md5($"1005{PlatformProtocols.KgSalt}20489{now}")), ("mid", mid), ("userid", session.UserId));
        var detail = (await c.Send(PlatformProtocols.Kg("/v1/audio/audio", new(), session, c.KgClient,
            "kmr.service.kugou.com", body), token).ConfigureAwait(false)).Body;
        var tracks = PJ.Rows(detail["data"], 1);
        PJ.Require(tracks.Count == 1, "IDENTITY_MISMATCH");
        var raw = tracks[0]; var audio = raw["audio_info"] as JsonObject ?? new();
        var hashes = new[] { raw["hash"], raw["FileHash"], audio["hash"] }.Where(n => n is not null).ToArray();
        PJ.Require(hashes.Length > 0 && hashes.All(n => PlatformSongs.KgHash(n) == hash), "IDENTITY_MISMATCH");
        var song = PlatformSongs.Kg(raw); var sd = NativeMusicSong.SourceData(song);
        var types = sd["_types"] as JsonObject ?? new();
        var levels = new Dictionary<string, string> { ["128k"] = "128", ["320k"] = "320", ["flac"] = "flac", ["flac24bit"] = "high" };
        string? quality = qualities.FirstOrDefault(q => levels.ContainsKey(q) && (q == "128k" || types[q]?["hash"] is not null));
        PJ.Require(quality is not null, "QUALITY_UNAVAILABLE");
        string resourceHash = quality == "128k" ? hash : PlatformSongs.KgHash(types[quality!]?["hash"]);
        string Album(string field, string alias)
        {
            string supplied = PJ.Scalar(data[field] ?? data[alias]), verified = PJ.Scalar(sd[field] ?? sd[alias]);
            if (supplied == "0") supplied = ""; if (verified == "0") verified = "";
            PJ.Require(supplied.Length == 0 || verified.Length == 0 || supplied == verified, "IDENTITY_MISMATCH");
            string value = verified.Length > 0 ? verified : supplied;
            return value.Length == 0 ? "0" : PJ.Id(JsonValue.Create(value), "^[1-9][0-9]{0,19}$", "INVALID_REQUEST");
        }
        string album = Album("albumId", "album_id"), albumAudio = Album("albumAudioId", "album_audio_id");
        var args = PJ.Obj(("album_id", album), ("area_code", 1), ("hash", resourceHash.ToLowerInvariant()),
            ("ssa_flag", "is_fromtrack"), ("version", 11430), ("page_id", 151369488), ("quality", levels[quality!]),
            ("album_audio_id", albumAudio), ("behavior", "play"), ("pid", 2), ("cmd", 26), ("pidversion", 3001),
            ("IsFreePart", 0), ("ppage_id", "463467626,350369493,788954147"), ("cdnBackup", 1),
            ("module", ""), ("clientver", 11430),
            ("key", PlatformProtocols.Md5(resourceHash.ToLowerInvariant() + "57ae12eb6890223e355ccfcb74edf70d" + "1005" + mid + session.UserId)));
        // The module's notSign is not request.js's notSignature: Android signature is still required.
        var response = (await c.Send(PlatformProtocols.Kg("/v5/url", args, session, c.KgClient,
            "trackercdn.kugou.com"), token).ConfigureAwait(false)).Body;
        PJ.Require(response["url"] is null || PJ.At(response, "data", "url") is null, "INVALID_RESPONSE");
        var result = response["url"] is not null ? response : response["data"] as JsonObject;
        PJ.Require(result is not null, "INVALID_RESPONSE");
        hashes = new[] { result!["hash"], result["fileHash"], result["req_hash"], response == result ? null : response["hash"] }
            .Where(n => n is not null).ToArray();
        PJ.Require(hashes.Length > 0 && hashes.All(n => PlatformSongs.KgHash(n) == resourceHash), "IDENTITY_MISMATCH");
        bool whole = false, uncertain = false;
        foreach (var record in new[] { response, result, response["trans_param"] as JsonObject, result["trans_param"] as JsonObject }.OfType<JsonObject>())
        {
            foreach (string f in new[] { "is_free_part", "IsFreePart", "free_part" })
                if (record[f] is { } flag) { PJ.Require(PJ.Scalar(flag) is "0" or "false", "TRIAL_ONLY"); whole = true; }
            PJ.Require(record["freeTrialInfo"] is null && record["freePart"] is null, "TRIAL_ONLY");
            foreach (string f in new[] { "pay_type", "privilege", "fail_process", "error_code", "errcode", "err_code" })
                PJ.Require(record[f] is null || PJ.Scalar(record[f]) == "0", "PLAYBACK_RESTRICTED");
            if (record["priv_status"] is not null) uncertain = true;
            foreach (var (field, expected) in new[] { ("album_id", album), ("album_audio_id", albumAudio) })
                if (record[field] is { } value && PJ.Scalar(value) != "0" && expected != "0")
                    PJ.Require(PJ.Scalar(value) == expected, "IDENTITY_MISMATCH");
        }
        var urls = result["url"] is JsonArray list ? list : new JsonArray(result["url"]?.DeepClone());
        PJ.Require(urls.Count is > 0 and <= 16, "INVALID_RESPONSE");
        var uris = urls.Select(MediaUri).ToArray();
        long expectedSize = quality switch {
            "128k" => Size(raw["filesize"] ?? raw["FileSize"] ?? raw["size"] ?? audio["filesize"]),
            "320k" => Size(raw["filesize_320"] ?? raw["HQFileSize"] ?? audio["filesize_320"]),
            "flac" => Size(raw["filesize_flac"] ?? raw["SQFileSize"] ?? audio["filesize_flac"]),
            _ => Size(raw["filesize_high"] ?? raw["ResFileSize"] ?? audio["filesize_high"])
        };
        long size = Size(result["fileSize"] ?? result["filesize"]);
        PJ.Require((result["fileSize"] is null || result["filesize"] is null ||
            Size(result["fileSize"]) == Size(result["filesize"])) && (size == 0 || expectedSize == 0 || size == expectedSize), "MEDIA_MISMATCH");
        return new(uris[0], quality!, "", false, Number(song["duration"]), size, null,
            whole && !uncertain && size > 0 && size == expectedSize);
    }
}
