using System.IO;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.AdaptiveMediaTests;

namespace MeowFilm.Windows.Tests;

public static partial class PlaybackRecoveryTests
{
    private static IEnumerable<TestCase> CatalogNextRecoveryCases()
    {
        yield return Test("playlist next recovers authoritative playback gates after lost durable receipt", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, scripts: [],
                catalogHttp: new PlaylistNextHttp());
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(section => section.Items)
                .Single(item => item.Kind == "playlist");
            await f.PlayPair();
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
            await f.Module.ChangeModeAsync(2);
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1, "lost receipt current media");
            Check(f.Runtime.Calls == 0 && f.Module.CanPlay && f.Module.CanTogglePlayback && f.Module.CanSeek,
                "current official media and all controls are active without Node");
            var queueBefore = await f.App.ExecuteAsync("music.queue.get", new { limit = 100 });
            Check(queueBefore.GetProperty("ok").GetBoolean(), "native queue baseline");
            var before = (f.Output.Identity, f.Output.NetworkOpens, f.Output.ReadOpens, f.Output.Reopens,
                f.Output.Commits, f.Output.CandidateStarts, f.App.HistoryWrites, f.App.ConfirmWrites);
            int resolutions = f.App.Resolutions.Count;
            var probe = f.App.PlaylistNext = new PlaylistNextProbe(holdPeek: true) { LoseCommitReceipt = true };
            f.Output.SetPosition(105);
            await probe.PeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var attempt = Field(f.Module, "musicPreload") ?? throw new InvalidOperationException("Missing old preload");
            var oldToken = ((CancellationTokenSource)Field(attempt, "Cancellation")!).Token;
            Check(!(bool)Field(attempt, "QueueObserved")! && !oldToken.IsCancellationRequested &&
                !probe.PeekToken.IsCancellationRequested && f.Prepared is null && !f.ValidationEntered.Task.IsCompleted,
                "old peek is held before queue observation or physical preparation");
            var action = f.Module.QueueCatalogCardAsync(card);
            try
            {
                await probe.QueueReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(probe.CommitReceipt is { } receipt && receipt.GetProperty("ok").GetBoolean() &&
                    probe.CatalogMethods.SequenceEqual(new[] { "music.catalog.playlist.prepare", "music.catalog.playlist.commit" }),
                    "real native commit succeeded before its receipt was lost");
                Check(!f.Module.CanPlay && !f.Module.CanTogglePlayback && !f.Module.CanSeek &&
                    !probe.QueueReadToken.IsCancellationRequested,
                    "lost receipt closes controls until lifetime-owned authority read settles");
                Check(oldToken.IsCancellationRequested && probe.PeekToken.IsCancellationRequested,
                    "recovery cancels the unobserved old preload before reconciliation");
                probe.ReleasePeek.TrySetResult();
                await Until(() => Field(f.Module, "musicPreload") is null, "lost receipt old peek drained");
                await using var absent = await f.Cache!.OpenAsync("fixture", f.NextSongId!, new string('a', 64), default);
                Check(absent is null && f.Prepared is null && !f.ValidationEntered.Task.IsCompleted &&
                    !Directory.EnumerateFiles(f.CacheRoot!, "*.audio").Any() && f.App.Resolutions.Count == resolutions &&
                    f.Output.HasMedia && f.Output.IsPlaying &&
                    before == (f.Output.Identity, f.Output.NetworkOpens, f.Output.ReadOpens, f.Output.Reopens,
                        f.Output.Commits, f.Output.CandidateStarts, f.App.HistoryWrites, f.App.ConfirmWrites),
                    "late old peek cannot prepare bytes resolve again or restart current media");
            }
            finally
            {
                probe.ReleasePeek.TrySetResult(); probe.ReleaseQueueRead.TrySetResult();
                await action.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Check(probe.CatalogMethods.SequenceEqual(new[] { "music.catalog.playlist.prepare", "music.catalog.playlist.commit",
                "music.catalog.playlist.discard" }), "uncertain commit is never replayed and stage is released once");
            var after = await f.App.ExecuteAsync("music.queue.get", new { limit = 100 });
            Check(after.GetProperty("ok").GetBoolean() &&
                after.GetProperty("currentId").GetString() == queueBefore.GetProperty("currentId").GetString() &&
                after.GetProperty("currentId").GetString() == f.Output.Identity &&
                after.GetProperty("mode").GetInt32() == queueBefore.GetProperty("mode").GetInt32() &&
                after.GetProperty("version").GetInt64() == queueBefore.GetProperty("version").GetInt64() + 1 &&
                after.GetProperty("entries").EnumerateArray().Select(entry => MusicSongFixture.NativeId(entry.GetProperty("song")))
                    .SequenceEqual(new[] { "First", "8001", "8002", "Next" }),
                "authoritative queue retains mode current and the whole inserted segment in order");
            var oldEntries = queueBefore.GetProperty("entries").EnumerateArray().ToArray();
            var entries = after.GetProperty("entries").EnumerateArray().ToArray();
            Check(entries.Length == oldEntries.Length + 2 &&
                entries[0].GetProperty("id").GetString() == oldEntries[0].GetProperty("id").GetString() &&
                entries[3].GetProperty("id").GetString() == oldEntries[1].GetProperty("id").GetString(),
                "old queue occurrences are not replaced by recovery");
            Check(f.Runtime.Calls == 0 && f.Module.CanPlay && f.Module.CanTogglePlayback && f.Module.CanSeek &&
                f.Output.HasMedia && f.Output.IsPlaying &&
                before == (f.Output.Identity, f.Output.NetworkOpens, f.Output.ReadOpens, f.Output.Reopens,
                    f.Output.Commits, f.Output.CandidateStarts, f.App.HistoryWrites, f.App.ConfirmWrites),
                "saved authority restores controls without Node or reopening the existing media");
        });
    }
}
