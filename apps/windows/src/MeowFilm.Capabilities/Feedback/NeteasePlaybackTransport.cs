using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>Exact-account preflight and the single NCBL playback file channel.</summary>
internal sealed class NeteasePlaybackTransport(IMusicPlatformSessionSource accounts, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients)
{
    private readonly MusicPlatformHttp http = http;
    internal const string UploadUrl = "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true";
    internal const string UserAgent = "Meowfilm (Windows)";

    internal async Task QualifyAccountAsync(MusicPlatformSession account, CancellationToken token)
    {
        Guard(account, token);
        _ = FeedbackProtocolRules.Cookies(account);
        var client = await clients.AcquireWyAsync(token).ConfigureAwait(false);
        Guard(account, token);
        var response = await http.SendFeedbackJsonAsync("wy", FeedbackProtocolRequests.WyIdentity(account, client), token)
            .ConfigureAwait(false);
        Guard(account, token);
        FeedbackProtocolRequests.VerifyWyIdentity(response.Body, account);
        // Validate authorization expiry without persisting response credentials or
        // adding the ordinary catalog client's synthetic cookies to the upload.
        _ = MusicWyRequestContext.ResponseFields(response.SetCookieHeaders, DateTimeOffset.UtcNow);
        Guard(account, token);
    }

    internal async Task<Upload> PrepareAsync(MusicPlatformSession account, NeteasePlaybackPending value,
        NeteasePlaybackEvent next, CancellationToken token)
    {
        try
        {
            FeedbackProtocolRules.Require(value.Account.Matches(account), "SESSION_CHANGED");
            await QualifyAccountAsync(account, token).ConfigureAwait(false);
            byte[] metadata = Metadata(account);
            byte[] encoded;
            try
            {
                byte[] records = Records(value, next);
                // Codec and HTTP body bounds are protocol/resource boundaries.
                FeedbackProtocolRules.Require(metadata.Length <= 8192 && records.Length <= 65536, "LIMIT");
                encoded = NeteasePlaybackLogCodec.Encode(metadata, records);
            }
            finally { CryptographicOperations.ZeroMemory(metadata); }
            var filename = "play_" + Guid.NewGuid().ToString("N");
            var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl);
            try
            {
                var multipart = new MultipartFormDataContent();
                request.Content = multipart;
                var part = new ByteArrayContent(encoded);
                part.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
                multipart.Add(part, "file", filename);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Cookie", account.Cookie);
                Guard(account, token);
                return new(this, account, request, encoded, filename);
            }
            catch { request.Dispose(); CryptographicOperations.ZeroMemory(encoded); throw; }
        }
        catch (MusicFeedbackException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new MusicFeedbackException(FeedbackProtocolRules.SafeError(error)); }
    }

    internal static NeteasePlaybackPending Start(MusicPlatformSession account, MusicFeedbackStart input, long revision)
    {
        string? source = input.Source is "" or "unknown" ? null : input.Source;
        string? sourceId = source is not null && input.SourceId.Length > 0 ? input.SourceId : null;
        string? scene = null;
        if (input.Recommendation is { Platform: "wy" } origin)
        {
            if (origin.Kind == "heartbeat") scene = "heartbeat";
            // Local mode/seed/liked-playlist context is not a wire source ID.
            // Only per-song upstream attribution may override the verified FM default.
            source = origin.FeedbackSource.Length > 0 ? origin.FeedbackSource :
                origin.Kind == "fm" && origin.Provider != "rule" ? "userfm" : null;
            sourceId = source is not null && origin.Metadata is { ValueKind: JsonValueKind.Object } metadata &&
                metadata.TryGetProperty("feedbackSourceId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 } upstreamId ? upstreamId : null;
        }
        return new(input.PlaybackId, NeteasePlaybackAccount.From(account), revision, input.Recording.Id,
            source, sourceId, input.Algorithm.Length > 0 ? input.Algorithm : null, false,
            new(Guid.NewGuid(), input.AdmittedAt.ToUnixTimeMilliseconds(), false, 0, 0, ""), null,
            scene);
    }

    internal static byte[] Metadata(MusicPlatformSession account) =>
        JsonSerializer.SerializeToUtf8Bytes(new { MUSIC_U = FeedbackProtocolRules.Cookies(account)["MUSIC_U"] },
            FeedbackProtocolRules.Json);

    internal static byte[] Records(NeteasePlaybackPending value, NeteasePlaybackEvent next)
    {
        bool start = value.Start?.Id == next.Id;
        FeedbackProtocolRules.Require(start || value.End?.Id == next.Id, "INSTANCE_MISMATCH");
        var json = new JsonObject
        {
            ["id"] = JsonNode.Parse(value.RecordingId), ["type"] = "song", ["time"] = Math.Floor(next.Time),
            ["realtime"] = Math.Floor(next.Realtime), ["_logTime"] = next.At
        };
        if (value.Source is not null) json["source"] = value.Source;
        if (value.SourceId is not null) json["sourceId"] = value.SourceId;
        if (value.Algorithm is not null) json["alg"] = value.Algorithm;
        if (value.PlayScene is not null) json["play_scene"] = value.PlayScene;
        if (!start)
        {
            json["end"] = next.Reason;
            json["play_speed"] = next.Rate;
        }
        string prefix = (next.At / 1000).ToString(CultureInfo.InvariantCulture);
        return Encoding.UTF8.GetBytes(prefix + "\x01" + (start ? "_plv" : "_pld") + "\x01" +
            json.ToJsonString(FeedbackProtocolRules.Json) + "\n");
    }

    private void Guard(MusicPlatformSession account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FeedbackProtocolRules.Require(account.Platform == "wy" && accounts.IsCurrent(account), "SESSION_CHANGED");
    }

    internal sealed class Upload(NeteasePlaybackTransport owner, MusicPlatformSession account,
        HttpRequestMessage request, byte[] encoded, string filename) : IDisposable
    {
        internal async Task SendAsync(CancellationToken token)
        {
            owner.Guard(account, token);
            try
            {
                var result = await owner.http.SendFeedbackJsonAsync("wy", request, token).ConfigureAwait(false);
                owner.Guard(account, token);
                FeedbackProtocolRules.WySuccess(result.Body);
                FeedbackProtocolRules.Require(result.Body["data"] is JsonObject data &&
                    data["successfiles"] is JsonArray files &&
                    files.All(item => item is JsonValue scalar && scalar.TryGetValue<string>(out _)) &&
                    files.Any(item => item!.GetValue<string>() == filename), "UPSTREAM_UNCONFIRMED");
            }
            catch (MusicFeedbackException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new MusicFeedbackException(FeedbackProtocolRules.SafeError(error)); }
        }
        public void Dispose() { request.Dispose(); CryptographicOperations.ZeroMemory(encoded); }
    }
}
