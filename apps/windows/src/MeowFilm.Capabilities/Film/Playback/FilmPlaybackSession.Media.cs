using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

internal sealed partial class FilmPlaybackSession
{
    private async Task EnterMediaCommitAsync(CancellationToken token)
    {
        while (true)
        {
            if (playbackAdmission is { } admission) await admission.Task.WaitAsync(token);
            await control.WaitAsync(token);
            // A command can arrive after a ready candidate queues for control.
            // Let that command run first; never wait for it while holding control.
            if (playbackCommands == 0) return;
            control.Release();
        }
    }

    private void SchedulePendingMedia(FilmPlaybackSnapshot next)
    {
        string id = next.AttemptId;
        if (ExecutionError.Length > 0 || releaseFailure is not null ||
            playbackCommands != 0 || id.Length == 0 || next.Attempt?.PrepareOnly == true ||
            id == revokedPublicationId || id == blockedAttemptId || id == processingId || id == current?.Receipt.AttemptId) return;
        processingId = id;
        pendingPublication = CancellationTokenSource.CreateLinkedTokenSource(connection!.Revoked);
        UpdateDebugSnapshot();
        DebugLog.Debug(DebugCategories.FilmPlayback, "session.media", "media.schedule",
            new { sessionId = next.SessionId, attemptId = id });
        Track(ConsumeAsync(next, ownerGeneration, intentGeneration, pendingPublication), notifyError: false);
    }

    private async Task PrepareNativeMediaAsync(RuntimeMediaPlaybackIntent intent, FilmMediaReceipt receipt,
        INativeMediaResourceLease? lease, CancellationToken token)
    {
        NativeVideoInput videoInput;
        try { videoInput = FilmVideoInput.Read(receipt.Format, receipt.Drm); }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
        await media.PrepareAsync(intent, receipt.Address, videoInput, lease, token);
    }

