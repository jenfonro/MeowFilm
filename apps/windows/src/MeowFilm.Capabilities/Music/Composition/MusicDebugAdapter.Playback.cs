using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed partial class MusicDebugAdapter
{
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
                raw = includeRaw ? (JsonElement?)Library.MusicNodeSongCodec.SongToElement(row.Song.Record) : null
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
        var device = capability.PlaybackDevice;
        string state = !playback.HasCurrent ? "stopped"
            : playback.IsBuffering ? "buffering"
            : playback.IsPlaying ? "playing"
            : playback.LastPlaybackError is not null && !device.HasMedia ? "error"
            : "paused";
        return new
        {
            state,
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
            rate = Math.Round(playback.PlaybackRate, 2),
            positionSec = Math.Round(playback.Clock.PositionSeconds, 3),
            durationSec = Math.Round(playback.Clock.DurationSeconds, 3),
            cacheFraction = Math.Round(playback.Clock.PlaybackCacheFraction, 4),
            bufferedSec = playback.OwnsCurrent ? Math.Round(playback.BufferedSeconds, 3) : (double?)null,
            underrunCount = playback.OwnsCurrent ? device.UnderrunCount : 0,
            deviceId = playback.OwnsCurrent ? device.ActiveDeviceId : null,
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
            lastErrorCode = playback.LastPlaybackError,
            lastError = string.IsNullOrWhiteSpace(capability.RawStatus) ? playback.LastPlaybackError : capability.RawStatus
        };
    }

    public async Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.MusicPlayback, "debug.command", "playback." + command, new { @params = parameters });
        switch (command)
        {
            case "play":
                if (!capability.Playback.IsPlaying)
                {
                    if (capability.Playback.OwnsCurrent && capability.PlaybackDevice.HasMedia)
                        await capability.Playback.ResumePlaybackAsync();
                    else if (capability.Playback.HasCurrent)
                        await capability.Playback.TogglePlaybackAsync();
                }
                return BuildMusicPlayerSnapshot();
            case "pause":
                if (capability.Playback.IsPlaying) await capability.Playback.TogglePlaybackAsync();
                else if (capability.Playback.OwnsCurrent) await capability.PlaybackDevice.PauseAsync();
                return BuildMusicPlayerSnapshot();
            case "toggle":
                await capability.Playback.TogglePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "stop":
                await capability.Playback.StopOwnedPlayback();
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
            case "select_source":
            {
                if (!capability.Playback.HasSourceChoices)
                    capability.Playback.OpenSourceChoices();
                var choices = capability.Playback.SourceChoices;
                if (choices.Count == 0)
                    throw DebugApiException.InvalidState("No music sources available for current track.");
                MusicSourceChoice? choice = null;
                if (GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= choices.Count)
                        throw DebugApiException.InvalidArgument($"Source index {idx} out of range (0..{choices.Count - 1}).");
                    choice = choices[idx];
                }
                else if ((GetStr(parameters, "key") ?? GetStr(parameters, "id")) is { Length: > 0 } key)
                {
                    choice = choices.FirstOrDefault(c =>
                        string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.Provider, key, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var provider = GetStr(parameters, "provider");
                    var platform = GetStr(parameters, "platform");
                    choice = choices.FirstOrDefault(c =>
                        c.Selectable &&
                        (string.IsNullOrWhiteSpace(provider) || string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrWhiteSpace(platform) || string.Equals(c.Platform, platform, StringComparison.OrdinalIgnoreCase)));
                }
                if (choice is null) throw DebugApiException.NotFound("Matching music source choice not found.");
                if (!choice.Selectable) throw DebugApiException.InvalidState($"Music source '{choice.Key}' is not selectable.");
                await capability.Playback.SelectSourceChoiceAsync(choice);
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
}
