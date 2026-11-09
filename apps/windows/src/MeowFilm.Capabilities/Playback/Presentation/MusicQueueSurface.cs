using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed record MusicQueueSurfaceRow(SongRow Song, bool IsCurrent)
{
    public string EntryId => Song.EntryId ?? "";
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    public string Cover => Song.Cover;
    public string Duration => Song.Duration;
}

public sealed partial class MusicCapability : IDesktopGlobalSurfaceSource
{
    private readonly ObservableCollection<MusicQueueSurfaceRow> queueSurfaceRows = [];
    private CancellationTokenSource? queueSurfaceCancellation;
    private Task? queueSurfacePending;
    private long queueSurfaceGeneration;
    private long queueSurfaceVersion = -1;
    private long queueSurfaceObservedMutation = -1;
    private long queueSurfaceReloadMutation = -1;
    private long queueSurfaceActiveReloadMutation = -1;
    private int queueSurfaceTotal;
    private bool queueSurfaceReloadRequested;
    private bool queueSurfaceLoadMoreRequested;
    private bool queueSurfaceLoadMoreAdmitted;
    private string queueSurfaceStatus = "";
    private bool playerDetailOpen;
    private bool queueSurfaceOpen;

    public ReadOnlyObservableCollection<MusicQueueSurfaceRow> QueueSurfaceRows { get; private set; } = null!;
    public string QueueSurfaceCountText => queueSurfaceTotal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string QueueSurfaceStatus => queueSurfaceStatus;
    public bool HasQueueSurfaceItems => queueSurfaceRows.Count > 0;
    public bool CanLoadMoreQueueSurface => queueSurfaceRows.Count < queueSurfaceTotal &&
        !queueSurfaceLoadMoreAdmitted &&
        queueSurfacePending is not { IsCompleted: false };
    public bool CanClearQueueSurface => CanEdit && queueSurfaceTotal > 0;
    public event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;

    private void InitializeGlobalSurfaces()
    {
        QueueSurfaceRows = new(queueSurfaceRows);
    }

    public FrameworkElement GetGlobalSurface(DesktopGlobalSurface surface) => surface switch
    {
        DesktopGlobalSurface.PlayerDetail => Presentation.Detail,
        DesktopGlobalSurface.PlayQueue => Presentation.Queue,
        _ => throw new ArgumentOutOfRangeException(nameof(surface))
    };

    public async Task ActivateGlobalSurfaceAsync(DesktopGlobalSurface surface, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (surface)
        {
            case DesktopGlobalSurface.PlayerDetail:
                Presentation.DeactivatePlayer();
                playerDetailOpen = true;
                await Presentation.Detail.ActivateAsync(cancellationToken);
                break;
            case DesktopGlobalSurface.PlayQueue:
                queueSurfaceOpen = true;
                queueSurfaceStatus = "";
                QueueSurfaceBindings();
                await RefreshQueueSurfaceAsync(force: queueSurfaceObservedMutation != queueEpoch, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }
    }

    public void DeactivateGlobalSurface(DesktopGlobalSurface surface)
    {
        switch (surface)
        {
            case DesktopGlobalSurface.PlayerDetail:
                playerDetailOpen = false;
                Presentation.DeactivateDetail();
                if (!NeedsLyrics) CancelLyrics();
                break;
            case DesktopGlobalSurface.PlayQueue:
                queueSurfaceOpen = false;
                queueSurfaceStatus = "";
                RetireQueueSurfaceRequest();
                QueueSurfaceBindings();
                break;
        }
    }

    public Task OpenPlayerDetailAsync()
    {
        if (!stopping) GlobalSurfaceRequested?.Invoke(this,
            new(DesktopGlobalSurface.PlayerDetail));
        return Task.CompletedTask;
    }

    public Task OpenPlayQueueAsync()
    {
        if (!stopping) GlobalSurfaceRequested?.Invoke(this,
            new(DesktopGlobalSurface.PlayQueue));
        return Task.CompletedTask;
    }

    public async Task LoadMoreQueueSurfaceAsync()
    {
        if (stopping || !queueSurfaceOpen || !CanLoadMoreQueueSurface) return;
        queueSurfaceLoadMoreAdmitted = true;
        queueSurfaceStatus = "";
        queueSurfaceLoadMoreRequested = true;
        QueueSurfaceBindings();
        try { await AwaitQueueSurfaceFlightAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && queueSurfaceOpen) queueSurfaceStatus = ErrorText(error);
        }
        finally
        {
            queueSurfaceLoadMoreAdmitted = false;
            QueueSurfaceBindings();
        }
    }

    private Task RefreshQueueSurfaceAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && queueSurfaceObservedMutation == queueEpoch && queueSurfaceRows.Count <= queueSurfaceTotal)
            return Task.CompletedTask;
        RequestQueueSurfaceReload(versionDrift: false);
        return AwaitQueueSurfaceFlightAsync(cancellationToken);
    }

    private void RequestQueueSurfaceReload(bool versionDrift)
    {
        var mutation = queueEpoch;
        if (!versionDrift &&
            (queueSurfaceObservedMutation == mutation ||
             queueSurfaceActiveReloadMutation == mutation ||
             queueSurfaceReloadRequested && queueSurfaceReloadMutation == mutation))
            return;
        if (versionDrift && queueSurfaceReloadRequested) return;
        queueSurfaceReloadMutation = mutation;
        queueSurfaceReloadRequested = true;
    }

    private Task AwaitQueueSurfaceFlightAsync(CancellationToken cancellationToken)
    {
        var flight = GetOrStartQueueSurfaceFlight();
        return cancellationToken.CanBeCanceled ? flight.WaitAsync(cancellationToken) : flight;
    }

    private Task GetOrStartQueueSurfaceFlight()
    {
        if (stopping || !queueSurfaceOpen) return Task.CompletedTask;
        return queueSurfacePending is { IsCompleted: false } pending
            ? pending
            : StartQueueSurfaceFlight();
    }

    private Task StartQueueSurfaceFlight()
    {
        var generation = ++queueSurfaceGeneration;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        queueSurfaceCancellation = request;
        var flight = RunQueueSurfaceFlightAsync();
        queueSurfacePending = flight;
        TrackQueueSurfaceDrain(flight);
        QueueSurfaceBindings();
        return flight;

        async Task RunQueueSurfaceFlightAsync()
        {
            await Task.Yield(); // Publish the unique flight before a synchronous port can complete.
            try
            {
                while (!stopping && generation == queueSurfaceGeneration && queueSurfaceOpen)
                {
                    if (queueSurfaceReloadRequested)
                    {
                        queueSurfaceReloadRequested = false;
                        var mutation = queueSurfaceReloadMutation;
                        queueSurfaceActiveReloadMutation = mutation;
                        try
                        {
                            var reply = await CallSettled("music.queue.get", new { offset = 0, limit = 100 }, request.Token);
                            request.Token.ThrowIfCancellationRequested();
                            if (stopping || generation != queueSurfaceGeneration || !queueSurfaceOpen) return;
                            // A newer queue generation arrived while this read was physical.
                            // Do not flash its older rows; coalesce all changes into one next reset.
                            if (queueSurfaceReloadRequested) continue;
                            var rows = MusicDto.Page(reply, "entries", 0)
                                .Select(item => SongRow.Read(item.GetProperty("song"), MusicDto.Text(item, "id"))).ToArray();
                            var total = MusicDto.Count(reply, "total");
                            var version = MusicDto.Integer(reply, "version");
                            queueSurfaceRows.Clear();
                            foreach (var row in rows)
                                queueSurfaceRows.Add(new(row, row.EntryId == current?.EntryId));
                            queueSurfaceTotal = total;
                            queueSurfaceVersion = version;
                            queueSurfaceObservedMutation = mutation;
                            QueueSurfaceBindings();
                        }
                        finally
                        {
                            if (queueSurfaceActiveReloadMutation == mutation)
                                queueSurfaceActiveReloadMutation = -1;
                        }
                        continue;
                    }

                    if (queueSurfaceLoadMoreRequested)
                    {
                        queueSurfaceLoadMoreRequested = false;
                        var offset = queueSurfaceRows.Count;
                        var expectedTotal = queueSurfaceTotal;
                        var expectedVersion = queueSurfaceVersion;
                        var reply = await CallSettled("music.queue.get", new { offset, limit = 100 }, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        if (stopping || generation != queueSurfaceGeneration || !queueSurfaceOpen) return;
                        var total = MusicDto.Count(reply, "total");
                        var version = MusicDto.Integer(reply, "version");
                        if (queueSurfaceReloadRequested || total != expectedTotal || version != expectedVersion)
                        {
                            if (!queueSurfaceReloadRequested)
                                RequestQueueSurfaceReload(versionDrift: true);
                            continue;
                        }
                        var rows = MusicDto.Page(reply, "entries", offset)
                            .Select(item => SongRow.Read(item.GetProperty("song"), MusicDto.Text(item, "id"))).ToArray();
                        foreach (var row in rows)
                            if (!queueSurfaceRows.Any(existing => existing.EntryId == row.EntryId))
                                queueSurfaceRows.Add(new(row, row.EntryId == current?.EntryId));
                        queueSurfaceTotal = total;
                        queueSurfaceVersion = version;
                        queueSurfaceObservedMutation = queueEpoch;
                        QueueSurfaceBindings();
                        continue;
                    }

                    return;
                }
            }
            finally
            {
                if (ReferenceEquals(queueSurfaceCancellation, request))
                {
                    queueSurfaceCancellation = null;
                    queueSurfacePending = null;
                    request.Dispose();
                    QueueSurfaceBindings();
                }
            }
        }
    }

    private void TrackQueueSurfaceDrain(Task work)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        _ = ObserveQueueSurfaceDrainAsync(work, drained);
    }

    private async Task ObserveQueueSurfaceDrainAsync(Task work, TaskCompletionSource drained)
    {
        try { await work; }
        catch { /* An awaiting activation observes the error; invalidation requests still need an observer. */ }
        finally
        {
            loadDrains.Remove(drained.Task);
            drained.TrySetResult();
        }
    }

    private void RefreshQueueSurfaceCurrent()
    {
        for (var i = 0; i < queueSurfaceRows.Count; i++)
        {
            var row = queueSurfaceRows[i];
            var active = row.EntryId == current?.EntryId;
            if (row.IsCurrent != active) queueSurfaceRows[i] = row with { IsCurrent = active };
        }
        QueueSurfaceBindings();
    }

    private void InvalidateQueueSurface()
    {
        if (!queueSurfaceOpen) return;
        RequestQueueSurfaceReload(versionDrift: false);
        GetOrStartQueueSurfaceFlight();
    }

    private void QueueSurfaceBindings()
    {
        Changed(nameof(QueueSurfaceCountText));
        Changed(nameof(QueueSurfaceStatus));
        Changed(nameof(HasQueueSurfaceItems));
        Changed(nameof(CanLoadMoreQueueSurface));
        Changed(nameof(CanClearQueueSurface));
    }

    private void RetireQueueSurfaceRequest()
    {
        queueSurfaceGeneration++;
        queueSurfaceReloadRequested = false;
        queueSurfaceLoadMoreRequested = false;
        queueSurfaceLoadMoreAdmitted = false;
        queueSurfaceActiveReloadMutation = -1;
        var prior = queueSurfaceCancellation;
        var pending = queueSurfacePending;
        queueSurfaceCancellation = null;
        queueSurfacePending = null;
        if (prior is null) return;
        TrackQueueSurfaceDrain(RetireAsync(prior, pending));
        static async Task RetireAsync(CancellationTokenSource source, Task? work)
        {
            try
            {
                await source.CancelAsync();
                if (work is not null) await work;
            }
            catch { }
            finally { source.Dispose(); }
        }
    }

    public Task PlayQueueSurfaceItemAsync(MusicQueueSurfaceRow row)
    {
        if (!queueSurfaceRows.Contains(row) || string.IsNullOrEmpty(row.EntryId)) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            await QueueCall("music.queue.select", new { id = row.EntryId });
            await ReadQueue(lifetime.Token);
            await StartCurrent(lifetime.Token);
            InvalidateQueueSurface();
        });
    }

    public Task RemoveQueueSurfaceItemAsync(MusicQueueSurfaceRow row)
    {
        if (!queueSurfaceRows.Contains(row) || string.IsNullOrEmpty(row.EntryId)) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var playing = playback.OwnerId == Id && playback.IsPlaying;
            var reply = await QueueCall("music.queue.remove", new { id = row.EntryId });
            var removedCurrent = reply.GetProperty("wasCurrent").GetBoolean();
            if (removedCurrent) await StopOwnedPlayback();
            await ReadQueue(lifetime.Token);
            if (removedCurrent && playing && current is not null) await StartCurrent(lifetime.Token);
            InvalidateQueueSurface();
        });
    }

    public Task ClearQueueSurfaceAsync()
    {
        if (!CanClearQueueSurface) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (!dialogs.Confirm("清空播放列表？", "播放列表")) return;
            await QueueCall("music.queue.clear");
            await StopOwnedPlayback();
            await ReadQueue(lifetime.Token);
            InvalidateQueueSurface();
        });
    }

    public Task FavoriteQueueSurfaceAsync()
    {
        if (!CanClearQueueSurface) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var songs = new List<JsonElement>();
            long? queueVersion = null;
            int offset = 0, total;
            do
            {
                var page = await Call("music.queue.get", new { offset, limit = 100 }, lifetime.Token);
                var version = MusicDto.Integer(page, "version");
                if (queueVersion is not null && queueVersion != version)
                    throw new MusicOperationException("REVISION_CONFLICT");
                queueVersion = version;
                songs.AddRange(MusicDto.Page(page, "entries", offset)
                    .Select(item => item.GetProperty("song").Clone()));
                total = MusicDto.Count(page, "total");
                offset = songs.Count;
            } while (offset < total);

            var favorite = await Call("music.library.detail", new { id = "favorites", offset = 0, limit = 1 }, lifetime.Token);
            var revision = MusicDto.Integer(favorite.GetProperty("collection"), "revision");
            foreach (var chunk in songs.Chunk(1000))
            {
                var reply = await Call("music.library.addSongs",
                    new { id = "favorites", ifRevision = revision, songs = chunk }, lifetime.Token);
                revision = MusicDto.Integer(reply.GetProperty("collection"), "revision");
            }
            await RefreshPlaylists(lifetime.Token);
        });
    }
}
