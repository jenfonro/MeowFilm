using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public sealed record FilmMetadataSettings(string ApiBase, string ImageBase,
    string ApiKey, string AccessToken, string Language, string Region, bool IncludeAdult)
{
    public string ApiToken => AccessToken.Length > 0 ? AccessToken : ApiKey;
}

public sealed record FilmDoubanSettings(string ApiBase, string SearchBase, string ImageBase,
    string ApiProxy, string ImageProxy, string Cookie);

/// <summary>TMDB access through the App runtime. Only the runtime persists settings and credentials.</summary>
public sealed class FilmMetadataService(IRuntimeClient runtime) : IFilmSearchMetadata, IAsyncDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HashSet<Task> pending = [];
    private CancellationTokenSource reads = new();
    private Task? preparation, disposal;
    private bool stopping, closed;
    public FilmMetadataSettings? Settings { get; private set; }
    public event EventHandler? Saved;

    public Task<JsonElement> SearchAsync(string query, CancellationToken token) =>
        ContentAsync("/3/search/multi", new { query, page = 1 }, "w500", token);
    public Task<JsonElement> DetailAsync(string type, long id, CancellationToken token)
    {
        if (type is not ("movie" or "tv") || id <= 0) throw new BridgeException("INVALID_REQUEST");
        return ContentAsync("/3/" + type + "/" + id.ToString(CultureInfo.InvariantCulture),
            new { append_to_response = "credits,similar,images" }, "original", token);
    }
    async Task<FilmSearchData> IFilmSearchMetadata.SearchAsync(string query, CancellationToken token) =>
        await ReadContentAsync("/3/search/multi", new { query, page = 1 }, "w500", token);
    Task<FilmSearchData> IFilmSearchMetadata.DetailAsync(string type, long id, CancellationToken token)
    {
        if (type is not ("movie" or "tv") || id <= 0) throw new BridgeException("INVALID_REQUEST");
        return ReadContentAsync("/3/" + type + "/" + id.ToString(CultureInfo.InvariantCulture),
            new { append_to_response = "credits,similar,images" }, "original", token);
    }
    private async Task<JsonElement> ContentAsync(string path, object query, string imageSize, CancellationToken token) =>
        (await ReadContentAsync(path, query, imageSize, token)).Value;
    private Task<FilmSearchData> ReadContentAsync(string path, object query, string imageSize, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new BridgeException("CLOSED");
        token.ThrowIfCancellationRequested();
        var work = ReadAsync();
        pending.Add(work);
        _ = ReleaseAsync(work);
        return work;
        async Task<FilmSearchData> ReadAsync()
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, reads.Token);
            return await FilmSearchDataClient.ReadAsync(runtime, "metadata",
                new { provider = "tmdb", path, query, imageSize },
                new { kind = "metadata", provider = "tmdb", path, query, imageSize }, request.Token);
        }
    }
    public string ResolveImage(string path) => path;

    public Task<FilmMetadataSettings> GetSettingsAsync(CancellationToken token = default)
        => RunAsync("tmdb.data.settings.get", new { }, value =>
        {
            var snapshot = ParseSettings(value);
            Settings = snapshot;
            return snapshot;
        }, token);

    public Task<FilmMetadataSettings> SaveSettingsAsync(IReadOnlyDictionary<string, object?> patch,
        CancellationToken token = default)
    {
        // Once admitted, a settings write waits for the runtime receipt even when the view leaves.
        // A cancelled UI wait cannot tell whether the runtime committed a credential update.
        return RunAsync("tmdb.data.settings.update", patch, value =>
        {
            var snapshot = ParseSettings(value);
            Settings = snapshot;
            Saved?.Invoke(this, EventArgs.Empty);
            return snapshot;
        }, token, writing: true);
    }

    public Task<FilmDoubanSettings> GetDoubanSettingsAsync(CancellationToken token = default) =>
        RunAsync("douban.data.settings.get", new { }, ParseDoubanSettings, token);

    public Task<FilmDoubanSettings> SaveDoubanSettingsAsync(IReadOnlyDictionary<string, object?> patch,
        CancellationToken token = default) =>
        RunAsync("douban.data.settings.update", patch, ParseDoubanSettings, token, writing: true);

    public Task ClearCacheAsync(string provider, CancellationToken token = default) =>
        RunAsync("film.data.invalidate", new { domain = provider }, _ => true, token, writing: true);

    private Task<T> RunAsync<T>(string method, object input, Func<JsonElement, T> receive,
        CancellationToken token, bool writing = false)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new BridgeException("CLOSED");
        token.ThrowIfCancellationRequested();
        var work = ExchangeAsync();
        pending.Add(work);
        _ = ReleaseAsync(work);
        return work;

        async Task<T> ExchangeAsync()
        {
            using var request = writing ? null : CancellationTokenSource.CreateLinkedTokenSource(token, reads.Token);
            var reply = await runtime.CallAsync(method, input, request?.Token ?? CancellationToken.None);
            if (!writing) request!.Token.ThrowIfCancellationRequested();
            if (!reply.GetProperty("ok").GetBoolean()) throw new BridgeException(reply.GetProperty("code").GetString()!);
            return receive(reply.GetProperty("value").Clone());
        }
    }
    private async Task ReleaseAsync(Task work)
    {
        try { await work; } catch { /* The caller owns the outcome. */ }
        pending.Remove(work);
    }
    private static FilmMetadataSettings ParseSettings(JsonElement value)
    {
        try
        {
            string Text(string key) => value.GetProperty(key).GetString() ?? throw new BridgeException("INVALID_RESPONSE");
            return new(Text("apiBase"), Text("imageBase"), Text("apiKey"), Text("accessToken"),
                Text("language"), Text("region"), value.GetProperty("includeAdult").GetBoolean());
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }
    private static FilmDoubanSettings ParseDoubanSettings(JsonElement value)
    {
        try
        {
            string Text(string key) => value.GetProperty(key).GetString() ?? throw new BridgeException("INVALID_RESPONSE");
            return new(Text("apiBase"), Text("searchBase"), Text("imageBase"),
                Text("apiProxy"), Text("imageProxy"), Text("cookie"));
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }
    public Task PrepareShutdownAsync()
    {
        dispatcher.VerifyAccess();
        if (preparation is not null) return preparation;
        stopping = true;
        reads.Cancel();
        return preparation = DrainAsync(pending.ToArray());
    }
    private static async Task DrainAsync(Task[] operations)
    { try { await Task.WhenAll(operations); } catch { /* Each caller receives its own failure. */ } }
    public void Resume()
    {
        dispatcher.VerifyAccess();
        if (closed) throw new BridgeException("CLOSED");
        if (preparation is null) return;
        // Individual request errors belong to their callers, not the lifecycle receipt.
        if (!preparation.IsCompleted) throw new InvalidOperationException("SHUTDOWN_NOT_DRAINED");
        preparation = null; stopping = false;
        reads.Dispose(); reads = new();
    }
    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        if (disposal is not null) return new(disposal);
        var drained = PrepareShutdownAsync();
        closed = true;
        return new(disposal = CloseAsync(drained));
    }
    private async Task CloseAsync(Task drained)
    {
        try { await drained; } catch { /* Request failures were already delivered to their callers. */ }
        reads.Dispose();
        Saved = null;
    }
}
