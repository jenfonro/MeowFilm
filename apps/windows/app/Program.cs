using MeowFilm.AppHost;
using MeowFilm.App;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.SystemMedia;
using MeowFilm.Platform.Storage;
using System.IO;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!AppInstallationLifetime.TryEnter())
        {
            System.Windows.MessageBox.Show("正在安装或卸载 meowfilm，请完成后重试。", "meowfilm",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        NativePlayback? playback = null;
        FilmRuleService? pendingRules = null;
        FilmSourceService? pendingSources = null;
        FilmMetadataService? pendingMetadata = null;
        NovelCapability? pendingNovel = null;
        SearchResultsPage? searchPage = null;
        AppMediaWindowAttachment? pendingMediaAttachment = null;
        CacheDocumentStore? cacheStore = null;
        AppLauncher.Run(new(AppIdentity.RuntimeId, AppIdentity.ScriptModules,
            AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces),
            (runtime, storage, capabilities) =>
            {
                playback = new NativePlayback();
                var cacheRoot = AppDataPaths.Current.Caches;
                cacheStore = new CacheDocumentStore(() =>
                {
                    Directory.CreateDirectory(cacheRoot);
                    return new LocalDocumentStore(cacheRoot, AppIdentity.CacheNamespaces,
                        protectedNamespaces: AppIdentity.CacheProtectedNamespaces);
                });
                capabilities.Add(new MusicCapability(runtime, storage, playback,
                    Path.Combine(cacheRoot, "audio"), cacheStore));
                pendingRules = new FilmRuleService(runtime);
                pendingSources = new FilmSourceService(runtime, storage);
                pendingMetadata = new FilmMetadataService(runtime);
                var capture = runtime as IRuntimeConnectionSource ?? throw new InvalidOperationException("RUNTIME_CAPTURE_UNAVAILABLE");
                capabilities.Add(new FilmCapability(pendingRules, pendingSources, runtime, storage, playback,
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
                        if (cacheStore is not null)
                        {
                            await cacheStore.DisposeAsync();
                            cacheStore = null;
                        }
                    }
                    finally
                    {
                        if (playback is not null)
                        {
                            await playback.DisposeAsync();
                            playback = null;
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
                var settings = shell.MediaControls ?? throw new InvalidOperationException("MEDIA_CONTROLS_MISSING");
                var attachment = pendingMediaAttachment = new AppMediaWindowAttachment(window, settings);
                music.AttachDesktopLyricsOwner(window);
                var systemMedia = new AppSystemMediaAttachment(window, playback!, film,
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
}
