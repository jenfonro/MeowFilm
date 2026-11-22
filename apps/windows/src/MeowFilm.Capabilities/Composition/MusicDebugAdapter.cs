using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Standalone debug control adapter for the music capability.</summary>
internal sealed class MusicDebugAdapter(MusicCapability capability) : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> MusicTargets =
    [
        "music.home", "music.channels", "music.discovery", "music.personal", "music.daily", "music.fm",
        "music.library", "music.favorites", "music.history", "music.archives", "music.playlist",
        "music.player", "music.player.detail", "music.queue", "settings.music"
    ];

    public string DebugModuleId => capability.Id;
    public IReadOnlyList<string> SupportedDebugTargets => MusicTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        if (settingsOpen)
        {
            settingsSection = capability.Presentation.Settings.CurrentSectionKey;
            return "settings.music";
        }
        settingsSection = null;
        if (capability.Search.IsSearchPage) return "search";
        var route = capability.CurrentRoute;
        if (route == "catalog-home") return "music.home";
        if (route == "catalog-channels" || route.StartsWith("catalog-channel/", StringComparison.Ordinal)) return "music.channels";
        if (route == "catalog-discovery") return "music.discovery";
        if (route == "catalog-personal") return "music.personal";
        if (route == "catalog-daily") return "music.daily";
        if (route == "catalog-fm") return "music.fm";
        if (route == "favorites") return "music.favorites";
        if (route == "history") return "music.history";
        if (route == "archives") return "music.archives";
        if (route.StartsWith("playlist/", StringComparison.Ordinal) || route.StartsWith("catalog-playlist/", StringComparison.Ordinal))
            return "music.playlist";
        return "music." + route;
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.MusicCatalog, "debug.nav", "open.target", new { target, @params = parameters });
        switch (target)
        {
            case "music.home":
                await capability.NavigateAsync("catalog-home", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.channels":
            {
                var key = GetStr(parameters, "channelKey");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var route = key.StartsWith("catalog-channel/", StringComparison.Ordinal) ? key : "catalog-channel/" + key;
                    await capability.NavigateAsync(route, cancellationToken);
                    return true;
                }
                await capability.NavigateAsync("catalog-channels", cancellationToken);
                if (GetInt(parameters, "channelIndex") is { } idx)
                {
                    if (idx < 0 || idx >= capability.Catalog.ChannelTabs.Count)
                        throw DebugApiException.InvalidArgument($"channelIndex {idx} out of range (0..{Math.Max(0, capability.Catalog.ChannelTabs.Count - 1)}).");
                    await capability.Catalog.OpenCatalogCardAsync(capability.Catalog.ChannelTabs[idx]);
                }
                return true;
            }
            case "music.discovery":
                await capability.NavigateAsync("catalog-discovery", cancellationToken);
                return true;
            case "music.personal":
                await capability.NavigateAsync("catalog-personal", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.daily":
                await capability.NavigateAsync("catalog-daily", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.fm":
                await capability.NavigateAsync("catalog-fm", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.library":
            case "music.favorites":
                await capability.NavigateAsync("favorites", cancellationToken);
                return true;
            case "music.history":
                await capability.NavigateAsync("history", cancellationToken);
                if (GetStr(parameters, "date") is { Length: > 0 } date)
                    await capability.Library.SelectHistoryDateAsync(date);
                return true;
            case "music.archives":
                await capability.NavigateAsync("archives", cancellationToken);
                return true;
            case "music.playlist":
            {
                var id = GetStr(parameters, "id");
                var platform = GetStr(parameters, "platform");
                var remoteId = GetStr(parameters, "remoteId");
                if (!string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(remoteId))
                {
                    await capability.NavigateAsync("catalog-playlist/" + platform + "/" + Uri.EscapeDataString(remoteId), cancellationToken);
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    await capability.Library.OpenPlaylistAsync(id);
                    return true;
                }
                var first = capability.Library.Playlists.FirstOrDefault() ?? capability.Library.NetworkPlaylists.FirstOrDefault();
                if (first is null) throw DebugApiException.InvalidState("No playlist available to open.");
                await capability.NavigateAsync(first.Route, cancellationToken);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task ApplyCatalogPlatformParamAsync(JsonElement? parameters)
    {
        if (GetStr(parameters, "platform") is not { Length: > 0 } platformKey) return;
        var p = capability.Catalog.CatalogPlatforms.FirstOrDefault(x => string.Equals(x.Key, platformKey, StringComparison.OrdinalIgnoreCase));
        if (p is null) throw DebugApiException.NotFound($"Music catalog platform not found: '{platformKey}'.");
        await capability.Catalog.ChangeCatalogPlatformAsync(p);
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        Task.FromResult(capability.Presentation.Settings.SelectDebugSection(section));

    public Task<object?> BuildDebugOverlayContentAsync(string target, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        var lyrics = capability.Lyrics;
        var playback = capability.Playback;
        var queue = capability.Queue;
        if (target is "music.player" or "music.player.detail")
        {
            var lyricRows = lyrics.Lyrics.Take(maxItems).Select((row, idx) => new
            {
                index = idx,
                id = idx.ToString(System.Globalization.CultureInfo.InvariantCulture),
                title = row.Text,
                subtitle = row.Translation,
                timeSec = Math.Round(row.Time, 3),
                selected = idx == lyrics.LyricDisplayIndex,
                state = idx == lyrics.ActiveLyricIndex ? "active" : "idle",
                actions = new[] { "seek", "preview" }
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target,
                kind = "player",
                loading = lyrics.CanCancelLyrics,
                error = string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = lyricRows,
                hasMore = false,
                detail = new
                {
                    title = playback.CurrentTitle,
                    artist = playback.CurrentArtist,
                    album = playback.CurrentAlbum,
                    source = playback.CurrentSource,
                    lyricStatus = lyrics.LyricStatus,
                    lyricCount = lyrics.Lyrics.Count,
                    activeLyricIndex = lyrics.ActiveLyricIndex,
                    lyricDisplayIndex = lyrics.LyricDisplayIndex,
                    showTranslation = lyrics.ShowLyricTranslation,
                    lyricOffsetSeconds = lyrics.LyricOffsetSeconds,
                    hasLyricPreview = lyrics.HasLyricPreview,
                    lyricPreviewTime = lyrics.LyricPreviewTime
                },
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }
        if (target == "music.queue")
        {
            var rows = queue.QueueSurfaceRows.Take(maxItems).Select((row, idx) => new
            {
                index = idx,
                id = row.EntryId.Length > 0 ? row.EntryId : row.Song.Id,
                title = row.Title,
                subtitle = row.Artist,
                duration = row.Duration,
                selected = row.IsCurrent,
                state = row.IsCurrent ? (playback.IsPlaying ? "playing" : "current") : "queued",
                actions = new[] { "play", "delete" },
                raw = includeRaw ? (JsonElement?)row.Song.Raw : null
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target = "music.queue",
                kind = "list",
                loading = false,
                error = string.IsNullOrWhiteSpace(queue.QueueSurfaceStatus)
                    ? (string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus)
                    : queue.QueueSurfaceStatus,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = queue.CanLoadMoreQueueSurface,
                detail = new
                {
                    totalText = queue.QueueSurfaceCountText,
                    loaded = queue.QueueSurfaceRows.Count,
                    hasRecommendationPanel = queue.HasRecommendationPanel,
                    recommendationTitle = queue.RecommendationTitle,
                    recommendationCount = queue.RecommendationCandidates.Count
                },
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }
        return Task.FromResult<object?>(null);
    }

    public async Task<object?> TryHandleDebugOverlayActionAsync(string target, string action, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var lyrics = capability.Lyrics;
        var queue = capability.Queue;
        if (target is "music.player" or "music.player.detail")
        {
            if (action.StartsWith("playback.", StringComparison.Ordinal))
                return await HandleOverlayPlaybackActionAsync(action["playback.".Length..], parameters, cancellationToken);

            if (action.StartsWith("lyric.", StringComparison.Ordinal))
                return await HandleOverlayLyricActionAsync(action["lyric.".Length..], parameters);

            switch (action)
            {
                case "refresh":
                    await lyrics.RefreshLyricsAsync();
                    return new { refreshed = true, target, lyricCount = lyrics.Lyrics.Count };

                case "list.item.invoke":
                {
                    int idx = GetInt(parameters, "index")
                        ?? throw DebugApiException.InvalidArgument("lyric list.item.invoke requires 'index'.");
                    if (idx < 0 || idx >= lyrics.Lyrics.Count)
                        throw DebugApiException.InvalidArgument($"Lyric index {idx} out of range (0..{Math.Max(0, lyrics.Lyrics.Count - 1)}).");
                    var verb = (GetStr(parameters, "verb") ?? "seek").Trim().ToLowerInvariant();
                    var row = lyrics.Lyrics[idx];
                    if (verb == "preview")
                    {
                        lyrics.PreviewLyric(row);
                        return new { invoked = true, target, verb, index = idx, text = row.Text, previewTime = lyrics.LyricPreviewTime };
                    }
                    await lyrics.SeekLyricAsync(row);
                    return new { invoked = true, target, verb = "seek", index = idx, text = row.Text, player = BuildMusicPlayerSnapshot() };
                }

                case "button.invoke":
                    return await HandlePlayerDetailButtonInvokeAsync(parameters);

                default:
                    return null;
            }
        }

        if (target == "music.queue")
        {
            if (action.StartsWith("playback.", StringComparison.Ordinal))
                return await HandleOverlayPlaybackActionAsync(action["playback.".Length..], parameters, cancellationToken);

            if (action.StartsWith("queue.", StringComparison.Ordinal))
                return await HandleOverlayQueueActionAsync(action["queue.".Length..], parameters);

            switch (action)
            {
                case "list.item.invoke":
                {
                    var verb = (GetStr(parameters, "verb") ?? "play").Trim().ToLowerInvariant();
                    var row = ResolveQueueRow(parameters);
                    if (verb is "delete" or "remove")
                    {
                        await queue.RemoveQueueSurfaceItemAsync(row);
                        return new { invoked = true, target, verb = "delete", id = row.EntryId, title = row.Title, queueCount = queue.QueueSurfaceRows.Count };
                    }
                    await queue.PlayQueueSurfaceItemAsync(row);
                    return new { invoked = true, target, verb = "play", id = row.EntryId, title = row.Title, player = BuildMusicPlayerSnapshot() };
                }

                case "list.load_more":
                {
                    if (queue.CanLoadMoreQueueSurface)
                    {
                        await queue.LoadMoreQueueSurfaceAsync();
                        return new { loadedMore = true, target, queueCount = queue.QueueSurfaceRows.Count };
                    }
                    return new { loadedMore = false, target, reason = "NO_MORE_ITEMS" };
                }

                case "button.invoke":
                    return await HandleQueueButtonInvokeAsync(parameters);

                default:
                    return null;
            }
        }

        return null;
    }

    public async Task<byte[]?> CaptureDebugAuxiliaryWindowPngAsync(string window, CancellationToken cancellationToken)
    {
        var normalized = window.Trim().ToLowerInvariant();
        if (normalized is "desktop-lyrics" or "desktop_lyrics" or "desktoplyrics")
            return await capability.Lyrics.CaptureDesktopLyricsWindowPngAsync(cancellationToken);
        return null;
    }

    private async Task<object?> HandleOverlayPlaybackActionAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "favorite":
                await capability.Playback.ToggleCurrentFavoriteAsync();
                return BuildMusicPlayerSnapshot();
            case "rate":
            {
                double rate = GetDouble(parameters, "rate") ?? GetDouble(parameters, "value")
                    ?? throw DebugApiException.InvalidArgument("playback.rate requires numeric 'rate'.");
                await capability.Playback.ChangePlaybackRateAsync(Math.Round(rate, 1));
                return BuildMusicPlayerSnapshot();
            }
            case "volume":
            {
                int vol = GetInt(parameters, "volume") ?? GetInt(parameters, "value")
                    ?? throw DebugApiException.InvalidArgument("playback.volume requires integer 'volume'.");
                capability.Playback.Volume = vol;
                await capability.Settings.SaveVolumeAsync();
                return BuildMusicPlayerSnapshot();
            }
            default:
                return await TryDebugPlaybackCommandAsync(command, parameters, cancellationToken);
        }
    }

    private async Task<object> HandleOverlayLyricActionAsync(string command, JsonElement? parameters)
    {
        var lyrics = capability.Lyrics;
        switch (command)
        {
            case "delay":
                lyrics.AdjustLyricOffset(.5);
                return new { command, lyricOffsetSeconds = lyrics.LyricOffsetSeconds };
            case "advance":
                lyrics.AdjustLyricOffset(-.5);
                return new { command, lyricOffsetSeconds = lyrics.LyricOffsetSeconds };
            case "offset":
            {
                double delta = GetDouble(parameters, "delta") ?? GetDouble(parameters, "seconds")
                    ?? throw DebugApiException.InvalidArgument("lyric.offset requires numeric 'delta'.");
                lyrics.AdjustLyricOffset(delta);
                return new { command, lyricOffsetSeconds = lyrics.LyricOffsetSeconds };
            }
            case "translation":
            {
                bool? want = GetBool(parameters, "show") ?? GetBool(parameters, "enabled") ?? GetBool(parameters, "value");
                if (want is null || lyrics.ShowLyricTranslation != want.Value)
                    lyrics.ToggleLyricTranslation();
                return new { command, showTranslation = lyrics.ShowLyricTranslation };
            }
            case "seek":
            {
                int idx = GetInt(parameters, "index")
                    ?? throw DebugApiException.InvalidArgument("lyric.seek requires 'index'.");
                if (idx < 0 || idx >= lyrics.Lyrics.Count)
                    throw DebugApiException.InvalidArgument($"Lyric index {idx} out of range (0..{Math.Max(0, lyrics.Lyrics.Count - 1)}).");
                await lyrics.SeekLyricAsync(lyrics.Lyrics[idx]);
                return new { command, index = idx, player = BuildMusicPlayerSnapshot() };
            }
            case "preview":
            {
                int idx = GetInt(parameters, "index")
                    ?? throw DebugApiException.InvalidArgument("lyric.preview requires 'index'.");
                if (idx < 0 || idx >= lyrics.Lyrics.Count)
                    throw DebugApiException.InvalidArgument($"Lyric index {idx} out of range (0..{Math.Max(0, lyrics.Lyrics.Count - 1)}).");
                lyrics.PreviewLyric(lyrics.Lyrics[idx]);
                return new { command, index = idx, hasLyricPreview = lyrics.HasLyricPreview, previewTime = lyrics.LyricPreviewTime };
            }
            case "confirm_preview":
                await lyrics.ConfirmLyricPreviewAsync();
                return new { command, hasLyricPreview = lyrics.HasLyricPreview, player = BuildMusicPlayerSnapshot() };
            case "cancel_preview":
                lyrics.CancelLyricPreview();
                return new { command, hasLyricPreview = lyrics.HasLyricPreview };
            case "refresh":
                await lyrics.RefreshLyricsAsync();
                return new { command, lyricCount = lyrics.Lyrics.Count, lyricStatus = lyrics.LyricStatus };
            default:
                throw DebugApiException.InvalidArgument($"Unsupported lyric action '{command}'.");
        }
    }

    private async Task<object> HandleOverlayQueueActionAsync(string command, JsonElement? parameters)
    {
        var queue = capability.Queue;
        switch (command)
        {
            case "play":
            {
                var row = ResolveQueueRow(parameters);
                await queue.PlayQueueSurfaceItemAsync(row);
                return new { command, id = row.EntryId, title = row.Title, player = BuildMusicPlayerSnapshot() };
            }
            case "remove":
            case "delete":
            {
                var row = ResolveQueueRow(parameters);
                await queue.RemoveQueueSurfaceItemAsync(row);
                return new { command, id = row.EntryId, title = row.Title, queueCount = queue.QueueSurfaceRows.Count };
            }
            case "clear":
                await queue.ClearQueueSurfaceAsync();
                return new { command, queueCount = queue.QueueSurfaceRows.Count };
            case "favorite_all":
                await queue.FavoriteQueueSurfaceAsync();
                return new { command, queueCount = queue.QueueSurfaceRows.Count };
            case "load_more":
                if (queue.CanLoadMoreQueueSurface)
                {
                    await queue.LoadMoreQueueSurfaceAsync();
                    return new { command, loadedMore = true, queueCount = queue.QueueSurfaceRows.Count };
                }
                return new { command, loadedMore = false, queueCount = queue.QueueSurfaceRows.Count };
            case "recommendation.choose":
            {
                var candidates = queue.RecommendationCandidates;
                if (candidates.Count == 0) throw DebugApiException.InvalidState("No recommendation candidates available.");
                var idx = GetInt(parameters, "index");
                var id = GetStr(parameters, "id");
                MusicRecommendationCandidate? picked;
                if (idx is { } ci)
                {
                    if (ci < 0 || ci >= candidates.Count)
                        throw DebugApiException.InvalidArgument($"Recommendation index {ci} out of range (0..{candidates.Count - 1}).");
                    picked = candidates[ci];
                }
                else picked = candidates.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal) || string.Equals(c.Song.Id, id, StringComparison.Ordinal));
                if (picked is null) throw DebugApiException.NotFound("Recommendation candidate not found.");
                await queue.ChooseRecommendationAsync(picked);
                return new { command, id = picked.Id, title = picked.Title, player = BuildMusicPlayerSnapshot() };
            }
            case "recommendation.retry":
                await queue.RetryRecommendationAsync();
                return new { command, executed = true };
            case "recommendation.reconcile":
                await queue.ReconcileRecommendationAsync();
                return new { command, executed = true };
            default:
                throw DebugApiException.InvalidArgument($"Unsupported queue action '{command}'.");
        }
    }

    private async Task<object> HandlePlayerDetailButtonInvokeAsync(JsonElement? parameters)
    {
        var key = (GetStr(parameters, "button") ?? GetStr(parameters, "name") ?? GetStr(parameters, "id") ?? GetStr(parameters, "label") ?? GetStr(parameters, "automationName") ?? "").Trim().ToLowerInvariant();
        switch (key)
        {
            case "play":
            case "pause":
            case "toggle":
            case "playbutton":
            case "播放或暂停":
                await capability.Playback.TogglePlaybackAsync();
                return new { invoked = true, button = key, player = BuildMusicPlayerSnapshot() };
            case "next":
            case "nextbutton":
            case "下一首":
                await capability.Playback.StepAsync(true, "manual");
                return new { invoked = true, button = key, player = BuildMusicPlayerSnapshot() };
            case "prev":
            case "previous":
            case "previousbutton":
            case "上一首":
                await capability.Playback.StepAsync(false, "manual");
                return new { invoked = true, button = key, player = BuildMusicPlayerSnapshot() };
            case "favorite":
            case "favoritebutton":
            case "收藏":
            case "收藏当前歌曲":
            case "取消收藏当前歌曲":
                await capability.Playback.ToggleCurrentFavoriteAsync();
                return new { invoked = true, button = key, isCurrentFavorite = capability.Playback.IsCurrentFavorite };
            case "queue":
            case "queuebutton":
            case "播放队列":
                await capability.OpenPlayQueueAsync();
                return new { invoked = true, button = key };
            case "desktop_lyrics":
            case "desktoplyricsbutton":
            case "桌面歌词":
            case "打开桌面歌词":
            case "关闭桌面歌词":
                await capability.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                return new { invoked = true, button = key, desktopLyrics = capability.Lyrics.DescribeDebugDesktopLyrics() };
            case "translation":
            case "lyrictranslationbutton":
            case "译文":
            case "显示译文歌词":
            case "隐藏译文歌词":
                await capability.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
                return new { invoked = true, button = key, showTranslation = capability.Lyrics.ShowLyricTranslation };
            case "delay_lyric":
            case "delaylyricbutton":
            case "歌词延后 0.5 秒":
                capability.Lyrics.AdjustLyricOffset(.5);
                return new { invoked = true, button = key, lyricOffsetSeconds = capability.Lyrics.LyricOffsetSeconds };
            case "advance_lyric":
            case "advancelyricbutton":
            case "歌词提前 0.5 秒":
                capability.Lyrics.AdjustLyricOffset(-.5);
                return new { invoked = true, button = key, lyricOffsetSeconds = capability.Lyrics.LyricOffsetSeconds };
            case "confirm_lyric":
            case "confirmlyric":
            case "播放此处":
                await capability.Lyrics.ConfirmLyricPreviewAsync();
                return new { invoked = true, button = key, player = BuildMusicPlayerSnapshot() };
            case "close":
            case "关闭":
                await capability.OpenPlayerDetailAsync();
                return new { invoked = true, button = key };
            default:
                throw DebugApiException.NotFound($"Unsupported player detail button '{key}'.");
        }
    }

    private async Task<object> HandleQueueButtonInvokeAsync(JsonElement? parameters)
    {
        var key = (GetStr(parameters, "button") ?? GetStr(parameters, "name") ?? GetStr(parameters, "id") ?? GetStr(parameters, "label") ?? GetStr(parameters, "automationName") ?? "").Trim().ToLowerInvariant();
        switch (key)
        {
            case "favorite_all":
            case "收藏全部":
                await capability.Queue.FavoriteQueueSurfaceAsync();
                return new { invoked = true, button = key };
            case "clear":
            case "清空":
                await capability.Queue.ClearQueueSurfaceAsync();
                return new { invoked = true, button = key };
            case "retry_recommendation":
            case "retryrecommendationbutton":
            case "重试推荐":
                await capability.Queue.RetryRecommendationAsync();
                return new { invoked = true, button = key };
            case "reconcile_recommendation":
            case "核对保存结果":
                await capability.Queue.ReconcileRecommendationAsync();
                return new { invoked = true, button = key };
            case "close":
            case "播放队列":
                await capability.OpenPlayQueueAsync();
                return new { invoked = true, button = key };
            default:
                throw DebugApiException.NotFound($"Unsupported queue button '{key}'.");
        }
    }

    private MusicQueueSurfaceRow ResolveQueueRow(JsonElement? parameters)
    {
        var rows = capability.Queue.QueueSurfaceRows;
        if (rows.Count == 0) throw DebugApiException.InvalidState("Play queue is empty.");
        var idx = GetInt(parameters, "index");
        var id = GetStr(parameters, "id");
        if (idx is { } qi)
        {
            if (qi < 0 || qi >= rows.Count)
                throw DebugApiException.InvalidArgument($"Queue index {qi} out of range (0..{rows.Count - 1}).");
            return rows[qi];
        }
        var match = rows.FirstOrDefault(r => string.Equals(r.EntryId, id, StringComparison.Ordinal) ||
            string.Equals(r.Song.Id, id, StringComparison.Ordinal));
        return match ?? throw DebugApiException.NotFound($"Queue item not found (id='{id}').");
    }

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        var catalog = capability.Catalog;
        var library = capability.Library;
        var search = capability.Search;
        var playback = capability.Playback;
        var settingsController = capability.Settings;
        var accounts = capability.Accounts;

        if (settingsOpen)
        {
            var settings = new
            {
                domain = "music",
                section = capability.Presentation.Settings.CurrentSectionKey,
                tabs = capability.Presentation.Settings.DescribeDebugTabs(),
                playbackQualityRequest = settingsController.PlaybackQualityRequest,
                playbackQualityOptions = settingsController.PlaybackQualityOptions,
                accounts = accounts.Accounts.Select(a => new
                {
                    platform = a.Platform,
                    name = a.Name,
                    state = a.State,
                    stateText = a.StateText,
                    displayName = a.DisplayName,
                    userId = a.UserId,
                    hasSession = a.HasSession
                }).ToArray(),
                sources = settingsController.Sources.Select((s, idx) => new
                {
                    index = idx,
                    key = s.Key,
                    installationId = s.InstallationId,
                    name = s.Name,
                    version = s.Version,
                    enabled = s.Enabled,
                    state = s.State,
                    summary = s.Summary
                }).ToArray()
            };
            return Task.FromResult<object?>(new
            {
                target = "settings.music",
                kind = "settings",
                loading = false,
                error = string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus,
                tabs = capability.Presentation.Settings.DescribeDebugTabs(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings
            });
        }

        var currentTarget = DescribeCurrentTarget(false, out _) ?? "music.home";

        if (catalog.IsCatalogLanding || catalog.IsCatalogContent && !catalog.IsCatalogDetailPage && !library.IsSongList)
        {
            var tabs = new List<object>();
            for (int i = 0; i < catalog.ChannelTabs.Count; i++)
            {
                var ch = catalog.ChannelTabs[i];
                tabs.Add(new
                {
                    group = "channel",
                    index = i,
                    id = ch.ActionId.Length > 0 ? ch.ActionId : ch.Id,
                    title = ch.Title,
                    selected = !string.IsNullOrEmpty(catalog.CurrentChannelKey) &&
                        (string.Equals(ch.ActionId, catalog.CurrentChannelKey, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(ch.Id, catalog.CurrentChannelKey, StringComparison.OrdinalIgnoreCase)),
                    enabled = ch.Available
                });
            }
            for (int i = 0; i < catalog.CatalogPlatforms.Count; i++)
            {
                var plat = catalog.CatalogPlatforms[i];
                tabs.Add(new
                {
                    group = "platform",
                    index = i,
                    id = plat.Key,
                    title = plat.Name,
                    selected = plat.Key == catalog.SelectedCatalogPlatform.Key,
                    enabled = true
                });
            }

            var filters = catalog.CatalogTags.Select((tag, idx) => new
            {
                group = "filter",
                index = idx,
                id = tag.Id,
                title = tag.Name,
                selected = tag.Selected
            }).ToArray();

            var sections = ProjectCatalogSections(catalog.DisplayCatalogSections, maxItems, includeRaw);
            return Task.FromResult<object?>(new
            {
                target = currentTarget,
                kind = "sections",
                loading = capability.LoadCancellation is not null || catalog.ShowCatalogLoading,
                error = string.IsNullOrWhiteSpace(catalog.Status) ? null : catalog.Status,
                tabs,
                filters,
                sections,
                rows = Array.Empty<object>(),
                hasMore = catalog.CanLoadMoreCatalogPlaylist || catalog.CanCatalogNext,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }

        if (library.IsArchives)
        {
            var rows = library.Archives.Take(maxItems).Select((a, idx) => new
            {
                index = idx,
                id = a.Id,
                title = a.Title,
                subtitle = $"{a.Count} 首",
                selected = false,
                state = "archived",
                actions = new[] { "open", "delete" }
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target = currentTarget,
                kind = "list",
                loading = capability.LoadCancellation is not null,
                error = string.IsNullOrWhiteSpace(library.Status) ? null : library.Status,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = false,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }

        var activeSongs = search.IsSearchPage ? search.SearchSongsSource : library.CatalogSongsSource;
        var activeSelection = search.IsSearchPage ? search.SearchSelectedSong : library.CatalogSelectedSong;
        var songRows = activeSongs.Take(maxItems).Select((song, idx) => new
        {
            index = idx,
            id = song.Id,
            title = song.Title,
            subtitle = string.IsNullOrEmpty(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}",
            duration = song.Duration,
            selected = ReferenceEquals(song, activeSelection),
            state = song.Id == playback.CurrentSongId ? (playback.IsPlaying ? "playing" : "current") : "idle",
            actions = new[] { "play", "select", "favorite" },
            raw = includeRaw ? (JsonElement?)song.Raw : null
        }).ToArray();

        var dateFilters = library.IsHistory
            ? library.HistoryDates.Select((d, idx) => new { group = "filter", index = idx, id = d, title = d, selected = d == library.HistoryDate }).ToArray()
            : Array.Empty<object>();

        var collection = library.CurrentCollection;
        return Task.FromResult<object?>(new
        {
            target = currentTarget,
            kind = "list",
            loading = capability.LoadCancellation is not null || search.ShowSearchLoading || library.ShowDetailLoading,
            error = string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus,
            tabs = Array.Empty<object>(),
            filters = dateFilters,
            sections = ProjectCatalogSections(catalog.DisplayCatalogSections, maxItems, includeRaw),
            rows = songRows,
            hasMore = library.CanLoadMoreCollection || catalog.CanLoadMoreCatalogPlaylist || library.CanNextPage,
            detail = collection is null ? null : new { id = collection.Id, name = collection.Name, kind = collection.Kind, count = collection.Count },
            player = BuildMusicPlayerSnapshot(),
            reader = (object?)null,
            settings = (object?)null
        });
    }

    internal object[] ProjectCatalogSections(IEnumerable<MusicCatalogSection> source, int maxItems, bool includeRaw)
    {
        var result = new List<object>();
        int secIdx = 0;
        foreach (var sec in source)
        {
            var items = new List<object>();
            int itemIdx = 0;
            foreach (var card in sec.Items.Take(maxItems))
            {
                var actions = new List<string>();
                if (card.CanOpen) actions.Add("open");
                if (card.CanPlay) actions.Add("play");
                if (card.CanFavorite) actions.Add("favorite");
                items.Add(new
                {
                    index = itemIdx++,
                    id = card.Id.Length > 0 ? card.Id : card.Key,
                    kind = card.Kind,
                    title = card.Title,
                    subtitle = card.Subtitle,
                    badge = card.Label.Length > 0 ? card.Label : card.CoverLabel.Length > 0 ? card.CoverLabel : card.PlayCountText,
                    coverUrl = card.Cover,
                    actions,
                    meta = new
                    {
                        platform = card.Platform,
                        actionId = card.ActionId,
                        available = card.Available,
                        errorCode = card.ErrorCode,
                        raw = includeRaw ? card.Song?.Raw : null
                    }
                });
            }
            result.Add(new
            {
                index = secIdx++,
                id = sec.Id.Length > 0 ? sec.Id : $"section_{secIdx - 1}",
                title = sec.Title,
                kind = sec.Type.Length > 0 ? sec.Type : "mixed",
                layout = sec.Layout.Length > 0 ? sec.Layout : sec.Presentation.ToString().ToLowerInvariant(),
                hasMore = sec.Action is { CanOpen: true },
                isLoading = sec.IsLoading,
                errorCode = sec.ErrorCode,
                selectedTab = sec.SelectedTab,
                sectionTabs = sec.Tabs?.Select((t, ti) => new { index = ti, id = t.Id, title = t.Title, selected = t.Id == sec.SelectedTab }).ToArray(),
                itemCount = sec.Items.Count,
                items
            });
        }
        return result.ToArray();
    }

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        var catalog = capability.Catalog;
        var library = capability.Library;
        var search = capability.Search;
        var queue = capability.Queue;

        switch (action)
        {
            case "refresh":
                await library.RefreshAsync();
                return new { refreshed = true, route = capability.CurrentRoute };

            case "section.item.invoke":
            {
                var sections = catalog.DisplayCatalogSections;
                var sec = ResolveSection(sections, parameters);
                var card = ResolveCard(sec.Items, parameters);
                var verb = (GetStr(parameters, "verb") ?? "open").Trim().ToLowerInvariant();
                DebugLog.Info(DebugCategories.MusicCatalog, "debug.action", "section.item.invoke",
                    new { section = sec.Title, item = card.Title, kind = card.Kind, verb });
                if (verb == "favorite")
                {
                    await catalog.FavoriteCatalogCardAsync(card);
                    return new { invoked = true, verb, title = card.Title };
                }
                if (verb == "play" && card.CanPlay)
                {
                    await catalog.PlayCatalogCardAsync(card);
                    return new { invoked = true, verb, title = card.Title, kind = card.Kind, route = capability.CurrentRoute };
                }
                await catalog.OpenCatalogCardAsync(card);
                return new { invoked = true, verb, title = card.Title, kind = card.Kind, route = capability.CurrentRoute };
            }

            case "section.more":
            {
                var sec = ResolveSection(catalog.DisplayCatalogSections, parameters);
                if (sec.Action is not { CanOpen: true } moreCard)
                    throw DebugApiException.InvalidState($"Section '{sec.Title}' has no 'more' action.");
                await catalog.OpenCatalogCardAsync(moreCard);
                return new { invoked = true, section = sec.Title, route = capability.CurrentRoute };
            }

            case "tab.select":
            {
                var group = (GetStr(parameters, "group") ?? "channel").Trim().ToLowerInvariant();
                var id = GetStr(parameters, "id");
                var index = GetInt(parameters, "index");
                if (group == "settings-section" || settingsOpen)
                {
                    string targetKey = id ?? (index is { } idx && idx >= 0 && idx < 5
                        ? new[] { "accounts", "search", "quality", "metadata", "sources" }[idx]
                        : throw DebugApiException.InvalidArgument("Provide 'id' or valid 'index' for settings-section."));
                    if (!capability.Presentation.Settings.SelectDebugSection(targetKey))
                        throw DebugApiException.NotFound($"Music settings section not found: '{targetKey}'.");
                    return new { group = "settings-section", selected = targetKey };
                }
                if (group == "channel")
                {
                    MusicCatalogCard? tab;
                    if (index is { } idx)
                    {
                        if (idx < 0 || idx >= catalog.ChannelTabs.Count)
                            throw DebugApiException.InvalidArgument($"Channel tab index {idx} out of range (0..{Math.Max(0, catalog.ChannelTabs.Count - 1)}).");
                        tab = catalog.ChannelTabs[idx];
                    }
                    else
                    {
                        tab = catalog.ChannelTabs.FirstOrDefault(c =>
                            string.Equals(c.ActionId, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tab is null) throw DebugApiException.NotFound($"Channel tab not found (index={index}, id='{id}').");
                    await catalog.OpenCatalogCardAsync(tab);
                    return new { group = "channel", selected = tab.Title, route = capability.CurrentRoute };
                }
                if (group == "platform")
                {
                    MusicCatalogPlatform? plat;
                    if (index is { } idx)
                    {
                        if (idx < 0 || idx >= catalog.CatalogPlatforms.Count)
                            throw DebugApiException.InvalidArgument($"Platform tab index {idx} out of range (0..{Math.Max(0, catalog.CatalogPlatforms.Count - 1)}).");
                        plat = catalog.CatalogPlatforms[idx];
                    }
                    else
                    {
                        plat = catalog.CatalogPlatforms.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (plat is null) throw DebugApiException.NotFound($"Catalog platform not found: '{id}'.");
                    await catalog.ChangeCatalogPlatformAsync(plat);
                    return new { group = "platform", selected = plat.Key };
                }
                if (group == "filter")
                {
                    if (library.IsHistory)
                    {
                        string? date;
                        if (index is { } idx)
                        {
                            if (idx < 0 || idx >= library.HistoryDates.Count)
                                throw DebugApiException.InvalidArgument($"History date index {idx} out of range (0..{Math.Max(0, library.HistoryDates.Count - 1)}).");
                            date = library.HistoryDates[idx];
                        }
                        else date = id;
                        if (string.IsNullOrWhiteSpace(date)) throw DebugApiException.NotFound("History date not found.");
                        await library.SelectHistoryDateAsync(date);
                        return new { group = "filter", selected = date };
                    }
                    MusicCatalogTag? tag;
                    if (index is { } ti)
                    {
                        if (ti < 0 || ti >= catalog.CatalogTags.Count)
                            throw DebugApiException.InvalidArgument($"Catalog tag index {ti} out of range (0..{Math.Max(0, catalog.CatalogTags.Count - 1)}).");
                        tag = catalog.CatalogTags[ti];
                    }
                    else
                    {
                        tag = catalog.CatalogTags.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tag is null) throw DebugApiException.NotFound($"Catalog tag not found (index={index}, id='{id}').");
                    await catalog.CatalogTagAsync(tag);
                    return new { group = "filter", selected = tag.Name };
                }
                if (group == "section-tab")
                {
                    var sec = ResolveSection(catalog.DisplayCatalogSections, parameters);
                    if (sec.Tabs is not { Count: > 0 } stabs)
                        throw DebugApiException.InvalidState($"Section '{sec.Title}' has no tabs.");
                    if (index is { } si && (si < 0 || si >= stabs.Count))
                        throw DebugApiException.InvalidArgument($"Section tab index {si} out of range (0..{stabs.Count - 1}).");
                    var picked = index is { } validIdx
                        ? stabs[validIdx]
                        : stabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Title, id, StringComparison.OrdinalIgnoreCase));
                    if (picked is null) throw DebugApiException.NotFound("Section tab not found.");
                    catalog.SelectCatalogSectionTab(picked);
                    return new { group = "section-tab", section = sec.Title, selected = picked.Id };
                }
                throw DebugApiException.InvalidArgument($"Unsupported tab group '{group}' for music.");
            }

            case "list.item.invoke":
            {
                var verb = (GetStr(parameters, "verb") ?? "play").Trim().ToLowerInvariant();
                var idx = GetInt(parameters, "index");
                var id = GetStr(parameters, "id");
                if (library.IsArchives)
                {
                    ArchivedPlaylist? archive;
                    if (idx is { } ai)
                    {
                        if (ai < 0 || ai >= library.Archives.Count)
                            throw DebugApiException.InvalidArgument($"Archive index {ai} out of range (0..{Math.Max(0, library.Archives.Count - 1)}).");
                        archive = library.Archives[ai];
                    }
                    else archive = library.Archives.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
                    if (archive is null) throw DebugApiException.NotFound("Archive item not found.");
                    if (verb == "delete") await library.DeleteArchiveAsync(archive.Id);
                    else await library.RestoreArchiveAsync(archive.Id);
                    return new { invoked = true, verb, id = archive.Id };
                }
                var songs = capability.ActiveSongs;
                SongRow? song;
                if (idx is { } si)
                {
                    if (si < 0 || si >= songs.Count)
                        throw DebugApiException.InvalidArgument($"Song index {si} out of range (0..{Math.Max(0, songs.Count - 1)}).");
                    song = songs[si];
                }
                else song = songs.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
                if (song is null) throw DebugApiException.NotFound("Song row not found.");
                library.SelectedSong = song;
                if (verb == "select") return new { invoked = true, verb, id = song.Id, title = song.Title };
                if (verb == "favorite") { await library.FavoriteAsync(); return new { invoked = true, verb, id = song.Id }; }
                if (verb == "delete") { await library.RemoveSongAsync(); return new { invoked = true, verb, id = song.Id }; }
                await library.PlaySelectionAsync();
                return new { invoked = true, verb = "play", id = song.Id, title = song.Title };
            }

            case "list.load_more":
            {
                if (library.CanLoadMoreCollection) { await library.LoadMoreCollectionAsync(); return new { loadedMore = true }; }
                if (catalog.CanLoadMoreCatalogPlaylist) { await catalog.LoadMoreCatalogPlaylistAsync(); return new { loadedMore = true }; }
                if (search.CanLoadMoreSearchSongs) { await search.MoreSearchAsync(); return new { loadedMore = true }; }
                if (library.CanNextPage) { await library.PageAsync(1); return new { loadedMore = true }; }
                if (catalog.CanCatalogNext) { await catalog.CatalogPageAsync(1); return new { loadedMore = true }; }
                if (queue.CanLoadMoreQueueSurface) { await queue.LoadMoreQueueSurfaceAsync(); return new { loadedMore = true }; }
                return new { loadedMore = false, reason = "NO_MORE_ITEMS" };
            }

            case "setting.set":
            {
                var key = (GetStr(parameters, "key") ?? "").Trim().ToLowerInvariant();
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                if (key is "playbackquality" or "quality" && val.ValueKind == JsonValueKind.String)
                {
                    await capability.Settings.SetPlaybackQualityRequestAsync(val.GetString()!);
                    return new { saved = true, key, value = capability.Settings.PlaybackQualityRequest };
                }
                if (key == "volume" && val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var vol))
                {
                    capability.Playback.Volume = vol;
                    return new { saved = true, key, value = capability.Playback.Volume };
                }
                throw DebugApiException.InvalidArgument($"Unsupported music setting key '{key}'.");
            }

            case "setting.action":
            {
                var cmd = (GetStr(parameters, "command") ?? "").Trim().ToLowerInvariant();
                if (cmd == "clear_search_history")
                {
                    await capability.Settings.ClearSearchHistoryAsync();
                    return new { executed = true, command = cmd };
                }
                if (cmd is "account.login" or "account.login.close")
                {
                    var platform = GetStr(parameters, "platform") ?? GetStr(parameters, "provider");
                    var close = cmd.EndsWith(".close", StringComparison.Ordinal) || GetBool(parameters, "close") == true;
                    var mode = GetStr(parameters, "mode");
                    return await capability.Presentation.Settings.DebugAccountLoginAsync(platform, close, mode, cancellationToken);
                }
                if (cmd is "account.logout" or "account.logout.close")
                {
                    var platform = GetStr(parameters, "platform") ?? GetStr(parameters, "provider");
                    var close = cmd.EndsWith(".close", StringComparison.Ordinal) || GetBool(parameters, "close") == true;
                    var confirm = GetBool(parameters, "confirm") == true;
                    return await capability.Presentation.Settings.DebugAccountLogoutAsync(platform, close, confirm, cancellationToken);
                }
                throw DebugApiException.InvalidArgument($"Unsupported music settings command '{cmd}'.");
            }

            default:
                return null;
        }
    }

    public Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(BuildMusicPlayerSnapshot());

    private object BuildMusicPlayerSnapshot()
    {
        var playback = capability.Playback;
        var lyrics = capability.Lyrics;
        var queue = capability.Queue;
        var current = playback.CurrentSong;
        int lyricIdx = lyrics.ActiveLyricIndex;
        string? activeLyric = lyricIdx >= 0 && lyricIdx < lyrics.Lyrics.Count ? lyrics.Lyrics[lyricIdx].Text : null;
        int queueIdx = -1;
        for (int i = 0; i < queue.QueueSurfaceRows.Count; i++)
        {
            if (queue.QueueSurfaceRows[i].IsCurrent) { queueIdx = i; break; }
        }
        return new
        {
            state = !playback.HasCurrent ? "stopped" : playback.IsPlaying ? "playing" : "paused",
            track = current is null ? null : new
            {
                id = current.Id,
                entryId = current.EntryId,
                title = current.Title,
                artist = current.Artist,
                album = current.Album,
                cover = current.Cover,
                isOnline = current.IsOnline
            },
            source = playback.CurrentSource,
            quality = capability.Settings.PlaybackQualityRequest,
            positionSec = Math.Round(playback.Clock.PositionSeconds, 3),
            durationSec = Math.Round(playback.Clock.DurationSeconds, 3),
            cacheFraction = Math.Round(playback.Clock.PlaybackCacheFraction, 4),
            bufferedSec = (double?)null,
            volume = playback.Volume,
            mode = playback.Mode,
            modeText = playback.ModeText,
            canPlay = playback.CanPlay,
            canStep = playback.CanStep,
            canTogglePlayback = playback.CanTogglePlayback,
            canSeek = playback.CanSeek,
            canFavoriteCurrent = playback.CanFavoriteCurrent,
            canAddCurrentToPlaylist = playback.CanAddCurrentToPlaylist,
            canChangePlaybackMode = queue.CanChangePlaybackMode,
            queueIndex = queueIdx,
            queueCount = queue.QueueSurfaceRows.Count,
            queueTotalText = queue.QueueSurfaceCountText,
            lyricLine = activeLyric,
            lyricCount = lyrics.Lyrics.Count,
            desktopLyrics = lyrics.DescribeDebugDesktopLyrics(),
            lastError = string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus
        };
    }

    public async Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.MusicPlayback, "debug.command", "playback." + command, new { @params = parameters });
        switch (command)
        {
            case "play":
                await capability.Playback.ResumePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "pause":
                if (capability.Playback.IsPlaying) await capability.Playback.TogglePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "toggle":
                await capability.Playback.TogglePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "stop":
                await capability.PlaybackDevice.StopAsync();
                return BuildMusicPlayerSnapshot();
            case "next":
                await capability.Playback.StepAsync(true, "manual");
                return BuildMusicPlayerSnapshot();
            case "previous":
            case "prev":
                await capability.Playback.StepAsync(false, "manual");
                return BuildMusicPlayerSnapshot();
            case "seek":
            {
                double sec = GetDouble(parameters, "seconds")
                    ?? throw DebugApiException.InvalidArgument("seek requires numeric 'seconds'.");
                await capability.Playback.SeekAsync(sec);
                return BuildMusicPlayerSnapshot();
            }
            case "mode":
            {
                int m = GetInt(parameters, "mode") ?? throw DebugApiException.InvalidArgument("mode requires integer 'mode'.");
                await capability.Playback.ChangeModeAsync(m);
                return BuildMusicPlayerSnapshot();
            }
            case "play_queue_index":
            {
                int idx = GetInt(parameters, "index") ?? throw DebugApiException.InvalidArgument("play_queue_index requires 'index'.");
                if (idx < 0 || idx >= capability.Queue.QueueSurfaceRows.Count)
                    throw DebugApiException.InvalidArgument($"Queue index {idx} out of range (0..{capability.Queue.QueueSurfaceRows.Count - 1}).");
                await capability.Queue.PlayQueueSurfaceItemAsync(capability.Queue.QueueSurfaceRows[idx]);
                return BuildMusicPlayerSnapshot();
            }
            case "desktop_lyrics":
            {
                var open = GetBool(parameters, "open");
                var locked = GetBool(parameters, "locked");
                var layout = GetStr(parameters, "layout");
                var translation = GetBool(parameters, "translation");
                return await capability.Lyrics.DebugControlDesktopLyricsAsync(open, locked, layout, translation);
            }
            default:
                return null;
        }
    }

    public Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    private static MusicCatalogSection ResolveSection(IList<MusicCatalogSection> sections, JsonElement? parameters)
    {
        if (sections.Count == 0) throw DebugApiException.InvalidState("No catalog sections are currently loaded.");
        if (GetInt(parameters, "sectionIndex") is { } idx)
        {
            if (idx < 0 || idx >= sections.Count)
                throw DebugApiException.InvalidArgument($"sectionIndex {idx} out of range (0..{sections.Count - 1}).");
            return sections[idx];
        }
        if (GetStr(parameters, "sectionId") is { Length: > 0 } sid)
        {
            return sections.FirstOrDefault(s =>
                string.Equals(s.Id, sid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, sid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Section '{sid}' not found.");
        }
        return sections[0];
    }

    private static MusicCatalogCard ResolveCard(IReadOnlyList<MusicCatalogCard> items, JsonElement? parameters)
    {
        if (items.Count == 0) throw DebugApiException.InvalidState("Target section has no items.");
        if (GetInt(parameters, "itemIndex") is { } idx)
        {
            if (idx < 0 || idx >= items.Count)
                throw DebugApiException.InvalidArgument($"itemIndex {idx} out of range (0..{items.Count - 1}).");
            return items[idx];
        }
        if (GetStr(parameters, "itemId") is { Length: > 0 } iid)
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Id, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Key, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Title, iid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Item '{iid}' not found.");
        }
        return items[0];
    }

    private static string? GetStr(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static int? GetInt(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val : null;

    private static double? GetDouble(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val)
            ? val : null;

    private static bool? GetBool(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean() : null;
}
