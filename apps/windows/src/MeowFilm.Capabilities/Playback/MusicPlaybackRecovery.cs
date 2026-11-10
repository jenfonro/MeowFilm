using System.Text.Json;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private sealed record MusicOnlineTarget(string? Installation, string Platform, long Generation,
        string? SourceKey, string PolicyRequest, string Quality,
        string? QualityPolicySignature = null, string? QualityCacheSignature = null,
        string? AccountScope = null, JsonElement? RecordingSong = null,
        string? ProviderName = null, long SourceRevision = 0, bool ManualSource = false)
    {
        internal bool Official => Installation is null;
    }
    private sealed record ResolvedMusic(Uri? Uri, IReadOnlyDictionary<string, string> Headers,
        MusicOnlineTarget? Target = null, long? ExpiresAt = null, JsonElement? Receipt = null,
        MusicFileIdentity? File = null);
    private sealed record MusicFileIdentity(string Scope, string Signature);
    // User intent outlives every resolved URL and every recovery attempt.
    private sealed record MusicSelection(MusicQualityPolicy.Tier? Tier = null, ResolvedMusic? Source = null,
        bool ManualSource = false)
    {
        internal bool Automatic => Tier is null && Source is null;
        internal bool Manual => ManualSource || Source is not null;
        internal bool Allows(ResolvedMusic media) =>
            (Tier is null || Tier.Levels.Contains(EffectiveLevel(media))) &&
            (Source is null || SourceIdentity(Source.Target!) == SourceIdentity(media.Target!) &&
                Source.Target!.PolicyRequest == media.Target!.PolicyRequest &&
                Source.Target.Quality == media.Target.Quality &&
                // A request label is not measured quality. Keep the exact target,
                // and reject a changed actual level only when both receipts prove it.
                (!QualityVerified(Source) || !QualityVerified(media) || EffectiveLevel(Source) == EffectiveLevel(media)));
    }
    private static MusicFileIdentity? FileIdentity(ResolvedMusic media) => media.File ??
        (media.Target is { Official: true, AccountScope: not null, QualityCacheSignature: not null } target
            ? new(target.AccountScope, target.QualityCacheSignature) : null);
    private sealed class PendingMusic(ResolvedMusic media, CancellationTokenSource access, bool restartFeedback)
    {
        internal readonly ResolvedMusic Media = media;
        internal readonly CancellationTokenSource Access = access;
        internal readonly bool RestartFeedback = restartFeedback;
        internal bool SourceStarted;
    }
    private sealed class MusicRecovery(SongRow song, long epoch, CancellationTokenSource access)
    {
        internal readonly SongRow Song = song;
        internal readonly long Epoch = epoch;
        internal readonly CancellationTokenSource Access = access;
        internal readonly CancellationToken Token = access.Token;
        internal long AccountEpoch;
        internal ResolvedMusic? Media;
        internal PendingMusic? Pending;
        internal bool Attempted, Opening, Recovering, Cached, RecoveryDisabled;
        internal bool ConfirmSourceOnStart, RestartFeedbackOnStart, DurationChecked;
        internal CancellationTokenSource? Switching;
        /// <summary>The one source selection of this playback; every source operation shares it.</summary>
        internal MusicSourceSession? Session;
        internal MusicSelection Selection = new();
        internal CancellationTokenSource? Expiry;
        internal string? PendingFailure, SwitchingFailure;
        internal Task Work = Task.CompletedTask;
        internal readonly TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private MusicRecovery? musicRecovery;

    private static bool RefreshableMusicError(string? code) => code is
        PlaybackError.HttpExpired or PlaybackError.HttpUnauthorized or PlaybackError.HttpNotFound or
        PlaybackError.HttpDeadline or PlaybackError.HttpNetworkFailed or PlaybackError.HttpTruncated or
        PlaybackError.HttpServerFailed or PlaybackError.HttpThrottled or PlaybackError.HttpRepresentationChanged or
        PlaybackError.HttpRangeRejected;

    private static bool CanChangeFailedSource(MusicRecovery state, string? code) =>
        !state.RecoveryDisabled && state.Media?.Target is not null &&
        (RefreshableMusicError(code) || code is PlaybackError.HttpFormatUnsupported or
            PlaybackError.OpenFailed or PlaybackError.PlaybackFailed or "PLAYBACK_UNAVAILABLE" ||
            code is not null && UnavailableSourceCandidate(code));

    private static bool RecoverableMusicError(MusicRecovery state, string? code) =>
        !state.RecoveryDisabled &&
        (!state.Attempted && RefreshableMusicError(code) || CanChangeFailedSource(state, code));

    private void QualityPolicyChanged(string signature)
    {
        CancelMusicPreload();
        if (musicRecovery is { Media.Target.QualityPolicySignature: not null } state &&
            state.Media.Target.QualityPolicySignature != signature && !state.Selection.Manual)
        {
            state.RecoveryDisabled = true;
            CancelPendingMedia(state, "QUALITY_POLICY_CHANGED");
        }
    }

    private Task<ResolvedMusic> ResolveMusicTarget(SongRow song, MusicOnlineTarget target,
        CancellationToken token, bool initial = false) => target.Official
            ? ResolveOfficialTarget(song, target, token, initial)
            : ResolveMusicQualityTarget(song, target, token, initial);

    private bool CurrentRecovery(MusicRecovery state) => !stopping && !disposed &&
        ReferenceEquals(musicRecovery, state) && state.Epoch == playbackEpoch &&
        (state.Media?.Target?.Official != true || state.AccountEpoch == Interlocked.Read(ref officialAccountEpoch)) &&
        (state.Media?.Target is not { Official: true, AccountScope: not null } official ||
            application.IsOfficialPlaybackCurrent(official.Platform, official.AccountScope, official.Generation)) &&
        current?.EntryId == state.Song.EntryId && !state.Access.IsCancellationRequested;

    private bool CurrentPendingMedia(MusicRecovery state, PendingMusic pending) =>
        CurrentRecovery(state) && ReferenceEquals(state.Pending, pending) &&
        !pending.Access.IsCancellationRequested &&
        (pending.Media.Target is not { } candidate || state.Session?.Excluded(candidate) != true) &&
        (pending.Media.Target is not { Official: true, AccountScope: not null } target ||
            application.IsOfficialPlaybackCurrent(target.Platform, target.AccountScope, target.Generation));

    private PendingMusic BeginPendingMedia(MusicRecovery state, ResolvedMusic media,
        bool restartFeedback, CancellationToken token)
    {
        DiscardPendingMedia(state);
        if (restartFeedback && recorded) SampleFeedback();
        var pending = new PendingMusic(media, CancellationTokenSource.CreateLinkedTokenSource(token), restartFeedback);
        state.Pending = pending;
        state.PendingFailure = null;
        return pending;
    }

    private static void CancelPendingMedia(MusicRecovery state, string failure)
    {
        if (state.Pending is not { } pending) return;
        state.PendingFailure = failure;
        try { pending.Access.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void DiscardPendingMedia(MusicRecovery state)
    {
        var pending = state.Pending;
        state.Pending = null;
        if (pending is null) return;
        try { pending.Access.Cancel(); }
        catch (ObjectDisposedException) { }
        pending.Access.Dispose();
    }

    private bool CommitPendingMedia(MusicRecovery state, PendingMusic pending, out bool alreadyStarted, bool published = false)
    {
        alreadyStarted = false;
        if (published ? !ReferenceEquals(musicRecovery, state) || !ReferenceEquals(state.Pending, pending)
            : !CurrentPendingMedia(state, pending)) return false;
        alreadyStarted = pending.SourceStarted;
        state.Media = pending.Media;
        state.Session?.Adopt(pending.Media);
        playbackInstallation = pending.Media.Target?.Installation;
        if (pending.Media.Target?.Official == true)
            state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
        state.Cached = pending.Media.Uri is null;
        state.DurationChecked = false;
        state.PendingFailure = null;
        state.SwitchingFailure = null;
        state.Pending = null;
        pending.Access.Dispose();
        state.ConfirmSourceOnStart = true;
        state.RestartFeedbackOnStart = pending.RestartFeedback;
        return true;
    }

    private void EnsurePendingMedia(MusicRecovery state, PendingMusic pending)
    {
        if (CurrentPendingMedia(state, pending)) return;
        if (CurrentRecovery(state) && ReferenceEquals(state.Pending, pending) &&
            state.PendingFailure is { } failure)
            throw new MusicOperationException(failure);
        if (pending.Media.Target is { } target && state.Session?.Excluded(target) == true)
            throw new MusicOperationException("SOURCE_EXCLUDED");
        throw new OperationCanceledException(pending.Access.Token);
    }

    private async Task ReopenPendingMedia(INativePlaybackContinuation port,
        NativePlaybackContinuation continuation, MusicRecovery state, PendingMusic pending)
    {
        try
        {
            if (pending.Media.Uri is null)
            {
                var lease = await OpenPlaybackFileAsync(state, pending.Media, pending.Access.Token);
                if (lease is null) throw new MusicOperationException("CACHE_MISS");
                await port.ReopenReadAsync(continuation, lease, pending.Access.Token);
            }
            else await port.ReopenAsync(continuation, pending.Media.Uri, pending.Media.Headers, pending.Access.Token);
        }
        catch (OperationCanceledException)
        {
            EnsurePendingMedia(state, pending);
            throw;
        }
        EnsurePendingMedia(state, pending);
    }

    private void ConfirmRecoveredSourceStarted(MusicRecovery state)
    {
        if (CurrentRecovery(state)) AcceptPlaybackStarted(state);
    }

    private async Task OpenOnlineCurrent(SongRow song, long epoch, CancellationToken token, Func<bool>? admission = null)
    {
        var access = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var state = new MusicRecovery(song, epoch, access) { Opening = true, AccountEpoch = Interlocked.Read(ref officialAccountEpoch) };
        musicRecovery = state;
        // Retain the cancellation source for the whole playback, not just initial resolution.
        onlineCancellation = access;
        try
        {
            try { state.Media = await ResolveOnline(state); }
            finally
            {
                // A prepared official file is speculative, not a playback choice.
                // If another source wins (or resolution fails), retire its producer
                // rather than waiting for a Started event that can never arrive.
                if (preparedAudio is { } prepared && (preparedEntry != song.EntryId ||
                    state.Media?.Target is not { Official: true, AccountScope: not null, QualityCacheSignature: not null } accepted ||
                    !prepared.Matches(accepted.AccountScope, song.Id, accepted.QualityCacheSignature)))
                    CancelPreparedAudio();
            }
            // Initial account hydration may notify while resolving; the native issued authority,
            // not an assumption about UI initialization order, decides whether this receipt is current.
            if (state.Media.Target?.Official == true) state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
            if (!CurrentRecovery(state) || admission is not null && !admission()) throw new OperationCanceledException(state.Token);
            AcceptOfficialSong(song, state.Media.Target);
            UpdateRecordingCandidates();
            UpdateFeedbackSelection();
            UpdateLyricSources();
            playbackInstallation = state.Media.Target!.Installation;
            openedEntry = song.EntryId; recorded = false;
            try
            {
                while (true)
                {
                    var lease = await OpenPlaybackFileAsync(state, state.Media, state.Token);
                    bool authorizedFile = lease is not null;
                    if (lease is null && preparedAudio is { } prepared && preparedEntry == song.EntryId &&
                        state.Media.Target is { AccountScope: not null, QualityCacheSignature: not null } pendingTarget &&
                        prepared.Matches(pendingTarget.AccountScope, song.Id, pendingTarget.QualityCacheSignature))
                        lease = await prepared.OpenAsync(state.Token);
                    if (lease is not null)
                    {
                        try
                        {
                            if (!authorizedFile && state.Media.Target is { Official: true } cachedTarget &&
                                state.Media.Receipt is { } cachedReceipt &&
                                (!cachedReceipt.TryGetProperty("offlineAllowed", out var offline) ||
                                    offline.ValueKind != JsonValueKind.True))
                                await QualityCall("music.official.authorize",
                                    OfficialInput(song, cachedTarget, false, false), state.Token);
                            if (!CurrentRecovery(state) || admission is not null && !admission())
                                throw new OperationCanceledException(state.Token);
                        }
                        catch { await lease.DisposeAsync(); throw; }
                        state.Cached = true;
                        try { await playback.OpenReadAsync(Id, song.EntryId!, lease, song.Title, state.Token); }
                        catch (Exception error) when (error is PlaybackException or NotSupportedException)
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
                    else await OpenNetwork();
                    break;
                }
                async Task OpenNetwork()
                {
                    if (!CurrentRecovery(state) || admission is not null && !admission())
                        throw new OperationCanceledException(state.Token);
                    await playback.OpenAsync(Id, song.EntryId!, state.Media.Uri!, song.Title, state.Media.Headers, state.Token);
                }
                async Task ReselectFile()
                {
                    var session = await CurrentSourceSession(state, state.Token);
                    var refreshed = await RefreshMissingPlaybackFileAsync(state, session, state.Media!, state.Token);
                    state.Media = await session.AutomaticAsync(state.Token, refreshed);
                    state.Cached = false;
                    state.PendingFailure = null;
                    playbackInstallation = state.Media.Target!.Installation;
                    if (state.Media.Target.Official) state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
                    AcceptOfficialSong(song, state.Media.Target);
                    UpdateFeedbackSelection(); UpdateRecordingCandidates(); UpdateLyricSources();
                }
            }
            catch (Exception error) when (error is PlaybackException native && RecoverableMusicError(state, native.Code) ||
                error is MusicOperationException operation && RecoverableMusicError(state, operation.Code))
            {
                if (!recorded) searchRecommendationStart = null;
                state.Opening = false;
                if (!await RecoverMusic(state, expired: false)) throw;
            }
            state.Opening = false;
            if (!CurrentRecovery(state) || admission is not null && !admission()) throw new OperationCanceledException(state.Token);
            if (state.PendingFailure is { } failure)
            {
                if (!RecoverableMusicError(state, failure) || !await RecoverMusic(state, expired: false))
                    throw new MusicOperationException(failure);
            }
            CheckMediaDuration();
            ScheduleMusicExpiry(state);
        }
        finally
        {
            state.Opening = false;
            state.Opened.TrySetResult();
            if (ReferenceEquals(onlineCancellation, access)) onlineCancellation = null;
            OnlineBindings();
        }
    }
    private void ScheduleMusicExpiry(MusicRecovery state)
    {
        state.Expiry?.Cancel(); state.Expiry?.Dispose(); state.Expiry = null;
        long? deadline = state.Cached ? null : state.Media?.ExpiresAt;
        if (state.Media?.Receipt is { } receipt && receipt.TryGetProperty("licenseExpiresAt", out var license) &&
            license.ValueKind == JsonValueKind.Number) deadline = Math.Min(deadline ?? long.MaxValue, license.GetInt64());
        if (deadline is { } expiry && playback is INativePlaybackContinuation)
        {
            state.Expiry = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
            var timer = ExpireMusic(state, expiry, state.Expiry.Token);
            TrackMusicRecovery(timer);
        }
    }
    private async Task ExpireMusic(MusicRecovery state, long expiry, CancellationToken token)
    {
        try
        {
            // Exactly one dormant timer, not a refresh loop. Very long signed URLs use bounded delay chunks.
            while (expiry - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() is var remaining && remaining > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, int.MaxValue)), token);
            token.ThrowIfCancellationRequested();
            if (state.Recovering) await state.Work.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (CurrentRecovery(state) && !await RecoverMusic(state, expired: true) && CurrentRecovery(state))
            {
                // Exhausting retry authority must not extend a cached license
                // indefinitely. A replacement timer cancels this old callback.
                if (OwnsCurrent) await playback.StopAsync();
                MusicRecoveryFailed();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (CurrentRecovery(state)) MusicRecoveryFailed(error); }
    }
    private bool TryMusicRecovery()
    {
        var state = musicRecovery;
        if (state is null || !CurrentRecovery(state)) return false;
        if (state.Switching is not null)
        {
            // Handoff preparation has its own failure channel. Failed here belongs to
            // the accepted input, and must survive a missing/cancelled replacement.
            state.SwitchingFailure = (playback as INativePlaybackContinuation)?.LastErrorCode ?? PlaybackError.PlaybackFailed;
            return true;
        }
        if (state.Opening || state.Recovering)
        {
            state.PendingFailure = (playback as INativePlaybackContinuation)?.LastErrorCode ?? PlaybackError.PlaybackFailed;
            return true;
        }
        if (state.Cached && !state.Attempted &&
            playback is INativePlaybackContinuation { LastErrorCode: not PlaybackError.ReadRevoked })
        {
            TrackMusicRecovery(RecoverMusic(state, expired: true));
            return true;
        }
        if (playback is not INativePlaybackContinuation port ||
            !RecoverableMusicError(state, port.LastErrorCode)) return false;
        TrackMusicRecovery(RecoverMusic(state, expired: false));
        return true;
    }
    private async Task<bool> RecoverMusic(MusicRecovery state, bool expired, bool mismatch = false)
    {
        if (!CurrentRecovery(state) || state.RecoveryDisabled || state.Recovering ||
            state.Media?.Target is null ||
            playback is not INativePlaybackContinuation port || playback.OwnerId != Id ||
            playback.Identity != state.Song.EntryId ||
            !mismatch && !expired && !RecoverableMusicError(state, port.LastErrorCode) ||
            (expired || mismatch) && (mismatch || state.Attempted) && !CanChangeFailedSource(state, PlaybackError.HttpExpired))
            return false;
        var continuation = port.CaptureContinuation();
        if (continuation is null) return false;
        string? originalFailure = expired ? null : port.LastErrorCode;
        // A mismatched recording is never refreshed: a different source must replace it.
        bool refreshExact = !mismatch && !state.Attempted && (expired || RefreshableMusicError(port.LastErrorCode));
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
            var sourceSession = await CurrentSourceSession(state, request.Token);
            if (originalFailure is not null) sourceSession.ObserveFailure(state.Media.Target, originalFailure);
            if (state.Cached) await InvalidatePlaybackFileCache(state);
            // A failed URL says nothing about the complete bytes already stored for it.
            // Try those before spending the one exact network refresh, even for a retired script.
            ResolvedMusic? completedFile = !state.Cached && !mismatch
                ? await CompletedPlaybackFileAsync(state, sourceSession, state.Media!, state.Selection, request.Token)
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
                        var session = await CurrentSourceSession(state, request.Token);
                        refreshed = state.Selection.Automatic ? await session.AutomaticAsync(request.Token, retry) :
                            state.Selection.Tier is { } tier
                                ? await session.ExactAsync(tier, request.Token, retry, state.Selection.Manual) ??
                                    throw new MusicOperationException("QUALITY_UNAVAILABLE") : retry;
                    }
                    else if (refreshExact)
                    {
                        refreshExact = false;
                        state.Attempted = true; // One exact refresh per selected occurrence.
                        var session = await CurrentSourceSession(state, request.Token);
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
                            state.Session.Reject(state.Media!, port.LastErrorCode ?? "PLAYBACK_UNAVAILABLE");
                            rejectCurrent = false;
                        }
                        if (state.Selection.Source is not null) throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                        var session = await CurrentSourceSession(state, request.Token);
                        refreshed = state.Selection.Tier is { } tier
                            ? await session.ExactAsync(tier, request.Token, refreshedReceipt, state.Selection.Manual) ??
                                throw new MusicOperationException(session.Failure ?? "PLAYBACK_UNAVAILABLE")
                            : await session.AutomaticAsync(request.Token, refreshedReceipt);
                    }
                    if (!state.Selection.Allows(refreshed))
                        throw new MusicOperationException("QUALITY_UNAVAILABLE");
                    request.Token.ThrowIfCancellationRequested();
                    if (!CurrentRecovery(state)) throw new OperationCanceledException(request.Token);
                    if (state.RecoveryDisabled || state.Session?.Signature != savedQualitySignature)
                        throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                    bool changedSource = SourceIdentity(state.Media!.Target!) != SourceIdentity(refreshed.Target!);
                    var pending = BeginPendingMedia(state, refreshed, changedSource, request.Token);
                    EnsurePendingMedia(state, pending);
                    await ReopenPendingMedia(port, continuation, state, pending);
                    request.Token.ThrowIfCancellationRequested();
                    EnsurePendingMedia(state, pending);
                    await RestorePlaybackRateAsync(state.Song, state.Epoch, pending.Access.Token);
                    await state.Session!.LoadExclusionsAsync(pending.Access.Token);
                    EnsurePendingMedia(state, pending);
                    if (state.PendingFailure is not null || !playback.HasMedia || port.LastErrorCode is not null)
                        throw new MusicOperationException(state.PendingFailure ?? port.LastErrorCode ?? PlaybackError.OpenFailed);
                    if (MediaDurationMismatch(state.Song, playback.DurationSeconds))
                        throw new MusicOperationException("RECORDING_MISMATCH");
                    if (!CommitPendingMedia(state, pending, out bool alreadyStarted))
                        throw new OperationCanceledException(request.Token);
                    if (changedSource && recorded) CompleteFeedback("source-change");
                    AcceptOfficialSong(state.Song, refreshed.Target);
                    if (changedSource) SelectFeedback(state.Song, currentFeedbackAutomatic, currentRecommendationFeedback);
                    UpdateFeedbackSelection(); UpdateRecordingCandidates(); UpdateLyricSources();
                    PlaybackSourceBindings();
                    if (NeedsLyrics) _ = RestoreLyricsAsync();
                    ScheduleMusicExpiry(state);
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
                        if (audioFileCache is not null && FileIdentity(failed) is { } file)
                            await audioFileCache.EraseAsync(file.Scope, state.Song.Id, request.Token);
                    }
                    DiscardPendingMedia(state);
                    continuation = port.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                    state.PendingFailure = null;
                }
                catch (MusicOperationException error) when (error.Code == "CACHE_MISS" &&
                    state.Pending?.Media is { Uri: null } && CurrentRecovery(state))
                {
                    var missing = state.Pending.Media;
                    DiscardPendingMedia(state);
                    fileRetry = await RefreshMissingPlaybackFileAsync(state,
                        await CurrentSourceSession(state, request.Token), missing, request.Token);
                    if (fileRetry is null && state.Selection.Source is not null)
                        throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                    continuation = port.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
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
                    continuation = port.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                    state.PendingFailure = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            DiscardPendingMedia(state);
            if (CurrentRecovery(state) && !continuation.Revoked.IsCancellationRequested)
            {
                if (playback.OwnerId == Id && playback.Identity == state.Song.EntryId) await playback.StopAsync();
                MusicRecoveryFailed();
            }
            throw;
        }
        catch (Exception error)
        {
            DiscardPendingMedia(state);
            if (CurrentRecovery(state))
            {
                if (playback.OwnerId == Id && playback.Identity == state.Song.EntryId) await playback.StopAsync();
                MusicRecoveryFailed(error);
            }
            // Callers may report a normal exhausted recovery themselves. A cache data
            // failure must keep its type all the way to that outer status owner.
            if (PlaybackCacheFailure(error)) throw;
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
    private static bool PlaybackCacheFailure(Exception? error) => error is
        PlaybackCache.MusicPlaybackCacheDataException or PlaybackCache.MusicPlaybackCacheCommitException or
        MusicOperationException { Code: "CACHE_DATA_INCOMPATIBLE" };
    private void MusicRecoveryFailed(Exception? error = null)
    { FinishFeedback("error"); status = PlaybackCacheFailure(error) ? ErrorText(error!) : "播放失败"; Changed(nameof(Status)); }
    private void TrackMusicRecovery(Task task)
    {
        var observed = ObserveRecovery();
        eventWrites.Add(observed);
        _ = Remove();
        async Task ObserveRecovery()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!stopping) MusicRecoveryFailed(error); }
        }
        async Task Remove() { await observed; eventWrites.Remove(observed); }
    }
    private Task RetireMusicRecovery()
    {
        var state = musicRecovery; musicRecovery = null;
        if (state is null) return Task.CompletedTask;
        CloseSourceChoices();
        state.Access.Cancel();
        return Drain();
        async Task Drain()
        {
            try { await Task.WhenAll(state.Opened.Task, state.Work); }
            finally
            {
                DiscardPendingMedia(state);
                state.Expiry?.Cancel(); state.Expiry?.Dispose(); state.Access.Dispose();
            }
        }
    }

    private async Task RevokeChangedMusicSource(string? installation)
    {
        if (musicRecovery is { Recovering: true, Switching: null, Pending: { } pending } recovery &&
            pending.Media.File is null && pending.Media.Target?.Installation == installation)
            CancelPendingMedia(recovery, "SOURCES_CHANGED");
        // Resolved URLs and downloaded files are independent history. An in-flight parser
        // still belongs to the installation and must drain before its content is replaced.
        if (musicRecovery?.Media?.Target?.Installation != installation || installation is null ||
            musicRecovery is { Recovering: false } or { Switching: not null } or { Cached: true }) return;
        var epoch = playbackEpoch; var identity = openedEntry;
        await RetireMusicRecovery();
        if (epoch == playbackEpoch && playback.OwnerId == Id && playback.Identity == identity)
            await playback.StopAsync();
    }
}
