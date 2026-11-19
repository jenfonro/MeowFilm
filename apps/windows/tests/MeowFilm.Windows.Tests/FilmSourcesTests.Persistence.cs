using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class FilmSourcesTests
{
    private const string SourceFacts = """
        {"version":1,"cats":[
          {"installationId":"a","name":"猫a","sourceType":"local","enabled":true,"inputRevision":1,"updateState":"future-update","sites":[
            {"key":"video","name":"缓存名","api":"/cached","actions":["home","search"],"methods":{"home":"GET","search":"POST"}}]},
          {"installationId":"b","name":"猫b","sourceType":"local","enabled":true,"inputRevision":1,"updateState":"idle","sites":[]}],
         "videos":[
          {"key":{"installationId":"a","siteKey":"video"},"name":"缓存名","api":"/cached","actions":["home","search"],"methods":{"home":"GET","search":"POST"},
           "options":{"enabled":false,"homeVisible":true,"searchEnabled":false,"aggregateImages":false},"order":37,
           "detection":{"result":"future-result","checkedAt":100,"playbackResolved":false,"steps":[{"operation":"home","state":"success","error":null}]}},
          {"key":{"installationId":"b","siteKey":"video"},"name":"缓存b","api":"/cached","actions":[],"methods":{},
           "options":{"enabled":true,"homeVisible":false,"searchEnabled":true,"aggregateImages":false},"order":8}],
         "deleted":[{"installationId":"a","siteKey":"deleted"}]}
        """;

    private static JsonElement RawVideo(JsonElement root, string installation, string site) =>
        root.GetProperty("videos").EnumerateArray().Single(row =>
            row.GetProperty("key").GetProperty("installationId").GetString() == installation &&
            row.GetProperty("key").GetProperty("siteKey").GetString() == site);

    private static Runtime PersistenceRuntime()
    {
        var runtime = new Runtime(); runtime.Add("a", "video", "deleted"); runtime.Add("b");
        foreach (var node in runtime.Nodes.Values) { node.SourceType = "local"; node.SourceUrl = null; }
        return runtime;
    }

    private static async Task RawSourcePersistence()
    {
        var runtime = PersistenceRuntime();
        await using var store = new Store(runtime) { Bytes = Encoding.UTF8.GetBytes(SourceFacts) };
        using var before = JsonDocument.Parse(store.Bytes!);
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(runtime.Calls.Count == 0 && service.Videos.Single(video => video.Key == Key("a")).Order == 37 &&
                service.Cats.Single(cat => cat.InstallationId == "a").UpdateState == "future-update",
                "cold projection restores typed facts without authorizing a cached runtime generation");
            await service.SaveVideoOptionsAsync(Key("b"), new(true, true, true, false), order: 9);
            using (var unrelated = JsonDocument.Parse(store.Bytes!))
                Check(JsonElement.DeepEquals(RawVideo(unrelated.RootElement, "a", "video"),
                    RawVideo(before.RootElement, "a", "video")), "editing one source preserves every sibling field");
            await service.SaveVideoOptionsAsync(Key("a"), new(false, false, false, false), order: 38);
            await service.RefreshAsync();
            Check(service.Videos.Single(video => video.Key == Key("a")).Options == new FilmVideoOptions(false, false, false, false) &&
                service.Videos.All(video => video.Key.SiteKey != "deleted") && service.Cats.All(cat => cat.CanDetect),
                "metadata refresh preserves user options and deletion facts");
        }
        int calls = runtime.Calls.Count;
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(runtime.Calls.Count == calls && reopened.Videos.Single(video => video.Key == Key("a")).Order == 38 &&
            reopened.Videos.Single(video => video.Key == Key("b")).Order == 9 && reopened.Cats.All(cat => !cat.CanDetect),
            "a new owner restores facts but obtains runtime permission only from a live status");
        using var after = JsonDocument.Parse(store.Bytes!);
        Check(JsonElement.DeepEquals(RawVideo(after.RootElement, "a", "video").GetProperty("detection"),
            RawVideo(before.RootElement, "a", "video").GetProperty("detection")) &&
            !Encoding.UTF8.GetString(store.Bytes!).Contains("generation", StringComparison.Ordinal),
            "declared detection fields persist while process generations never become durable");
    }

    private static async Task AmbiguousSourceIdentityMembers()
    {
        var runtime = PersistenceRuntime();
        await using var store = new Store(runtime) { Bytes = Encoding.UTF8.GetBytes(SourceFacts) };
        var before = store.Bytes!;
        foreach (string sql in new[]
        {
            "INSERT INTO windows_film_sources_videos SELECT * FROM windows_film_sources_videos",
            "UPDATE windows_film_sources_videos SET scope_key='missing-parent'",
            "UPDATE windows_film_sources_videos SET ordinal=-1",
            "UPDATE windows_film_sources_videos_options SET enabled=2"
        })
        {
            try
            {
                await store.Inner.WriteAsync(db =>
                {
                    db.Execute("UPDATE windows_film_sources_videos SET name='must-roll-back'");
                    return db.Execute(sql);
                });
                throw new InvalidOperationException("Expected relational constraint failure");
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
            Check(store.Bytes!.SequenceEqual(before), "identity foreign-key order and type failures roll back preceding writes");
        }
    }

    private static async Task IsolatedSourceRecords()
    {
        var runtime = PersistenceRuntime();
        await using var store = new Store(runtime) { Bytes = Encoding.UTF8.GetBytes(SourceFacts) };
        var cache = store.Project(FilmStorageRecords.Sources, "device")!;
        cache["cats"] = new JsonArray();
        store.Seed(FilmStorageRecords.Sources, cache, "device");
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(service.Cats.Count == 2 && service.Cats.All(cat => !cat.CanDetect) && service.Videos.Count == 2,
                "missing display cache does not erase native source intent or user facts");
            await service.SaveVideoOptionsAsync(Key("b"), new(true, true, false, false), order: 12);
            await service.RefreshAsync();
            Check(service.Videos.Single(video => video.Key == Key("a")).Order == 37 &&
                service.Videos.All(video => video.Key.SiteKey != "deleted"), "cache rebuild preserves options and tombstones");
        }
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(reopened.Videos.Single(video => video.Key == Key("b")).Order == 12 &&
            !reopened.Videos.Single(video => video.Key == Key("b")).Options.SearchEnabled,
            "healthy facts reopen after rebuilding disposable metadata");
    }

    private static async Task UnknownUpdateState()
    {
        var runtime = new Runtime(); runtime.Add("a"); runtime.Nodes["a"].UpdateState = "future-state";
        await using var service = new FilmSourceService(runtime, new Store(runtime));
        await service.RefreshAsync();
        var cat = service.Cats.Single();
        Check(cat.UpdateState == "future-state" && cat.CanDetect && cat.Generation == 1 &&
            new FilmSettingsCatRow(cat).UpdateText == "future-state", "unknown display state cannot retire a valid source");
        var result = await service.DetectAsync(Key("a"));
        Check(result.Result == "valid" && runtime.Calls.Any(call => call.Method == "invoke" &&
            call.Input.GetProperty("generation").GetInt64() == 1), "execution uses the admitted runtime generation");
    }

    private static async Task SourceDocumentBoundaries()
    {
        foreach (string sql in new[]
        {
            "UPDATE windows_film_sources SET version=2",
            "UPDATE windows_film_sources SET shape_mask=shape_mask & ~4",
            "UPDATE windows_film_sources SET shape_mask=shape_mask & ~8"
        })
        {
            var runtime = PersistenceRuntime();
            await using var broken = new Store(runtime) { Bytes = Encoding.UTF8.GetBytes(SourceFacts) };
            await broken.Inner.WriteAsync(db => db.Execute(sql));
            var before = broken.Bytes!;
            await using var invalid = new FilmSourceService(runtime, broken);
            await Code(() => invalid.InitializeAsync(), "INVALID_DOCUMENT");
            await Code(() => invalid.RefreshAsync(), "INVALID_DOCUMENT");
            Check(broken.Writes == 0 && broken.Bytes!.SequenceEqual(before) && runtime.Calls.Count == 0,
                "invalid current roots are never reset or passed to Node");
        }
        var live = PersistenceRuntime();
        await using var store = new Store(live) { Bytes = Encoding.UTF8.GetBytes(SourceFacts) };
        await using var service = new FilmSourceService(live, store);
        await using var other = new FilmSourceService(live, store);
        await service.InitializeAsync(); await other.InitializeAsync();
        var previous = service.Videos.Single(video => video.Key == Key("b"));
        await other.SaveVideoOptionsAsync(Key("a"), new(false, true, false, true), order: 44);
        var external = store.Bytes!;
        int publications = 0; service.Changed += (_, _) => publications++;
        try { await service.SaveVideoOptionsAsync(Key("b"), new(false, false, false, false)); throw new InvalidOperationException("Expected CAS conflict"); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
        Check(publications == 0 && store.Bytes!.SequenceEqual(external) && service.Videos.Single(video => video.Key == Key("b")) == previous,
            "stale owners preserve the actual external commit and their last confirmed publication");
    }
}
