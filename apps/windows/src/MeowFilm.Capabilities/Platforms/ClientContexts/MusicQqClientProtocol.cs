// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 2698673d QIMEI and anonymous Android session recipes; see LICENSES/NOTICE.md.
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal sealed partial class MusicPlatformClientProtocol
{
    internal async Task<MusicClientQimei> RequestQqQimeiAsync(MusicQqClientContext device, CancellationToken token)
    {
        var body = await SendQqDevice(MusicQqDeviceHttpStep.Qimei, QqQimeiRecipe.Request(device, Clock.GetUtcNow()), token).ConfigureAwait(false);
        if (body["code"] is { } code) PJ.Require(StrictCode(code) == 0, "UPSTREAM_ERROR");
        string serialized = Text(body["data"]);
        PJ.Require(Encoding.UTF8.GetByteCount(serialized) <= 65536, "LIMIT");
        JsonObject inner;
        byte[] encoded = Encoding.UTF8.GetBytes(serialized);
        try { inner = PJ.Parse(encoded, 65536); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
        if (inner["code"] is { } innerCode) PJ.Require(StrictCode(innerCode) == 0, "UPSTREAM_ERROR");
        PJ.Require(inner["data"] is JsonObject, "INVALID_RESPONSE");
        var data = inner["data"]!.AsObject();
        return new((device.Qimei?.Generation ?? 0) + 1, MusicClientContextDocument.ValidateDeviceText(Text(data["q16"])),
            MusicClientContextDocument.ValidateDeviceText(Text(data["q36"])), Clock.GetUtcNow().ToUnixTimeMilliseconds());
    }
    internal async Task<MusicClientDeviceSession> RequestQqDeviceSessionAsync(MusicQqClientContext device, CancellationToken token)
    {
        PJ.Require(device.QimeiCurrent(Clock.GetUtcNow()), "DEVICE_CONTEXT_REQUIRED");
        var stale = device.Session;
        // This comm intentionally has NO qq/authst/tmeLoginType/Cookie/uid/sid.
        var comm = new JsonObject { ["ct"] = "11", ["cv"] = "20090008", ["v"] = "20090008", ["chid"] = "10003505",
            ["tmeAppID"] = "qqmusic", ["traceid"] = "10002_" + device.OpenUdid + "_" + Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["QIMEI36"] = device.Qimei!.Q36, ["OpenUDID"] = device.OpenUdid, ["udid"] = device.OpenUdid,
            ["OpenUDID2"] = device.OpenUdid2, ["aid"] = device.AndroidId, ["os_ver"] = "15", ["phonetype"] = "V2408A" };
        var body = new JsonObject { ["comm"] = comm, ["req_0"] = new JsonObject {
            ["module"] = "music.getSession.session", ["method"] = "GetSession",
            ["param"] = new JsonObject { ["uid"] = stale?.Uid ?? "", ["vkey"] = 0, ["caller"] = stale is null ? 2 : 1 } } };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg") {
            Content = new StringContent(PJ.Encode(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 20090008(android 15)");
        var data = QqDeviceData(await SendQqDevice(MusicQqDeviceHttpStep.Session, request, token).ConfigureAwait(false));
        PJ.Require(data["session"] is JsonObject, "INVALID_RESPONSE");
        var session = data["session"]!.AsObject();
        string uid;
        if (session["uid"] is JsonValue v && v.TryGetValue<string>(out var uidString)) uid = uidString;
        else
        {
            uid = Scalar(session["uid"]);
            PJ.Require(Match(uid, "^-?[0-9]{1,512}$"), "INVALID_RESPONSE");
        }
        return new((stale?.Generation ?? 0) + 1, MusicClientContextDocument.ValidateDeviceText(uid), MusicClientContextDocument.ValidateDeviceText(Text(session["sid"])),
            Clock.GetUtcNow().ToUnixTimeMilliseconds(), device.Qimei!.Generation);
    }
}
