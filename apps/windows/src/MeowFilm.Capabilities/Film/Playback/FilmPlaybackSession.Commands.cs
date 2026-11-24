using MeowFilm.Core.Debug;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

internal sealed partial class FilmPlaybackSession
{
    internal async Task WaitForBrowsingAsync(FilmPlaybackPage targetPage,
        Func<FilmPlaybackSnapshot, bool> matches, CancellationToken token)
    {
        RequirePage(targetPage);
        var activeConnection = connection!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, activeConnection.Revoked);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            RequirePage(targetPage);
            if (targetPage.ConnectionFailed) throw new FilmPlaybackException("DISCONNECTED");
            var changed = browseChanged.Task;
            if (snapshot is { } state && !FilmJson.Bool(state.Browsing, "loading"))
            {
                if (FilmJson.Text(state.Browsing, "error") is { Length: > 0 } failure)
                    throw new FilmPlaybackException(failure);
                if (matches(state)) return;
            }
            await changed.WaitAsync(linked.Token);
        }
    }

    internal Task CommandAsync(string action, object? parameters = null)
    {
        if (rpc is null || snapshot is null || connection is null || closed || closing is not null)
            return Task.FromException(new FilmPlaybackException("STALE_SESSION"));
        if (ExecutionError.Length > 0 && ChangesPlayback(action) && action != "stop")
            return Task.FromException(new FilmPlaybackException(ExecutionError));
        long ticket = intentGeneration;
        if (ChangesPlayback(action))
        {
            blockedAttemptId = "";
            if (playbackCommands++ == 0)
                playbackAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (action is not ("block" or "media.retry"))
            {
                continuingFrom = action is "pan" or "quality" or "switch" ? current?.Receipt.AttemptId ?? "" : "";
            }
            // Node resolves each intent, including repeated exact selections.
            // Fence native commit while it decides; only its accepted snapshot
            // may replace a pending receipt. Stop/address-only preparation revoke
            // native publication immediately rather than starting output.
            if (action is "stop" or "prepare")
            {
                ticket = ++intentGeneration;
                if (pendingPublication is not null) revokedPublicationId = processingId;
                pendingPublication?.Cancel();
            }
        }
        DebugLog.Debug(DebugCategories.FilmPlayback, "session.command", "command.dispatch",
            new { action, ticket, sessionId = snapshot.SessionId });
        return Track(CommandCoreAsync(action, parameters, ticket));
    }

    private async Task CommandCoreAsync(string action, object? parameters, long ticket)
    {
        var client = rpc ?? throw new FilmPlaybackException("STALE_SESSION");
        var id = snapshot?.SessionId ?? throw new FilmPlaybackException("STALE_SESSION");
        var activeConnection = connection ?? throw new FilmPlaybackException("STALE_SESSION");
        long generation = ownerGeneration;
        Exception? stopFailure = null;
        await control.WaitAsync();
        try
        {
            if (generation != ownerGeneration || closing is not null) throw new FilmPlaybackException("STALE_SESSION");
            if (action == "stop")
            {
                try { await FlushProgressLockedAsync(); }
                catch (Exception error) { stopFailure = error; }
            }
            try
            {
                var value = await client.CommandAsync(id, action, parameters, activeConnection.Revoked);
                if (generation == ownerGeneration)
                {
                    if (action is "block" or "media.retry" && ticket == intentGeneration)
                        continuingFrom = current?.Receipt.AttemptId ?? "";
                    Apply(new(value), scheduleMedia: action != "browse.refresh" &&
                        !ChangesPlayback(action) && ticket == intentGeneration);
                }
            }
            catch (Exception error) when (action == "stop") { stopFailure ??= error; }
        }
        catch (Exception error) when (generation != ownerGeneration && action != "stop" &&
            Code(error) != PlaybackError.ReleaseFailed)
        {
            // The admitted RPC is drained, but its retired UI no longer owns
            // success or business-error publication. Physical release errors remain failures.
        }
        finally
        {
            control.Release();
            if (ChangesPlayback(action) && --playbackCommands == 0)
            {
                var admission = playbackAdmission; playbackAdmission = null;
                admission?.TrySetResult();
                if (generation == ownerGeneration && snapshot is { } latest) SchedulePendingMedia(latest);
            }
        }
        if (action == "stop")
        {
            // A progress or RPC failure is not permission to leave the actual
            // stopped output running. Drain it and then preserve the failure.
            if (generation == ownerGeneration) RevokeMedia();
            if (current?.Stop is { } stopping)
                try { await stopping; } catch (Exception error) { stopFailure ??= error; }
            // A cancelled pending prepare still owns native work until its discard/release settles.
            await nativeWork.WaitAsync();
            try { stopFailure ??= await DrainUndrainedAsync(); }
            finally { nativeWork.Release(); }
            if (stopFailure is not null) throw stopFailure;
            if (generation == ownerGeneration) SetExecutionError("");
        }
    }

    private static bool ChangesPlayback(string action) => action is "play" or "prepare" or "stop" or
        "select.target" or "next" or "previous" or "pan" or "quality" or "switch" or "block" or "media.retry";

    internal Task PauseAsync() => SetPausedAsync(!MediaPaused);

    internal Task SetPausedAsync(bool paused) => Track(ControlMediaAsync(async input =>
    {
        await media.SetPausedAsync(input.NativeScope!, paused);
        await FlushProgressLockedAsync();
        NotifyMedia();
    }));

    internal Task SeekAsync(double position)
    {
        if (!double.IsFinite(position) || position < 0 || Duration > 0 && position > Duration)
            throw new FilmPlaybackException("INVALID_REQUEST");
        return Track(ControlMediaAsync(async input =>
        {
            await media.SeekAsync(input.NativeScope!, position);
            await FlushProgressLockedAsync();
            NotifyMedia();
        }));
    }

    internal Task RateAsync(double value)
    {
        if (!double.IsFinite(value) || value <= 0) throw new FilmPlaybackException("INVALID_REQUEST");
        return Track(ControlMediaAsync(async input =>
        {
            await media.RateAsync(input.NativeScope!, value);
            rate = value; NotifyMedia();
        }));
    }

    internal Task AspectAsync(string? value)
    {
        if (value is not (null or "16:9" or "4:3")) throw new FilmPlaybackException("INVALID_REQUEST");
        return Track(ControlMediaAsync(async input =>
        {
            await media.AspectAsync(input.NativeScope!, value);
            aspect = value; NotifyMedia();
        }));
    }

    internal Task SetMutedAsync(bool muted) => Track(ControlMediaAsync(async input =>
    {
        await media.SetMutedAsync(input.NativeScope!, muted);
        NotifyMedia();
    }));

    internal Task<FilmMediaTracks> ReadTracksAsync() =>
        ControlMediaResultAsync(input => media.ReadTracksAsync(input.NativeScope!));

    internal Task<IReadOnlyList<FilmMediaChapter>> ReadChaptersAsync() =>
        ControlMediaResultAsync(input => media.ReadChaptersAsync(input.NativeScope!));

    internal Task<FilmKernelDiagnostics> ReadDiagnosticsAsync() =>
        ControlMediaResultAsync(input => media.ReadDiagnosticsAsync(input.NativeScope!));

    internal Task SelectVideoTrackAsync(int index) => Track(ControlMediaAsync(async input =>
    {
        await media.SelectVideoTrackAsync(input.NativeScope!, index);
        NotifyMedia();
    }));

    internal Task SelectAudioTrackAsync(int index) => Track(ControlMediaAsync(async input =>
    {
        await media.SelectAudioTrackAsync(input.NativeScope!, index);
        NotifyMedia();
    }));

    internal Task SelectSubtitleTrackAsync(int? index) => Track(ControlMediaAsync(async input =>
    {
        await media.SelectSubtitleTrackAsync(input.NativeScope!, index);
        NotifyMedia();
    }));

    private async Task<T> ControlMediaResultAsync<T>(Func<Input, Task<T>> operation)
    {
        var input = current ?? throw new FilmPlaybackException("STALE_SESSION");
        long generation = ownerGeneration;
        await control.WaitAsync();
        try
        {
            if (generation != ownerGeneration || closing is not null || !ReferenceEquals(current, input) || !HasOutput)
                throw new FilmPlaybackException("STALE_SESSION");
            return await operation(input);
        }
        finally { control.Release(); }
    }

    private async Task ControlMediaAsync(Func<Input, Task> operation)
    {
        var input = current ?? throw new FilmPlaybackException("STALE_SESSION");
        long generation = ownerGeneration;
        await control.WaitAsync();
        try
        {
            if (generation != ownerGeneration || closing is not null || !ReferenceEquals(current, input) || !HasOutput)
                throw new FilmPlaybackException("STALE_SESSION");
            await operation(input);
        }
        finally { control.Release(); }
    }

    private void QueueProgress(bool immediate = false)
    {
        if (!HasOutput || current?.Started != true || rpc is null || snapshot is null || connection is null) return;
        long now = Environment.TickCount64;
        if (!immediate && MediaPaused == lastProgressPaused && now - lastProgressSentTick < ProgressIntervalMs)
            return;
        progressDirty = true;
        if (progress is null || progress.IsCompleted) progress = Track(ProgressCoreAsync(), notifyError: false);
    }

    // Must only be called while holding `control`. Sends progress directly without awaiting
    // the background `ProgressCoreAsync` loop, preventing a control-lock deadlock.
    private async Task FlushProgressLockedAsync()
    {
        progressDirty = false;
        if (current is not { Started: true } input || snapshot is not { } state || rpc is not { } client ||
            connection is not { Revoked.IsCancellationRequested: false } activeConnection || !HasOutput)
            return;
        long generation = ownerGeneration;
        bool pausedNow = MediaPaused;
        lastProgressSentTick = Environment.TickCount64;
        lastProgressPaused = pausedNow;
        try
        {
            await client.CommandAsync(state.SessionId, "media.progress", new
            {
                attemptId = input.Receipt.AttemptId, position = Position,
                duration = Duration, paused = pausedNow
            }, activeConnection.Revoked);
        }
        catch (Exception error)
        {
            if (Code(error) == "STALE_SESSION" && (generation != ownerGeneration || !ReferenceEquals(current, input)))
                return;
            throw;
        }
    }

    private async Task ProgressCoreAsync()
    {
        while (progressDirty)
        {
            var activeConnection = connection;
            if (activeConnection is null || activeConnection.Revoked.IsCancellationRequested)
            {
                progressDirty = false;
                return;
            }
            await control.WaitAsync(activeConnection.Revoked);
            try
            {
                if (!progressDirty || current is not { Started: true } input || snapshot is not { } state ||
                    rpc is not { } client || !ReferenceEquals(connection, activeConnection) || !HasOutput)
                {
                    progressDirty = false;
                    return;
                }
                progressDirty = false;
                long generation = ownerGeneration;
                bool pausedNow = MediaPaused;
                lastProgressSentTick = Environment.TickCount64;
                lastProgressPaused = pausedNow;
                try
                {
                    await client.CommandAsync(state.SessionId, "media.progress", new
                    {
                        attemptId = input.Receipt.AttemptId, position = Position,
                        duration = Duration, paused = pausedNow
                    }, activeConnection.Revoked);
                }
                catch (Exception error)
                {
                    if (Code(error) == "STALE_SESSION" && (generation != ownerGeneration || !ReferenceEquals(current, input)))
                        return;
                    throw;
                }
            }
            finally { control.Release(); }
        }
    }
}
