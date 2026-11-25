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

public sealed partial class MusicPlaybackController
{
    private MusicRecovery? musicRecovery;
    private CancellationTokenSource? onlineCancellation;
    private string? playbackInstallation;
    private long officialAccountEpoch;

    private bool CurrentPlaybackExcluded =>
        musicRecovery is { Session: { } session, Media.Target: { } target } && session.Excluded(target);

    internal void CancelOnline() => onlineCancellation?.Cancel();

    private CancellationTokenSource BeginLocalOnlineRequest(CancellationToken token)
    {
        onlineCancellation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        onlineCancellation = source;
        settings.SourceBindings();
        return source;
    }

    private void EndLocalOnlineRequest(CancellationTokenSource source)
    {
        if (ReferenceEquals(onlineCancellation, source)) onlineCancellation = null;
        source.Dispose();
        settings.SourceBindings();
    }

    private static bool RefreshableMusicError(string? code) => code is
        PlaybackError.HttpExpired or PlaybackError.HttpUnauthorized or PlaybackError.HttpNotFound or
        PlaybackError.HttpDeadline or PlaybackError.HttpNetworkFailed or PlaybackError.HttpTruncated or
        PlaybackError.HttpServerFailed or PlaybackError.HttpThrottled or PlaybackError.HttpRepresentationChanged or
        PlaybackError.HttpRangeRejected;

    private static bool CanChangeFailedSource(MusicRecovery state, string? code) =>
        !state.RecoveryDisabled && state.Media?.Target is not null &&
        code != PlaybackError.AudioDeviceUnavailable &&
        (RefreshableMusicError(code) || code is PlaybackError.HttpFormatUnsupported or
            PlaybackError.OpenFailed or PlaybackError.PlaybackFailed or "PLAYBACK_UNAVAILABLE" ||
            state.Media?.Bytes is not null && (code is PlaybackError.ReadFailed or PlaybackError.ReadTruncated or PlaybackError.ReadFormatUnsupported) ||
            code is not null && UnavailableSourceCandidate(code));

    private static bool RecoverableMusicError(MusicRecovery state, string? code) =>
        !state.RecoveryDisabled && code != PlaybackError.AudioDeviceUnavailable &&
        (!state.Attempted && RefreshableMusicError(code) || CanChangeFailedSource(state, code));

    private static bool RetryablePreparation(string code) => RefreshableMusicError(code) || UnavailableSourceCandidate(code) ||
        code is PlaybackError.OpenFailed or PlaybackError.PlaybackFailed or PlaybackError.HttpFormatUnsupported or PlaybackError.ReadFailed;

    private static bool UnusablePlaybackFile(Exception error) =>
        (error is PlaybackException native ? native.Code : error is MusicOperationException music ? music.Code : null) is
            "CACHE_MISS" or PlaybackError.OpenFailed or PlaybackError.ReadFailed or PlaybackError.ReadTruncated or
            PlaybackError.ReadChanged or PlaybackError.ReadFormatUnsupported;

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

