using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

public static class MediaTrackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("initial track state is passive and forged scopes cannot start playback", async () =>
        {
            await using var player = New();
            Check(player.Tracks is null && await player.ReadTracksAsync() is null);
            await Error(() => player.SetRateAsync(new("film", "absent", 0), 1.25), PlaybackError.Superseded);
            Check(!player.HasMedia && player.NativeVersion is null);
        });
        yield return Case("real audio track list selects native ID and clears on stop", async () =>
        {
            using var wave = new Wave(); await using var player = New();
            var tracks = await Start(player, wave);
            int nativeId = tracks.Audio.First(track => track.Id >= 0).Id;
            Check(tracks.Scope.OwnerId == "film" && tracks.Scope.Identity == "fixture");
            Check(tracks.Audio.Select(track => track.Id).Distinct().Count() == tracks.Audio.Count);
            await player.SelectAudioTrackAsync(tracks.Scope, nativeId);
            await Until(async () => (await player.ReadTracksAsync())!.AudioId == nativeId);
            if (tracks.Audio.Any(track => track.Id == -1))
            {
                await player.SelectAudioTrackAsync(tracks.Scope, -1);
                await Until(async () => (await player.ReadTracksAsync())!.AudioId == -1);
                await player.SelectAudioTrackAsync(tracks.Scope, nativeId);
            }
            await player.StopAsync();
            Check(player.Tracks is null && await player.ReadTracksAsync() is null);
            await Error(() => player.SelectAudioTrackAsync(tracks.Scope, nativeId), PlaybackError.Superseded);
        });
        yield return Case("invalid tracks rate delay leave native media session and current audio intact", async () =>
        {
            using var wave = new Wave(); await using var player = New();
            var before = await Start(player, wave);
            await Error(() => player.SelectAudioTrackAsync(before.Scope, int.MaxValue), PlaybackError.InvalidTrack);
            await Error(() => player.SelectSubtitleTrackAsync(before.Scope, int.MaxValue), PlaybackError.InvalidTrack);
            foreach (double rate in new[] { double.NaN, double.PositiveInfinity, -1, 0, 0.1, 4.1 })
                await Error(() => player.SetRateAsync(before.Scope, rate), PlaybackError.InvalidRate);
            await Error(() => player.SetSubtitleDelayAsync(before.Scope, double.NaN), PlaybackError.InvalidSubtitleDelay);
            await Error(() => player.SetSubtitleDelayAsync(before.Scope, 121), PlaybackError.InvalidSubtitleDelay);
            if (before.Subtitles.Count == 0)
                await Error(() => player.SetSubtitleDelayAsync(before.Scope, 1), PlaybackError.SubtitleUnavailable);
            var after = (await player.ReadTracksAsync())!;
            Check(player.HasMedia && before.Scope == after.Scope && before.AudioId == after.AudioId);
            Check(player.LastErrorCode is null);
        });
        yield return Case("replacement same identity and cross-domain owner both reject stale commands", async () =>
        {
            using var wave = new Wave(); await using var player = New();
            var old = await Start(player, wave);
            await player.OpenAsync("film", "fixture", wave.Uri, "合成音频");
            await Until(async () => (await player.ReadTracksAsync()) is { Audio.Count: > 0 });
            var current = (await player.ReadTracksAsync())!;
            Check(current.Scope.Generation != old.Scope.Generation);
            await Error(() => player.SetRateAsync(old.Scope, 2), PlaybackError.Superseded);
            await Error(() => player.SetRateAsync(current.Scope with { OwnerId = "music" }, 2), PlaybackError.Superseded);
            await Error(() => player.SetRateAsync(current.Scope with { Identity = "other" }, 2), PlaybackError.Superseded);
            Check(player.HasMedia && player.Tracks!.Scope == current.Scope);
        });
        yield return Case("rate is a native requested policy not an effective output assertion", async () =>
        {
            using var wave = new Wave(); await using var player = New();
            var tracks = await Start(player, wave);
            await player.SetRateAsync(tracks.Scope, 1.25);
            var requested = (await player.ReadTracksAsync())!;
            Check(Math.Abs(requested.Rate - 1.25) < 0.00001 && !requested.EffectiveRateVerified);
            await player.SetRateAsync(tracks.Scope, 1);
            Check(player.Tracks!.Rate == 1);
        });
        yield return Case("cancelled commands do not mutate and cross-thread notifications remain dispatcher owned", async () =>
        {
            using var wave = new Wave(); await using var player = New();
            var tracks = await Start(player, wave); int thread = Environment.CurrentManagedThreadId;
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await player.SetRateAsync(tracks.Scope, 2, cancel.Token); throw new InvalidOperationException("Expected cancel"); }
            catch (OperationCanceledException) { }
            bool wrongThread = false; int changes = 0;
            player.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NativePlayback.Tracks))
                { changes++; wrongThread |= Environment.CurrentManagedThreadId != thread; }
            };
            await Task.Run(async () => { await player.ReadTracksAsync(); await player.SetRateAsync(tracks.Scope, 1.5); });
            Check(changes > 0 && !wrongThread && player.Tracks!.Scope == tracks.Scope);
        });
    }
    private static NativePlayback New() => new(audioOutput: NativeAudioOutput.Dummy);
    private static TestCase Case(string name, Func<Task> body) => new("media-tracks: " + name, () => Sta(body));
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Media tracks assertion"); }
    private static async Task Error(Func<Task> operation, string code)
    {
        try { await operation(); throw new InvalidOperationException("Expected " + code); }
        catch (PlaybackException error) { Check(error.Code == code); }
    }
    private static async Task<NativeMediaTracks> Start(NativePlayback player, Wave wave)
    {
        await player.OpenAsync("film", "fixture", wave.Uri, "合成音频");
        await Until(async () => (await player.ReadTracksAsync()) is { Audio.Count: > 0 } && player.IsPlaying);
        return (await player.ReadTracksAsync())!;
    }
    private static async Task Until(Func<Task<bool>> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Native track event deadline");
            await Task.Delay(20);
        }
    }
    private static async Task Sta(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await body(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Check(thread.Join(3000));
    }
    private sealed class Wave : IDisposable
    {
        private readonly string path;
        public Uri Uri => new(path);
        public Wave()
        {
            string root = TestWorkspace.DataPath("meowfilm-media-code-tests");
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "tracks-" + Guid.NewGuid().ToString("N") + ".wav");
            using var output = new BinaryWriter(File.Create(path), Encoding.ASCII);
            const int samples = 48000 * 20, length = samples * 2;
            output.Write("RIFF"u8); output.Write(36 + length); output.Write("WAVEfmt "u8);
            output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(48000); output.Write(96000);
            output.Write((short)2); output.Write((short)16); output.Write("data"u8); output.Write(length);
            for (int i = 0; i < samples; i++) output.Write((short)(Math.Sin(i * Math.PI * 880 / 48000) * 4000));
        }
        public void Dispose()
        {
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length > 44);
            File.Delete(path); // Exact self-created fixture, never a user file or recursive directory.
        }
    }
}
