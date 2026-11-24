using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone Catalog service owning playlist/batch/section/card playback actions,
/// staged playlist commit/discard, and network playlist bookmark CRUD.
/// </summary>
internal sealed class MusicPlaylistDetailService
{
    private sealed class CatalogAction(CancellationTokenSource request, string platform, long observation)
    {
        internal readonly CancellationTokenSource Request = request;
        internal readonly string Platform = platform;
        internal readonly long Observation = observation;
        internal long? PlaybackEpoch;
    }

    private readonly MusicRuntimeContext ctx;
    private readonly MusicSettingsController settings;
    private readonly MusicAccountsController accounts;
    private readonly MusicPlaybackController playbackController;
    private readonly MusicQueueController queue;
    private readonly MusicCatalogBrowseService browse;
    private readonly MusicPersonalCatalogService personal;
    private readonly MusicCatalogActionState catalogActions;
    private readonly MusicContentState contentState;
    private readonly Action<string> notify;

    private CatalogAction? catalogAction;

    internal MusicPlaylistDetailService(
        MusicRuntimeContext ctx,
        MusicSettingsController settings,
        MusicAccountsController accounts,
        MusicPlaybackController playbackController,
        MusicQueueController queue,
        MusicCatalogBrowseService browse,
        MusicPersonalCatalogService personal,
        MusicCatalogActionState catalogActions,
        MusicContentState contentState,
        Action<string> notify)
    {
        this.ctx = ctx;
        this.settings = settings;
        this.accounts = accounts;
        this.playbackController = playbackController;
        this.queue = queue;
        this.browse = browse;
        this.personal = personal;
        this.catalogActions = catalogActions;
        this.contentState = contentState;
        this.notify = notify;

        catalogActions.Invalidated += OnActionInvalidated;
        browse.CatalogAccountObserved += OnCatalogAccountObserved;
    }

    // Explicit upward events wired at MusicCapability composition root.
    internal event Func<CancellationToken, Task>? PlaylistsChanged;
    internal event Func<SongRow, Task>? SongAppendNextRequested;
    internal event Func<SongRow, bool, Task>? SongFavoriteRequested;

    internal bool HasActiveAction => catalogAction is not null;

    internal bool CanCatalogAction => browse.CanCatalogBrowse && ctx.LoadCancellation is null && personal.IsCatalogCollection &&
        (!personal.IsDailyStyle || !personal.DailyStyleBusy && !personal.DailyStyleNeedsRead && personal.AccountStamp.Length > 0);
    internal bool CanPlayCatalogPlaylist => CanCatalogAction && contentState.Total > 0;

    // Network playlist bookmarking projection
    private PlaylistRow? CurrentNetworkPlaylist => browse.IsCatalogPlaylist
        ? contentState.NetworkPlaylists.FirstOrDefault(reference => reference.Route == ctx.CurrentRoute) : null;
    internal bool ShowsNetworkPlaylistAction => browse.IsCatalogPlaylist &&
        personal.LoadedTarget?.ManagedPlaylist != true &&
        personal.LoadedTarget?.Params.Personal is null;
    internal bool CanSaveNetworkPlaylist => CanCatalogAction && ShowsNetworkPlaylistAction;
    internal bool CanToggleNetworkPlaylist => ctx.CanEdit && ShowsNetworkPlaylistAction &&
        (NetworkPlaylistSaved || CanSaveNetworkPlaylist);
    internal bool NetworkPlaylistSaved => CurrentNetworkPlaylist is not null;
    internal string NetworkPlaylistActionText => NetworkPlaylistSaved ? "取消收藏" : "收藏";

    internal void NetworkPlaylistBindings()
    {
        notify(nameof(MusicCatalogController.ShowsNetworkPlaylistAction));
        notify(nameof(MusicCatalogController.CanSaveNetworkPlaylist));
        notify(nameof(MusicCatalogController.CanToggleNetworkPlaylist));
        notify(nameof(MusicCatalogController.NetworkPlaylistSaved));
        notify(nameof(MusicCatalogController.NetworkPlaylistActionText));
    }

    private void OnActionInvalidated()
    {
        var active = catalogAction; catalogAction = null;
        if (active?.Request is { IsCancellationRequested: false } request) request.Cancel();
        if (active is not null) notify(nameof(MusicCatalogController.CanCancelCatalog));
    }

    private void OnCatalogAccountObserved(string platform, long observation)
    {
        if (catalogAction is { } action && action.Platform == platform && action.Observation < observation)
            catalogActions.Invalidate();
    }

