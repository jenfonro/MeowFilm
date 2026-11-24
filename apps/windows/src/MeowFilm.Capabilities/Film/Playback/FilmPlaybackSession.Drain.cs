using MeowFilm.Core.Debug;
using MeowFilm.Media;
using System.Text.Json;

namespace MeowFilm.Capabilities;

internal sealed partial class FilmPlaybackSession
{
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
}
