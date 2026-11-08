using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public interface IMusicSearchSource
{
    event Action<JsonElement>? SearchChanged;
}

public sealed partial class MusicNativeApplication
{
    private readonly MusicSearchCoordinator searchCoordinator;
    private readonly SemaphoreSlim searchPolicyLane = new(1, 1);
    private bool searchPolicyUnconfirmed;
    public event Action<JsonElement>? SearchChanged
    {
        add => searchCoordinator.Changed += value;
        remove => searchCoordinator.Changed -= value;
    }

    private async Task<JsonElement> SearchCatalogAsync(MusicSearchRequest request, CancellationToken token)
    {
        // The coordinator fetches after its admission receipt has returned.
        // Capture the actual lease inside each physical request, not in start().
        using var frame = sessions.Begin();
        try
        {
            token.ThrowIfCancellationRequested();
            // Stable search/matching owners retain the real operation through their
            // slot drain; catalog owns its deadline and account/shutdown invalidation.
            var result = await catalog.SearchAsync(request).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Remembers the search authority for observed recording evidence; a retired session
            // leaves no context, so its late page cannot suppress a later matching request.
            if (frame.Session is not { } current || accounts.IsCurrent(current))
                lock (gate) matchingContexts[request.Platform] = MatchingContext(request.Platform, frame.Session);
            return result;
        }
        catch (MusicPlatformException error) when (error.Code is "AUTH_REQUIRED" or "AUTH_EXPIRED")
        {
            if (frame.Session is { } failed) accounts.Revoke(failed);
            throw;
        }
    }

    private async Task<JsonElement> SearchAndObserveCatalogAsync(MusicSearchRequest request, CancellationToken token)
    {
        await PrepareSongFactsAsync(token).ConfigureAwait(false);
        using var frame = sessions.Begin();
        var observation = songMatcher.BeginSearch(request.Platform);
        try
        {
            var reply = await catalog.SearchAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (frame.Session is { } session && !accounts.IsCurrent(session))
                throw new MusicPlatformException("AUTH_CHANGED");
            songMatcher.ObservePage(request, reply, "", MatchingContext(request.Platform, frame.Session), observation);
            return reply;
        }
        catch (MusicPlatformException error)
        {
            if (error.Code is "AUTH_REQUIRED" or "AUTH_EXPIRED" && frame.Session is { } failed) accounts.Revoke(failed);
            token.ThrowIfCancellationRequested();
            if (frame.Session is not { } session || accounts.IsCurrent(session))
                songMatcher.ObservePage(request, null, error.Code, MatchingContext(request.Platform, frame.Session), observation);
            throw;
        }
    }

    private async Task<JsonElement> ExecuteSearchAsync(string method, object? parameters, CancellationToken token)
    {
        if (method.StartsWith("music.search.history.", StringComparison.Ordinal))
            return await library.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
        if (method == "music.search.start")
            return await StartSearchAsync(parameters, token).ConfigureAwait(false);
        if (method is not ("music.search.settings.save" or "music.search.settings.get"))
            return await searchCoordinator.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
        await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (method == "music.search.settings.save")
            {
                // A lost CAS receipt may already have disabled providers. Fence
                // and physically drain both kinds before the policy write.
                searchPolicyUnconfirmed = true;
                await searchCoordinator.PrepareShutdownAsync().ConfigureAwait(false);
                var receipt = await library.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
                if (Okay(receipt))
                {
                    await searchCoordinator.CancelShutdownPreparationAsync().ConfigureAwait(false);
                    searchPolicyUnconfirmed = false;
                }
                return receipt;
            }
            var reply = await library.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
            if (Okay(reply) && searchPolicyUnconfirmed)
            {
                await searchCoordinator.CancelShutdownPreparationAsync().ConfigureAwait(false);
                searchPolicyUnconfirmed = false;
            }
            return reply;
        }
        finally { searchPolicyLane.Release(); }
    }

    private async Task<JsonElement> StartSearchAsync(object? parameters, CancellationToken token)
    {
        var input = parameters is JsonElement element ? element : JsonSerializer.SerializeToElement(parameters);
        if (input.ValueKind != JsonValueKind.Object ||
            input.EnumerateObject().Any(p => p.Name is not ("keyword" or "kind" or "platforms" or "refresh")) ||
            !input.TryGetProperty("keyword", out var word) || word.ValueKind != JsonValueKind.String ||
            !input.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            kind.GetString() is not ("songs" or "playlists") ||
            input.TryGetProperty("refresh", out var refresh) && refresh.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failed("INVALID_REQUEST");
        // Hydrate the native account snapshot before taking the policy lane. A
        // settings save may then run to completion while this read is pending;
        // the policy snapshot below is always the one current at admission.
        var restored = await accounts.EnsureInitialObservationAsync(token).ConfigureAwait(false);
        if (!Okay(restored)) return restored;
        await PrepareSongFactsAsync(token).ConfigureAwait(false);
        Task<JsonElement> start;
        await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (searchPolicyUnconfirmed) return Failed("SEARCH_SETTINGS_UNCONFIRMED");
            var policy = await library.ExecuteAsync("music.search.settings.get", new { }, token).ConfigureAwait(false);
            if (!Okay(policy)) return policy;
            var settings = policy.GetProperty("settings")
                .GetProperty(kind.GetString() == "songs" ? "songs" : "playlists");
            // Enabled platforms are already in saved priority order.
            string[] platforms = settings.GetProperty("enabled").EnumerateArray().Select(p => p.GetString()!).ToArray();
            // A trusted UI still cannot dispatch its unsaved draft or a
            // retired preference revision. Empty saved selections stay empty.
            if (!input.TryGetProperty("platforms", out var requested) || requested.ValueKind != JsonValueKind.Array ||
                !requested.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String) ||
                !requested.EnumerateArray().Select(p => p.GetString()).SequenceEqual(platforms))
                return Failed("SEARCH_SETTINGS_CHANGED");
            // Admission is synchronous. The lane is released before waiting for
            // an old query to drain, so get/next/cancel and replacement
            // admission never wait behind this start.
            start = searchCoordinator.ExecuteStartAsync(new
            {
                keyword = word.GetString(), kind = kind.GetString(), platforms,
                pages = settings.GetProperty("pages").GetInt32(),
                concurrency = settings.GetProperty("concurrency").GetInt32(),
                cacheMinutes = settings.GetProperty("cacheMinutes").GetInt32(),
                refresh = input.TryGetProperty("refresh", out var again) && again.GetBoolean()
            }, token);
        }
        finally { searchPolicyLane.Release(); }
        return await start.ConfigureAwait(false);
    }

    private static bool Okay(JsonElement reply) =>
        reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
}
