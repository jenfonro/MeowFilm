using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static class MusicSourceExclusionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("wrong-source storage exact scope idempotency restart and explicit restore", Persist);
        yield return new("wrong-source failed write never returns a successful exclusion receipt", Failure);
    }
    private static readonly string A = "11111111-1111-4111-8111-111111111111";
    private static readonly string B = "22222222-2222-4222-8222-222222222222";
    private static object Entry(string song = "song:a", string? provider = null, string platform = "wy") =>
        new { songId = song, provider = provider ?? A, platform, title = "Synthetic", artist = "Fixture", providerName = "Provider" };
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("exclusion contract"); }
    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? input = null)
    {
        var result = await owner.ExecuteAsync(method, input);
        Check(result.GetProperty("ok").GetBoolean()); return result;
    }
    private static async Task Persist()
    {
        var store = new QualityFixture.DocumentStore();
        await using (var owner = new MusicLibraryOwner(store))
        {
            Check((await Ok(owner, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 0 && store.Writes == 0);
            await Ok(owner, "music.sourceExclusions.add", Entry());
            int writes = store.Writes;
            Check(!(await Ok(owner, "music.sourceExclusions.add", Entry())).GetProperty("added").GetBoolean() && store.Writes == writes);
            await Ok(owner, "music.sourceExclusions.add", Entry(provider: B));
            await Ok(owner, "music.sourceExclusions.add", Entry(platform: "kg"));
            await Ok(owner, "music.sourceExclusions.add", Entry(song: "song:b"));
            await Ok(owner, "music.sourceExclusions.add", Entry(provider: "official"));
            Check((await Ok(owner, "music.sourceExclusions.list", new { songId = "song:a" })).GetProperty("items").GetArrayLength() == 4);
            await Ok(owner, "music.settings.save", new { ifRevision = 0, lyrics = false });
        }
        await using var reopened = new MusicLibraryOwner(store);
        Check((await Ok(reopened, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 5);
        await Ok(reopened, "music.sourceExclusions.remove", new { songId = "song:a", provider = A, platform = "wy" });
        var rows = (await Ok(reopened, "music.sourceExclusions.list")).GetProperty("items");
        Check(rows.GetArrayLength() == 4 && rows.EnumerateArray().Any(row => row.GetProperty("provider").GetString() == B));
    }
    private static async Task Failure()
    {
        var store = new QualityFixture.DocumentStore { Fault = true, FaultNamespace = MusicLibraryOwner.DocumentNamespace };
        await using var owner = new MusicLibraryOwner(store);
        var reply = await owner.ExecuteAsync("music.sourceExclusions.add", Entry());
        Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
        await owner.ReconcileAsync();
        Check((await Ok(owner, "music.sourceExclusions.list")).GetProperty("items").GetArrayLength() == 0);
    }
}
