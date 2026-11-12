namespace MeowFilm.Windows.Tests;

public static partial class PlaybackRecoveryTests
{
    private static IEnumerable<TestCase> FeedbackLifecycleCases()
    {
        foreach (bool failed in new[] { false, true })
            yield return Test("feedback observes accepted audio throughout " + (failed ? "failed" : "successful") + " handoff preparation", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(); await f.Play();
                await Until(() => f.App.FeedbackProbe.Starts == 1 && f.App.HistoryWrites == 1, "accepted listen");
                var choice = await TxChoice(f.Module);
                var pending = f.Output.Plan(hold: true, failure: failed ? MeowFilm.Media.PlaybackError.HttpUnauthorized : null);
                var handoff = f.Module.SelectSourceChoiceAsync(choice);
                try
                {
                    await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    f.Output.SetPosition(8);
                    Check(f.App.FeedbackProbe.Observations.Any(s => s.Position == 8 && s.Playing) &&
                        f.App.FeedbackProbe.Starts == 1 && f.App.FeedbackProbe.Finishes.Count == 0,
                        "preparation neither suppresses the old real audio nor starts another listen");
                    pending.Release.TrySetResult(); await handoff;
                    Check(f.App.FeedbackProbe.Starts == 1 && f.App.FeedbackProbe.Finishes.Count == 0 &&
                        f.App.HistoryWrites == 1 && f.Output.IsPlaying && f.Output.PositionSeconds == 8,
                        "one occurrence survives the prepared result");
                    Check(f.Module.CurrentSource == (failed ? "网易云音乐" : "QQ 音乐"),
                        "only a successful prepared input changes delivery");
                }
                finally { pending.Release.TrySetResult(); await handoff; }
            });
        foreach (bool paused in new[] { false, true })
            yield return Test("feedback wrong-source rejection invalidates before async replacement paused " + paused, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(); await f.Play();
                await Until(() => f.App.FeedbackProbe.Starts == 1 && f.App.HistoryWrites == 1, "accepted listen");
                f.Output.SetPosition(20);
                if (paused) await f.Module.TogglePlaybackAsync();
                var pending = f.Output.Plan(hold: true);
                var replace = f.Module.ReportWrongSourceAsync();
                try
                {
                    Check(!f.Output.HasMedia && !f.Output.IsPlaying && f.App.FeedbackProbe.Invalidations == 1 &&
                        f.App.FeedbackProbe.Finishes.Count == 0, "wrong audio stops and cannot produce an accepted end");
                    await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    f.Output.SetPosition(22);
                    Check(f.App.FeedbackProbe.Starts == 1, "the rejected donor cannot restart while searching");
                    pending.Release.TrySetResult(); await replace;
                    Check(f.App.FeedbackProbe.Starts == (paused ? 1 : 2) && f.App.FeedbackProbe.Finishes.Count == 0 &&
                        f.App.HistoryWrites == 1, "replacement does not inherit rejected facts or fabricate paused listening");
                    if (paused)
                    {
                        await f.Module.TogglePlaybackAsync();
                        Check(f.Output.IsPlaying && f.App.FeedbackProbe.Starts == 2,
                            "real resume of the committed replacement starts its own facts");
                    }
                }
                finally { pending.Release.TrySetResult(); await replace; }
            });
        yield return Test("feedback duration mismatch stops the wrong audio before pending recovery", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(); await f.Play();
            await Until(() => f.App.FeedbackProbe.Starts == 1 && f.App.HistoryWrites == 1, "accepted listen");
            var pending = f.Output.Plan(hold: true);
            f.Output.SetDuration(180);
            try
            {
                Check(!f.Output.IsPlaying && !f.Output.HasMedia && f.App.FeedbackProbe.Invalidations == 1,
                    "the duration event invalidates and stops the contradicted recording immediately");
                await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.App.FeedbackProbe.Starts == 1 && f.App.FeedbackProbe.Finishes.Count == 0,
                    "pending recovery is not accepted listening");
                f.Output.SetDuration(120); pending.Release.TrySetResult();
                await Until(() => f.Module.CurrentSource == "QQ 音乐" && f.Output.HasMedia,
                    "trusted replacement prepared");
                Check(f.App.FeedbackProbe.Starts == 1, "prepared replacement has not started listening");
                f.Output.Start();
                await Until(() => f.App.FeedbackProbe.Starts == 2 && f.Module.CurrentSource == "QQ 音乐",
                    "trusted replacement committed");
                Check(f.App.FeedbackProbe.Invalidations == 1 && f.App.FeedbackProbe.Finishes.Count == 0 &&
                    f.App.HistoryWrites == 1, "the old wrong duration cannot become replacement listening");
            }
            finally { pending.Release.TrySetResult(); }
        });
        yield return Test("feedback small native seek stays suspended until its real post-seek sample", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(); await f.Play();
            await Until(() => f.App.FeedbackProbe.Starts == 1, "accepted listen");
            f.Output.SetPosition(10); int before = f.App.FeedbackProbe.Observations.Count;
            await f.Module.SeekAsync(11);
            var samples = f.App.FeedbackProbe.Observations.Skip(before).ToArray();
            Check(samples.Any(s => s.Position == 10 && !s.Playing) &&
                samples.Any(s => s.Position == 11 && !s.Playing) && samples.Last() is { Position: 11, Playing: true },
                "even an in-range one-second jump has an explicit non-listening boundary");
            Check(f.App.FeedbackProbe.Starts == 1 && f.App.FeedbackProbe.Finishes.Count == 0, "seek is not a replay");
        });
    }
}
