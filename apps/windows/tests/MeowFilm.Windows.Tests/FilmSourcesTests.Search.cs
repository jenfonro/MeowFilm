using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static partial class FilmSourcesTests
{
    internal static async Task SearchWire()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b"); runtime.Add("disabled"); runtime.Nodes["disabled"].Enabled = false;
        using var epoch = new SearchDataRuntime(runtime);
        await using var service = new FilmSourceService(epoch, new Store());
        await service.RefreshAsync();
        await service.SaveVideoOptionsAsync(Key("b"), new(true, true, false, false));
        var sources = await service.ReadSearchSourcesAsync(default);
        Check(sources.Count == 1 && sources[0].Cat.InstallationId == "a", "only enabled ready searchable source is selected");
        epoch.Read = (input, _) =>
        {
            var source = input.GetProperty("source");
            Check(source.GetProperty("installationId").GetString() == "a" &&
                source.GetProperty("generation").GetInt64() == runtime.Nodes["a"].Generation &&
                source.GetProperty("siteId").GetString() == sources[0].Site.Key.SiteKey &&
                source.GetProperty("key").GetString() == sources[0].Key, "data search preserves the exact tuple identity and generation");
            Fields(input, "source", "query", "page");
            Check(input.GetProperty("query").GetString() == "原词" && input.GetProperty("page").GetInt32() == 4,
                "production adapter sends original query and independently selected page to Node");
            var raw = Element(new { pageCount = 4,
                list = Enumerable.Range(0, 141).Select(i => new { vod_id = i.ToString(), vod_name = i + "影片" }).ToArray() });
            return Task.FromResult(Element(new { items = raw.GetProperty("list"), raw, page = 4, hasMore = false }));
        };
        var payload = await service.SearchAsync(sources[0], "原词", 4, default);
        Check(payload.Value.GetProperty("raw").GetProperty("list").GetArrayLength() == 141 &&
            ReferenceEquals(payload.Owner!.Client, epoch) && payload.CacheEntry is null &&
            epoch.Methods.SequenceEqual(new[] { "film.data.search", "film.data.cache.entry" }) &&
            runtime.Invocations.All(call => call.Operation != "search"),
            "only Node content data is read; null receipt stays null and the complete raw page is retained");
        epoch.Read = (_, _) => Task.FromException<JsonElement>(new FilmPlaybackException("UPSTREAM_ERROR"));
        try { await service.SearchAsync(sources[0], "原词", 4, default); throw new InvalidOperationException("expected source error"); }
        catch (FilmPlaybackException error) { Check(error.Code == "UPSTREAM_ERROR", "a failed data search is not an empty successful page"); }
        await service.EnableCatAsync("a", false);
        Check(!service.IsSearchSourceCurrent(sources[0]), "disabled generation loses search authority");
    }

    private sealed class SearchDataRuntime(Runtime cat) : IRuntimeClient, IRuntimeConnectionSource, IDisposable
    {
        private readonly CancellationTokenSource revoked = new();
        internal readonly List<string> Methods = [];
        internal Func<JsonElement, CancellationToken, Task<JsonElement>> Read = null!;
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeConnection(this, new Uri("http://127.0.0.1:31111"), revoked.Token));
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (!method.StartsWith("film.", StringComparison.Ordinal))
                return await cat.CallAsync(method, parameters, cancellationToken);
            Methods.Add(method);
            var input = Element(parameters!);
            if (method == "film.data.cache.entry")
            {
                Check(input.GetProperty("kind").GetString() == "search" && input.GetProperty("page").GetInt32() == 4 &&
                    input.GetProperty("query").GetString() == "原词", "cache receipt uses the same original request identity");
                return Element(new { ok = true, contract = "film.playback.v1", value = (object?)null });
            }
            Check(method == "film.data.search", "no direct Cat content call");
            return Element(new { ok = true, contract = "film.playback.v1", value = await Read(input, cancellationToken) });
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            cat.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
        public void Dispose() { revoked.Cancel(); revoked.Dispose(); }
    }
}
