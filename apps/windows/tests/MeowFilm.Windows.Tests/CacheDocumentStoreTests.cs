using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class CacheDocumentStoreTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Current typed cache invariant"); }
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("cache.sqlite.image-write-admission-bounds-queued-bytes-and-count", async () =>
        {
            foreach (int length in new[] { 4 * 1024 * 1024, 1 })
            {
                await using var database = new NativeTestDatabase();
                await using var cache = new MusicPresentationCache(database);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                database.BeforeWrite = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
                byte[] bytes = new byte[length];
                var first = cache.WriteAsync("first", bytes, cache.Epoch, default);
                var waiting = new List<Task>();
                using var cancel = new CancellationTokenSource();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    int capacity = length == 1 ? 128 : 4;
                    for (int i = 1; i < capacity; i++)
                        waiting.Add(cache.WriteAsync("waiting-" + i, bytes, cache.Epoch, cancel.Token));
                    var overflow = cache.WriteAsync("overflow", bytes, cache.Epoch, default);
                    Check(overflow.IsCompleted);
                    try { await overflow; throw new InvalidOperationException("Expected bounded image admission"); }
                    catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Limit); }
                    cancel.Cancel();
                    foreach (var work in waiting)
                    {
                        try { await work; throw new InvalidOperationException("Expected queued image cancellation"); }
                        catch (OperationCanceledException) { }
                    }
                    var replacement = cache.WriteAsync("replacement", bytes, cache.Epoch, default);
                    Check(!replacement.IsCompleted);
                    release.TrySetResult();
                    await Task.WhenAll(first, replacement).WaitAsync(TimeSpan.FromSeconds(10));
                    Check((await database.ReadPresentationEntriesAsync()).Count == 2);
                    Check(await cache.ReadAsync("overflow", default) is null);
                    await cache.ClearImagesAsync(default);
                    Check((await database.ReadPresentationEntriesAsync()).Count == 0);
                }
                finally
                {
                    cancel.Cancel(); release.TrySetResult();
                    await first;
                    foreach (var work in waiting)
                        try { await work; } catch (OperationCanceledException) { }
                }
            }
        });
        yield return new("cache.sqlite.actual-read-failure-is-a-miss-without-durable-data-loss", async () =>
        {
            await using var database = new NativeTestDatabase();
            await database.Inner.WriteAsync(db => db.CommitRevision("business-test", StorageRevision.Empty, "unchanged"));
            database.BeforeRead = _ => throw new DocumentStoreException(DocumentStoreError.Storage);
            var cache = new MusicSongMatchStore(database);
            Check((await cache.ReadAsync(default)).Count == 0);
            database.BeforeRead = null;
            Check(await database.Inner.RevisionAsync("business-test") == new StorageRevision(1, "unchanged"));
        });
        yield return new("cache.sqlite.no-opaque-document-column-and-same-database-root", async () =>
        {
            await using var database = new NativeTestDatabase();
            var first = new MusicSongMatchStore(database); var second = new MusicSongMatchStore(database);
            await first.WriteAsync([], default); Check((await second.ReadAsync(default)).Count == 0);
            await database.Inner.ReadAsync(db =>
            {
                Check(db.Scalar<long>("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('documents','areas','data')") == 0);
                return true;
            });
        });
        yield return new("cache.sqlite.failed-write-does-not-invent-a-row-or-replay", async () =>
        {
            await using var database = new NativeTestDatabase();
            await database.SetReadOnlyAsync(true);
            var cache = new MusicSongMatchStore(database);
            await cache.WriteAsync([], default);
            await database.SetReadOnlyAsync(false);
            Check((await new MusicSongMatchStore(database).ReadAsync(default)).Count == 0);
            await cache.WriteAsync([], default);
            Check((await database.Inner.RevisionAsync(MusicSongMatchStore.Namespace)).Revision == 1);
        });
    }
}
