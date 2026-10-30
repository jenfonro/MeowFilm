// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 2698673d utils/android_session.py + core/versioning.py.
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

internal interface IMusicQqFeedbackAccountProtocol
{
    Task<MusicAccountProfile> VerifyQqFeedbackAccountAsync(MusicPlatformSession account, CancellationToken token);
}

public sealed partial class MusicAccountProtocol
{
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
    async Task<MusicAccountProfile> IMusicQqFeedbackAccountProtocol.VerifyQqFeedbackAccountAsync(MusicPlatformSession account, CancellationToken token)
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
}
