using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Windows.Tests;

public static partial class MusicPlaybackFeedbackTests
{
    private static IEnumerable<TestCase> FactCases()
    {
        yield return Case("one-listen-facts-dispatch-independently-to-platforms", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy", "tx", "kg");
            f.Protocol.FailPlatforms.Add("tx"); f.Play(Merged()); await f.Drain();
            f.Tick(4, 4); f.Controller.Observe("entry-1", 4, 200, true, 2);
            f.Tick(3, 10, rate: 2); f.Tick(2, 10, rate: 2); // Real stall, not two more seconds.
            f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Starts.Count == 3 && f.Protocol.Ends.Count == 2);
            Check(f.Protocol.Ends.Select(e => e.Recording.Platform).ToHashSet().SetEquals(["wy", "kg"]));
            Check(f.Protocol.Starts.Select(s => s.PlaybackId).Distinct().Count() == 1 &&
                f.Protocol.Starts.Select(s => s.Attempt).Distinct().Count() == 3);
            foreach (var end in f.Protocol.Finished)
                Check(end.Listened == 7 && end.MediaListened == 10 &&
                    end.PlaybackListened == 7 && end.PlaybackMediaListened == 10 &&
                    end.EndedAt == f.Clock.GetUtcNow());
        });
        yield return Case("account-admission-is-prospective-but-ack-is-not-a-meter", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            var at = f.Clock.GetUtcNow(); f.Sessions.Arrived = Signal(); f.Sessions.Release = Signal();
            f.Play(); await f.Sessions.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Tick(20, 20); f.Sessions.Release.SetResult(); await f.Drain();
            f.Tick(5, 25); f.Controller.Finish(); await f.Drain();
            var start = f.Protocol.Starts.Single(); var end = f.Protocol.Finished.Single();
            Check(start.StartedAt == at && start.AdmittedAt == at.AddSeconds(20) && start.Position == 20);
            Check(end.Listened == 5 && end.MediaListened == 5 && end.PlaybackListened == 25);
        });
        yield return Case("slow-ack-end-freezes-original-facts-and-times-before-next-entry", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            var at = f.Clock.GetUtcNow(); f.Protocol.Arrived = Signal(); f.Protocol.Release = Signal();
            f.Play(); await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Tick(4, 4); f.Controller.Observe("entry-1", 4, 200, false);
            f.Tick(10, 4, false); f.Controller.Observe("entry-1", 4, 200, true);
            f.Tick(3, 7); f.Controller.Finish("ui");
            var endedAt = f.Clock.GetUtcNow();
            f.Controller.Select(new("entry-2", Song("456")));
            f.Controller.Started("entry-2", 0, 200);
            f.Protocol.Release.SetResult(); await f.Drain();
            var end = f.Protocol.Finished.Single();
            var old = f.Protocol.Starts.Single(s => s.EntryId == "entry-1");
            var next = f.Protocol.Starts.Single(s => s.EntryId == "entry-2");
            Check(old.StartedAt == at && old.AdmittedAt == at && next.StartedAt == endedAt);
            Check(end.Listened == 7 && end.MediaListened == 7 && end.Position == 7 &&
                end.EndedAt == endedAt && end.PlaybackId == old.PlaybackId && old.PlaybackId != next.PlaybackId);
            f.Clock.Advance(2); f.Controller.Observe("entry-2", 2, 200, true);
            f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.Last().Listened == 2);
        });
        yield return Case("unknown-duration-is-a-fact-not-a-common-platform-gate", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Protocol.NeedsDuration = false;
            f.Controller.Select(Selection()); f.Controller.Started("entry-1", 0, 0); await f.Drain();
            Check(f.Protocol.Starts.Single().Duration == 0);
            f.Clock.Advance(5); f.Controller.Observe("entry-1", 5, 0, true);
            f.Clock.Advance(2); f.Controller.Observe("entry-1", 7, 200, true);
            f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.Single().Listened == 7 && f.Protocol.Finished.Single().MediaListened == 7 &&
                f.Protocol.Starts.Count == 1);
        });
        yield return Case("fused-search-origin-survives-mapping-enrichment-and-one-traversal", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy", "tx", "kg");
            f.Controller.Select(Selection(Merged(), new { sourceType = "search", searchKeyword = "真实搜索词" }));
            f.Controller.Started("entry-1", 0, 200); await f.Drain(); f.Tick(5, 5);
            f.Controller.UpdateSelection(Selection(Merged(), new { sourcePlatform = "wy", sourceType = "playlist", sourceId = "500" }));
            f.Controller.Started("entry-1", 5, 200); await f.Drain(); f.Tick(3, 8);
            f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Starts.Count == 3 && f.Protocol.Finished.All(e => e.Listened == 8));
            var wy = f.Protocol.Starts.Single(s => s.Recording.Platform == "wy");
            Check(wy.Source == "search" && wy.SourceId == "真实搜索词");
            Check(f.Protocol.Starts.All(s => s.Context?.GetProperty("searchKeyword").GetString() == "真实搜索词"));
        });
        yield return Case("foreign-collection-remains-origin-without-becoming-target-collection", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy", "tx");
            f.Controller.Select(Selection(Merged(), new { sourcePlatform = "tx", sourceType = "playlist", sourceId = "500" }));
            f.Controller.Started("entry-1", 0, 200); await f.Drain();
            var wy = f.Protocol.Starts.Single(s => s.Recording.Platform == "wy");
            Check(wy.Source == "unknown" && wy.SourceId == "");
            Check(f.Protocol.Starts.All(s => s.Context?.GetProperty("sourcePlatform").GetString() == "tx" &&
                s.Context?.GetProperty("sourceId").GetString() == "500"));
        });
        yield return Case("recommendation-native-id-and-raw-origin-survive-foreign-primary", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy", "tx");
            var song = MusicSongFixture.Online("tx", "mid123", "same", "artist", "", 200);
            song["sources"] = new JsonArray(Raw("wy", "123"), Raw("tx", "mid123"));
            var selection = Recommended("queue", "queue-recommendations", "actual-source") with
                { Song = MusicSongFixture.Element(song) };
            f.Controller.Select(selection); f.Controller.Started("entry-1", 0, 200); await f.Drain();
            var wy = f.Protocol.Starts.Single(s => s.Recording.Platform == "wy");
            Check(wy.Source == "actual-source" && wy.Algorithm == "fixture-alg");
            Check(f.Protocol.Starts.All(s => s.Recommendation is { EntryId: "entry-1", Metadata: { } metadata } &&
                metadata.GetProperty("id").GetString() == "123"));
        });
        yield return Case("missing-upstream-recommendation-source-is-not-invented", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Controller.Select(Recommended("queue", "queue-recommendations", ""));
            f.Controller.Started("entry-1", 0, 200); await f.Drain();
            var start = f.Protocol.Starts.Single();
            Check(start.Recommendation is { Provider: "queue-recommendations", FeedbackSource: "", Algorithm: "fixture-alg" });
            Check(start.Source == "unknown" && !start.IsQueueRecommendation);
        });
        yield return Case("recommendation-origin-cannot-cross-native-recording-ids-on-the-same-platform", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            var selection = Recommended("heartbeat", "heartbeat", "upstream-heartbeat") with
                { Song = Song("456", [Raw("wy", "456"), Raw("wy", "123")]) };
            f.Controller.Select(selection); f.Controller.Started("entry-1", 0, 200); await f.Drain();
            var start = f.Protocol.Starts.Single();
            Check(start.Recording.Id == "456" && start.Recommendation is null &&
                start.Source == "unknown" && start.SourceId == "" && start.Algorithm == "");
        });
        yield return Case("recommendation-algorithm-is-not-truncated-by-common-facts", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            var selection = Recommended("queue", "queue-recommendations", "actual-source");
            var proof = selection.Recommendation!;
            string algorithm = new('a', 3000);
            var origin = proof.Value with { Algorithm = algorithm,
                Metadata = JsonSerializer.SerializeToElement(new { id = "123", source = "queue-recommendations", alg = algorithm }) };
            f.Controller.Select(selection with { Recommendation = new(origin, proof.IsCurrent) });
            f.Controller.Started("entry-1", 0, 200); await f.Drain();
            Check(f.Protocol.Starts.Single().Algorithm == origin.Algorithm &&
                f.Protocol.Starts.Single().Recommendation?.Algorithm == origin.Algorithm);
        });
        yield return Case("wrong-recording-invalidates-pending-ack-without-crediting-replacement", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Protocol.Arrived = Signal(); f.Protocol.Release = Signal();
            f.Play(); await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Tick(20, 20);
            f.Controller.Invalidate();
            Check(f.Protocol.StartTokens.Single().IsCancellationRequested);
            f.Controller.UpdateSelection(Selection(Song("456")));
            f.Protocol.Arrived = Signal(); f.Controller.Started("entry-1", 0, 200);
            await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Tick(3, 3); f.Controller.Finish();
            f.Protocol.Release.SetResult(); await f.Drain();
            Check(f.Protocol.Starts.Count == 2 && f.Protocol.Finished.Count == 1);
            Check(f.Protocol.Starts.Select(s => s.PlaybackId).Distinct().Count() == 2);
            Check(f.Protocol.Ends.Single().Recording.Id == "456" && f.Protocol.Finished.Single().Listened == 3);
            Check(f.Protocol.DisposedSessions == 2);
        });
        yield return Case("actual-replay-of-same-occurrence-creates-a-new-playback", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Play(); await f.Drain(); f.Tick(2, 2); f.Controller.Finish(); await f.Drain();
            f.Play(); await f.Drain(); f.Tick(3, 3); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Starts.Count == 2 && f.Protocol.Starts.Select(s => s.PlaybackId).Distinct().Count() == 2);
            Check(f.Protocol.Finished.Select(e => e.Listened).SequenceEqual([2d, 3d]));
        });
    }
}
