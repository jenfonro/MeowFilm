using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private CancellationTokenSource? catalogActionCancellation;
    private long catalogActionGeneration;

    // Called synchronously at all user playback-intent/queue mutation boundaries.
    public void InvalidateCatalogAction()
    {
        catalogActionGeneration++;
        if (catalogActionCancellation is { IsCancellationRequested: false } active)
        {
            active.Cancel();
            if (status is "正在加载完整歌单" or "正在加载歌曲")
            { status = "已取消"; Changed(nameof(Status)); }
        }
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
    public Task SaveCatalogPlaylistAsync() => IsCatalogBatch ? CatalogBatchActionAsync("save", 0) : CatalogPlaylistActionAsync("save", 0);
    private Task CatalogPlaylistActionAsync(string action, int index, string? expectedSongId = null, string? memberId = null,
        MusicCatalogCard? entry = null)
    {
        if (entry is null ? !CanCatalogAction : !CanCatalogBrowse || !entry.CanPlay || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        var parts = entry is null ? location.Route.Split('/', 3) : new[] { "catalog-playlist", entry.Platform, Uri.EscapeDataString(entry.ActionId) };
        if (parts.Length != 3 || index < 0) return Task.CompletedTask;
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        var expectedSnapshot = entry is null ? catalogSnapshot : "";
        JsonElement? personal = entry?.Locator;
        if (entry is null && catalogLoadedTarget is { } loaded && loaded.Parameters.TryGetProperty("personal", out var locator)) personal = locator.Clone();
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            long version = 0;
            if (action == "play")
            {
                var queue = await Call("music.queue.get", new { offset = 0, limit = 1 }, signal);
                version = MusicDto.Integer(queue, "version");
            }
            var prepare = new Dictionary<string, object?> { ["platform"] = platform, ["id"] = id, ["refresh"] = true };
            if (personal is not null) prepare["personal"] = personal.Value;
            var prepared = await CallSettledReceipt("music.catalog.playlist.prepare", prepare, signal);
            var token = MusicDto.Text(prepared, "token");
            try
            {
                MusicDto.Require(MusicCatalogDto.Text(prepared, "platform") == platform &&
                    MusicCatalogDto.Text(prepared, "id") == id && MusicDto.Count(prepared, "count") <= 10000);
                if (action == "play" && entry is null && expectedSnapshot != MusicCatalogDto.Text(prepared, "snapshot"))
                    throw new MusicOperationException("SNAPSHOT_CHANGED");
                if (!currentAction()) return;
                await RunAsync(async () =>
                {
                    if (!currentAction()) return;
                    RequireCatalogContent();
                    var parameters = new Dictionary<string, object?> { ["token"] = token, ["action"] = action };
                    if (action == "play")
                    {
                        if (memberId is not null) parameters["memberId"] = memberId; else parameters["index"] = index;
                        parameters["expectedVersion"] = version;
                    }
                    if (expectedSongId is not null) parameters["expectedSongId"] = expectedSongId;
                    try
                    {
                        if (action == "play") queueEpoch++;
                        var result = await CallSettledReceipt("music.catalog.playlist.commit", parameters, signal);
                        if (action == "play")
                        {
                            MusicDto.Require(MusicDto.Integer(result, "version") > version);
                            await ReadQueue(lifetime.Token);
                            if (currentAction()) await StartCurrent(signal);
                        }
                        else
                        {
                            _ = PlaylistRow.Read(result.GetProperty("collection"));
                            await RefreshPlaylists(lifetime.Token);
                            if (currentAction()) { status = "已保存到本地歌单"; Changed(nameof(Status)); }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        if (action == "play") queueSaved = false;
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
        });
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
            MusicDto.Require(!catalogMore && songs.Length == total && songs.Length <= 1000);
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
                        if (currentAction()) await StartCurrent(signal);
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
    private Task PlayCatalogSongAsync(MusicCatalogCard card) =>
        PlayCatalogSongAsync(card.Platform, card.ActionId,
            () => ContainsCatalogCard(card), requireCatalogContent: true);

    private Task PlayCatalogSongAsync(string platform, string id, Func<bool> sourceCurrent,
        bool requireCatalogContent = false) => TrackCatalogAction(async (signal, currentAction) =>
    {
        if (requireCatalogContent) RequireCatalogContent();
        if (!sourceCurrent()) return;
        var reply = await Call("music.catalog.song", new { platform, id }, signal);
        if (!currentAction() || !sourceCurrent()) return;
        var song = SongRow.Read(reply.GetProperty("song"));
        MusicDto.Require(MusicCatalogDto.SongNativeId(song.Raw) == id &&
            MusicCatalogDto.Text(song.Raw.GetProperty("source_data"), "source") == platform);
        await RunAsync(async () =>
        {
            if (!currentAction() || !sourceCurrent()) return;
            if (requireCatalogContent) RequireCatalogContent();
            await QueueCall("music.queue.replace", new { songs = new[] { song.Raw }, index = 0,
                context = new { sourcePlatform = platform, sourceType = "song" } });
            await ReadQueue(lifetime.Token);
            if (currentAction() && sourceCurrent()) await StartCurrent(signal);
        });
    }, "正在加载歌曲");
    private void PublishCatalogActionError(Exception error)
    {
        string code = error is MusicOperationException e ? e.Code : error is OperationCanceledException ? "CANCELLED" : "INVALID_RESPONSE";
        if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "STALE_SESSION" or "SESSION_CHANGED" or "INVALID_CREDENTIALS")
            RetireCatalogSession();
        status = MusicCatalogDto.ErrorText(code); Changed(nameof(Status));
    }
    private Task TrackCatalogAction(Func<CancellationToken, Func<bool>, Task> action, string loadingStatus = "正在加载完整歌单")
    {
        if (stopping) return Task.CompletedTask;
        InvalidateCatalogAction();
        var generation = catalogActionGeneration; var locationGeneration = loadGeneration;
        var playGeneration = playbackEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        catalogActionCancellation = request;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        status = loadingStatus; Changed(nameof(Status)); CatalogBindings();
        return Execute();
        bool Current() => !stopping && !request.IsCancellationRequested && generation == catalogActionGeneration &&
            locationGeneration == loadGeneration;
        async Task Execute()
        {
            try
            {
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
                if (ReferenceEquals(catalogActionCancellation, request)) catalogActionCancellation = null;
                request.Dispose(); loadDrains.Remove(drained.Task); drained.TrySetResult();
                if (!disposed) { RefreshBindings(); CatalogBindings(); }
            }
        }
    }
}
