// SPDX-License-Identifier: GPL-3.0-only
// Exact-account read only. Playback writes use the NCBL file channel.
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal static class FeedbackProtocolRequests
{
    internal static HttpRequestMessage WyIdentity(MusicPlatformSession account, MusicWyClientContext client)
    {
        var request = MusicPlatformRequests.WyWithCookie("/api/w/nuser/account/get", new(), account.Cookie, client, weapi: true);
        request.Headers.Remove("Referer");
        request.Headers.Remove("User-Agent");
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("User-Agent", NeteasePlaybackTransport.UserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", account.Cookie);
        return request;
    }
    internal static void VerifyWyIdentity(JsonObject body, MusicPlatformSession account)
    {
        FeedbackProtocolRules.WySuccess(body);
        if (body.ContainsKey("profile") && body["profile"] is null) throw new MusicFeedbackException("AUTH_EXPIRED");
        FeedbackProtocolRules.Require(body["profile"] is JsonObject, "INVALID_RESPONSE");
        var profile = body["profile"]!.AsObject();
        FeedbackProtocolRules.Require(FeedbackProtocolRules.ResponseId(profile["userId"]) == account.UserId, "IDENTITY_MISMATCH");
        FeedbackProtocolRules.Require(profile["nickname"] is JsonValue name && name.TryGetValue<string>(out _), "INVALID_RESPONSE");
        if (body["account"] is not null)
        {
            FeedbackProtocolRules.Require(body["account"] is JsonObject, "INVALID_RESPONSE");
            if (body["account"]!["id"] is { } id)
                FeedbackProtocolRules.Require(FeedbackProtocolRules.ResponseId(id) == account.UserId, "IDENTITY_MISMATCH");
        }
    }
}
