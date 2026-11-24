using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;
using System.ComponentModel;
using System.Text.Json;

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
            playbackCommands != 0 || id.Length == 0 || FilmJson.Bool(next.Attempt, "prepareOnly") ||
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
        try
        {
            await nativeWork.WaitAsync(publication.Token); entered = true;
            publication.Token.ThrowIfCancellationRequested();
            var receipt = FilmMediaReceipt.Read(await client.CallAsync("session.attempt",
                new { sessionId = candidateSnapshot.SessionId.ToString(), attemptId = id }, publication.Token));
            if (receipt.AttemptId != id) throw new FilmPlaybackException("INVALID_RESPONSE");
            var candidate = FilmCandidateItem.Read(FilmJson.Member(candidateSnapshot.Attempt, "candidate"));
            // publication is already linked to activeConnection.Revoked and is detached from pendingPublication at commit.
            input = new(receipt, candidate, candidateSnapshot.Target, candidateSnapshot.WorkInfo?.Kind ?? candidateSnapshot.Target.Kind,
                FilmJson.Member(FilmJson.Member(candidateSnapshot.Attempt, "candidate"), "source").Clone(),
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
                // Keep the receipt tied to this session's RuntimeMediaAuthority.
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
                // Wait for a visible surface strictly OUTSIDE control so minimized/hidden
                // windows never block next/previous/switch/pause/seek commands.
                await media.WaitForTargetAsync(publication.Token);
                await EnterMediaCommitAsync(publication.Token);
                try
                {
                    if (generation != ownerGeneration || ticket != intentGeneration || snapshot?.AttemptId != id ||
                        FilmJson.Bool(snapshot.Attempt, "prepareOnly")) throw new OperationCanceledException(publication.Token);
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
                bool fromRelease = donorReleaseFailed || Code(error) == PlaybackError.ReleaseFailed;
                if (fromRelease) releaseFailure ??= error;
                SetExecutionError(Code(error), fromRelease: fromRelease);
                OperationFailed?.Invoke(this, Code(error));
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
            if (releaseError is not null) throw releaseError;
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
        var previous = input.Target;
        var next = replacement.Target;
        if (previous.Id != next.Id) return false;
        if (input.ContentKind == "movie" && replacement.ContentKind == "movie") return true;
        int? previousGlobal = input.Candidate.GlobalEpisode ?? previous.GlobalEpisode;
        int? nextGlobal = replacement.Candidate.GlobalEpisode ?? next.GlobalEpisode;
        if (previousGlobal is { } beforeGlobal && nextGlobal is { } afterGlobal)
            return beforeGlobal == afterGlobal;
        int? previousSeason = input.Candidate.Season ?? previous.Season;
        int? previousEpisode = input.Candidate.Episode ?? previous.Episode;
        int? nextSeason = replacement.Candidate.Season ?? next.Season;
        int? nextEpisode = replacement.Candidate.Episode ?? next.Episode;
        if (previousSeason is not null && previousEpisode is not null &&
            nextSeason is not null && nextEpisode is not null)
            return previousSeason == nextSeason && previousEpisode == nextEpisode;
        // Raw play references are intentionally absent from public snapshots.
        // Exact candidate equality or an explicit same-target replacement action
        // supplies continuation; missing coordinates never compare equal by accident.
        return input.Candidate.Id == replacement.Candidate.Id || continuingFrom == input.Receipt.AttemptId;
    }

    private static async ValueTask ReleaseResourceAsync(Input input, FilmPlaybackClient client, Guid sessionId, string resourceId)
    {
        // Native retirement may precede its terminal event. Retain this bounded
        // input identity when the release snapshot hides the receipt.
        input.Retiring = true;
        if (input.ResourceReleased) return;
        // A revoked runtime epoch cannot retain a usable resource. Still drain
        // native output locally; never send releases to a replacement epoch.
        if (input.RuntimeRevoked.IsCancellationRequested)
        {
            input.ResourceReleased = true;
            return;
        }
        try
        {
            await client.ReleaseMediaAsync(sessionId, input.Receipt.AttemptId, resourceId, input.RuntimeRevoked);
            input.ResourceReleased = true;
        }
        catch (Exception error) when (input.RuntimeRevoked.IsCancellationRequested && Code(error) is "CLOSED" or "CANCELLED")
        {
            input.ResourceReleased = true;
        }
    }

    private Task ReleaseInputAsync(Input input, FilmPlaybackClient client, Guid sessionId)
    {
        TaskCompletionSource completion;
        lock (input.ReleaseGate)
        {
            input.ReleaseClient = client;
            input.ReleaseSessionId = sessionId;
            if (input.Release is { IsFaulted: false, IsCanceled: false } existing) return existing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Release = completion.Task;
        }
        _ = RunReleaseInputAsync(input, client, sessionId, completion);
        return completion.Task;
    }

    private async Task RunReleaseInputAsync(Input input, FilmPlaybackClient client, Guid sessionId,
        TaskCompletionSource completion)
    {
        try
        {
            await ReleaseInputCoreAsync(input, client, sessionId);
            completion.TrySetResult();
        }
        catch (OperationCanceledException error)
        {
            completion.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    private static void DisposeInputTokens(Input input)
    {
        bool dispose = false;
        lock (input.ReleaseGate)
        {
            if (!input.TokensDisposed)
            {
                input.TokensDisposed = true;
                dispose = true;
            }
        }
        if (dispose)
        {
            input.Lifetime.Dispose();
        }
    }

    private async Task ReleaseInputCoreAsync(Input input, FilmPlaybackClient client, Guid sessionId)
    {
        input.Retiring = true;
        Exception? failure = null;
        if (!input.ResourceReleased)
        {
            try
            {
                if (input.Lease is not null) await input.Lease.DisposeAsync();
                else await ReleaseResourceAsync(input, client, sessionId, input.Receipt.ResourceId);
            }
            catch (Exception error) { failure ??= error; }
        }
        bool shouldCancel;
        lock (input.ReleaseGate) shouldCancel = !input.TokensDisposed;
        if (shouldCancel)
        {
            try { await input.Lifetime.CancelAsync(); }
            catch (ObjectDisposedException) { }
            catch (Exception error) { failure ??= error; }
            if (input.IsDrained) DisposeInputTokens(input);
        }
        lock (undrainedGate)
        {
            if (input.IsDrained)
            {
                undrainedInputs.Remove(input);
            }
            else if (!undrainedInputs.Contains(input))
            {
                undrainedInputs.Add(input);
            }
        }
        if (failure is not null)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "session.media", "media.release.failed",
                new { sessionId, attemptId = input.Receipt.AttemptId, drained = input.IsDrained, code = Code(failure) }, error: failure);
            if (!input.IsDrained) releaseFailure ??= failure;
            throw failure;
        }
        DebugLog.Debug(DebugCategories.FilmPlayback, "session.media", "media.release",
            new { sessionId, attemptId = input.Receipt.AttemptId });
    }

    // Must be called while holding `nativeWork`. Retries any previously undrained discards/inputs
    // and clears `releaseFailure` once physical drain completes or old runtime epochs are revoked.
    private async Task<Exception?> DrainUndrainedAsync()
    {
        Exception? firstFailure = null;
        RuntimeMediaPlaybackIntent[] pendingDiscards;
        lock (undrainedGate) pendingDiscards = undrainedDiscards.ToArray();
        foreach (var intent in pendingDiscards)
        {
            try
            {
                await media.DiscardAsync(intent);
                lock (undrainedGate) undrainedDiscards.Remove(intent);
            }
            catch (PlaybackException error) when (error.Code is PlaybackError.Disposed or PlaybackError.Superseded)
            {
                lock (undrainedGate) undrainedDiscards.Remove(intent);
            }
            catch (Exception error)
            {
                firstFailure ??= error;
            }
        }
        Input[] pendingInputs;
        lock (undrainedGate) pendingInputs = undrainedInputs.ToArray();
        foreach (var input in pendingInputs)
        {
            if (input.IsDrained)
            {
                DisposeInputTokens(input);
                lock (undrainedGate) undrainedInputs.Remove(input);
                continue;
            }
            FilmPlaybackClient? client;
            Guid releaseSessionId;
            lock (input.ReleaseGate)
            {
                client = input.ReleaseClient;
                releaseSessionId = input.ReleaseSessionId;
            }
            if (client is null)
            {
                lock (undrainedGate) undrainedInputs.Remove(input);
                continue;
            }
            try
            {
                await ReleaseInputAsync(input, client, releaseSessionId);
            }
            catch (Exception error)
            {
                firstFailure ??= error;
            }
        }
        bool allDrained;
        lock (undrainedGate) allDrained = undrainedInputs.Count == 0 && undrainedDiscards.Count == 0;
        if (allDrained && firstFailure is null)
        {
            if (releaseFailure is not null)
            {
                DebugLog.Info(DebugCategories.FilmPlayback, "session.media", "media.release.recovered");
                releaseFailure = null;
                if (executionErrorFromRelease || ExecutionError == PlaybackError.ReleaseFailed)
                    SetExecutionError("");
            }
            return null;
        }
        releaseFailure = firstFailure ?? releaseFailure;
        return releaseFailure;
    }

    /// <summary>
    /// Cooperative cross-domain preemption (e.g., music playback acquiring audio output).
    /// Cancels pending candidates without reporting media.failed to Node and drains active native output
    /// while keeping the Node session and playback page alive so the user can resume cleanly.
    /// </summary>
    internal Task PreemptAsync() =>
        dispatcher.HasThreadAccess ? PreemptCoreAsync() : dispatcher.EnqueueAsync(PreemptCoreAsync);

    private async Task PreemptCoreAsync()
    {
        if (closed) return;
        if (processingId.Length > 0) revokedPublicationId = processingId;
        pendingPublication?.Cancel();
        var active = current;
        if (active is not null)
        {
            active.Preempted = true;
            active.Retiring = true;
        }
        if (DebugLog.IsEnabled && (active is not null || processingId.Length > 0))
            DebugLog.Info(DebugCategories.FilmPlayback, "session", "media.preempted",
                new { sessionId = snapshot?.SessionId.ToString(), attemptId = active?.Receipt.AttemptId, cancelledPending = processingId.Length > 0 ? processingId : null });
        await nativeWork.WaitAsync();
        try
        {
            await control.WaitAsync();
            try
            {
                active = current ?? active;
                if (active is not null)
                {
                    active.Preempted = true;
                    active.Retiring = true;
                    if (active.Started && rpc is { } progressClient && snapshot is { } progressSnap &&
                        connection is { Revoked.IsCancellationRequested: false } activeConn)
                    {
                        try
                        {
                            await progressClient.CommandAsync(progressSnap.SessionId, "media.progress", new
                            {
                                attemptId = active.Receipt.AttemptId,
                                position = media.PositionSeconds,
                                duration = media.DurationSeconds,
                                paused = true
                            }, activeConn.Revoked);
                        }
                        catch { }
                    }
                    try
                    {
                        if (active.NativeScope is { } scope && media.Scope == scope)
                            await media.StopAsync(scope);
                    }
                    catch (Exception error)
                    {
                        if (DebugLog.IsEnabled)
                            DebugLog.Warn(DebugCategories.FilmPlayback, "session", "preempt.stop_error",
                                new { code = Code(error) });
                    }
                    if (rpc is { } client && snapshot is { } state)
                    {
                        try { await ReleaseInputAsync(active, client, state.SessionId); }
                        catch (Exception error)
                        {
                            if (DebugLog.IsEnabled)
                                DebugLog.Warn(DebugCategories.FilmPlayback, "session", "preempt.release_error",
                                    new { code = Code(error) });
                        }
                    }
                    if (ReferenceEquals(current, active)) current = null;
                }
                else if (media.HasOutput)
                {
                    await media.StopAsync();
                }
                NotifyMedia();
            }
            finally { control.Release(); }
        }
        finally { nativeWork.Release(); }
    }

    private void RevokeMedia(bool cancelPending = true)
    {
        if (cancelPending) pendingPublication?.Cancel();
        if (current is { } input)
        {
            if (input.Stop is not null || input.Release is not null) return;
            input.Retiring = true;
            input.Stop = Track(StopRevokedAsync(input));
        }
    }

    private async Task StopRevokedAsync(Input input)
    {
        Exception? failure = null;
        // Revocation interrupts reads immediately; serialization only gates their physical drain.
        bool canCancel;
        lock (input.ReleaseGate) canCancel = !input.TokensDisposed;
        if (canCancel)
        {
            try { await input.Lifetime.CancelAsync(); }
            catch (ObjectDisposedException) { }
            catch (Exception error) { failure = error; }
        }
        await nativeWork.WaitAsync();
        try
        {
            await control.WaitAsync();
            try
            {
                try { if (input.NativeScope is { } scope && media.Scope == scope) await media.StopAsync(scope); }
                catch (Exception error) { failure ??= error; }
                try { if (rpc is { } client && snapshot is { } state) await ReleaseInputAsync(input, client, state.SessionId); }
                catch (Exception error) { failure ??= error; }
            }
            finally
            {
                if (ReferenceEquals(current, input)) current = null;
                NotifyMedia();
                control.Release();
            }
            if (failure is not null) throw failure;
        }
        finally { nativeWork.Release(); }
    }

    internal void SourcesChanged(IReadOnlyList<JsonElement> available)
    {
        if (snapshot is { } state && state.AttemptId == processingId &&
            !Available(FilmJson.Member(FilmJson.Member(state.Attempt, "candidate"), "source")))
            pendingPublication?.Cancel();
        foreach (var input in new[] { pending, current }.OfType<Input>())
        {
            if (input.Retiring) continue;
            if (!Available(input.Source))
            {
                if (ReferenceEquals(input, current)) RevokeMedia(cancelPending: false);
                else input.Lifetime.Cancel();
            }
        }
        // The snapshot already owns an attempt while its receipt RPC is in flight.
        // Formal source/pan account epochs are invalidated by their Node owner.
        bool Available(JsonElement expected) => FilmJson.Text(expected, "engine") != "cat" ||
            available.Any(value => FilmPlaybackOpen.SourceEquals(value, expected));
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
            if (Code(error) != "STALE_SESSION" || generation == ownerGeneration && ReferenceEquals(current, input))
                OperationFailed?.Invoke(this, Code(error));
        }
        finally
        {
            control.Release();
            await nativeWork.WaitAsync();
            try
            {
                await ReleaseInputAsync(input, client, state.SessionId);
            }
            catch (Exception error)
            {
                releaseFailure ??= error;
                if (generation == ownerGeneration) SetExecutionError(Code(error), fromRelease: true);
                throw;
            }
            finally
            {
                if (ReferenceEquals(current, input)) current = null;
                NotifyMedia();
                nativeWork.Release();
            }
        }
    }
}
