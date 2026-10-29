// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus + existing pinned platform recipes. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

public sealed partial class NativeMusicLyrics
{
    private static async Task<NativeLyricPayload?> Wy(Call c)
    {
        var p = c.Request;
        var data = await c.Read(MusicPlatformRequests.WyWithCookie("/api/song/lyric/v1",
            new JsonObject { ["id"] = p.RecordingId, ["cp"] = false, ["tv"] = 0, ["lv"] = 0,
                ["rv"] = 0, ["kv"] = 0, ["yv"] = 0, ["ytv"] = 0, ["yrv"] = 0 }, c.Session?.Cookie)).ConfigureAwait(false);
        PlatformProtocols.WySuccess(data);
        if (data["id"] is not null) L.Require(L.Id(data["id"]) == p.RecordingId, "IDENTITY_MISMATCH");
        string Track(string name)
        {
            if (data[name] is null) return "";
            L.Require(data[name] is JsonObject, "INVALID_RESPONSE");
            return L.Text(data[name]!["lyric"], optional: true);
        }
        var yrc = Track("yrc"); var lrc = Track("lrc");
        var ytlrc = Track("ytlrc"); var tlyric = Track("tlyric");
        var yrcChosen = L.Timed(yrc);
        var text = yrcChosen ? yrc : lrc;
        // Do not shift or zip unrelated LRC clocks. The caller's native parser
        // aligns exact timestamps. ytlrc is bound to the selected YRC clock.
        var translation = yrcChosen && L.Timed(ytlrc) ? ytlrc : L.Timed(tlyric) ? tlyric : "";
        return Payload(p, text, translation, "", c.Token);
    }

    private static async Task<NativeLyricPayload?> Tx(Call c)
    {
        var p = c.Request;
        var lookup = await c.Read(MusicPlatformRequests.TxWithCookie("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo",
            new JsonObject { ["ctx"] = 0, ["client"] = 1, ["types"] = new JsonArray(0), ["modify_stamp"] = new JsonArray(0),
                ["mids"] = new JsonArray(p.RecordingId) }, c.Session?.Cookie)).ConfigureAwait(false);
        var details = PlatformProtocols.TxSuccess(lookup);
        L.Require(details["tracks"] is JsonArray tracks && tracks.Count <= 500 && tracks.All(t => t is JsonObject), "INVALID_RESPONSE");
        var matching = ((JsonArray)details["tracks"]!).Cast<JsonObject>().Where(t => L.Text(t["mid"], true) == p.RecordingId).ToArray();
        L.Require(matching.Length == 1, "IDENTITY_MISMATCH");
        var id = L.Id(matching[0]["id"]);
        // Song.type and LyricApi type are distinct protocols. Only the exact
        // fixed reference's normal recording (Song.type == 1) is confirmed.
        L.Require(L.Integer(matching[0]["type"]) == 1, "IDENTITY_MISMATCH");
        var reply = await c.Read(MusicPlatformRequests.TxWithCookie("music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo",
            new JsonObject { ["songMid"] = p.RecordingId, ["type"] = 1, ["crypt"] = 1,
                ["lrc_t"] = 0, ["qrc"] = 1, ["qrc_t"] = 0, ["trans"] = 1, ["trans_t"] = 0,
                ["roma"] = 1, ["roma_t"] = 0, ["needSingingAnnotations"] = false }, c.Session?.Cookie)).ConfigureAwait(false);
        var data = PlatformProtocols.TxSuccess(reply);
        L.Require(L.Id(data["songID"]) == id, "IDENTITY_MISMATCH");
        // *_t are timestamps, NOT encoding flags. Pinned protocol always crypt=1.
        // It may already contain decoded text; do not invent base64 fallback.
        var text = LyricCodecs.Qrc(L.Text(data["lyric"]), c.Token);
        var translation = LyricCodecs.Qrc(L.Text(data["trans"], true), c.Token);
        var romanization = LyricCodecs.Qrc(L.Text(data["roma"], true), c.Token);
        return Payload(p, text, translation, romanization, c.Token);
    }

