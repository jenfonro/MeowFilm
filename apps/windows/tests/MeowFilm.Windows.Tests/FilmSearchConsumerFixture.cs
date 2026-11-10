using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

// Explicit wire-only consumer fixture: no media receipts, no matching, no generated episodes or output.
internal sealed class FilmSearchConsumerRuntime : IRuntimeClient, IRuntimeConnectionSource, IDisposable
{
    internal readonly CancellationTokenSource Revoked = new();
    internal readonly List<(string Method, JsonElement Input)> Calls = [];
    internal Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Respond;
    private readonly Dictionary<Guid, (JsonElement Value, Channel<JsonElement> Events)> sessions = [];
    internal Guid LastSessionId { get; private set; }
    internal RuntimeConnection Connection => new(this, new Uri("http://127.0.0.1:34101"), Revoked.Token);
    public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Revoked.Token.ThrowIfCancellationRequested();
        return Task.FromResult(Connection);
    }
    internal static JsonElement Success(object? value) => JsonSerializer.SerializeToElement(new { ok = true, contract = "film.playback.v1", value });
    internal static JsonElement Failure(string code) => FilmJson.Encode(new { ok = false, contract = "film.playback.v1", code });
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = FilmJson.Encode(parameters ?? new { });
        Calls.Add((method, input));
        if (Respond is { } respond) return await respond(method, input, cancellationToken);
        return Handle(method, input);
    }
    internal JsonElement Handle(string method, JsonElement input)
    {
        if (method == "film.session.open")
        {
            var id = Guid.NewGuid();
            LastSessionId = id;
            var metadata = FilmJson.Member(input, "metadata");
            if (metadata.ValueKind == JsonValueKind.Undefined) metadata = FilmJson.Encode((object)null!);
            var value = FilmPlaybackFixture.Snapshot(id, 0, FilmJson.Target(input.GetProperty("target")), metadata);
            sessions.Add(id, (value, Channel.CreateUnbounded<JsonElement>()));
            return Success(value);
        }
        var sessionId = Guid.Parse(FilmJson.RequiredText(input, "sessionId"));
        var session = sessions[sessionId];
        if (method == "film.session.release")
        {
            session.Events.Writer.TryComplete();
            return Success(new { released = true });
        }
        if (method == "film.session.snapshot") return Success(session.Value);
        if (method == "film.session.command" && FilmJson.Text(input, "action") == "search.update")
        {
            // This is an explicit presentation receipt, not a simulation of Node association or playback.
            var value = FilmPlaybackFixture.Snapshot(sessionId, session.Value.GetProperty("sequence").GetInt64() + 1,
                FilmJson.Target(session.Value.GetProperty("target")), FilmJson.Member(input, "metadata"));
            sessions[sessionId] = (value, session.Events);
            return Success(value);
        }
        throw new InvalidOperationException("Unexpected consumer call: " + method);
    }
    public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var value in sessions[sessionId].Events.Reader.ReadAllAsync(cancellationToken)) yield return value;
    }
    internal void Publish(Guid sessionId, FilmPlaybackTarget target, JsonElement metadata)
    {
        var session = sessions[sessionId];
        var value = FilmPlaybackFixture.Snapshot(sessionId, session.Value.GetProperty("sequence").GetInt64() + 1, target, metadata);
        sessions[sessionId] = (value, session.Events);
        session.Events.Writer.TryWrite(value);
    }
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Search does not upload sources.");
    public void Dispose() { Revoked.Cancel(); Revoked.Dispose(); }
}