    internal Task OpenCatalogCardAsync(MusicCatalogCard card)
    {
        if (!browse.CanCatalogBrowse || card is null || !card.CanOpen || !browse.ContainsCatalogCard(card)) return Task.CompletedTask;
        if (MusicPersonalCatalogService.IsPrivateCatalogCard(card))
            return TrackCatalogAction(async (signal, current) =>
            {
                if (current()) await OpenCatalogCardCoreAsync(card);
            }, card: card);
        return OpenCatalogCardCoreAsync(card);
    }

    private Task OpenCatalogCardCoreAsync(MusicCatalogCard card)
    {
        var channelTab = browse.HasChannelTabs && browse.ChannelTabs.Any(item => ReferenceEquals(item, card));
        if (!channelTab)
        {
            try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
            catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        }
        if (card.Kind == "song" && card.Song is not null)
        {
            if (browse.IsCatalogPlaylist)
            {
                var songCards = browse.CatalogSections.SelectMany(s => s.Items).Where(item => item.Song is not null).ToArray();
                int index = Array.FindIndex(songCards, item => ReferenceEquals(item, card));
                if (index < 0) return Task.CompletedTask;
                int occurrence = songCards.Take(index).Count(item => SongJson.NativeId(item.Song!.Record) == card.MemberId);
                return CatalogPlaylistActionAsync("play", index, card.Song.Id,
                    card.MemberId.Length > 0 ? card.MemberId : null, memberOccurrence: occurrence);
            }
            if (personal.IsCatalogBatch)
            {
                var songCards = browse.CatalogSections.SelectMany(s => s.Items).Where(item => item.Song is not null).ToArray();
                int index = Array.FindIndex(songCards, item => ReferenceEquals(item, card));
                return index < 0 ? Task.CompletedTask : CatalogBatchActionAsync("play", index);
            }
            return PlayCatalogSectionAsync(card);
        }
        if (card.Kind == "song") return PlayCatalogSongAsync(card);
        if (card.Kind == "playlist" && card.Locator is null)
            return personal.OpenCatalogTargetAsync(
                MusicCatalogDispatcher.Target("playlist", card.Platform, card.Title, new(Id: card.ActionId))
                    with { ManagedPlaylist = card.IsManagedPlaylist, Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count },
                browse.RememberCatalogLocation, browse.RetireCatalogLocation, card.ActionId);
        if (card.Locator is not null || card.Kind is "daily" or "fm" or "similar" or "heartbeat" or "album" or "chart")
            return personal.OpenPersonalCatalogCardAsync(card, browse.IsCatalogHome, browse.RememberCatalogLocation, browse.RetireCatalogLocation);
        browse.RememberCatalogLocation();
        var destination = "catalog-" + card.Kind + "/" + card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
        browse.RegisterChannelRouteMeta(destination, card.PageCode, card.ChannelKind);
        return ctx.RequestNavigationAsync(destination);
    }