    private readonly string guestMid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private static HttpRequestMessage KgRequest(Call c, string path, JsonObject parameters, bool search)
    {
        // Reuse the existing credential validation, client defaults and native
        // signing builder. The fixed search recipe clears default parameters;
        // its `notSign` spelling is not request.js's `notSignature`, so the
        // pinned implementation still signs the explicit parameter map.
        var request = MusicPlatformRequests.KgWithCookie(path, parameters, c.Session?.Cookie, c.Owner.guestMid);
        if (search)
        {
            var values = parameters.ToDictionary(pair => pair.Key, pair => pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                ? text : PJ.Scalar(pair.Value), StringComparer.Ordinal);
            const string salt = "OIlwieks28dk2k092lksi2UIkp"; // Public protocol constant, not an account secret.
            values["signature"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(salt +
                string.Concat(values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + v.Value)) + salt)));
            request.RequestUri = new Uri("https://lyrics.kugou.com" + path + "?" + string.Join("&",
                values.Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value))));
            request.Headers.Remove("clienttime");
        }
        else request.RequestUri = new Uri("https://lyrics.kugou.com" + request.RequestUri!.PathAndQuery);
        return request;
    }
    private static void KgErrors(JsonObject data)
    {
        foreach (var name in new[] { "error_code", "errcode", "err_code", "code" })
        {
            var code = PJ.Scalar(data[name]);
            if (code == "20018") throw new MusicPlatformException("AUTH_REQUIRED");
            if (code == "20028") throw new MusicPlatformException("VERIFICATION_REQUIRED");
        }
        L.Require(data["ssaCode"] is null && data["ssa-code"] is null, "VERIFICATION_REQUIRED");
        L.Require(data["error"] is null || PJ.Scalar(data["error"]) is "false" or "0" or "\"\"", "UPSTREAM_ERROR");
    }
    private static async Task<NativeLyricPayload?> Kg(Call c)
    {
        var p = c.Request; var hash = p.RecordingId.ToUpperInvariant();
        long duration = (long)Math.Floor(p.DurationSeconds * 1000 + .5);
        var album = p.AlbumAudioId ?? "0";
        var data = await c.Read(KgRequest(c, "/v1/search", new JsonObject {
            ["album_audio_id"] = album, ["appid"] = 1005, ["clientver"] = 20489, ["duration"] = duration,
            ["hash"] = hash, ["keyword"] = "", ["lrctxt"] = 1, ["man"] = "no" }, search: true)).ConfigureAwait(false);
        KgErrors(data);
        L.Require(data["candidates"] is JsonArray candidates && candidates.Count <= 100 && candidates.All(v => v is JsonObject), "INVALID_RESPONSE");
        var rows = (JsonArray)data["candidates"]!;
        if (L.Integer(data["status"]) == 404 && L.Integer(data["errcode"]) == 404 && rows.Count == 0) return null;
        L.Require(L.Integer(data["status"]) == 200 && L.Integer(data["errcode"]) == 200, "UPSTREAM_ERROR");
        // No ranking/title matching/proposal fallback. Ambiguous search is no exact result.
        if (rows.Count != 1) return null;
        var row = rows[0]!; var id = L.Id(row["id"]); var accessKey = L.Text(row["accesskey"]);
        L.Require(L.Match(accessKey, "^[a-fA-F0-9]{32}$"), "INVALID_RESPONSE");
        L.Require(L.Integer(row["contenttype"]) == 0, "INVALID_RESPONSE");
        var length = L.Integer(row["duration"]); L.Require(length > 0, "INVALID_RESPONSE");
        if (row["hash"] is not null) L.Require(L.Text(row["hash"]).ToUpperInvariant() == hash, "IDENTITY_MISMATCH");
        if (row["album_audio_id"] is not null) L.Require(L.Id(row["album_audio_id"], allowZero: true) == album, "IDENTITY_MISMATCH");
        L.Require(duration == 0 || Math.Abs(length - (double)duration) <= Math.Max(3000, Math.Min(length, duration) * .02), "IDENTITY_MISMATCH");
        var result = await c.Read(KgRequest(c, "/download", new JsonObject {
            ["ver"] = 1, ["client"] = "android", ["id"] = id, ["accesskey"] = accessKey,
            ["fmt"] = "krc", ["charset"] = "utf8" }, search: false)).ConfigureAwait(false);
        KgErrors(result);
        L.Require(L.Integer(result["status"]) == 200 && L.Integer(result["error_code"]) == 0, "UPSTREAM_ERROR");
        L.Require(L.Id(result["id"]) == id, "IDENTITY_MISMATCH");
        if (result["hash"] is not null) L.Require(L.Text(result["hash"]).ToUpperInvariant() == hash, "IDENTITY_MISMATCH");
        L.Require(L.Text(result["fmt"]) == "krc" && L.Integer(result["contenttype"]) == 0, "INVALID_RESPONSE");
        var content = L.Text(result["content"]);
        if (content.Length == 0) return null;
        var converted = KrcLyrics.Convert(LyricCodecs.Krc(content, c.Token), c.Token);
        return converted is null ? null : Payload(p, converted.Value.Text, converted.Value.Translation, "", c.Token);
    }
    private static NativeLyricPayload? Payload(NativeLyricRequest request, string text, string translation, string romanization, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var value in new[] { text, translation, romanization })
            L.Require(value.Length <= L.MaxSegment, "LIMIT");
        L.Require(Encoding.UTF8.GetByteCount(text) + Encoding.UTF8.GetByteCount(translation) + Encoding.UTF8.GetByteCount(romanization) <= L.MaxResponse, "LIMIT");
        return L.Timed(text) ? new(request.Platform, request.RecordingId, text, translation, romanization) : null;
    }
}
