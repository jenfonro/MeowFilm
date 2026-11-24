using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

internal sealed class PendingMusic(ResolvedMusic media, CancellationTokenSource access, bool reopening)
{
    internal readonly ResolvedMusic Media = media;
    internal readonly CancellationTokenSource Access = access;
    internal readonly bool Reopening = reopening;
    internal bool SourceStarted;
}

internal sealed class MusicRecovery(SongRow song, long epoch, CancellationTokenSource access)
{
    internal readonly SongRow Song = song;
    internal readonly long Epoch = epoch;
    internal readonly CancellationTokenSource Access = access;
    internal readonly CancellationToken Token = access.Token;
    internal long AccountEpoch;
    internal ResolvedMusic? Media;
    internal PendingMusic? Pending;
    internal bool Attempted, Opening, Recovering, Cached, RecoveryDisabled;
    internal bool ConfirmSourceOnStart;
    internal CancellationTokenSource? Switching;
    /// <summary>The one source selection of this playback; every source operation shares it.</summary>
    internal MusicSourceSession? Session;
    internal readonly List<MusicSourceSession> RetiredSessions = [];
    internal MusicSelection Selection = new();
    internal CancellationTokenSource? Expiry;
    internal string? PendingFailure, SwitchingFailure;
    internal Task Work = Task.CompletedTask;
    internal Task ExpansionWork = Task.CompletedTask;
    internal Task RejectedOutput = Task.CompletedTask;
    internal readonly TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal MusicAudioFileCache.PreparedFile? ObservedBytes;
    internal Action? CacheProgress;
}

