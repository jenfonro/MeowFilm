using System.Reflection;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static partial class MusicAggregateSearchTests
{
    private static IEnumerable<TestCase> RecordingCases()
    {
        yield return new("Search rounds preserve canonical sources primary identity and displayed positions", CanonicalRounds);
        yield return new("Search later rounds enforce all recording pairs and feed real song consumers", PairwiseRounds);
        yield return new("Search same-round conflicting subgroup accepts compatible platform consensus", SameRoundSubgroups);
        yield return new("Search later rounds reuse conflicting subgroups even after a platform ends", LaterRoundSubgroups);
        foreach (string kind in new[] { "songs", "playlists" })
        {
            yield return new($"Search {kind} paused account invalidation expires only affected platform cache",
                () => PausedAccountInvalidation(kind));
            yield return new($"Search {kind} invalidated initial recomputation never swallows resubmission",
                () => InvalidatedInitialResubmission(kind));
        }
    }

    private static JsonElement[] CanonicalRows(JsonElement snapshot)
    {
        Check(!snapshot.GetProperty("partial").GetBoolean(), snapshot.GetRawText());
        var rows = snapshot.GetProperty("items").EnumerateArray().ToArray();
        foreach (var row in rows)
        {
            _ = SongRow.Read(row);
            var keys = row.GetProperty("sources").EnumerateArray().Select(source => S(source, "sourceKey")).ToArray();
            Check(keys.Length == keys.Distinct(StringComparer.Ordinal).Count());
            Check(keys.Skip(1).SequenceEqual(keys.Skip(1).Order(StringComparer.Ordinal)), "non-primary sources are not canonical");
        }
        return rows;
    }

    private static async Task CanonicalRounds()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r,
            [Song(r.Platform, r.Platform + r.Page),
             Song(r.Platform, "unique-" + r.Page, r.Platform + " Unique " + r.Page)], r.Page < 3));
        string id = S(await f.Start(["wy", "tx"]), "id");
        var previous = CanonicalRows(await f.Idle(id));
        var shared = previous.Single(row => S(row, "title") == "Song");
        string sharedId = S(shared, "songId"), primary = S(shared.GetProperty("sources")[0], "sourceKey");
        for (int round = 2; round <= 3; round++)
        {
            await f.Call("next", id);
            var current = CanonicalRows(await f.Idle(id));
            Check(current.Take(previous.Length).Select(row => S(row, "songId"))
                .SequenceEqual(previous.Select(row => S(row, "songId"))), "an already displayed row moved");
            shared = current.Single(row => S(row, "songId") == sharedId);
            Check(shared.GetProperty("sources").GetArrayLength() == round * 2 &&
                S(shared.GetProperty("sources")[0], "sourceKey") == primary);
            previous = current;
        }
        Check(f.Catalog.Calls.Count == 6);
    }

    private static async Task PairwiseRounds()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r,
            [Song(r.Platform, r.Platform + r.Page, duration: r.Platform == "wy" ? 103 : r.Page == 1 ? 100 : 106)], r.Page == 1));
        string id = S(await f.Start(["wy", "tx"]), "id");
        var first = CanonicalRows(await f.Idle(id));
        Check(first.Length == 1 && first[0].GetProperty("sources").GetArrayLength() == 2);
        await f.Call("next", id);
        var rows = CanonicalRows(await f.Idle(id));
        Check(rows.Length == 2 && S(rows[0], "songId") == S(first[0], "songId"));
        Check(rows[0].GetProperty("sources").GetArrayLength() == 3 &&
            rows[1].GetProperty("duration").GetDouble() == 106);

        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var library = new MusicLibraryOwner(store);
        async Task<JsonElement> Call(string method, object data)
        {
            var reply = await library.ExecuteAsync(method, data);
            Check(reply.GetProperty("ok").GetBoolean(), method + ":" + reply.GetRawText());
            return reply;
        }
        await Call("music.library.addSongs", new { id = "favorites", ifRevision = 0, songs = rows });
        var favorites = await Call("music.library.detail", new { id = "favorites" });
        Check(favorites.GetProperty("songs").GetArrayLength() == 2);
        await Call("music.queue.replace", new { songs = rows, index = 0 });
        var queue = await Call("music.queue.get", new { });
        var current = queue.GetProperty("current");
        _ = SongRow.Read(current.GetProperty("song"));
        await Call("music.history.recordPlayed", new { entryId = S(current, "id"), at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1 });
        Check((await Call("music.history.get", new { kind = "days" })).GetProperty("total").GetInt32() == 1);
    }

    private static async Task SameRoundSubgroups()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, r.Platform == "wy"
            ? [Song("wy", "100", duration: 100), Song("wy", "200", duration: 200)]
            : [Song("tx", "200", duration: 200)]));
        var rows = CanonicalRows(await f.Idle(S(await f.Start(["wy", "tx"]), "id")));
        Check(rows.Length == 2 && rows[0].GetProperty("duration").GetDouble() == 200,
            "compatible secondary group must gain the TX consensus rank");
        Check(rows[0].GetProperty("sources").GetArrayLength() == 2 && NativeId(rows[0]) == "200");
    }

    private static async Task LaterRoundSubgroups()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(r.Platform == "wy"
            ? Page(r, [Song("wy", "100", duration: 100), Song("wy", "200", duration: 200)])
            : Page(r, r.Page switch
            {
                1 => [Song("tx", "unrelated", "Unrelated")],
                2 => [Song("tx", "200", duration: 200)],
                _ => [Song("tx", "200b", duration: 200), Song("tx", "100", duration: 100)]
            }, r.Page < 3));
        string id = S(await f.Start(["wy", "tx"]), "id");
        var first = CanonicalRows(await f.Idle(id));
        Check(first.Length == 3);
        for (int round = 2; round <= 3; round++)
        {
            await f.Call("next", id);
            var rows = CanonicalRows(await f.Idle(id));
            Check(rows.Select(row => S(row, "songId")).SequenceEqual(first.Select(row => S(row, "songId"))));
            var longer = rows.Single(row => row.GetProperty("duration").GetDouble() == 200);
            Check(longer.GetProperty("sources").GetArrayLength() == round && NativeId(longer) == "200");
            if (round == 3) Check(rows.Single(row => row.GetProperty("duration").GetDouble() == 100)
                .GetProperty("sources").GetArrayLength() == 2);
        }
        Check(f.Catalog.Calls.Count(r => r.Platform == "wy") == 1 && f.Catalog.Calls.Count == 4);
    }

    private static void AccountResponses(Fixture f, string kind, Func<string> account)
    {
        f.Catalog.Handler = (r, _) =>
        {
            string value = r.Platform == "wy" ? account() : "unchanged";
            return Task.FromResult(Page(r, [kind == "songs"
                ? Song(r.Platform, value, r.Platform + " " + value)
                : Card(r.Platform, value, r.Platform + " " + value)]));
        };
    }
    private static void CurrentAccount(JsonElement result, string kind, string account)
    {
        string field = kind == "songs" ? "title" : "name";
        var names = result.GetProperty("items").EnumerateArray().Select(row => S(row, field)).ToHashSet();
        Check(names.SetEquals(["wy " + account, "tx unchanged"]) && !result.GetProperty("partial").GetBoolean());
    }
    private static async Task PausedAccountInvalidation(string kind)
    {
        await using var f = new Fixture();
        string account = "A"; AccountResponses(f, kind, () => account);
        CurrentAccount(await f.Idle(S(await f.Start(["wy", "tx"], kind), "id")), kind, "A");
        await f.Search.PrepareShutdownAsync();
        account = "B"; f.Search.Invalidate("wy");
        await f.Search.CancelShutdownPreparationAsync();
        CurrentAccount(await f.Idle(S(await f.Start(["wy", "tx"], kind), "id")), kind, "B");
        Check(f.Catalog.Calls.Count(r => r.Platform == "wy") == 2 &&
            f.Catalog.Calls.Count(r => r.Platform == "tx") == 1, "pause lost invalidation or erased an unrelated cache");
    }
    private static async Task InvalidatedInitialResubmission(string kind)
    {
        await using var f = new Fixture();
        string account = "A"; AccountResponses(f, kind, () => account);
        string previous = S(await f.Start(["wy", "tx"], kind), "id");
        CurrentAccount(await f.Idle(previous), kind, "A");
        account = "B";
        Task<JsonElement> request, duplicate;
        // Select the legal admission-before-recomputation schedule with the existing owner lock.
        // No query state is modified and no product test hook or artificial delay is required.
        object gate = f.Search.GetType().GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Search)!;
        lock (gate)
        {
            f.Search.Invalidate("wy");
            request = f.Start(["wy", "tx"], kind);
            duplicate = f.Start(["wy", "tx"], kind);
        }
        string next = S(await request, "id");
        Check(next != previous && S(await duplicate, "id") == next, "invalid identity reused as a live initial search");
        CurrentAccount(await f.Idle(next), kind, "B");
        Check(f.Catalog.Calls.Count(r => r.Platform == "wy") == 2 &&
            f.Catalog.Calls.Count(r => r.Platform == "tx") == 1);
    }
}