    private async Task ConsumeAsync(FilmPlaybackSnapshot candidateSnapshot, long generation, long ticket,
        CancellationTokenSource publication)
    {
        var client = rpc!;
        var activeConnection = connection!;
        string id = candidateSnapshot.AttemptId;
        Input? input = null, donor = null;
        RuntimeMediaPlaybackIntent? intent = null;
        Task? started = null;
        bool entered = false, handedToMedia = false, prepared = false, committed = false;
        bool donorReleaseAttempted = false, donorReleaseFailed = false;
        string? notifiedError = null;
        try
        {
            await nativeWork.WaitAsync(publication.Token); entered = true;
            publication.Token.ThrowIfCancellationRequested();
            var receipt = FilmMediaReceipt.Read(await client.CallAsync("session.attempt",
                new { sessionId = candidateSnapshot.SessionId.ToString(), attemptId = id }, publication.Token));
            if (receipt.AttemptId != id) throw new FilmPlaybackException("INVALID_RESPONSE");
            var attemptInfo = candidateSnapshot.Attempt ?? throw new FilmPlaybackException("INVALID_RESPONSE");
            input = new(receipt, attemptInfo, candidateSnapshot.TargetId, candidateSnapshot.Work.Kind,
                publication, activeConnection.Revoked)
            {
                ReleaseClient = client,
                ReleaseSessionId = candidateSnapshot.SessionId
            };
            pending = input;
            UpdateDebugSnapshot();
            publication.Token.ThrowIfCancellationRequested();
            if (receipt.MediaOrigin is not null)
            {
                var sessionAuthority = authority ?? throw new FilmPlaybackException("STALE_SESSION");
                input.Lease = sessionAuthority.CreateLease(receipt.ResourceId, receipt.Address, publication.Token,
                    resourceId => ReleaseResourceAsync(input, client, candidateSnapshot.SessionId, resourceId));
            }
            bool sameEpisode = HasOutput && current is { } acceptedDonor && SameEpisode(acceptedDonor, input);
            intent = new RuntimeMediaPlaybackIntent("film", id,
                receipt.Position, receipt.Paused, sameEpisode ? current!.NativeScope : null);
            handedToMedia = true;
            await PrepareNativeMediaAsync(intent, receipt, input.Lease, publication.Token);
            prepared = true;
            while (!committed)
            {
                await media.WaitForTargetAsync(publication.Token);
                await EnterMediaCommitAsync(publication.Token);
                try
                {
                    if (generation != ownerGeneration || ticket != intentGeneration || snapshot?.AttemptId != id ||
                        snapshot.Attempt?.PrepareOnly == true) throw new OperationCanceledException(publication.Token);
                    donor = current;
                    bool admitted = await media.TryCommitAsync(intent, () => playbackCommands == 0, () =>
                    {
                        committed = true;
                        current = input; pending = null;
                        input.NativeScope = media.Scope;
                        if (ReferenceEquals(pendingPublication, publication)) pendingPublication = null;
                        // The actual output has committed. Begin its precise business receipt
                        // before waiting for the old native lease's physical retirement.
                        started = ConfirmStartedAsync(input, client, activeConnection, candidateSnapshot.SessionId, generation);
                    }, publication.Token);
                    // Native alignment may yield to a newer block. Keep the same
                    // prepared receipt and release control before awaiting that RPC.
                    if (!admitted) continue;
                    input.NativeScope = media.Scope;
                    if (!media.HasOutput || input.NativeScope?.Identity != id)
                        throw new FilmPlaybackException(PlaybackError.OpenFailed);
                    DebugLog.Info(DebugCategories.FilmPlayback, "session.media", "media.commit",
                        new { sessionId = candidateSnapshot.SessionId, attemptId = id, sameEpisode });
                    try
                    {
                        await media.RateAsync(input.NativeScope, rate);
                        await media.AspectAsync(input.NativeScope, aspect);
                    }
                    catch (Exception error) { OperationFailed?.Invoke(this, Code(error)); }
                    await started!;
                    if (donor is not null)
                    {
                        donorReleaseAttempted = true;
                        try { await ReleaseInputAsync(donor, client, candidateSnapshot.SessionId); }
                        catch { donorReleaseFailed = true; throw; }
                    }
                    NotifyMedia();
                    QueueProgress(immediate: true);
                }
                finally { control.Release(); }
            }
        }
        catch (OperationCanceledException) when (!committed && (publication.IsCancellationRequested ||
            input?.Lifetime.IsCancellationRequested == true || generation != ownerGeneration)) { }
        catch (PlaybackException error) when (!committed && error.Code == PlaybackError.Superseded) { }
        catch (Exception error)
        {
            if (committed)
            {
                if (started is not null)
                    try { await started; } catch { /* Original sanitized failure is reported below. */ }
                // Publication succeeded but drain/report failed. Do not falsely label the new media
                // a failed candidate or start another chain over an unknown native release outcome.
                string code = Code(error);
                bool fromRelease = donorReleaseFailed || code == PlaybackError.ReleaseFailed;
                if (fromRelease) releaseFailure ??= error;
                SetExecutionError(code, fromRelease: fromRelease);
                notifiedError = code;
                OperationFailed?.Invoke(this, code);
            }
            else if (!publication.IsCancellationRequested && generation == ownerGeneration && ticket == intentGeneration &&
                (snapshot?.AttemptId == id || input?.Retiring == true && snapshot?.AttemptId.Length == 0))
            {
                if (!handedToMedia)
                {
                    // Receipt RPC itself failed before handing to native media: do NOT report media.failed.
                    string code = Code(error);
                    blockedAttemptId = id;
                    DebugLog.Warn(DebugCategories.FilmPlayback, "session.media", "media.attempt.failed",
                        new { sessionId = candidateSnapshot.SessionId, attemptId = id, code }, error: error);
                    if (code is "CLOSED" or "DISCONNECTED" or "STALE_SESSION") page?.SetConnectionError(code);
                    notifiedError = code;
                    OperationFailed?.Invoke(this, code);
                }
                else
                {
                    try
                    {
                        var failed = await client.CommandAsync(candidateSnapshot.SessionId, "media.failed",
                            new { attemptId = id, code = Code(error) }, activeConnection.Revoked);
                        if (generation == ownerGeneration) Apply(new(failed));
                    }
                    catch (Exception reportError) { page?.SetConnectionError(Code(reportError)); }
                }
            }
        }
        finally
        {
            Exception? releaseError = null;
            // No future intent can publish this uncommitted input while it is
            // being discarded. Detach before ReleaseInput disposes its source.
            if (!committed && ReferenceEquals(pendingPublication, publication)) pendingPublication = null;
            try
            {
                if (!committed)
                {
                    if (prepared)
                    {
                        try { await media.DiscardAsync(intent!); }
                        catch (Exception error)
                        {
                            releaseError = error;
                            lock (undrainedGate)
                            {
                                if (!undrainedDiscards.Contains(intent!)) undrainedDiscards.Add(intent!);
                            }
                        }
                    }
                    if (input is not null)
                        try { await ReleaseInputAsync(input, client, candidateSnapshot.SessionId); }
                        catch (Exception error) { releaseError ??= error; }
                    if (ReferenceEquals(pending, input)) pending = null;
                    if (input is null) publication.Dispose();
                }
                else if (donor is not null && !donorReleaseAttempted)
                    try { await ReleaseInputAsync(donor, client, candidateSnapshot.SessionId); }
                    catch (Exception error) { releaseError = error; }
            }
            finally
            {
                if (processingId == id) processingId = "";
                if (releaseError is not null)
                {
                    releaseFailure ??= releaseError;
                    if (generation == ownerGeneration && ExecutionError.Length == 0)
                        SetExecutionError(Code(releaseError), fromRelease: true);
                }
                UpdateDebugSnapshot();
                if (entered) nativeWork.Release();
            }
            if (releaseError is not null)
            {
                string releaseCode = Code(releaseError);
                if (notifiedError != releaseCode) OperationFailed?.Invoke(this, releaseCode);
                throw releaseError;
            }
            // Only a different pending attempt needs rescheduling. Replaying a
            // settled snapshot would clear a local post-commit failure.
            if (generation == ownerGeneration && snapshot is { } latest &&
                latest.AttemptId.Length > 0 && latest.AttemptId != id) SchedulePendingMedia(latest);
        }
    }

