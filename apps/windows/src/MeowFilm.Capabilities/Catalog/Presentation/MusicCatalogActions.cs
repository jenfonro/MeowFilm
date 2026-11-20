using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
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

    // Called synchronously at all user playback-intent/queue mutation boundaries.
    public void InvalidateCatalogAction()
    {
        catalogActionGeneration++;
        var active = catalogAction; catalogAction = null;
        if (active?.Request is { IsCancellationRequested: false } request) request.Cancel();
        ClearPreparingCatalogCard();
        if (active is not null) Changed(nameof(CanCancelCatalog));
    }
    private void ClearPreparingCatalogCard()
    {
        if (preparingCatalogCard is { } card) card.IsPreparing = false;
        preparingCatalogCard = null;
    }
    public Task PlayCatalogPlaylistAsync()
    {
        if (CanCatalogAction && catalogLoadedTarget is { Operation: "fm" or "similar" or "heartbeat" } target)
        {
            try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
            return StartRecommendation(target);
        }
        return IsCatalogBatch ? CatalogBatchActionAsync("play", 0) : CatalogPlaylistActionAsync("play", 0);
    }
    public Task SaveCatalogPlaylistAsync() => IsCatalogBatch ? CatalogBatchActionAsync("save", 0) : SaveNetworkPlaylistAsync();
    private Task CatalogPlaylistActionAsync(string action, int index, string? songId = null, string? memberId = null,
        MusicCatalogCard? entry = null, string? sourcePlaylistId = null, int memberOccurrence = 0)
    {
        if (entry is null ? !CanCatalogAction : !CanCatalogBrowse || !entry.CanPlay || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        var parts = entry is null ? location.Route.Split('/', 3) : new[] { "catalog-playlist", entry.Platform, Uri.EscapeDataString(sourcePlaylistId ?? entry.ActionId) };
        if (parts.Length != 3 || index < 0) return Task.CompletedTask;
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        JsonElement? personal = entry?.Locator;
        if (entry is null && catalogLoadedTarget is { } loaded && loaded.Parameters.TryGetProperty("personal", out var locator)) personal = locator.Clone();
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var queue = await Call("music.queue.get", new { offset = 0, limit = 1 }, signal);
            long version = MusicDto.Integer(queue, "version");
            var prepare = new Dictionary<string, object?> { ["platform"] = platform, ["id"] = id, ["refresh"] = true };
            if (personal is not null) prepare["personal"] = personal.Value;
            var prepared = await CallSettledReceipt("music.catalog.playlist.prepare", prepare, signal);
            var token = MusicDto.Text(prepared, "token");
            try
            {
                if (!currentAction()) return;
                await RunAsync(async () =>
                {
                    if (!currentAction()) return;
                    RequireCatalogContent();
                    var parameters = new Dictionary<string, object?> { ["token"] = token, ["action"] = action };
                    if (action == "play")
                    {
                        if (memberId is not null) parameters["memberId"] = memberId;
                        else if (songId is not null) parameters["songId"] = songId;
                        else if (sourcePlaylistId is null) parameters["index"] = index;
                        if (memberId is not null || songId is not null) parameters["memberOccurrence"] = memberOccurrence;
                    }
                    parameters["expectedVersion"] = version;
                    try
                    {
                        queueEpoch++;
                        var result = await CallSettledReceipt("music.catalog.playlist.commit", parameters, signal);
                        MusicDto.Require(MusicDto.Integer(result, "version") > version);
                        if (action == "next")
                        {
                            ObservePreloadQueueMutation(result);
                            InvalidateQueueSurface();
                        }
                        await ReadQueue(lifetime.Token);
                        if (action == "play" && currentAction())
                        {
                            await StartCatalogCurrent(signal);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (action == "next") await RecoverNextQueueAsync();
                    }
                    catch (Exception error)
                    {
                        if (action == "play") queueSaved = false;
                        if (action == "next") await RecoverNextQueueAsync();
                        if (currentAction()) PublishCatalogActionError(error);
                    }
                });
            }
            finally
            {
                // Idempotent release; no song payload and no retry of an uncertain commit.
                try { await CallSettledReceipt("music.catalog.playlist.discard", new { token }, lifetime.Token); }
                catch { /* Native stage TTL/invalidate/close also release the bounded stage. */ }
            }
        }, card: entry);
    }
    private async Task RecoverNextQueueAsync()
    {
        queueSaved = false;
        // A missing commit receipt may hide a durable mutation, including while
        // the old preload is still awaiting peekNext and has no queue version.
        CancelMusicPreload();
        RefreshBindings();
        InvalidateQueueSurface();
        try { await ReadQueue(lifetime.Token); }
        catch { /* Retain the conservative gate until an authoritative snapshot succeeds. */ }
    }
    private Task CatalogBatchActionAsync(string action, int index)
    {
        if (!CanCatalogAction || !IsCatalogBatch || action == "play" && (index < 0 || index >= Songs.Count)) return Task.CompletedTask;
        var songs = Songs.Select(s => s.Raw.Clone()).ToArray();
        var title = heading; var route = location.Route; var platform = catalogLoadedPlatform;
        var kind = catalogLoadedTarget!.Operation;
        var queueContext = CaptureFeedbackContext();
        queueContext["sourceTitle"] = title;
        queueContext["queueSource"] = new { id = route, title, kind, platform };
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            MusicDto.Require(songs.Length > 0);
            long version = 0;
            if (action == "play") version = MusicDto.Integer(await Call("music.queue.get", new { offset = 0, limit = 1 }, signal), "version");
            if (!currentAction()) return;
            await RunAsync(async () =>
            {
                if (!currentAction()) return;
                RequireCatalogContent();
                try
                {
                    if (action == "play")
                    {
                        queueEpoch++;
                        // Complete batches use the ordinary native queue command.
                        // Its public envelope has no expectedVersion; playlist stage
                        // commit above owns the stronger complete-playlist CAS.
                        var result = await CallSettledReceipt("music.queue.replace", new { songs, index,
                            context = queueContext }, signal);
                        MusicDto.Require(MusicDto.Integer(result, "version") > version);
                        await ReadQueue(lifetime.Token);
                        if (currentAction())
                        {
                            await StartCatalogCurrent(signal);
                        }
                    }
                    else
                    {
                        var result = await CallSettledReceipt("music.library.create", new { name = title, songs }, signal);
                        _ = PlaylistRow.Read(result.GetProperty("collection"));
                        await RefreshPlaylists(lifetime.Token);
                        if (currentAction()) { status = "已保存到本地歌单"; Changed(nameof(Status)); }
                    }
                }
                catch (Exception error)
                {
                    if (action == "play") queueSaved = false;
                    if (currentAction()) PublishCatalogActionError(error);
                }
            });
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
        var songs = members.Select(item => item.Song!.Raw.Clone()).ToArray();
        return TrackCatalogAction(async (signal, current) =>
        {
            RequireCatalogContent();
            await RunAsync(async () =>
            {
                if (!current()) return;
                RequireCatalogContent();
                queueEpoch++;
                try
                {
                    await CallSettledReceipt("music.queue.replace", new { songs, index, context = new
                    { queueSource = new { id = section.Id, title = section.Title, kind = "songs", platform = card.Platform } } }, signal);
                }
                catch { queueSaved = false; throw; }
                await ReadQueue(lifetime.Token);
                if (current())
                {
                    await StartCatalogCurrent(signal);
                }
            });
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
        long navigation = navigationSubmission;
        return PlayCatalogSongAsync(card.Platform, card.ActionId,
            () => fromHome ? IsCatalogHome && navigation == navigationSubmission && card.Platform == catalogSourcePlatform :
                ContainsCatalogCard(card), requireCatalogContent: true, card: card);
    }

    private Task PlayCatalogSongAsync(string platform, string id, Func<bool> sourceCurrent,
        bool requireCatalogContent = false, MusicCatalogCard? card = null) => TrackCatalogAction(async (signal, currentAction) =>
    {
        if (requireCatalogContent) RequireCatalogContent();
        if (!sourceCurrent()) return;
        var reply = await Call("music.catalog.song", new { platform, id }, signal);
        if (!currentAction() || !sourceCurrent()) return;
        var song = SongRow.Read(reply.GetProperty("song"));
        await RunAsync(async () =>
        {
            if (!currentAction() || !sourceCurrent()) return;
            if (requireCatalogContent) RequireCatalogContent();
            await QueueCall("music.queue.replace", new { songs = new[] { song.Raw }, index = 0,
                context = new { sourcePlatform = platform, sourceType = "song" } });
            await ReadQueue(lifetime.Token);
            if (currentAction() && sourceCurrent())
            {
                await StartCatalogCurrent(signal);
            }
        });
    }, card is null ? "正在加载歌曲" : null, card);
    private void PublishCatalogActionError(Exception error)
    {
        string code = error is MusicOperationException e ? e.Code : error is OperationCanceledException ? "CANCELLED" : "INVALID_RESPONSE";
        if (!IsCatalogHome && code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "SESSION_CHANGED")
            RetireCatalogSession();
        status = MusicCatalogDto.ErrorText(code); Changed(nameof(Status));
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
        return StartCurrent(token, ownsEpoch: epoch => pending.PlaybackEpoch = epoch);
    }
    private void CommitCatalogPlayback()
    {
        if (catalogAction is not { } pending || pending.PlaybackEpoch != playbackEpoch) return;
        // The accepted occurrence now owns its input. Page retirement must not cancel
        // that input while this action is still draining its stage cleanup receipt.
        catalogAction = null;
        ClearPreparingCatalogCard();
        Changed(nameof(CanCancelCatalog));
    }
    private Task TrackCatalogAction(Func<CancellationToken, Func<bool>, Task> action, string? loadingStatus = null, MusicCatalogCard? card = null)
    {
        if (stopping) return Task.CompletedTask;
        catalogAccountNotice = ""; Changed(nameof(Status));
        InvalidateCatalogAction();
        var generation = catalogActionGeneration; var locationGeneration = loadGeneration;
        bool fromHome = IsCatalogHome;
        var navigation = navigationSubmission;
        var actionPlatform = card?.Platform ?? catalogLoadedPlatform;
        var observation = CatalogAccountObservation(actionPlatform);
        var playGeneration = playbackEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var pending = new CatalogAction(request, actionPlatform, observation);
        catalogAction = pending;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        preparingCatalogCard = card;
        if (card is not null) card.IsPreparing = true;
        if (loadingStatus is not null) { status = loadingStatus; Changed(nameof(Status)); }
        Changed(nameof(CanCancelCatalog));
        return Execute();
        bool Current() => !stopping && !request.IsCancellationRequested && generation == catalogActionGeneration &&
            observation == CatalogAccountObservation(actionPlatform) &&
            (fromHome ? IsCatalogHome && navigation == navigationSubmission : locationGeneration == loadGeneration);
        async Task Execute()
        {
            try
            {
                if (IsPrivateCatalogCard(card))
                {
                    await EnsureAccountsForRequestAsync(request.Token);
                    if (!Current()) return;
                    RequireCatalogAccount(card);
                }
                // Public user-intent hooks cancel immediately; epoch adds a guard
                // against native playback changes while content is being fetched.
                await action(request.Token, () => Current() && playGeneration == playbackEpoch);
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
