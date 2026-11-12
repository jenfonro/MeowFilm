using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>One row of the current song's source list: a resolved or failed attempt of its session.</summary>
public sealed record MusicSourceChoice(string Key, string Provider, string Platform, string Quality, string State,
    bool Current, bool Selectable)
{
    public string QualitySymbol { get; init; } = "";
    public string Recording { get; init; } = "";
    public string Level { get; init; } = "";
}
/// <summary>One quality the player can switch the current playback to.</summary>
public sealed record MusicQualityChoice(string Key, string Label, bool Current);

public sealed partial class MusicCapability
{
    private CancellationTokenSource? sourceExpansion;
    public ObservableCollection<MusicSourceChoice> SourceChoices { get; } = [];
    public bool HasSourceChoices => SourceChoices.Count > 0;
    public bool HasConfirmedSourceChoices => SourceChoices.Any(choice => choice.Key != "auto" && choice.Selectable);
    public MusicSourceChoice? AutomaticSourceChoice => SourceChoices.FirstOrDefault(choice => choice.Key == "auto");
    public bool CanLoadMoreSources => CanSwitchSource && musicRecovery?.Recovering == false && sourceExpansion is null &&
        (musicRecovery?.Session?.CanExpand == true || musicRecovery?.Session?.Revision != selectionSourcesRevision ||
            musicRecovery?.Session?.Signature != savedQualitySignature);
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
    private static string LevelName(string level) => MusicQualityLabels.Name(
        MusicQualityPolicy.Spatial.Contains(level) ? level : MusicQualityPolicy.Category(level));
    public string CurrentSourceQuality => musicRecovery?.Media is { Target: not null } media && QualityVerified(media)
        ? LevelName(EffectiveLevel(media)) : "音质未知";
    public string CurrentSourceSymbol => musicRecovery?.Media is { Target: not null } media && QualityVerified(media)
        ? QualitySymbol(EffectiveLevel(media)) : "";
    public string CurrentSourceDescription => musicRecovery?.Media?.Target is { Official: false } target
        ? MusicSourceRow.PlatformName(target.Platform) + " · " + CurrentSource : CurrentSource;
    private static string QualitySymbol(string level) => MusicQualityPolicy.Category(level) switch
    {
        "standard" => "SD", "high" => "HQ", "lossless" => "SQ", "hires" => "HR",
        "master" => "M", "spatial" => "3D", "enhanced" => "E", _ => ""
    };
    public bool IsLoadingSources => sourceExpansion is not null;
    /// <summary>Enabled categories, best first; spatial is offered through its own levels.</summary>
    public IReadOnlyList<MusicQualityChoice> QualityChoices()
    {
        if (savedQualityPolicy is not { } policy) return [];
        string? level = musicRecovery?.Media is { Target: not null } media ? EffectiveLevel(media) : null;
        return new[] { "master", "hires", "lossless", "high", "standard", "enhanced" }
            .Where(category => MusicQualityPolicy.Exact(category, policy) is not null)
            .Select(category => new MusicQualityChoice(category, MusicQualityLabels.Name(category),
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
            explicitRecordings.Remove(oldest);
            foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(oldest + "\0", StringComparison.Ordinal)).ToArray())
                recordingChoices.Remove(key);
        }
        recordingPlatforms[row.SongId] = row.Platform;
        recordingChoices[row.SongId + "\0" + row.Platform] = row.SourceKey;
    }
    private void ForgetRecording(string songId)
    {
        recordingPlatforms.Remove(songId);
        explicitRecordings.Remove(songId);
        foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(songId + "\0", StringComparison.Ordinal)).ToArray())
            recordingChoices.Remove(key);
    }

    /// <summary>Projects the session's known attempts. Opening the list never resolves anything.</summary>
    private void UpdateSourceChoices()
    {
        SourceChoices.Clear();
        if (musicRecovery is { Session: { } session, Media.Target: not null } state && CurrentRecovery(state))
        {
            SourceChoices.Add(new("auto", "自动选择", "", "", "", state.Selection.Automatic, true));
            foreach (var attempt in session.Attempts.Values
                .Where(attempt => attempt.State != SourceAttemptState.Pending)
                .OrderBy(attempt => attempt.State == SourceAttemptState.Ok ? 0 : 1))
            {
                string provider = attempt.Target.Official ? MusicSourceRow.OfficialName(attempt.Target.Platform)
                    : attempt.Target.ProviderName is { Length: > 0 } name ? name : "JS 音源";
                bool ok = attempt.State == SourceAttemptState.Ok && attempt.Media is not null;
                string level = ok ? EffectiveLevel(attempt.Media!) : attempt.Target.Quality;
                string quality = level is "128k" or "192k" or "320k" ? level[..^1] + " kbps" : LevelName(level);
                if (ok && !QualityVerified(attempt.Media!)) quality += "?";
                bool selected = ok && state.Media is { } media && !state.Selection.Automatic &&
                    SourceIdentity(media.Target!) == SourceIdentity(attempt.Target) && media.Target!.Quality == attempt.Target.Quality;
                SourceChoices.Add(new(attempt.Key, provider, MusicSourceRow.PlatformName(attempt.Target.Platform), quality,
                    ok ? "可用" : "不可用", selected, ok)
                {
                    QualitySymbol = ok && QualityVerified(attempt.Media!) ? QualitySymbol(EffectiveLevel(attempt.Media!)) : "",
                    Recording = attempt.Target.SourceKey ?? "",
                    Level = level
                });
            }
        }
        Changed(nameof(HasSourceChoices)); Changed(nameof(HasConfirmedSourceChoices)); Changed(nameof(AutomaticSourceChoice));
        Changed(nameof(CanSwitchSource)); Changed(nameof(CanLoadMoreSources));
    }
    public void OpenSourceChoices() => UpdateSourceChoices();
    public void CloseSourceChoices()
    {
        sourceExpansion?.Cancel();
        sourceExpansion = null;
        SourceChoices.Clear();
        Changed(nameof(HasSourceChoices)); Changed(nameof(HasConfirmedSourceChoices)); Changed(nameof(AutomaticSourceChoice));
        Changed(nameof(CanLoadMoreSources)); Changed(nameof(IsLoadingSources));
    }
    /// <summary>Explores the next group of declared source qualities, cache first.</summary>
    public Task LoadMoreSourcesAsync()
    {
        if (!CanLoadMoreSources || musicRecovery is not { Session: not null } state) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        sourceExpansion = request;
        Changed(nameof(CanLoadMoreSources)); Changed(nameof(IsLoadingSources));
        var work = Run();
        eventWrites.Add(work); _ = Observe();
        return work;
        async Task Observe() { try { await work; } catch { } finally { eventWrites.Remove(work); } }
        async Task Run()
        {
            try { await (await CurrentSourceSession(state, request.Token)).ExpandAsync(request.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (CurrentRecovery(state)) { status = ErrorText(error); Changed(nameof(Status)); } }
            finally
            {
                if (ReferenceEquals(sourceExpansion, request)) sourceExpansion = null;
                request.Dispose();
                if (ReferenceEquals(musicRecovery, state) && SourceChoices.Count > 0) UpdateSourceChoices();
                Changed(nameof(CanLoadMoreSources)); Changed(nameof(IsLoadingSources));
            }
        }
    }

    /// <summary>Switch the current playback to one listed source. "自动选择" re-runs the automatic order.</summary>
    public Task SelectSourceChoiceAsync(MusicSourceChoice choice)
    {
        if (!CanSwitchSource || !choice.Selectable || musicRecovery is not { Session: { } session } state) return Task.CompletedTask;
        if (choice.Key == "auto")
            return SwitchAsync(state, (current, token) => SelectAutomatic(current, token), new(),
                "当前歌曲暂无可用音源", pauseWhenMissing: false, accepted: _ => ForgetRecording(state.Song.Id));
        if (!session.Attempts.TryGetValue(choice.Key, out var attempt) || attempt.Media is not { } media) return Task.CompletedTask;
        var selection = new MusicSelection(Source: media);
        return SwitchAsync(state, async (current, token) =>
        {
            await current.LoadExclusionsAsync(token);
            if (current.Excluded(media.Target!)) return null;
            if (ReferenceEquals(media, state.Media) && MusicSourceSession.Fresh(attempt) &&
                (!media.Target!.Official || media.Target.QualityPolicySignature == current.Signature) &&
                current.CanReuse(media) && HealthyCurrentInput(state)) return media;
            if (await CompletedPlaybackFileAsync(state, current, media, selection, token) is { } cached) return cached;
            if (!MusicSourceSession.Fresh(attempt) || media.Target is { Official: true } official &&
                (official.QualityPolicySignature != current.Signature || official.AccountScope is not { } scope ||
                    !application.IsOfficialPlaybackCurrent(official.Platform, scope, official.Generation)))
                media = await current.RefreshAsync(media.Target! with { ManualSource = true }, token);
            return media;
        }, selection, "当前歌曲暂无可用音源", pauseWhenMissing: false,
            accepted: next => RememberRecording(new(state.Song.Id, next.Target!.SourceKey ?? "", next.Target.Platform, "")));
    }
    private static async Task<ResolvedMusic?> SelectAutomatic(MusicSourceSession session, CancellationToken token)
    {
        session.ReleaseRecording();
        return await session.AutomaticAsync(token);
    }

    /// <summary>A manual quality for this playback only: never saved, never lowered automatically.</summary>
    public Task SwitchQualityAsync(string choice)
    {
        if (!CanSwitchSource || musicRecovery is not { Session: not null } state) return Task.CompletedTask;
        if (savedQualityPolicy is not { } policy || MusicQualityPolicy.Exact(choice, policy) is not { } tier)
        {
            state.Switching?.Cancel();
            return Notify("未匹配到对应音质");
        }
        // Even selecting the playing tier supersedes an older pending choice and locks this tier.
        if (state.Media is { } playing && tier.Levels.Contains(EffectiveLevel(playing)) &&
            state.Session.CanReuse(playing) && HealthyCurrentInput(state))
        {
            state.Switching?.Cancel();
            state.Selection = new(Tier: tier);
            if (playing.Target!.ManualSource)
            {
                state.Media = playing with { Target = playing.Target with { ManualSource = false } };
                state.Session.Adopt(state.Media);
            }
            state.RecoveryDisabled = false;
            if (SourceChoices.Count > 0) UpdateSourceChoices();
            return Task.CompletedTask;
        }
        return SwitchAsync(state, async (session, token) =>
        {
            await session.LoadExclusionsAsync(token);
            if (state.Media is { } media && tier.Levels.Contains(EffectiveLevel(media)) &&
                session.CanReuse(media) && HealthyCurrentInput(state)) return media;
            return await session.ExactAsync(tier, token);
        }, new(Tier: tier), "未匹配到对应音质", pauseWhenMissing: false);
    }
    private bool HealthyCurrentInput(MusicRecovery state) => OwnsCurrent && playback.HasMedia &&
        playback is INativePlaybackContinuation { LastErrorCode: null } && state.SwitchingFailure is null;
    /// <summary>Exclude this provider/platform permanently, then find the same tier or pause.</summary>
    public Task ReportWrongSourceAsync()
    {
        if (!CanSwitchSource || musicRecovery is not { Session: not null, Media.Target: { } target } state) return Task.CompletedTask;
        string provider = target.Installation ?? MusicLibraryOwner.OfficialProvider;
        string name = target.Official ? MusicSourceRow.OfficialName(target.Platform) : target.ProviderName ?? "JS 音源";
        string level = EffectiveLevel(state.Media!);
        var tier = MusicQualityPolicy.Category(level) == "spatial" ? new MusicQualityPolicy.Tier("spatial", [level]) :
            MusicQualityPolicy.SourceTiers.FirstOrDefault(value => value.Levels.Contains(level)) ??
                new MusicQualityPolicy.Tier(MusicQualityPolicy.Category(level), [level]);
        state.Switching?.Cancel();
        sourceExpansion?.Cancel();
        state.Expiry?.Cancel();
        // Fence every resume path before the first asynchronous write or search.
        state.Session.Exclude(provider, target.Platform);
        InvalidateFeedback();
        var stopped = ((INativePlaybackHandoff)playback).StopOutputForHandoffAsync();
        var persisted = PersistExclusion();
        return SwitchAsync(state, async (session, token) =>
        {
            session.Exclude(provider, target.Platform);
            session.ReleaseRecording();
            return await session.ExactAsync(tier, token, manualSource: target.ManualSource);
        }, new(Tier: tier, ManualSource: target.ManualSource), "未找到同音质的其他音源，已暂停",
            pauseWhenMissing: true, beforeSelection: persisted);
        async Task PersistExclusion()
        {
            await stopped;
            // A newer source choice may cancel the replacement, not the user's durable report.
            await CallSettledReceipt("music.sourceExclusions.add", new { songId = state.Song.Id, provider,
                platform = target.Platform, providerName = name, title = state.Song.Title, artist = state.Song.Artist }, lifetime.Token);
            exclusionsRevision++;
            state.Session!.Exclude(provider, target.Platform);
            ForgetRecording(state.Song.Id);
        }
    }
    internal Task Notify(string message)
    {
        status = message; Changed(nameof(Status));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hand off only to a prepared, positioned input. A failed normal replacement leaves the
    /// old stream untouched; a wrong-source report has already stopped its rejected output.
    /// </summary>
    private Task SwitchAsync(MusicRecovery state, Func<MusicSourceSession, CancellationToken, Task<ResolvedMusic?>> select,
        MusicSelection selection, string missing, bool pauseWhenMissing, Action<ResolvedMusic>? accepted = null,
        Task? beforeSelection = null)
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
            bool committed = false;
            try
            {
                if (beforeSelection is not null) await beforeSelection;
                request.Token.ThrowIfCancellationRequested();
                request.CancelAfter(TimeSpan.FromSeconds(60));
                var session = await CurrentSourceSession(state, request.Token);
                ResolvedMusic? fileRetry = null;
                while (true)
                {
                    var next = fileRetry is { } refreshed
                        ? selection.Automatic ? await session.AutomaticAsync(request.Token, refreshed)
                            : selection.Tier is { } tier ? await session.ExactAsync(tier, request.Token, refreshed, selection.Manual) : refreshed
                        : await select(session, request.Token);
                    fileRetry = null;
                    request.Token.ThrowIfCancellationRequested();
                    if (!CurrentRecovery(state)) throw new OperationCanceledException(request.Token);
                    if (next is null || !selection.Allows(next))
                    {
                        if (pauseWhenMissing && ReferenceEquals(state.Switching, request) && OwnsCurrent) await playback.PauseAsync();
                        status = missing; Changed(nameof(Status));
                        return;
                    }
                    if (session.Signature != savedQualitySignature) throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                    bool published = false;
                    void Accepted()
                    {
                        published = true;
                        committed = true;
                        state.Selection = selection;
                        state.RecoveryDisabled = false;
                        if (state.Media?.Target is { } currentTarget && currentTarget.ManualSource != selection.Manual)
                        {
                            state.Media = state.Media with { Target = currentTarget with { ManualSource = selection.Manual } };
                            state.Session!.Adopt(state.Media);
                        }
                        accepted?.Invoke(next);
                    }
                    if (ReferenceEquals(next, state.Media) && HealthyCurrentInput(state)) { Accepted(); return; }
                    if (next.Uri is not null)
                        next = await CompletedPlaybackFileAsync(state, session, next, selection, request.Token) ?? next;
                    if (selection.Manual)
                        next = next with { Target = next.Target! with { ManualSource = true } };
                    try { await HandoffAsync(state, next, request.Token, Accepted); return; }
                    catch (Exception error) when (!published && next.Uri is null &&
                        UnusablePlaybackFile(error) && CurrentRecovery(state))
                    {
                        fileRetry = await RefreshMissingPlaybackFileAsync(state, session, next, request.Token);
                        if (selection.Source is not null && fileRetry is null)
                        { status = missing; Changed(nameof(Status)); return; }
                        // Tier/automatic selection re-evaluates the refreshed receipt in its
                        // original order. A lower actual quality never bypasses that order.
                    }
                    catch (Exception error) when (!published && selection.Source is null &&
                        error is not OperationCanceledException && CurrentRecovery(state) &&
                        (error is PlaybackException playbackError && RetryablePreparation(playbackError.Code) ||
                         error is MusicOperationException operation && RetryablePreparation(operation.Code)))
                    {
                        session.Reject(next, error is PlaybackException native ? native.Code : ((MusicOperationException)error).Code);
                        // This source failed before publication. Keep the old stream and try every other same-tier candidate.
                    }
                    catch (Exception error) when (!published && selection.Source is not null &&
                        error is not OperationCanceledException && CurrentRecovery(state) &&
                        (error is PlaybackException mediaError && RetryablePreparation(mediaError.Code) ||
                            error is MusicOperationException operation && RetryablePreparation(operation.Code)))
                    {
                        session.Reject(next, error is PlaybackException native ? native.Code : ((MusicOperationException)error).Code);
                        throw;
                    }
                }
            }
            catch
            {
                if (pauseWhenMissing && ReferenceEquals(state.Switching, request) && CurrentRecovery(state) && OwnsCurrent)
                    await playback.PauseAsync();
                throw;
            }
            finally
            {
                try
                {
                    if (ReferenceEquals(state.Switching, request))
                    {
                        state.Switching = null; state.Recovering = false;
                        var failure = state.SwitchingFailure;
                        state.SwitchingFailure = null;
                        // Successful publication clears the old input's error. A newer
                        // queued choice owns any remaining error; only the latest drains it.
                        if (failure is not null && CurrentRecovery(state) && (!pauseWhenMissing || committed))
                        {
                            bool expired = state.Cached && !state.Attempted && failure != PlaybackError.ReadRevoked;
                            if (!await RecoverMusic(state, expired) && CurrentRecovery(state)) MusicRecoveryFailed();
                        }
                    }
                }
                finally
                {
                    request.Dispose(); done.TrySetResult();
                    PlaybackSourceBindings(); Changed(nameof(CanSwitchSource));
                    if (SourceChoices.Count > 0) UpdateSourceChoices();
                    CheckMediaDuration();
                }
            }
        });
    }
    private static bool RetryablePreparation(string code) => RefreshableMusicError(code) || UnavailableSourceCandidate(code) ||
        code is PlaybackError.OpenFailed or PlaybackError.PlaybackFailed or PlaybackError.HttpFormatUnsupported or PlaybackError.ReadFailed;
    private static bool UnusablePlaybackFile(Exception error) =>
        (error is PlaybackException native ? native.Code : error is MusicOperationException music ? music.Code : null) is
            "CACHE_MISS" or PlaybackError.OpenFailed or PlaybackError.ReadFailed or PlaybackError.ReadTruncated or
            PlaybackError.ReadChanged or PlaybackError.ReadFormatUnsupported;
    private async Task HandoffAsync(MusicRecovery state, ResolvedMusic next, CancellationToken token, Action accepted)
    {
        if (playback is not INativePlaybackHandoff port || !OwnsCurrent) throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
        token.ThrowIfCancellationRequested();
        if (!CurrentRecovery(state)) throw new OperationCanceledException(token);
        await state.Session!.LoadExclusionsAsync(token);
        if (state.Session.Excluded(next.Target!)) throw new MusicOperationException("SOURCE_EXCLUDED");
        var pending = BeginPendingMedia(state, next, reopening: false, token);
        try
        {
            if (pending.Media.Uri is null || pending.Media.Bytes is not null)
            {
                var lease = await OpenPlaybackFileAsync(state, pending.Media, pending.Access.Token);
                if (lease is null) throw new MusicOperationException("CACHE_MISS");
                await port.PrepareReadHandoffAsync(lease, pending.Access.Token);
            }
            else await port.PrepareHandoffAsync(pending.Media.Uri, pending.Media.Headers, pending.Access.Token);
            EnsurePendingMedia(state, pending);
            await state.Session!.LoadExclusionsAsync(pending.Access.Token);
            if (state.Session.Excluded(next.Target!)) throw new MusicOperationException("SOURCE_EXCLUDED");
            if (MediaDurationMismatch(state.Song, port.PreparedHandoffDurationSeconds))
                throw new MusicOperationException("RECORDING_MISMATCH");
            await port.CommitHandoffAsync(() =>
            {
                SampleFeedback();
                if (!CommitPendingMedia(state, pending, out _, published: true)) return;
                accepted();
                AcceptOfficialSong(state.Song, next.Target);
                UpdateFeedbackSelection();
                state.Attempted = false;
                ScheduleMusicExpiry(state);
                UpdateRecordingCandidates(); UpdateLyricSources();
                PlaybackSourceBindings();
            }, pending.Access.Token);
        }
        catch
        {
            await port.DiscardHandoffAsync();
            DiscardPendingMedia(state);
            throw;
        }
        if (NeedsLyrics) _ = RestoreLyricsAsync();
        // A handed-off input emits no second Started; confirm the newly playing source here.
        ConfirmRecoveredSourceStarted(state);
    }

    private static double ExpectedDuration(SongRow song) => song.Raw.TryGetProperty("duration", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
    private static bool MediaDurationMismatch(SongRow song, double actual) =>
        double.IsFinite(actual) && actual > 0 && ExpectedDuration(song) is > 0 and var expected && Math.Abs(actual - expected) > 10;

    /// <summary>A resolved network input whose real duration contradicts the recording is the wrong
    /// recording: drop it for this session and change source. Unknown durations never mismatch.</summary>
    private void CheckMediaDuration()
    {
        if (musicRecovery is not { Session: { } session, Media: { } media } state || state.Recovering ||
            state.PendingFailure == "RECORDING_MISMATCH" || !CurrentRecovery(state) || !OwnsCurrent || !playback.HasMedia) return;
        double actual = playback.DurationSeconds;
        if (actual <= 0 || ExpectedDuration(state.Song) <= 0 || !double.IsFinite(actual)) return;
        if (!MediaDurationMismatch(state.Song, actual)) return;
        session.Reject(media, "RECORDING_MISMATCH");
        InvalidateFeedback();
        state.PendingFailure = "RECORDING_MISMATCH";
        state.RejectedOutput = playback is INativePlaybackHandoff port ? port.StopOutputForHandoffAsync() : playback.StopAsync();
        if (!state.Opening) TrackMusicRecovery(RecoverRejectedMusic(state));
    }
    private async Task<bool> RecoverRejectedMusic(MusicRecovery state)
    {
        await state.RejectedOutput;
        if (!CurrentRecovery(state)) return false;
        bool recovered = await RecoverMusic(state, expired: false, mismatch: true);
        if (!recovered && CurrentRecovery(state)) MusicRecoveryFailed();
        return recovered;
    }
}
