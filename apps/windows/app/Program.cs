using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
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
        AppLauncher.Run(new(AppIdentity.RuntimeId, AppIdentity.ScriptModules,
            AppIdentity.NativeNamespaces, [], AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces),
            (runtime, storage, capabilities) =>
            {
                playback = new NativePlayback();
                capabilities.Add(new MusicCapability(runtime, storage, playback,
                    Path.Combine(AppDataPaths.Current.Documents, "music-playback-cache")));
                pendingRules = new FilmRuleService(
                    new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), storage);
                pendingSources = new FilmSourceService(runtime, storage);
                capabilities.Add(new FilmCapability(pendingRules, pendingSources));
                // The completed module owns these services. On a constructor
                // failure the App resource callback still owns the partial pair.
                pendingRules = null;
                pendingSources = null;
            },
            async () =>
            {
                try
                {
                    await Task.WhenAll(pendingRules?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                        pendingSources?.DisposeAsync().AsTask() ?? Task.CompletedTask);
                }
                finally { if (playback is not null) await playback.DisposeAsync(); }
            },
            (window, capabilities) =>
            {
                var music = capabilities.OfType<MusicCapability>().Single();
                // Platform callbacks are composed before this attachment. Host
                // restoration is App-owned and independent of the selected page.
                _ = capabilities.OfType<FilmCapability>().Single().StartSourcesAsync();
                return new SystemMediaWindowAttachment(window, playback!,
                    [new("music", _ => music.StepAsync(true), _ => music.StepAsync(false))]);
            });
    }
}
