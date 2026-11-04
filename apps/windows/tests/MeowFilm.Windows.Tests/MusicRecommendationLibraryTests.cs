using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.RecommendationTest;

namespace MeowFilm.Windows.Tests;

public static class MusicRecommendationLibraryTests
{
    private const string Prefix = "music.queue.recommendation.";
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Recommendation owner empty checkpoint restored by one CAS", () => Restore(0));
        yield return new("Recommendation owner singleton retains occurrence context and selection", () => Restore(1));
        yield return new("Recommendation owner full paged queue restore preserves all occurrences", () => Restore(120));
        yield return new("Recommendation owner enter preserves current seed and one envelope CAS", Seed);
        yield return new("Recommendation owner nonce receipt replay does not write again", Replay);
        yield return new("Recommendation owner unrelated reads preserve a reconciled receipt", UnacceptedReceipt);
        yield return new("Recommendation owner receipt is replaced by the next capture", ReceiptLifetime);
        yield return new("Recommendation owner optimistic conflict never captures checkpoint", VersionConflict);
        yield return new("Recommendation owner failed before enter explicit reconcile not applied", () => UnknownEnter(false));
        yield return new("Recommendation owner lost enter ACK explicitly restores real runtime", () => UnknownEnter(true));
        yield return new("Recommendation owner failed restore does not consume checkpoint", () => UnknownRestore(false));
        yield return new("Recommendation owner lost restore ACK consumes only after read reconcile", () => UnknownRestore(true));
        yield return new("Recommendation owner atomic append select and lost ACK never duplicates", Append);
        yield return new("Recommendation owner checkpoint private to owner cannot cold revive", OwnerScope);
        yield return new("Recommendation owner cancelled before admission no writes or captures", CancelBefore);
        yield return new("Recommendation owner cancellation after admission awaits physical CAS", CancelAfter);
        yield return new("Recommendation owner pending close blocked and independent checkpoints retained", CloseBudget);
        yield return new("Recommendation owner DPAPI cold queue retained live checkpoint absent", Dpapi);
    }
    private static string Nonce() => Guid.NewGuid().ToString("D");
    private static async Task<JsonElement> Command(MusicLibraryOwner owner, string action, object? p = null) => await Ok(owner, Prefix + action, p);
    private static async Task<JsonElement> Queue(MusicLibraryOwner owner) => await Ok(owner, "music.queue.get");
    private static async Task Error(MusicLibraryOwner owner, string action, object p, string code)
    {
        var result = await owner.ExecuteAsync(Prefix + action, p);
        Check(!result.GetProperty("ok").GetBoolean() && S(result, "code") == code, result.GetRawText());
    }
    private static async Task<JsonElement> Enter(MusicLibraryOwner owner, string? seed = null)
    {
        var q = await Queue(owner);
        if (seed is not null) return await Command(owner, "enter", new { nonce = Nonce(), expectedVersion = N(q, "version"), songs = new[] { Song("900") }, seedEntryId = seed });
        return await Command(owner, "enter", new { nonce = Nonce(), expectedVersion = N(q, "version"), songs = new[] { Song("900") } });
    }
    private static async Task Restore(int count)
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        if (count > 0) await Ok(owner, "music.queue.replace", new { songs = Enumerable.Range(1, count).Select(x => Song(x.ToString())).ToArray(), index = 0,
            context = new { sourceTitle = "Original", sourceType = "search", sourceId = "source", searchKeyword = "q", dailyBranch = "default" } });
        var before = await Full(owner); int writes = store.Writes;
        var entered = await Enter(owner); Check(store.Writes == writes + 1);
        await Command(owner, "restore", new { nonce = Nonce(), expectedVersion = N(await Queue(owner), "version"), checkpoint = S(entered, "checkpoint") });
        Check(store.Writes == writes + 2 && (await Full(owner)) == before);
    }
    private static async Task<string> Full(MusicLibraryOwner owner)
    {
        var result = new List<JsonElement>();
        var first = await Queue(owner); int total = (int)N(first, "total");
        for (int offset = 0; offset < total; offset += 100)
        {
            var page = await Ok(owner, "music.queue.get", new { offset, limit = 100 });
            result.AddRange(page.GetProperty("entries").EnumerateArray().Select(x => x.Clone()));
        }
        return J(new { currentId = Current(first), entries = result, mode = N(first, "mode") }).GetRawText();
    }
    private static async Task Seed()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        await Ok(owner, "music.queue.replace", new { songs = new[] { Song("1"), Song("2") }, index = 1 });
        var before = await Queue(owner); string seed = Current(before); int writes = store.Writes;
        var entered = await Enter(owner, seed);
        var q = await Queue(owner);
        Check(N(q, "total") == 2 && Current(q) == seed && S(q.GetProperty("entries")[0], "id") == seed && store.Writes == writes + 1);
        Check(entered.GetProperty("entryIds").GetArrayLength() == 1);
        await Command(owner, "restore", new { nonce = Nonce(), expectedVersion = N(q, "version"), checkpoint = S(entered, "checkpoint") });
        Check((await Queue(owner)).GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText());
    }
    private static async Task Replay()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        string nonce = Nonce(); var p = new { nonce, expectedVersion = N(await Queue(owner), "version"), songs = new[] { Song("1") } };
        var result = await Command(owner, "enter", p); int writes = store.Writes;
        Check((await Command(owner, "enter", p)).GetRawText() == result.GetRawText() && store.Writes == writes);
        await Error(owner, "enter", new { nonce, expectedVersion = p.expectedVersion, songs = new[] { Song("2") } }, "INVALID_NONCE");
        var discarded = await Command(owner, "discard", new { checkpoint = S(result, "checkpoint") });
        Check(discarded.GetProperty("discarded").GetBoolean());
        Check(!(await Command(owner, "discard", new { checkpoint = S(result, "checkpoint") })).GetProperty("discarded").GetBoolean());
    }
    private static async Task VersionConflict()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        long version = N(await Queue(owner), "version");
        await Ok(owner, "music.queue.replace", new { songs = new[] { Song("1") }, index = 0 });
        int writes = store.Writes;
        await Error(owner, "enter", new { nonce = Nonce(), expectedVersion = version, songs = new[] { Song("2") } }, "REVISION_CONFLICT");
        Check(store.Writes == writes);
        await Error(owner, "capture", new { nonce = "wrong", expectedVersion = version }, "INVALID_NONCE");
    }
    private static async Task UnacceptedReceipt()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        var port = new MusicRecommendationQueuePort(owner);
        string nonce = Nonce(); store.Failure = 2;
        await Error(owner, "enter", new { nonce, expectedVersion = 0, songs = new[] { Song("1") } }, "COMMIT_UNKNOWN");
        JsonElement receipt = default;
        try
        {
            await port.InLaneAsync<bool>(async (library, _) =>
            {
                receipt = await Command(library, "reconcile", new { nonce });
                throw new IOException("Synthetic consumer readback failure");
            }, CancellationToken.None);
            throw new InvalidOperationException("Expected readback failure");
        }
        catch (IOException) { }
        await port.ExecuteAsync("music.queue.get");
        Check((await Command(owner, "reconcile", new { nonce })).GetRawText() == receipt.GetRawText());
        Check(store.Writes == 1);
    }
    private static async Task ReceiptLifetime()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        long version = N(await Queue(owner), "version");
        string first = Nonce();
        var previous = await Command(owner, "capture", new { nonce = first, expectedVersion = version });
        string next = Nonce();
        var latest = await Command(owner, "capture", new { nonce = next, expectedVersion = version });
        Check((await Command(owner, "capture", new { nonce = next, expectedVersion = version })).GetRawText() == latest.GetRawText());
        var replaced = await Command(owner, "capture", new { nonce = first, expectedVersion = version });
        Check(replaced.GetRawText() != previous.GetRawText());
        await Command(owner, "discard", new { checkpoint = S(previous, "checkpoint") });
        await Command(owner, "discard", new { checkpoint = S(latest, "checkpoint") });
        await Command(owner, "discard", new { checkpoint = S(replaced, "checkpoint") });
        Check(store.Writes == 0);
    }
    private static async Task UnknownEnter(bool applied)
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        await Ok(owner, "music.queue.replace", new { songs = new[] { Song("1"), Song("2"), Song("3") }, index = 0 });
        var q = await Queue(owner); string a = Current(q), b = S(q.GetProperty("entries")[1], "id");
        await Ok(owner, "music.queue.mode", new { mode = 2 }); await Ok(owner, "music.queue.select", new { id = b });
        string nonce = Nonce(); var p = new { nonce, expectedVersion = N(await Queue(owner), "version"), songs = new[] { Song("9") } };
        store.Failure = applied ? 2 : 1; await Error(owner, "enter", p, "COMMIT_UNKNOWN");
        int writes = store.Writes;
        var confirmed = await Command(owner, "reconcile", new { nonce }); Check(confirmed.GetProperty("applied").GetBoolean() == applied && store.Writes == writes);
        Check((await Command(owner, "enter", p)).GetProperty("applied").GetBoolean() == applied && store.Writes == writes);
        if (applied) await Command(owner, "restore", new { nonce = Nonce(), expectedVersion = N(await Queue(owner), "version"), checkpoint = S(confirmed, "checkpoint") });
        var previous = await Ok(owner, "music.queue.previous"); Check(S(previous, "targetId") == a, "runtime shuffle history survives reconcile");
    }
    private static async Task UnknownRestore(bool applied)
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        await Ok(owner, "music.queue.replace", new { songs = new[] { Song("1") }, index = 0 });
        var entered = await Enter(owner); string checkpoint = S(entered, "checkpoint"), nonce = Nonce();
        var p = new { nonce, expectedVersion = N(await Queue(owner), "version"), checkpoint };
        store.Failure = applied ? 2 : 1; await Error(owner, "restore", p, "COMMIT_UNKNOWN");
        await Error(owner, "discard", new { checkpoint }, "PENDING_WRITE");
        int writes = store.Writes;
        var confirmed = await Command(owner, "reconcile", new { nonce });
        Check(confirmed.GetProperty("applied").GetBoolean() == applied && store.Writes == writes);
        Check((await Command(owner, "restore", p)).GetProperty("applied").GetBoolean() == applied);
        var again = new { nonce = Nonce(), expectedVersion = N(await Queue(owner), "version"), checkpoint };
        if (applied) await Error(owner, "restore", again, "CHECKPOINT_NOT_FOUND");
        else await Command(owner, "restore", again);
        Check(MusicSongFixture.NativeId((await Queue(owner)).GetProperty("current").GetProperty("song")) == "1");
    }
    private static async Task Append()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        await Ok(owner, "music.queue.replace", new { songs = new[] { Song("1") }, index = 0 });
        string nonce = Nonce(); var p = new { nonce, expectedVersion = N(await Queue(owner), "version"), songs = new[] { Song("2"), Song("3") }, selectFirst = true };
        int writes = store.Writes; store.Failure = 2;
        await Error(owner, "append", p, "COMMIT_UNKNOWN");
        Check(store.Writes == writes + 1);
        await Command(owner, "reconcile", new { nonce }); await Command(owner, "append", p);
        var q = await Queue(owner); Check(store.Writes == writes + 1 && N(q, "total") == 3 &&
            MusicSongFixture.NativeId(q.GetProperty("current").GetProperty("song")) == "2");
    }
    private static async Task OwnerScope()
    {
        var store = new Store(); await using var first = new MusicLibraryOwner(store);
        var result = await Enter(first);
        await using var other = new MusicLibraryOwner(store);
        await Error(other, "restore", new { nonce = Nonce(), expectedVersion = N(await Queue(other), "version"), checkpoint = S(result, "checkpoint") }, "CHECKPOINT_NOT_FOUND");
    }
    private static async Task CancelBefore()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await owner.ExecuteAsync(Prefix + "enter", new { nonce = Nonce(), expectedVersion = 0, songs = new[] { Song("1") } }, cancel.Token);
        Check(S(result, "code") == "CANCELLED" && store.Writes == 0);
    }
    private static async Task CancelAfter()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        long version = N(await Queue(owner), "version");
        store.Entered = NewSignal(); store.Release = NewSignal();
        using var cancel = new CancellationTokenSource();
        var call = owner.ExecuteAsync(Prefix + "enter", new { nonce = Nonce(), expectedVersion = version, songs = new[] { Song("1") } }, cancel.Token);
        await store.Entered.Task; cancel.Cancel(); Check(!call.IsCompleted);
        store.Release.TrySetResult(); var result = await call;
        Check(result.GetProperty("ok").GetBoolean() && store.Writes == 1);
    }
    private static async Task CloseBudget()
    {
        var store = new Store(); await using var owner = new MusicLibraryOwner(store);
        long version = N(await Queue(owner), "version");
        for (int i = 0; i < 8; i++) await Command(owner, "capture", new { nonce = Nonce(), expectedVersion = version });
        await owner.PrepareShutdownAsync(); await owner.CancelShutdownPreparationAsync();
        await Command(owner, "capture", new { nonce = Nonce(), expectedVersion = version });
        store.Failure = 1; string nonce = Nonce();
        await Error(owner, "enter", new { nonce, expectedVersion = version, songs = new[] { Song("1") } }, "COMMIT_UNKNOWN");
        try { await owner.PrepareShutdownAsync(); throw new InvalidOperationException("pending close accepted"); }
        catch (MusicLibraryException e) { Check(e.Code == "COMMIT_UNKNOWN"); }
        await Command(owner, "reconcile", new { nonce });
    }
    private static async Task Dpapi()
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("test root required");
        Check(Path.IsPathFullyQualified(root) && Directory.Exists(root));
        string directory = Path.Combine(root, "recommendation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var store = new LocalDocumentStore(directory, [MusicLibraryOwner.DocumentNamespace]);
        string checkpoint;
        await using (var first = new MusicLibraryOwner(store)) checkpoint = S(await Enter(first), "checkpoint");
        await using var second = new MusicLibraryOwner(store);
        var q = await Queue(second); Check(N(q, "total") == 1);
        await Error(second, "restore", new { nonce = Nonce(), expectedVersion = N(q, "version"), checkpoint }, "CHECKPOINT_NOT_FOUND");
    }
}
