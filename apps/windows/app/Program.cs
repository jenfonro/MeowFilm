using MeowFilm.AppHost;
using MeowFilm.App;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Platform.Scripting;
using MeowFilm.SystemMedia;
using System.IO;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        NativePlayback? playback = null;
        FilmRuleService? pendingRules = null;
        FilmSourceService? pendingSources = null;
        FilmMetadataService? pendingMetadata = null;
        SearchResultsPage? searchPage = null;
        AppMediaWindowAttachment? pendingMediaAttachment = null;
        AppLauncher.Run(new(AppIdentity.RuntimeId, AppIdentity.ScriptModules,
            AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces),
            (runtime, storage, capabilities) =>
            {
                playback = new NativePlayback();
                capabilities.Add(new MusicCapability(runtime, storage, playback,
                    Path.Combine(AppDataPaths.Current.Documents, "music-playback-cache")));
                pendingRules = new FilmRuleService(
                    new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), storage);
                pendingSources = new FilmSourceService(runtime, storage);
                pendingMetadata = new FilmMetadataService(runtime);
                capabilities.Add(new FilmCapability(pendingRules, pendingSources, pendingMetadata));
                // The completed module owns these services. On a constructor
                // failure the App resource callback still owns the partial pair.
                pendingRules = null;
                pendingSources = null;
                pendingMetadata = null;
            },
            async () =>
            {
                if (pendingMediaAttachment is not null) await pendingMediaAttachment.DisposeAsync();
                try
                {
                    searchPage?.Dispose();
                    await Task.WhenAll(pendingRules?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingSources?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingMetadata?.DisposeAsync().AsTask() ?? Task.CompletedTask);
                }
                finally { if (playback is not null) await playback.DisposeAsync(); }
            },
            (window, capabilities) =>
            {
                var music = capabilities.OfType<MusicCapability>().Single();
                var film = capabilities.OfType<FilmCapability>().Single();
                var page = searchPage ?? throw new InvalidOperationException("Search page is missing.");
                var shell = (MeowFilm.Desktop.ShellWindow)window;
                page.NavigationToken = () => shell.GetPageNavigationToken(page, film.Id, "film-playback");
                page.NavigateToPlaybackAsync = async (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    film.ReceiveSearchPlayback(item);
                    // The page lease is admission only; leaving retires that lease itself.
                    await shell.NavigateFromPageAsync(page, film.Id, "film-playback");
                };
                // Platform callbacks are composed before this attachment. Host
                // restoration is App-owned and independent of the selected page.
                _ = capabilities.OfType<FilmCapability>().Single().StartSourcesAsync();
                var settings = shell.MediaControls ?? throw new InvalidOperationException("MEDIA_CONTROLS_MISSING");
                var attachment = pendingMediaAttachment = new AppMediaWindowAttachment(window, settings);
                music.AttachDesktopLyricsOwner(window);
                var systemMedia = new SystemMediaWindowAttachment(window, playback!,
                    [new("music", _ => music.StepAsync(true), _ => music.StepAsync(false),
                        Play: _ => music.ResumePlaybackAsync())],
                    enabled: settings.IsAvailable && settings.Preferences.SystemMediaEnabled);
                attachment.OwnSystemMedia(systemMedia, systemMedia.SetEnabled);
                attachment.OwnShortcuts(new MediaShortcutWindowAttachment(window, settings, music.ExecuteMediaShortcutAsync));
                pendingMediaAttachment = null;
                return attachment;
            },
            capabilities => [searchPage = new SearchResultsPage(capabilities.OfType<MusicCapability>().Single(),
                capabilities.OfType<FilmCapability>().Single())]);
    }
}
