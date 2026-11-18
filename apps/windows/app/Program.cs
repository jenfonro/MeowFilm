using MeowFilm.AppHost;
using MeowFilm.App;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.SystemMedia;
using MeowFilm.Platform.Storage;
using System.IO;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!AppInstallationLifetime.TryEnter())
        {
            MessageBox(0, "正在安装或卸载 meowfilm，请完成后重试。", "meowfilm", 0x40);
            return;
        }
        NativeAudioPlayback? musicPlayback = null;
        FilmPlayback? filmPlayback = null;
        AppPlaybackCoordinator? playbackCoordinator = null;
        FilmRuleService? pendingRules = null;
        FilmSourceService? pendingSources = null;
        FilmMetadataService? pendingMetadata = null;
        NovelCapability? pendingNovel = null;
        SearchResultsPage? searchPage = null;
        AppMediaWindowAttachment? pendingMediaAttachment = null;
        CacheDocumentStore? cacheStore = null;
        MusicLibraryDatabase? musicLibraryStore = null;
        AppLauncher.Run(new(AppIdentity.RuntimeId, AppIdentity.ScriptModules,
            AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces),
            (runtime, storage, capabilities) =>
            {
                playbackCoordinator = new(
                    () => musicPlayback?.StopAsync() ?? Task.CompletedTask,
                    () => filmPlayback?.StopAsync() ?? Task.CompletedTask);
                musicPlayback = new NativeAudioPlayback(acquireOutput: playbackCoordinator.AcquireMusicAsync);
                filmPlayback = new FilmPlayback(acquireOutput: playbackCoordinator.AcquireFilmAsync);
                var cacheRoot = AppDataPaths.Current.Caches;
                cacheStore = new CacheDocumentStore(() =>
                {
                    Directory.CreateDirectory(cacheRoot);
                    return new LocalDocumentStore(cacheRoot, AppIdentity.CacheNamespaces,
                        protectedNamespaces: AppIdentity.CacheProtectedNamespaces);
                });
                musicLibraryStore = new(Path.Combine(AppDataPaths.Current.Documents, "music-library"));
                capabilities.Add(new MusicCapability(runtime, storage, musicLibraryStore, musicPlayback,
                    Path.Combine(cacheRoot, "music-audio"), cacheStore,
                    Path.Combine(cacheRoot, "music-presentation-v2")));
                pendingRules = new FilmRuleService(runtime, storage);
                pendingSources = new FilmSourceService(runtime, storage);
                pendingMetadata = new FilmMetadataService(runtime, storage);
                var capture = runtime as IRuntimeConnectionSource ?? throw new InvalidOperationException("RUNTIME_CAPTURE_UNAVAILABLE");
                capabilities.Add(new FilmCapability(pendingRules, pendingSources, runtime, storage, filmPlayback,
                    capture.CaptureConnectionAsync, pendingMetadata));
                // The completed module owns these services. On a constructor
                // failure the App resource callback still owns the partial pair.
                pendingRules = null;
                pendingSources = null;
                pendingMetadata = null;
                pendingNovel = new NovelCapability(runtime, storage);
                capabilities.Add(pendingNovel);
                pendingNovel = null;
            },
            launch => new NativeApplication(launch),
            async () =>
            {
                if (pendingMediaAttachment is not null)
                {
                    await pendingMediaAttachment.DisposeAsync();
                    pendingMediaAttachment = null;
                }
                try
                {
                    searchPage?.Dispose();
                    searchPage = null;
                    await Task.WhenAll(pendingRules?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingSources?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingMetadata?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingNovel?.DisposeAsync().AsTask() ?? Task.CompletedTask);
                    pendingRules = null;
                    pendingSources = null;
                    pendingMetadata = null;
                    pendingNovel = null;
                }
                finally
                {
                    try
                    {
                        if (musicLibraryStore is not null)
                        {
                            await musicLibraryStore.DisposeAsync();
                            musicLibraryStore = null;
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (cacheStore is not null)
                            {
                                await cacheStore.DisposeAsync();
                                cacheStore = null;
                            }
                        }
                        finally
                        {
                            try
                            {
                                if (musicPlayback is not null)
                                {
                                    await musicPlayback.DisposeAsync();
                                    musicPlayback = null;
                                }
                            }
                            finally
                            {
                                if (filmPlayback is not null)
                                {
                                    await filmPlayback.DisposeAsync();
                                    filmPlayback = null;
                                }
                            }
                        }
                    }
                }
            },
            (window, capabilities) =>
            {
                var music = capabilities.OfType<MusicCapability>().Single();
                var film = capabilities.OfType<FilmCapability>().Single();
                var novel = capabilities.OfType<NovelCapability>().Single();
                var page = searchPage ?? throw new InvalidOperationException("Search page is missing.");
                var shell = (MeowFilm.Desktop.ShellWindow)window;
                page.NavigationToken = () => shell.GetPageNavigationToken(page, film.Id, "film-playback");
                page.NavigateToPlaybackAsync = async (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await film.ReceiveSearchPlaybackAsync(item, token);
                    // The page lease is admission only; leaving retires that lease itself.
                    await shell.NavigateFromPageAsync(page, film.Id, "film-playback");
                };
                page.NovelNavigationToken = () => shell.GetPageNavigationToken(page, novel.Id, NovelCapability.BookRoute);
                page.NavigateToNovelAsync = async (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await novel.ReceiveBookAsync(item, token);
                    await shell.NavigateFromPageAsync(page, novel.Id, NovelCapability.BookRoute);
                };
                // Platform callbacks are composed before this attachment. Host
                // restoration is App-owned and independent of the selected page.
                _ = film.StartSourcesAsync();
                _ = novel.StartSourcesAsync();
                var settings = shell.MediaControls ?? throw new InvalidOperationException("MEDIA_CONTROLS_MISSING");
                var attachment = pendingMediaAttachment = new AppMediaWindowAttachment(window, settings);
                music.AttachDesktopLyricsOwner(window, shell.RestoreFromTray);
                var systemMedia = new AppSystemMediaAttachment(window, musicPlayback!, filmPlayback!, film,
                    () => playbackCoordinator!.ActiveOwner,
                    [new("music", _ => music.StepAsync(true), _ => music.StepAsync(false),
                        Play: _ => music.ResumePlaybackAsync()),
                     new("film", _ => film.StepAsync(true), _ => film.StepAsync(false), Kind: SystemPlaybackKind.Video,
                        Play: _ => film.ResumePlaybackAsync())],
                    enabled: settings.IsAvailable && settings.Preferences.SystemMediaEnabled);
                attachment.OwnSystemMedia(systemMedia, systemMedia.SetEnabled);
                attachment.OwnShortcuts(new MediaShortcutWindowAttachment(window, settings, music.ExecuteMediaShortcutAsync));
                pendingMediaAttachment = null;
                return attachment;
            },
            capabilities => [searchPage = new SearchResultsPage(capabilities.OfType<MusicCapability>().Single(),
                capabilities.OfType<FilmCapability>().Single(), capabilities.OfType<NovelCapability>().Single())]);
    }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint flags);
}
