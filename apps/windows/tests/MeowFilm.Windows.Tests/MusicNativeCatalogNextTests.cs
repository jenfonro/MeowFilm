using System.Text.Json;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeCatalogTests
{
    private static IEnumerable<TestCase> NextCases()
    {
        yield return new("Native catalog next commits over 1000 private members with duplicate occurrences in order", CompleteNext);
        yield return new("Native catalog next accepts only token action version and rejects empty stage", NextFields);
        yield return new("Native catalog next stale queue consumes token without any partial append", NextConflict);
        yield return new("Native catalog next empty queue selects first and consumes stage once", NextEmpty);
        yield return new("Native catalog next queued cancellation releases token without queue writes", NextCancellation);
        yield return new("Native catalog next admitted CAS cancellation reports durable success", NextAdmittedCancellation);
        yield return new("Native catalog next tail failure and late page cancellation leave queue unchanged", NextPrepareFailure);
        foreach (bool after in new[] { false, true })
            yield return new("Native catalog next unknown CAS requires reconciliation " + (after ? "after" : "before"), () => NextUnknown(after));
    }

    private static async Task CompleteNext()
    {
        await using var f = new Fixture();
        var before = Ok(await f.Library.ReplaceQueueAsync([Song("wy", 9000), Song("wy", 9001), Song("wy", 9002)], 0, null, 0));
        Ok(await f.Library.ExecuteAsync("music.queue.mode", new { mode = 2 }));
        f.Platform.Pages = p => Page(p, 1005, i => Song("kg", i == 501 ? 0 : i, (1000 + i).ToString()));
        var locator = O(E(Locator("kg"))); locator["total"] = 1005;
        var stage = await f.Prepare("kg", "0:42", locator); int writes = f.Store.Writes;
        Check(N(stage, "count") == 1005 && f.Platform.Calls.Length == 68);
        var result = Ok(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "next", expectedVersion = N(before, "version") }));
        Check(S(result, "currentId") == S(before, "currentId") && N(result, "mode") == 2 &&
            N(result, "version") == N(before, "version") + 1 && f.Store.Writes == writes + 1);
        Check(result.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "currentId", "mode", "ok", "version" }));
        var rows = new List<JsonElement>();
        for (int offset = 0; offset < 1008; offset += 100)
        {
            var page = Ok(await f.Library.ExecuteAsync("music.queue.get", new { offset, limit = 100 }));
            Check(N(page, "total") == 1008);
            rows.AddRange(page.GetProperty("entries").EnumerateArray().Select(e => e.Clone()));
        }
        Check(rows.Select(e => S(e, "id")).Distinct().Count() == 1008);
        Check(rows.Skip(1).Take(1005).Select(e => S(MusicSongFixture.Info(e.GetProperty("song")), "fileid"))
            .SequenceEqual(Enumerable.Range(1000, 1005).Select(i => i.ToString())));
        Check(S(rows[1].GetProperty("song"), "songId") == S(rows[502].GetProperty("song"), "songId"));
        Check(S(rows[0].GetProperty("song"), "songId") == S(Song("wy", 9000), "songId") &&
            S(rows[^2].GetProperty("song"), "songId") == S(Song("wy", 9001), "songId") &&
            S(rows[^1].GetProperty("song"), "songId") == S(Song("wy", 9002), "songId"));
        Check(S(rows[1].GetProperty("context"), "sourceId") == "0:42");
        var peek = Ok(await f.Library.ExecuteAsync("music.queue.peekNext"));
        Check(S(peek.GetProperty("next"), "id") == S(rows[1], "id"));
        Check(!Ok(await f.Run("playlist.discard", new { token = S(stage, "token") })).GetProperty("discarded").GetBoolean());
    }

    private static async Task NextFields()
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        string token = S(await f.Prepare(), "token");
        foreach (string field in new[] { "songs", "context", "index", "memberId", "expectedSongId", "name" })
        {
            var input = O(E(new { token, action = "next", expectedVersion = 0 })); input[field] = null;
            Error(await f.Run("playlist.commit", input), "INVALID_REQUEST");
        }
        Error(await f.Run("playlist.commit", new { token, action = "next" }), "INVALID_REQUEST");
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = -1 }), "INVALID_REQUEST");
        Check(f.Store.Writes == 0);
        Ok(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }));
        f.Platform.Total = 0; token = S(await f.Prepare(), "token");
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 1 }), "INVALID_REQUEST");
        Check(Ok(await f.Run("playlist.discard", new { token })).GetProperty("discarded").GetBoolean());
        Check(f.Store.Writes == 1);
    }

    private static async Task NextConflict()
    {
        await using var f = new Fixture(); f.Platform.Total = 1005;
        string token = S(await f.Prepare(), "token");
        Ok(await f.Library.ReplaceQueueAsync([Song("wy", 9999)], 0, null, 0));
        var bytes = f.Store.Bytes!.ToArray();
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }), "REVISION_CONFLICT");
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 1 }), "CATALOG_TOKEN_EXPIRED");
        Check(!Ok(await f.Run("playlist.discard", new { token })).GetProperty("discarded").GetBoolean());
        Check(f.Store.Writes == 1 && bytes.SequenceEqual(f.Store.Bytes!) && N(await f.Queue(), "total") == 1);
    }

    private static async Task NextEmpty()
    {
        await using var f = new Fixture(); f.Platform.Total = 1005;
        string token = S(await f.Prepare(), "token");
        var result = Ok(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }));
        var queue = await f.Queue();
        Check(N(queue, "total") == 1005 && N(result, "mode") == 1 && N(result, "version") == 1);
        Check(S(result, "currentId") == S(queue.GetProperty("entries")[0], "id"));
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 1 }), "CATALOG_TOKEN_EXPIRED");
        Check(f.Store.Writes == 1);
    }

    private static async Task NextCancellation()
    {
        await using var f = new Fixture(); f.Platform.Total = 2;
        string token = S(await f.Prepare(), "token");
        f.Store.Block();
        var other = f.Library.CreatePlaylistAsync("Unrelated", [Song("wy", 999)]);
        await f.Store.Entered!.Task;
        using var cancel = new CancellationTokenSource();
        var queued = f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }, cancel.Token);
        cancel.Cancel(); Error(await queued, "CANCELLED");
        Check(!Ok(await f.Run("playlist.discard", new { token })).GetProperty("discarded").GetBoolean());
        f.Store.Release!.TrySetResult(); Ok(await other);
        Check(f.Store.Writes == 1 && N(await f.Queue(), "total") == 0);
        token = S(await f.Prepare(), "token");
        Ok(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }));
        Check(f.Store.Writes == 2 && N(await f.Queue(), "total") == 2);
    }

    private static async Task NextAdmittedCancellation()
    {
        await using var f = new Fixture(); f.Platform.Total = 2;
        string token = S(await f.Prepare(), "token"); f.Store.Block();
        using var cancel = new CancellationTokenSource();
        var writing = f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }, cancel.Token);
        await f.Store.Entered!.Task; cancel.Cancel(); f.Store.Release!.TrySetResult();
        Ok(await writing);
        Check(f.Store.WriteWasUncancellable && f.Store.Writes == 1 && N(await f.Queue(), "total") == 2);
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 1 }), "CATALOG_TOKEN_EXPIRED");
    }

    private static async Task NextPrepareFailure()
    {
        await using var f = new Fixture();
        Ok(await f.Library.ReplaceQueueAsync([Song("wy", 9999)], 0, null, 0));
        var bytes = f.Store.Bytes!.ToArray();
        f.Platform.Pages = p => p.Page == 11 ? throw new InvalidOperationException("tail") : Page(p, 1005);
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }), "INVALID_RESPONSE");
        using var cancel = new CancellationTokenSource(); var entered = Signal(); var release = f.Hold();
        f.Platform.Handler = async c =>
        {
            var p = (MeowFilm.Capabilities.Platforms.MusicPlaylistRequest)c.Request!;
            if (p.Page == 11) { entered.TrySetResult(); await release.Task; }
            return Page(p, 1005);
        };
        var preparing = f.Run("playlist.prepare", new { platform = "wy", id = "42" }, cancel.Token);
        await entered.Task; cancel.Cancel(); release.TrySetResult(); Error(await preparing, "CANCELLED");
        Check(f.Store.Writes == 1 && bytes.SequenceEqual(f.Store.Bytes!) && N(await f.Queue(), "total") == 1);
        f.Platform.Handler = null; f.Platform.Pages = p => Page(p, 1);
        string token = S(await f.Prepare(), "token");
        Ok(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 1 }));
        Check(f.Store.Writes == 2 && N(await f.Queue(), "total") == 2);
    }

    private static async Task NextUnknown(bool after)
    {
        await using var f = new Fixture(); f.Platform.Total = 2;
        string token = S(await f.Prepare(), "token"); f.Store.Failure = after ? 2 : 1;
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }), "COMMIT_UNKNOWN");
        Error(await f.Run("playlist.commit", new { token, action = "next", expectedVersion = 0 }), "CATALOG_TOKEN_EXPIRED");
        await f.Library.ReconcileAsync();
        Check(N(await f.Queue(), "total") == (after ? 2 : 0) && f.Store.Writes == 1);
    }
}
