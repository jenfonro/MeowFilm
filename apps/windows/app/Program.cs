using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using MeowFilm.SystemMedia;
using System.IO;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        NativePlayback? playback = null;
        AppLauncher.Run(new(AppIdentity.RuntimeId, AppIdentity.ScriptModules,
            AppIdentity.NativeNamespaces, [], AppIdentity.NativeCollections),
            (runtime, storage, capabilities) =>
            {
                playback = new NativePlayback();
                capabilities.Add(new MusicCapability(runtime, storage, playback,
                    Path.Combine(AppDataPaths.Current.Documents, "music-playback-cache")));
            },
            async () => { if (playback is not null) await playback.DisposeAsync(); },
            (window, capabilities) =>
            {
                var music = capabilities.OfType<MusicCapability>().Single();
                return new SystemMediaWindowAttachment(window, playback!,
                    [new("music", _ => music.StepAsync(true), _ => music.StepAsync(false))]);
            });
    }
}
