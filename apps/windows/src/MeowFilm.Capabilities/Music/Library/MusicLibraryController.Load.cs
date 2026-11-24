using Microsoft.UI.Dispatching;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicLibraryController
{
    internal async Task LoadUiAsync(MusicLocation target)
    {
        if (stopping) return;
        try { await LoadAsync(target, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.MusicLibrary,
                "navigation", "load_ui_failed", new { route = target.Route, error = error.Message });
        }
    }

    internal Task LoadAsync(MusicLocation target, CancellationToken cancellationToken, int retry = 0,
        bool publishStatus = true)
    {
        if (stopping) return Task.CompletedTask;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        return Tracked();
        async Task Tracked()
        {
            try { await LoadCoreAsync(target, cancellationToken, retry, publishStatus); }
            finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); }
        }
    }

    private async Task LoadCoreAsync(MusicLocation target, CancellationToken cancellationToken, int retry,
        bool publishStatus)
    {
        var generation = ctx.AdvanceLoadGeneration();
        var stamp = publishStatus ? ctx.NextStatusEpoch() : ctx.StatusEpoch;
        CancelCollectionPage();
        ctx.LoadCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        ctx.LoadCancellation = request;
        var token = request.Token;
        try
        {
            if (publishStatus) ctx.ClearStatusQuiet();
            ctx.RefreshBindings();
            var rows = Array.Empty<SongRow>();
            var dates = Array.Empty<string>();
            var archives = Array.Empty<ArchivedPlaylist>();
            PlaylistRow? detail = null;
            string title;
            var count = 0;
            switch (target.Route)
            {
                case "search":
                {
                    title = "曲库搜索";
                    var reply = await application.SearchLibraryAsync(target.Query, target.Offset, 100, token);
                    rows = reply.Items.Select(item => SongRow.FromRecord(item)).ToArray();
                    count = reply.Total;
                    break;
                }
                case "history":
                {
                    title = "最近播放";
                    var history = await ReadHistoryDaysAsync(token);
                    dates = history.Select(item => item.Id).ToArray();
                    MusicDto.Require(dates.Distinct().Count() == dates.Length);
                    target = target with { Date = dates.Contains(target.Date) ? target.Date : dates.FirstOrDefault() };
                    if (target.Date is not null)
                    {
                        var reply = await application.GetHistoryDayDetailAsync(target.Date, target.Offset, 100, token);
                        rows = reply.Items.Select(item => SongRow.FromRecord(item.Song)).ToArray();
                        count = reply.Total;
                    }
                    break;
                }
                case "archives":
                {
                    title = "已播队列";
                    var history = await ReadHistoryPlaylistsAsync(token);
                    archives = history.Select(item => new ArchivedPlaylist(item.Id, item.Title, item.Count)).ToArray();
                    break;
                }
                default:
                {
                    target = target with { Offset = 0 };
                    var id = target.Route == "favorites" ? "favorites" : target.Route[9..];
                    var reply = await application.GetCollectionDetailAsync(id, 0, 100, token);
                    detail = PlaylistRow.FromSummary(reply.Collection);
                    MusicDto.Require(detail.Id == id);
                    title = detail.Name; count = reply.Total;
                    rows = reply.Songs.Select(item => SongRow.FromRecord(item)).ToArray();
                    break;
                }
            }
            token.ThrowIfCancellationRequested();
            if (stopping || generation != ctx.LoadGeneration) return;
            if (detail is null && target.Offset > 0 && target.Offset >= count)
            {
                if (retry >= 2) throw new MusicOperationException("REVISION_CONFLICT");
                await LoadAsync(target with { Offset = count == 0 ? 0 : (count - 1) / 100 * 100 },
                    cancellationToken, retry + 1, publishStatus);
                return;
            }
            ctx.Publishing = true;
            try
            {
                var previousSelection = ctx.CaptureContentSelection();
                ctx.CurrentLocation = target;
                ApplyLoadedLocation(detail, title, count, rows, dates, archives, target.Selected);
                ctx.Locations[target.Key] = target;
                ctx.PublishContentSelection(previousSelection);
                if (ReferenceEquals(ctx.LoadCancellation, request)) ctx.LoadCancellation = null;
                ctx.RefreshBindings();
            }
            finally { ctx.Publishing = false; }
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (!stopping && generation == ctx.LoadGeneration) ctx.RestoreScroll(target.Scroll);
            }, DispatcherQueuePriority.Low);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (publishStatus && !stopping && generation == ctx.LoadGeneration && stamp == ctx.StatusEpoch)
                ctx.SetStatus(ErrorText(error));
            throw;
        }
        finally
        {
            if (ReferenceEquals(ctx.LoadCancellation, request))
            { ctx.LoadCancellation = null; if (!disposed) ctx.RefreshBindings(); }
        }
    }

    private async Task<MusicHistoryDaySummary[]> ReadHistoryDaysAsync(CancellationToken token)
    {
        var rows = new List<MusicHistoryDaySummary>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var reply = await application.ListHistoryDaysAsync(rows.Count, 100, token);
            MusicDto.Require(reply.Items.All(row => ids.Add(row.Id)));
            rows.AddRange(reply.Items);
            if (rows.Count >= reply.Total || reply.Items.Length == 0) return rows.ToArray();
        }
    }

    private async Task<MusicHistoryPlaylistSummary[]> ReadHistoryPlaylistsAsync(CancellationToken token)
    {
        var rows = new List<MusicHistoryPlaylistSummary>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var reply = await application.ListHistoryPlaylistsAsync(rows.Count, 100, token);
            MusicDto.Require(reply.Items.All(row => ids.Add(row.Id)));
            rows.AddRange(reply.Items);
            if (rows.Count >= reply.Total || reply.Items.Length == 0) return rows.ToArray();
        }
    }
}
