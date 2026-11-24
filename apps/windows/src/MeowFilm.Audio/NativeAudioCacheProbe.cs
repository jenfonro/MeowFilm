using System.IO;

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
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var decoder = await AudioDecoder.OpenAsync(new Uri(Path.GetFullPath(file)),
            new Dictionary<string, string>(), deadline.Token).ConfigureAwait(false);
        return await AudioWorkerQueue.RunAsync(() =>
        {
            using (decoder)
            {
                double duration = decoder.DurationSeconds;
                if (!double.IsFinite(duration) || duration <= 0 || expectedDuration > 0 && Math.Abs(duration - expectedDuration) > 10)
                    throw new PlaybackException(PlaybackError.ReadFailed);
                _ = decoder.Seek(Math.Max(0, duration - .5), deadline.Token);
                // Silence is intentional: no output device or media session is created.
                // Drain all three stages, not just the first frame accepted by a decoder.
                var samples = new float[8192];
                long decoded = 0;
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    int count = decoder.Read(samples, 0, samples.Length, deadline.Token);
                    if (count == 0) break;
                    decoded += count;
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (decoded == 0) throw new PlaybackException(PlaybackError.ReadFailed);
                return duration;
            }
        }).ConfigureAwait(false);
    }
}
