using System.Text;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static partial class FilmLibraryTests
{
    private static TestCase Test(string name, Func<Task> run) => new("film-library: " + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("official App storage writes favorites and restores them through a fresh owner", async () =>
        {
            string path = TestWorkspace.DataPath("film-library", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(path);
                await using (var store = new NativeDatabase(path))
                await using (var library = new FilmLibrary(store))
                    await library.SetFavoriteAsync(Card(), true);
                await using (var store = new NativeDatabase(path))
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
        yield return Test("typed favorite fields and exact source identities survive independent edits and cold reopen", TypedPersistence);
        yield return Test("corrupt favorite rows remain unchanged by unrelated writes and cannot be silently reset", CorruptRow);
        yield return Test("favorite identity foreign key and type constraints roll back the entire transaction", IdentityConstraints);
        yield return Test("independent favorite writers never overwrite each other with a stale collection", ConcurrentWriters);
        yield return Test("group favorites keep member identities without persisting source or search credentials", GroupPrivacy);
        yield return Test("failed transactions never publish favorites and corrupt roots are not silently reset", async () =>
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
            store.FailWrite = false;
            await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_film_library SET version=2"));
            byte[] damaged = store.Data!;
            await Error("CORRUPT_STORAGE", () => library.ReadAsync());
            await Error("CORRUPT_STORAGE", () => library.SetFavoriteAsync(Card(), false));
            Check(store.Data!.SequenceEqual(damaged),
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
        yield return Test("failed favorite refresh drains before abort releases owners", () =>
            MusicQualityConsumerTests.Sta(() => AbortFavoriteRefresh(false)));
        yield return Test("abort retries actual source release failure after favorite refresh settles", () =>
            MusicQualityConsumerTests.Sta(() => AbortFavoriteRefresh(true)));
    }

    private static async Task AbortFavoriteRefresh(bool failRelease)
    {
        var store = new RefreshStore();
        var library = new FilmLibrary(store);
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        var source = new ReleaseSource { Failures = failRelease ? 1 : 0 };
        var module = new FilmCapability(source, rig.Session, library);
        var failures = new List<string>();
        module.OperationFailed += (_, error) => failures.Add(error);
        try
        {
            await library.SetFavoriteAsync(Card(), true);
            await store.Entered.Task;
            var abort = module.AbortAsync();
            Check(!abort.IsCompleted && source.Attempts == 0,
                "abort waits for the actual registered favorite read");
            store.Finish.TrySetException(new DocumentStoreException(DocumentStoreError.Storage));
            if (failRelease)
            {
                try { await abort; throw new InvalidOperationException("Expected release failure"); }
                catch (InvalidOperationException error) when (error.Message == "SYNTHETIC_RELEASE_FAILED") { }
                Check(source.Attempts == 1, "real release failure remains visible");
                await module.AbortAsync();
                Check(source.Attempts == 2, "explicit retry performs the failed release again");
            }
            else
            {
                await abort;
                Check(source.Attempts == 1, "business read failure cannot skip resource release");
            }
            await module.DisposeAsync();
            Check(source.Attempts == (failRelease ? 2 : 1) && failures.SequenceEqual(["收藏读取失败"]),
                "business failure is reported once and successful release is retained");
            await Error("CLOSED", () => library.ReadAsync());
            Check(!rig.Session.HasSession, "playback owner is released before abort succeeds");
        }
        finally { store.Finish.TrySetResult(); await module.AbortAsync(); }
    }

    private sealed class RefreshStore : NativeTestDatabase
    {
        internal bool HoldRead;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default)
        {
            if (HoldRead) { Entered.TrySetResult(); await Finish.Task; }
            return await base.ReadAsync(read, token);
        }
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            var receipt = await base.WriteAsync(write, token, retainedBytes, commit);
            HoldRead = true;
            return receipt;
        }
    }

    private sealed class ReleaseSource : IFilmHomeSource
    {
        internal int Attempts, Failures;
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FilmCardItem>>([]);
        public void Dispose()
        {
            if (++Attempts <= Failures) throw new InvalidOperationException("SYNTHETIC_RELEASE_FAILED");
        }
    }
}