    internal Task PlayCatalogCardAsync(MusicCatalogCard card)
    {
        if (!browse.IsCatalogPage || !browse.IsCatalogHome && ctx.LoadCancellation is not null ||
            !browse.CanCatalogBrowse || !card.CanPlay || !browse.ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("play", 0, entry: card);
        if (card.Kind is not ("daily" or "album")) return OpenCatalogCardAsync(card);
        var cardSource = new MusicQueueSourceRecord(card.ContentKey, card.Title, card.Kind, card.Platform);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
            var reply = await MusicCatalogDispatcher.InvokeCardReadAsync(ctx.Application, card, previewLimit: false, signal);
            if (!currentAction()) return;
            var songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(s => s.Items)
                .Where(c => c.Song is not null).Select(c => c.Song!.Record).ToArray();
            MusicDto.Require(songs.Length > 0);
            await ctx.RunPlaybackActionAsync(async _ =>
            {
                if (!currentAction()) return;
                personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
                var queueSnapshot = await ctx.Application.GetQueueAsync(0, 1, signal);
                var version = queueSnapshot.Version;
                if (!currentAction()) return;
                try
                {
                    playbackController.AdvanceQueueEpoch();
                    var result = await ctx.Application.ReplaceQueueAsync(songs, 0, null, cardSource, ct: signal);
                    MusicDto.Require(result.Version > version);
                    await playbackController.ReadQueue(ctx.Lifetime.Token);
                }
                catch (OperationCanceledException) { return; }
                catch { playbackController.MarkQueueUnsaved(); throw; }
                if (currentAction()) await StartCatalogCurrent(signal);
            }, supersede: true);
        }, card: card);
    }

    internal Task FavoriteCatalogCardAsync(MusicCatalogCard card)
    {
        if (!ctx.CanEdit || !browse.ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return SaveNetworkPlaylistAsync(card);
        return FavoriteSongCardAsync(card, toggle: false);
    }

    internal Task ToggleCatalogCardFavoriteAsync(MusicCatalogCard card)
    {
        if (!ctx.CanEdit || !browse.ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist")
            return NetworkPlaylistForCard(card) is { } reference
                ? RemoveNetworkPlaylistAsync(reference)
                : SaveNetworkPlaylistAsync(card);
        return FavoriteSongCardAsync(card, toggle: true);
    }

    private Task FavoriteSongCardAsync(MusicCatalogCard card, bool toggle)
    {
        if (card.Song is null || !card.CanFavorite) return Task.CompletedTask;
        try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return SongFavoriteRequested?.Invoke(card.Song, toggle) ?? Task.CompletedTask;
    }

    internal Task SimilarCatalogCardAsync(MusicCatalogCard card)
    {
        if (!browse.CanCatalogBrowse || !browse.ContainsCatalogCard(card) || !card.CanSimilar) return Task.CompletedTask;
        try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return queue.StartRecommendation(MusicCatalogDispatcher.Target("similar", "wy", "相似歌曲",
            new(SourceIds: [Recommendations.RecommendationJson.NativeId(card.Song!.Record)], SourceType: "song")));
    }

    internal Task AlbumCatalogCardAsync(MusicCatalogCard card)
    {
        if (!browse.CanCatalogBrowse || !browse.ContainsCatalogCard(card) || !card.CanAlbum) return Task.CompletedTask;
        try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return personal.OpenCatalogTargetAsync(
            MusicCatalogDispatcher.Target("album", card.Platform, card.Song!.Album.Length > 0 ? card.Song.Album : "专辑",
                new(Id: card.AlbumId)) with
            {
                Artwork = card.Song.Cover.Length > 0 ? card.Song.Cover : card.Cover,
                Subtitle = card.Song.Artist
            },
            browse.RememberCatalogLocation, browse.RetireCatalogLocation, card.AlbumId);
    }

    internal Task QueueCatalogCardAsync(MusicCatalogCard card)
    {
        if (!ctx.CanEdit || !browse.ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("next", 0, entry: card);
        if (!card.CanFavorite || card.Song is not { } song) return Task.CompletedTask;
        try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return SongAppendNextRequested?.Invoke(song) ?? Task.CompletedTask;
    }

    internal Task HeartbeatFavoritesCatalogCardAsync(MusicCatalogCard card)
    {
        if (!browse.CanCatalogBrowse || !browse.ContainsCatalogCard(card) || !card.CanHeartbeatFavorites) return Task.CompletedTask;
        try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
        catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return queue.StartRecommendation(MusicCatalogDispatcher.Target("heartbeat", "wy", "心动模式", new(Entry: "favorites")));
    }

    internal Task CatalogQueueRecommendationsAsync()
    {
        if (!personal.CanCatalogWy) return Task.CompletedTask;
        return OpenQueueRecommendations();
        async Task OpenQueueRecommendations()
        {
            if (queue.HasQueueRecommendations) await queue.RetryRecommendationAsync();
            else await queue.RecommendCurrentAsync(playbackController.PlaybackPresentationKey);
            if (queue.HasQueueRecommendations) await ctx.OpenPlayQueueAsync();
        }
    }

    internal Task PlayCatalogPlaylistAsync()
    {
        if (CanCatalogAction && personal.LoadedTarget is { Operation: "fm" or "similar" or "heartbeat" } target)
        {
            try { personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform); }
            catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
            return queue.StartRecommendation(target);
        }
        return personal.IsCatalogBatch ? CatalogBatchActionAsync("play", 0) : CatalogPlaylistActionAsync("play", 0);
    }

    internal Task SaveCatalogPlaylistAsync() => personal.IsCatalogBatch ? CatalogBatchActionAsync("save", 0) : SaveNetworkPlaylistAsync();

    // Network playlists CRUD
    internal Task ToggleNetworkPlaylistAsync() => !CanToggleNetworkPlaylist ? Task.CompletedTask :
        CurrentNetworkPlaylist is { } reference ? RemoveNetworkPlaylistAsync(reference.Id) : SaveNetworkPlaylistAsync();

    internal PlaylistRow? NetworkPlaylistForCard(MusicCatalogCard card) =>
        contentState.NetworkPlaylists.FirstOrDefault(reference => reference.Platform == card.Platform && reference.RemoteId == card.ActionId);

    internal Task RemoveNetworkPlaylistAsync(string id)
    {
        var reference = contentState.NetworkPlaylists.FirstOrDefault(item => item.Id == id);
        return reference is null || !ctx.CanEdit ? Task.CompletedTask : RemoveNetworkPlaylistAsync(reference);
    }

    internal Task RemoveNetworkPlaylistAsync(PlaylistRow reference)
    {
        var origin = ctx.LoadGeneration;
        return ctx.RunAsync(async () =>
        {
            var receipt = await ctx.Application.RemoveNetworkPlaylistAsync(reference.Id, reference.Revision, ctx.Lifetime.Token);
            if (PlaylistsChanged is { } changed) await changed(ctx.Lifetime.Token);
            if (origin == ctx.LoadGeneration) PublishNetworkPlaylistReceipt(receipt, "已取消收藏");
        });
    }

    internal Task SaveNetworkPlaylistAsync(MusicCatalogCard? entry = null)
    {
        if (entry is null ? !CanSaveNetworkPlaylist :
            !browse.CanCatalogBrowse || !entry.CanSavePlaylist || !browse.ContainsCatalogCard(entry)) return Task.CompletedTask;
        string[] parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : ["catalog-playlist", entry.Platform, Uri.EscapeDataString(entry.ActionId)];
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        string title = entry?.Title ?? contentState.Heading, cover = entry?.Cover ?? browse.CatalogArtwork;
        int count = entry?.Count ?? (entry is null ? contentState.Total : 0);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
            await ctx.RunAsync(async () =>
            {
                if (!currentAction()) return;
                var receipt = await ctx.Application.SaveCatalogPlaylistAsync(platform, id, title, cover, count, signal);
                _ = PlaylistRow.FromSummary(receipt.Collection);
                if (PlaylistsChanged is { } changed) await changed(ctx.Lifetime.Token);
                if (currentAction()) PublishNetworkPlaylistReceipt(receipt, "已收藏歌单");
            });
        }, card: entry);
    }

    private void PublishNetworkPlaylistReceipt(MusicNetworkPlaylistMutationResult receipt, string success)
    {
        MusicDto.Require(receipt.LocalSaved);
        var sync = receipt.Sync;
        bool partial = sync.Phase == "partial";
        if (sync.Ok && !partial) ctx.SetStatus(success);
        else
        {
            var code = partial && !string.IsNullOrEmpty(sync.FirstIssueCode) ? sync.FirstIssueCode! : sync.Code;
            ctx.SetStatus(success + "，平台同步未完成：" + MusicRuntimeContext.ErrorText(new MusicOperationException(code)));
        }
        NetworkPlaylistBindings();
    }

    private Task CatalogPlaylistActionAsync(string action, int index, string? songId = null, string? memberId = null,
        MusicCatalogCard? entry = null, string? sourcePlaylistId = null, int memberOccurrence = 0)
    {
        if (entry is null ? !CanCatalogAction : !browse.CanCatalogBrowse || !entry.CanPlay || !browse.ContainsCatalogCard(entry)) return Task.CompletedTask;
        var parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : new[] { "catalog-playlist", entry.Platform, Uri.EscapeDataString(sourcePlaylistId ?? entry.ActionId) };
        if (parts.Length != 3 || index < 0) return Task.CompletedTask;
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        JsonElement? personalLocator = entry?.Locator;
        if (entry is null && personal.LoadedTarget?.Params.Personal is { } locator) personalLocator = locator.Clone();
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
            var queueSnapshot = await ctx.Application.GetQueueAsync(0, 1, signal);
            long version = queueSnapshot.Version;
            var prepared = await ctx.Application.PrepareCatalogPlaylistAsync(platform, id, refresh: true, personal: personalLocator, ct: signal);
            var token = prepared.Token;
            try
            {
                if (!currentAction()) return;
                Func<Task> commit = async () =>
                {
                    if (!currentAction()) return;
                    personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
                    bool queueUpdated = false;
                    try
                    {
                        playbackController.AdvanceQueueEpoch();
                        var result = await ctx.Application.CommitCatalogPlaylistAsync(
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
                        await playbackController.ReadQueue(ctx.Lifetime.Token);
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
                    await ctx.RunPlaybackActionAsync(_ => commit(), supersede: true);
                else
                    await ctx.RunAsync(commit);
            }
            finally
            {
                try { await ctx.Application.DiscardCatalogPlaylistAsync(token, ctx.Lifetime.Token); }
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
        try { await playbackController.ReadQueue(ctx.Lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicCatalog, "catalog-actions", "recover-next-queue-failed",
                new { error = error.Message });
        }
    }

    internal MusicPlayContextRecord? CaptureCatalogFeedbackContext()
    {
        if (!browse.IsCatalogPage || browse.CatalogLoadedPlatform is not ("wy" or "tx" or "kg"))
            return null;
        string? sourceType, sourceId = null, dailyBranch = null, searchKeyword = null;
        if (personal.LoadedTarget is { } target)
        {
            sourceType = target.Operation;
            if (target.Params.Id is { Length: > 0 } sid)
                sourceId = sid;
            if (target.Operation == "daily" && target.Params.Branch is { Length: > 0 } db)
                dailyBranch = db;
        }
        else if (ctx.CurrentRoute == "catalog-search" && browse.CatalogQuery.Length > 0)
        {
            sourceType = "search";
            searchKeyword = browse.CatalogQuery;
        }
        else sourceType = "song";
        return MusicPlayContextRecord.Empty with
        {
            SourcePlatform = browse.CatalogLoadedPlatform,
            SourceType = sourceType,
            SourceId = sourceId,
            DailyBranch = dailyBranch,
            SearchKeyword = searchKeyword
        };
    }

    private Task CatalogBatchActionAsync(string action, int index)
    {
        var activeSongs = ctx.ActiveSongs;
        if (!CanCatalogAction || !personal.IsCatalogBatch || action == "play" && (index < 0 || index >= activeSongs.Count)) return Task.CompletedTask;
        var songs = activeSongs.Select(s => s.Record).ToArray();
        var title = contentState.Heading; var route = ctx.CurrentRoute; var platform = browse.CatalogLoadedPlatform;
        var kind = personal.LoadedTarget!.Operation;
        var capturedContext = CaptureCatalogFeedbackContext();
        var playContext = (capturedContext ?? MusicPlayContextRecord.Empty) with { SourceTitle = title };
        var queueSource = new MusicQueueSourceRecord(route, title, kind, platform);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
            MusicDto.Require(songs.Length > 0);
            long version = 0;
            if (action == "play") version = (await ctx.Application.GetQueueAsync(0, 1, signal)).Version;
            if (!currentAction()) return;
            Func<Task> batch = async () =>
            {
                if (!currentAction()) return;
                personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
                bool queueUpdated = false;
                try
                {
                    if (action == "play")
                    {
                        playbackController.AdvanceQueueEpoch();
                        var result = await ctx.Application.ReplaceQueueAsync(songs, index, playContext, queueSource, ct: signal);
                        MusicDto.Require(result.Version > version);
                        await playbackController.ReadQueue(ctx.Lifetime.Token);
                        queueUpdated = true;
                        if (currentAction())
                            await StartCatalogCurrent(signal);
                    }
                    else
                    {
                        var result = await ctx.Application.CreatePlaylistAsync(title, songs, signal);
                        _ = PlaylistRow.FromSummary(result);
                        if (PlaylistsChanged is { } changed) await changed(ctx.Lifetime.Token);
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
                await ctx.RunPlaybackActionAsync(_ => batch(), supersede: true);
            else
                await ctx.RunAsync(batch);
        });
    }

    private Task PlayCatalogSectionAsync(MusicCatalogCard card)
    {
        var section = browse.CatalogSections.FirstOrDefault(s => s.Items.Any(item => ReferenceEquals(item, card)));
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
            personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
            await ctx.RunPlaybackActionAsync(async _ =>
            {
                if (!current()) return;
                personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
                playbackController.AdvanceQueueEpoch();
                try
                {
                    await ctx.Application.ReplaceQueueAsync(songs, index, null, sectionSource, ct: signal);
                }
                catch { playbackController.MarkQueueUnsaved(); throw; }
                await playbackController.ReadQueue(ctx.Lifetime.Token);
                if (current())
                    await StartCatalogCurrent(signal);
            }, supersede: true);
        }, card: card);
    }

    private Task CatalogSectionPlaylistAsync(MusicCatalogCard card, string playlistId)
    {
        var section = browse.CatalogSections.First(s => s.Items.Any(item => ReferenceEquals(item, card)));
        int occurrence = section.Items.TakeWhile(item => !ReferenceEquals(item, card))
            .Count(item => item.Song?.Id == card.Song!.Id);
        return CatalogPlaylistActionAsync("play", 0, card.Song!.Id, entry: card,
            sourcePlaylistId: playlistId, memberOccurrence: occurrence);
    }

    private Task PlayCatalogSongAsync(MusicCatalogCard card)
    {
        bool fromHome = browse.IsCatalogHome;
        long navigation = ctx.NavigationSubmission;
        return PlayCatalogSongAsync(card.Platform, card.ActionId,
            () => fromHome ? browse.IsCatalogHome && navigation == ctx.NavigationSubmission && card.Platform == settings.CatalogSourcePlatform :
                browse.ContainsCatalogCard(card), requireCatalogContent: true, card: card);
    }

    internal Task PlayCatalogSongAsync(string platform, string id, Func<bool> sourceCurrent,
        bool requireCatalogContent = false, MusicCatalogCard? card = null) => TrackCatalogAction(async (signal, currentAction) =>
    {
        if (requireCatalogContent) personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
        if (!sourceCurrent()) return;
        var record = await ctx.Application.GetCatalogSongAsync(platform, id, ct: signal);
        if (!currentAction() || !sourceCurrent()) return;
        var song = SongRow.FromRecord(record);
        var songContext = MusicPlayContextRecord.Empty with { SourcePlatform = platform, SourceType = "song" };
        await ctx.RunPlaybackActionAsync(async _ =>
        {
            if (!currentAction() || !sourceCurrent()) return;
            if (requireCatalogContent) personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
            await playbackController.QueueCallAsync(
                ct => ctx.Application.ReplaceQueueAsync([song.Record], 0, songContext, ct: ct),
                clearsSearchRecommendation: true);
            await playbackController.ReadQueue(ctx.Lifetime.Token);
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
        string code = error is MusicOperationException e ? e.Code : error is OperationCanceledException ? "CANCELLED" : "INVALID_RESPONSE";
        if (!browse.IsCatalogHome && code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "SESSION_CHANGED")
            browse.RetireCatalogSession();
        ctx.SetStatus(MusicCatalogDto.ErrorText(code));
        if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED")
        {
            browse.PublishCatalogAccountNotice(code);
            browse.RecoverHomeAfterAuthenticationFailure();
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
        catalogActions.ClearPreparingCard();
        notify(nameof(MusicCatalogController.CanCancelCatalog));
    }

    private Task TrackCatalogAction(Func<CancellationToken, Func<bool>, Task> action, string? loadingStatus = null, MusicCatalogCard? card = null)
    {
        if (ctx.Stopping) return Task.CompletedTask;
        browse.ClearCatalogAccountNotice(); ctx.NotifyStatusChanged();
        catalogActions.Invalidate();
        var generation = catalogActions.Generation; var locationGeneration = ctx.LoadGeneration;
        bool fromHome = browse.IsCatalogHome;
        var navigation = ctx.NavigationSubmission;
        var actionPlatform = card?.Platform ?? browse.CatalogLoadedPlatform;
        var observation = accounts.PlatformObservation(actionPlatform);
        var playGeneration = playbackController.PlaybackEpoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
        var pending = new CatalogAction(request, actionPlatform, observation);
        catalogAction = pending;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.LoadDrains.Add(drained.Task);
        catalogActions.SetPreparingCard(card);
        if (loadingStatus is not null) ctx.SetStatus(loadingStatus);
        notify(nameof(MusicCatalogController.CanCancelCatalog));
        return Execute();
        bool Current() => !ctx.Stopping && !request.IsCancellationRequested && generation == catalogActions.Generation &&
            observation == accounts.PlatformObservation(actionPlatform) &&
            (fromHome ? browse.IsCatalogHome && navigation == ctx.NavigationSubmission : locationGeneration == ctx.LoadGeneration);
        async Task Execute()
        {
            try
            {
                if (MusicPersonalCatalogService.IsPrivateCatalogCard(card))
                {
                    await accounts.EnsureAccountsForRequestAsync(request.Token);
                    if (!Current()) return;
                    personal.RequireCatalogAccount(card);
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
                    if (generation == catalogActions.Generation) catalogActions.ClearPreparingCard();
                }
                request.Dispose(); ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult();
                if (!ctx.Disposed) notify(nameof(MusicCatalogController.CanCancelCatalog));
            }
        }
    }
}
