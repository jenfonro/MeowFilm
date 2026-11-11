using System.Windows;
using MeowFilm.AppHost;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> FeedbackBoundaryCases()
    {
        yield return Case("feedback actual rate consumer keeps progress suspended through a slow command and closes once", async () =>
        {
            var output = new RateProbe(); var feedback = new FeedbackBoundaryProbe();
            await using var f = await Fixture.Create(output: output, feedback: feedback);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); output.Start();
            int before = feedback.Samples.Count;
            output.HoldWrite = true;
            var change = f.Module.ChangePlaybackRateAsync(2);
            try
            {
                await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); output.Tick(1);
                var samples = feedback.Samples.Skip(before).ToArray();
                True(samples.Length >= 2 && samples.All(s => !s.Playing), "in-flight native rate has no credited transition");
                output.Release.TrySetResult(); await change;
                True(feedback.Samples.Last() is { Playing: true, Rate: 2 }, "post-command sample uses exact input readback");
                Equal(1, feedback.Starts);
                await f.Module.PrepareShutdownAsync();
                True(feedback.Ends.SequenceEqual(["interrupt"]), "actual close consumer freezes one end");
                int count = feedback.Samples.Count; output.Tick(2);
                Equal(count, feedback.Samples.Count);
            }
            finally { output.Release.TrySetResult(); await change; }
        });
        yield return Case("feedback mismatching rate readback remains fact without claiming requested intent", async () =>
        {
            var output = new RateProbe(); var feedback = new FeedbackBoundaryProbe();
            await using var f = await Fixture.Create(output: output, feedback: feedback);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); output.Start();
            await f.Module.ChangePlaybackRateAsync(2); output.WrongRate = true;
            await f.Module.ChangePlaybackRateAsync(0.5);
            Equal(2d, f.Module.PlaybackRate);
            True(f.Module.Status.Length > 0 && feedback.Samples.Last() is { Playing: true, Rate: 1 },
                "failed requested intent cannot overwrite the current input's readback facts");
        });
        yield return Case("feedback real shell tray hide keeps listening and exit freezes one terminal before application prepare", async () =>
        {
            var feedback = new FeedbackBoundaryProbe();
            await using var f = await Fixture.Create(feedback: feedback);
            await using var preferences = new AppClosePreferences(f.Store);
            await preferences.LoadAsync();
            True(await preferences.SaveAsync(WindowCloseBehavior.MinimizeToTray), "fixture tray preference");
            var lifecycle = new AppShutdown([f.Module], [], sharedParticipants: [preferences]);
            var tray = new FeedbackTrayProbe();
            var shell = new ShellWindow([f.Module], [], lifecycle.CloseAsync, lifecycle.ForceReleaseAsync,
                lifecycle.CancelShutdownPreparationAsync, TimeProvider.System, closePreferences: preferences,
                trayIconFactory: (_, show, exit, unavailable) =>
                {
                    tray.Exit = exit;
                    return tray;
                })
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed += (_, _) => closed.TrySetResult();
            HeldReply? prepare = null;
            try
            {
                shell.Show(); await Drain();
                await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start();
                await Until(() => f.Calls.Count("music.history.recordPlayed") == 1);
                Equal(1, feedback.Starts);
                shell.Close();
                await Until(() => !shell.IsVisible);
                int before = feedback.Samples.Count;
                f.Playback.Tick(5);
                True(feedback.Ends.Count == 0 && feedback.Samples.Count > before &&
                    feedback.Samples.Last().Playing && f.Calls.Count("application.prepare") == 0 &&
                    !shell.ShutdownCompletion.IsCompleted && !lifecycle.IsReleased && tray.Disposals == 0,
                    "tray hiding does not admit shutdown or stop real playback observation");
                prepare = f.Calls.HoldNext("application.prepare");
                tray.Exit();
                await prepare.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(shell.IsVisible && feedback.Ends.SequenceEqual(["interrupt"]) && !lifecycle.IsReleased,
                    "real module terminal is frozen before the application can prepare its feedback owner");
                shell.Close(); await Drain();
                Equal(1, f.Calls.Count("application.prepare"));
                True(feedback.Ends.SequenceEqual(["interrupt"]), "duplicate close does not repeat the terminal");
                prepare.Release();
                await Until(() => closed.Task.IsCompleted);
                True(shell.ShutdownSucceeded && lifecycle.IsReleased && tray.Disposals == 1 &&
                    feedback.Ends.SequenceEqual(["interrupt"]), "successful host teardown keeps exactly one terminal");
                before = feedback.Samples.Count; f.Playback.Tick(6);
                Equal(before, feedback.Samples.Count);
            }
            finally
            {
                prepare?.Release();
                if (!closed.Task.IsCompleted) await shell.AbortAsync();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private sealed class FeedbackTrayProbe : IDesktopTrayIcon
    {
        internal Action Exit = null!;
        internal int Disposals;
        public bool TryShow() => true;
        public void Dispose() => Disposals++;
    }

    private sealed class FeedbackBoundaryProbe : IMusicPlaybackFeedback
    {
        internal int Starts;
        internal readonly List<(bool Playing, double Rate)> Samples = [];
        internal readonly List<string> Ends = [];
        private bool active;
        public string? LastError => null;
        public Task<MusicFeedbackSettings> ReadSettingsAsync(bool refresh = false, CancellationToken token = default) =>
            Task.FromResult(new MusicFeedbackSettings(0, []));
        public Task<MusicFeedbackSettings> SaveSettingsAsync(long revision, IReadOnlyList<string> enabled, CancellationToken token = default) =>
            Task.FromResult(new MusicFeedbackSettings(revision + 1, enabled));
        public void Select(MusicFeedbackSelection value) => active = false;
        public void UpdateSelection(MusicFeedbackSelection value) { }
        public void Started(string entryId, double position, double duration, double rate = 1, int mode = 1)
        { active = true; Starts++; }
        public void Observe(string entryId, double position, double duration, bool isPlaying, double rate = 1, int mode = 1) =>
            Samples.Add((isPlaying, rate));
        public void Finish(string reason = "ui")
        { if (active) { Ends.Add(reason); active = false; } }
        public void Invalidate() => active = false;
    }
}
