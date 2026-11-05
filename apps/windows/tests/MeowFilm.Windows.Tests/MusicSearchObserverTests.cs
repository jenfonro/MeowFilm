using System.Collections.Concurrent;
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchObserverTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("observer repeated cancel does not publish another revision", RepeatedCancel);
        yield return Case("synchronous observer dispose drains physical work without joining itself", SynchronousDispose);
        yield return Case("concurrent cancel and abandoned waiter share one physical settlement", ConcurrentCancel);
        yield return Case("synchronous observer preparation retains the external notification barrier", SynchronousPrepare);
        yield return Case("observer can prepare and resume without reviving its old notice", ObserverResume);
        yield return Case("observer dispose can join externally started preparation and resume", ExternalPreparation);
        yield return Case("active observer child can synchronously dispose its owner", ChildDispose);
        yield return Case("escaped context from a throwing observer cannot bypass a later barrier", ExpiredFrame);
        yield return Case("cancel observer retires only its kind and dispose drains both kinds", DualKinds);
        yield return Case("cancelled preparation waiter does not forget physical work", AbandonedPreparation);
        yield return Case("another owner's observer cannot skip this owner's notification barrier", OtherOwner);
    }

    private static TestCase Case(string name, Func<Task> run) => new("Music search observer " + name, run);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Id(JsonElement value) => value.GetProperty("id").GetString()!;
    private static bool Loading(JsonElement value) => value.GetProperty("loading").GetBoolean();
    private static long Revision(JsonElement value) => value.GetProperty("revision").GetInt64();
    private static Task<JsonElement> Start(MusicSearchCoordinator search, string[]? platforms = null,
        string kind = "songs", string keyword = "observer") =>
        search.ExecuteAsync("music.search.start", new { keyword, kind, platforms = platforms ?? ["wy"], pages = 1, concurrency = 1, cacheMinutes = 60 });
    private static Task<JsonElement> Call(MusicSearchCoordinator search, string method, string id) =>
        search.ExecuteAsync("music.search." + method, new { id });
    private static JsonElement Empty(MusicSearchRequest request) => JsonSerializer.SerializeToElement(new
    {
        platform = request.Platform, page = request.Page, limit = request.Limit,
        items = Array.Empty<object>(), more = false, continuation = (object?)null
    });

    private static async Task RepeatedCancel()
    {
        using var catalog = new Catalog();
        await using var search = new MusicSearchCoordinator(catalog);
        var checkedCancellation = Signal<(long Before, long After)>();
        int calls = 0;
        Action<JsonElement>? observer = null;
        observer = value =>
        {
            Interlocked.Increment(ref calls);
            var cancelled = Call(search, "cancel", Id(value)).GetAwaiter().GetResult();
            if (!value.GetProperty("cancelled").GetBoolean()) return;
            // Stop after the first redundant revision, so the unfixed implementation
            // is reproduced exactly without leaving an unbounded notification loop.
            search.Changed -= observer;
            checkedCancellation.TrySetResult((Revision(value), Revision(cancelled)));
        };
        search.Changed += observer;
        await Start(search, []);
        var revisions = await checkedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(revisions.Before == revisions.After, "NO_OP_CANCEL_REPUBLISHED_A_REVISION");
        Check(calls == 2, "CANCEL_NOTIFICATION_WAS_NOT_BOUNDED");
    }

    private static async Task SynchronousDispose()
    {
        var physical = Signal<JsonElement>();
        var transportEntered = Signal<MusicSearchRequest>();
        var tokenCancelled = Signal();
        using var catalog = new Catalog((request, token) =>
        {
            token.Register(() => tokenCancelled.TrySetResult());
            transportEntered.TrySetResult(request);
            return physical.Task; // The physical operation deliberately ignores cancellation.
        });
        var search = new MusicSearchCoordinator(catalog);
        var issued = Signal<Task>();
        var returnedInside = Signal();
        var releaseObserver = Signal();
        int once = 0, laterObservers = 0;
        search.Changed += value =>
        {
            if (!Loading(value) || Interlocked.Exchange(ref once, 1) != 0) return;
            try
            {
                transportEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var closing = search.DisposeAsync().AsTask();
                issued.TrySetResult(closing);
                closing.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                returnedInside.TrySetResult();
                releaseObserver.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { returnedInside.TrySetException(error); }
        };
        search.Changed += _ => Interlocked.Increment(ref laterObservers);
        Task? external = null;
        try
        {
            await Start(search);
            var request = await transportEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var inside = await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await tokenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            external = search.DisposeAsync().AsTask();
            Check(!inside.IsCompleted && !external.IsCompleted, "CLOSE_COMPLETED_BEFORE_PHYSICAL_SETTLEMENT");
            physical.TrySetResult(Empty(request));
            await returnedInside.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(inside.IsCompletedSuccessfully && !external.IsCompleted, "EXTERNAL_CLOSE_DID_NOT_WAIT_FOR_OBSERVER");
            releaseObserver.TrySetResult();
            await external.WaitAsync(TimeSpan.FromSeconds(10));
            Check(laterObservers == 0 && catalog.DisposeCalls == 0, "RETIRED_NOTICE_OR_BORROWED_CATALOG_RELEASED");
        }
        finally
        {
            releaseObserver.TrySetResult();
            physical.TrySetResult(JsonSerializer.SerializeToElement(new { }));
            await search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (external is not null) await external.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ConcurrentCancel()
    {
        var held = Signal<JsonElement>(); var entered = Signal<MusicSearchRequest>(); var cancelled = Signal();
        using var catalog = new Catalog((r, t) =>
        { t.Register(() => cancelled.TrySetResult()); entered.TrySetResult(r); return held.Task; });
        await using var search = new MusicSearchCoordinator(catalog);
        var pendingNotice = Signal(); var settledNotice = Signal();
        var notices = new ConcurrentQueue<JsonElement>();
        search.Changed += value =>
        {
            if (!value.GetProperty("cancelled").GetBoolean()) return;
            notices.Enqueue(value);
            if (Loading(value)) pendingNotice.TrySetResult(); else settledNotice.TrySetResult();
        };
        using var waiter = new CancellationTokenSource();
        try
        {
            string id = Id(await Start(search)); var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var abandoned = search.ExecuteAsync("music.search.cancel", new { id }, waiter.Token);
            var first = Call(search, "cancel", id); var second = Call(search, "cancel", id);
            await pendingNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!cancelled.Task.IsCompleted, "QUERY_RETIREMENT_ABORTED_ADMITTED_READ");
            waiter.Cancel();
            Check(!(await abandoned).GetProperty("ok").GetBoolean(), "ABANDONED_CANCEL_WAS_REPORTED_AS_SUCCESS");
            long pendingRevision = Revision(await Call(search, "get", id));
            var third = Call(search, "cancel", id);
            Check(Revision(await Call(search, "get", id)) == pendingRevision &&
                !first.IsCompleted && !second.IsCompleted && !third.IsCompleted, "REPEATED_CANCEL_CHANGED_OR_FORGOT_PHYSICAL_WORK");
            held.TrySetResult(Empty(request));
            var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));
            await settledNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(results.All(r => !Loading(r) && Revision(r) == Revision(results[0])), "CANCEL_SETTLEMENT_DIVERGED");
            Check(Revision(await Call(search, "cancel", id)) == Revision(results[0]), "SETTLED_CANCEL_REPUBLISHED");
            await search.PrepareShutdownAsync();
            Check(notices.Count == 2 && notices.First().GetProperty("loading").GetBoolean() &&
                !Loading(notices.Last()), "CANCEL_NOTIFICATIONS_DID_NOT_MATCH_PHYSICAL_TRANSITIONS");
        }
        finally { held.TrySetResult(JsonSerializer.SerializeToElement(new { })); }
    }

    private static async Task SynchronousPrepare()
    {
        var held = Signal<JsonElement>(); var entered = Signal<MusicSearchRequest>(); var cancelled = Signal();
        using var catalog = new Catalog((r, t) =>
        { t.Register(() => cancelled.TrySetResult()); entered.TrySetResult(r); return held.Task; });
        var search = new MusicSearchCoordinator(catalog);
        var issued = Signal<Task>(); var insideDone = Signal(); var release = Signal();
        int once = 0;
        search.Changed += value =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            try
            {
                entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var task = search.PrepareShutdownAsync(); issued.TrySetResult(task);
                task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                insideDone.TrySetResult(); release.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { insideDone.TrySetException(error); }
        };
        try
        {
            string old = Id(await Start(search)); var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var inside = await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var external = search.PrepareShutdownAsync();
            Check(!inside.IsCompleted && !external.IsCompleted, "PREPARATION_SKIPPED_PHYSICAL_WORK");
            Check((await Start(search, [])).GetProperty("code").GetString() == "CLOSED", "PREPARATION_DID_NOT_CLOSE_ADMISSION");
            held.TrySetResult(Empty(request)); await insideDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!external.IsCompleted, "PREPARATION_SKIPPED_ACTIVE_OBSERVER");
            release.TrySetResult(); await external.WaitAsync(TimeSpan.FromSeconds(10));
            await search.CancelShutdownPreparationAsync();
            Check((await Call(search, "get", old)).GetProperty("code").GetString() == "SEARCH_NOT_FOUND", "RESUME_RETAINED_OLD_QUERY");
            Check((await Start(search, [])).GetProperty("ok").GetBoolean(), "RESUME_DID_NOT_REOPEN_ADMISSION");
        }
        finally
        {
            release.TrySetResult(); held.TrySetResult(JsonSerializer.SerializeToElement(new { }));
            await search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ObserverResume()
    {
        using var catalog = new Catalog();
        await using var search = new MusicSearchCoordinator(catalog);
        var resumed = Signal<(string Old, string Fresh)>(); var later = new ConcurrentQueue<string>();
        int once = 0;
        search.Changed += value =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            try
            {
                search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                search.CancelShutdownPreparationAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                string fresh = Id(Start(search, [], "playlists").GetAwaiter().GetResult());
                resumed.TrySetResult((Id(value), fresh));
            }
            catch (Exception error) { resumed.TrySetException(error); }
        };
        search.Changed += value => later.Enqueue(Id(value));
        await Start(search, []);
        var ids = await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(ids.Old != ids.Fresh && (await Call(search, "get", ids.Fresh)).GetProperty("ok").GetBoolean(), "RESUME_DID_NOT_CREATE_NEW_QUERY");
        await search.PrepareShutdownAsync();
        Check(later.All(id => id != ids.Old), "RETIRED_NOTICE_REACHED_LATER_OBSERVER");
    }

    private static async Task ExternalPreparation()
    {
        var held = Signal<JsonElement>(); var transport = Signal<MusicSearchRequest>();
        using var catalog = new Catalog((r, _) => { transport.TrySetResult(r); return held.Task; });
        var search = new MusicSearchCoordinator(catalog);
        var entered = Signal(); var go = Signal(); var issued = Signal<Task>();
        var insideDone = Signal(); var release = Signal();
        search.Changed += _ =>
        {
            entered.TrySetResult();
            try
            {
                go.Task.GetAwaiter().GetResult();
                var closing = search.DisposeAsync().AsTask(); issued.TrySetResult(closing);
                closing.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                insideDone.TrySetResult(); release.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { insideDone.TrySetException(error); }
        };
        try
        {
            await Start(search); var request = await transport.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var prepared = search.PrepareShutdownAsync();
            var resume = search.CancelShutdownPreparationAsync();
            go.TrySetResult(); var inside = await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var external = search.DisposeAsync().AsTask();
            Check(!inside.IsCompleted && !prepared.IsCompleted && !external.IsCompleted, "PREPARE_RESUME_DISPOSE_SKIPPED_TRANSPORT");
            held.TrySetResult(Empty(request)); await insideDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!prepared.IsCompleted && !external.IsCompleted, "EXTERNAL_LIFECYCLE_LOST_NOTIFICATION_BARRIER");
            bool closed = false; try { await resume; } catch (InvalidOperationException e) when (e.Message == "CLOSED") { closed = true; }
            Check(closed, "DISPOSAL_DID_NOT_FENCE_RESUME");
            release.TrySetResult(); await Task.WhenAll(prepared, external).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            go.TrySetResult(); release.TrySetResult(); held.TrySetResult(JsonSerializer.SerializeToElement(new { }));
            await search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ChildDispose()
    {
        using var catalog = new Catalog(); var search = new MusicSearchCoordinator(catalog);
        var done = Signal();
        search.Changed += _ =>
        {
            try
            {
                Task.Run(async () => await search.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
        };
        try { await Start(search, []); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task ExpiredFrame()
    {
        using var catalog = new Catalog(); var search = new MusicSearchCoordinator(catalog);
        var created = Signal(); var launch = Signal(); var issued = Signal<Task>();
        var secondEntered = Signal(); var coreDone = Signal(); var release = Signal();
        Task escaped = Task.CompletedTask; int phase = 0;
        search.Changed += _ =>
        {
            if (Volatile.Read(ref phase) == 0)
            {
                escaped = Task.Run(async () =>
                {
                    await launch.Task;
                    var task = search.DisposeAsync().AsTask(); issued.TrySetResult(task);
                    await task;
                });
                created.TrySetResult();
                throw new InvalidOperationException("SYNTHETIC_OBSERVER_FAILURE");
            }
            secondEntered.TrySetResult();
            try
            {
                issued.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                coreDone.TrySetResult(); release.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { coreDone.TrySetException(error); }
        };
        try
        {
            await Start(search, []); await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await search.PrepareShutdownAsync(); await search.CancelShutdownPreparationAsync();
            Volatile.Write(ref phase, 1); await Start(search, []);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); launch.TrySetResult();
            var external = await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await coreDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!external.IsCompleted, "EXPIRED_OBSERVER_CONTEXT_SKIPPED_CURRENT_OBSERVER");
            release.TrySetResult(); await escaped.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            launch.TrySetResult(); release.TrySetResult();
            await search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await escaped.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task DualKinds()
    {
        var songs = Signal<JsonElement>(); var lists = Signal<JsonElement>();
        var songRequest = Signal<MusicSearchRequest>(); var listRequest = Signal<MusicSearchRequest>();
        using var catalog = new Catalog((r, _) =>
        {
            if (r.Kind == MusicSearchKind.Songs) { songRequest.TrySetResult(r); return songs.Task; }
            listRequest.TrySetResult(r); return lists.Task;
        });
        var search = new MusicSearchCoordinator(catalog);
        var observerDone = Signal(); int once = 0;
        search.Changed += value =>
        {
            if (value.GetProperty("kind").GetString() != "songs" ||
                !value.GetProperty("cancelled").GetBoolean() || Interlocked.Exchange(ref once, 1) != 0) return;
            try
            {
                var result = Call(search, "cancel", Id(value)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check(!Loading(result), "OBSERVER_CANCEL_FINISHED_BEFORE_PHYSICAL_SETTLEMENT");
                observerDone.TrySetResult();
            }
            catch (Exception error) { observerDone.TrySetException(error); }
        };
        try
        {
            string songId = Id(await Start(search)); string listId = Id(await Start(search, kind: "playlists"));
            var sr = await songRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var lr = await listRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancel = Call(search, "cancel", songId);
            songs.TrySetResult(Empty(sr)); await cancel; await observerDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var other = await Call(search, "get", listId);
            Check(Loading(other) && !other.GetProperty("cancelled").GetBoolean(), "CANCELLING_SONGS_RETIRED_PLAYLISTS");
            var close = search.DisposeAsync().AsTask();
            Check(!close.IsCompleted, "DISPOSE_FORGOT_SECOND_KIND_PHYSICAL_WORK");
            lists.TrySetResult(Empty(lr)); await close.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            songs.TrySetResult(JsonSerializer.SerializeToElement(new { })); lists.TrySetResult(JsonSerializer.SerializeToElement(new { }));
            await search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task AbandonedPreparation()
    {
        var held = Signal<JsonElement>(); var entered = Signal<MusicSearchRequest>(); var cancelled = Signal();
        using var catalog = new Catalog((r, t) =>
        { t.Register(() => cancelled.TrySetResult()); entered.TrySetResult(r); return held.Task; });
        await using var search = new MusicSearchCoordinator(catalog); using var caller = new CancellationTokenSource();
        try
        {
            await Start(search); var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var preparation = search.PrepareShutdownAsync(caller.Token);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
            bool abandoned = false; try { await preparation; } catch (OperationCanceledException) { abandoned = true; }
            var retained = search.PrepareShutdownAsync();
            Check(abandoned && !retained.IsCompleted, "ABANDONED_PREPARATION_DROPPED_PHYSICAL_DRAIN");
            held.TrySetResult(Empty(request)); await retained.WaitAsync(TimeSpan.FromSeconds(10));
            await search.CancelShutdownPreparationAsync();
            Check((await Start(search, [])).GetProperty("ok").GetBoolean(), "REOPEN_AFTER_PHYSICAL_DRAIN_FAILED");
        }
        finally { held.TrySetResult(JsonSerializer.SerializeToElement(new { })); }
    }

    private static async Task OtherOwner()
    {
        using var firstCatalog = new Catalog(); using var secondCatalog = new Catalog();
        var first = new MusicSearchCoordinator(firstCatalog); var second = new MusicSearchCoordinator(secondCatalog);
        var secondEntered = Signal(); var issued = Signal<Task>(); var physicalDone = Signal(); var release = Signal(); var firstDone = Signal();
        second.Changed += _ =>
        {
            secondEntered.TrySetResult();
            try
            {
                issued.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                second.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                physicalDone.TrySetResult(); release.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { physicalDone.TrySetException(error); }
        };
        first.Changed += _ =>
        {
            try
            {
                var closingOtherOwner = second.DisposeAsync().AsTask(); issued.TrySetResult(closingOtherOwner);
                closingOtherOwner.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); firstDone.TrySetResult();
            }
            catch (Exception error) { firstDone.TrySetException(error); }
        };
        try
        {
            await Start(second, []); await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Start(first, []); var task = await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await physicalDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!task.IsCompleted, "OTHER_OWNER_OBSERVER_SKIPPED_NOTIFICATION_BARRIER");
            release.TrySetResult(); await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await second.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class Catalog(Func<MusicSearchRequest, CancellationToken, Task<JsonElement>>? handler = null) : IMusicPlatformCatalog
    {
        internal readonly ConcurrentQueue<MusicSearchRequest> Calls = new();
        internal int DisposeCalls;
        public Task<JsonElement> SearchAsync(MusicSearchRequest request, CancellationToken token = default)
        { Calls.Enqueue(request); return handler?.Invoke(request, token) ?? Task.FromResult(Empty(request)); }
        public JsonElement Describe() => JsonSerializer.SerializeToElement(new
        { platforms = new { wy = new { search = new[] { "songs", "playlists" } } } });
        public void Invalidate(string? platform = null) { }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
        private static Task<JsonElement> Unused() => throw new InvalidOperationException("NON_SEARCH_CALL");
        public Task<JsonElement> SongAsync(MusicSongRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> HomeAsync(MusicHomeRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> ChannelsAsync(MusicChannelsRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> ChannelAsync(MusicChannelRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> DailyAsync(MusicDailyRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> FmAsync(MusicFmRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> SimilarAsync(MusicSimilarRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> AlbumAsync(MusicAlbumRequest r, CancellationToken t = default) => Unused();
    }
}
