using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed record MusicQueueSurfaceRow(SongRow Song, bool IsCurrent) : System.ComponentModel.INotifyPropertyChanged
{
    public bool IsCurrent { get; private set; } = IsCurrent;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void SetCurrent(bool value)
    {
        if (IsCurrent == value) return;
        IsCurrent = value;
        PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
    }
    internal void RefreshSong(MusicSongRecord? value, long publication = -1)
    {
        var before = (Title, Artist, Cover, Duration);
        if (!Song.ApplyRepository(value, publication)) return;
        if (before.Title != Title) PropertyChanged?.Invoke(this, new(nameof(Title)));
        if (before.Artist != Artist) PropertyChanged?.Invoke(this, new(nameof(Artist)));
        if (before.Cover != Cover) PropertyChanged?.Invoke(this, new(nameof(Cover)));
        if (before.Duration != Duration) PropertyChanged?.Invoke(this, new(nameof(Duration)));
    }
    public string EntryId => Song.EntryId ?? "";
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    public string Cover => Song.Cover;
    public string Duration => Song.Duration;
}

/// <summary>Standalone Layer-3 owner for the music play-queue surface and recommendation sessions.</summary>
public sealed partial class MusicQueueController : MusicController
{
    private readonly MusicPlaybackController playbackController;
    private readonly MusicAccountsController accounts;
    private readonly MusicCatalogActionState catalogActions;
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
    private bool queueSurfaceOpen;

    internal MusicQueueController(MusicRuntimeContext ctx, MusicPlaybackController playbackController,
        MusicAccountsController accounts, MusicCatalogActionState catalogActions) : base(ctx)
    {
        this.playbackController = playbackController;
        this.accounts = accounts;
        this.catalogActions = catalogActions;
        QueueSurfaceRows = new(queueSurfaceRows);
        InitializeRecommendations();
    }

    internal event Func<CancellationToken, Task>? PlaylistsChanged;
    internal event Action<Exception>? RecommendationActionError;

