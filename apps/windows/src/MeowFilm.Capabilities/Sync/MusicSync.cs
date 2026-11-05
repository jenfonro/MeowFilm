using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
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

    public Task ActivateLibrarySyncAsync() => stopping || disposed
        ? Task.CompletedTask : LibrarySync.ActivateAsync(lifetime.Token);
    public Task DeactivateLibrarySyncAsync() => LibrarySync.DeactivateAsync();

    private void LibrarySyncChanged()
    {
        // The native coordinator can finish on a pool thread. Coalesce refreshes
        // on the existing UI command lane, never create another library writer.
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return;
        _ = Presentation.Dispatcher.InvokeAsync(() =>
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
                        long origin = loadGeneration;
                        await RefreshPlaylists(lifetime.Token);
                        // Sync updates the local library, not catalog navigation,
                        // playback, online source selection or a lyrics request.
                        // location describes the last published page while navigation is loading.
                        // Never cancel that new destination by refreshing the old local page.
                        if (origin == loadGeneration && loadCancellation is null && !IsCatalogPage && !IsLyrics &&
                            (location.Route is "favorites" or "search" ||
                             location.Route.StartsWith("playlist/", StringComparison.Ordinal)))
                            await LoadAsync(CaptureLocation(), lifetime.Token, publishStatus: false);
                        if (Presentation.IsSettingsActive && LibrarySync.CanEdit)
                            await LibrarySync.RefreshStatusAsync();
                    }
                }
                finally { syncRefreshQueued = false; }
            }, publishStatus: false);
        });
    }

    private async ValueTask DisposeLibrarySyncAsync()
    {
        application.LibrarySyncChanged -= LibrarySyncChanged;
        await LibrarySync.DisposeAsync();
    }
}
