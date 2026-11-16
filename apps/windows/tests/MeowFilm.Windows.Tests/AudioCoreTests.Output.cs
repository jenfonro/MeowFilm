using MeowFilm.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;

namespace MeowFilm.Windows.Tests;

// Explicitly selected real-output cases. No Dummy sink and no unavailable-device skip.
// The generated samples are silent; rendering is not a physical-speaker listening test.
public static class AudioOutputTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("audio.output.wasapi-start-pause-resume-seek-volume-session-stop", () => Sta(Playback));
        yield return new("audio.output.wasapi-native-clock-and-natural-end-release", () => Sta(NaturalEnd));
        yield return new("audio.output.wasapi-paused-lease-revocation-releases", () => Sta(PausedRevocation));
    }

    private static MMDevice RequireEndpoint()
    {
        Check(OperatingSystem.IsWindows(), "AUDIO_OUTPUT_CONDITION: real Windows WASAPI required");
        using var devices = new MMDeviceEnumerator();
        if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
            throw new InvalidOperationException(
                "AUDIO_OUTPUT_CONDITION: no default WASAPI render endpoint; real output is unverified, not skipped or passed.");
        Console.WriteLine("CONDITION real WASAPI default render endpoint available; generated silent PCM only");
        return device;
    }

    private static async Task Playback()
    {
        using var device = RequireEndpoint();
        using var wave = new Wave(5, silent: true);
        await using var player = new NativeAudioPlayback();
        int started = 0, ended = 0, failed = 0;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await Start(player, () => player.OpenAsync("music", "wasapi-fixture", wave.Uri, "silent WAV"));
        await Until(() => player.OutputDevicePositionSeconds > .1, "actual WASAPI audio clock advances");
        Check(player.HasMedia && player.IsPlaying && started == 1 && player.AudioSessionName == "meowfilm",
            "real output starts with application audio-session identity");
        Near(player.DurationSeconds, 5, .025, "real output decoded duration");
        player.Volume = 31;
        SessionObservation? observed = null;
        await Until(() =>
        {
            observed = Observe(device);
            return observed is { Name: "meowfilm" } && Math.Abs(observed.Volume - .31f) < .02;
        }, "actual Windows process session name and per-session volume");
        Check(observed!.Name == player.AudioSessionName, "native getter agrees with external Windows session enumeration");
        Console.WriteLine($"OBSERVED real WASAPI name={observed.Name}; session-volume={observed.Volume:R}; " +
            $"device-seconds={player.OutputDevicePositionSeconds:R}; media-seconds={player.PositionSeconds:R}");
        await player.PauseAsync();
        Check(player.HasMedia && !player.IsPlaying && player.PauseRequested, "real output paused, input retained");
        await Task.Delay(150); // Let any already-submitted endpoint packet retire before the stable-clock observation.
        double devicePaused = player.OutputDevicePositionSeconds, mediaPaused = player.PositionSeconds;
        await Task.Delay(180);
        Near(player.OutputDevicePositionSeconds, devicePaused, .04, "actual WASAPI clock stable while paused");
        Near(player.PositionSeconds, mediaPaused, .04, "logical position stable while paused");
        await player.SeekAsync(1.25);
        Near(player.PositionSeconds, 1.25, .025, "real output paused seek");
        Check(!player.IsPlaying && player.PauseRequested && player.Volume == 31, "seek does not auto-resume or reset volume");
        await player.ResumeAsync();
        await Until(() => player.IsPlaying && player.OutputDevicePositionSeconds > .1 && player.PositionSeconds > 1.3,
            "real resumed device clock and media clock");
        Check(started == 1 && ended == 0 && failed == 0 && player.AudioSessionName == "meowfilm",
            "seek and resume retain one logical Started and session identity");
        await Until(() => Observe(device) is { Name: "meowfilm", Volume: > .29f and < .33f },
            "actual session volume retained after WASAPI rebuild");
        await player.StopAsync();
        Check(!player.HasMedia && !player.IsPlaying && player.OwnerId is null && player.Identity is null &&
            ended == 0 && failed == 0, "explicit stop is not natural EOF or output failure");
        wave.AssertReleased();
    }

    private static async Task NaturalEnd()
    {
        using var device = RequireEndpoint();
        using var wave = new Wave(1.1, silent: true);
        await using var player = new NativeAudioPlayback();
        int started = 0, ended = 0, failed = 0;
        bool releasedAtEnd = false;
        player.Started += (_, _) => started++;
        player.Failed += (_, _) => failed++;
        player.Ended += (_, _) =>
        {
            ended++;
            releasedAtEnd = !player.HasMedia && wave.IsReleased;
        };
        await Start(player, () => player.OpenAsync("music", "wasapi-eof", wave.Uri, "silent WAV"));
        await Until(() => player.OutputDevicePositionSeconds > .1, "real output clock before EOF");
        double actualClock = player.OutputDevicePositionSeconds;
        Check(Observe(device) is { Name: "meowfilm" }, "an actual active application WASAPI session exists");
        await Until(() => ended == 1, "real PCM drain raises natural end", seconds: 5);
        Check(started == 1 && ended == 1 && failed == 0 && releasedAtEnd && !player.IsPlaying && !player.HasMedia,
            "real WASAPI drains once and releases before notifying the queue");
        wave.AssertReleased();
        Console.WriteLine($"OBSERVED real WASAPI clock-before-eof={actualClock:R}; ended={ended}; failed={failed}");
    }

    private static async Task PausedRevocation()
    {
        using var device = RequireEndpoint();
        var lease = new Lease(WaveBytes(5, silent: true));
        await using var player = new NativeAudioPlayback();
        int ended = 0, failed = 0;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await Start(player, () => player.OpenReadAsync("music", "wasapi-revoked", lease, "silent WAV"));
        await Until(() => player.OutputDevicePositionSeconds > .1, "real render before lease revocation");
        Check(Observe(device) is { Name: "meowfilm" }, "real process audio session before revocation");
        await player.PauseAsync();
        lease.Revoke();
        await Until(() => !player.HasMedia && failed == 1 && lease.Released.Task.IsCompletedSuccessfully,
            "real paused WASAPI lease authorization loss");
        Check(!player.IsPlaying && ended == 0 && player.LastErrorCode == PlaybackError.ReadRevoked,
            "paused output authorization loss is failure, not a natural end");
        lease.AssertReleased();
    }

    private sealed record SessionObservation(string Name, float Volume);

    private static SessionObservation? Observe(MMDevice device)
    {
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (int index = 0; index < sessions.Count; index++)
        {
            using var session = sessions[index];
            if (session.GetProcessID != (uint)Environment.ProcessId ||
                session.State != AudioSessionState.AudioSessionStateActive) continue;
            if (session.DisplayName == "meowfilm")
                return new(session.DisplayName, session.SimpleAudioVolume.Volume);
        }
        return null;
    }
}
