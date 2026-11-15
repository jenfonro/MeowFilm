using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MeowFilm.Capabilities;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

public static partial class MusicOfficialPlaybackScopeTests
{
    private static IEnumerable<TestCase> NativeNextTrackCases()
    {
        foreach (string mode in new[] { "sequential", "cached", "waiting" })
        {
            bool cached = mode == "cached", waiting = mode == "waiting";
            yield return Case("native next track retires active sequential audio cached=" + cached +
                (waiting ? " while awaiting uncached bytes" : ""), async () =>
            {
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                await using var f = await Scene.Create(files: true, playback: new NativeMusicPlayback(player), bytes: LongWave());
                if (waiting) f.Files.PrefixLength = 512 * 1024;
                var first = MusicSongFixture.Online("wy", "first", "Track first", "Artist", duration: 120);
                var second = MusicSongFixture.Online("wy", "second", "Track second", "Artist", duration: 120);
                if (cached)
                {
                    await f.SeedFile(first, "wy");
                    await f.SeedFile(second, "wy");
                }
                else
                {
                    await f.App.Receipt(first, "wy");
                    await f.App.Receipt(second, "wy");
                }
                await f.Play(first, second);
                await Until(() => player.IsPlaying);
                Check(player.HasMedia && player.Identity == f.Module.QueueSurfaceRows[0].EntryId,
                    "the first actual native input is playing before next");
                var session = typeof(NativePlayback).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                var retired = (MediaPlayer)session.GetType().GetField("Player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                string? previous = player.Identity;
                Task seek = Task.CompletedTask;
                if (waiting)
                {
                    var input = session.GetType().GetField("ReadInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                    var stream = (Stream)input.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!;
                    seek = player.SeekAsync(70);
                    await Until(() => stream.Position > f.Files.PrefixLength);
                    Check(!f.Files.ReleaseBody.Task.IsCompleted, "the old input has sought beyond the held producer prefix");
                }
                Task next = f.Module.StepAsync(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (waiting)
                {
                    await Until(() => retired.NativeReference == IntPtr.Zero);
                    Check(!next.IsCompleted, "native retirement does not require the upstream to deliver its held body");
                    f.Files.ReleaseBody.TrySetResult();
                }
                await next.WaitAsync(TimeSpan.FromSeconds(8));
                await seek;
                await Until(() => player.IsPlaying);
                Check(player.Identity != previous && player.Identity == f.Module.QueueSurfaceRows.Single(row => row.IsCurrent).EntryId &&
                    f.Module.CurrentSongId == second["songId"]!.GetValue<string>() && player.HasMedia && !f.Module.Busy,
                    "next settles at the exact new native queue input");
                Check(retired.NativeReference == IntPtr.Zero, "the active first native player is released, not abandoned");
                Check(f.Module.Status == "" && player.LastErrorCode is null, f.Module.Status);
                await player.StopAsync();
                Check(!player.HasMedia && player.OwnerId is null, "the replacement remains stoppable");
            });
        }
    }

    private static byte[] LongWave()
    {
        byte[] sample = MusicOfficialAudioTests.Wav();
        var bytes = new byte[44 + 16000 * 120];
        sample.AsSpan(0, 44).CopyTo(bytes);
        for (int second = 0; second < 120; second++)
            sample.AsSpan(44).CopyTo(bytes.AsSpan(44 + second * 16000));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        return bytes;
    }
}