    public ReadOnlyObservableCollection<MusicQueueSurfaceRow> QueueSurfaceRows { get; }
    public string QueueSurfaceCountText => queueSurfaceTotal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string QueueSurfaceStatus => queueSurfaceStatus;
    public bool HasQueueSurfaceItems => queueSurfaceRows.Count > 0;
    public bool CanLoadMoreQueueSurface => queueSurfaceRows.Count < queueSurfaceTotal &&
        !queueSurfaceLoadMoreAdmitted &&
        queueSurfacePending is not { IsCompleted: false };
    public bool CanClearQueueSurface => CanEdit && queueSurfaceTotal > 0;

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        QueueSurfaceBindings();
        RecommendationBindings();
    }

    internal async Task ActivatePlayQueueAsync(CancellationToken cancellationToken)
    {
        queueSurfaceOpen = true;
        queueSurfaceStatus = "";
        QueueSurfaceBindings();
        await RefreshQueueSurfaceAsync(force: queueSurfaceObservedMutation != playbackController.QueueEpoch, cancellationToken);
    }

    internal void DeactivatePlayQueue()
    {
        queueSurfaceOpen = false;
        queueSurfaceStatus = "";
        RetireQueueSurfaceRequest();
        QueueSurfaceBindings();
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
        if (!force && queueSurfaceObservedMutation == playbackController.QueueEpoch && queueSurfaceRows.Count <= queueSurfaceTotal)
            return Task.CompletedTask;
        RequestQueueSurfaceReload(versionDrift: false);
        return AwaitQueueSurfaceFlightAsync(cancellationToken);
    }

    private void RequestQueueSurfaceReload(bool versionDrift)
    {
        var mutation = playbackController.QueueEpoch;
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
                            var reply = await application.GetQueueAsync(0, 100, request.Token);
                            request.Token.ThrowIfCancellationRequested();
                            if (stopping || generation != queueSurfaceGeneration || !queueSurfaceOpen) return;
                            // A newer queue generation arrived while this read was physical.
                            // Do not flash its older rows; coalesce all changes into one next receipt.
                            if (queueSurfaceReloadRequested) continue;
                            var rows = reply.Entries
                                .Select(item => SongRow.FromRecord(item.Song, item.EntryId)).ToArray();
                            var total = reply.Total;
                            var version = reply.Version;
                            PublishQueueSurfaceRows(rows);
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
                        var reply = await application.GetQueueAsync(offset, 100, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        if (stopping || generation != queueSurfaceGeneration || !queueSurfaceOpen) return;
                        var total = reply.Total;
                        var version = reply.Version;
                        if (queueSurfaceReloadRequested || total != expectedTotal || version != expectedVersion)
                        {
                            if (!queueSurfaceReloadRequested)
                                RequestQueueSurfaceReload(versionDrift: true);
                            continue;
                        }
                        var rows = reply.Entries
                            .Select(item => SongRow.FromRecord(item.Song, item.EntryId)).ToArray();
                        var currentEntry = playbackController.CurrentSong?.EntryId;
                        foreach (var row in rows)
                            if (!queueSurfaceRows.Any(existing => existing.EntryId == row.EntryId))
                                queueSurfaceRows.Add(new(row, row.EntryId == currentEntry));
                        queueSurfaceTotal = total;
                        queueSurfaceVersion = version;
                        queueSurfaceObservedMutation = playbackController.QueueEpoch;
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

    internal void RefreshQueueSurfaceCurrent()
    {
        var currentEntry = playbackController.CurrentSong?.EntryId;
        foreach (var row in queueSurfaceRows) row.SetCurrent(row.EntryId == currentEntry);
        QueueSurfaceBindings();
    }

    private void PublishQueueSurfaceRows(IReadOnlyList<SongRow> rows)
    {
        var currentEntry = playbackController.CurrentSong?.EntryId;
        var ids = rows.Select(row => row.EntryId).ToHashSet(StringComparer.Ordinal);
        for (int index = queueSurfaceRows.Count - 1; index >= 0; index--)
            if (!ids.Contains(queueSurfaceRows[index].EntryId)) queueSurfaceRows.RemoveAt(index);
        var retained = queueSurfaceRows.ToDictionary(row => row.EntryId, StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            var song = rows[index];
            if (retained.TryGetValue(song.EntryId!, out var row))
            {
                if (!ReferenceEquals(queueSurfaceRows[index], row))
                    queueSurfaceRows.Move(queueSurfaceRows.IndexOf(row), index);
                row.RefreshSong(song.Record);
                row.SetCurrent(row.EntryId == currentEntry);
            }
            else queueSurfaceRows.Insert(index, new(song, song.EntryId == currentEntry));
        }
    }

    internal void InvalidateQueueSurface()
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

    internal void RetireQueueSurfaceRequest()
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
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.MusicPlayback,
                    "queue", "queue_surface_retire_failed", new { error = error.Message });
            }
            finally { source.Dispose(); }
        }
    }

    public Task PlayQueueSurfaceItemAsync(MusicQueueSurfaceRow row)
    {
        if (!queueSurfaceRows.Contains(row) || string.IsNullOrEmpty(row.EntryId)) return Task.CompletedTask;
        playbackController.TrackMusicRecovery(playbackController.RetireMusicRecovery());
        catalogActions.Invalidate();
        playbackController.CancelOnline();
        return RunPlaybackActionAsync(async generation =>
        {
            await playbackController.QueueCallAsync(token => application.SelectQueueEntryAsync(row.EntryId, ct: token));
            await playbackController.ReadQueue(lifetime.Token);
            if (generation != ctx.PlaybackActionGeneration) return;
            await playbackController.StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration);
            InvalidateQueueSurface();
        }, supersede: true);
    }

    public Task RemoveQueueSurfaceItemAsync(MusicQueueSurfaceRow row)
    {
        if (!queueSurfaceRows.Contains(row) || string.IsNullOrEmpty(row.EntryId)) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var playing = playback.OwnerId == Id && playback.IsPlaying;
            var reply = await playbackController.QueueCallAsync(token => application.RemoveQueueEntryAsync(row.EntryId, token));
            var removedCurrent = reply.WasCurrent;
            if (removedCurrent) await playbackController.StopOwnedPlayback();
            await playbackController.ReadQueue(lifetime.Token);
            if (removedCurrent && playing && playbackController.CurrentSong is not null) await playbackController.StartCurrent(lifetime.Token);
            InvalidateQueueSurface();
        });
    }

    public Task ClearQueueSurfaceAsync()
    {
        if (!CanClearQueueSurface) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (!await ConfirmAsync("清空播放列表？", "播放列表", lifetime.Token)) return;
            await playbackController.QueueCallAsync(token => application.ClearQueueAsync(token), clearsSearchRecommendation: true);
            await playbackController.StopOwnedPlayback();
            await playbackController.ReadQueue(lifetime.Token);
            InvalidateQueueSurface();
        });
    }

    public Task FavoriteQueueSurfaceAsync()
    {
        if (!CanClearQueueSurface) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var songs = new List<MusicSongRecord>();
            long? queueVersion = null;
            int offset = 0, total;
            do
            {
                var page = await application.GetQueueAsync(offset, 100, lifetime.Token);
                var version = page.Version;
                if (queueVersion is not null && queueVersion != version)
                    throw new MusicOperationException("REVISION_CONFLICT");
                queueVersion = version;
                songs.AddRange(page.Entries.Select(item => item.Song));
                total = page.Total;
                offset = songs.Count;
            } while (offset < total);

            var favorite = await application.GetCollectionDetailAsync("favorites", 0, 1, lifetime.Token);
            var revision = favorite.Collection.Revision;
            foreach (var chunk in songs.Chunk(1000))
            {
                var reply = await application.AddSongsToCollectionAsync("favorites", revision, chunk, lifetime.Token);
                revision = reply.Revision;
            }
            if (PlaylistsChanged is { } changed) await changed(lifetime.Token);
        });
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, MusicSongRecord?> read, long publication)
    {
        foreach (var row in queueSurfaceRows.Where(row => ids.Contains(row.Song.Id)))
            row.RefreshSong(read(row.Song.Id), publication);
        foreach (var row in RecommendationCandidates.Where(row => ids.Contains(row.Song.Id)))
            row.RefreshSong(read(row.Song.Id), publication);
    }
}
