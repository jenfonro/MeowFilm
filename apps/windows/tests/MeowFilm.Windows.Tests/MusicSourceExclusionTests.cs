using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static class MusicSourceExclusionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("wrong-source filtered playback reads only selected identities and removal ignores old display", Isolation);
        yield return new("wrong-source storage exact scope idempotency restart and explicit restore", Persist);
        yield return new("wrong-source failed write never returns a successful exclusion receipt", Failure);
    }
    private static readonly string A = "11111111-1111-4111-8111-111111111111";
    private static readonly string B = "22222222-2222-4222-8222-222222222222";
    private static object Entry(string song, string? provider = null, string platform = "wy") =>
        new { songId = song, provider = provider ?? A, platform, title = "Synthetic", artist = "Fixture", providerName = "Provider" };
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("exclusion contract"); }
    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? input = null)
    {
        var result = await owner.ExecuteAsync(method, input);
        Check(result.GetProperty("ok").GetBoolean()); return result;
    }
    private static async Task Persist()
    {
        var store = new QualityFixture.Database();
        string songA;
        await using (var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage))
        {
            Check((await Ok(owner, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 0 && MusicTestStores.For(store).Writes == 0);
            await Ok(owner, "music.queue.replace", new { songs = new[] {
                MusicSongFixture.Online("wy", "a", "Track A", "Fixture", duration: 180),
                MusicSongFixture.Online("wy", "b", "Track B", "Fixture", duration: 180) }, index = 0 });
            var queue = await Ok(owner, "music.queue.get");
            songA = queue.GetProperty("entries")[0].GetProperty("song").GetProperty("songId").GetString()!;
            string songB = queue.GetProperty("entries")[1].GetProperty("song").GetProperty("songId").GetString()!;
            await Ok(owner, "music.sourceExclusions.add", Entry(songA));
            int writes = MusicTestStores.For(store).Writes;
            Check(!(await Ok(owner, "music.sourceExclusions.add", Entry(songA))).GetProperty("added").GetBoolean() && MusicTestStores.For(store).Writes == writes);
            await Ok(owner, "music.sourceExclusions.add", Entry(songA, provider: B));
            await Ok(owner, "music.sourceExclusions.add", Entry(songA, platform: "kg"));
            await Ok(owner, "music.sourceExclusions.add", Entry(songB));
            await Ok(owner, "music.sourceExclusions.add", Entry(songA, provider: "official"));
            Check((await Ok(owner, "music.sourceExclusions.list", new { songId = songA })).GetProperty("items").GetArrayLength() == 4);
            await Ok(owner, "music.settings.save", new { ifRevision = 0, lyrics = false });
        }
        await using var reopened = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
        Check((await Ok(reopened, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 5);
        await Ok(reopened, "music.sourceExclusions.remove", new { songId = songA, provider = A, platform = "wy" });
        var rows = (await Ok(reopened, "music.sourceExclusions.list")).GetProperty("items");
        Check(rows.GetArrayLength() == 4 && rows.EnumerateArray().Any(row => row.GetProperty("provider").GetString() == B));
    }
    private static async Task Isolation()
    {
        var store = new QualityFixture.Database(); var records = MusicTestStores.For(store);
        string songA, songB;
        await using (var setup = new MusicLibraryOwner(records, database: records.Storage))
        {
            await Ok(setup, "music.queue.replace", new { songs = new[] {
                MusicSongFixture.Online("wy", "selected", "Track A", "Fixture", duration: 180),
                MusicSongFixture.Online("wy", "brother", "Track B", "Fixture", duration: 180) }, index = 0 });
            var queue = await Ok(setup, "music.queue.get");
            songA = queue.GetProperty("entries")[0].GetProperty("song").GetProperty("songId").GetString()!;
            songB = queue.GetProperty("entries")[1].GetProperty("song").GetProperty("songId").GetString()!;
            await Ok(setup, "music.sourceExclusions.add", Entry(songA));
        }
        await records.Storage.WriteAsync(db =>
        {
            MusicStorageRecords.Areas["native-source-exclusions"].Relation("items").Replace(db,
                LJ.Obj(("songId", songB), ("provider", B), ("platform", "wy")), ["local", "broken"], 1);
            return true;
        });
        await using var owner = new MusicLibraryOwner(records, database: records.Storage);
        var selected = await Ok(owner, "music.sourceExclusions.list", new { songId = songA });
        Check(selected.GetProperty("items").GetArrayLength() == 1 && selected.GetProperty("items")[0].GetProperty("provider").GetString() == A);
        var damaged = await owner.ExecuteAsync("music.sourceExclusions.list");
        Check(!damaged.GetProperty("ok").GetBoolean() && damaged.GetProperty("code").GetString() == "CORRUPT_STORAGE");
        Check(await records.Storage.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM music_windows_exclusions_items WHERE items_key='broken'")) == 1);
        await Ok(owner, "music.sourceExclusions.remove", new { songId = songB, provider = B, platform = "wy" });
        Check((await Ok(owner, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 1);
    }

    private static async Task Failure()
    {
        var store = new QualityFixture.Database();
        await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
        await Ok(owner, "music.queue.replace", new { songs = new[] {
            MusicSongFixture.Online("wy", "failure", "Track failure", "Fixture", duration: 180) }, index = 0 });
        string id = (await Ok(owner, "music.queue.get")).GetProperty("current").GetProperty("song").GetProperty("songId").GetString()!;
        MusicTestStores.For(store).Fault = true;
        var reply = await owner.ExecuteAsync("music.sourceExclusions.add", Entry(id));
        Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
        await owner.ReconcileAsync();
        Check((await Ok(owner, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 0);
    }
}
