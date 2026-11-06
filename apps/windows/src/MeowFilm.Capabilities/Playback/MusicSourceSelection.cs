using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>One row of the current song's source list: a resolved or failed attempt of its session.</summary>
public sealed record MusicSourceChoice(string Key, string Provider, string Platform, string Quality, string State,
    bool Current, bool Selectable);
/// <summary>One quality the player can switch the current playback to.</summary>
public sealed record MusicQualityChoice(string Key, string Label, bool Current);

public sealed partial class MusicCapability
{
    private CancellationTokenSource? sourceExpansion;
    public ObservableCollection<MusicSourceChoice> SourceChoices { get; } = [];
    public bool HasSourceChoices => SourceChoices.Count > 0;
    public bool CanLoadMoreSources => CanSwitchSource && musicRecovery?.Recovering == false && sourceExpansion is null &&
        musicRecovery?.Session?.CanExpand == true;
    public bool CanSwitchSource => !stopping && musicRecovery is { Opening: false, Session: not null } state && (!state.Recovering || state.Switching is not null) &&
        CurrentRecovery(state) && state.Media?.Target is not null && playback is INativePlaybackHandoff && OwnsCurrent;
    /// <summary>Hidden without a current online playback; "匹配中" while a source is being selected.</summary>
    public string CurrentQualityLabel
    {
        get
        {
            if (current is not { IsOnline: true } song || musicRecovery is not { } state || state.Song.EntryId != song.EntryId ||
                !CurrentRecovery(state)) return "";
            if (state.Opening || state.Recovering) return "匹配中";
            if (state.Media?.Target is null) return "";
            if (!QualityVerified(state.Media)) return "音质未知";
            return QualityName(EffectiveLevel(state.Media));
        }
    }
    public bool HasQualityButton => CurrentQualityLabel.Length > 0;
    private static string QualityName(string level) => MusicQualityPolicy.Category(level) switch
    {
        "standard" => "标准", "high" => "高音质", "lossless" => "无损", "hires" => "Hi-Res", "master" => "母带",
        "spatial" => "空间", "enhanced" => "增强", _ => "音质未知"
    };
    private static string LevelName(string level) => level switch
    {
        "dolby" => "杜比全景声", "sky" => "沉浸环绕声", "vivid" => "臻音全景声", _ => QualityName(level)
    };
    /// <summary>Enabled categories, best first; spatial is offered through its own levels.</summary>
    public IReadOnlyList<MusicQualityChoice> QualityChoices()
    {
        if (savedQualityPolicy is not { } policy) return [];
        string? level = musicRecovery?.Media is { Target: not null } media ? EffectiveLevel(media) : null;
        return new[] { "master", "hires", "lossless", "high", "standard", "enhanced" }
            .Where(category => MusicQualityPolicy.Exact(category, policy) is not null)
            .Select(category => new MusicQualityChoice(category, QualityName(category),
                level is not null && MusicQualityPolicy.Category(level) == category)).ToArray();
    }
    public IReadOnlyList<MusicQualityChoice> SpatialChoices()
    {
        if (savedQualityPolicy is not { } policy) return [];
        string? level = musicRecovery?.Media is { Target: not null } media ? EffectiveLevel(media) : null;
        return MusicQualityPolicy.Levels("spatial", policy)
            .Select(spatial => new MusicQualityChoice(spatial, LevelName(spatial), level == spatial)).ToArray();
    }

