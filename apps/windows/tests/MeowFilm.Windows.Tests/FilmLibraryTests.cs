using System.Text;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmLibraryTests
{
    private static TestCase Test(string name, Func<Task> run) => new("film-library: " + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("App composition registers the sole favorite document namespace", () =>
        {
            Check(AppIdentity.NativeNamespaces.Contains(FilmLibrary.DocumentNamespace),
                "the official App document store must admit the native favorite owner");
            return Task.CompletedTask;
        });
        yield return Test("official App storage writes favorites and restores them through a fresh owner", async () =>
        {
            string path = TestWorkspace.DataPath("film-library", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(path);
                await using (var store = new LocalDocumentStore(path, AppIdentity.NativeNamespaces,
                    AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces))
                await using (var library = new FilmLibrary(store))
                    await library.SetFavoriteAsync(Card(), true);
                await using (var store = new LocalDocumentStore(path, AppIdentity.NativeNamespaces,
                    AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces))
                await using (var library = new FilmLibrary(store))
                    Check((await library.ReadAsync()).Single().PlaybackContext!.Target.Id == Card().Id,
                        "actual official namespace admission and durable restore");
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        });
        yield return Test("exact metadata identities survive a new owner and removal affects only its identity", async () =>
        {
            var store = new FilmMemoryStore();
            var first = Card();
            var second = first with { PlaybackContext = new(first.PlaybackContext!.Target with
            { Id = "douban:tv:100", MetadataProvider = "douban" }) };
            await using (var library = new FilmLibrary(store))
            {
                await library.SetFavoriteAsync(first, true); await library.SetFavoriteAsync(second, true);
                await library.SetFavoriteAsync(first with { Title = "更新标题" }, true);
                Check((await library.ReadAsync()).Count == 2, "an update never duplicates an exact identity");
            }
            await using var restored = new FilmLibrary(store);
            var items = await restored.ReadAsync();
            Check(items.Single(row => row.PlaybackContext!.Target.Id == first.Id).Title == "更新标题" &&
                items.Any(row => row.PlaybackContext!.Target.MetadataProvider == "douban"),
                "a new owner restores independent providers even when titles and numeric metadata ids match");
            await restored.SetFavoriteAsync(first, false);
            Check((await restored.ReadAsync()).Single().PlaybackContext!.Target.Id == "douban:tv:100",
                "removal uses target identity, not title or array position");
        });
        yield return Test("favorites retain typed source identity but never search cache pages or media credentials", async () =>
        {
            var store = new FilmMemoryStore();
            await using var library = new FilmLibrary(store);
            var target = new FilmPlaybackTarget("source:fixture:0", "source", "原始资料", Source: FilmJson.Encode(new
            { key = "source:fixture", detailId = 0, lineId = 0, playId = "private-play-reference", episodeId = "private-file-reference" }));
            var item = new FilmCardItem(target.Id, target.Title, "", "") { PlaybackContext = new(target,
                Query: "original-query", Results: FilmJson.Encode(new[] { "search-result" }),
                Search: FilmJson.Encode(new[] { new { page = 2 } }), Metadata: FilmJson.Encode(new { privateValue = "not-favorite-data" }),
                CacheEntries: FilmJson.Encode(new[] { new { fetchedAt = 100 } })) };
            await library.SetFavoriteAsync(item, true);
            var restored = (await library.ReadAsync()).Single().PlaybackContext!;
            Check(restored.Target.Source!.Value.GetProperty("detailId").GetInt32() == 0 &&
                restored.Target.Source.Value.GetProperty("lineId").GetInt32() == 0 &&
                restored.Target.Source.Value.EnumerateObject().Count() == 3 &&
                restored.Query is null && restored.Results is null && restored.Search is null &&
                restored.Metadata is null && restored.CacheEntries is null,
                "durable identity preserves numeric zero but not ephemeral search or play state");
            string bytes = Encoding.UTF8.GetString(store.Data!);
            Check(!bytes.Contains("private-play-reference", StringComparison.Ordinal) &&
                !bytes.Contains("private-file-reference", StringComparison.Ordinal) &&
                !bytes.Contains("original-query", StringComparison.Ordinal), "no short-lived private reference is serialized");
        });
        yield return Test("failed CAS never publishes a favorite and corrupt documents are not silently reset", async () =>
        {
            var store = new FilmMemoryStore();
            await using var library = new FilmLibrary(store);
            int publications = 0; library.Changed += (_, _) => publications++;
            await library.SetFavoriteAsync(Card(), true);
            byte[] original = store.Data!.ToArray();
            store.FailWrite = true;
            try { await library.SetFavoriteAsync(Card(number: 200), true); throw new InvalidOperationException("Expected write failure"); }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Storage) { }
            Check(publications == 1 && store.Data!.SequenceEqual(original) && (await library.ReadAsync()).Count == 1,
                "a failed durable write is not successful data or a Changed event");
            store.FailWrite = false; store.Data = Encoding.UTF8.GetBytes("{broken");
            int writes = store.Writes;
            await Error("CORRUPT_STORAGE", () => library.ReadAsync());
            await Error("CORRUPT_STORAGE", () => library.SetFavoriteAsync(Card(), false));
            Check(store.Writes == writes && Encoding.UTF8.GetString(store.Data!) == "{broken",
                "bad storage remains available for recovery rather than being replaced with an empty collection");
        });
        yield return Test("an admitted favorite settles after caller cancellation and close waits its real write receipt", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new FilmMemoryStore();
            bool cancellable = true;
            store.BeforeWrite = async token => { cancellable = token.CanBeCanceled; entered.TrySetResult(); await finish.Task; };
            var library = new FilmLibrary(store);
            using var caller = new CancellationTokenSource();
            try
            {
                var save = library.SetFavoriteAsync(Card(), true, caller.Token);
                await entered.Task; caller.Cancel();
                var close = library.DisposeAsync().AsTask();
                Check(!save.IsCompleted && !close.IsCompleted && !cancellable,
                    "accepted persistence is owned until physical completion, not by a retired view's token");
                finish.TrySetResult(); await Task.WhenAll(save, close);
                await using var restored = new FilmLibrary(store);
                Check((await restored.ReadAsync()).Count == 1, "the real receipt remains durable after owner retirement");
                await Error("CLOSED", () => library.ReadAsync());
            }
            finally { finish.TrySetResult(); await library.DisposeAsync(); }
        });
    }
}
