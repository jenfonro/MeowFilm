// SPDX-License-Identifier: GPL-3.0-only
// Wire fields: fixed iWebPlus netease.ts; existing native EAPI/WEAPI owns cryptography.
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal static class FeedbackProtocolRequests
{
    internal static HttpRequestMessage WyIdentity(MusicPlatformSession account) =>
        MusicPlatformRequests.WyWithCookie("/api/w/nuser/account/get", new(), account.Cookie, weapi: true);
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
    internal static HttpRequestMessage WyRecording(MusicPlatformSession account, MusicFeedbackRecording recording) =>
        MusicPlatformRequests.WyWithCookie("/api/v3/song/detail",
            new() { ["c"] = "[{\"id\":" + recording.Id + "}]" }, account.Cookie, weapi: true);
    internal static void VerifyWyRecording(JsonObject body, MusicFeedbackStart start)
    {
        FeedbackProtocolRules.WySuccess(body);
        FeedbackProtocolRules.Require(body["songs"] is JsonArray { Count: 1 } songs && songs[0] is JsonObject, "IDENTITY_MISMATCH");
        var song = body["songs"]![0]!.AsObject();
        FeedbackProtocolRules.Require(FeedbackProtocolRules.ResponseId(song["id"]) == start.Recording.Id, "IDENTITY_MISMATCH");
        FeedbackProtocolRules.Require(FeedbackProtocolRules.CompatibleDuration(
            FeedbackProtocolRules.Number(song["dt"]) / 1000, start.Duration), "IDENTITY_MISMATCH");
        // No title search, media URL, privilege override, alternate recording, or VIP gate.
    }
    internal static HttpRequestMessage WyReport(MusicPlatformSession account, MusicFeedbackStart start, MusicFeedbackEnd? end)
    {
        string source = start.Source switch
        {
            "playlist" or "likeMusic" => "list", "userfm" => "radio",
            "album" or "search" or "dailySongRecommend" or "song" => start.Source, _ => ""
        };
        var json = new JsonObject { ["id"] = start.Recording.Id, ["type"] = "song" };
        if (source.Length > 0) { json["source"] = source; json["sourceId"] = start.SourceId; }
        if (start.Algorithm.Length > 0) json["alg"] = start.Algorithm;
        json["mainsite"] = "1"; json["mainsiteWeb"] = "1"; json["content"] = "";
        if (end is not null)
        {
            json["download"] = 0; json["end"] = end.Reason; json["wifi"] = 0;
            json["time"] = (long)Math.Floor(end.Listened);
        }
        var logs = new JsonArray(new JsonObject { ["action"] = end is null ? "startplay" : "play", ["json"] = json });
        var request = MusicPlatformRequests.WyWithCookie("/api/feedback/weblog",
            new() { ["logs"] = logs.ToJsonString(FeedbackProtocolRules.Json) }, account.Cookie);
        // Same EAPI plaintext path and cipher; the audited weblog domain is not the content domain.
        request.RequestUri = new Uri("https://clientlog.music.163.com/eapi/feedback/weblog");
        return request;
    }
}
