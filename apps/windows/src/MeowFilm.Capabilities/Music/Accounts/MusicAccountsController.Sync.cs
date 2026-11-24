using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

public sealed partial class MusicAccountsController
{
    private bool syncRefreshQueued, syncRefreshRequested;

    private Task<bool> ConfirmResetSyncAccountAsync(string platform, CancellationToken token) =>
        ConfirmAsync(
            $"{MusicPlatformInfo.DisplayName(platform)}已切换账号，是否解绑旧账号的同步关系？（不会删除本地歌曲与歌单）",
            "平台同步",
            token);

    public async Task ResetPlatformSyncAccountAsync(string platform)
    {
        if (stopping || disposed || string.IsNullOrEmpty(platform)) return;
        if (!await ConfirmResetSyncAccountAsync(platform, lifetime.Token)) return;
        await LibrarySync.ResetAccountAsync(platform, confirmed: true);
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
                        if (LibrarySynced is { } synced) await synced(origin, lifetime.Token);
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
