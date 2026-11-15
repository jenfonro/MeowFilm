using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class FilmSourcesTests
{
    private const string RawSourceDocument = """
        {"version":1,"Version":[],"Videos":{},"é":1,"e\u0301":2,"rootExtra":{"integer":900719925474099312345678901234567890,"fraction":0.12345678901234567890123456789},
         "cats":[
          {"installationId":"a","InstallationId":[],"name":"猫a","Name":false,"sourceType":"local","enabled":true,"updateState":"future-update","sites":[
           {"key":"video","name":"缓存名","api":"/cached","actions":["home","category","detail","play","search"],
            "methods":{"home":"GET","category":"POST","detail":"POST","play":"POST","search":"POST"},
            "siteExtra":{"number":1e999}}],
           "catExtra":{"number":1.2345678901234567890123456789}},
          {"installationId":"b","name":"猫b","sourceType":"local","enabled":true,"updateState":"idle","sites":[]}],
         "videos":[
          {"key":{"installationId":"a","InstallationId":[],"siteKey":"video","keyExtra":{"number":90071992547409931234567890}},
           "Key":false,"Order":[],
           "name":"缓存名","api":"/cached","actions":["home","category","detail","play","search"],
           "methods":{"home":"GET","category":"POST","detail":"POST","play":"POST","search":"POST"},
           "options":{"enabled":false,"Enabled":[],"homeVisible":true,"searchEnabled":false,"aggregateImages":false,
            "optionsExtra":{"é":3,"e\u0301":4,"number":1.2345678901234567890123456789}},
           "order":37,"detection":{"result":"future-result","checkedAt":100,"playbackResolved":false,
            "steps":[{"operation":"home","state":"success","stepExtra":{"number":1e999}}],
            "detectionExtra":{"number":90071992547409931234567890}},
           "videoExtra":{"number":1e999}},
          {"key":{"installationId":"b","siteKey":"video"},"name":"缓存b","api":"/cached","actions":[],"methods":{},
           "options":{"enabled":true,"homeVisible":false,"searchEnabled":true,"aggregateImages":false},"order":8}],
         "deleted":[{"installationId":"a","siteKey":"deleted","deletedExtra":{"number":1.2345678901234567890123456789}}]}
        """;

    private static JsonElement RawVideo(JsonElement root, string installation, string site) =>
        root.GetProperty("videos").EnumerateArray().Single(row => row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("key", out var key) && key.GetProperty("installationId").GetString() == installation &&
            key.GetProperty("siteKey").GetString() == site);

    private static async Task RawSourcePersistence()
    {
        var runtime = new Runtime(); runtime.Add("a", "video", "deleted"); runtime.Add("b");
        var store = new Store { Bytes = Encoding.UTF8.GetBytes(RawSourceDocument) };
        using var before = JsonDocument.Parse(store.Bytes!);
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(runtime.Calls.Count == 0 && service.Videos.Single(video => video.Key == Key("a")).Order == 37 &&
                service.Cats.Single(cat => cat.InstallationId == "a").UpdateState == "future-update",
                "cold projection keeps user facts and unknown display values without contacting Node");
            await service.SaveVideoOptionsAsync(Key("b"), new(true, true, true, false), order: 9);
            using (var unrelated = JsonDocument.Parse(store.Bytes!))
                Check(JsonElement.DeepEquals(RawVideo(unrelated.RootElement, "a", "video").GetProperty("options"),
                    RawVideo(before.RootElement, "a", "video").GetProperty("options")),
                    "another row's save does not rewrite these user facts");
            await service.SaveVideoOptionsAsync(Key("a"), new(false, false, false, false), order: 38);
            await service.RefreshAsync();
            Check(service.Videos.Single(video => video.Key == Key("a")).Options == new FilmVideoOptions(false, false, false, false) &&
                service.Videos.All(video => video.Key.SiteKey != "deleted") &&
                service.Cats.All(cat => cat.CanDetect), "metadata rebuild retains exact user options and tombstones");
        }
        int calls = runtime.Calls.Count;
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(runtime.Calls.Count == calls && reopened.Videos.Single(video => video.Key == Key("a")).Order == 38 &&
            reopened.Videos.Single(video => video.Key == Key("b")).Order == 9 &&
            reopened.Cats.All(cat => !cat.CanDetect), "cold read restores facts but does not authorize a cached generation");
        using var after = JsonDocument.Parse(store.Bytes!);
        var root = after.RootElement;
        Check(root.GetProperty("Version").ValueKind == JsonValueKind.Array && root.GetProperty("Videos").ValueKind == JsonValueKind.Object &&
            root.GetProperty("é").GetInt32() == 1 && root.GetProperty("e\u0301").GetInt32() == 2 &&
            root.GetProperty("rootExtra").GetProperty("integer").GetRawText() == "900719925474099312345678901234567890" &&
            root.GetProperty("rootExtra").GetProperty("fraction").GetRawText() == "0.12345678901234567890123456789",
            "root values and distinct Unicode keys are not normalized through CLR numbers or dictionaries");
        var video = RawVideo(root, "a", "video");
        var original = RawVideo(before.RootElement, "a", "video");
        foreach (string field in new[] { "key", "Key", "Order", "detection", "videoExtra" })
            Check(JsonElement.DeepEquals(video.GetProperty(field), original.GetProperty(field)),
                "unrelated saves and synchronization preserve nested facts and extensions: " + field);
        Check(JsonElement.DeepEquals(video.GetProperty("options").GetProperty("optionsExtra"),
                original.GetProperty("options").GetProperty("optionsExtra")) &&
            !video.GetProperty("options").GetProperty("homeVisible").GetBoolean() &&
            video.GetProperty("options").GetProperty("Enabled").ValueKind == JsonValueKind.Array &&
            video.GetProperty("videoExtra").GetProperty("number").GetRawText() == "1e999" &&
            video.GetProperty("detection").GetProperty("steps")[0].GetProperty("stepExtra").GetProperty("number").GetRawText() == "1e999" &&
            video.GetProperty("options").GetProperty("optionsExtra").GetProperty("number").GetRawText() == "1.2345678901234567890123456789" &&
            video.GetProperty("options").GetProperty("optionsExtra").GetProperty("é").GetInt32() == 3 &&
            video.GetProperty("options").GetProperty("optionsExtra").GetProperty("e\u0301").GetInt32() == 4,
            "nested unread numeric material and exact keys remain intact");
        var cat = root.GetProperty("cats").EnumerateArray().Single(row => row.GetProperty("installationId").GetString() == "a");
        Check(cat.GetProperty("InstallationId").ValueKind == JsonValueKind.Array && cat.GetProperty("Name").ValueKind == JsonValueKind.False &&
            cat.GetProperty("catExtra").GetProperty("number").GetRawText() == "1.2345678901234567890123456789" &&
            cat.GetProperty("sites").EnumerateArray().Single(site => site.GetProperty("key").GetString() == "video")
                .GetProperty("siteExtra").GetProperty("number").GetRawText() == "1e999" &&
            root.GetProperty("deleted")[0].GetProperty("deletedExtra").GetProperty("number").GetRawText() == "1.2345678901234567890123456789",
            "cache refresh and fact edits retain matching cat/site and deletion extensions");
    }

    private static async Task AmbiguousSourceIdentityMembers()
    {
        string[] isolated =
        [
            """{"key":{"installationId":"a","siteKey":"first"},"key":{"installationId":"a","siteKey":"last"},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":1,"extra":{"number":1e999}}""",
            """{"key":{"installationId":"a","installationId":"c","siteKey":"inner-install"},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":2}""",
            """{"key":{"installationId":"a","siteKey":"inner-first","siteKey":"inner-last"},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":3}""",
            """{"key":{"installationId":"a","siteKey":"same"},"key":{"installationId":"a","siteKey":"same"},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":4}""",
            """{"key":false,"key":{"installationId":"a","siteKey":"partial"},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":5}""",
            """{"key":{"installationId":"a","siteKey":"first"},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":6}"""
        ];
        const string ambiguousDeleted = """{"installationId":"a","siteKey":"deleted-first","siteKey":"deleted-last","extra":{"number":1e999}}""";
        using var input = JsonDocument.Parse(RawSourceDocument);
        string videos = string.Join(",", input.RootElement.GetProperty("videos").EnumerateArray().Select(row => row.GetRawText()));
        var store = new Store { Bytes = Encoding.UTF8.GetBytes(
            "{\"version\":1,\"cats\":[],\"videos\":[" + string.Join(",", isolated) + "," + videos + "],\"deleted\":[" + ambiguousDeleted + "]}") };
        var runtime = new Runtime();
        runtime.Add("a", "video", "first", "last", "inner-install", "inner-first", "inner-last", "same", "partial", "deleted-first", "deleted-last");
        runtime.Add("b"); runtime.Add("c", "inner-install");
        FilmSourceKey[] candidates =
        [
            new("a", "first"), new("a", "last"), new("a", "inner-install"), new("c", "inner-install"),
            new("a", "inner-first"), new("a", "inner-last"), new("a", "same"), new("a", "partial"),
            new("a", "deleted-first"), new("a", "deleted-last")
        ];
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(service.Videos.Count == 2 && service.Videos.All(video => !candidates.Contains(video.Key)),
                "cold projection fences every recognizable outer or nested identity and all ambiguous deletion candidates");
            await service.SaveVideoOptionsAsync(Key("b"), new(false, true, false, false), order: 11);
            await service.RefreshAsync();
            Check(service.Videos.Count == 2 && service.Videos.All(video => !candidates.Contains(video.Key)),
                "authoritative metadata refresh cannot recreate isolated user facts as new enabled defaults");
            int writes = store.Writes;
            foreach (var key in candidates)
            {
                await Code(() => service.SaveVideoOptionsAsync(key, new()), "NOT_FOUND");
                await Code(() => service.DeleteVideoAsync(key), "NOT_FOUND");
            }
            Check(store.Writes == writes, "neither candidate options nor removal overwrite any isolated raw row");
        }
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(reopened.Videos.Count == 2 && reopened.Videos.Single(video => video.Key == Key("b")).Order == 11 &&
            !reopened.Videos.Single(video => video.Key == Key("b")).Options.Enabled,
            "independent healthy changes reopen without promoting ambiguous identities");
        using var after = JsonDocument.Parse(store.Bytes!);
        var records = after.RootElement.GetProperty("videos");
        Check(records.GetArrayLength() == isolated.Length + 2 &&
            isolated.Select((raw, index) => records[index].GetRawText() == raw).All(equal => equal) &&
            after.RootElement.GetProperty("deleted")[0].GetRawText() == ambiguousDeleted,
            "all duplicate members candidate identities and exact numeric raw material survive unrelated saves and synchronization");
    }

    private static async Task IsolatedSourceRecords()
    {
        const string broken = """{"key":{"installationId":"a","siteKey":"broken"},"order":7,"options":{"enabled":"not-a-boolean"},"extra":{"number":1e999}}""";
        const string duplicate = """{"key":{"installationId":"a","siteKey":"duplicate"},"name":"重复","api":"","actions":[],"methods":{},"options":{"enabled":true,"homeVisible":true,"searchEnabled":true,"aggregateImages":false},"order":5}""";
        using var input = JsonDocument.Parse(RawSourceDocument);
        string videos = string.Join(",", input.RootElement.GetProperty("videos").EnumerateArray().Select(row => row.GetRawText()));
        var store = new Store { Bytes = Encoding.UTF8.GetBytes($$$"""
            {"version":1,"rootExtra":{"number":1e999},"cats":[{"installationId":"a","sites":"broken"}],
             "videos":[{{{videos}}},{{{broken}}},null,{{{duplicate}}},{{{duplicate}}}],
             "deleted":[{"installationId":"a","siteKey":"deleted","extra":{"number":1e999}},null,{"siteKey":"bad-identity"}]}
            """) };
        var runtime = new Runtime(); runtime.Add("a", "video", "deleted", "broken", "duplicate"); runtime.Add("b");
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(service.Cats.Count == 0 && service.Videos.Count == 2 && service.CurrentSearchSources.Count == 0,
                "unusable cat cache does not erase options or authorize cached execution; malformed siblings are isolated");
            await service.SaveVideoOptionsAsync(Key("b"), new(true, true, false, false), order: 12);
            await service.RefreshAsync();
            Check(service.Cats.Count == 2 && service.Videos.Count == 2 && service.Videos.All(video =>
                video.Key.SiteKey is not ("deleted" or "broken" or "duplicate")) &&
                service.Videos.Single(video => video.Key == Key("a")).Order == 37,
                "rebuilding metadata does not recreate invalid or ambiguous facts as default enabled sources");
        }
        using var after = JsonDocument.Parse(store.Bytes!);
        var rows = after.RootElement.GetProperty("videos").EnumerateArray().ToArray();
        Check(rows.Length == 6 && rows.Count(row => row.ValueKind == JsonValueKind.Null) == 1 &&
            RawVideo(after.RootElement, "a", "broken").GetRawText() == broken &&
            rows.Count(row => row.ValueKind == JsonValueKind.Object && row.GetProperty("key").GetProperty("siteKey").GetString() == "duplicate") == 2 &&
            after.RootElement.GetProperty("deleted").GetArrayLength() == 3,
            "bad records and unknown deletion material survive unrelated writes in their original form");
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(reopened.Videos.Count == 2 && reopened.Videos.Single(video => video.Key == Key("b")).Order == 12 &&
            !reopened.Videos.Single(video => video.Key == Key("b")).Options.SearchEnabled,
            "the healthy edit is durable independently of the retained bad records");
    }

    private static async Task UnknownUpdateState()
    {
        var runtime = new Runtime(); runtime.Add("a"); runtime.Nodes["a"].UpdateState = "future-state";
        await using var service = new FilmSourceService(runtime, new Store());
        await service.RefreshAsync();
        var cat = service.Cats.Single();
        Check(cat.UpdateState == "future-state" && cat.CanDetect && cat.Generation == 1 &&
            new FilmSettingsCatRow(cat).UpdateText == "future-state", "unknown update state displays raw text without retiring a valid source");
        var result = await service.DetectAsync(Key("a"));
        Check(result.Result == "valid" && runtime.Calls.Any(call => call.Method == "invoke" &&
            call.Input.GetProperty("generation").GetInt64() == 1), "real execution still uses the exact admitted generation");
    }

    private static async Task SourceDocumentBoundaries()
    {
        var runtime = new Runtime();
        foreach (string root in new[]
        {
            "{broken", "null", "[]", """{"version":2,"cats":[],"videos":[],"deleted":[]}""",
            """{"version":1,"cats":[],"videos":{},"deleted":[]}""", """{"version":1,"cats":[],"videos":[],"deleted":{}}""",
            """{"version":2,"version":1,"cats":[],"videos":[],"deleted":[]}""",
            """{"version":1,"cats":{},"cats":[],"videos":[],"deleted":[]}""",
            """{"version":1,"cats":[],"videos":{},"videos":[],"deleted":[]}""",
            """{"version":1,"cats":[],"videos":[],"deleted":{},"deleted":[]}"""
        })
        {
            var broken = new Store { Bytes = Encoding.UTF8.GetBytes(root) };
            await using var invalid = new FilmSourceService(runtime, broken);
            await Code(() => invalid.InitializeAsync(), "INVALID_DOCUMENT");
            await Code(() => invalid.RefreshAsync(), "INVALID_DOCUMENT");
            Check(broken.Writes == 0 && Encoding.UTF8.GetString(broken.Bytes!) == root && runtime.Calls.Count == 0,
                "invalid fact roots and unsupported versions never become empty successful storage or start Node");
        }
        var store = new Store { Bytes = Encoding.UTF8.GetBytes(RawSourceDocument) };
        await using var service = new FilmSourceService(runtime, store);
        await service.InitializeAsync();
        var before = service.Videos.Single(video => video.Key == Key("b"));
        byte[] external = Encoding.UTF8.GetBytes("""{"version":1,"cats":[],"videos":[],"deleted":[],"external":true}""");
        store.Bytes = external;
        int publications = 0; service.Changed += (_, _) => publications++;
        try { await service.SaveVideoOptionsAsync(Key("b"), new(false, false, false, false)); throw new InvalidOperationException("Expected CAS conflict"); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
        Check(publications == 0 && store.Bytes.SequenceEqual(external) && service.Videos.Single(video => video.Key == Key("b")) == before,
            "real CAS conflicts preserve both the external bytes and the last confirmed publication");
    }
}
