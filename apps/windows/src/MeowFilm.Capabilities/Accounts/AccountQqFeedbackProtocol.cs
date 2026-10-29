// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 2698673d utils/android_session.py + core/versioning.py.
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

internal interface IMusicQqFeedbackDeviceProtocol
{
    TimeProvider QqFeedbackClock { get; }
    Task<MusicAccountProfile> VerifyQqFeedbackAccountAsync(MusicPlatformSession account, CancellationToken token);
    Task<StoredQqQimei> RequestQqQimeiAsync(StoredQqFeedbackDevice device, CancellationToken token);
    Task<StoredQqDeviceSession> RequestQqDeviceSessionAsync(StoredQqFeedbackDevice device, CancellationToken token);
}

public sealed partial class MusicAccountProtocol
{
    TimeProvider IMusicQqFeedbackDeviceProtocol.QqFeedbackClock => clock;
    private async Task<JsonObject> SendQqDevice(MusicQqDeviceHttpStep step, HttpRequestMessage request, CancellationToken token)
    {
        using var owned = request;
        try
        {
            Task<MusicPlatformHttpResult> task;
            lock (gate)
            {
                AccountRules.Require(!closed, "CLOSED");
                task = transport.SendQqDeviceJsonAsync(step, request, token);
                Track(task);
            }
            var result = await task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (gate) AccountRules.Require(!closed, "CLOSED");
            Inspect(result.Body); return result.Body;
        }
        catch (MusicPlatformException e) { throw new MusicAccountException(e.Code == "AUTH_REQUIRED" ? "AUTH_EXPIRED" : e.Code); }
        catch (MusicAccountException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new MusicAccountException("NETWORK_ERROR"); }
    }
    private static JsonObject QqDeviceData(JsonObject body)
    {
        QqStatus(body["code"]);
        AccountRules.Require(body["req_0"] is JsonObject, "INVALID_RESPONSE");
        QqStatus(body["req_0"]!["code"]);
        AccountRules.Require(body["req_0"]!["data"] is JsonObject, "INVALID_RESPONSE");
        return body["req_0"]!["data"]!.AsObject();
    }
    async Task<MusicAccountProfile> IMusicQqFeedbackDeviceProtocol.VerifyQqFeedbackAccountAsync(MusicPlatformSession account, CancellationToken token)
    {
        AccountRules.Require(account.Platform == "tx", "INVALID_REQUEST");
        var parts = AccountRules.Cookies(account.Cookie);
        string id = AccountRules.Id(parts.GetValueOrDefault("musicid"), "INVALID_CREDENTIALS");
        AccountRules.Require(id == account.UserId && (!parts.TryGetValue("str_musicid", out var str) || str == id), "IDENTITY_MISMATCH");
        AccountRules.Text(parts.GetValueOrDefault("musickey"), 8192, "INVALID_CREDENTIALS");
        string login = parts.GetValueOrDefault("login_type", parts.GetValueOrDefault("loginType", ""));
        AccountRules.Require((login.Length == 0 || AccountRules.Match(login, "^[1-9][0-9]?$")) &&
            (!parts.TryGetValue("loginType", out var loginAlias) || loginAlias == login), "INVALID_CREDENTIALS");
        string euin = parts.GetValueOrDefault("encrypt_uin", parts.GetValueOrDefault("encryptUin", ""));
        AccountRules.Require(AccountRules.Match(euin, "^[A-Za-z0-9=_-]{1,256}$") &&
            (!parts.TryGetValue("encryptUin", out var alias) || alias == euin), "INVALID_CREDENTIALS");
        var auth = QqDeviceData(await SendQqDevice(MusicQqDeviceHttpStep.Account,
            MusicPlatformRequests.TxWithCookie("music.UserInfo.userInfoServer", "GetLoginUserInfo", new(), account.Cookie), token).ConfigureAwait(false));
        if (auth["code"] is { } code) QqStatus(code);
        var home = QqDeviceData(await SendQqDevice(MusicQqDeviceHttpStep.Profile,
            MusicPlatformRequests.TxWithCookie("music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader",
                new() { ["uin"] = euin, ["IsQueryTabDetail"] = 1 }, account.Cookie), token).ConfigureAwait(false));
        AccountRules.Require(home["Info"] is JsonObject info && info["BaseInfo"] is JsonObject, "INVALID_RESPONSE");
        var profile = home["Info"]!["BaseInfo"]!.AsObject();
        AccountRules.Require(Scalar(profile["EncryptedUin"]) == euin, "IDENTITY_MISMATCH");
        return Summary("tx", id, profile["Name"], profile["Avatar"], account.Cookie);
    }
    async Task<StoredQqQimei> IMusicQqFeedbackDeviceProtocol.RequestQqQimeiAsync(StoredQqFeedbackDevice device, CancellationToken token)
    {
        var body = await SendQqDevice(MusicQqDeviceHttpStep.Qimei, QqQimeiRecipe.Request(device, clock.GetUtcNow()), token).ConfigureAwait(false);
        if (body["code"] is { } code) AccountRules.Require(StrictCode(code) == 0, "UPSTREAM_ERROR");
        string serialized = Text(body["data"]);
        AccountRules.Require(Encoding.UTF8.GetByteCount(serialized) <= 65536, "LIMIT");
        JsonObject inner;
        try { inner = PJ.Parse(Encoding.UTF8.GetBytes(serialized), 65536); }
        catch (MusicPlatformException e) { throw new MusicAccountException(e.Code); }
        if (inner["code"] is { } innerCode) AccountRules.Require(StrictCode(innerCode) == 0, "UPSTREAM_ERROR");
        AccountRules.Require(inner["data"] is JsonObject, "INVALID_RESPONSE");
        var data = inner["data"]!.AsObject();
        return new((device.Qimei?.Generation ?? 0) + 1, AccountDocument.QqDeviceText(Text(data["q16"])),
            AccountDocument.QqDeviceText(Text(data["q36"])), clock.GetUtcNow().ToUnixTimeMilliseconds());
    }
    async Task<StoredQqDeviceSession> IMusicQqFeedbackDeviceProtocol.RequestQqDeviceSessionAsync(StoredQqFeedbackDevice device, CancellationToken token)
    {
        AccountRules.Require(device.QimeiCurrent(clock.GetUtcNow()), "DEVICE_CONTEXT_REQUIRED");
        var stale = device.Session;
        // This comm intentionally has NO qq/authst/tmeLoginType/Cookie/uid/sid.
        var comm = new JsonObject { ["ct"] = "11", ["cv"] = "20090008", ["v"] = "20090008", ["chid"] = "10003505",
            ["tmeAppID"] = "qqmusic", ["traceid"] = "10002_" + device.OpenUdid + "_" + clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["QIMEI36"] = device.Qimei!.Q36, ["OpenUDID"] = device.OpenUdid, ["udid"] = device.OpenUdid,
            ["OpenUDID2"] = device.OpenUdid2, ["aid"] = device.AndroidId, ["os_ver"] = "15", ["phonetype"] = "V2408A" };
        var body = new JsonObject { ["comm"] = comm, ["req_0"] = new JsonObject {
            ["module"] = "music.getSession.session", ["method"] = "GetSession",
            ["param"] = new JsonObject { ["uid"] = stale?.Uid ?? "", ["vkey"] = 0, ["caller"] = stale is null ? 2 : 1 } } };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg") {
            Content = new StringContent(PJ.Encode(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 20090008(android 15)");
        var data = QqDeviceData(await SendQqDevice(MusicQqDeviceHttpStep.Session, request, token).ConfigureAwait(false));
        AccountRules.Require(data["session"] is JsonObject, "INVALID_RESPONSE");
        var session = data["session"]!.AsObject();
        string uid;
        if (session["uid"] is JsonValue v && v.TryGetValue<string>(out var uidString)) uid = uidString;
        else
        {
            uid = Scalar(session["uid"]);
            AccountRules.Require(AccountRules.Match(uid, "^-?[0-9]{1,512}$"), "INVALID_RESPONSE");
        }
        return new((stale?.Generation ?? 0) + 1, AccountDocument.QqDeviceText(uid), AccountDocument.QqDeviceText(Text(session["sid"])),
            clock.GetUtcNow().ToUnixTimeMilliseconds(), device.Qimei!.Generation);
    }
}
