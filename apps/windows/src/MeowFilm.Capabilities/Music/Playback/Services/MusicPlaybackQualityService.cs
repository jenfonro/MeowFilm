using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
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

/// <summary>
/// Standalone Playback service owning source/quality choices projection, manual source/quality switching,
/// seamless handoff preparation/commit, and wrong-source / wrong-recording reporting.
/// </summary>
internal sealed class MusicPlaybackQualityService(
    MusicRuntimeContext ctx,
    MusicSettingsController settings,
    MusicPlaybackSourceService sourceService,
    MusicPlaybackCacheService cacheService,
    MusicPlaybackRecoveryService recoveryService,
    MusicPlaybackMetadataService metadataService,
    Func<MusicPlaybackTelemetry> readTelemetry,
    Func<string> getCurrentSource,
    Action<string> notify)
{
    private CancellationTokenSource? sourceExpansion;

    internal event Action? PlaybackSourceBindingsRequested;
    internal event Action? SourcesUpdatedForLyrics;
    internal event Action? RestoreLyricsIfNeeded;
    internal event Action<SongRow, MusicOnlineTarget?>? OfficialSongAccepted;

    internal ObservableCollection<MusicSourceChoice> SourceChoices { get; } = [];
    internal bool HasSourceChoices => SourceChoices.Count > 0;
    internal bool HasConfirmedSourceChoices => SourceChoices.Any(choice => choice.Key != "auto" && choice.Selectable);
    internal bool HasEmptySources => !HasConfirmedSourceChoices && !IsLoadingSources;
    internal MusicSourceChoice? AutomaticSourceChoice => SourceChoices.FirstOrDefault(choice => choice.Key == "auto");
    internal bool IsLoadingSources => sourceExpansion is not null;

    internal bool CanLoadMoreSources => CanSwitchSource && recoveryService.ActiveRecovery?.Recovering == false && sourceExpansion is null &&
        (recoveryService.ActiveRecovery?.Session?.CanExpand == true ||
         recoveryService.ActiveRecovery?.Session?.Revision != sourceService.SelectionSourcesRevision ||
         recoveryService.ActiveRecovery?.Session?.Signature != settings.SavedQualitySignature);

    internal bool CanSwitchSource => !ctx.Stopping && recoveryService.ActiveRecovery is { Opening: false, Session: not null } state &&
        (!state.Recovering || state.Switching is not null) &&
        recoveryService.CurrentRecovery(state) && state.Media?.Target is not null && readTelemetry().OwnsCurrent;

    internal bool CanExcludeCurrentRecording => CanSwitchSource &&
        recoveryService.ActiveRecovery is { Session: { } session, Media.Target.SourceKey: { Length: > 0 } sourceKey } &&
        session.HasOtherUnexcludedRecording(sourceKey);

    internal string CurrentQualityLabel
    {
        get
        {
            var t = readTelemetry();
            if (t.Current is not { IsOnline: true } song || recoveryService.ActiveRecovery is not { } state ||
                state.Song.EntryId != song.EntryId || !recoveryService.CurrentRecovery(state)) return "";
            if (state.Opening || state.Recovering) return "匹配中";
            if (state.Media?.Target is null) return "";
            if (!MusicPlaybackSourceService.QualityVerified(state.Media)) return "音质未知";
            return QualityName(MusicPlaybackSourceService.EffectiveLevel(state.Media));
        }
    }

    internal bool HasQualityButton => CurrentQualityLabel.Length > 0;

    private static string QualityName(string level) => MusicQualityPolicy.Category(level) switch
    {
        "standard" => "标准", "high" => "高音质", "lossless" => "无损", "hires" => "Hi-Res", "master" => "母带",
        "spatial" => "空间", "enhanced" => "增强", _ => "音质未知"
    };

    private static string LevelName(string level) => MusicQualityLabels.Name(
        MusicQualityPolicy.Spatial.Contains(level) ? level : MusicQualityPolicy.Category(level));

    internal string CurrentSourceQuality => recoveryService.ActiveRecovery?.Media is { Target: not null } media && MusicPlaybackSourceService.QualityVerified(media)
        ? LevelName(MusicPlaybackSourceService.EffectiveLevel(media)) : "音质未知";

    internal string CurrentSourceSymbol => recoveryService.ActiveRecovery?.Media is { Target: not null } media && MusicPlaybackSourceService.QualityVerified(media)
        ? QualitySymbol(MusicPlaybackSourceService.EffectiveLevel(media)) : "";

    internal string CurrentSourceDescription => recoveryService.ActiveRecovery?.Media?.Target is { Official: false } target
        ? MusicSourceRow.PlatformName(target.Platform) + " · " + getCurrentSource() : getCurrentSource();

    private static string QualitySymbol(string level) => MusicQualityPolicy.Category(level) switch
    {
        "standard" => "SD", "high" => "HQ", "lossless" => "SQ", "hires" => "HR",
        "master" => "M", "spatial" => "3D", "enhanced" => "E", _ => ""
    };

    internal IReadOnlyList<MusicQualityChoice> QualityChoices()
    {
        if (settings.SavedQualityPolicy is not { } policy) return [];
        string? level = recoveryService.ActiveRecovery?.Media is { Target: not null } media ? MusicPlaybackSourceService.EffectiveLevel(media) : null;
        return new[] { "master", "hires", "lossless", "high", "standard", "enhanced" }
            .Where(category => MusicQualityPolicy.Exact(category, policy) is not null)
            .Select(category => new MusicQualityChoice(category, MusicQualityLabels.Name(category),
                level is not null && MusicQualityPolicy.Category(level) == category)).ToArray();
    }

    internal IReadOnlyList<MusicQualityChoice> SpatialChoices()
    {
        if (settings.SavedQualityPolicy is not { } policy) return [];
        string? level = recoveryService.ActiveRecovery?.Media is { Target: not null } media ? MusicPlaybackSourceService.EffectiveLevel(media) : null;
        return MusicQualityPolicy.Levels("spatial", policy)
            .Select(spatial => new MusicQualityChoice(spatial, LevelName(spatial), level == spatial)).ToArray();
    }

    /// <summary>Projects the session's known attempts. Opening the list never resolves anything.</summary>
    internal void UpdateSourceChoices()
    {
        SourceChoices.Clear();
        if (recoveryService.ActiveRecovery is { Session: { } session, Media.Target: not null } state && recoveryService.CurrentRecovery(state))
        {
            SourceChoices.Add(new("auto", "自动选择", "", "", "", state.Selection.Automatic, true));
            foreach (var attempt in session.Attempts.Values
                .Where(attempt => attempt.State != SourceAttemptState.Pending)
                .OrderBy(attempt => attempt.State == SourceAttemptState.Ok ? 0 : 1))
            {
                string provider = attempt.Target.Official ? MusicSourceRow.OfficialName(attempt.Target.Platform)
                    : attempt.Target.ProviderName is { Length: > 0 } name ? name : "JS 音源";
                bool ok = attempt.State == SourceAttemptState.Ok && attempt.Media is not null;
                string level = ok ? MusicPlaybackSourceService.EffectiveLevel(attempt.Media!) : attempt.Target.Quality;
                string quality = level is "128k" or "192k" or "320k" ? level[..^1] + " kbps" : LevelName(level);
                if (ok && !MusicPlaybackSourceService.QualityVerified(attempt.Media!)) quality += "?";
                bool selected = ok && state.Media is { } media && !state.Selection.Automatic &&
                    MusicPlaybackSourceService.SourceIdentity(media.Target!) == MusicPlaybackSourceService.SourceIdentity(attempt.Target) && media.Target!.Quality == attempt.Target.Quality;
                SourceChoices.Add(new(attempt.Key, provider, MusicSourceRow.PlatformName(attempt.Target.Platform), quality,
                    ok ? "可用" : "不可用", selected, ok)
                {
                    QualitySymbol = ok && MusicPlaybackSourceService.QualityVerified(attempt.Media!) ? QualitySymbol(MusicPlaybackSourceService.EffectiveLevel(attempt.Media!)) : "",
                    Recording = attempt.Target.SourceKey ?? "",
                    Level = level
                });
            }
        }
        notify(nameof(MusicPlaybackController.HasSourceChoices));
        notify(nameof(MusicPlaybackController.HasConfirmedSourceChoices));
        notify(nameof(MusicPlaybackController.HasEmptySources));
        notify(nameof(MusicPlaybackController.AutomaticSourceChoice));
        notify(nameof(MusicPlaybackController.CanSwitchSource));
        notify(nameof(MusicPlaybackController.CanExcludeCurrentRecording));
        notify(nameof(MusicPlaybackController.CanLoadMoreSources));
    }

    internal void UpdateSourceChoicesIfOpen()
    {
        if (SourceChoices.Count > 0) UpdateSourceChoices();
    }

    internal void OpenSourceChoices() => UpdateSourceChoices();

    internal void CloseSourceChoices()
    {
        sourceExpansion?.Cancel();
        sourceExpansion = null;
        SourceChoices.Clear();
        notify(nameof(MusicPlaybackController.HasSourceChoices));
        notify(nameof(MusicPlaybackController.HasConfirmedSourceChoices));
        notify(nameof(MusicPlaybackController.HasEmptySources));
        notify(nameof(MusicPlaybackController.AutomaticSourceChoice));
        notify(nameof(MusicPlaybackController.CanLoadMoreSources));
        notify(nameof(MusicPlaybackController.IsLoadingSources));
    }

    internal Task LoadMoreSourcesAsync()
    {
        if (!CanLoadMoreSources || recoveryService.ActiveRecovery is not { Session: not null } state) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        sourceExpansion = request;
        notify(nameof(MusicPlaybackController.CanLoadMoreSources));
        notify(nameof(MusicPlaybackController.IsLoadingSources));
        notify(nameof(MusicPlaybackController.HasEmptySources));
        var work = Run();
        state.ExpansionWork = work;
        ctx.AddEventWrite(work); _ = Observe();
        return work;
        async Task Observe()
        {
            try { await work; }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "source-selection", "load-more-failed",
                    new { error = error.Message });
            }
            finally { ctx.RemoveEventWrite(work); }
        }
        async Task Run()
        {
            try { await (await recoveryService.CurrentSourceSessionAsync(state, request.Token)).ExpandAsync(request.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (recoveryService.CurrentRecovery(state)) ctx.SetStatus(MusicRuntimeContext.ErrorText(error)); }
            finally
            {
                if (ReferenceEquals(sourceExpansion, request)) sourceExpansion = null;
                request.Dispose();
                if (ReferenceEquals(recoveryService.ActiveRecovery, state) && SourceChoices.Count > 0) UpdateSourceChoices();
                notify(nameof(MusicPlaybackController.CanLoadMoreSources));
                notify(nameof(MusicPlaybackController.IsLoadingSources));
                notify(nameof(MusicPlaybackController.HasEmptySources));
            }
        }
    }

    internal Task SelectSourceChoiceAsync(MusicSourceChoice choice)
    {
        if (!CanSwitchSource || !choice.Selectable || recoveryService.ActiveRecovery is not { Session: { } session } state) return Task.CompletedTask;
        if (choice.Key == "auto")
            return SwitchAsync(state, (current, token) => SelectAutomaticAsync(current, token), new(),
                "当前歌曲暂无可用音源", pauseWhenMissing: false, accepted: _ => sourceService.ForgetRecording(state.Song.Id));
        if (!session.Attempts.TryGetValue(choice.Key, out var attempt) || attempt.Media is not { } media) return Task.CompletedTask;
        var selection = new MusicSelection(Source: media);
        return SwitchAsync(state, async (current, token) =>
        {
            await current.LoadExclusionsAsync(token);
            if (current.Excluded(media.Target!)) return null;
            if (ReferenceEquals(media, state.Media) && MusicSourceSession.Fresh(attempt) &&
                (!media.Target!.Official || media.Target.QualityPolicySignature == current.Signature) &&
                current.CanReuse(media) && HealthyCurrentInput(state)) return media;
            if (await cacheService.CompletedPlaybackFileAsync(state, current, media, selection, token) is { } cached) return cached;
            if (!MusicSourceSession.Fresh(attempt) || media.Target is { Official: true } official &&
                (official.QualityPolicySignature != current.Signature || official.AccountScope is not { } scope ||
                    !ctx.Application.IsOfficialPlaybackCurrent(official.Platform, scope, official.Generation)))
                media = await current.RefreshAsync(media.Target! with { ManualSource = true }, token);
            return media;
        }, selection, "当前歌曲暂无可用音源", pauseWhenMissing: false,
            accepted: next => sourceService.RememberRecording(state.Song.Id, next.Target!.Platform, next.Target.SourceKey ?? ""));
    }

    private static async Task<ResolvedMusic?> SelectAutomaticAsync(MusicSourceSession session, CancellationToken token)
    {
        session.ReleaseRecording();
        return await session.AutomaticAsync(token);
    }

    internal Task SwitchQualityAsync(string choice)
    {
        if (!CanSwitchSource || recoveryService.ActiveRecovery is not { Session: not null } state) return Task.CompletedTask;
        if (settings.SavedQualityPolicy is not { } policy || MusicQualityPolicy.Exact(choice, policy) is not { } tier)
        {
            state.Switching?.Cancel();
            return ctx.Notify("未匹配到对应音质");
        }
        // Even selecting the playing tier supersedes an older pending choice and locks this tier.
        if (state.Media is { } playing && tier.Levels.Contains(MusicPlaybackSourceService.EffectiveLevel(playing)) &&
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
            UpdateSourceChoicesIfOpen();
            return Task.CompletedTask;
        }
        return SwitchAsync(state, async (session, token) =>
        {
            await session.LoadExclusionsAsync(token);
            if (state.Media is { } media && tier.Levels.Contains(MusicPlaybackSourceService.EffectiveLevel(media)) &&
                session.CanReuse(media) && HealthyCurrentInput(state)) return media;
            return await session.ExactAsync(tier, token);
        }, new(Tier: tier), "未匹配到对应音质", pauseWhenMissing: false);
    }

    private bool HealthyCurrentInput(MusicRecovery state) => readTelemetry().OwnsCurrent && ctx.PlaybackDevice.HasMedia &&
        ctx.PlaybackDevice.LastErrorCode is null && state.SwitchingFailure is null;

    internal Task ReportWrongSourceAsync() => ReportWrongSourceCoreAsync(recordingOnly: false);
    internal Task ReportWrongRecordingAsync() => ReportWrongSourceCoreAsync(recordingOnly: true);

    private Task ReportWrongSourceCoreAsync(bool recordingOnly)
    {
        if (!CanSwitchSource || recoveryService.ActiveRecovery is not { Session: not null, Media.Target: { } target } state) return Task.CompletedTask;
        string? recordingKey = null;
        string exclusionProvider;
        if (recordingOnly)
        {
            if (target.SourceKey is not { Length: > 0 } key || !state.Session.HasOtherUnexcludedRecording(key))
                return Task.CompletedTask;
            recordingKey = key;
            exclusionProvider = MusicSourceExclusionsOwner.MakeRecordingProvider(key);
        }
        else
        {
            exclusionProvider = target.Installation ?? MusicLibraryOwner.OfficialProvider;
        }
        string name = target.Official ? MusicSourceRow.OfficialName(target.Platform) : target.ProviderName ?? "JS 音源";
        string level = MusicPlaybackSourceService.EffectiveLevel(state.Media!);
        var tier = MusicQualityPolicy.Category(level) == "spatial" ? new MusicQualityPolicy.Tier("spatial", [level]) :
            MusicQualityPolicy.SourceTiers.FirstOrDefault(value => value.Levels.Contains(level)) ??
                new MusicQualityPolicy.Tier(MusicQualityPolicy.Category(level), [level]);
        state.Switching?.Cancel();
        sourceExpansion?.Cancel();
        state.Expiry?.Cancel();
        // Fence every resume path before the first asynchronous write or search.
        state.Session.Exclude(exclusionProvider, target.Platform);
        metadataService.InvalidateFeedback();
        var stopped = ctx.PlaybackDevice.StopOutputForHandoffAsync();
        var persisted = PersistExclusion();
        return SwitchAsync(state, async (session, token) =>
        {
            session.Exclude(exclusionProvider, target.Platform);
            session.ReleaseRecording();
            return await session.ExactAsync(tier, token, manualSource: target.ManualSource);
        }, new(Tier: tier, ManualSource: target.ManualSource), "未找到同音质的其他音源，已暂停",
            pauseWhenMissing: true, beforeSelection: persisted);
        async Task PersistExclusion()
        {
            await stopped;
            // A newer source choice may cancel the replacement, not the user's durable report.
            if (recordingKey is not null)
                await ctx.Application.AddRecordingExclusionAsync(
                    state.Song.Id, recordingKey, target.Platform, name, state.Song.Title, state.Song.Artist, ctx.Lifetime.Token);
            else
                await ctx.Application.AddSourceExclusionAsync(
                    state.Song.Id, exclusionProvider, target.Platform, name, state.Song.Title, state.Song.Artist, ctx.Lifetime.Token);
            sourceService.IncrementExclusionsRevision();
            state.Session!.Exclude(exclusionProvider, target.Platform);
            sourceService.ForgetRecording(state.Song.Id);
        }
    }

    private Task SwitchAsync(MusicRecovery state, Func<MusicSourceSession, CancellationToken, Task<ResolvedMusic?>> select,
        MusicSelection selection, string missing, bool pauseWhenMissing, Action<ResolvedMusic>? accepted = null,
        Task? beforeSelection = null)
    {
        if (!recoveryService.CurrentRecovery(state) || state.Recovering && state.Switching is null) return Task.CompletedTask;
        state.Switching?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        state.Switching = request;
        sourceExpansion?.Cancel();
        state.Recovering = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Work = done.Task;
        PlaybackSourceBindingsRequested?.Invoke(); notify(nameof(MusicPlaybackController.CanSwitchSource));
        bool started = false;
        var action = ctx.RunPlaybackActionAsync(async _ =>
        {
            started = true;
            bool committed = false;
            try
            {
                if (beforeSelection is not null) await beforeSelection;
                request.Token.ThrowIfCancellationRequested();
                request.CancelAfter(TimeSpan.FromSeconds(60));
                var session = await recoveryService.CurrentSourceSessionAsync(state, request.Token);
                ResolvedMusic? fileRetry = null;
                while (true)
                {
                    var next = fileRetry is { } refreshed
                        ? selection.Automatic ? await session.AutomaticAsync(request.Token, refreshed)
                            : selection.Tier is { } tier ? await session.ExactAsync(tier, request.Token, refreshed, selection.Manual) : refreshed
                        : await select(session, request.Token);
                    fileRetry = null;
                    request.Token.ThrowIfCancellationRequested();
                    if (!recoveryService.CurrentRecovery(state)) throw new OperationCanceledException(request.Token);
                    if (next is null || !selection.Allows(next))
                    {
                        if (pauseWhenMissing && ReferenceEquals(state.Switching, request) && readTelemetry().OwnsCurrent) await ctx.PlaybackDevice.PauseAsync();
                        ctx.SetStatus(missing);
                        return;
                    }
                    if (session.Signature != settings.SavedQualitySignature) throw new MusicOperationException("QUALITY_POLICY_CHANGED");
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
                        next = await cacheService.CompletedPlaybackFileAsync(state, session, next, selection, request.Token) ?? next;
                    if (selection.Manual)
                        next = next with { Target = next.Target! with { ManualSource = true } };
                    try { await HandoffAsync(state, next, request.Token, Accepted); return; }
                    catch (Exception error) when (!published && next.Uri is null &&
                        MusicPlaybackRecoveryService.UnusablePlaybackFile(error) && recoveryService.CurrentRecovery(state))
                    {
                        fileRetry = await cacheService.RefreshMissingFileAsync(state, session, next, request.Token);
                        if (selection.Source is not null && fileRetry is null)
                        { ctx.SetStatus(missing); return; }
                    }
                    catch (Exception error) when (!published && selection.Source is null &&
                        error is not OperationCanceledException && recoveryService.CurrentRecovery(state) &&
                        (error is PlaybackException playbackError && MusicPlaybackRecoveryService.RetryablePreparation(playbackError.Code) ||
                         error is MusicOperationException operation && MusicPlaybackRecoveryService.RetryablePreparation(operation.Code)))
                    {
                        session.Reject(next, error is PlaybackException native ? native.Code : ((MusicOperationException)error).Code);
                    }
                    catch (Exception error) when (!published && selection.Source is not null &&
                        error is not OperationCanceledException && recoveryService.CurrentRecovery(state) &&
                        (error is PlaybackException mediaError && MusicPlaybackRecoveryService.RetryablePreparation(mediaError.Code) ||
                            error is MusicOperationException operation && MusicPlaybackRecoveryService.RetryablePreparation(operation.Code)))
                    {
                        session.Reject(next, error is PlaybackException native ? native.Code : ((MusicOperationException)error).Code);
                        throw;
                    }
                }
            }
            catch
            {
                if (pauseWhenMissing && ReferenceEquals(state.Switching, request) && recoveryService.CurrentRecovery(state) && readTelemetry().OwnsCurrent)
                    await ctx.PlaybackDevice.PauseAsync();
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
                        if (failure is not null && recoveryService.CurrentRecovery(state) && (!pauseWhenMissing || committed))
                        {
                            if (failure == PlaybackError.AudioDeviceUnavailable)
                                recoveryService.ReportRecoveryFailed(new MusicOperationException(failure));
                            else
                            {
                                bool expired = state.Cached && !state.Attempted && failure != PlaybackError.ReadRevoked;
                                if (!await recoveryService.RecoverMusicAsync(state, expired) && recoveryService.CurrentRecovery(state))
                                    recoveryService.ReportRecoveryFailed();
                            }
                        }
                    }
                }
                finally
                {
                    request.Dispose(); done.TrySetResult();
                    PlaybackSourceBindingsRequested?.Invoke(); notify(nameof(MusicPlaybackController.CanSwitchSource));
                    UpdateSourceChoicesIfOpen();
                    recoveryService.CheckMediaDuration();
                }
            }
        }, supersede: false);
        return FinishSwitchAsync();

        async Task FinishSwitchAsync()
        {
            try { await action; }
            finally
            {
                if (!started)
                {
                    if (beforeSelection is not null)
                        try { await beforeSelection; }
                        catch (OperationCanceledException) { }
                        catch (Exception error)
                        {
                            DebugLog.Warn(DebugCategories.MusicPlayback, "source-selection", "before-selection-failed",
                                new { error = error.Message });
                        }
                    if (ReferenceEquals(state.Switching, request))
                    {
                        state.Switching = null;
                        state.Recovering = false;
                    }
                    request.Dispose();
                    done.TrySetResult();
                    if (!ctx.Disposed) { PlaybackSourceBindingsRequested?.Invoke(); notify(nameof(MusicPlaybackController.CanSwitchSource)); }
                }
            }
        }
    }

    private async Task HandoffAsync(MusicRecovery state, ResolvedMusic next, CancellationToken token, Action accepted)
    {
        if (!readTelemetry().OwnsCurrent) throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
        token.ThrowIfCancellationRequested();
        if (!recoveryService.CurrentRecovery(state)) throw new OperationCanceledException(token);
        await state.Session!.LoadExclusionsAsync(token);
        if (state.Session.Excluded(next.Target!)) throw new MusicOperationException("SOURCE_EXCLUDED");
        var pending = recoveryService.BeginPendingMedia(state, next, reopening: false, token);
        try
        {
            if (pending.Media.Uri is null || pending.Media.Bytes is not null)
            {
                var lease = await cacheService.OpenPlaybackFileAsync(state, pending.Media, pending.Access.Token);
                if (lease is null) throw new MusicOperationException("CACHE_MISS");
                await ctx.PlaybackDevice.PrepareReadHandoffAsync(lease, pending.Access.Token);
            }
            else await ctx.PlaybackDevice.PrepareHandoffAsync(pending.Media.Uri, pending.Media.Headers, pending.Access.Token);
            recoveryService.EnsurePendingMedia(state, pending);
            await state.Session!.LoadExclusionsAsync(pending.Access.Token);
            if (state.Session.Excluded(next.Target!)) throw new MusicOperationException("SOURCE_EXCLUDED");
            if (MusicPlaybackSourceService.MediaDurationMismatch(state.Song, ctx.PlaybackDevice.PreparedHandoffDurationSeconds))
                throw new MusicOperationException("RECORDING_MISMATCH");
            await ctx.PlaybackDevice.CommitHandoffAsync(() =>
            {
                metadataService.SampleFeedback();
                if (!recoveryService.CommitPendingMedia(state, pending, out _, published: true)) return;
                accepted();
                OfficialSongAccepted?.Invoke(state.Song, next.Target);
                metadataService.UpdateFeedbackSelection();
                state.Attempted = false;
                recoveryService.ScheduleExpiry(state);
                SourcesUpdatedForLyrics?.Invoke();
                PlaybackSourceBindingsRequested?.Invoke();
            }, pending.Access.Token);
        }
        catch
        {
            await ctx.PlaybackDevice.DiscardHandoffAsync();
            MusicPlaybackRecoveryService.DiscardPendingMedia(state);
            throw;
        }
        RestoreLyricsIfNeeded?.Invoke();
        // A handed-off input emits no second Started; confirm the newly playing source here.
        recoveryService.ConfirmRecoveredSourceStarted(state);
    }
}
