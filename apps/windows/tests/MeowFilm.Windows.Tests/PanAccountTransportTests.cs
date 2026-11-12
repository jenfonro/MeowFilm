using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Core.Runtime;
using static MeowFilm.Windows.Tests.PanAccountFixture;

namespace MeowFilm.Windows.Tests;

public static class PanAccountTransportTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("pan-http: existing host forwards pan routes and one-call deadlines without changing payload", Host);
        yield return new("pan-http: deadline expiration and caller cancellation release one exchange only", Cancellation);
    }
    private static async Task Host()
    {
        await using var server = new RuntimeHttpClientTests.Server();
        Session? session = null;
        await using var host = new AppRuntimeConnectionHost(owner =>
        {
            session = new(server.BaseUrl, owner);
            return Task.FromResult<IRuntimeSession>(session);
        });
        host.Start();
        var call = host.CallAsync("pan.login.poll", new { sessionId = "synthetic-session" }, TimeSpan.FromSeconds(45));
        await using (var exchange = await server.AcceptAsync())
        {
            using var document = JsonDocument.Parse(exchange.Body);
            Check(exchange.Method == "POST" && exchange.Path == "/v1/pan/login.poll" &&
                document.RootElement.EnumerateObject().Select(item => item.Name).SequenceEqual(["sessionId"]),
                "deadline travels in host call options, never pan request JSON");
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(Ok(new { sessionId = "synthetic-session", status = "pending" })));
        }
        Check((await call).GetProperty("value").GetProperty("status").GetString() == "pending" &&
            session!.Deadlines.SequenceEqual([TimeSpan.FromSeconds(45)]), "App host and epoch client forward exactly one 45-second option");
        foreach (string operation in new[] { "providers", "settings.get", "settings.set", "settings.clear", "refresh", "login.start", "login.cancel" })
        {
            var request = host.CallAsync("pan." + operation);
            await using var exchange = await server.AcceptAsync();
            Check(exchange.Path == "/v1/pan/" + operation, "pan stays on existing HTTP routes");
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(Ok(new { })));
            await request;
        }
        Check(session!.Deadlines.Count == 1, "untimed calls retain the default host deadline");
    }
    private static async Task Cancellation()
    {
        await using var server = new RuntimeHttpClientTests.Server();
        await using var client = new RuntimeHttpClient(server.BaseUrl);
        var expired = client.CallAsync("pan.login.poll", new { sessionId = "synthetic-session" }, TimeSpan.FromSeconds(1));
        await using (var exchange = await server.AcceptAsync())
            await BridgeTests.Expect("TIMEOUT", () => expired);
        using var cancellation = new CancellationTokenSource();
        var cancelled = client.CallAsync("pan.login.poll", new { sessionId = "synthetic-session" },
            TimeSpan.FromSeconds(45), cancellation.Token);
        await using (var exchange = await server.AcceptAsync())
        {
            cancellation.Cancel();
            try { await cancelled; throw new InvalidOperationException("Expected caller cancellation"); }
            catch (OperationCanceledException) { }
        }
        var next = client.CallAsync("pan.providers");
        await using (var exchange = await server.AcceptAsync())
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(Ok(new { providers = Array.Empty<object>() })));
        Check((await next).GetProperty("ok").GetBoolean(), "a cancelled timed call does not close or alter the borrowed HTTP client");
    }
    private sealed class Session(string baseUrl, CancellationToken owner) : IRuntimeSession, IRuntimeConnectionSource
    {
        private readonly RuntimeHttpClient client = new(baseUrl);
        internal readonly List<TimeSpan> Deadlines = [];
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeConnection(this, new Uri(baseUrl), owner));
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            client.CallAsync(method, parameters, cancellationToken);
        public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Deadlines.Add(timeout);
            return client.CallAsync(method, parameters, timeout, cancellationToken);
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            client.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default) =>
            client.ReadImageAsync(reference, maximumBytes, cancellationToken);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