/// <summary>
/// Standalone Playback service owning online media opening, failure recovery/retry policy,
/// URL expiry refresh timers, duration-mismatch rejection, and official/script revocation.
/// </summary>
internal sealed class MusicPlaybackRecoveryService(
    MusicRuntimeContext ctx,
    MusicSettingsController settings,
    MusicPlaybackSourceService sourceService,
    MusicPlaybackCacheService cacheService,
    MusicPlaybackMetadataService metadataService,
    Func<MusicPlaybackTelemetry> readTelemetry,
    Action<string?, bool> setOpenedState)
{
    private MusicRecovery? musicRecovery;
    private CancellationTokenSource? onlineCancellation;
    private string? playbackInstallation;
    private long officialAccountEpoch;

    internal event Action? PlaybackSourceBindingsRequested;
    internal event Action? UpdateSourceChoicesRequested;
    internal event Action? CloseSourceChoicesRequested;
    internal event Action? SourcesUpdatedForLyrics;
    internal event Action? RestoreLyricsIfNeeded;
    internal event Action<SongRow, MusicOnlineTarget?>? OfficialSongAccepted;
    internal event Action? SearchRecommendationCleared;
    internal event Action<MusicRecovery?>? PlaybackStartedAccepted;
    internal event Action? CanLoadMoreSourcesChanged;
    internal event Action? SyncClockCacheRequested;

    internal MusicRecovery? ActiveRecovery => musicRecovery;
    internal string? PlaybackInstallation => playbackInstallation;
    internal bool CurrentPlaybackExcluded =>
        musicRecovery is { Session: { } session, Media.Target: { } target } && session.Excluded(target);

    internal void CancelOnline() => onlineCancellation?.Cancel();

    internal CancellationTokenSource BeginLocalOnlineRequest(CancellationToken token)
    {
        onlineCancellation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(token, ctx.Lifetime.Token);
        onlineCancellation = source;
        settings.SourceBindings();
        return source;
    }

    internal void EndLocalOnlineRequest(CancellationTokenSource source)
    {
        if (ReferenceEquals(onlineCancellation, source)) onlineCancellation = null;
        source.Dispose();
        settings.SourceBindings();
    }

    internal bool ClearInstallationIfPresent()
    {
        bool hadInstallation = playbackInstallation is not null;
        playbackInstallation = null;
        return hadInstallation;
    }

    internal static bool RefreshableMusicError(string? code) => code is
        PlaybackError.HttpExpired or PlaybackError.HttpUnauthorized or PlaybackError.HttpNotFound or
        PlaybackError.HttpDeadline or PlaybackError.HttpNetworkFailed or PlaybackError.HttpTruncated or
        PlaybackError.HttpServerFailed or PlaybackError.HttpThrottled or PlaybackError.HttpRepresentationChanged or
        PlaybackError.HttpRangeRejected;

    internal static bool CanChangeFailedSource(MusicRecovery state, string? code) =>
        !state.RecoveryDisabled && state.Media?.Target is not null &&
        code != PlaybackError.AudioDeviceUnavailable &&
        (RefreshableMusicError(code) || code is PlaybackError.HttpFormatUnsupported or
            PlaybackError.OpenFailed or PlaybackError.PlaybackFailed or "PLAYBACK_UNAVAILABLE" ||
            state.Media?.Bytes is not null && (code is PlaybackError.ReadFailed or PlaybackError.ReadTruncated or PlaybackError.ReadFormatUnsupported) ||
            code is not null && MusicPlaybackSourceService.UnavailableSourceCandidate(code));

    internal static bool RecoverableMusicError(MusicRecovery state, string? code) =>
        !state.RecoveryDisabled && code != PlaybackError.AudioDeviceUnavailable &&
        (!state.Attempted && RefreshableMusicError(code) || CanChangeFailedSource(state, code));

    internal static bool RetryablePreparation(string code) => RefreshableMusicError(code) || MusicPlaybackSourceService.UnavailableSourceCandidate(code) ||
        code is PlaybackError.OpenFailed or PlaybackError.PlaybackFailed or PlaybackError.HttpFormatUnsupported or PlaybackError.ReadFailed;

    internal static bool UnusablePlaybackFile(Exception error) =>
        (error is PlaybackException native ? native.Code : error is MusicOperationException music ? music.Code : null) is
            "CACHE_MISS" or PlaybackError.OpenFailed or PlaybackError.ReadFailed or PlaybackError.ReadTruncated or
            PlaybackError.ReadChanged or PlaybackError.ReadFormatUnsupported;

    internal void QualityPolicyChanged(string signature)
    {
        cacheService.CancelPreload();
        if (musicRecovery is { Media.Target.QualityPolicySignature: not null } state &&
            state.Media.Target.QualityPolicySignature != signature && !state.Selection.Manual)
        {
            state.RecoveryDisabled = true;
            CancelPendingMedia(state, "QUALITY_POLICY_CHANGED");
        }
    }

    internal bool CurrentRecovery(MusicRecovery state)
    {
        var t = readTelemetry();
        return !ctx.Stopping && !ctx.Disposed &&
            ReferenceEquals(musicRecovery, state) && state.Epoch == t.PlaybackEpoch &&
            (state.Media?.Target?.Official != true || state.AccountEpoch == Interlocked.Read(ref officialAccountEpoch)) &&
            (state.Media?.Target is not { Official: true, AccountScope: not null } official ||
                ctx.Application.IsOfficialPlaybackCurrent(official.Platform, official.AccountScope, official.Generation)) &&
            t.Current?.EntryId == state.Song.EntryId && !state.Access.IsCancellationRequested;
    }

    internal bool CurrentPendingMedia(MusicRecovery state, PendingMusic pending) =>
        CurrentRecovery(state) && ReferenceEquals(state.Pending, pending) &&
        !pending.Access.IsCancellationRequested &&
        (pending.Media.Target is not { } candidate || state.Session?.Excluded(candidate) != true) &&
        (pending.Media.Target is not { Official: true, AccountScope: not null } target ||
            ctx.Application.IsOfficialPlaybackCurrent(target.Platform, target.AccountScope, target.Generation));

    internal PendingMusic BeginPendingMedia(MusicRecovery state, ResolvedMusic media,
        bool reopening, CancellationToken token)
    {
        DiscardPendingMedia(state);
        if (reopening) metadataService.SampleFeedback(false);
        media = cacheService.PreparePlaybackBytes(state, media, CurrentRecovery);
        var pending = new PendingMusic(media, CancellationTokenSource.CreateLinkedTokenSource(token), reopening);
        state.Pending = pending;
        state.PendingFailure = null;
        return pending;
    }

    internal static void CancelPendingMedia(MusicRecovery state, string failure)
    {
        if (state.Pending is not { } pending) return;
        state.PendingFailure = failure;
        if (!ReferenceEquals(pending.Media.Bytes, state.Media?.Bytes)) pending.Media.Bytes?.Dispose();
        try { pending.Access.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal static void DiscardPendingMedia(MusicRecovery state)
    {
        var pending = state.Pending;
        state.Pending = null;
        if (pending is null) return;
        if (!ReferenceEquals(pending.Media.Bytes, state.Media?.Bytes)) pending.Media.Bytes?.Dispose();
        try { pending.Access.Cancel(); }
        catch (ObjectDisposedException) { }
        pending.Access.Dispose();
    }

    internal bool CommitPendingMedia(MusicRecovery state, PendingMusic pending, out bool alreadyStarted, bool published = false)
    {
        alreadyStarted = false;
        if (published ? !ReferenceEquals(musicRecovery, state) || !ReferenceEquals(state.Pending, pending)
            : !CurrentPendingMedia(state, pending)) return false;
        alreadyStarted = pending.SourceStarted;
        var previous = state.Media?.Bytes;
        state.Media = pending.Media;
        state.Cached = pending.Media.Uri is null;
        state.Session?.Adopt(pending.Media);
        ObservePlaybackCache(state);
        if (!ReferenceEquals(previous, pending.Media.Bytes)) previous?.Dispose();
        playbackInstallation = pending.Media.Target?.Installation;
        if (pending.Media.Target?.Official == true)
            state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
        state.PendingFailure = null;
        state.SwitchingFailure = null;
        state.RejectedOutput = Task.CompletedTask;
        state.Pending = null;
        pending.Access.Dispose();
        state.ConfirmSourceOnStart = true;
        return true;
    }

    internal void EnsurePendingMedia(MusicRecovery state, PendingMusic pending)
    {
        if (CurrentPendingMedia(state, pending)) return;
        if (CurrentRecovery(state) && ReferenceEquals(state.Pending, pending) &&
            state.PendingFailure is { } failure)
            throw new MusicOperationException(failure);
        if (pending.Media.Target is { } target && state.Session?.Excluded(target) == true)
            throw new MusicOperationException("SOURCE_EXCLUDED");
        throw new OperationCanceledException(pending.Access.Token);
    }

    internal async Task ReopenPendingMediaAsync(
        NativePlaybackContinuation continuation, MusicRecovery state, PendingMusic pending)
    {
        try
        {
            if (pending.Media.Uri is null || pending.Media.Bytes is not null)
            {
                var lease = await cacheService.OpenPlaybackFileAsync(state, pending.Media, pending.Access.Token);
                if (lease is null) throw new MusicOperationException("CACHE_MISS");
                await ctx.PlaybackDevice.ReopenReadAsync(continuation, lease, pending.Access.Token);
            }
            else await ctx.PlaybackDevice.ReopenAsync(continuation, pending.Media.Uri, pending.Media.Headers, pending.Access.Token);
        }
        catch (OperationCanceledException)
        {
            EnsurePendingMedia(state, pending);
            throw;
        }
        EnsurePendingMedia(state, pending);
    }

    internal void ConfirmRecoveredSourceStarted(MusicRecovery state)
    {
        if (CurrentRecovery(state)) PlaybackStartedAccepted?.Invoke(state);
    }

    internal async Task<MusicSourceSession> CurrentSourceSessionAsync(MusicRecovery state, CancellationToken token)
    {
        var previous = state.Session!;
        if (previous.Revision == sourceService.SelectionSourcesRevision && previous.Signature == settings.SavedQualitySignature) return previous;
        var session = await sourceService.BeginSessionAsync(state.Song, token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRecovery(state)) throw new OperationCanceledException(token);
        session.Inherit(previous);
        state.RetiredSessions.Add(previous);
        state.Session = session;
        state.RecoveryDisabled = false;
        return session;
    }

    private async Task<ResolvedMusic> ResolveOnlineAsync(MusicRecovery state)
    {
        await settings.ReadOnlineSettings(state.Token);
        state.Session = await sourceService.BeginSessionAsync(state.Song, state.Token);
        return await state.Session.AutomaticAsync(state.Token);
    }

    private void ObservePlaybackCache(MusicRecovery state) =>
        cacheService.ObservePlaybackCache(state, CurrentRecovery,
            () => SyncClockCacheRequested?.Invoke(),
            () =>
            {
                UpdateSourceChoicesRequested?.Invoke();
                PlaybackSourceBindingsRequested?.Invoke();
            });

    internal async Task OpenOnlineCurrentAsync(SongRow song, long epoch, CancellationToken token, Func<bool>? admission = null)
    {
        var access = CancellationTokenSource.CreateLinkedTokenSource(token, ctx.Lifetime.Token);
        var state = new MusicRecovery(song, epoch, access) { Opening = true, AccountEpoch = Interlocked.Read(ref officialAccountEpoch) };
        musicRecovery = state;
        // Retain the cancellation source for the whole playback, not just initial resolution.
        onlineCancellation = access;
        bool succeeded = false;
        try
        {
            try { state.Media = await ResolveOnlineAsync(state); }
            finally
            {
                // A prepared official file is speculative, not a playback choice.
                // If another source wins (or resolution fails), retire its producer
                // rather than waiting for a Started event that can never arrive.
                cacheService.CancelPreparedAudioIfMismatch(song, state.Media?.Target);
            }
            // Initial account hydration may notify while resolving; the native issued authority,
            // not an assumption about UI initialization order, decides whether this receipt is current.
            if (state.Media.Target?.Official == true) state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
            if (!CurrentRecovery(state) || admission is not null && !admission()) throw new OperationCanceledException(state.Token);
            state.Media = cacheService.PreparePlaybackBytes(state, state.Media, CurrentRecovery);
            state.Session?.Adopt(state.Media);
            ObservePlaybackCache(state);
            OfficialSongAccepted?.Invoke(song, state.Media.Target);
            UpdateSourceChoicesRequested?.Invoke();
            metadataService.UpdateFeedbackSelection();
            SourcesUpdatedForLyrics?.Invoke();
            playbackInstallation = state.Media.Target!.Installation;
            setOpenedState(song.EntryId, false);
            try
            {
                while (true)
                {
                    var lease = await cacheService.OpenPlaybackFileAsync(state, state.Media, state.Token);
                    if (lease is not null)
                    {
                        try
                        {
                            if (!CurrentRecovery(state) || admission is not null && !admission())
                                throw new OperationCanceledException(state.Token);
                        }
                        catch { await lease.DisposeAsync(); throw; }
                        state.Cached = state.Media.Bytes is null;
                        try { await ctx.PlaybackDevice.OpenReadAsync(ctx.Id, song.EntryId!, lease, song.Title, state.Token); }
                        catch (Exception error) when (state.Cached && (error is PlaybackException native && native.Code != PlaybackError.AudioDeviceUnavailable || error is NotSupportedException))
                        {
                            await ReselectFile();
                            continue;
                        }
                    }
                    else if (state.Media.Uri is null)
                    {
                        await ReselectFile();
                        continue;
                    }
                    else if (state.Media.Bytes is not null) throw new MusicOperationException("CACHE_MISS");
                    else await OpenNetwork();
                    break;
                }
                async Task OpenNetwork()
                {
                    if (!CurrentRecovery(state) || admission is not null && !admission())
                        throw new OperationCanceledException(state.Token);
                    await ctx.PlaybackDevice.OpenAsync(ctx.Id, song.EntryId!, state.Media.Uri!, song.Title, state.Media.Headers, state.Token);
                }
                async Task ReselectFile()
                {
                    var previousBytes = state.Media?.Bytes;
                    var session = await CurrentSourceSessionAsync(state, state.Token);
                    var refreshed = await cacheService.RefreshMissingFileAsync(state, session, state.Media!, state.Token);
                    state.Media = await session.AutomaticAsync(state.Token, refreshed);
                    state.Media = cacheService.PreparePlaybackBytes(state, state.Media, CurrentRecovery);
                    if (!ReferenceEquals(previousBytes, state.Media.Bytes)) previousBytes?.Dispose();
                    session.Adopt(state.Media);
                    ObservePlaybackCache(state);
                    state.Cached = false;
                    state.PendingFailure = null;
                    playbackInstallation = state.Media.Target!.Installation;
                    if (state.Media.Target.Official) state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
                    OfficialSongAccepted?.Invoke(song, state.Media.Target);
                    metadataService.UpdateFeedbackSelection();
                    UpdateSourceChoicesRequested?.Invoke();
                    SourcesUpdatedForLyrics?.Invoke();
                }
            }
            catch (Exception error) when (error is PlaybackException native && RecoverableMusicError(state, native.Code) ||
                error is MusicOperationException operation && RecoverableMusicError(state, operation.Code))
            {
                if (!readTelemetry().Recorded) SearchRecommendationCleared?.Invoke();
                state.Opening = false;
                if (!await RecoverMusicAsync(state, expired: false)) throw;
            }
            state.Opening = false;
            if (!CurrentRecovery(state) || admission is not null && !admission()) throw new OperationCanceledException(state.Token);
            if (state.PendingFailure is { } failure)
            {
                bool recovered = failure == "RECORDING_MISMATCH" ? await RecoverRejectedMusicAsync(state) :
                    RecoverableMusicError(state, failure) && await RecoverMusicAsync(state, expired: false);
                if (!recovered)
                    throw new MusicOperationException(failure);
            }
            CheckMediaDuration();
            ScheduleExpiry(state);
            succeeded = true;
        }
        finally
        {
            state.Opening = false;
            state.Opened.TrySetResult();
            if (ReferenceEquals(onlineCancellation, access)) onlineCancellation = null;
            if (!succeeded && ReferenceEquals(musicRecovery, state))
                TrackRecovery(RetireAsync());
            settings.SourceBindings();
        }
    }

    internal void ScheduleExpiry(MusicRecovery state)
    {
        state.Expiry?.Cancel(); state.Expiry?.Dispose(); state.Expiry = null;
        long? deadline = state.Cached || state.Media?.Bytes is not null ? null : state.Media?.ExpiresAt;
        if (deadline is { } expiry)
        {
            state.Expiry = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
            var timer = ExpireMusicAsync(state, expiry, state.Expiry.Token);
            TrackRecovery(timer);
        }
    }

    private async Task ExpireMusicAsync(MusicRecovery state, long expiry, CancellationToken token)
    {
        try
        {
            // Exactly one dormant timer, not a refresh loop. Very long signed URLs use bounded delay chunks.
            while (expiry - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() is var remaining && remaining > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, int.MaxValue)), token);
            token.ThrowIfCancellationRequested();
            if (state.Recovering) await state.Work.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (CurrentRecovery(state) && !await RecoverMusicAsync(state, expired: true) && CurrentRecovery(state))
            {
                // Exhausting retry authority must not keep a failed input
                // indefinitely. A replacement timer cancels this old callback.
                if (readTelemetry().OwnsCurrent) await ctx.PlaybackDevice.StopAsync();
                ReportRecoveryFailed();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (CurrentRecovery(state)) ReportRecoveryFailed(); }
    }

    internal bool TryRecover()
    {
        var state = musicRecovery;
        if (state is null || !CurrentRecovery(state)) return false;
        if (state.Switching is not null)
        {
            // Handoff preparation has its own failure channel. Failed here belongs to
            // the accepted input, and must survive a missing/cancelled replacement.
            state.SwitchingFailure = ctx.PlaybackDevice.LastErrorCode ?? PlaybackError.PlaybackFailed;
            return true;
        }
        if (state.Opening || state.Recovering)
        {
            state.PendingFailure = ctx.PlaybackDevice.LastErrorCode ?? PlaybackError.PlaybackFailed;
            return true;
        }
        if (ctx.PlaybackDevice.LastErrorCode == PlaybackError.AudioDeviceUnavailable) return false;
        if (state.Cached && !state.Attempted && ctx.PlaybackDevice.LastErrorCode != PlaybackError.ReadRevoked)
        {
            TrackRecovery(RecoverMusicAsync(state, expired: true));
            return true;
        }
        if (!RecoverableMusicError(state, ctx.PlaybackDevice.LastErrorCode)) return false;
        TrackRecovery(RecoverMusicAsync(state, expired: false));
        return true;
    }

    internal async Task<bool> RecoverMusicAsync(MusicRecovery state, bool expired, bool mismatch = false)
    {
        if (!CurrentRecovery(state) || state.RecoveryDisabled || state.Recovering ||
            state.Media?.Target is null ||
            ctx.PlaybackDevice.OwnerId != ctx.Id ||
            ctx.PlaybackDevice.Identity != state.Song.EntryId ||
            !mismatch && !expired && !RecoverableMusicError(state, ctx.PlaybackDevice.LastErrorCode) ||
            (expired || mismatch) && (mismatch || state.Attempted) && !CanChangeFailedSource(state, PlaybackError.HttpExpired))
            return false;
        var continuation = ctx.PlaybackDevice.CaptureContinuation();
        if (continuation is null) return false;
        string? originalFailure = expired ? null : ctx.PlaybackDevice.LastErrorCode;
        // A mismatched recording is never refreshed: a different source must replace it.
        bool refreshExact = !mismatch && !state.Attempted && (expired || RefreshableMusicError(ctx.PlaybackDevice.LastErrorCode));
        bool rejectCurrent = !refreshExact;
        state.Recovering = true;
        state.PendingFailure = null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token, continuation.Revoked);
        request.CancelAfter(TimeSpan.FromSeconds(state.Selection.Automatic ? 55 : 20));
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Work = settled.Task;
        ResolvedMusic? fileRetry = null, refreshedReceipt = null;
        try
        {
            var sourceSession = await CurrentSourceSessionAsync(state, request.Token);
            if (originalFailure is not null) sourceSession.ObserveFailure(state.Media.Target, originalFailure);
            if (state.Cached) await cacheService.InvalidateFileCacheAsync(state);
            // A failed URL says nothing about the complete bytes already stored for it.
            // Try those before spending the one exact network refresh, even for a retired script.
            ResolvedMusic? completedFile = !state.Cached && !mismatch
                ? await cacheService.CompletedPlaybackFileAsync(state, sourceSession, state.Media!, state.Selection, request.Token)
                : null;
            while (true)
            {
                bool cachedAttempt = completedFile is not null;
                bool exactAttempt = !cachedAttempt && refreshExact;
                try
                {
                    ResolvedMusic refreshed;
                    if (completedFile is { } local)
                    {
                        refreshed = local;
                        completedFile = null;
                    }
                    else if (fileRetry is { } retry)
                    {
                        fileRetry = null;
                        refreshedReceipt = retry;
                        var session = await CurrentSourceSessionAsync(state, request.Token);
                        refreshed = state.Selection.Automatic ? await session.AutomaticAsync(request.Token, retry) :
                            state.Selection.Tier is { } tier
                                ? await session.ExactAsync(tier, request.Token, retry, state.Selection.Manual) ??
                                    throw new MusicOperationException("QUALITY_UNAVAILABLE") : retry;
                    }
                    else if (refreshExact)
                    {
                        refreshExact = false;
                        state.Attempted = true; // One exact refresh per selected occurrence.
                        var session = await CurrentSourceSessionAsync(state, request.Token);
                        refreshed = refreshedReceipt = await session.RefreshAsync(state.Media!.Target!, request.Token);
                        // A refreshed lower-quality receipt is a later-tier candidate, not
                        // permission to skip the remaining sources of the preferred tier.
                        if (state.Selection.Automatic) refreshed = await session.AutomaticAsync(request.Token, refreshedReceipt);
                    }
                    else
                    {
                        if (!CanChangeFailedSource(state, PlaybackError.HttpExpired) || state.Session is null)
                            throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                        if (rejectCurrent)
                        {
                            state.Session.Reject(state.Media!, ctx.PlaybackDevice.LastErrorCode ?? "PLAYBACK_UNAVAILABLE");
                            rejectCurrent = false;
                        }
                        if (state.Selection.Source is not null) throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                        var session = await CurrentSourceSessionAsync(state, request.Token);
                        refreshed = state.Selection.Tier is { } tier
                            ? await session.ExactAsync(tier, request.Token, refreshedReceipt, state.Selection.Manual) ??
                                throw new MusicOperationException(session.Failure ?? "PLAYBACK_UNAVAILABLE")
                            : await session.AutomaticAsync(request.Token, refreshedReceipt);
                    }
                    if (!state.Selection.Allows(refreshed))
                        throw new MusicOperationException("QUALITY_UNAVAILABLE");
                    request.Token.ThrowIfCancellationRequested();
                    if (!CurrentRecovery(state)) throw new OperationCanceledException(request.Token);
                    if (state.RecoveryDisabled || state.Session?.Signature != settings.SavedQualitySignature)
                        throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                    var pending = BeginPendingMedia(state, refreshed, reopening: true, request.Token);
                    EnsurePendingMedia(state, pending);
                    await ReopenPendingMediaAsync(continuation, state, pending);
                    request.Token.ThrowIfCancellationRequested();
                    EnsurePendingMedia(state, pending);
                    await metadataService.RestorePlaybackRateAsync(state.Song, state.Epoch, pending.Access.Token);
                    await state.Session!.LoadExclusionsAsync(pending.Access.Token);
                    EnsurePendingMedia(state, pending);
                    if (state.PendingFailure is not null || !ctx.PlaybackDevice.HasMedia || ctx.PlaybackDevice.LastErrorCode is not null)
                        throw new MusicOperationException(state.PendingFailure ?? ctx.PlaybackDevice.LastErrorCode ?? PlaybackError.OpenFailed);
                    if (MusicPlaybackSourceService.MediaDurationMismatch(state.Song, ctx.PlaybackDevice.DurationSeconds))
                        throw new MusicOperationException("RECORDING_MISMATCH");
                    if (!CommitPendingMedia(state, pending, out bool alreadyStarted))
                        throw new OperationCanceledException(request.Token);
                    OfficialSongAccepted?.Invoke(state.Song, refreshed.Target);
                    metadataService.UpdateFeedbackSelection();
                    UpdateSourceChoicesRequested?.Invoke();
                    SourcesUpdatedForLyrics?.Invoke();
                    PlaybackSourceBindingsRequested?.Invoke();
                    RestoreLyricsIfNeeded?.Invoke();
                    ScheduleExpiry(state);
                    if (alreadyStarted) ConfirmRecoveredSourceStarted(state);
                    return true;
                }
                catch (Exception error) when (cachedAttempt && error is not OperationCanceledException &&
                    CurrentRecovery(state) && (error is PlaybackException native && RetryablePreparation(native.Code) ||
                        error is MusicOperationException operation && RetryablePreparation(operation.Code)))
                {
                    // Only an unusable local representation is retired here. Its source
                    // remains eligible for the original finite refresh/fallback sequence.
                    if (state.Pending?.Media is { } failed)
                    {
                        string code = error is PlaybackException fileError ? fileError.Code : ((MusicOperationException)error).Code;
                        if (code is "RECORDING_MISMATCH" or "IDENTITY_MISMATCH" or "MEDIA_MISMATCH")
                        {
                            state.Session!.Reject(failed, code);
                            refreshExact = false;
                            rejectCurrent = true;
                        }
                        if (ctx.AudioFileCache is not null && MusicPlaybackSourceService.FileIdentity(failed) is { } file)
                            await ctx.AudioFileCache.EraseAsync(file.Scope, state.Song.Id, file.Signature, request.Token);
                    }
                    DiscardPendingMedia(state);
                    continuation = ctx.PlaybackDevice.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                    state.PendingFailure = null;
                }
                catch (MusicOperationException error) when (error.Code == "CACHE_MISS" &&
                    state.Pending?.Media is { Uri: null } && CurrentRecovery(state))
                {
                    var missing = state.Pending.Media;
                    DiscardPendingMedia(state);
                    fileRetry = await cacheService.RefreshMissingFileAsync(state,
                        await CurrentSourceSessionAsync(state, request.Token), missing, request.Token);
                    if (fileRetry is null && state.Selection.Source is not null)
                        throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                    continuation = ctx.PlaybackDevice.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                    state.PendingFailure = null;
                }
                catch (Exception error) when ((exactAttempt || state.Pending is not null) &&
                    error is not OperationCanceledException && CurrentRecovery(state) &&
                    CanChangeFailedSource(state, error is PlaybackException mediaError ? mediaError.Code :
                        error is MusicOperationException musicError ? musicError.Code : null))
                {
                    if (state.Pending?.Media is { } failed)
                        state.Session?.Reject(failed, error is PlaybackException rejectedMedia ? rejectedMedia.Code :
                            error is MusicOperationException rejectedOperation ? rejectedOperation.Code : "PLAYBACK_UNAVAILABLE");
                    else if (exactAttempt)
                    {
                        state.Session?.Reject(state.Media!, error is PlaybackException rejectedNative ? rejectedNative.Code :
                            error is MusicOperationException rejectedResolve ? rejectedResolve.Code : "PLAYBACK_UNAVAILABLE");
                        rejectCurrent = false;
                    }
                    DiscardPendingMedia(state);
                    continuation = ctx.PlaybackDevice.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                    state.PendingFailure = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            DiscardPendingMedia(state);
            if (CurrentRecovery(state) && !continuation.Revoked.IsCancellationRequested)
            {
                if (ctx.PlaybackDevice.OwnerId == ctx.Id && ctx.PlaybackDevice.Identity == state.Song.EntryId) await ctx.PlaybackDevice.StopAsync();
                ReportRecoveryFailed();
            }
            throw;
        }
        catch (Exception error)
        {
            DiscardPendingMedia(state);
            if (CurrentRecovery(state))
            {
                if (ctx.PlaybackDevice.OwnerId == ctx.Id && ctx.PlaybackDevice.Identity == state.Song.EntryId) await ctx.PlaybackDevice.StopAsync();
                ReportRecoveryFailed(error);
            }
            return false;
        }
        finally
        {
            DiscardPendingMedia(state);
            state.Recovering = false;
            settled.TrySetResult();
            CheckMediaDuration();
        }
    }

    internal void ReportRecoveryFailed(Exception? error = null)
    {
        metadataService.FinishFeedback("error");
        ctx.SetStatus(error is PlaybackException { Code: PlaybackError.AudioDeviceUnavailable }
            or MusicOperationException { Code: PlaybackError.AudioDeviceUnavailable }
            ? MusicRuntimeContext.ErrorText(error)
            : "播放失败");
    }

    internal void TrackRecovery(Task task)
    {
        var observed = ObserveRecovery();
        ctx.AddEventWrite(observed);
        _ = Remove();
        async Task ObserveRecovery()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!ctx.Stopping) ReportRecoveryFailed(); }
        }
        async Task Remove() { await observed; ctx.RemoveEventWrite(observed); }
    }

    internal Task RetireAsync()
    {
        var state = musicRecovery; musicRecovery = null;
        if (state is null) return Task.CompletedTask;
        CloseSourceChoicesRequested?.Invoke();
        state.Access.Cancel();
        state.Media?.Bytes?.Dispose();
        if (state.ObservedBytes is { } bytes && state.CacheProgress is { } progress)
            bytes.ProgressChanged -= progress;
        PlaybackSourceBindingsRequested?.Invoke();
        return Drain();
        async Task Drain()
        {
            try { await Task.WhenAll(state.Opened.Task, state.Work, state.RejectedOutput, state.ExpansionWork); }
            finally
            {
                DiscardPendingMedia(state);
                if (state.Media?.Bytes is { } source) await source.Work;
                if (state.Session is { } activeSession)
                {
                    state.Session = null;
                    await activeSession.DisposeAsync();
                }
                foreach (var retired in state.RetiredSessions)
                    await retired.DisposeAsync();
                state.RetiredSessions.Clear();
                state.Expiry?.Cancel(); state.Expiry?.Dispose(); state.Access.Dispose();
            }
        }
    }

    internal async Task RevokeChangedSourceAsync(string? installation)
    {
        if (musicRecovery is { Recovering: true, Switching: null, Pending: { } pending } recovery &&
            pending.Media.File is null && pending.Media.Target?.Installation == installation)
            CancelPendingMedia(recovery, "SOURCES_CHANGED");
        // Resolved URLs and downloaded files are independent history. An in-flight parser
        // still belongs to the installation and must drain before its content is replaced.
        if (musicRecovery?.Media?.Target?.Installation != installation || installation is null ||
            musicRecovery is { Recovering: false } or { Switching: not null } or { Cached: true }) return;
        var t = readTelemetry();
        var epoch = t.PlaybackEpoch; var identity = t.OpenedEntry;
        await RetireAsync();
        if (epoch == readTelemetry().PlaybackEpoch && ctx.PlaybackDevice.OwnerId == ctx.Id && ctx.PlaybackDevice.Identity == identity)
            await ctx.PlaybackDevice.StopAsync();
    }

    internal void ConfirmOfficialPlayback()
    {
        if (musicRecovery is not { Media.Target.Official: true } state || !CurrentRecovery(state)) return;
        var target = state.Media!.Target!;
        TrackRecovery(sourceService.ConfirmOfficialPlaybackAsync(state.Song, target, state.Token));
    }

    internal void OnOfficialAccountChanged(string platform)
    {
        // Account notifications may arrive off-dispatcher. Fence the selection snapshot
        // even when a script is playing, so its next explicit search uses new authority.
        sourceService.IncrementSelectionSourcesRevision();
        // Native authority is already fenced. Inspect and retire UI-owned resources
        // on their dispatcher, against the authority current when cleanup runs.
        if (!ctx.Dispatcher.IsClosing)
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (ctx.Stopping || ctx.Disposed) return;
                bool Revoked(MusicOnlineTarget? target) =>
                    target is { Official: true, AccountScope: { } scope } && target.Platform == platform &&
                    !ctx.Application.IsOfficialPlaybackCurrent(platform, scope, target.Generation);
                var state = musicRecovery;
                bool revokeActive = Revoked(state?.Media?.Target);
                if (revokeActive || cacheService.HasRevokedPreparedOfficial(platform))
                    cacheService.CancelPreload();
                if (state is not null && Revoked(state.Pending?.Media.Target))
                    CancelPendingMedia(state, "AUTH_REQUIRED");
                CanLoadMoreSourcesChanged?.Invoke();
                if (!revokeActive || state is null) return;
                Interlocked.Increment(ref officialAccountEpoch);
                try { state.Access.Cancel(); } catch (ObjectDisposedException) { }
                TrackRecovery(StopRevoked());
                async Task StopRevoked()
                {
                    var epoch = state.Epoch;
                    await RetireAsync();
                    if (epoch == readTelemetry().PlaybackEpoch && ctx.PlaybackDevice.OwnerId == ctx.Id && ctx.PlaybackDevice.Identity == state.Song.EntryId)
                        await ctx.PlaybackDevice.StopAsync();
                }
            });
    }

    /// <summary>A resolved network input whose real duration contradicts the recording is the wrong
    /// recording: drop it for this session and change source. Unknown durations never mismatch.</summary>
    internal void CheckMediaDuration()
    {
        if (musicRecovery is not { Session: { } session, Media: { } media } state || state.Recovering ||
            state.PendingFailure == "RECORDING_MISMATCH" || !CurrentRecovery(state) || !readTelemetry().OwnsCurrent || !ctx.PlaybackDevice.HasMedia) return;
        double actual = ctx.PlaybackDevice.DurationSeconds;
        if (actual <= 0 || MusicPlaybackSourceService.ExpectedDuration(state.Song) <= 0 || !double.IsFinite(actual)) return;
        if (!MusicPlaybackSourceService.MediaDurationMismatch(state.Song, actual)) return;
        session.Reject(media, "RECORDING_MISMATCH");
        metadataService.InvalidateFeedback();
        state.PendingFailure = "RECORDING_MISMATCH";
        state.RejectedOutput = ctx.PlaybackDevice.StopOutputForHandoffAsync();
        if (!state.Opening) TrackRecovery(RecoverRejectedMusicAsync(state));
    }

    internal async Task<bool> RecoverRejectedMusicAsync(MusicRecovery state)
    {
        await state.RejectedOutput;
        if (!CurrentRecovery(state)) return false;
        bool recovered = await RecoverMusicAsync(state, expired: false, mismatch: true);
        if (!recovered && CurrentRecovery(state)) ReportRecoveryFailed();
        return recovered;
    }

    internal void AdmitSourceExclusion(string songId, string provider, string platform)
    {
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            session.Exclude(provider, platform);
            if (state.Pending?.Media.Target is { } pendingTarget && session.Excluded(pendingTarget))
                CancelPendingMedia(state, "SOURCE_EXCLUDED");
            if (state.Media?.Target is { } activeTarget && session.Excluded(activeTarget))
            {
                state.Switching?.Cancel();
                state.Expiry?.Cancel();
                metadataService.InvalidateFeedback();
                if (readTelemetry().OwnsCurrent) TrackRecovery(ctx.PlaybackDevice.PauseAsync());
            }
            UpdateSourceChoicesRequested?.Invoke();
        }
    }

    internal void OnSourceExclusionAdded(string songId, string provider, string platform)
    {
        sourceService.IncrementExclusionsRevision();
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            session.Exclude(provider, platform);
            UpdateSourceChoicesRequested?.Invoke();
        }
        sourceService.ForgetRecording(songId);
    }

    internal async Task OnSourceExclusionRemovedAsync(string songId, CancellationToken token)
    {
        sourceService.IncrementExclusionsRevision();
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            await session.LoadExclusionsAsync(token);
            UpdateSourceChoicesRequested?.Invoke();
        }
    }
}
