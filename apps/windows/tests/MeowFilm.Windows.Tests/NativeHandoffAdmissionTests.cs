using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AdaptiveMediaTests;

namespace MeowFilm.Windows.Tests;

public static class NativeHandoffAdmissionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("rejected output stops before replacement resolution and retains occurrence controls", async () =>
        {
            await using var native = Player();
            var old = new WaveLease(); var next = new WaveLease();
            await native.OpenReadAsync("music", "same-entry", old, "fixture");
            await Decoded(native);
            await native.SetRateAsync((await native.ReadTracksAsync())!.Scope, 1.6);
            native.Volume = 37;
            await native.SeekAsync(18);
            await native.StopOutputForHandoffAsync();
            Check(!native.IsPlaying && !native.HasMedia && native.Identity == "same-entry" &&
                native.OwnerId == "music" && old.Disposals == 1 && !native.PauseRequested,
                "output is retired but the logical occurrence and play intent survive");
            await native.PrepareReadHandoffAsync(next, default);
            Check(!native.IsPlaying && next.Disposals == 0, "preparation after exclusion is silent");
            await native.PauseAsync();
            int commits = 0;
            await native.CommitHandoffAsync(() => commits++, default);
            Check(commits == 1 && native.PauseRequested && !native.IsPlaying && native.Volume == 37 &&
                Math.Abs(native.PositionSeconds - 18) <= .25 &&
                Math.Abs((await native.ReadTracksAsync())!.Rate - 1.6) < .01,
                "prepared replacement inherits position, latest pause, rate and volume");
            await native.StopAsync();
            Check(next.Disposals == 1, "replacement lease released");
        });
        yield return Test("revoked prepared input cannot replace healthy media after readiness", async () =>
        {
            await using var native = Player();
            var old = new WaveLease();
            await native.OpenReadAsync("music", "same-entry", old, "fixture");
            await Decoded(native);
            var accepted = Session(native);
            var next = new WaveLease();
            await native.PrepareReadHandoffAsync(next, default);
            next.Revoke();
            bool committed = false;
            try
            {
                await native.CommitHandoffAsync(() => committed = true, default);
                throw new InvalidOperationException("Revoked replacement was committed");
            }
            catch (PlaybackException error)
            {
                Check(error.Code == PlaybackError.ReadRevoked, "the real input revocation is preserved");
            }
            await native.DiscardHandoffAsync();
            Check(!committed && ReferenceEquals(accepted, Session(native)) && native.IsPlaying &&
                old.Disposals == 0 && next.Disposals == 1, "only the rejected prepared lease retires");
        });
        yield return Test("prepared replacement survives donor content failure without reopening", async () =>
        {
            await using var native = Player();
            var old = new WaveLease(); var next = new WaveLease();
            await native.OpenReadAsync("music", "same-entry", old, "fixture");
            await Decoded(native);
            await native.SeekAsync(22);
            await native.PrepareReadHandoffAsync(next, default);
            var input = Field(Session(native)!, "ReadInput")!;
            // Inject the input's real terminal callback after native readiness.
            input.GetType().GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(input, [PlaybackError.ReadFailed]);
            await Until(() => native.LastErrorCode == PlaybackError.ReadFailed);
            Check(!native.HasMedia && old.Disposals == 1 && next.Disposals == 0,
                "failed donor retires without discarding the prepared replacement");
            await native.PauseAsync();
            int commits = 0;
            await native.CommitHandoffAsync(() => commits++, default);
            Check(commits == 1 && native.HasMedia && !native.IsPlaying && native.PauseRequested &&
                native.PositionSeconds >= 21.75 && next.Disposals == 0,
                "the existing preparation takes over with retained position and latest pause");
        });
        yield return Test("late donor seek is aligned before replacement publication", async () =>
        {
            await using var native = Player();
            var old = new WaveLease(); var next = new WaveLease();
            await native.OpenReadAsync("music", "same-entry", old, "fixture");
            await Decoded(native);
            await native.PrepareReadHandoffAsync(next, default);
            await native.PauseAsync();
            await native.SeekAsync(37);
            int commits = 0;
            await native.CommitHandoffAsync(() => commits++, default);
            Check(commits == 1 && native.PauseRequested && !native.IsPlaying &&
                Math.Abs(native.PositionSeconds - 37) <= .25 && old.Disposals == 1 && next.Disposals == 0,
                "commit keeps the latest accepted seek and pause, not the preparation position");
            await native.StopAsync();
            Check(next.Disposals == 1, "accepted replacement releases exactly once");
        });
        yield return Test("preparation input failure leaves the current occurrence playing", async () =>
        {
            await using var native = Player();
            var old = new WaveLease();
            await native.OpenReadAsync("music", "same-entry", old, "fixture");
            await Decoded(native);
            var accepted = Session(native);
            var rejected = new WaveLease();
            rejected.Revoke();
            try
            {
                await native.PrepareReadHandoffAsync(rejected, default);
                throw new InvalidOperationException("Revoked input was prepared");
            }
            catch (PlaybackException error)
            {
                Check(error.Code == PlaybackError.ReadRevoked, "preparation reports revocation");
            }
            Check(ReferenceEquals(accepted, Session(native)) && native.IsPlaying &&
                native.Identity == "same-entry" && old.Disposals == 0,
                "failed preparation never replaces or stops the accepted media");
            await Until(() => rejected.Disposals == 1);
        });
    }

    private static TestCase Test(string name, Func<Task> run) =>
        new("native-handoff-admission: " + name, () => Sta(run));
    private static NativePlayback Player() => new(Dispatcher.CurrentDispatcher, null, NativeAudioOutput.Dummy);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Lease release");
            await Task.Delay(15);
        }
    }
    private sealed class WaveLease : INativeMediaReadLease
    {
        private readonly CancellationTokenSource revocation = new();
        public Stream Content { get; } = new MemoryStream(Wave(), writable: false);
        public long Length => Content.Length;
        public CancellationToken Revoked => revocation.Token;
        internal int Disposals;
        internal void Revoke() => revocation.Cancel();
        public ValueTask DisposeAsync()
        {
            Disposals++;
            Content.Dispose(); revocation.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private static byte[] Wave()
    {
        const int length = 8000 * 2 * 120;
        var bytes = new byte[length + 44];
        using var memory = new MemoryStream(bytes); using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length);
        return bytes;
    }
}