    private void RememberRecording(MusicRecordingRow row)
    {
        if (!recordingPlatforms.ContainsKey(row.SongId) && recordingPlatforms.Count >= 256)
        {
            string oldest = recordingPlatforms.Keys.First();
            recordingPlatforms.Remove(oldest);
            foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(oldest + "\0", StringComparison.Ordinal)).ToArray())
                recordingChoices.Remove(key);
        }
        recordingPlatforms[row.SongId] = row.Platform;
        recordingChoices[row.SongId + "\0" + row.Platform] = row.SourceKey;
    }
    private void ForgetRecording(string songId)
    {
        recordingPlatforms.Remove(songId);
        foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(songId + "\0", StringComparison.Ordinal)).ToArray())
            recordingChoices.Remove(key);
    }

    /// <summary>Projects the session's known attempts. Opening the list never resolves anything.</summary>
    private void UpdateSourceChoices()
    {
        SourceChoices.Clear();
        if (musicRecovery is { Session: { } session, Media.Target: { } active } state && CurrentRecovery(state))
        {
            SourceChoices.Add(new("auto", "自动选择", "", "", "", active.Automatic, true));
            var policy = session.Policy;
            foreach (var attempt in session.Attempts.Values
                .Where(attempt => attempt.State != SourceAttemptState.Pending)
                .OrderBy(attempt => attempt.State == SourceAttemptState.Ok ? 0 : 1)
                .ThenBy(attempt => attempt.Media is { } media ? MusicQualityPolicy.Priority(EffectiveLevel(media), policy) : int.MaxValue))
            {
                string provider = attempt.Target.Official ? MusicSourceRow.OfficialName(attempt.Target.Platform)
                    : session.ProviderName(attempt.Target.Installation) is { Length: > 0 } name ? name : "JS 音源";
                bool ok = attempt.State == SourceAttemptState.Ok && attempt.Media is not null;
                string quality = ok ? LevelName(EffectiveLevel(attempt.Media!)) + (QualityVerified(attempt.Media!) ? "" : "?")
                    : LevelName(attempt.Target.Quality);
                bool selected = ok && state.Media is { } media && !active.Automatic &&
                    SourceIdentity(media.Target!) == SourceIdentity(attempt.Target) && media.Target!.Quality == attempt.Target.Quality;
                SourceChoices.Add(new(attempt.Key, provider, MusicSourceRow.PlatformName(attempt.Target.Platform), quality,
                    ok ? "可用" : "不可用", selected, ok));
            }
        }
        Changed(nameof(HasSourceChoices)); Changed(nameof(CanSwitchSource)); Changed(nameof(CanLoadMoreSources));
    }
    public void OpenSourceChoices() => UpdateSourceChoices();
    public void CloseSourceChoices()
    {
        sourceExpansion?.Cancel();
        sourceExpansion = null;
        SourceChoices.Clear();
        Changed(nameof(HasSourceChoices)); Changed(nameof(CanLoadMoreSources));
    }
    /// <summary>Explores the next automatic tier: every queued source of it, cache first.</summary>
    public Task LoadMoreSourcesAsync()
    {
        if (!CanLoadMoreSources || musicRecovery is not { Session: { } session } state) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        sourceExpansion = request;
        Changed(nameof(CanLoadMoreSources));
        var work = Run();
        eventWrites.Add(work); _ = Observe();
        return work;
        async Task Observe() { try { await work; } catch { } finally { eventWrites.Remove(work); } }
        async Task Run()
        {
            try { await session.ExpandAsync(request.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (CurrentRecovery(state)) { status = ErrorText(error); Changed(nameof(Status)); } }
            finally
            {
                if (ReferenceEquals(sourceExpansion, request)) sourceExpansion = null;
                request.Dispose();
                if (ReferenceEquals(musicRecovery, state) && SourceChoices.Count > 0) UpdateSourceChoices();
                Changed(nameof(CanLoadMoreSources));
            }
        }
    }

    /// <summary>Switch the current playback to one listed source. "自动选择" re-runs the automatic order.</summary>
    public Task SelectSourceChoiceAsync(MusicSourceChoice choice)
    {
        if (!CanSwitchSource || !choice.Selectable || musicRecovery is not { Session: { } session } state) return Task.CompletedTask;
        if (choice.Key == "auto")
            return SwitchAsync(state, token => session.AutomaticAsync(token)!, "当前歌曲暂无可用音源", pauseWhenMissing: false,
                accepted: _ => ForgetRecording(state.Song.Id));
        if (!session.Attempts.TryGetValue(choice.Key, out var attempt) || attempt.Media is not { } media) return Task.CompletedTask;
        return SwitchAsync(state, async token =>
        {
            await session.LoadExclusionsAsync(token);
            if (session.Excluded(media.Target!)) return null;
            if (!MusicSourceSession.Fresh(attempt))
            {
                media = await ResolveMusicTarget(state.Song, media.Target!, token);
                attempt.Media = media;
                attempt.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return media with { Target = media.Target! with { Automatic = false } };
        },
            "当前歌曲暂无可用音源", pauseWhenMissing: false,
            accepted: next => RememberRecording(new(state.Song.Id, next.Target!.SourceKey ?? "", next.Target.Platform, "")));
    }
    /// <summary>A manual quality for this playback only: never saved, never lowered automatically.</summary>
    public Task SwitchQualityAsync(string choice)
    {
        if (!CanSwitchSource || musicRecovery is not { Session: { } session } state) return Task.CompletedTask;
        if (MusicQualityPolicy.Exact(choice, session.Policy) is not { } tier)
            return Notify("未匹配到对应音质");
        if (state.Media is { } media && tier.Levels.Contains(EffectiveLevel(media))) return Task.CompletedTask;
        return SwitchAsync(state, async token => await session.ExactAsync(tier, token) is { } next
            ? next with { Target = next.Target! with { Automatic = false } } : null, "未匹配到对应音质", pauseWhenMissing: false);
    }
    /// <summary>
    /// Persistently excludes this song's current provider on this platform, then looks for the same
    /// quality elsewhere. Without a same-quality alternative the song pauses; it never skips.
    /// </summary>
    public Task ReportWrongSourceAsync()
    {
        if (!CanSwitchSource || musicRecovery is not { Session: { } session, Media.Target: { } target } state) return Task.CompletedTask;
        string provider = target.Installation ?? MusicLibraryOwner.OfficialProvider;
        string name = target.Official ? MusicSourceRow.OfficialName(target.Platform)
            : session.ProviderName(target.Installation) is { Length: > 0 } script ? script : "JS 音源";
        string level = EffectiveLevel(state.Media!);
        var tier = MusicQualityPolicy.Exact(MusicQualityPolicy.Category(level) == "spatial" ? level : MusicQualityPolicy.Category(level),
            session.Policy) ?? new MusicQualityPolicy.Tier(MusicQualityPolicy.Category(level), [level]);
        return SwitchAsync(state, async token =>
        {
            await CallSettledReceipt("music.sourceExclusions.add", new { songId = state.Song.Id, provider,
                platform = target.Platform, providerName = name, title = state.Song.Title, artist = state.Song.Artist }, token);
            exclusionsRevision++;
            session.Exclude(provider, target.Platform);
            try { return await session.ExactAsync(tier, token) is { } next ? next with { Target = next.Target! with { Automatic = false } } : null; }
            catch { if (CurrentRecovery(state) && OwnsCurrent && playback.IsPlaying) await playback.PauseAsync(); throw; }
        }, "未找到同音质的其他音源，已暂停", pauseWhenMissing: true);
    }
    private Task Notify(string message)
    {
        status = message; Changed(nameof(Status));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve while the old stream keeps playing; hand off only to a prepared, positioned input.
    /// A failed or missing replacement leaves the old playback untouched.
    /// </summary>
    private Task SwitchAsync(MusicRecovery state, Func<CancellationToken, Task<ResolvedMusic?>> select, string missing,
        bool pauseWhenMissing, Action<ResolvedMusic>? accepted = null)
    {
        if (!CurrentRecovery(state) || state.Recovering && state.Switching is null) return Task.CompletedTask;
        state.Switching?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        state.Switching = request;
        sourceExpansion?.Cancel();
        state.Recovering = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Work = done.Task;
        PlaybackSourceBindings(); Changed(nameof(CanSwitchSource));
        return RunAsync(async () =>
        {
            try
            {
                request.Token.ThrowIfCancellationRequested();
                request.CancelAfter(TimeSpan.FromSeconds(60));
                var next = await select(request.Token);
                if (!CurrentRecovery(state)) throw new OperationCanceledException(request.Token);
                if (next is null)
                {
                    if (pauseWhenMissing && OwnsCurrent && playback.IsPlaying) await playback.PauseAsync();
                    status = missing; Changed(nameof(Status));
                    return;
                }
                if (state.RecoveryDisabled || next.Target?.QualityPolicySignature is { } policy && policy != savedQualitySignature)
                    throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                try { await HandoffAsync(state, next, request.Token); }
                catch
                {
                    if (pauseWhenMissing && CurrentRecovery(state) && OwnsCurrent && playback.IsPlaying) await playback.PauseAsync();
                    throw;
                }
                accepted?.Invoke(next);
            }
            finally
            {
                if (ReferenceEquals(state.Switching, request)) { state.Switching = null; state.Recovering = false; }
                request.Dispose(); done.TrySetResult();
                PlaybackSourceBindings(); Changed(nameof(CanSwitchSource));
                if (SourceChoices.Count > 0) UpdateSourceChoices();
            }
        });
    }
    private async Task HandoffAsync(MusicRecovery state, ResolvedMusic next, CancellationToken token)
    {
        if (playback is not INativePlaybackHandoff port || !OwnsCurrent) throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
        token.ThrowIfCancellationRequested();
        if (!CurrentRecovery(state)) throw new OperationCanceledException(token);
        var old = state.Media!;
        bool changedSource = SourceIdentity(old.Target!) != SourceIdentity(next.Target!);
        var pending = BeginPendingMedia(state, next, changedSource, token);
        try
        {
            if (next.Uri is null && audioFileCache is not null && next.Target is { AccountScope: not null, QualityCacheSignature: not null } cached)
            {
                var lease = await audioFileCache.OpenAsync(cached.AccountScope, state.Song.Id, cached.QualityCacheSignature, pending.Access.Token);
                if (lease is null) throw new MusicOperationException("CACHE_MISS");
                try
                {
                    if (next.Receipt is not { } receipt || !receipt.TryGetProperty("offlineAllowed", out var offline) || offline.ValueKind != JsonValueKind.True)
                        await QualityCall("music.official.authorize", OfficialInput(state.Song, cached, false, false), pending.Access.Token);
                }
                catch { await lease.DisposeAsync(); throw; }
                await port.PrepareReadHandoffAsync(lease, pending.Access.Token);
            }
            else
            {
                if (next.Uri is null) throw new MusicOperationException("CACHE_MISS");
                await port.PrepareHandoffAsync(next.Uri, next.Headers, pending.Access.Token);
            }
            EnsurePendingMedia(state, pending);
            await state.Session!.LoadExclusionsAsync(pending.Access.Token);
            if (state.Session.Excluded(next.Target!)) throw new MusicOperationException("SOURCE_EXCLUDED");
            double expected = state.Song.Raw.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds) ? seconds : 0;
            double actual = port.PreparedHandoffDurationSeconds;
            if (expected > 0 && actual > 0 && Math.Abs(expected - actual) > Math.Max(3, Math.Min(expected, actual) * .02))
            {
                state.Session.Reject(next, "RECORDING_MISMATCH");
                throw new MusicOperationException("RECORDING_MISMATCH");
            }
            await port.CommitHandoffAsync(pending.Access.Token);
            if (!CommitPendingMedia(state, pending, out _)) throw new OperationCanceledException(token);
        }
        catch
        {
            await port.DiscardHandoffAsync();
            DiscardPendingMedia(state);
            throw;
        }
        if (changedSource && recorded) CompleteFeedback("source-change");
        AcceptOfficialSong(state.Song, next.Target);
        if (changedSource) SelectFeedback(state.Song, currentFeedbackAutomatic, currentRecommendationFeedback);
        UpdateFeedbackSelection();
        state.Attempted = false; state.DurationChecked = false; state.Cached = next.Uri is null;
        playbackInstallation = next.Target!.Installation;
        ScheduleMusicExpiry(state);
        UpdateRecordingCandidates(); UpdateLyricSources();
        PlaybackSourceBindings();
        if (IsLyrics) _ = RestoreLyricsAsync();
        // A handed-off input emits no second Started; confirm the newly playing source here.
        ConfirmRecoveredSourceStarted(state);
    }

    /// <summary>A resolved network input whose real duration contradicts the recording is the wrong
    /// recording: drop it for this session and change source. Unknown durations never mismatch.</summary>
    private void CheckMediaDuration()
    {
        if (musicRecovery is not { Session: { } session, Media: { Uri: not null } media } state || state.DurationChecked ||
            state.Opening || state.Recovering || !CurrentRecovery(state) || !OwnsCurrent || !playback.HasMedia) return;
        double actual = playback.DurationSeconds;
        double expected = state.Song.Raw.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : 0;
        if (actual <= 0 || expected <= 0 || !double.IsFinite(actual) || !double.IsFinite(expected)) return;
        state.DurationChecked = true;
        if (Math.Abs(actual - expected) <= Math.Max(3, Math.Min(actual, expected) * .02)) return;
        session.Reject(media, "RECORDING_MISMATCH");
        TrackMusicRecovery(RecoverMusic(state, expired: false, mismatch: true));
    }
}