    private async Task ConfirmStartedAsync(Input input, FilmPlaybackClient client, RuntimeConnection activeConnection,
        Guid sessionId, long generation)
    {
        var response = await client.CommandAsync(sessionId, "media.started",
            new { attemptId = input.Receipt.AttemptId }, activeConnection.Revoked);
        input.Started = true;
        if (generation == ownerGeneration) Apply(new(response));
    }

    private bool SameEpisode(Input input, Input replacement)
    {
        if (input.TargetId != replacement.TargetId) return false;
        if (input.ContentKind == "movie" && replacement.ContentKind == "movie") return true;
        if (input.AttemptInfo.GlobalEpisode is { } beforeGlobal && replacement.AttemptInfo.GlobalEpisode is { } afterGlobal)
            return beforeGlobal == afterGlobal;
        if (input.AttemptInfo.Season is { } ps && input.AttemptInfo.Episode is { } pe &&
            replacement.AttemptInfo.Season is { } ns && replacement.AttemptInfo.Episode is { } ne)
            return ps == ns && pe == ne;
        return (input.AttemptInfo.CandidateId.Length > 0 && input.AttemptInfo.CandidateId == replacement.AttemptInfo.CandidateId)
            || continuingFrom == input.Receipt.AttemptId;
    }

    private void MediaChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (current is not { } input) return;
        if (media.Scope is { } scope && scope.Identity == input.Receipt.AttemptId) input.NativeScope = scope;
        if (args.PropertyName is "Scope" or "Identity" or "HasOutput")
        {
            // Native publication changes several properties before its atomic
            // committed callback. Recheck this captured input after that turn,
            // not in the middle of a successful Film-to-Film handoff.
            _ = dispatcher.EnqueueAsync(new Action(() =>
            {
                if (!ReferenceEquals(current, input) || HoldsNativeScope(input) ||
                    input.TerminalReported || input.Retiring || input.Preempted || closing is not null) return;
                Track(CloseSessionAsync());
            }));
        }
        NotifyMedia();
        if (args.PropertyName is "PositionSeconds" or "DurationSeconds" or "IsPaused")
            QueueProgress(immediate: args.PropertyName == "IsPaused");
    }

    private void MediaFailed(object? sender, EventArgs args) =>
        Track(ReportTerminalAsync("media.failed", media.LastErrorCode ?? PlaybackError.PlaybackFailed), notifyError: false);

    private void MediaEnded(object? sender, EventArgs args) => Track(ReportTerminalAsync("media.ended"), notifyError: false);

    private async Task ReportTerminalAsync(string action, string? code = null)
    {
        if (current is not { } input || snapshot is not { } state || rpc is not { } client ||
            connection is not { } activeConnection ||
            media.Identity != input.Receipt.AttemptId || input.TerminalReported ||
            input.Preempted || input.Lifetime.IsCancellationRequested) return;
        input.TerminalReported = true;
        long generation = ownerGeneration;
        double position = media.PositionSeconds;
        double duration = media.DurationSeconds;
        bool paused = media.IsPaused;
        string? notifiedError = null;
        DebugLog.Info(DebugCategories.FilmPlayback, "session.media", "media.terminal",
            new { sessionId = state.SessionId, attemptId = input.Receipt.AttemptId, action, code, position, duration });
        await control.WaitAsync();
        if (generation != ownerGeneration || !ReferenceEquals(current, input))
        {
            control.Release();
            return;
        }
        try
        {
            Exception? progressFailure = null;
            if (input.Started)
            {
                try
                {
                    await client.CommandAsync(state.SessionId, "media.progress", new
                    {
                        attemptId = input.Receipt.AttemptId, position, duration, paused
                    }, activeConnection.Revoked);
                }
                catch (Exception error) { progressFailure ??= error; }
            }
            // Loss before business admission is a failed attempt, not completed viewing.
            var value = await client.CommandAsync(state.SessionId, input.Started ? action : "media.failed",
                new { attemptId = input.Receipt.AttemptId,
                    code = action == "media.failed" ? code : input.Started ? null : PlaybackError.OpenFailed },
                activeConnection.Revoked);
            if (generation == ownerGeneration) Apply(new(value));
            if (progressFailure is not null) throw progressFailure;
        }
        catch (Exception error)
        {
            string errorCode = Code(error);
            if (errorCode != "STALE_SESSION" || generation == ownerGeneration && ReferenceEquals(current, input))
            {
                notifiedError = errorCode;
                OperationFailed?.Invoke(this, errorCode);
            }
        }
        finally
        {
            control.Release();
            Exception? terminalReleaseError = null;
            await nativeWork.WaitAsync();
            try
            {
                await ReleaseInputAsync(input, client, state.SessionId);
            }
            catch (Exception error)
            {
                terminalReleaseError = error;
                releaseFailure ??= error;
                if (generation == ownerGeneration) SetExecutionError(Code(error), fromRelease: true);
            }
            finally
            {
                if (ReferenceEquals(current, input)) current = null;
                NotifyMedia();
                nativeWork.Release();
            }
            if (terminalReleaseError is not null)
            {
                string releaseCode = Code(terminalReleaseError);
                if (notifiedError != releaseCode) OperationFailed?.Invoke(this, releaseCode);
                throw terminalReleaseError;
            }
        }
    }
}
