namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    private static IEnumerable<TestCase> FullAppendCases()
    {
        yield return new("Native music full next append preserves order occurrences current mode and one CAS", FullAppend);
        yield return new("Native music full next append stale version and cancellation cannot write", FullAppendRejected);
        yield return new("Native music full next append empty queue selects first and public append contract stays bounded", FullAppendEmpty);
    }

    private static async Task FullAppend()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var before = await Ok(owner, "music.queue.replace", new { songs = new[] { Song("a"), Song("b"), Song("c") }, index = 0 });
        await Ok(owner, "music.queue.mode", new { mode = 2 });
        var prior = await Ok(owner, "music.queue.append", new { songs = new[] { Song("prior") }, next = true });
        string priorId = prior.GetProperty("entryIds")[0].GetString()!;
        long version = prior.GetProperty("version").GetInt64();
        var songs = Enumerable.Range(0, 1005).Select(i => E(Song(i == 501 ? "0" : i.ToString()))).ToArray();
        int writes = f.Store.Writes;
        var result = await owner.AppendQueueAsync(songs, E(new { sourceTitle = "Full" }), version);
        Check(result.GetProperty("ok").GetBoolean(), result.ToString());
        Check(result.GetProperty("currentId").GetString() == before.GetProperty("currentId").GetString() &&
            result.GetProperty("version").GetInt64() == version + 1 && result.GetProperty("mode").GetInt32() == 2);
        Check(!result.TryGetProperty("entryIds", out _) && f.Store.Writes == writes + 1);
        var entries = f.Store.State("queue")["entries"]!.AsArray();
        var expected = new[] { "library:local:a" }.Concat(songs.Select(s => s.GetProperty("songId").GetString()))
            .Concat(new[] { "library:local:prior", "library:local:b", "library:local:c" });
        Check(entries.Select(e => e!["key"]!.GetValue<string>()).SequenceEqual(expected));
        Check(entries.Select(e => e!["id"]!.GetValue<string>()).Distinct().Count() == 1009);
        Check(entries[1]!["context"]!["sourceTitle"]!.GetValue<string>() == "Full");
        var priority = f.Store.State("queue")["priority"]!.AsArray();
        Check(priority.Select(e => e!.GetValue<string>()).SequenceEqual(
            entries.Skip(1).Take(1005).Select(e => e!["id"]!.GetValue<string>()).Append(priorId)));
        var peek = await Ok(owner, "music.queue.peekNext");
        Check(peek.GetProperty("next").GetProperty("id").GetString() == entries[1]!["id"]!.GetValue<string>());
        var next = await Ok(owner, "music.queue.next");
        Check(next.GetProperty("currentId").GetString() == entries[1]!["id"]!.GetValue<string>());
    }

    private static async Task FullAppendRejected()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var initial = await Ok(owner, "music.queue.replace", new { songs = new[] { Song("a") }, index = 0 });
        long stale = initial.GetProperty("version").GetInt64();
        var changed = await Ok(owner, "music.queue.replace", new { songs = new[] { Song("b") }, index = 0 });
        var bytes = f.Store.Bytes!.ToArray(); int writes = f.Store.Writes;
        var conflict = await owner.AppendQueueAsync([E(Song("c"))], null, stale);
        Check(conflict.GetProperty("code").GetString() == "REVISION_CONFLICT");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = await owner.AppendQueueAsync([E(Song("c"))], null,
            changed.GetProperty("version").GetInt64(), cancel.Token);
        Check(cancelled.GetProperty("code").GetString() == "CANCELLED");
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
        var queue = await Ok(owner, "music.queue.get");
        Check(queue.GetProperty("version").GetInt64() == changed.GetProperty("version").GetInt64());
    }

    private static async Task FullAppendEmpty()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var songs = Enumerable.Range(0, 1001).Select(i => E(Song(i.ToString()))).ToArray();
        await Error(owner, "music.queue.append", new { songs, next = true }, "LIMIT");
        await Error(owner, "music.queue.append", new { songs = songs.Take(1).ToArray(), next = true, expectedVersion = 0 }, "INVALID_REQUEST");
        Check(f.Store.Writes == 0);
        var result = await owner.AppendQueueAsync(songs, null, 0);
        Check(result.GetProperty("ok").GetBoolean(), result.ToString());
        var queue = await Ok(owner, "music.queue.get");
        Check(queue.GetProperty("total").GetInt32() == 1001 && queue.GetProperty("mode").GetInt32() == 1);
        Check(queue.GetProperty("currentId").GetString() == queue.GetProperty("entries")[0].GetProperty("id").GetString());
        Check(f.Store.State("queue")["priority"]!.AsArray().Count == 0 && f.Store.Writes == 1);
    }
}
