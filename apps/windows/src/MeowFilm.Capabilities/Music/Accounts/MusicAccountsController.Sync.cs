using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicAccountsController
{
    public MusicLibrarySyncViewModel LibrarySync { get; private set; } = null!;
    private bool syncRefreshQueued, syncRefreshRequested;

    private void InitializeLibrarySync()
    {
        LibrarySync = new((method, parameters, token) =>
            stopping || disposed
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { ok = false, code = "CLOSED" }))
                : application.ExecuteAsync(method, parameters, token));
        application.LibrarySyncChanged += LibrarySyncChanged;
    }

    private void LibrarySyncChanged()
    {
        // The native coordinator can finish on a pool thread. Coalesce refreshes
        // on the existing UI command lane, never create another library writer.
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (stopping || disposed) return;
            syncRefreshRequested = true;
            if (syncRefreshQueued) return;
            syncRefreshQueued = true;
            _ = RunAsync(async () =>
            {
                try
                {
                    while (syncRefreshRequested && !stopping && !disposed)
                    {
                        syncRefreshRequested = false;
                        long origin = ctx.LoadGeneration;
                        await OnRefreshLibraryPlaylistsAsync(lifetime.Token);
                        // Sync updates the local library, not catalog navigation,
                        // playback, online source selection or a lyrics request.
                        // location describes the last published page while navigation is loading.
                        // Never cancel that new destination by refreshing the old local page.
                        var route = ctx.CurrentRoute;
                        if (origin == ctx.LoadGeneration && ctx.LoadCancellation is null && !GetIsCatalogPage() && !GetIsLyrics() &&
                            (route is "favorites" or "search" ||
                             route.StartsWith("playlist/", StringComparison.Ordinal)))
                            await ctx.LoadAsync(ctx.CaptureLocation(), lifetime.Token, publishStatus: false);
                        if (ctx.IsSettingsActive() && LibrarySync.CanEdit)
                            await LibrarySync.RefreshStatusAsync();
                    }
                }
                finally { syncRefreshQueued = false; }
            }, publishStatus: false);
        });
    }

    internal async ValueTask DisposeLibrarySyncAsync()
    {
        application.LibrarySyncChanged -= LibrarySyncChanged;
        await LibrarySync.DisposeAsync();
    }
}
