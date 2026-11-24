using MeowFilm.AppHost;
using MeowFilm.App;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.SystemMedia;
using System.IO;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var (debugOptions, debugError) = DebugLaunchOptions.Parse(args);
        if (debugError is not null)
        {
            Console.Error.WriteLine(debugError);
            Environment.ExitCode = 2;
            return;
        }
        if (!AppInstallationLifetime.TryEnter())
        {
            MessageBox(0, $"正在安装或卸载 {AppIdentity.Name}，请完成后重试。", AppIdentity.Name, 0x40);
            return;
        }
        var effectivePaths = AppDataPaths.Resolve(debugOptions);
        NativeAudioPlayback? musicPlayback = null;
        FilmPlayback? filmPlayback = null;
        FilmCapability? filmCapability = null;
        AppPlaybackCoordinator? playbackCoordinator = null;
        FilmRuleService? pendingRules = null;
        FilmSourceService? pendingSources = null;
        FilmMetadataService? pendingMetadata = null;
        SearchResultsPage? searchPage = null;
        AppMediaWindowAttachment? pendingMediaAttachment = null;
        MusicLibraryDatabase? musicLibraryStore = null;
        AppLauncher.Run(new(AppIdentity.RuntimeId, AppIdentity.ScriptModules),
            (runtime, dataStorage, cacheStorage, capabilities) =>
            {
                playbackCoordinator = new(
                    stopMusic: () => musicPlayback?.StopAsync() ?? Task.CompletedTask,
                    stopFilm: () => filmCapability?.PreemptPlaybackAsync() ?? filmPlayback?.StopAsync() ?? Task.CompletedTask,
                    hasOutput: owner => owner == "film"
                        ? filmPlayback?.HasOutput == true
                        : musicPlayback is { HasMedia: true } or { IsPlaying: true });
                musicPlayback = new NativeAudioPlayback(acquireOutput: playbackCoordinator.AcquireMusicAsync);
                filmPlayback = new FilmPlayback(acquireOutput: playbackCoordinator.AcquireFilmAsync);
                musicPlayback.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(NativeAudioPlayback.HasMedia) or nameof(NativeAudioPlayback.IsPlaying) &&
                        musicPlayback is { HasMedia: false, IsPlaying: false })
                        playbackCoordinator?.ReleaseOwner("music");
                };
                filmPlayback.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(FilmPlayback.HasOutput) && filmPlayback is { HasOutput: false })
                        playbackCoordinator?.ReleaseOwner("film");
                };
                var cacheRoot = effectivePaths.Caches;
                musicLibraryStore = new(dataStorage);
                capabilities.Add(new MusicCapability(runtime, dataStorage, cacheStorage, musicLibraryStore, musicPlayback,
                    Path.Combine(cacheRoot, "music-audio")));
                pendingRules = new FilmRuleService(runtime, dataStorage);
                pendingSources = new FilmSourceService(runtime, dataStorage);
                pendingMetadata = new FilmMetadataService(runtime, dataStorage);
                var capture = runtime as IRuntimeConnectionSource ?? throw new InvalidOperationException("RUNTIME_CAPTURE_UNAVAILABLE");
                filmCapability = new FilmCapability(pendingRules, pendingSources, runtime, dataStorage, filmPlayback,
                    capture.CaptureConnectionAsync, pendingMetadata);
                capabilities.Add(filmCapability);
                // The completed module owns these services. On a constructor
                // failure the App resource callback still owns the partial pair.
                pendingRules = null;
                pendingSources = null;
                pendingMetadata = null;
                capabilities.Add(new NovelCapability(runtime, dataStorage));
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
                    searchPage?.Dispose(); searchPage = null;
                    await Task.WhenAll(pendingRules?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingSources?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingMetadata?.DisposeAsync().AsTask() ?? Task.CompletedTask);
                    pendingRules = null; pendingSources = null; pendingMetadata = null;
                }
                finally
                {
                    try
                    {
                        if (musicLibraryStore is not null) { await musicLibraryStore.DisposeAsync(); musicLibraryStore = null; }
                    }
                    finally
                    {
                        try
                        {
                            if (musicPlayback is not null) { await musicPlayback.DisposeAsync(); musicPlayback = null; }
                        }
                        finally
                        {
                            if (filmPlayback is not null) { await filmPlayback.DisposeAsync(); filmPlayback = null; }
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
                var shell = (ShellWindow)window;
                shell.ActivePlaybackOwnerProvider = () => playbackCoordinator?.ActiveOwner;
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
                music.Lyrics.AttachDesktopLyricsOwner(window, shell.RestoreFromTray);
                var systemMedia = new AppSystemMediaAttachment(window, musicPlayback!, filmPlayback!, film,
                    playbackCoordinator!,
                    [new("music", _ => music.Playback.StepAsync(true), _ => music.Playback.StepAsync(false),
                        Play: _ => music.Playback.ResumePlaybackAsync()),
                     new("film", _ => film.StepAsync(true), _ => film.StepAsync(false), Kind: SystemPlaybackKind.Video,
                        Play: _ => film.ResumePlaybackAsync())],
                    enabled: settings.IsAvailable && settings.Preferences.SystemMediaEnabled);
                attachment.OwnSystemMedia(systemMedia, systemMedia.SetEnabled);
                attachment.OwnShortcuts(new MediaShortcutWindowAttachment(window, settings, action =>
                    playbackCoordinator!.ActiveOwner == "film" && action is MediaShortcutAction.TogglePlayback or
                        MediaShortcutAction.Previous or MediaShortcutAction.Next or
                        MediaShortcutAction.VolumeUp or MediaShortcutAction.VolumeDown
                        ? film.ExecuteMediaShortcutAsync(action)
                        : music.ExecuteMediaShortcutAsync(action)));
                pendingMediaAttachment = null;
                return attachment;
            },
            capabilities => [searchPage = new SearchResultsPage(capabilities.OfType<MusicCapability>().Single(),
                capabilities.OfType<FilmCapability>().Single(), capabilities.OfType<NovelCapability>().Single())],
            runtimeSettings: database => new FilmRuntimeSettings(database),
            stopPlayback: () => Task.WhenAll(musicPlayback?.StopAsync() ?? Task.CompletedTask,
                filmCapability?.PrepareShutdownAsync() ?? filmPlayback?.StopAsync() ?? Task.CompletedTask),
            debugOptions: debugOptions.Enabled ? debugOptions : null);
    }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint flags);
}
