using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Capabilities;

public sealed record NovelSearchSourceState(NovelSource Source, string Status, int Page, bool Ended, string? ErrorCode = null);

public sealed class NovelSearch(NovelSourceService sources, NovelRuleProgram rules, NovelBook? matchingBook = null)
{
    private const int MaxConcurrency = 6;
    private const int MergeBatchThreshold = 4;
    private const int MergeDebounceMs = 100;

    private readonly DispatcherQueue? uiDispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly NovelWork work = new();
    private readonly object stateGate = new();
    private readonly List<NovelSearchSourceState> stateItems = [];
    private CancellationTokenSource? request;
    private CancellationToken activeToken;
    private long generation;
    private readonly Dictionary<string, NovelBook> raw = new(StringComparer.Ordinal);
    private Task? mergeTask;
    private TaskCompletionSource? mergeWakeup;
    private bool mergeDirty, mergingInFlight, active, initializing, shuttingDown;
    private int pendingMergeSources;

    public string Query { get; private set; } = "";
    public ObservableCollection<NovelBook> Books { get; } = [];
    public ObservableCollection<NovelSearchSourceState> States { get; } = [];
    public IReadOnlyList<JsonElement> Groups { get; private set; } = [];
    public string? Error { get; private set; }
    public bool Loading { get; private set; }
    public bool CanLoadMore => !Loading && States.Any(s => !s.Ended);
    private bool AllSourcesCancelled =>
        States.Count > 0 &&
        States.Any(s => s.Status == "已取消") &&
        States.All(s => s.Status is "已取消" or "失败" or "等待");
    public string Status => Loading ? "搜索中…" :
        Query.Length == 0 ? "" :
        Error is { Length: > 0 } ? Error :
        States.Count == 0 ? (sources.Sources.Any(s => s.Enabled && s.Actions.Contains("search")) ? "" : "无可用书源") :
        Books.Count == 0 ? States.All(s => s.Status == "失败") ? "搜索失败" :
        AllSourcesCancelled ? "搜索已取消" : "无搜索结果" : "";
    public event EventHandler? Changed;
    public Task CurrentTask { get; private set; } = Task.CompletedTask;

    private bool IsGeneration(long id)
    {
        lock (stateGate) return id == generation;
    }

