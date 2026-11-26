using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly MusicSearchCoordinator searchCoordinator;
    private readonly SemaphoreSlim searchPolicyLane = new(1, 1);

    private async Task<JsonElement> SearchCatalogAsync(MusicSearchRequest request, CancellationToken token)
    {
        using var frame = sessions.Begin();
        long observation = library.BeginSongObservation();
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await catalog.SearchAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            result = await library.AdoptPlatformResultAsync(result, token, observation: observation,
                shape: request.Kind == MusicSearchKind.Songs ? MusicLibraryOwner.PlatformSongShape.Page
                    : MusicLibraryOwner.PlatformSongShape.None).ConfigureAwait(false);
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

    public Task<MusicSearchPolicyRecord> SaveSearchPolicyAsync(long ifRevision, MusicSearchKindRecord songs,
        MusicSearchKindRecord playlists, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            await searchPolicyLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await searchCoordinator.PrepareShutdownAsync().ConfigureAwait(false);
                try
                {
                    return await library.SaveSearchPolicyAsync(ifRevision, songs, playlists, token).ConfigureAwait(false);
                }
                finally
                {
                    await searchCoordinator.CancelShutdownPreparationAsync().ConfigureAwait(false);
                }
            }
            finally { searchPolicyLane.Release(); }
        }, cancellationToken);

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
}
