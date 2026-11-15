using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static partial class FilmLibraryTests
{
    private const string RawFavorite = """
        {"id":"source:raw","title":"原始","poster":"","score":"","recordExtra":{"number":1.234567890123456789012345678901},
         "playbackContext":{"contextExtra":{"value":90071992547409931234567890},"metadata":false,
          "target":{"id":"source:raw","Id":{"extra":1e999},"kind":"source","Kind":false,"title":"原始","Title":[],
           "Source":false,"targetExtra":{"é":7,"e\u0301":8},
           "source":{"key":"raw-source","detailId":0,"lineId":"0","sourceExtra":{"number":1e999}}}}}
        """;

    private static async Task RawPersistence()
    {
        var store = new FilmMemoryStore { Data = Encoding.UTF8.GetBytes($$"""
            {"version":1,"Version":[],"Favorites":{},"é":1,"e\u0301":2,"rootExtra":{"integer":900719925474099312345678901234567890,
             "fraction":0.12345678901234567890123456789},"favorites":[{{RawFavorite}}]}
            """) };
        using var before = JsonDocument.Parse(store.Data!);
        await using (var library = new FilmLibrary(store))
        {
            var original = (await library.ReadAsync()).Single();
            Check(original.PlaybackContext!.Target.Id == "source:raw" &&
                original.PlaybackContext.Target.Source!.Value.GetProperty("detailId").GetInt32() == 0,
                "persistent keys are exact: differently cased extras and optional context fields do not change identity");
            await library.SetFavoriteAsync(Card(number: 200), true);
            await library.SetFavoriteAsync(original with { Title = "明确修改标题" }, true);
        }
        await using var reopened = new FilmLibrary(store);
        Check((await reopened.ReadAsync()).Count == 2 &&
            (await reopened.ReadAsync()).Single(row => row.Id == "source:raw").Title == "明确修改标题",
            "real edits and independent favorites survive cold reopen");
        using var after = JsonDocument.Parse(store.Data!);
        var root = after.RootElement;
        Check(root.GetProperty("Version").ValueKind == JsonValueKind.Array && root.GetProperty("Favorites").ValueKind == JsonValueKind.Object &&
            root.GetProperty("é").GetInt32() == 1 && root.GetProperty("e\u0301").GetInt32() == 2 &&
            JsonElement.DeepEquals(root.GetProperty("rootExtra"), before.RootElement.GetProperty("rootExtra")) &&
            root.GetProperty("rootExtra").GetProperty("integer").GetRawText() == "900719925474099312345678901234567890" &&
            root.GetProperty("rootExtra").GetProperty("fraction").GetRawText() == "0.12345678901234567890123456789",
            "distinct Unicode keys and high precision root values remain exact");
        var saved = root.GetProperty("favorites").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "source:raw");
        using var expected = JsonDocument.Parse(RawFavorite);
        Check(JsonElement.DeepEquals(saved.GetProperty("recordExtra"), expected.RootElement.GetProperty("recordExtra")) &&
            saved.GetProperty("recordExtra").GetProperty("number").GetRawText() == "1.234567890123456789012345678901" &&
            JsonElement.DeepEquals(saved.GetProperty("playbackContext"), expected.RootElement.GetProperty("playbackContext")) &&
            saved.GetProperty("playbackContext").GetProperty("target").GetProperty("source")
                .GetProperty("sourceExtra").GetProperty("number").GetRawText() == "1e999",
            "editing a title does not rewrite existing context target source extensions or optional malformed material");
    }

    private static async Task IsolatedRecords()
    {
        const string broken = """{"id":"broken","title":{},"playbackContext":{"target":{"id":"broken"}},"raw":{"number":1e999}}""";
        const string ambiguous = """{"id":"ambiguous","title":"重复","playbackContext":{"target":{"id":"tmdb:tv:300","kind":"tv","title":"重复","metadataProvider":"tmdb","metadataId":"300"}}}""";
        var store = new FilmMemoryStore { Data = Encoding.UTF8.GetBytes($$"""
            {"version":1,"favorites":[{{broken}},null,{{ambiguous}},{{RawFavorite}},{{ambiguous}}]}
            """) };
        await using (var library = new FilmLibrary(store))
        {
            Check((await library.ReadAsync()).Single().Id == "source:raw", "bad and ambiguous siblings do not reject the healthy row");
            await library.SetFavoriteAsync(Card(number: 200), true);
            int writes = store.Writes;
            await Error("CORRUPT_STORAGE", () => library.SetFavoriteAsync(Card(number: 300), true));
            Check(store.Writes == writes, "an ambiguous identity is not guessed or overwritten");
        }
        await using var reopened = new FilmLibrary(store);
        Check((await reopened.ReadAsync()).Count == 2, "healthy records still reopen independently");
        using var after = JsonDocument.Parse(store.Data!);
        var rows = after.RootElement.GetProperty("favorites").EnumerateArray().ToArray();
        Check(rows.Length == 6 && rows[0].GetRawText() == broken && rows[1].ValueKind == JsonValueKind.Null &&
            rows.Count(row => row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("id", out var id) && id.GetString() == "ambiguous") == 2,
            "unreadable original records are retained rather than filtered out by an unrelated save");
        foreach (string badRoot in new[]
        {
            "{broken", "[]", """{"version":2,"favorites":[]}""", """{"version":1,"favorites":{}}""",
            """{"version":2,"version":1,"favorites":[]}""",
            """{"version":1,"favorites":{},"favorites":[]}"""
        })
        {
            store.Data = Encoding.UTF8.GetBytes(badRoot);
            int writes = store.Writes;
            await Error("CORRUPT_STORAGE", () => reopened.ReadAsync());
            await Error("CORRUPT_STORAGE", () => reopened.SetFavoriteAsync(Card(), false));
            Check(store.Writes == writes && Encoding.UTF8.GetString(store.Data!) == badRoot, "bad roots are never reset");
        }
    }

    private static async Task AmbiguousIdentityMembers()
    {
        string[] isolated =
        [
            """{"id":"duplicate-id","title":"row","playbackContext":{"target":{"id":"tmdb:tv:310","id":"tmdb:tv:311","kind":"tv","title":"row","metadataProvider":"tmdb","metadataId":"311"}},"extra":{"number":1e999}}""",
            """{"id":"duplicate-target","title":"row","playbackContext":{"target":{"id":"tmdb:tv:312"},"target":{"id":"tmdb:tv:313","kind":"tv","title":"row","metadataProvider":"tmdb","metadataId":"313"}}}""",
            """{"id":"duplicate-context","title":"row","playbackContext":{"target":{"id":"tmdb:tv:314"}},"playbackContext":{"target":{"id":"tmdb:tv:315","kind":"tv","title":"row","metadataProvider":"tmdb","metadataId":"315"}}}""",
            """{"id":"same-id-twice","title":"row","playbackContext":{"target":{"id":"tmdb:tv:316","id":"tmdb:tv:316","kind":"tv","title":"row","metadataProvider":"tmdb","metadataId":"316"}}}""",
            """{"id":"partial-context","title":"row","playbackContext":false,"playbackContext":{"target":{"id":"tmdb:tv:317","kind":"tv","title":"row","metadataProvider":"tmdb","metadataId":"317"}}}""",
            """{"id":"colliding-row","title":"row","playbackContext":{"target":{"id":"tmdb:tv:310","kind":"tv","title":"row","metadataProvider":"tmdb","metadataId":"310"}}}"""
        ];
        var store = new FilmMemoryStore { Data = Encoding.UTF8.GetBytes(
            "{\"version\":1,\"favorites\":[" + string.Join(",", isolated) + "," + RawFavorite + "]}") };
        await using (var library = new FilmLibrary(store))
        {
            Check((await library.ReadAsync()).Single().Id == "source:raw",
                "cold read isolates all candidates, including a separate otherwise healthy row sharing an ambiguous candidate");
            await library.SetFavoriteAsync(Card(number: 200), true);
            int writes = store.Writes;
            for (int number = 310; number <= 317; number++)
            {
                await Error("CORRUPT_STORAGE", () => library.SetFavoriteAsync(Card(number: number), true));
                await Error("CORRUPT_STORAGE", () => library.SetFavoriteAsync(Card(number: number), false));
            }
            Check(store.Writes == writes, "neither add/update nor removal guesses an earlier or last duplicate identity");
        }
        await using var reopened = new FilmLibrary(store);
        Check((await reopened.ReadAsync()).Count == 2, "unrelated healthy writes reopen without promoting isolated candidates");
        using var after = JsonDocument.Parse(store.Data!);
        var records = after.RootElement.GetProperty("favorites");
        Check(records.GetArrayLength() == isolated.Length + 2 &&
            isolated.Select((raw, index) => records[index].GetRawText() == raw).All(equal => equal),
            "duplicate member order, every original identity and exact unused numbers survive unrelated persistence");
    }

    private static async Task CasConflict()
    {
        var store = new FilmMemoryStore();
        await using var library = new FilmLibrary(store);
        await library.SetFavoriteAsync(Card(), true);
        byte[] external = Encoding.UTF8.GetBytes("""{"version":1,"favorites":[],"external":true}""");
        int publications = 0; library.Changed += (_, _) => publications++;
        store.BeforeWrite = _ => { store.Data = external; return Task.CompletedTask; };
        try { await library.SetFavoriteAsync(Card(number: 200), true); throw new InvalidOperationException("Expected CAS conflict"); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
        Check(publications == 0 && store.Data!.SequenceEqual(external), "a failed CAS preserves the other writer and publishes no success");
    }

    private static async Task GroupPrivacy()
    {
        var store = new FilmMemoryStore();
        await using var library = new FilmLibrary(store);
        var target = new FilmPlaybackTarget("group:private", "group", "聚合",
            Members: JsonSerializer.Deserialize<JsonElement>("""
                [{"sourceKey":"source:raw","detailId":0,"credentials":"private-member-secret","playId":"private-play-reference"}]
                """));
        var context = new FilmSearchContext(new(null!, "private-search-query"),
            FilmJson.Encode(new { displayKey = "group:private", credential = "private-search-secret" }));
        await library.SetFavoriteAsync(new(target.Id, target.Title, "", "") { PlaybackContext = new(target), SearchContext = context }, true);
        var members = (await library.ReadAsync()).Single().PlaybackContext!.Target.Members!.Value;
        var metadata = Card(number: 200);
        await library.SetFavoriteAsync(metadata with { PlaybackContext = new(metadata.PlaybackContext!.Target with
        {
            Source = FilmJson.Encode(new { credential = "private-unused-source" }),
            Members = FilmJson.Encode(new { credential = "private-unused-members" })
        }) }, true);
        Check(members[0].GetProperty("detailId").ValueKind == JsonValueKind.Number &&
            members[0].EnumerateObject().Count() == 2 &&
            !Encoding.UTF8.GetString(store.Data!).Contains("private-", StringComparison.Ordinal),
            "new favorites project only stable member identities and never serialize search or transient credentials");
    }
}
