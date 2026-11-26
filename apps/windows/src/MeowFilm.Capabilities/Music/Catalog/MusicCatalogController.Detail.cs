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

    public bool CanCatalogAction => CanCatalogBrowse && ctx.LoadCancellation is null && IsCatalogCollection &&
        (!IsDailyStyle || !dailyStyleBusy && !dailyStyleNeedsRead && catalogAccountStamp.Length > 0);
    public bool CanPlayCatalogPlaylist => CanCatalogAction && catalogTotal > 0;

    private PlaylistRow? CurrentNetworkPlaylist => IsCatalogPlaylist
        ? library.NetworkPlaylists.FirstOrDefault(reference => reference.Route == ctx.CurrentRoute) : null;
    public bool ShowsNetworkPlaylistAction => IsCatalogPlaylist &&
        catalogLoadedTarget?.ManagedPlaylist != true &&
        catalogLoadedTarget?.Params.Personal is null;
    public bool CanSaveNetworkPlaylist => CanCatalogAction && ShowsNetworkPlaylistAction;
    public bool CanToggleNetworkPlaylist => CanEdit && ShowsNetworkPlaylistAction &&
        (NetworkPlaylistSaved || CanSaveNetworkPlaylist);
    public bool NetworkPlaylistSaved => CurrentNetworkPlaylist is not null;
    public string NetworkPlaylistActionText => NetworkPlaylistSaved ? "取消收藏" : "收藏";

    internal void NetworkPlaylistBindings()
    {
        Changed(nameof(ShowsNetworkPlaylistAction));
        Changed(nameof(CanSaveNetworkPlaylist));
        Changed(nameof(CanToggleNetworkPlaylist));
        Changed(nameof(NetworkPlaylistSaved));
        Changed(nameof(NetworkPlaylistActionText));
    }

    private void OnActionInvalidated()
    {
        var active = catalogAction; catalogAction = null;
        if (active?.Request is { IsCancellationRequested: false } request) request.Cancel();
        if (active is not null) Changed(nameof(CanCancelCatalog));
    }

    public Task OpenCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || card is null || !card.CanOpen || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (IsPrivateCatalogCard(card))
            return TrackCatalogAction(async (signal, current) =>
            {
                if (current()) await OpenCatalogCardCoreAsync(card);
            }, card: card);
        return OpenCatalogCardCoreAsync(card);
    }

    private Task OpenCatalogCardCoreAsync(MusicCatalogCard card)
    {
        var channelTab = HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card));
        if (!channelTab)
        {
            try { RequireCatalogContent(); }
            catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        }
        if (card.Kind == "song" && card.Song is not null)
        {
            if (IsCatalogPlaylist)
            {
                var songCards = CatalogSections.SelectMany(s => s.Items).Where(item => item.Song is not null).ToArray();
                int index = Array.FindIndex(songCards, item => ReferenceEquals(item, card));
                if (index < 0) return Task.CompletedTask;
                int occurrence = songCards.Take(index).Count(item => SongJson.NativeId(item.Song!.Record) == card.MemberId);
                return CatalogPlaylistActionAsync("play", index, card.Song.Id,
                    card.MemberId.Length > 0 ? card.MemberId : null, memberOccurrence: occurrence);
            }
            if (IsCatalogBatch)
            {
                var songCards = CatalogSections.SelectMany(s => s.Items).Where(item => item.Song is not null).ToArray();
                int index = Array.FindIndex(songCards, item => ReferenceEquals(item, card));
                return index < 0 ? Task.CompletedTask : CatalogBatchActionAsync("play", index);
            }
            return PlayCatalogSectionAsync(card);
        }
        if (card.Kind == "song") return PlayCatalogSongAsync(card);
        if (card.Kind == "playlist" && card.Locator is null)
            return OpenCatalogTargetAsync(
                MusicCatalogDispatcher.Target("playlist", card.Platform, card.Title, new(Id: card.ActionId))
                    with { ManagedPlaylist = card.IsManagedPlaylist, Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count },
                card.ActionId);
        if (card.Locator is not null || card.Kind is "daily" or "fm" or "similar" or "heartbeat" or "album" or "chart")
            return OpenPersonalCatalogCardAsync(card);
        RememberCatalogLocation();
        var destination = "catalog-" + card.Kind + "/" + card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
        RegisterChannelRouteMeta(destination, card.PageCode, card.ChannelKind);
        return RequestNavigationAsync(destination);
    }

    public Task PlayCatalogCardAsync(MusicCatalogCard card)
    {
        if (!IsCatalogPage || !IsCatalogHome && ctx.LoadCancellation is not null ||
            !CanCatalogBrowse || !card.CanPlay || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("play", 0, entry: card);
        if (card.Kind is not ("daily" or "album")) return OpenCatalogCardAsync(card);
        var cardSource = new MusicQueueSourceRecord(card.ContentKey, card.Title, card.Kind, card.Platform);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var reply = await MusicCatalogDispatcher.InvokeCardReadAsync(application.Catalog, card, previewLimit: false, signal);
            if (!currentAction()) return;
            var songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(s => s.Items)
                .Where(c => c.Song is not null).Select(c => c.Song!.Record).ToArray();
            if (songs.Length == 0) throw new MusicOperationException("EMPTY_COLLECTION");
            await RunPlaybackActionAsync(async _ =>
            {
                if (!currentAction()) return;
                RequireCatalogContent();
                try
                {
                    playbackController.AdvanceQueueEpoch();
                    await application.Library.ReplaceQueueAsync(songs, 0, null, cardSource, cancellationToken: signal);
                    await playbackController.ReadQueue(lifetime.Token);
                }
                catch (OperationCanceledException) { return; }
                catch { playbackController.MarkQueueUnsaved(); throw; }
                if (currentAction()) await StartCatalogCurrent(signal);
            }, supersede: true);
        }, card: card);
    }

    public Task FavoriteCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return SaveNetworkPlaylistAsync(card);
        return FavoriteSongCardAsync(card, toggle: false);
    }

    public Task ToggleCatalogCardFavoriteAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist")
            return NetworkPlaylistForCard(card) is { } reference
                ? RemoveNetworkPlaylistAsync(reference)
                : SaveNetworkPlaylistAsync(card);
        return FavoriteSongCardAsync(card, toggle: true);
    }

    private Task FavoriteSongCardAsync(MusicCatalogCard card, bool toggle)
    {
        if (card.Song is null || !card.CanFavorite) return Task.CompletedTask;
        try { RequireCatalogContent(); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        if (IsDailyStyle && !CatalogSongsSource.Any(row => ReferenceEquals(row, card.Song))) return Task.CompletedTask;
        return toggle ? library.ToggleSongFavoriteAsync(card.Song) : library.FavoriteSongAsync(card.Song);
    }

    public Task SimilarCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanSimilar) return Task.CompletedTask;
        try { RequireCatalogContent(); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return queue.StartRecommendation(MusicCatalogDispatcher.Target("similar", "wy", "相似歌曲",
            new(SourceIds: [Recommendations.RecommendationJson.NativeId(card.Song!.Record)], SourceType: "song")));
    }

    public Task AlbumCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanAlbum) return Task.CompletedTask;
        try { RequireCatalogContent(); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return OpenCatalogTargetAsync(
            MusicCatalogDispatcher.Target("album", card.Platform, card.Song!.Album.Length > 0 ? card.Song.Album : "专辑",
                new(Id: card.AlbumId)) with
            {
                Artwork = card.Song.Cover.Length > 0 ? card.Song.Cover : card.Cover,
                Subtitle = card.Song.Artist
            },
            card.AlbumId);
    }

    public Task QueueCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("next", 0, entry: card);
        if (!card.CanFavorite || card.Song is not { } song) return Task.CompletedTask;
        try { RequireCatalogContent(); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        if (IsDailyStyle && !CatalogSongsSource.Any(row => ReferenceEquals(row, song))) return Task.CompletedTask;
        return library.AppendSongNextAsync(song, CaptureCatalogFeedbackContext());
    }

    public Task HeartbeatFavoritesCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanHeartbeatFavorites) return Task.CompletedTask;
        try { RequireCatalogContent(); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return queue.StartRecommendation(MusicCatalogDispatcher.Target("heartbeat", "wy", "心动模式", new(Entry: "favorites")));
    }

    public Task CatalogQueueRecommendationsAsync()
    {
        if (!CanCatalogWy) return Task.CompletedTask;
        return OpenQueueRecommendations();
        async Task OpenQueueRecommendations()
        {
            if (queue.HasQueueRecommendations) await queue.RetryRecommendationAsync();
            else await queue.RecommendCurrentAsync(playbackController.PlaybackPresentationKey);
            if (queue.HasQueueRecommendations) await ctx.OpenPlayQueueAsync();
        }
    }

    public Task PlayCatalogPlaylistAsync()
    {
        if (CanCatalogAction && catalogLoadedTarget is { Operation: "fm" or "similar" or "heartbeat" } target)
        {
            try { RequireCatalogContent(); }
            catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
            return queue.StartRecommendation(target);
        }
        return IsCatalogBatch ? CatalogBatchActionAsync("play", 0) : CatalogPlaylistActionAsync("play", 0);
    }

    public Task PlaySelectedCatalogSongAsync()
    {
        var song = catalogSelectedSong;
        var card = song is null ? null : CatalogSections.SelectMany(section => section.Items)
            .FirstOrDefault(item => ReferenceEquals(item.Song, song));
        return card is null ? Task.CompletedTask : OpenCatalogCardAsync(card);
    }

    public Task SaveCatalogPlaylistAsync() => IsCatalogBatch ? CatalogBatchActionAsync("save", 0) : SaveNetworkPlaylistAsync();

    public Task ToggleNetworkPlaylistAsync() => !CanToggleNetworkPlaylist ? Task.CompletedTask :
        CurrentNetworkPlaylist is { } reference ? RemoveNetworkPlaylistAsync(reference.Id) : SaveNetworkPlaylistAsync();

    internal PlaylistRow? NetworkPlaylistForCard(MusicCatalogCard card) =>
        library.NetworkPlaylists.FirstOrDefault(reference => reference.Platform == card.Platform && reference.RemoteId == card.ActionId);

    public Task RemoveNetworkPlaylistAsync(string id)
    {
        var reference = library.NetworkPlaylists.FirstOrDefault(item => item.Id == id);
        return reference is null || !CanEdit ? Task.CompletedTask : RemoveNetworkPlaylistAsync(reference);
    }

    internal Task RemoveNetworkPlaylistAsync(PlaylistRow reference)
    {
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            var receipt = await application.Library.RemoveNetworkPlaylistAsync(reference.Id, reference.Revision, lifetime.Token);
            await library.RefreshPlaylists(lifetime.Token);
            if (origin == ctx.LoadGeneration) PublishNetworkPlaylistReceipt(receipt, "已取消收藏");
        });
    }

    internal Task SaveNetworkPlaylistAsync(MusicCatalogCard? entry = null)
    {
        if (entry is null ? !CanSaveNetworkPlaylist :
            !CanCatalogBrowse || !entry.CanSavePlaylist || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        string[] parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : ["catalog-playlist", entry.Platform, Uri.EscapeDataString(entry.ActionId)];
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        string title = entry?.Title ?? catalogHeading, cover = entry?.Cover ?? catalogArtwork;
        int count = entry?.Count ?? (entry is null ? catalogTotal : 0);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            await RunAsync(async () =>
            {
                if (!currentAction()) return;
                var receipt = await application.Catalog.SavePlaylistAsync(platform, id, title, cover, count, signal);
                await library.RefreshPlaylists(lifetime.Token);
                if (currentAction()) PublishNetworkPlaylistReceipt(receipt, "已收藏歌单");
            });
        }, card: entry);
    }

    private void PublishNetworkPlaylistReceipt(MusicNetworkPlaylistMutationResult receipt, string success)
    {
        var sync = receipt.Sync;
        bool partial = sync.Phase == "partial";
        if (sync.Ok && !partial) ctx.SetStatus(success);
        else
        {
            var code = partial && !string.IsNullOrEmpty(sync.FirstIssueCode) ? sync.FirstIssueCode! : sync.Code;
            ctx.SetStatus(success + "，平台同步未完成：" + ErrorText(new MusicOperationException(code)));
        }
        NetworkPlaylistBindings();
    }

    private Task CatalogPlaylistActionAsync(string action, int index, string? songId = null, string? memberId = null,
        MusicCatalogCard? entry = null, string? sourcePlaylistId = null, int memberOccurrence = 0)
    {
        if (entry is null ? !CanCatalogAction : !CanCatalogBrowse || !entry.CanPlay || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        var parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : new[] { "catalog-playlist", entry.Platform, Uri.EscapeDataString(sourcePlaylistId ?? entry.ActionId) };
        if (parts.Length != 3 || index < 0) return Task.CompletedTask;
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        JsonElement? personalLocator = entry?.Locator;
        if (entry is null && catalogLoadedTarget?.Params.Personal is { } locator) personalLocator = locator.Clone();
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var queueSnapshot = await application.Library.GetQueueAsync(0, 1, signal);
            long version = queueSnapshot.Version;
            var prepared = await application.Catalog.PreparePlaylistAsync(platform, id, refresh: true, personal: personalLocator, cancellationToken: signal);
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
                        var result = await application.Catalog.CommitPlaylistAsync(
                            token,
                            action,
                            version,
                            index: action == "play" && memberId is null && songId is null && sourcePlaylistId is null ? index : 0,
                            memberId: action == "play" ? memberId : null,
                            songId: action == "play" && memberId is null ? songId : null,
                            memberOccurrence: action == "play" && (memberId is not null || songId is not null) ? memberOccurrence : 0,
                            cancellationToken: signal);
                        if (action == "next")
                        {
                            playbackController.ObservePreloadQueueMutation(result);
                            queue.InvalidateQueueSurface();
                        }
                        await playbackController.ReadQueue(lifetime.Token);
                        queueUpdated = true;
                        if (action == "play" && currentAction())
                            await StartCatalogCurrent(signal);
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
                try { await application.Catalog.DiscardPlaylistAsync(token, lifetime.Token); }
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

    internal MusicPlayContextRecord? CaptureCatalogFeedbackContext()
    {
        if (!IsCatalogPage || catalogLoadedPlatform is not ("wy" or "tx" or "kg"))
            return null;
        string? sourceType, sourceId = null, dailyBranch = null, searchKeyword = null;
        if (catalogLoadedTarget is { } target)
        {
            sourceType = target.Operation;
            if (target.Params.Id is { Length: > 0 } sid) sourceId = sid;
            if (target.Operation == "daily" && target.Params.Branch is { Length: > 0 } db) dailyBranch = db;
        }
        else if (ctx.CurrentRoute == "catalog-search" && catalogQuery.Length > 0)
        {
            sourceType = "search";
            searchKeyword = catalogQuery;
        }
        else sourceType = "song";
        return MusicPlayContextRecord.Empty with
        {
            SourcePlatform = catalogLoadedPlatform,
            SourceType = sourceType,
            SourceId = sourceId,
            DailyBranch = dailyBranch,
            SearchKeyword = searchKeyword
        };
    }

    private Task CatalogBatchActionAsync(string action, int index)
    {
        var activeSongs = CatalogSongsSource;
        if (!CanCatalogAction || !IsCatalogBatch || action == "play" && (index < 0 || index >= activeSongs.Count)) return Task.CompletedTask;
        var songs = activeSongs.Select(s => s.Record).ToArray();
        var title = catalogHeading; var route = ctx.CurrentRoute; var platform = catalogLoadedPlatform;
        var kind = catalogLoadedTarget!.Operation;
        var capturedContext = CaptureCatalogFeedbackContext();
        var playContext = (capturedContext ?? MusicPlayContextRecord.Empty) with { SourceTitle = title };
        var queueSource = new MusicQueueSourceRecord(route, title, kind, platform);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            if (songs.Length == 0) throw new MusicOperationException("EMPTY_COLLECTION");
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
                        await application.Library.ReplaceQueueAsync(songs, index, playContext, queueSource, cancellationToken: signal);
                        await playbackController.ReadQueue(lifetime.Token);
                        queueUpdated = true;
                        if (currentAction())
                            await StartCatalogCurrent(signal);
                    }
                    else
                    {
                        await application.Library.CreatePlaylistAsync(title, songs, signal);
                        await library.RefreshPlaylists(lifetime.Token);
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
                try { await application.Library.ReplaceQueueAsync(songs, index, null, sectionSource, cancellationToken: signal); }
                catch { playbackController.MarkQueueUnsaved(); throw; }
                await playbackController.ReadQueue(lifetime.Token);
                if (current()) await StartCatalogCurrent(signal);
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
        var record = await application.Catalog.SongAsync(platform, id, cancellationToken: signal);
        if (!currentAction() || !sourceCurrent()) return;
        var song = SongRow.FromRecord(record);
        var songContext = MusicPlayContextRecord.Empty with { SourcePlatform = platform, SourceType = "song" };
        await RunPlaybackActionAsync(async _ =>
        {
            if (!currentAction() || !sourceCurrent()) return;
            if (requireCatalogContent) RequireCatalogContent();
            await playbackController.QueueCallAsync(
                ct => application.Library.ReplaceQueueAsync([song.Record], 0, songContext, cancellationToken: ct),
                clearsSearchRecommendation: true);
            await playbackController.ReadQueue(lifetime.Token);
            if (currentAction() && sourceCurrent())
                await StartCatalogCurrent(signal);
        }, supersede: true);
    }, card is null ? "正在加载歌曲" : null, card);

    internal Task CatalogBusinessError(string code)
    {
        ctx.SetStatus(MusicCatalogDto.ErrorText(code));
        return Task.CompletedTask;
    }

    internal void PublishCatalogActionError(Exception error)
    {
        string code = ErrorCode(error) ?? "INVALID_RESPONSE";
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
        catalogAction = null;
        ClearPreparingCard();
        Changed(nameof(CanCancelCatalog));
    }

    private Task TrackCatalogAction(Func<CancellationToken, Func<bool>, Task> action, string? loadingStatus = null, MusicCatalogCard? card = null)
    {
        if (stopping) return Task.CompletedTask;
        ClearCatalogAccountNotice(); ctx.NotifyStatusChanged();
        InvalidateCatalogAction();
        var generation = actionGeneration; var locationGeneration = ctx.LoadGeneration;
        bool fromHome = IsCatalogHome;
        var navigation = ctx.NavigationSubmission;
        var actionPlatform = card?.Platform ?? catalogLoadedPlatform;
        var observation = accounts.PlatformObservation(actionPlatform);
        var playGeneration = playbackController.PlaybackEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var pending = new CatalogAction(request, actionPlatform, observation);
        catalogAction = pending;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        SetPreparingCard(card);
        if (loadingStatus is not null) ctx.SetStatus(loadingStatus);
        Changed(nameof(CanCancelCatalog));
        return Execute();
        bool Current() => !stopping && !request.IsCancellationRequested && generation == actionGeneration &&
            observation == accounts.PlatformObservation(actionPlatform) &&
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
                    if (generation == actionGeneration) ClearPreparingCard();
                }
                request.Dispose(); loadDrains.Remove(drained.Task); drained.TrySetResult();
                if (!disposed) Changed(nameof(CanCancelCatalog));
            }
        }
    }
}
