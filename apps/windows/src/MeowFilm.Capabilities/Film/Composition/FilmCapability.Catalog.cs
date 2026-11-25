using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class FilmCapability
{
    internal async Task RetrySectionAsync(FilmHomeSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (stopping || disposed || !IsHome || !Sections.Contains(section) ||
            requests.ContainsKey(section) || !section.CanRetry)
            return;
        await StartSectionLoad(section, navigationGeneration, lifetime.Token, refresh: true);
    }

    internal Task RefreshSectionAsync(FilmHomeSection section) =>
        !stopping && !disposed && IsHome && Sections.Contains(section) && !requests.ContainsKey(section)
            ? StartSectionLoad(section, navigationGeneration, lifetime.Token, refresh: true)
            : Task.CompletedTask;

    internal Task RefreshDataAsync() =>
        !stopping && !disposed && IsData && dataRequest is null
            ? StartDataLoad(ActiveData, navigationGeneration, DataPageSize, lifetime.Token, replace: true, refresh: true)
            : Task.CompletedTask;

    internal async Task RetryDataAsync()
    {
        var section = ActiveData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanRetry)
            return;
        await StartDataLoad(section, navigationGeneration, NextDataLimit(section), lifetime.Token,
            replace: section.ReloadPending, refresh: true);
    }

    internal async Task LoadMoreDataAsync()
    {
        var section = ActiveData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanPage)
            return;
        await StartDataLoad(section, navigationGeneration, NextDataLimit(section), lifetime.Token,
            replace: section.ReloadPending,
            refresh: section.State is FilmSectionState.Failed or FilmSectionState.Cancelled);
    }

    internal async Task SelectDataFilterAsync(FilmFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var section = ActiveData;
        if (stopping || disposed || !IsData) return;
        if (!section.Contains(option))
            throw new ArgumentException("Unknown film filter option.", nameof(option));
        if (option.IsSelected) return;

        long generation = ++navigationGeneration;
        RetireDataRequest();
        if (!section.SelectFilter(option)) return;
        await StartDataLoad(section, generation, DataPageSize, lifetime.Token);
    }

    private Task StartSectionLoad(
        FilmHomeSection section,
        long generation,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        requests.Add(section, request);
        section.Begin();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        operations.Add(operation);
        _ = RunAsync();
        return operation;

        async Task RunAsync()
        {
            try
            {
                await Task.Yield();
                request.Token.ThrowIfCancellationRequested();
                var items = await source.LoadAsync(section.Definition with { Refresh = refresh }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (CanPublish(section, request, generation))
                {
                    section.Complete(items);
                    NotifyCacheError(section.Cache);
                }
            }
            catch (OperationCanceledException)
            {
                if (CanPublish(section, request, generation)) section.Cancel();
            }
            catch (Exception error)
            {
                if (CanPublish(section, request, generation))
                {
                    section.Fail();
                    OperationFailed?.Invoke(this, error is FilmHomeException home
                        ? FilmPlaybackError.Message(home.Code, "栏目加载失败")
                        : "栏目加载失败");
                }
            }
            finally
            {
                if (requests.TryGetValue(section, out var current) && ReferenceEquals(current, request))
                    requests.Remove(section);
                operations.Remove(operation);
                request.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private bool CanPublish(
        FilmHomeSection section,
        CancellationTokenSource request,
        long generation) =>
        !stopping && !disposed && IsHome && generation == navigationGeneration &&
        requests.TryGetValue(section, out var current) && ReferenceEquals(current, request);

    private Task StartDataLoad(
        FilmDataSection section,
        long generation,
        int limit,
        CancellationToken cancellationToken,
        bool replace = false,
        bool refresh = false)
    {
        if (limit < 1 || limit > DataPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        int start = replace ? 0 : section.NextStart;
        var definition = section.Definition with { Refresh = refresh };
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        dataRequest = request;
        dataRequestSection = section;
        section.Begin(replace);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        operations.Add(operation);
        _ = RunAsync();
        return operation;

        async Task RunAsync()
        {
            try
            {
                await Task.Yield();
                request.Token.ThrowIfCancellationRequested();
                if (replace)
                {
                    var page = await source.LoadAsync(definition, 0, DataPageSize, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    var cache = (page as FilmHomePage)?.Cache;
                    if (CanPublishData(section, request, generation))
                    {
                        section.Replace(page, page.Count, page.Count >= DataPageSize, cache);
                        NotifyCacheError(cache);
                    }
                }
                else
                {
                    var items = await source.LoadAsync(definition, start, limit, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    if (CanPublishData(section, request, generation))
                    {
                        section.Complete(items, start, limit);
                        NotifyCacheError(section.Cache);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (CanPublishData(section, request, generation)) section.Cancel();
            }
            catch (Exception error)
            {
                if (CanPublishData(section, request, generation))
                {
                    section.Fail();
                    OperationFailed?.Invoke(this, error is FilmHomeException home
                        ? FilmPlaybackError.Message(home.Code, "影片数据加载失败")
                        : "影片数据加载失败");
                }
            }
            finally
            {
                if (ReferenceEquals(dataRequest, request))
                {
                    dataRequest = null;
                    dataRequestSection = null;
                }
                operations.Remove(operation);
                request.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private void NotifyCacheError(FilmMetadataCache? cache)
    {
        if (cache?.ErrorCode is { Length: > 0 } code)
            OperationFailed?.Invoke(this, FilmPlaybackError.Message(code, "影片数据刷新失败"));
    }

    private bool CanPublishData(
        FilmDataSection section,
        CancellationTokenSource request,
        long generation) =>
        !stopping && !disposed && IsData && ReferenceEquals(ActiveData, section) &&
        generation == navigationGeneration && ReferenceEquals(dataRequest, request) &&
        ReferenceEquals(dataRequestSection, section);

    private static int NextDataLimit(FilmDataSection section) =>
        section.NextStart < DataPageSize ? DataPageSize - section.NextStart : DataPageSize;

    private void RetireRequests()
    {
        var retired = requests.Values.ToArray();
        foreach (var section in requests.Keys) section.Cancel();
        requests.Clear();
        foreach (var request in retired)
        {
            try { request.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "requests", "section.cancel.failed", error: error);
            }
        }
        RetireDataRequest();
    }

    private void RetireDataRequest()
    {
        var retiredData = dataRequest;
        if (retiredData is not null) dataRequestSection?.Cancel();
        dataRequest = null;
        dataRequestSection = null;
        if (retiredData is not null)
        {
            try { retiredData.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "requests", "data.cancel.failed", error: error);
            }
        }
    }
}
