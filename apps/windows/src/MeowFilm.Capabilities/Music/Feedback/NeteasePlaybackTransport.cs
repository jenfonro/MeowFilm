using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>Node protocol adapter for the native durable playback outbox.</summary>
internal sealed class NeteasePlaybackTransport(IMusicPlatformSessionSource accounts, MusicPlatformClient client)
{
    private readonly MusicPlatformClient client = client;
    internal async Task QualifyAccountAsync(MusicPlatformSession account, CancellationToken token)
    {
        Guard(account, token);
        try { _ = await client.RequestAsync("wy", "feedback.qualify", new { }, account, token, () => Guard(account, token)).ConfigureAwait(false); }
        catch (MusicPlatformException error) { throw new MusicFeedbackException(error.Code); }
    }
    internal async Task<Upload> PrepareAsync(MusicPlatformSession account, NeteasePlaybackPending value,
        NeteasePlaybackEvent next, CancellationToken token)
    {
        FeedbackProtocolRules.Require(value.Account.Matches(account), "SESSION_CHANGED");
        await QualifyAccountAsync(account, token).ConfigureAwait(false);
        bool start = value.Start?.Id == next.Id;
        FeedbackProtocolRules.Require(start || value.End?.Id == next.Id, "INSTANCE_MISMATCH");
        var facts = JsonSerializer.SerializeToElement(new
        {
            @event = start ? "start" : "end", occurredAt = next.At / 1000d, listened = next.Realtime,
            mediaListened = next.Time, reason = next.Reason, rate = next.Rate,
            target = new { nativeId = value.RecordingId, source = value.Source ?? "", sourceId = value.SourceId ?? "",
                algorithm = value.Algorithm ?? "", origin = new { kind = value.PlayScene ?? "" } }
        });
        return new(this, account, facts);
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

    private void Guard(MusicPlatformSession account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FeedbackProtocolRules.Require(account.Platform == "wy" && accounts.IsCurrent(account), "SESSION_CHANGED");
    }
    internal sealed class Upload(NeteasePlaybackTransport owner, MusicPlatformSession account, JsonElement facts) : IDisposable
    {
        private JsonElement payload = facts;
        internal async Task SendAsync(CancellationToken token)
        {
            owner.Guard(account, token);
            try
            {
                var receipt = await owner.client.RequestAsync("wy", "feedback.upload", new { events = new[] { payload } }, account, token,
                    () => owner.Guard(account, token)).ConfigureAwait(false);
                string status = receipt.GetProperty("status").GetString()!;
                if (status != "confirmed") throw new MusicFeedbackException(receipt.GetProperty("errorCode").GetString() ?? "UPSTREAM_UNCONFIRMED", status);
            }
            catch (MusicPlatformException error) { throw new MusicFeedbackException(error.Code); }
        }
        public void Dispose() { payload = default; }
    }
}
