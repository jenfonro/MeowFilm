using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly MusicSearchCoordinator searchCoordinator;
    private readonly SemaphoreSlim searchPolicyLane = new(1, 1);
    private bool searchPolicyUnconfirmed;
    public event Action<MusicSearchSnapshot>? SearchChanged
    {
        add => searchCoordinator.Changed += value;
        remove => searchCoordinator.Changed -= value;
    }

    private async Task<JsonElement> SearchCatalogAsync(MusicSearchRequest request, CancellationToken token)
    {
        // The coordinator fetches after its admission receipt has returned.
        // Capture the actual lease inside each physical request, not in start().
        using var frame = sessions.Begin();
        long observation = library.BeginSongObservation();
        try
        {
            token.ThrowIfCancellationRequested();
            // Stable search/matching owners retain the real operation through their
            // slot drain; honor their deadline and invalidation token on the transport read.
            var result = await catalog.SearchAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            result = await library.AdoptPlatformResultAsync(result, token, observation: observation,
                shape: request.Kind == MusicSearchKind.Songs ? MusicLibraryOwner.PlatformSongShape.Page
                    : MusicLibraryOwner.PlatformSongShape.None).ConfigureAwait(false);
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

    public Task<MusicSearchPolicyRecord> GetSearchPolicyAsync(string? kind = null, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var policy = await library.GetSearchPolicyAsync(kind, token).ConfigureAwait(false);
                if (searchPolicyUnconfirmed)
                {
                    await searchCoordinator.CancelShutdownPreparationAsync().ConfigureAwait(false);
                    searchPolicyUnconfirmed = false;
                }
                return policy;
            }
            finally { searchPolicyLane.Release(); }
        }, cancellationToken);

    public Task<MusicSearchPolicyRecord> SaveSearchPolicyAsync(long ifRevision, MusicSearchKindRecord songs,
        MusicSearchKindRecord playlists, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // A lost CAS receipt may already have disabled providers. Fence
                // and physically drain both kinds before the policy write.
                searchPolicyUnconfirmed = true;
                await searchCoordinator.PrepareShutdownAsync().ConfigureAwait(false);
                var saved = await library.SaveSearchPolicyAsync(ifRevision, songs, playlists, token).ConfigureAwait(false);
                await searchCoordinator.CancelShutdownPreparationAsync().ConfigureAwait(false);
                searchPolicyUnconfirmed = false;
                return saved;
            }
            finally { searchPolicyLane.Release(); }
        }, cancellationToken);

    public Task<MusicSearchHistoryRecord> GetSearchHistoryAsync(CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => library.GetSearchHistoryAsync(token), cancellationToken);

    public Task<MusicSearchHistoryRecord> RecordSearchHistoryAsync(string text, string kind, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => library.RecordSearchHistoryAsync(text, kind, token), cancellationToken);

    public Task<MusicSearchHistoryRecord> ClearSearchHistoryAsync(long ifRevision, bool confirm, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => library.ClearSearchHistoryAsync(ifRevision, confirm, token), cancellationToken);

    public Task<MusicSearchSnapshot> StartSearchAsync(string keyword, string kind, string[] platforms, bool refresh = false,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(kind is "songs" or "playlists");
            await accounts.EnsureInitialObservationAsync(token).ConfigureAwait(false);
            await PrepareSongFactsAsync(token).ConfigureAwait(false);
            Task<MusicSearchSnapshot> start;
            await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (searchPolicyUnconfirmed) throw new MusicOperationException("SEARCH_SETTINGS_UNCONFIRMED");
                var policy = await library.GetSearchPolicyAsync(kind, token).ConfigureAwait(false);
                var kindPolicy = kind == "songs" ? policy.Songs : policy.Playlists;
                string[] savedPlatforms = kindPolicy.Enabled;
                if (!platforms.SequenceEqual(savedPlatforms, StringComparer.Ordinal))
                    throw new MusicOperationException("SEARCH_SETTINGS_CHANGED");
                start = searchCoordinator.StartAdmittedAsync(
                    keyword, kind, savedPlatforms,
                    (int)kindPolicy.Pages, (int)kindPolicy.Concurrency, (int)kindPolicy.CacheMinutes,
                    refresh, token);
            }
            finally { searchPolicyLane.Release(); }
            return await start.ConfigureAwait(false);
        }, cancellationToken);

    public Task<MusicSearchSnapshot> GetSearchAsync(string id, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => searchCoordinator.GetAsync(id, token), cancellationToken);

    public Task<MusicSearchSnapshot> NextSearchAsync(string id, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => searchCoordinator.NextAsync(id, token), cancellationToken);

    public Task<MusicSearchSnapshot> CancelSearchAsync(string id, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => searchCoordinator.CancelAsync(id, token), cancellationToken);
}
