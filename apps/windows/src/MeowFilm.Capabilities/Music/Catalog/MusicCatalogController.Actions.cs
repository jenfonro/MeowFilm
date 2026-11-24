using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private sealed class CatalogAction(CancellationTokenSource request, string platform, long observation)
    {
        internal readonly CancellationTokenSource Request = request;
        internal readonly string Platform = platform;
        internal readonly long Observation = observation;
        internal long? PlaybackEpoch;
    }
    private CatalogAction? catalogAction;
    private long catalogActionGeneration;
    private MusicCatalogCard? preparingCatalogCard;

    internal long CatalogActionGeneration => catalogActionGeneration;
    internal MusicCatalogCard? PreparingCatalogCard => preparingCatalogCard;

    internal void SetPreparingCatalogCard(MusicCatalogCard? card)
    {
        preparingCatalogCard = card;
        if (card is not null) card.IsPreparing = true;
    }

    // Called synchronously at all user playback-intent/queue mutation boundaries.
    public void InvalidateCatalogAction()
    {
        catalogActionGeneration++;
        var active = catalogAction; catalogAction = null;
        if (active?.Request is { IsCancellationRequested: false } request) request.Cancel();
        ClearPreparingCatalogCard();
        if (active is not null) Changed(nameof(CanCancelCatalog));
    }

    internal void ClearPreparingCatalogCard()
    {
        if (preparingCatalogCard is { } card) card.IsPreparing = false;
        preparingCatalogCard = null;
    }

    public Task PlayCatalogPlaylistAsync()
    {
        if (CanCatalogAction && catalogLoadedTarget is { Operation: "fm" or "similar" or "heartbeat" } target)
        {
            try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
            return queue.StartRecommendation(target);
        }
        return IsCatalogBatch ? CatalogBatchActionAsync("play", 0) : CatalogPlaylistActionAsync("play", 0);
    }

    public Task SaveCatalogPlaylistAsync() => IsCatalogBatch ? CatalogBatchActionAsync("save", 0) : SaveNetworkPlaylistAsync();

    private Task CatalogPlaylistActionAsync(string action, int index, string? songId = null, string? memberId = null,
        MusicCatalogCard? entry = null, string? sourcePlaylistId = null, int memberOccurrence = 0)
    {
        if (entry is null ? !CanCatalogAction : !CanCatalogBrowse || !entry.CanPlay || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        var parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : new[] { "catalog-playlist", entry.Platform, Uri.EscapeDataString(sourcePlaylistId ?? entry.ActionId) };
        if (parts.Length != 3 || index < 0) return Task.CompletedTask;
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        JsonElement? personal = entry?.Locator;
        if (entry is null && catalogLoadedTarget?.Params.Personal is { } locator) personal = locator.Clone();
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var queueSnapshot = await application.GetQueueAsync(0, 1, signal);
            long version = queueSnapshot.Version;
            var prepared = await application.PrepareCatalogPlaylistAsync(platform, id, refresh: true, personal: personal, ct: signal);
            var token = prepared.Token;
            try
            {
                if (!currentAction()) return;
                Func<Task> commit = async () =>
                {
                    if (!currentAction()) return;
                    RequireCatalogContent();
                    bool queueUpdated = false;
                    try
                    {
                        playbackController.AdvanceQueueEpoch();
                        var result = await application.CommitCatalogPlaylistAsync(
                            token,
                            action,
                            version,
                            index: action == "play" && memberId is null && songId is null && sourcePlaylistId is null ? index : 0,
                            memberId: action == "play" ? memberId : null,
                            songId: action == "play" && memberId is null ? songId : null,
                            memberOccurrence: action == "play" && (memberId is not null || songId is not null) ? memberOccurrence : 0,
                            ct: signal);
                        MusicDto.Require(result.Version > version);
                        if (action == "next")
                        {
                            playbackController.ObservePreloadQueueMutation(result);
                            queue.InvalidateQueueSurface();
                        }
                        await playbackController.ReadQueue(lifetime.Token);
                        queueUpdated = true;
                        if (action == "play" && currentAction())
                        {
                            await StartCatalogCurrent(signal);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!queueUpdated && action == "next") await RecoverNextQueueAsync();
                    }
                    catch (Exception error)
                    {
                        if (!queueUpdated)
                        {
                            if (action == "play") playbackController.MarkQueueUnsaved();
                            if (action == "next") await RecoverNextQueueAsync();
                        }
                        if (currentAction()) PublishCatalogActionError(error);
                    }
                };
                if (action == "play")
                    await RunPlaybackActionAsync(_ => commit(), supersede: true);
                else
                    await RunAsync(commit);
            }
            finally
            {
                // Idempotent release; no song payload and no retry of an uncertain commit.
                try { await application.DiscardCatalogPlaylistAsync(token, lifetime.Token); }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    DebugLog.Warn(DebugCategories.MusicCatalog, "catalog-actions", "discard-stage-failed",
                        new { error = error.Message });
                }
            }
        }, card: entry);
    }

    private async Task RecoverNextQueueAsync()
    {
        playbackController.MarkQueueUnsaved();
        // A missing commit receipt may hide a durable mutation, including while
        // the old preload is still awaiting peekNext and has no queue version.
        playbackController.CancelMusicPreload();
        ctx.RefreshBindings();
        queue.InvalidateQueueSurface();
        try { await playbackController.ReadQueue(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicCatalog, "catalog-actions", "recover-next-queue-failed",
                new { error = error.Message });
        }
    }

    private Task CatalogBatchActionAsync(string action, int index)
    {
        var activeSongs = ctx.ActiveSongs;
        if (!CanCatalogAction || !IsCatalogBatch || action == "play" && (index < 0 || index >= activeSongs.Count)) return Task.CompletedTask;
        var songs = activeSongs.Select(s => s.Record).ToArray();
        var title = libraryPort.Heading; var route = ctx.CurrentRoute; var platform = catalogLoadedPlatform;
        var kind = catalogLoadedTarget!.Operation;
        var (capturedContext, _) = playbackController.CaptureFeedbackContext();
        var playContext = (capturedContext ?? MusicPlayContextRecord.Empty) with { SourceTitle = title };
        var queueSource = new MusicQueueSourceRecord(route, title, kind, platform);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            MusicDto.Require(songs.Length > 0);
            long version = 0;
            if (action == "play") version = (await application.GetQueueAsync(0, 1, signal)).Version;
            if (!currentAction()) return;
            Func<Task> batch = async () =>
            {
                if (!currentAction()) return;
                RequireCatalogContent();
                bool queueUpdated = false;
                try
                {
                    if (action == "play")
                    {
                        playbackController.AdvanceQueueEpoch();
                        // Complete batches use the ordinary native queue command.
                        // Its public envelope has no expectedVersion; playlist stage
                        // commit above owns the stronger complete-playlist CAS.
                        var result = await application.ReplaceQueueAsync(songs, index, playContext, queueSource, ct: signal);
                        MusicDto.Require(result.Version > version);
                        await playbackController.ReadQueue(lifetime.Token);
                        queueUpdated = true;
                        if (currentAction())
                        {
                            await StartCatalogCurrent(signal);
                        }
                    }
                    else
                    {
                        var result = await application.CreatePlaylistAsync(title, songs, signal);
                        _ = PlaylistRow.FromSummary(result);
                        await libraryPort.RefreshPlaylistsAsync(lifetime.Token);
                        if (currentAction()) ctx.SetStatus("已保存到本地歌单");
                    }
                }
                catch (Exception error)
                {
                    if (action == "play" && !queueUpdated) playbackController.MarkQueueUnsaved();
                    if (currentAction()) PublishCatalogActionError(error);
                }
            };
            if (action == "play")
                await RunPlaybackActionAsync(_ => batch(), supersede: true);
            else
                await RunAsync(batch);
        });
    }

    private Task PlayCatalogSectionAsync(MusicCatalogCard card)
    {
        var section = CatalogSections.FirstOrDefault(s => s.Items.Any(item => ReferenceEquals(item, card)));
        if (section is null) return Task.CompletedTask;
        var source = card.PlaybackSource;
        if (source is null && section.Action is { Kind: "playlist" } playlist) source = playlist.Action;
        if (source is { } declared && MusicCatalogDto.Text(declared, "type") == "playlist")
        {
            var id = MusicCatalogDto.Text(declared, "id");
            if (id.Length > 0) return CatalogSectionPlaylistAsync(card, id);
        }
        var members = section.Items.Where(item => item.Song is not null).ToArray();
        int index = Array.FindIndex(members, item => ReferenceEquals(item, card));
        if (index < 0) return Task.CompletedTask;
        var songs = members.Select(item => item.Song!.Record).ToArray();
        var sectionSource = new MusicQueueSourceRecord(section.Id, section.Title, "songs", card.Platform);
        return TrackCatalogAction(async (signal, current) =>
        {
            RequireCatalogContent();
            await RunPlaybackActionAsync(async _ =>
            {
                if (!current()) return;
                RequireCatalogContent();
                playbackController.AdvanceQueueEpoch();
                try
                {
                    await application.ReplaceQueueAsync(songs, index, null, sectionSource, ct: signal);
                }
                catch { playbackController.MarkQueueUnsaved(); throw; }
                await playbackController.ReadQueue(lifetime.Token);
                if (current())
                {
                    await StartCatalogCurrent(signal);
                }
            }, supersede: true);
        }, card: card);
    }

    private Task CatalogSectionPlaylistAsync(MusicCatalogCard card, string playlistId)
    {
        var section = CatalogSections.First(s => s.Items.Any(item => ReferenceEquals(item, card)));
        int occurrence = section.Items.TakeWhile(item => !ReferenceEquals(item, card))
            .Count(item => item.Song?.Id == card.Song!.Id);
        return CatalogPlaylistActionAsync("play", 0, card.Song!.Id, entry: card,
            sourcePlaylistId: playlistId, memberOccurrence: occurrence);
    }

    private Task PlayCatalogSongAsync(MusicCatalogCard card)
    {
        bool fromHome = IsCatalogHome;
        long navigation = ctx.NavigationSubmission;
        return PlayCatalogSongAsync(card.Platform, card.ActionId,
            () => fromHome ? IsCatalogHome && navigation == ctx.NavigationSubmission && card.Platform == settings.CatalogSourcePlatform :
                ContainsCatalogCard(card), requireCatalogContent: true, card: card);
    }

    private Task PlayCatalogSongAsync(string platform, string id, Func<bool> sourceCurrent,
        bool requireCatalogContent = false, MusicCatalogCard? card = null) => TrackCatalogAction(async (signal, currentAction) =>
    {
        if (requireCatalogContent) RequireCatalogContent();
        if (!sourceCurrent()) return;
        var record = await application.GetCatalogSongAsync(platform, id, ct: signal);
        if (!currentAction() || !sourceCurrent()) return;
        var song = SongRow.FromRecord(record);
        var songContext = MusicPlayContextRecord.Empty with { SourcePlatform = platform, SourceType = "song" };
        await RunPlaybackActionAsync(async _ =>
        {
            if (!currentAction() || !sourceCurrent()) return;
            if (requireCatalogContent) RequireCatalogContent();
            await playbackController.QueueCallAsync(
                ct => application.ReplaceQueueAsync([song.Record], 0, songContext, ct: ct),
                clearsSearchRecommendation: true);
            await playbackController.ReadQueue(lifetime.Token);
            if (currentAction() && sourceCurrent())
            {
                await StartCatalogCurrent(signal);
            }
        }, supersede: true);
    }, card is null ? "正在加载歌曲" : null, card);

    internal void PublishCatalogActionError(Exception error)
    {
        string code = error is MusicOperationException e ? e.Code : error is OperationCanceledException ? "CANCELLED" : "INVALID_RESPONSE";
        if (!IsCatalogHome && code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "SESSION_CHANGED")
            RetireCatalogSession();
        ctx.SetStatus(MusicCatalogDto.ErrorText(code));
        if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED")
        {
            PublishCatalogAccountNotice(code);
            RecoverHomeAfterAuthenticationFailure();
        }
    }

    private Task StartCatalogCurrent(CancellationToken token)
    {
        var pending = catalogAction;
        token.ThrowIfCancellationRequested();
        if (pending is null || pending.Request.Token != token) throw new OperationCanceledException(token);
        return playbackController.StartCurrent(token, ownsEpoch: epoch => pending.PlaybackEpoch = epoch);
    }

    internal void CommitCatalogPlayback()
    {
        if (catalogAction is not { } pending || pending.PlaybackEpoch != playbackController.PlaybackEpoch) return;
        // The accepted occurrence now owns its input. Page retirement must not cancel
        // that input while this action is still draining its stage cleanup receipt.
        catalogAction = null;
        ClearPreparingCatalogCard();
        Changed(nameof(CanCancelCatalog));
    }

    private Task TrackCatalogAction(Func<CancellationToken, Func<bool>, Task> action, string? loadingStatus = null, MusicCatalogCard? card = null)
    {
        if (stopping) return Task.CompletedTask;
        catalogAccountNotice = ""; ctx.NotifyStatusChanged();
        InvalidateCatalogAction();
        var generation = catalogActionGeneration; var locationGeneration = ctx.LoadGeneration;
        bool fromHome = IsCatalogHome;
        var navigation = ctx.NavigationSubmission;
        var actionPlatform = card?.Platform ?? catalogLoadedPlatform;
        var observation = CatalogAccountObservation(actionPlatform);
        var playGeneration = playbackController.PlaybackEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var pending = new CatalogAction(request, actionPlatform, observation);
        catalogAction = pending;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        preparingCatalogCard = card;
        if (card is not null) card.IsPreparing = true;
        if (loadingStatus is not null) ctx.SetStatus(loadingStatus);
        Changed(nameof(CanCancelCatalog));
        return Execute();
        bool Current() => !stopping && !request.IsCancellationRequested && generation == catalogActionGeneration &&
            observation == CatalogAccountObservation(actionPlatform) &&
            (fromHome ? IsCatalogHome && navigation == ctx.NavigationSubmission : locationGeneration == ctx.LoadGeneration);
        async Task Execute()
        {
            try
            {
                if (IsPrivateCatalogCard(card))
                {
                    await accounts.EnsureAccountsForRequestAsync(request.Token);
                    if (!Current()) return;
                    RequireCatalogAccount(card);
                }
                // Public user-intent hooks cancel immediately; epoch adds a guard
                // against native playback changes while content is being fetched.
                await action(request.Token, () => Current() && playGeneration == playbackController.PlaybackEpoch);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (Current()) PublishCatalogActionError(error);
            }
            finally
            {
                if (ReferenceEquals(catalogAction, pending))
                {
                    catalogAction = null;
                    if (generation == catalogActionGeneration) ClearPreparingCatalogCard();
                }
                request.Dispose(); loadDrains.Remove(drained.Task); drained.TrySetResult();
                if (!disposed) Changed(nameof(CanCancelCatalog));
            }
        }
    }
}
