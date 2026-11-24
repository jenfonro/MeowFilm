using System.Globalization;
using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed partial class FilmDebugAdapter
{
    private object BuildPlaybackDebugContent(int maxItems)
    {
        var pb = BuildFilmPlayerSnapshot();
        var playback = capability.Playback;
        return new
        {
            target = "film.playback",
            kind = "player",
            loading = playback?.PlayerBusy ?? false,
            error = playback is { ExecutionFailed: true } or { ConnectionFailed: true } ? playback.StatusText : null,
            tabs = Array.Empty<object>(),
            filters = Array.Empty<object>(),
            sections = Array.Empty<object>(),
            rows = Array.Empty<object>(),
            hasMore = false,
            detail = playback is null ? null : new
            {
                title = playback.Title,
                sources = playback.Sources.Select((s, i) => new { index = i, id = s.Id, label = s.Label, selected = ReferenceEquals(s, playback.SelectedSource) }).ToArray(),
                lines = playback.Lines.Select((l, i) => new { index = i, id = l.Id, label = l.Label, selected = ReferenceEquals(l, playback.SelectedLine) }).ToArray(),
                episodes = playback.Episodes.Take(maxItems).Select((e, i) => new { index = i, id = e.Identity, title = e.Title, number = e.Number, selected = e.IsSelected }).ToArray()
            },
            player = pb,
            reader = (object?)null,
            settings = (object?)null
        };
    }

    public async Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken)
    {
        var nativeFilmPlayback = capability.DebugNativeFilmPlayback;
        if (nativeFilmPlayback.HasOutput)
        {
            try { await nativeFilmPlayback.ReadCurrentDiagnosticsAsync(cancellationToken); } catch { }
        }
        return BuildFilmPlayerSnapshot();
    }

    private object BuildFilmPlayerSnapshot()
    {
        var nativeFilmPlayback = capability.DebugNativeFilmPlayback;
        var filmPlayback = capability.DebugFilmPlayback;
        var tracks = nativeFilmPlayback.Tracks;
        var chapters = nativeFilmPlayback.Chapters;
        var output = nativeFilmPlayback.Output;
        var diag = nativeFilmPlayback.LastDiagnostics;
        var page = capability.Playback;
        var playerView = capability.DebugView.PlaybackViewControl;

        string? snapshotError = page?.CurrentSnapshotError is { Length: > 0 } snapErr ? snapErr : null;
        string? lastError = !string.IsNullOrEmpty(nativeFilmPlayback.LastErrorCode) ? nativeFilmPlayback.LastErrorCode
            : !string.IsNullOrEmpty(filmPlayback.ExecutionError) ? filmPlayback.ExecutionError
            : snapshotError;

        string state;
        if (filmPlayback.HasOutput)
        {
            state = filmPlayback.IsBuffering ? "buffering"
                : filmPlayback.IsPlaying ? "playing" : "paused";
        }
        else if (page is { ExecutionFailed: true } or { ConnectionFailed: true } ||
                 page?.CurrentStage is "error" or "unavailable")
        {
            state = "error";
        }
        else if (page?.CurrentStage == "ended")
        {
            state = "ended";
        }
        else
        {
            state = "stopped";
        }

        return new
        {
            state,
            stage = page?.CurrentStage,
            engine = "filmkernel",
            title = page?.Title ?? filmPlayback.MediaTitle,
            source = page?.SelectedSource?.Label,
            episode = page?.SelectedEpisode?.Title,
            line = page?.SelectedLine?.Label,
            positionSec = Math.Round(filmPlayback.Position, 3),
            durationSec = Math.Round(filmPlayback.Duration, 3),
            bufferedSec = Math.Round(filmPlayback.BufferedUntil, 3),
            canSeek = filmPlayback.CanSeek,
            paused = filmPlayback.HasOutput && !filmPlayback.IsPlaying && !filmPlayback.IsBuffering,
            buffering = filmPlayback.IsBuffering,
            videoSize = filmPlayback.VideoWidth > 0 && filmPlayback.VideoHeight > 0
                ? new { width = filmPlayback.VideoWidth, height = filmPlayback.VideoHeight } : null,
            rate = filmPlayback.Rate,
            aspect = filmPlayback.Aspect,
            volume = Math.Round(filmPlayback.Volume / 100.0, 4),
            muted = filmPlayback.IsMuted,
            pictureInPicture = playerView.IsPipActive,
            pictureInPictureState = playerView.IsPipActive ? "active" : (filmPlayback.HasOutput ? "possible" : "unavailable"),
            fullscreen = playerView.IsFullscreenActive,
            nativeHdr = output?.NativeHdr ?? false,
            toneMapped = output?.ToneMapped ?? false,
            dolbyVision = output?.DolbyVision ?? false,
            hardwareDecoding = output?.HardwareDecoding ?? false,
            audioDegraded = output?.AudioDegraded ?? false,
            submittedPictures = output?.SubmittedPictures ?? 0UL,
            statusText = page?.PlayerStatusText,
            videoTracks = tracks?.Video
                .Select(t => new { id = t.Index, index = t.Index, name = t.Title, codec = t.Codec, language = t.Language, title = t.Title, selected = t.Selected }).ToArray(),
            audioTracks = tracks?.Audio
                .Select(t => new { id = t.Index, index = t.Index, name = t.Title, codec = t.Codec, language = t.Language, title = t.Title, selected = t.Selected }).ToArray(),
            subtitleTracks = tracks?.Subtitles
                .Select(t => new { id = t.Index, index = t.Index, name = t.Title, codec = t.Codec, language = t.Language, title = t.Title, selected = t.Selected }).ToArray(),
            selectedVideoTrack = tracks?.VideoIndex,
            selectedAudioTrack = tracks?.AudioIndex,
            selectedSubtitleTrack = tracks?.SubtitleIndex,
            chapters = chapters.Select((c, idx) => new
            {
                index = idx,
                id = c.Index,
                title = c.Title,
                startSec = Math.Round(c.StartSeconds, 3),
                endSec = Math.Round(c.EndSeconds, 3),
                current = page is not null && page.ActiveChapterIndex == idx
            }).ToArray(),
            activeChapterIndex = page is { ActiveChapterIndex: >= 0 } activePage ? activePage.ActiveChapterIndex : (int?)null,
            activeChapterTitle = page?.ActiveChapterTitle is { Length: > 0 } chapterTitle ? chapterTitle : null,
            diagnostics = diag is null ? null : new
            {
                droppedFrames = diag.DroppedFrames,
                avSyncDiffSec = Math.Round(diag.AvSyncDiffSeconds, 4),
                renderFps = Math.Round(diag.RenderFps, 2),
                videoPacketQueue = diag.VideoPacketQueue,
                audioPacketQueue = diag.AudioPacketQueue,
                subtitlePacketQueue = diag.SubtitlePacketQueue,
                pictureQueue = diag.PictureQueue,
                audioUnderruns = diag.AudioUnderruns,
                audioReconnects = diag.AudioReconnects,
                subtitleErrors = diag.SubtitleErrors,
                hardwareDecoding = output?.HardwareDecoding ?? false,
                hardwareFallback = diag.HardwareFallback,
                displayHdrEnabled = diag.DisplayHdrEnabled,
                toneMapped = diag.ToneMapped,
                nativeHdr = output?.NativeHdr ?? false,
                dolbyVision = output?.DolbyVision ?? false,
                audioDegraded = diag.AudioDegraded,
                submittedPictures = output?.SubmittedPictures ?? 0UL,
                decodeFallbackReason = diag.DecodeFallbackReason,
                lastNativeError = diag.LastNativeError
            },
            lastError
        };
    }

    public async Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.FilmPlayback, "debug.command", "playback." + command, new { @params = parameters });
        var filmPlayback = capability.DebugFilmPlayback;
        switch (command)
        {
            case "play":
                if (filmPlayback.HasOutput)
                    await filmPlayback.SetPausedAsync(false);
                else if (capability.Playback is { CanPlay: true } playPage)
                    await playPage.PlayAsync();
                else
                    throw DebugApiException.InvalidState("No active film output or playable film page.");
                return BuildFilmPlayerSnapshot();
            case "pause":
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output to pause.");
                await filmPlayback.SetPausedAsync(true);
                return BuildFilmPlayerSnapshot();
            case "toggle":
                if (filmPlayback.HasOutput)
                    await filmPlayback.PauseAsync();
                else if (capability.Playback is { CanPlay: true } togglePage)
                    await togglePage.PlayAsync();
                else
                    throw DebugApiException.InvalidState("No active film output or playable film page.");
                return BuildFilmPlayerSnapshot();
            case "stop":
                await capability.StopPlaybackAsync();
                return BuildFilmPlayerSnapshot();
            case "next":
                await capability.StepAsync(true);
                return BuildFilmPlayerSnapshot();
            case "previous":
            case "prev":
                await capability.StepAsync(false);
                return BuildFilmPlayerSnapshot();
            case "seek":
            {
                double sec = DebugParams.GetDouble(parameters, "seconds")
                    ?? DebugParams.GetDouble(parameters, "position")
                    ?? throw DebugApiException.InvalidArgument("seek requires numeric 'seconds'.");
                await capability.SeekPlaybackAsync(sec);
                return BuildFilmPlayerSnapshot();
            }
            case "rate":
            {
                double rateVal = DebugParams.GetDouble(parameters, "rate")
                    ?? DebugParams.GetDouble(parameters, "value")
                    ?? throw DebugApiException.InvalidArgument("rate requires numeric 'rate' or 'value'.");
                await filmPlayback.RateAsync(rateVal);
                return BuildFilmPlayerSnapshot();
            }
            case "aspect":
            {
                string? raw = DebugParams.GetString(parameters, "aspect") ?? DebugParams.GetString(parameters, "value");
                string? normalized = string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase)
                    ? null : raw.Trim();
                await filmPlayback.AspectAsync(normalized);
                return BuildFilmPlayerSnapshot();
            }
            case "volume":
            {
                double vol = DebugParams.GetDouble(parameters, "volume")
                    ?? DebugParams.GetDouble(parameters, "value")
                    ?? throw DebugApiException.InvalidArgument("volume requires numeric 'volume' or 'value' (0..1).");
                if (!double.IsFinite(vol) || vol < 0 || vol > 1)
                    throw DebugApiException.InvalidArgument($"volume {vol} out of range (0..1).");
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output.");
                filmPlayback.Volume = (int)Math.Round(vol * 100);
                return BuildFilmPlayerSnapshot();
            }
            case "mute":
            {
                bool muted = DebugParams.GetBool(parameters, "muted")
                    ?? DebugParams.GetBool(parameters, "value")
                    ?? !filmPlayback.IsMuted;
                await filmPlayback.SetMutedAsync(muted);
                return BuildFilmPlayerSnapshot();
            }
            case "pip_start":
            {
                if (!capability.DebugView.PlaybackViewControl.TrySetPip(true))
                    throw DebugApiException.InvalidState("Unable to start film PiP window (no active output).");
                return BuildFilmPlayerSnapshot();
            }
            case "pip_stop":
            {
                capability.DebugView.PlaybackViewControl.TrySetPip(false);
                return BuildFilmPlayerSnapshot();
            }
            case "fullscreen":
            {
                bool enabled = DebugParams.GetBool(parameters, "enabled")
                    ?? DebugParams.GetBool(parameters, "value")
                    ?? !capability.DebugView.PlaybackViewControl.IsFullscreenActive;
                if (!capability.DebugView.PlaybackViewControl.TrySetFullscreen(enabled))
                    throw DebugApiException.InvalidState("Unable to change film fullscreen state.");
                return BuildFilmPlayerSnapshot();
            }
            case "read_tracks":
            {
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output.");
                await filmPlayback.ReadTracksAsync();
                await filmPlayback.ReadChaptersAsync();
                await filmPlayback.ReadDiagnosticsAsync();
                return BuildFilmPlayerSnapshot();
            }
            case "select_video_track":
            {
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output.");
                int idx = ParseTrackIndex(parameters)
                    ?? throw DebugApiException.InvalidArgument("select_video_track requires integer 'id' or 'index'.");
                await filmPlayback.SelectVideoTrackAsync(idx);
                return BuildFilmPlayerSnapshot();
            }
            case "select_audio_track":
            {
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output.");
                int idx = ParseTrackIndex(parameters)
                    ?? throw DebugApiException.InvalidArgument("select_audio_track requires integer 'id' or 'index'.");
                await filmPlayback.SelectAudioTrackAsync(idx);
                return BuildFilmPlayerSnapshot();
            }
            case "select_subtitle_track":
            {
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output.");
                bool explicitDisable = DebugParams.GetBool(parameters, "disable") == true
                    || HasExplicitNullTrackParam(parameters);
                int? idx = ParseTrackIndex(parameters);
                if (explicitDisable || idx is < 0)
                {
                    await filmPlayback.SelectSubtitleTrackAsync(null);
                }
                else if (idx is { } selected)
                {
                    await filmPlayback.SelectSubtitleTrackAsync(selected);
                }
                else
                {
                    throw DebugApiException.InvalidArgument("select_subtitle_track requires 'id', 'index', or 'disable': true.");
                }
                return BuildFilmPlayerSnapshot();
            }
            case "select_chapter":
            {
                if (!filmPlayback.HasOutput)
                    throw DebugApiException.InvalidState("No active native film output.");
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                if (page.Chapters.Count == 0)
                    throw DebugApiException.InvalidState("Current film media has no chapters.");
                FilmChapterOption chapter;
                if (DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Chapters.Count)
                        throw DebugApiException.InvalidArgument($"Chapter index {idx} out of range (0..{page.Chapters.Count - 1}).");
                    chapter = page.Chapters[idx];
                }
                else if (DebugParams.GetString(parameters, "title") is { Length: > 0 } title)
                {
                    chapter = page.Chapters.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Chapter '{title}' not found.");
                }
                else
                {
                    throw DebugApiException.InvalidArgument("select_chapter requires 'index' or 'title'.");
                }
                await page.SelectChapterAsync(chapter);
                return BuildFilmPlayerSnapshot();
            }
            case "switch":
            {
                await filmPlayback.CommandAsync("switch");
                return BuildFilmPlayerSnapshot();
            }
            case "pan":
            {
                var provider = DebugParams.GetString(parameters, "provider") ?? DebugParams.GetString(parameters, "value")
                    ?? throw DebugApiException.InvalidArgument("pan requires 'provider'.");
                await filmPlayback.CommandAsync("pan", new { provider });
                return BuildFilmPlayerSnapshot();
            }
            case "quality":
            {
                var quality = DebugParams.GetString(parameters, "quality") ?? DebugParams.GetString(parameters, "value")
                    ?? throw DebugApiException.InvalidArgument("quality requires 'quality'.");
                await filmPlayback.CommandAsync("quality", new { quality });
                return BuildFilmPlayerSnapshot();
            }
            case "block":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                var attemptId = page.CurrentAttemptId;
                if (string.IsNullOrEmpty(attemptId))
                    throw DebugApiException.InvalidState("No active attempt to block.");
                await page.CommandAsync("block", new { attemptId });
                return BuildFilmPlayerSnapshot();
            }
            case "retry":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                if (!page.CanRetryMedia)
                    throw DebugApiException.InvalidState("No retryable film media attempt.");
                await page.RetryMediaAsync();
                return BuildFilmPlayerSnapshot();
            }
            case "select_candidate":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                string cid;
                if (DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Candidates.Count)
                        throw DebugApiException.InvalidArgument($"Candidate index {idx} out of range.");
                    cid = page.Candidates[idx].Id;
                }
                else if (DebugParams.GetString(parameters, "id") is { Length: > 0 } explicitId)
                {
                    cid = explicitId;
                }
                else throw DebugApiException.InvalidArgument("select_candidate requires 'index' or 'id'.");
                await page.SelectCandidateAsync(cid);
                return BuildFilmPlayerSnapshot();
            }
            case "select_episode":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                FilmEpisodeItem ep;
                if (DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Episodes.Count)
                        throw DebugApiException.InvalidArgument($"Episode index {idx} out of range.");
                    ep = page.Episodes[idx];
                }
                else if (DebugParams.GetString(parameters, "id") is { Length: > 0 } eid)
                {
                    ep = page.Episodes.FirstOrDefault(e => string.Equals(e.Identity, eid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Title, eid, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Episode '{eid}' not found.");
                }
                else throw DebugApiException.InvalidArgument("select_episode requires 'index' or 'id'.");
                await page.SelectEpisodeAsync(ep);
                return BuildFilmPlayerSnapshot();
            }
            case "select_source":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                FilmSelection src;
                if (DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Sources.Count)
                        throw DebugApiException.InvalidArgument($"Source index {idx} out of range.");
                    src = page.Sources[idx];
                }
                else if (DebugParams.GetString(parameters, "id") is { Length: > 0 } sid)
                {
                    src = page.Sources.FirstOrDefault(s => string.Equals(s.Id, sid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Label, sid, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Source '{sid}' not found.");
                }
                else throw DebugApiException.InvalidArgument("select_source requires 'index' or 'id'.");
                await page.BrowseSourceAsync(src);
                return BuildFilmPlayerSnapshot();
            }
            case "select_line":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                FilmSelection line;
                if (DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Lines.Count)
                        throw DebugApiException.InvalidArgument($"Line index {idx} out of range.");
                    line = page.Lines[idx];
                }
                else if (DebugParams.GetString(parameters, "id") is { Length: > 0 } lid)
                {
                    line = page.Lines.FirstOrDefault(l => string.Equals(l.Id, lid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l.Label, lid, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Line '{lid}' not found.");
                }
                else throw DebugApiException.InvalidArgument("select_line requires 'index' or 'id'.");
                await page.BrowseLineAsync(line);
                return BuildFilmPlayerSnapshot();
            }
            default:
                return null;
        }
    }

    private static int? ParseTrackIndex(JsonElement? parameters)
    {
        if (DebugParams.GetInt(parameters, "id") is { } idInt) return idInt;
        if (DebugParams.GetInt(parameters, "index") is { } idxInt) return idxInt;
        if (DebugParams.GetString(parameters, "id") is { Length: > 0 } idStr &&
            int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId))
            return parsedId;
        if (DebugParams.GetString(parameters, "index") is { Length: > 0 } idxStr &&
            int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIdx))
            return parsedIdx;
        return null;
    }

    private static bool HasExplicitNullTrackParam(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return false;
        return (obj.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Null) ||
               (obj.TryGetProperty("index", out var idxProp) && idxProp.ValueKind == JsonValueKind.Null);
    }
}