    private bool CurrentRecovery(MusicRecovery state) =>
        !stopping && !disposed &&
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
        bool reopening, CancellationToken token)
    {
        DiscardPendingMedia(state);
        if (reopening) SampleFeedback(false);
        media = PreparePlaybackBytes(state, media);
        var pending = new PendingMusic(media, CancellationTokenSource.CreateLinkedTokenSource(token), reopening);
        state.Pending = pending;
        state.PendingFailure = null;
        return pending;
    }

    private static void CancelPendingMedia(MusicRecovery state, string failure)
    {
        if (state.Pending is not { } pending) return;
        state.PendingFailure = failure;
        if (!ReferenceEquals(pending.Media.Bytes, state.Media?.Bytes)) pending.Media.Bytes?.Dispose();
        try { pending.Access.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void DiscardPendingMedia(MusicRecovery state)
    {
        var pending = state.Pending;
        state.Pending = null;
        if (pending is null) return;
        if (!ReferenceEquals(pending.Media.Bytes, state.Media?.Bytes)) pending.Media.Bytes?.Dispose();
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

    private async Task ReopenPendingMediaAsync(
        NativePlaybackContinuation continuation, MusicRecovery state, PendingMusic pending)
    {
        try
        {
            if (pending.Media.Uri is null || pending.Media.Bytes is not null)
            {
                var lease = await OpenPlaybackFileAsync(state, pending.Media, pending.Access.Token);
                if (lease is null) throw new MusicOperationException("CACHE_MISS");
                await playback.ReopenReadAsync(continuation, lease, pending.Access.Token);
            }
            else await playback.ReopenAsync(continuation, pending.Media.Uri, pending.Media.Headers, pending.Access.Token);
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

    private async Task<MusicSourceSession> CurrentSourceSessionAsync(MusicRecovery state, CancellationToken token)
    {
        var previous = state.Session!;
        if (previous.Revision == SelectionSourcesRevision && previous.Signature == settings.SavedQualitySignature) return previous;
        var session = await BeginSourceSessionAsync(state.Song, token);
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
        state.Session = await BeginSourceSessionAsync(state.Song, state.Token);
        return await state.Session.AutomaticAsync(state.Token);
    }

    private async Task OpenOnlineCurrentAsync(SongRow song, long epoch, CancellationToken token, Func<bool>? admission = null)
    {
        var access = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var state = new MusicRecovery(song, epoch, access) { Opening = true, AccountEpoch = Interlocked.Read(ref officialAccountEpoch) };
        musicRecovery = state;
        onlineCancellation = access;
        bool succeeded = false;
        try
        {
            try { state.Media = await ResolveOnlineAsync(state); }
            finally { CancelPreparedAudioIfMismatch(song, state.Media?.Target); }
            if (state.Media.Target?.Official == true) state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
            if (!CurrentRecovery(state) || admission is not null && !admission()) throw new OperationCanceledException(state.Token);
            state.Media = PreparePlaybackBytes(state, state.Media);
            state.Session?.Adopt(state.Media);
            ObservePlaybackCache(state);
            AcceptOfficialSong(song, state.Media.Target);
            UpdateSourceChoicesIfOpen();
            UpdateFeedbackSelection();
            SourcesUpdatedForLyrics?.Invoke();
            playbackInstallation = state.Media.Target!.Installation;
            openedEntry = song.EntryId; recorded = false;
            try
            {
                while (true)
                {
                    var lease = await OpenPlaybackFileAsync(state, state.Media, state.Token);
                    if (lease is not null)
                    {
                        try
                        {
                            if (!CurrentRecovery(state) || admission is not null && !admission())
                                throw new OperationCanceledException(state.Token);
                        }
                        catch { await lease.DisposeAsync(); throw; }
                        state.Cached = state.Media.Bytes is null;
                        try { await playback.OpenReadAsync(Id, song.EntryId!, lease, song.Title, state.Token); }
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
                    else
                    {
                        if (!CurrentRecovery(state) || admission is not null && !admission())
                            throw new OperationCanceledException(state.Token);
                        await playback.OpenAsync(Id, song.EntryId!, state.Media.Uri!, song.Title, state.Media.Headers, state.Token);
                    }
                    break;
                }
                async Task ReselectFile()
                {
                    var previousBytes = state.Media?.Bytes;
                    var session = await CurrentSourceSessionAsync(state, state.Token);
                    var refreshed = await RefreshMissingFileAsync(state, session, state.Media!, state.Token);
                    state.Media = await session.AutomaticAsync(state.Token, refreshed);
                    state.Media = PreparePlaybackBytes(state, state.Media);
                    if (!ReferenceEquals(previousBytes, state.Media.Bytes)) previousBytes?.Dispose();
                    session.Adopt(state.Media);
                    ObservePlaybackCache(state);
                    state.Cached = false;
                    state.PendingFailure = null;
                    playbackInstallation = state.Media.Target!.Installation;
                    if (state.Media.Target.Official) state.AccountEpoch = Interlocked.Read(ref officialAccountEpoch);
                    AcceptOfficialSong(song, state.Media.Target);
                    UpdateFeedbackSelection();
                    UpdateSourceChoicesIfOpen();
                    SourcesUpdatedForLyrics?.Invoke();
                }
            }
            catch (Exception error) when (error is PlaybackException native && RecoverableMusicError(state, native.Code) ||
                error is MusicOperationException operation && RecoverableMusicError(state, operation.Code))
            {
                if (!recorded) SearchRecommendationCleared?.Invoke();
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
                TrackMusicRecovery(RetireMusicRecovery());
            settings.SourceBindings();
        }
    }

    private void ScheduleExpiry(MusicRecovery state)
    {
        state.Expiry?.Cancel(); state.Expiry?.Dispose(); state.Expiry = null;
        long? deadline = state.Cached || state.Media?.Bytes is not null ? null : state.Media?.ExpiresAt;
        if (deadline is { } expiry)
        {
            state.Expiry = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
            TrackMusicRecovery(ExpireMusicAsync(state, expiry, state.Expiry.Token));
        }
    }

    private async Task ExpireMusicAsync(MusicRecovery state, long expiry, CancellationToken token)
    {
        try
        {
            while (expiry - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() is var remaining && remaining > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, int.MaxValue)), token);
            token.ThrowIfCancellationRequested();
            if (state.Recovering) await state.Work.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (CurrentRecovery(state) && !await RecoverMusicAsync(state, expired: true) && CurrentRecovery(state))
            {
                if (OwnsCurrent) await playback.StopAsync();
                ReportRecoveryFailed();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (CurrentRecovery(state)) ReportRecoveryFailed(); }
    }

    private bool TryRecover()
    {
        var state = musicRecovery;
        if (state is null || !CurrentRecovery(state)) return false;
        if (state.Switching is not null)
        {
            state.SwitchingFailure = playback.LastErrorCode ?? PlaybackError.PlaybackFailed;
            return true;
        }
        if (state.Opening || state.Recovering)
        {
            state.PendingFailure = playback.LastErrorCode ?? PlaybackError.PlaybackFailed;
            return true;
        }
        if (playback.LastErrorCode == PlaybackError.AudioDeviceUnavailable) return false;
        if (state.Cached && !state.Attempted && playback.LastErrorCode != PlaybackError.ReadRevoked)
        {
            TrackMusicRecovery(RecoverMusicAsync(state, expired: true));
            return true;
        }
        if (!RecoverableMusicError(state, playback.LastErrorCode)) return false;
        TrackMusicRecovery(RecoverMusicAsync(state, expired: false));
        return true;
    }

    private async Task<bool> RecoverMusicAsync(MusicRecovery state, bool expired, bool mismatch = false)
    {
        if (!CurrentRecovery(state) || state.RecoveryDisabled || state.Recovering ||
            state.Media?.Target is null ||
            playback.OwnerId != Id ||
            playback.Identity != state.Song.EntryId ||
            !mismatch && !expired && !RecoverableMusicError(state, playback.LastErrorCode) ||
            (expired || mismatch) && (mismatch || state.Attempted) && !CanChangeFailedSource(state, PlaybackError.HttpExpired))
            return false;
        var continuation = playback.CaptureContinuation();
        if (continuation is null) return false;
        string? originalFailure = expired ? null : playback.LastErrorCode;
        bool refreshExact = !mismatch && !state.Attempted && (expired || RefreshableMusicError(playback.LastErrorCode));
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
            if (state.Cached) await InvalidateFileCacheAsync(state);
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
                        var session = await CurrentSourceSessionAsync(state, request.Token);
                        refreshed = state.Selection.Automatic ? await session.AutomaticAsync(request.Token, retry) :
                            state.Selection.Tier is { } tier
                                ? await session.ExactAsync(tier, request.Token, retry, state.Selection.Manual) ??
                                    throw new MusicOperationException("QUALITY_UNAVAILABLE") : retry;
                    }
                    else if (refreshExact)
                    {
                        refreshExact = false;
                        state.Attempted = true;
                        var session = await CurrentSourceSessionAsync(state, request.Token);
                        refreshed = refreshedReceipt = await session.RefreshAsync(state.Media!.Target!, request.Token);
                        if (state.Selection.Automatic) refreshed = await session.AutomaticAsync(request.Token, refreshedReceipt);
                    }
                    else
                    {
                        if (!CanChangeFailedSource(state, PlaybackError.HttpExpired) || state.Session is null)
                            throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                        if (rejectCurrent)
                        {
                            state.Session.Reject(state.Media!, playback.LastErrorCode ?? "PLAYBACK_UNAVAILABLE");
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
                    await RestorePlaybackRateAsync(state.Song, state.Epoch, pending.Access.Token);
                    await state.Session!.LoadExclusionsAsync(pending.Access.Token);
                    EnsurePendingMedia(state, pending);
                    if (state.PendingFailure is not null || !playback.HasMedia || playback.LastErrorCode is not null)
                        throw new MusicOperationException(state.PendingFailure ?? playback.LastErrorCode ?? PlaybackError.OpenFailed);
                    if (MediaDurationMismatch(state.Song, playback.DurationSeconds))
                        throw new MusicOperationException("RECORDING_MISMATCH");
                    if (!CommitPendingMedia(state, pending, out bool alreadyStarted))
                        throw new OperationCanceledException(request.Token);
                    AcceptOfficialSong(state.Song, refreshed.Target);
                    UpdateFeedbackSelection();
                    UpdateSourceChoicesIfOpen();
                    SourcesUpdatedForLyrics?.Invoke();
                    PlaybackSourceBindings();
                    RestoreLyricsRequested?.Invoke();
                    ScheduleExpiry(state);
                    if (alreadyStarted) ConfirmRecoveredSourceStarted(state);
                    return true;
                }
                catch (Exception error) when (cachedAttempt && error is not OperationCanceledException &&
                    CurrentRecovery(state) && (error is PlaybackException native && RetryablePreparation(native.Code) ||
                        error is MusicOperationException operation && RetryablePreparation(operation.Code)))
                {
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
                            await audioFileCache.EraseAsync(file.Scope, state.Song.Id, file.Signature, request.Token);
                    }
                    DiscardPendingMedia(state);
                    continuation = playback.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                    state.PendingFailure = null;
                }
                catch (MusicOperationException error) when (error.Code == "CACHE_MISS" &&
                    state.Pending?.Media is { Uri: null } && CurrentRecovery(state))
                {
                    var missing = state.Pending.Media;
                    DiscardPendingMedia(state);
                    fileRetry = await RefreshMissingFileAsync(state,
                        await CurrentSourceSessionAsync(state, request.Token), missing, request.Token);
                    if (fileRetry is null && state.Selection.Source is not null)
                        throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                    continuation = playback.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
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
                    continuation = playback.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
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
                ReportRecoveryFailed();
            }
            throw;
        }
        catch (Exception error)
        {
            DiscardPendingMedia(state);
            if (CurrentRecovery(state))
            {
                if (playback.OwnerId == Id && playback.Identity == state.Song.EntryId) await playback.StopAsync();
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

    private void ReportRecoveryFailed(Exception? error = null)
    {
        FinishFeedback("error");
        ctx.SetStatus(error is PlaybackException { Code: PlaybackError.AudioDeviceUnavailable }
            or MusicOperationException { Code: PlaybackError.AudioDeviceUnavailable }
            ? ErrorText(error)
            : "播放失败");
    }

    internal void TrackMusicRecovery(Task task)
    {
        var observed = ObserveRecovery();
        AddEventWrite(observed);
        _ = Remove();
        async Task ObserveRecovery()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!stopping) ReportRecoveryFailed(); }
        }
        async Task Remove() { await observed; RemoveEventWrite(observed); }
    }

    internal Task RetireMusicRecovery()
    {
        var state = musicRecovery; musicRecovery = null;
        if (state is null) return Task.CompletedTask;
        CloseSourceChoices();
        state.Access.Cancel();
        state.Media?.Bytes?.Dispose();
        if (state.ObservedBytes is { } bytes && state.CacheProgress is { } progress)
            bytes.ProgressChanged -= progress;
        PlaybackSourceBindings();
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

    internal async Task RevokeChangedMusicSource(string? installation)
    {
        if (musicRecovery is { Recovering: true, Switching: null, Pending: { } pending } recovery &&
            pending.Media.File is null && pending.Media.Target?.Installation == installation)
            CancelPendingMedia(recovery, "SOURCES_CHANGED");
        if (musicRecovery?.Media?.Target?.Installation != installation || installation is null ||
            musicRecovery is { Recovering: false } or { Switching: not null } or { Cached: true }) return;
        var epoch = playbackEpoch; var identity = openedEntry;
        await RetireMusicRecovery();
        if (epoch == playbackEpoch && playback.OwnerId == Id && playback.Identity == identity)
            await playback.StopAsync();
    }

    private void ConfirmOfficialPlayback()
    {
        if (musicRecovery is not { Media.Target.Official: true } state || !CurrentRecovery(state)) return;
        TrackMusicRecovery(ConfirmOfficialPlaybackAsync(state.Song, state.Media!.Target!, state.Token));
    }

    private void OnOfficialAccountChanged(string platform)
    {
        IncrementSelectionSourcesRevision();
        if (!ctx.Dispatcher.IsClosing)
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (stopping || disposed) return;
                bool Revoked(MusicOnlineTarget? target) =>
                    target is { Official: true, AccountScope: { } scope } && target.Platform == platform &&
                    !application.IsOfficialPlaybackCurrent(platform, scope, target.Generation);
                var state = musicRecovery;
                bool revokeActive = Revoked(state?.Media?.Target);
                if (revokeActive || HasRevokedPreparedOfficial(platform))
                    CancelMusicPreload();
                if (state is not null && Revoked(state.Pending?.Media.Target))
                    CancelPendingMedia(state, "AUTH_REQUIRED");
                Changed(nameof(CanLoadMoreSources));
                if (!revokeActive || state is null) return;
                Interlocked.Increment(ref officialAccountEpoch);
                try { state.Access.Cancel(); } catch (ObjectDisposedException) { }
                TrackMusicRecovery(StopRevoked());
                async Task StopRevoked()
                {
                    var epoch = state.Epoch;
                    await RetireMusicRecovery();
                    if (epoch == playbackEpoch && playback.OwnerId == Id && playback.Identity == state.Song.EntryId)
                        await playback.StopAsync();
                }
            });
    }

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
        state.RejectedOutput = playback.StopOutputForHandoffAsync();
        if (!state.Opening) TrackMusicRecovery(RecoverRejectedMusicAsync(state));
    }

    private async Task<bool> RecoverRejectedMusicAsync(MusicRecovery state)
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
                InvalidateFeedback();
                if (OwnsCurrent) TrackMusicRecovery(playback.PauseAsync());
            }
            UpdateSourceChoicesIfOpen();
        }
    }

    internal void OnSourceExclusionAdded(string songId, string provider, string platform)
    {
        IncrementExclusionsRevision();
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            session.Exclude(provider, platform);
            UpdateSourceChoicesIfOpen();
        }
        ForgetRecording(songId);
    }

    internal async Task OnSourceExclusionRemovedAsync(string songId, CancellationToken token)
    {
        IncrementExclusionsRevision();
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            await session.LoadExclusionsAsync(token);
            UpdateSourceChoicesIfOpen();
        }
    }
}
