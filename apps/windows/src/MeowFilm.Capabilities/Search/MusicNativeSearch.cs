using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public interface IMusicAggregateSearchSource
{
    event Action<JsonElement>? SearchChanged;
}

public sealed partial class MusicNativeApplication
{
    private readonly MusicSearchCoordinator aggregateSearch;
    private readonly SemaphoreSlim searchPolicyLane = new(1, 1);
    private bool searchPolicyUnconfirmed;
    public event Action<JsonElement>? SearchChanged
    {
        add => aggregateSearch.Changed += value;
        remove => aggregateSearch.Changed -= value;
    }

    private async Task<JsonElement> SearchCatalogAsync(MusicSearchRequest request, CancellationToken token)
    {
        // The coordinator fetches after its admission receipt has returned.
        // Capture the actual lease inside each physical request, not in start().
        using var frame = sessions.Begin();
        try { return await catalog.SearchAsync(request, token).ConfigureAwait(false); }
        catch (MusicPlatformException error) when (error.Code is "AUTH_REQUIRED" or "AUTH_EXPIRED")
        {
            if (frame.Session is { } failed) accounts.Revoke(failed);
            throw;
        }
    }

    private async Task<JsonElement> ExecuteSearchAsync(string method, object? parameters, CancellationToken token)
    {
        if (method.StartsWith("music.search.history.", StringComparison.Ordinal))
            return await library.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
        await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (method == "music.search.settings.save")
            {
                // A lost CAS receipt may already have disabled providers. Fence
                // and physically drain both kinds before the policy write.
                searchPolicyUnconfirmed = true;
                await aggregateSearch.PrepareShutdownAsync().ConfigureAwait(false);
                var receipt = await library.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
                if (Okay(receipt))
                {
                    await aggregateSearch.CancelShutdownPreparationAsync().ConfigureAwait(false);
                    searchPolicyUnconfirmed = false;
                }
                return receipt;
            }
            if (method == "music.search.settings.get")
            {
                var receipt = await library.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
                if (Okay(receipt) && searchPolicyUnconfirmed)
                {
                    await aggregateSearch.CancelShutdownPreparationAsync().ConfigureAwait(false);
                    searchPolicyUnconfirmed = false;
                }
                return receipt;
            }
            if (searchPolicyUnconfirmed) return Failed("SEARCH_SETTINGS_UNCONFIRMED");
            if (method == "music.search.start")
            {
                var input = parameters is JsonElement element ? element : JsonSerializer.SerializeToElement(parameters);
                if (input.ValueKind != JsonValueKind.Object ||
                    input.EnumerateObject().Any(p => p.Name is not ("keyword" or "kind" or "platforms")) ||
                    !input.TryGetProperty("keyword", out var word) || word.ValueKind != JsonValueKind.String ||
                    !input.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
                    kind.GetString() is not ("songs" or "playlists")) return Failed("INVALID_REQUEST");
                var policy = await library.ExecuteAsync("music.search.settings.get", new { }, token).ConfigureAwait(false);
                if (!Okay(policy)) return policy;
                var settings = policy.GetProperty("settings")
                    .GetProperty(kind.GetString() == "songs" ? "songs" : "playlists");
                string[] platforms = settings.GetProperty("mode").GetString() == "single"
                    ? [settings.GetProperty("platform").GetString()!]
                    : settings.GetProperty("enabled").EnumerateArray().Select(p => p.GetString()!).ToArray();
                // A trusted UI still cannot dispatch its unsaved draft or a
                // retired preference revision. Empty saved selections stay empty.
                if (!input.TryGetProperty("platforms", out var requested) || requested.ValueKind != JsonValueKind.Array ||
                    !requested.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String) ||
                    !requested.EnumerateArray().Select(p => p.GetString()).SequenceEqual(platforms))
                    return Failed("SEARCH_SETTINGS_CHANGED");
                // Hydrate the native account snapshot before registering either
                // query. Initial durable-load notifications are not a logout.
                var restored = await accounts.EnsureInitialObservationAsync(token).ConfigureAwait(false);
                if (!Okay(restored)) return restored;
            }
            return await aggregateSearch.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
        }
        finally { searchPolicyLane.Release(); }
    }

    private static bool Okay(JsonElement reply) =>
        reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
}
