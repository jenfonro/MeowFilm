using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed class FilmPlaybackClient(IRuntimeClient client)
{
    internal async Task<JsonElement> CallAsync(string operation, object parameters, CancellationToken token = default)
    {
        var envelope = await client.CallAsync("film." + operation, FilmJson.Encode(parameters), token).ConfigureAwait(false);
        if (envelope.ValueKind != JsonValueKind.Object || FilmJson.Text(envelope, "contract") != "film.playback.v1")
            throw new FilmPlaybackException("INVALID_RESPONSE");
        var ok = FilmJson.Member(envelope, "ok");
        if (ok.ValueKind == JsonValueKind.False)
            throw new FilmPlaybackException(FilmJson.RequiredText(envelope, "code"));
        if (ok.ValueKind != JsonValueKind.True || !envelope.TryGetProperty("value", out var value))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return value.Clone();
    }

    internal Task<JsonElement> CommandAsync(Guid sessionId, string action, object? parameters = null,
        CancellationToken token = default)
    {
        var values = new Dictionary<string, object?> { ["sessionId"] = sessionId.ToString(), ["action"] = action };
        if (parameters is not null)
            foreach (var field in FilmJson.Encode(parameters).EnumerateObject()) values.Add(field.Name, field.Value);
        return CallAsync("session.command", values, token);
    }

    internal async IAsyncEnumerable<FilmPlaybackSnapshot> ObserveAsync(Guid sessionId, long after,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in client.EventsAsync("film", sessionId, after, token).ConfigureAwait(false))
        {
            var snapshot = new FilmPlaybackSnapshot(value);
            if (snapshot.SessionId != sessionId) throw new FilmPlaybackException("INVALID_RESPONSE");
            yield return snapshot;
        }
    }

    internal async ValueTask ReleaseMediaAsync(Guid sessionId, string attemptId, string resourceId)
    {
        var result = await CallAsync("session.media.release",
            new { sessionId = sessionId.ToString(), attemptId, resourceId }).ConfigureAwait(false);
        if (!FilmJson.Bool(result, "released")) throw new FilmPlaybackException("INVALID_RESPONSE");
    }

    internal async Task ReleaseAsync(Guid sessionId, bool keepMedia = false)
    {
        var result = await CallAsync("session.release", new { sessionId = sessionId.ToString(), keepMedia }).ConfigureAwait(false);
        if (!FilmJson.Bool(result, keepMedia ? "retained" : "released"))
            throw new FilmPlaybackException("INVALID_RESPONSE");
    }
}