    private void PostUi(Action action)
    {
        if (uiDispatcher is null || uiDispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        _ = uiDispatcher.TryEnqueue(() => action());
    }

    private Task RunOnUiAsync(Action action)
    {
        if (uiDispatcher is null || uiDispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!uiDispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception error) { tcs.TrySetException(error); }
        }))
        {
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

    private void NotifyChanged(long? expectedGeneration = null) => PostUi(() =>
    {
        if (expectedGeneration is { } expected && !IsGeneration(expected)) return;
        Changed?.Invoke(this, EventArgs.Empty);
    });

    public Task StartAsync(string query, CancellationToken token = default, bool restart = false)
    {
        query = query.Trim();
        if (query.Length > 0) token.ThrowIfCancellationRequested();
        bool hasStates, hasResumable;
        lock (stateGate)
        {
            active = query.Length > 0;
            activeToken = active ? token : default;
            hasStates = stateItems.Count > 0;
            hasResumable = stateItems.Any(s => s.Status is "已取消" or "等待");
        }
        if (!restart && Query == query && hasStates)
            return Loading ? CurrentTask : hasResumable ? LoadMoreAsync(token) : Task.CompletedTask;
        long resetGen = Reset(keepActive: query.Length > 0, nextToken: token);
        Query = query;
        if (query.Length == 0)
        {
            NotifyChanged(resetGen);
            return Task.CompletedTask;
        }
        return LoadMoreAsync(true, token, out _);
    }

    public void SourcesChanged()
    {
        var available = sources.Sources.Where(s => s.Enabled && s.Actions.Contains("search")).ToArray();
        bool shouldRestart, wasActive;
        CancellationToken restartToken;
        lock (stateGate)
        {
            if (shuttingDown || initializing || Query.Length == 0) return;
            if (stateItems.Count == 0)
            {
                if (available.Length == 0) return;
            }
            else if (available.Length == stateItems.Count && available.All(s => stateItems.Any(state =>
                state.Source.InstallationId == s.InstallationId && state.Source.Revision == s.Revision)))
            {
                return;
            }
            wasActive = active;
            restartToken = activeToken;
            shouldRestart = wasActive && !restartToken.IsCancellationRequested && available.Length > 0;
        }
        long resetGen = Reset(keepActive: wasActive && !restartToken.IsCancellationRequested, nextToken: restartToken);
        if (shouldRestart)
        {
            _ = RestartAfterSourcesChangedAsync(resetGen, restartToken);
        }
        else
        {
            NotifyChanged(resetGen);
        }
    }

    private async Task RestartAfterSourcesChangedAsync(long baseGeneration, CancellationToken token)
    {
        long observedGeneration = baseGeneration;
        try
        {
            if (!IsGeneration(baseGeneration)) return;
            var task = LoadMoreAsync(true, token, out observedGeneration);
            await task;
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) when (error.Message == "SHUTTING_DOWN") { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Search, "novel", "sources_changed.restart_error", new { query = Query }, error: error);
            bool notify;
            lock (stateGate)
            {
                notify = generation == observedGeneration && Error is null;
                if (notify) Error = "搜索失败";
            }
            if (notify) NotifyChanged(observedGeneration);
        }
    }

    private long Reset(bool keepActive = false, CancellationToken nextToken = default)
    {
        Cancel(notify: false, keepActive: keepActive, nextToken: nextToken);
        CurrentTask = Task.CompletedTask;
        long resetGen;
        lock (stateGate)
        {
            raw.Clear();
            Groups = [];
            Error = null;
            stateItems.Clear();
            resetGen = generation;
        }
        PostUi(() =>
        {
            if (!IsGeneration(resetGen)) return;
            Books.Clear();
            States.Clear();
        });
        return resetGen;
    }

    public Task LoadMoreAsync(CancellationToken token = default) => LoadMoreAsync(false, token, out _);

    private Task LoadMoreAsync(bool initialize, CancellationToken token, out long startedGeneration)
    {
        CancellationTokenSource? oldRequest;
        CancellationTokenSource nextRequest;
        long id;
        string query;
        lock (stateGate)
        {
            startedGeneration = generation;
            if (Loading) return CurrentTask;
            if (shuttingDown || Query.Length == 0 || (!initialize && stateItems.All(s => s.Ended)))
                return Task.CompletedTask;
            token.ThrowIfCancellationRequested();
            nextRequest = CancellationTokenSource.CreateLinkedTokenSource(token);
            oldRequest = request;
            request = nextRequest;
            id = ++generation;
            startedGeneration = id;
            query = Query;
            active = true;
            activeToken = token;
            Error = null;
            Loading = true;
            if (initialize) initializing = true;
        }
        oldRequest?.Dispose();
        try
        {
            NotifyChanged(id);
            return CurrentTask = work.Run(async ct =>
            {
                try
                {
                    if (initialize)
                    {
                        try
                        {
                            await sources.EnsureExecutionReadyAsync(ct);
                            ct.ThrowIfCancellationRequested();
                            NovelSearchSourceState[] initialStates;
                            lock (stateGate)
                            {
                                if (id != generation) return false;
                                stateItems.Clear();
                                foreach (var source in sources.Sources.Where(s => s.Enabled && s.Actions.Contains("search")))
                                    stateItems.Add(new(source, "等待", 0, false));
                                initialStates = stateItems.ToArray();
                                initializing = false;
                            }
                            await RunOnUiAsync(() =>
                            {
                                if (!IsGeneration(id)) return;
                                Books.Clear();
                                States.Clear();
                                foreach (var item in initialStates) States.Add(item);
                            });
                        }
                        finally
                        {
                            lock (stateGate)
                            {
                                if (id == generation) initializing = false;
                            }
                        }
                    }
                    NovelSearchSourceState[] pendingStates;
                    lock (stateGate) pendingStates = stateItems.Where(s => !s.Ended).ToArray();
                    DebugLog.Info(DebugCategories.Search, "novel", "wave.start", new
                    {
                        query,
                        sourceCount = pendingStates.Length,
                        maxConcurrency = MaxConcurrency,
                        matchingBook = matchingBook is not null
                    });
                    using var concurrency = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
                    await Task.WhenAll(pendingStates.Select(state => SearchOne(state, concurrency)));
                    if (IsGeneration(id) && !ct.IsCancellationRequested)
                    {
                        Task? runningMerge;
                        TaskCompletionSource? wakeupToSignal;
                        lock (stateGate)
                        {
                            wakeupToSignal = mergeWakeup;
                            mergeWakeup = null;
                            runningMerge = mergeTask;
                        }
                        wakeupToSignal?.TrySetResult();
                        if (runningMerge is not null) await runningMerge;
                        bool stillDirty;
                        lock (stateGate) stillDirty = mergeDirty;
                        if (stillDirty && IsGeneration(id) && !ct.IsCancellationRequested)
                        {
                            try
                            {
                                await FlushMergeAsync(id, ct);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            {
                                DebugLog.Warn(DebugCategories.Search, "novel", "merge.error", new { query = Query }, error: error);
                                RecordMergeError(id, error);
                            }
                        }
                        int rawCount, failedCount, exhaustedCount;
                        lock (stateGate)
                        {
                            rawCount = raw.Count;
                            failedCount = stateItems.Count(s => s.Status == "失败");
                            exhaustedCount = stateItems.Count(s => s.Ended);
                        }
                        int booksCount = 0;
                        await RunOnUiAsync(() => booksCount = Books.Count);
                        DebugLog.Info(DebugCategories.Search, "novel", "wave.end", new
                        {
                            query,
                            rawCount,
                            booksCount,
                            failedCount,
                            exhaustedCount
                        });
                    }
                    return true;

                    async Task SearchOne(NovelSearchSourceState state, SemaphoreSlim pool)
                    {
                        bool acquired = false;
                        try
                        {
                            await pool.WaitAsync(ct);
                            acquired = true;
                            if (!sources.IsCurrent(state.Source))
                            {
                                MarkCancelled(state.Source.InstallationId);
                                return;
                            }
                            int startedIndex = -1;
                            NovelSearchSourceState? startedState = null;
                            lock (stateGate)
                            {
                                if (id == generation && TryUpdateStateLocked(state.Source.InstallationId,
                                    s => s with { Status = "搜索中", ErrorCode = null }, out startedIndex, out var updated))
                                    startedState = updated;
                            }
                            if (startedState is null) return;
                            ApplyStateUpdate(id, startedIndex, startedState, notifyChanged: true);
                            var value = await sources.InvokeAsync(state.Source, "search", new { keyword = query, page = state.Page + 1 }, ct);
                            var books = NovelBook.ReadList(value.GetProperty("books"), state.Source, "INVALID_SEARCH_BOOKS");
                            ct.ThrowIfCancellationRequested();
                            if (!sources.IsCurrent(state.Source))
                            {
                                MarkCancelled(state.Source.InstallationId);
                                return;
                            }
                            bool added = false;
                            bool shouldSchedule = false;
                            bool immediate = false;
                            int doneIndex = -1;
                            NovelSearchSourceState? doneState = null;
                            lock (stateGate)
                            {
                                if (id != generation) return;
                                foreach (var book in books)
                                {
                                    if (raw.TryAdd(book.SourceIdentity, book)) added = true;
                                }
                                if (TryUpdateStateLocked(state.Source.InstallationId, s => s with
                                {
                                    Status = books.Length == 0 ? "无结果" : "完成",
                                    Page = state.Page + 1,
                                    Ended = !added,
                                    ErrorCode = null
                                }, out doneIndex, out var updated))
                                    doneState = updated;
                                if (added)
                                {
                                    mergeDirty = true;
                                    pendingMergeSources++;
                                    shouldSchedule = true;
                                    immediate = pendingMergeSources >= MergeBatchThreshold;
                                }
                            }
                            if (doneState is not null) ApplyStateUpdate(id, doneIndex, doneState);
                            DebugLog.Debug(DebugCategories.Search, "novel", "source.done", new
                            {
                                sourceId = state.Source.InstallationId,
                                page = state.Page + 1,
                                incomingCount = books.Length,
                                added,
                                exhausted = !added
                            });
                            if (shouldSchedule) ScheduleMerge(id, immediate, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            MarkCancelled(state.Source.InstallationId);
                        }
                        catch (BridgeException error) when (error.Code is "STALE_SOURCE" or "SOURCE_DISABLED" || !sources.IsCurrent(state.Source))
                        {
                            MarkCancelled(state.Source.InstallationId);
                        }
                        catch (Exception error)
                        {
                            if (!sources.IsCurrent(state.Source))
                            {
                                MarkCancelled(state.Source.InstallationId);
                                return;
                            }
                            string code = error is BridgeException bridge ? bridge.Code
                                : error is JsonException or InvalidDataException ? "INVALID_RESPONSE"
                                : error.GetType().Name;
                            DebugLog.Warn(DebugCategories.Search, "novel", "source.error", new
                            {
                                sourceId = state.Source.InstallationId,
                                page = state.Page + 1,
                                code
                            }, error: error);
                            int failedIndex = -1;
                            NovelSearchSourceState? failedState = null;
                            lock (stateGate)
                            {
                                if (id == generation && TryUpdateStateLocked(state.Source.InstallationId,
                                    s => s with { Status = "失败", ErrorCode = code }, out failedIndex, out var updated))
                                    failedState = updated;
                            }
                            if (failedState is not null) ApplyStateUpdate(id, failedIndex, failedState);
                        }
                        finally
                        {
                            if (acquired) pool.Release();
                            if (IsGeneration(id)) NotifyChanged(id);
                        }

                        void MarkCancelled(string installationId)
                        {
                            int cancelledIndex = -1;
                            NovelSearchSourceState? cancelledState = null;
                            lock (stateGate)
                            {
                                if (id == generation && TryUpdateStateLocked(installationId,
                                    s => s with { Status = "已取消", ErrorCode = null }, out cancelledIndex, out var updated))
                                    cancelledState = updated;
                            }
                            if (cancelledState is not null) ApplyStateUpdate(id, cancelledIndex, cancelledState);
                        }
                    }
                }
                finally
                {
                    bool notify;
                    lock (stateGate)
                    {
                        notify = id == generation;
                        if (notify) { Loading = false; initializing = false; }
                    }
                    if (notify) NotifyChanged(id);
                }
            }, nextRequest.Token);
        }
        catch
        {
            bool notify;
            lock (stateGate)
            {
                notify = id == generation;
                if (notify) { Loading = false; initializing = false; }
            }
            if (notify) NotifyChanged(id);
            throw;
        }
    }

    private bool TryUpdateStateLocked(
        string installationId,
        Func<NovelSearchSourceState, NovelSearchSourceState> transform,
        out int index,
        out NovelSearchSourceState updated)
    {
        for (int i = 0; i < stateItems.Count; i++)
        {
            if (stateItems[i].Source.InstallationId == installationId)
            {
                updated = transform(stateItems[i]);
                stateItems[i] = updated;
                index = i;
                return true;
            }
        }
        index = -1;
        updated = default!;
        return false;
    }

    private void ApplyStateUpdate(long id, int index, NovelSearchSourceState next, bool notifyChanged = false) => PostUi(() =>
    {
        if (!IsGeneration(id)) return;
        if (index >= 0 && index < States.Count && States[index].Source.InstallationId == next.Source.InstallationId)
        {
            if (States[index] != next) States[index] = next;
        }
        else
        {
            for (int i = 0; i < States.Count; i++)
            {
                if (States[i].Source.InstallationId == next.Source.InstallationId)
                {
                    if (States[i] != next) States[i] = next;
                    break;
                }
            }
        }
        if (notifyChanged) Changed?.Invoke(this, EventArgs.Empty);
    });

    private void ScheduleMerge(long id, bool immediate, CancellationToken ct)
    {
        TaskCompletionSource? wakeupToSignal = null;
        lock (stateGate)
        {
            if (id != generation || ct.IsCancellationRequested) return;
            if (mergeTask is { IsCompleted: false })
            {
                if (immediate && !mergingInFlight)
                {
                    wakeupToSignal = mergeWakeup;
                    mergeWakeup = null;
                }
                else
                {
                    return;
                }
            }
            else
            {
                var wakeup = immediate ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mergeWakeup = wakeup;
                mergeTask = RunCoalescedMergeAsync(id, wakeup, ct);
            }
        }
        wakeupToSignal?.TrySetResult();
    }

    private async Task RunCoalescedMergeAsync(long id, TaskCompletionSource? wakeup, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            if (wakeup is not null)
            {
                using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(MergeDebounceMs, timerCts.Token);
                var winner = await Task.WhenAny(delay, wakeup.Task);
                if (!ReferenceEquals(winner, delay))
                {
                    timerCts.Cancel();
                    try { await delay; } catch (OperationCanceledException) { }
                }
                lock (stateGate)
                {
                    if (ReferenceEquals(mergeWakeup, wakeup)) mergeWakeup = null;
                }
            }
            if (!IsGeneration(id) || ct.IsCancellationRequested) return;
            await FlushMergeAsync(id, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Search, "novel", "merge.error", new { query = Query }, error: error);
            RecordMergeError(id, error);
        }
        finally
        {
            bool reschedule = false;
            long rescheduleGen = 0;
            CancellationToken rescheduleCt = default;
            lock (stateGate)
            {
                mergeTask = null;
                if (mergeDirty && !shuttingDown && Error is null &&
                    request is { IsCancellationRequested: false } activeReq)
                {
                    reschedule = true;
                    rescheduleGen = generation;
                    rescheduleCt = activeReq.Token;
                }
            }
            if (reschedule) ScheduleMerge(rescheduleGen, immediate: false, rescheduleCt);
        }
    }

    private void RecordMergeError(long id, Exception error)
    {
        string message = error switch
        {
            BridgeException { Code: "TIMEOUT" } => "搜索结果合并超时",
            BridgeException bridge => $"搜索结果合并失败（{bridge.Code}）",
            _ => "搜索结果合并失败"
        };
        bool updated;
        lock (stateGate)
        {
            updated = id == generation;
            if (updated) Error = message;
        }
        if (updated) NotifyChanged(id);
    }

    private async Task FlushMergeAsync(long id, CancellationToken ct)
    {
        lock (stateGate)
        {
            if (mergingInFlight) return;
            mergingInFlight = true;
        }
        try
        {
            while (true)
            {
                NovelBook[] snapshot;
                lock (stateGate)
                {
                    if (!mergeDirty || id != generation || ct.IsCancellationRequested)
                    {
                        mergingInFlight = false;
                        return;
                    }
                    mergeDirty = false;
                    pendingMergeSources = 0;
                    snapshot = raw.Values.ToArray();
                }
                var merged = await rules.InvokeAsync(new { operation = "merge", books = snapshot.Select(b => b.Json()).ToArray() }, ct);
                ct.ThrowIfCancellationRequested();
                if (!IsGeneration(id)) return;
                var rawGroups = merged.GetProperty("books").EnumerateArray().Select(b => b.Clone()).ToArray();
                JsonElement[] effectiveGroups = rawGroups;
                if (matchingBook is not null && rawGroups.Length > 0)
                {
                    var filtered = await rules.SourceCandidatesAsync(snapshot, matchingBook, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!IsGeneration(id)) return;
                    var matchedKeys = filtered.Candidates
                        .Where(c => c.SameBook)
                        .Select(c => c.InstallationId + "\n" + c.BookUrl)
                        .ToHashSet(StringComparer.Ordinal);
                    effectiveGroups = FilterGroups(rawGroups, matchedKeys);
                }
                var sourceById = sources.Sources.ToDictionary(s => s.InstallationId, StringComparer.Ordinal);
                var desired = new List<NovelBook>(effectiveGroups.Length);
                foreach (var group in effectiveGroups)
                {
                    if (sourceById.TryGetValue(NovelJson.Text(group, "installationId"), out var source))
                        desired.Add(NovelBook.From(group, source));
                }
                bool applied = false;
                await RunOnUiAsync(() =>
                {
                    lock (stateGate)
                    {
                        if (id != generation || ct.IsCancellationRequested) return;
                        Groups = effectiveGroups;
                        Error = null;
                    }
                    ReconcileBooks(desired);
                    applied = true;
                    DebugLog.Debug(DebugCategories.Search, "novel", "merge.flush", new
                    {
                        rawCount = snapshot.Length,
                        mergedCount = desired.Count,
                        matchingBook = matchingBook is not null
                    });
                    Changed?.Invoke(this, EventArgs.Empty);
                });
                if (!applied) return;
            }
        }
        finally
        {
            lock (stateGate) mergingInFlight = false;
        }
    }

    private static JsonElement[] FilterGroups(JsonElement[] groups, HashSet<string> matchedKeys)
    {
        var result = new List<JsonElement>(groups.Length);
        foreach (var group in groups)
        {
            if (!group.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) continue;
            var kept = new List<JsonElement>();
            foreach (var candidate in candidates.EnumerateArray())
            {
                string key = NovelJson.Text(candidate, "installationId") + "\n" + NovelJson.Text(candidate, "bookUrl");
                if (matchedKeys.Contains(key)) kept.Add(candidate.Clone());
            }
            if (kept.Count == 0) continue;
            var obj = System.Text.Json.Nodes.JsonNode.Parse(kept[0].GetRawText())!.AsObject();
            obj["groupKey"] = NovelJson.Text(group, "groupKey");
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in kept) arr.Add(System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText()));
            obj["candidates"] = arr;
            result.Add(JsonSerializer.SerializeToElement(obj, NovelJson.Options));
        }
        return result.ToArray();
    }

    private void ReconcileBooks(List<NovelBook> desired)
    {
        var desiredKeys = desired.Select(b => b.SourceIdentity).ToHashSet(StringComparer.Ordinal);
        for (int i = Books.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(Books[i].SourceIdentity)) Books.RemoveAt(i);
        }
        for (int i = 0; i < desired.Count; i++)
        {
            var book = desired[i];
            int existing = i;
            while (existing < Books.Count && Books[existing].SourceIdentity != book.SourceIdentity) existing++;
            if (existing == Books.Count)
            {
                Books.Insert(i, book);
            }
            else
            {
                if (existing != i) Books.Move(existing, i);
                if (!Books[i].ContentEquals(book)) Books[i] = book;
            }
        }
    }

    public void Cancel() => Cancel(notify: true, keepActive: false, nextToken: default);

    private void Cancel(bool notify, bool keepActive, CancellationToken nextToken)
    {
        TaskCompletionSource? wakeupToSignal;
        CancellationTokenSource? requestToCancel;
        List<(int Index, NovelSearchSourceState State)>? cancelledStates = null;
        long cancelGen;
        lock (stateGate)
        {
            cancelGen = ++generation;
            Loading = false;
            initializing = false;
            active = keepActive;
            activeToken = keepActive ? nextToken : default;
            mergeDirty = false;
            pendingMergeSources = 0;
            wakeupToSignal = mergeWakeup;
            mergeWakeup = null;
            requestToCancel = request;
            if (notify)
            {
                for (int i = 0; i < stateItems.Count; i++)
                {
                    if (stateItems[i].Status is "搜索中" or "等待")
                    {
                        stateItems[i] = stateItems[i] with { Status = "已取消", ErrorCode = null };
                        (cancelledStates ??= []).Add((i, stateItems[i]));
                    }
                }
            }
        }
        if (requestToCancel is not null)
        {
            try { requestToCancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        wakeupToSignal?.TrySetResult();
        if (cancelledStates is not null || notify)
        {
            PostUi(() =>
            {
                if (!IsGeneration(cancelGen)) return;
                if (cancelledStates is not null)
                {
                    foreach (var (index, state) in cancelledStates)
                    {
                        if (index >= 0 && index < States.Count && States[index].Source.InstallationId == state.Source.InstallationId)
                        {
                            if (States[index] != state) States[index] = state;
                        }
                        else
                        {
                            for (int i = 0; i < States.Count; i++)
                            {
                                if (States[i].Source.InstallationId == state.Source.InstallationId)
                                {
                                    if (States[i] != state) States[i] = state;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (notify) Changed?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public async Task PrepareShutdownAsync()
    {
        lock (stateGate) shuttingDown = true;
        Cancel();
        await work.PauseAsync();
        Task? runningMerge;
        CancellationTokenSource? requestToDispose;
        lock (stateGate)
        {
            runningMerge = mergeTask;
            mergeTask = null;
            requestToDispose = request;
            request = null;
        }
        if (runningMerge is not null)
        {
            try { await runningMerge; } catch (OperationCanceledException) { }
        }
        requestToDispose?.Dispose();
    }
}
