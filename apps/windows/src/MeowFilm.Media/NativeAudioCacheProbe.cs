using System.Globalization;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

/// <summary>
/// On-demand silent local-file validation. No system media session, network input, audible output
/// or persistent player. The caller serializes work and validates complete container structure first.
/// </summary>
public static class NativeAudioCacheProbe
{
    public static async Task<double> ValidateAsync(string file, double expectedDuration, CancellationToken token)
    {
        if (!double.IsFinite(expectedDuration) || expectedDuration < 0)
            throw new PlaybackException(PlaybackError.InvalidRequest);
        return await Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            NativeRuntimeModules.EnsureLoaded(null);
            using var engine = new LibVLC(false, "--quiet", "--ignore-config", "--no-interact", "--keystore=none",
                "--no-video", "--no-osd", "--no-sub-autodetect-file", "--no-metadata-network-access");
            engine.Log += (_, _) => { };
            using var media = new LibVLCSharp.Shared.Media(engine, file, FromType.FromPath);
            // LibVLCSharp Parse completes inline on the native parser callback. Stay on this
            // worker when rejecting a duration: disposing LibVLC from that callback deadlocks.
            media.Parse(MediaParseOptions.ParseLocal, 5000, deadline.Token).GetAwaiter().GetResult();
            double duration = media.Duration / 1000d;
            if (!double.IsFinite(duration) || duration <= 0 || expectedDuration > 0 && Math.Abs(duration - expectedDuration) > 10)
                throw new PlaybackException(PlaybackError.ReadFailed);
            media.AddOption(":start-time=" + Math.Max(0, duration - .5).ToString("R", CultureInfo.InvariantCulture));
            using var player = new MediaPlayer(media);
            var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Audio callbacks replace audio output entirely; samples are not copied into managed memory.
            player.SetAudioFormat("S16N", 44100, 2);
            player.SetAudioCallbacks((_, _, count, _) => { if (count > 0) decoded.TrySetResult(); }, null, null, null, null);
            player.EncounteredError += (_, _) => decoded.TrySetException(new PlaybackException(PlaybackError.ReadFailed));
            try
            {
                if (!player.Play()) throw new PlaybackException(PlaybackError.ReadFailed);
                await decoded.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return duration;
            }
            finally { player.Stop(); }
        }, token).ConfigureAwait(false);
    }
}
