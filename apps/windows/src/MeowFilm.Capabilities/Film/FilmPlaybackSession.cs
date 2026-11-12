using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>
/// Stable native film owner. Node is the only matching/cache/library owner; this class
/// observes snapshots, fences native publication and reports exact media occurrences.
/// </summary>
internal sealed class FilmPlaybackSession : IAsyncDisposable
{
    private sealed class Input(FilmMediaReceipt receipt, FilmCandidateItem candidate, FilmPlaybackTarget target, JsonElement source,
        CancellationTokenSource lifetime, CancellationTokenSource publication, CancellationToken runtimeRevoked)
    {
        internal readonly FilmMediaReceipt Receipt = receipt;
        internal readonly FilmCandidateItem Candidate = candidate;
        internal readonly FilmPlaybackTarget Target = target;
        internal readonly JsonElement Source = source;
        internal readonly CancellationTokenSource Lifetime = lifetime;
        internal readonly CancellationTokenSource Publication = publication;
        internal readonly CancellationToken RuntimeRevoked = runtimeRevoked;
        internal RuntimeMediaAuthority? Authority;
        internal INativeMediaResourceLease? Lease;
        internal NativePlaybackScope? NativeScope;
        internal readonly object ReleaseGate = new();
        internal Task? Release, Stop;
        internal volatile bool Retiring;
        internal bool Started, TerminalReported;
    }

    private readonly Func<CancellationToken, Task<RuntimeConnection>> capture;
    private readonly Func<FilmPlaybackPage, JsonElement, bool, CancellationToken, Task<FilmPlaybackOpen>>? prepareOpen;
    private readonly IFilmNativeMedia media;
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private readonly SemaphoreSlim control = new(1, 1);
    private readonly SemaphoreSlim nativeWork = new(1, 1);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly HashSet<Task> operations = [];
    private RuntimeConnection? connection;
    private FilmPlaybackClient? rpc;
    private FilmPlaybackSnapshot? snapshot;
    private FilmPlaybackPage? page;
    private (FilmPlaybackPage Page, JsonElement Parameters)? openRequest;
    private IReadOnlyList<JsonElement> searchScope = [];
    private FilmPlaybackTarget? searchSelection;
    private (FilmPlaybackPage Page, JsonElement Parameters, long Generation)? searchUpdate;
    private Task? searchUpdating;
    private CancellationTokenSource? observation;
    private CancellationTokenRegistration runtimeRevoke;
    private Task? observing, closing, disposal;
    private Input? pending, current;
    private CancellationTokenSource? pendingPublication;
    private string processingId = "", revokedPublicationId = "", continuingFrom = "";
    private long ownerGeneration, intentGeneration;
    private bool closed, progressDirty, presenting;
    private int playbackCommands;
    private TaskCompletionSource? playbackAdmission;
    private Window? retainedWindow;
    private Task? progress;
    private Exception? releaseFailure;
    private double rate = 1;
    private string? aspect;

    internal FilmPlaybackSession(Func<CancellationToken, Task<RuntimeConnection>> capture, IFilmNativeMedia media,
        Func<FilmPlaybackPage, JsonElement, bool, CancellationToken, Task<FilmPlaybackOpen>>? prepareOpen = null)
    {
        this.capture = capture;
        this.prepareOpen = prepareOpen;
        this.media = media;
        dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        media.PropertyChanged += MediaChanged;
        media.Failed += MediaFailed;
        media.Ended += MediaEnded;
    }

    internal event EventHandler<string>? OperationFailed;
    internal ContentControl? MediaView => media.View;
    internal bool IsPlaying => HasOutput && !MediaPaused;
    internal bool IsBuffering => HasOutput && media.Buffering;
    internal bool HasOutput => current is { Retiring: false, TerminalReported: false } input &&
        HoldsNativeScope(input) && media.HasOutput;
    internal bool OwnsNativeMedia => current is { Retiring: false, TerminalReported: false } input &&
        HoldsNativeScope(input);
    private bool HoldsNativeScope(Input input) => input.NativeScope is { } scope && media.Scope == scope;
    private bool MediaPaused => media.Paused;
    internal double Position => HasOutput ? media.Position : 0;
    internal double Duration => HasOutput ? media.Duration : 0;
    internal double Rate => rate;
    internal string? Aspect => aspect;
    internal int Volume
    {
        get => media.Volume;
        set
        {
            if (!HasOutput) return;
            media.Volume = value;
        }
    }
    internal bool CanRetainMedia => retainedWindow?.IsVisible == true && HasOutput;
    internal bool HasSession => snapshot is not null && rpc is not null && connection is { Revoked.IsCancellationRequested: false };
    internal string? MediaIdentity => current?.Receipt.AttemptId;
    internal string MediaTitle => current?.Receipt.Title ?? "";
    internal event EventHandler? MediaStateChanged;
    private void NotifyMedia()
    {
        page?.NotifyMedia();
        MediaStateChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void RequirePage(FilmPlaybackPage value)
    {
        if (!IsCurrentPage(value)) throw new FilmPlaybackException("STALE_SESSION");
    }
    internal bool IsCurrentPage(FilmPlaybackPage value) => ReferenceEquals(page, value) && !closed && closing is null;
    internal void RetainOwner(FilmPlaybackPage value, Window? owner)
    {
        if (!ReferenceEquals(page, value)) return;
        if (owner is not null) RequirePage(value);
        if (owner is not null && (!owner.IsVisible || !HasOutput))
            throw new FilmPlaybackException("INVALID_REQUEST");
        retainedWindow = owner;
    }
    internal Task EndRetainedOwnerAsync(FilmPlaybackPage value) =>
        ReferenceEquals(page, value) && !presenting && !CanRetainMedia ? CloseSessionAsync() : Task.CompletedTask;

    internal Task OpenAsync(FilmPlaybackPage targetPage, object openParameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closed) throw new FilmPlaybackException("CLOSED");
        var ownedParameters = FilmJson.Encode(openParameters);
        openRequest = (targetPage, ownedParameters);
        var retirement = CloseSessionAsync();
        long generation = ++ownerGeneration;
        return Track(OpenCoreAsync(targetPage, ownedParameters, token, retirement, generation, reconnect: false));
    }

    private async Task OpenCoreAsync(FilmPlaybackPage targetPage, JsonElement openParameters, CancellationToken token,
        Task retirement, long generation, bool reconnect)
    {
        await retirement;
        token.ThrowIfCancellationRequested();
        await lifecycle.WaitAsync(token);
        try
        {
            if (generation != ownerGeneration || closed) return;
            page = targetPage; presenting = true;
            FilmPlaybackOpen opening;
            if (prepareOpen is { } prepare)
                opening = await prepare(targetPage, openParameters, reconnect, token);
            else
            {
                var borrowed = await capture(token);
                opening = new(borrowed, FilmPlaybackOpen.ParametersFor(targetPage, openParameters, borrowed,
                    reconnect ? [] : FilmJson.Array(openParameters, "sources")));
            }
            var captured = opening.Connection;
            captured.Revoked.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (generation != ownerGeneration || closed) return;
            // Freeze the admitted source range, not the App's future global list.
            // Keep original rows/pages/cache provenance separate from this current scope.
            if (!reconnect && FilmJson.Member(openParameters, "sources").ValueKind != JsonValueKind.Array)
            {
                var remembered = JsonNode.Parse(openParameters.GetRawText())!.AsObject();
                remembered["sources"] = JsonSerializer.SerializeToNode(
                    FilmPlaybackOpen.OriginalSources(openParameters, FilmJson.Array(opening.Parameters, "sources")),
                    FilmJson.Options);
                openRequest = (targetPage, JsonSerializer.SerializeToElement(remembered, FilmJson.Options));
            }
            var client = new FilmPlaybackClient(captured.Client);
            // Once session.open is admitted, obtain its receipt even if the view leaves.
            // A cancelled observer never strands a Node session whose id was hidden by cancellation.
            var opened = new FilmPlaybackSnapshot(await client.CallAsync("session.open", opening.Parameters, captured.Revoked));
            if (generation != ownerGeneration || token.IsCancellationRequested || closed)
            {
                await client.ReleaseAsync(opened.SessionId);
                token.ThrowIfCancellationRequested();
                return;
            }
            connection = captured; rpc = client;
            searchScope = FilmJson.Array(opening.Parameters, "sources");
            searchSelection = FilmJson.Target(opening.Parameters.GetProperty("target"));
            targetPage.FreezeSearchScope(FilmPlaybackOpen.OriginalSources(openParameters, searchScope));
            runtimeRevoke = captured.Revoked.Register(() =>
            {
                if (!dispatcher.HasShutdownStarted)
                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation == ownerGeneration)
                        {
                            page?.SetConnectionError("CLOSED");
                            RevokeMedia();
                        }
                    }));
            });
            Apply(opened);
            StartObserving();
            targetPage.PublishSearch();
        }
        catch (Exception error)
        {
            if (generation == ownerGeneration) targetPage.SetConnectionError(Code(error));
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private void StartObserving()
    {
        if (rpc is null || snapshot is null || connection is null || observing is { IsCompleted: false }) return;
        observation?.Dispose();
        observation = CancellationTokenSource.CreateLinkedTokenSource(connection.Revoked);
        observing = ObserveAsync(rpc, snapshot.SessionId, snapshot.Sequence, ownerGeneration, observation.Token);
    }

    private async Task ObserveAsync(FilmPlaybackClient client, Guid id, long after, long generation, CancellationToken token)
    {
        try
        {
            await foreach (var next in client.ObserveAsync(id, after, token))
            {
                if (token.IsCancellationRequested || generation != ownerGeneration) break;
                Apply(next);
            }
            if (!token.IsCancellationRequested && generation == ownerGeneration)
                page?.SetConnectionError("DISCONNECTED");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == ownerGeneration && connection?.Revoked.IsCancellationRequested == true) RevokeMedia();
        }
        catch (Exception error)
        {
            if (generation == ownerGeneration)
            {
                page?.SetConnectionError(Code(error));
                if (connection?.Revoked.IsCancellationRequested == true) RevokeMedia();
            }
        }
    }

    internal async Task ReconnectAsync()
    {
        if (!HasSession)
        {
            if (openRequest is not { } request || closed) throw new FilmPlaybackException("STALE_SESSION");
            request = (request.Page, FilmPlaybackOpen.RememberResults(request.Parameters, snapshot));
            openRequest = request;
            var retirement = CloseSessionAsync();
            long reopeningGeneration = ++ownerGeneration;
            await Track(OpenCoreAsync(request.Page, request.Parameters, CancellationToken.None, retirement, reopeningGeneration,
                reconnect: true));
            return;
        }
        var client = rpc ?? throw new FilmPlaybackException("STALE_SESSION");
        var state = snapshot ?? throw new FilmPlaybackException("STALE_SESSION");
        long generation = ownerGeneration;
        var oldObservation = observation; var oldObserving = observing;
        if (oldObservation is not null) await oldObservation.CancelAsync();
        if (oldObserving is not null) await oldObserving;
        var latest = new FilmPlaybackSnapshot(await client.CallAsync("session.snapshot", new { sessionId = state.SessionId.ToString() }));
        if (generation != ownerGeneration || closing is not null) throw new FilmPlaybackException("STALE_SESSION");
        Apply(latest);
        if (current is { Started: false, Retiring: false, TerminalReported: false } input && HasOutput &&
            latest.AttemptId == input.Receipt.AttemptId && !FilmJson.Bool(latest.Attempt, "prepareOnly"))
        {
            var confirmed = await client.CommandAsync(state.SessionId, "media.started", new { attemptId = input.Receipt.AttemptId });
            input.Started = true;
            if (generation != ownerGeneration) throw new FilmPlaybackException("STALE_SESSION");
            Apply(new(confirmed)); QueueProgress();
        }
        StartObserving();
    }
    internal Task ResumePresentationAsync()
    {
        presenting = true;
        if (rpc is null || snapshot is null) return Task.CompletedTask;
        return observing is { IsCompleted: false } ? Task.CompletedTask : ReconnectAsync();
    }

    internal Task CommandAsync(string action, object? parameters = null)
    {
        if (rpc is null || snapshot is null || closed || closing is not null)
            return Task.FromException(new FilmPlaybackException("STALE_SESSION"));
        // play is idempotent for the same admitted pending receipt. Cancelling
        // its native prepare would release the very address Node is reusing.
        if (action == "play" && pendingPublication is { IsCancellationRequested: false } &&
            pending is not { Retiring: true } && snapshot.AttemptId == processingId &&
            !FilmJson.Bool(snapshot.Attempt, "prepareOnly"))
            return Task.CompletedTask;
        long ticket = intentGeneration;
        if (ChangesPlayback(action))
        {
            if (playbackCommands++ == 0)
                playbackAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Blocking can be rejected without any business mutation. Keep the
            // admitted candidate alive; control serializes its commit behind
            // the durable RPC, and only an accepted snapshot may retire it.
            if (action != "block")
            {
                continuingFrom = action is "pan" or "quality" or "switch" ? current?.Receipt.AttemptId ?? "" : "";
                ticket = ++intentGeneration;
                if (pendingPublication is not null) revokedPublicationId = processingId;
                pendingPublication?.Cancel();
            }
        }
        return Track(CommandCoreAsync(action, parameters, ticket));
    }

    internal void UpdateSearch(FilmPlaybackPage targetPage, FilmPlaybackTarget carrier, long revision)
    {
        if (!IsCurrentPage(targetPage) || rpc is null || snapshot is null || connection is null ||
            searchSelection is null || connection.Revoked.IsCancellationRequested) return;
        if (FilmJson.Text(snapshot.Raw, "stage") == "stopped" ||
            snapshot.Target.Kind == "group" && snapshot.Target.Members!.Value.GetArrayLength() == 0) return;
        var parameters = FilmPlaybackOpen.SearchUpdateFor(targetPage, searchSelection, carrier, revision, connection, searchScope);
        searchUpdate = (targetPage, parameters, ownerGeneration);
        if (searchUpdating is not { IsCompleted: false }) searchUpdating = Track(PublishSearchAsync());
    }

    private async Task PublishSearchAsync()
    {
        // One pending update coalesces a burst of source/detail publications.
        await Task.Yield();
        try
        {
            while (searchUpdate is { } update)
            {
                searchUpdate = null;
                await control.WaitAsync();
                try
                {
                    if (update.Generation != ownerGeneration || !IsCurrentPage(update.Page) ||
                        rpc is not { } client || snapshot is not { } state || connection is null) continue;
                    var value = await client.CommandAsync(state.SessionId, "search.update", update.Parameters, connection.Revoked);
                    if (update.Generation != ownerGeneration || !IsCurrentPage(update.Page)) continue;
                    if (openRequest is { } original && ReferenceEquals(original.Page, update.Page))
                    {
                        var remembered = JsonNode.Parse(original.Parameters.GetRawText())!.AsObject();
                        var request = update.Page.CaptureRequest;
                        remembered["sources"] = JsonSerializer.SerializeToNode(request.Sources, FilmJson.Options);
                        remembered["results"] = request.Results is { } results ? JsonNode.Parse(results.GetRawText()) : null;
                        remembered["search"] = request.Search is { } search ? JsonNode.Parse(search.GetRawText()) : null;
                        remembered["cacheEntries"] = request.CacheEntries is { } entries ? JsonNode.Parse(entries.GetRawText()) : null;
                        openRequest = (update.Page, JsonSerializer.SerializeToElement(remembered, FilmJson.Options));
                    }
                    Apply(new(value));
                }
                catch (Exception) when (update.Generation != ownerGeneration || !IsCurrentPage(update.Page))
                {
                    // A retired page owns neither a late snapshot nor its error.
                }
                finally { control.Release(); }
            }
        }
        finally
        {
            searchUpdating = null;
            // Preserve a newer publication already coalesced while the failed
            // request was awaiting its receipt. The failed request is not retried.
            if (searchUpdate is not null && !closed) searchUpdating = Track(PublishSearchAsync());
        }
    }

    private async Task CommandCoreAsync(string action, object? parameters, long ticket)
    {
        var client = rpc ?? throw new FilmPlaybackException("STALE_SESSION");
        var id = snapshot?.SessionId ?? throw new FilmPlaybackException("STALE_SESSION");
        long generation = ownerGeneration;
        Exception? stopFailure = null;
        await control.WaitAsync();
        try
        {
            if (generation != ownerGeneration || closing is not null) throw new FilmPlaybackException("STALE_SESSION");
            if (action == "stop")
            {
                try { await FlushProgressAsync(); }
                catch (Exception error) { stopFailure = error; }
            }
            try
            {
                var value = await client.CommandAsync(id, action, parameters, connection!.Revoked);
                if (generation == ownerGeneration)
                {
                    if (action == "block" && ticket == intentGeneration)
                        continuingFrom = current?.Receipt.AttemptId ?? "";
                    Apply(new(value), scheduleMedia: !ChangesPlayback(action) && ticket == intentGeneration);
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
                if (generation == ownerGeneration && snapshot is { } latest) Apply(latest);
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
            nativeWork.Release();
            if (stopFailure is not null) throw stopFailure;
        }
    }

    private static bool ChangesPlayback(string action) => action is "play" or "prepare" or "stop" or
        "select.episode" or "select.candidate" or "next" or "previous" or "pan" or "quality" or "switch" or "block";

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

    private void Apply(FilmPlaybackSnapshot next, bool scheduleMedia = true)
    {
        if (snapshot is { } previous &&
            (next.SessionId != previous.SessionId || next.Sequence < previous.Sequence)) return;
        snapshot = next;
        page?.Apply(next);
        NotifyMedia();
        string id = next.AttemptId;
        // Removing a pending attempt revokes publication, not a healthy accepted donor.
        if (id != processingId && processingId.Length > 0 &&
            !(id.Length == 0 && pending is { Retiring: true }))
            pendingPublication?.Cancel();
        if (current is { Retiring: false, TerminalReported: false } accepted &&
            id != accepted.Receipt.AttemptId && next.CurrentId != accepted.Receipt.AttemptId)
        {
            // The authoritative owner explicitly dropped the accepted resource (scope/stop).
            RevokeMedia(cancelPending: false);
        }
        if (!scheduleMedia || playbackCommands != 0 || id.Length == 0 || FilmJson.Bool(next.Attempt, "prepareOnly") ||
            id == revokedPublicationId || id == processingId || id == current?.Receipt.AttemptId) return;
        processingId = id;
        pendingPublication = CancellationTokenSource.CreateLinkedTokenSource(connection!.Revoked);
        Track(ConsumeAsync(next, ownerGeneration, intentGeneration, pendingPublication));
    }

    private async Task ConsumeAsync(FilmPlaybackSnapshot candidateSnapshot, long generation, long ticket,
        CancellationTokenSource publication)
    {
        var client = rpc!;
        string id = candidateSnapshot.AttemptId;
        Input? input = null, donor = null;
        RuntimeMediaPlaybackIntent? intent = null;
        Task? started = null;
        bool entered = false, prepared = false, committed = false;
        try
        {
            await nativeWork.WaitAsync(publication.Token); entered = true;
            publication.Token.ThrowIfCancellationRequested();
            var receipt = FilmMediaReceipt.Read(await client.CallAsync("session.attempt",
                new { sessionId = candidateSnapshot.SessionId.ToString(), attemptId = id }, publication.Token));
            if (receipt.AttemptId != id) throw new FilmPlaybackException("INVALID_RESPONSE");
            var candidate = FilmCandidateItem.Read(FilmJson.Member(candidateSnapshot.Attempt, "candidate"));
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(connection!.Revoked, publication.Token);
            input = new(receipt, candidate, candidateSnapshot.Target,
                FilmJson.Member(FilmJson.Member(candidateSnapshot.Attempt, "candidate"), "source").Clone(),
                lifetime, publication, connection.Revoked);
            pending = input;
            publication.Token.ThrowIfCancellationRequested();
            if (receipt.MediaOrigin is not null)
            {
                // Keep the receipt tied to this runtime's release operation.
                input.Authority = new(connection.Revoked,
                    resourceId => ReleaseResourceAsync(input, client, candidateSnapshot.SessionId, resourceId));
                input.Lease = input.Authority.CreateLease(receipt.ResourceId, receipt.Address, lifetime.Token);
            }
            bool sameEpisode = HasOutput && current is { } acceptedDonor && SameEpisode(acceptedDonor, candidateSnapshot.Target, candidate);
            intent = new RuntimeMediaPlaybackIntent("film", id, receipt.Title,
                receipt.Position, receipt.Paused, sameEpisode ? current!.NativeScope : null);
            await media.PrepareAsync(intent, receipt, input.Lease, lifetime.Token);
            prepared = true;
            while (!committed)
            {
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
                        started = ConfirmStartedAsync(input, client, candidateSnapshot.SessionId, generation);
                    }, publication.Token);
                    // Native alignment may yield to a newer block. Keep the same
                    // prepared receipt and release control before awaiting that RPC.
                    if (!admitted) continue;
                    input.NativeScope = media.Scope;
                    if (!media.HasOutput || input.NativeScope?.Identity != id)
                        throw new FilmPlaybackException(PlaybackError.OpenFailed);
                    try
                    {
                        await media.RateAsync(input.NativeScope, rate);
                        await media.AspectAsync(input.NativeScope, aspect);
                    }
                    catch (Exception error) { OperationFailed?.Invoke(this, Code(error)); }
                    await started!;
                    if (donor is not null) await ReleaseInputAsync(donor, client, candidateSnapshot.SessionId);
                    NotifyMedia();
                    QueueProgress();
                }
                finally { control.Release(); }
            }
        }
        catch (OperationCanceledException) when (!committed && (publication.IsCancellationRequested ||
            input?.Lifetime.IsCancellationRequested == true || generation != ownerGeneration)) { }
        catch (Exception error)
        {
            if (committed)
            {
                if (started is not null)
                    try { await started; } catch { /* Original sanitized failure is reported below. */ }
                // Publication succeeded but drain/report failed. Do not falsely label the new media
                // a failed candidate or start another chain over an unknown native release outcome.
                page?.SetConnectionError(Code(error));
                OperationFailed?.Invoke(this, "播放状态提交失败");
            }
            else if (!publication.IsCancellationRequested && generation == ownerGeneration && ticket == intentGeneration &&
                (snapshot?.AttemptId == id || input?.Retiring == true && snapshot?.AttemptId.Length == 0))
            {
                try
                {
                    var failed = await client.CommandAsync(candidateSnapshot.SessionId, "media.failed", new { attemptId = id });
                    if (generation == ownerGeneration) Apply(new(failed));
                }
                catch (Exception reportError) { page?.SetConnectionError(Code(reportError)); }
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
                        try { await media.DiscardAsync(intent!); } catch (Exception error) { releaseError = error; }
                    if (input is not null)
                        try { await ReleaseInputAsync(input, client, candidateSnapshot.SessionId); }
                        catch (Exception error) { releaseError ??= error; }
                    if (ReferenceEquals(pending, input)) pending = null;
                    if (input is null) publication.Dispose();
                }
                else if (donor is not null)
                    try { await ReleaseInputAsync(donor, client, candidateSnapshot.SessionId); }
                    catch (Exception error) { releaseError = error; }
            }
            finally
            {
                if (processingId == id) processingId = "";
                if (releaseError is not null) releaseFailure ??= releaseError;
                if (entered) nativeWork.Release();
            }
            if (releaseError is not null) throw releaseError;
            // Only a different pending attempt needs rescheduling. Replaying a
            // settled snapshot would clear a local post-commit failure.
            if (generation == ownerGeneration && snapshot is { } latest &&
                latest.AttemptId.Length > 0 && latest.AttemptId != id) Apply(latest);
        }
    }

    private async Task ConfirmStartedAsync(Input input, FilmPlaybackClient client, Guid sessionId, long generation)
    {
        var response = await client.CommandAsync(sessionId, "media.started", new { attemptId = input.Receipt.AttemptId });
        input.Started = true;
        if (generation == ownerGeneration) Apply(new(response));
    }

    private bool SameEpisode(Input input, FilmPlaybackTarget next, FilmCandidateItem candidate)
    {
        var previous = input.Target;
        if (previous.Id != next.Id || previous.Kind != next.Kind) return false;
        if (next.Kind == "movie") return true;
        if (previous.GlobalEpisode is { } beforeGlobal && next.GlobalEpisode is { } afterGlobal)
            return beforeGlobal == afterGlobal;
        if (next.Kind == "tv") return previous.Season is not null && previous.Episode is not null &&
            previous.Season == next.Season && previous.Episode == next.Episode;
        // Raw play references are intentionally absent from public snapshots.
        // Exact candidate equality or an explicit same-target replacement action
        // supplies continuation; missing coordinates never compare equal by accident.
        return input.Candidate.Id == candidate.Id || continuingFrom == input.Receipt.AttemptId;
    }

    private static async ValueTask ReleaseResourceAsync(Input input, FilmPlaybackClient client, Guid sessionId, string resourceId)
    {
        // Native retirement may precede its terminal event. Retain this bounded
        // input identity when the release snapshot hides the receipt.
        input.Retiring = true;
        // A revoked runtime epoch cannot retain a usable resource. Still drain
        // native output locally; never send releases to a replacement epoch.
        if (!input.RuntimeRevoked.IsCancellationRequested)
        {
            try { await client.ReleaseMediaAsync(sessionId, input.Receipt.AttemptId, resourceId); }
            catch (Exception error) when (input.RuntimeRevoked.IsCancellationRequested && Code(error) is "CLOSED" or "CANCELLED") { }
        }
    }

    private Task ReleaseInputAsync(Input input, FilmPlaybackClient client, Guid sessionId)
    {
        lock (input.ReleaseGate) return input.Release ??= ReleaseInputCoreAsync(input, client, sessionId);
    }

    private async Task ReleaseInputCoreAsync(Input input, FilmPlaybackClient client, Guid sessionId)
    {
        input.Retiring = true;
        Exception? failure = null;
        try
        {
            if (input.Authority is not null) await input.Authority.DisposeAsync();
        }
        catch (Exception error) { failure ??= error; }
        try
        {
            // A rejected CreateLease has no callback in its empty authority.
            if (input.Lease is null) await ReleaseResourceAsync(input, client, sessionId, input.Receipt.ResourceId);
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            try { await input.Lifetime.CancelAsync(); } catch (Exception error) { failure ??= error; }
            input.Lifetime.Dispose(); input.Publication.Dispose();
        }
        if (failure is not null) { releaseFailure ??= failure; throw failure; }
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
        try { await input.Lifetime.CancelAsync(); } catch (Exception error) { failure = error; }
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
                if (failure is not null) throw failure;
                if (ReferenceEquals(current, input)) current = null;
                NotifyMedia();
            }
            finally { control.Release(); }
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
                else input.Publication.Cancel();
            }
        }
        // The snapshot already owns an attempt while its receipt RPC is in flight.
        // Formal source/pan account epochs are invalidated by their Node owner.
        bool Available(JsonElement expected) => FilmJson.Text(expected, "engine") != "cat" ||
            available.Any(value => FilmJson.Text(value, "engine") == FilmJson.Text(expected, "engine") &&
                FilmJson.Text(value, "installationId") == FilmJson.Text(expected, "installationId") &&
                FilmJson.Text(value, "siteId") == FilmJson.Text(expected, "siteId") &&
                FilmJson.Text(value, "generation") == FilmJson.Text(expected, "generation") &&
                FilmJson.Text(value, "revision") == FilmJson.Text(expected, "revision"));
    }

    internal Task PauseAsync() => SetPausedAsync(!MediaPaused);
    internal Task SetPausedAsync(bool paused) => Track(ControlMediaAsync(async input =>
    {
        await media.PauseAsync(input.NativeScope!, paused);
        await FlushProgressAsync();
        NotifyMedia();
    }));
    internal Task SeekAsync(double position)
    {
        if (!double.IsFinite(position) || position < 0 || Duration > 0 && position > Duration)
            throw new FilmPlaybackException("INVALID_REQUEST");
        return Track(ControlMediaAsync(async input =>
        {
            await media.SeekAsync(input.NativeScope!, position);
            await FlushProgressAsync();
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
        if (value is not (null or "16:9" or "4:3" or "1:1")) throw new FilmPlaybackException("INVALID_REQUEST");
        return Track(ControlMediaAsync(async input =>
        {
            await media.AspectAsync(input.NativeScope!, value);
            aspect = value; NotifyMedia();
        }));
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

    private void MediaChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (current is not { } input) return;
        if (media.Scope is { } scope && scope.Identity == input.Receipt.AttemptId) input.NativeScope = scope;
        if (args.PropertyName is "OwnerId" or "Identity" or "ObservedVideoOutput" or "HasMedia")
        {
            // Native publication changes several properties before its atomic
            // committed callback. Recheck this captured input after that turn,
            // not in the middle of a successful Film-to-Film handoff.
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(current, input) || HoldsNativeScope(input) ||
                    input.TerminalReported || input.Retiring || closing is not null) return;
                Track(CloseSessionAsync());
            }));
        }
        NotifyMedia();
        if (args.PropertyName is "PositionSeconds" or "DurationSeconds" or "PauseRequested") QueueProgress();
    }
    private void QueueProgress()
    {
        if (!HasOutput || current?.Started != true || rpc is null || snapshot is null) return;
        progressDirty = true;
        if (progress is null || progress.IsCompleted) progress = Track(ProgressCoreAsync());
    }
    private Task FlushProgressAsync()
    {
        progressDirty = true;
        return progress is { IsCompleted: false } ? progress : progress = Track(ProgressCoreAsync());
    }
    private async Task ProgressCoreAsync()
    {
        while (progressDirty && current is { Started: true } input && snapshot is { } state && rpc is { } client && HasOutput)
        {
            progressDirty = false;
            long generation = ownerGeneration;
            try
            {
                await client.CommandAsync(state.SessionId, "media.progress", new
                {
                    attemptId = input.Receipt.AttemptId, position = Position,
                    duration = Duration, paused = MediaPaused
                });
            }
            catch (Exception error)
            {
                if (Code(error) == "STALE_SESSION" && (generation != ownerGeneration || !ReferenceEquals(current, input)))
                    return;
                throw;
            }
        }
    }
    private void MediaFailed(object? sender, EventArgs args) => Track(ReportTerminalAsync("media.failed"));
    private void MediaEnded(object? sender, EventArgs args) => Track(ReportTerminalAsync("media.ended"));
    private async Task ReportTerminalAsync(string action)
    {
        if (current is not { } input || snapshot is not { } state || rpc is not { } client ||
            media.Identity != input.Receipt.AttemptId || input.TerminalReported ||
            input.Lifetime.IsCancellationRequested) return;
        input.TerminalReported = true;
        long generation = ownerGeneration;
        double position = media.Position;
        double duration = media.Duration;
        bool paused = media.Paused;
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
                    });
                }
                catch (Exception error) { progressFailure ??= error; }
            }
            // Loss before business admission is a failed attempt, not completed viewing.
            var value = await client.CommandAsync(state.SessionId, input.Started ? action : "media.failed",
                new { attemptId = input.Receipt.AttemptId });
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
            try
            {
                await ReleaseInputAsync(input, client, state.SessionId);
            }
            finally
            {
                if (ReferenceEquals(current, input)) current = null;
                NotifyMedia();
            }
        }
    }

    internal Task LeaveAsync(bool retainedOwner)
    {
        presenting = false;
        if (retainedOwner && CanRetainMedia && HasOutput) return RetainAsync();
        return CloseSessionAsync();
    }
    private async Task RetainAsync()
    {
        // A real retained owner still needs snapshots for automatic next/failure recovery.
        // The stable owner, not UI visibility, owns this bounded subscription.
        if (snapshot is { } state && rpc is { } client) await client.ReleaseAsync(state.SessionId, keepMedia: true);
    }

    internal Task CloseSessionAsync()
    {
        if (closing is not null) return closing;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closing = completion.Task;
        ownerGeneration++; intentGeneration++;
        searchUpdate = null; searchScope = []; searchSelection = null;
        pendingPublication?.Cancel();
        NotifyMedia();
        _ = FinishAsync();
        return completion.Task;

        async Task FinishAsync()
        {
            try { await CloseCoreAsync(); closing = null; completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
    private async Task CloseCoreAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            Exception? failure = null;
            async Task Drain(Task work)
            {
                try { await work; } catch (Exception error) { failure ??= error; }
            }
            if (observation is not null) await Drain(observation.CancelAsync());
            if (observing is not null) await Drain(observing);
            await nativeWork.WaitAsync();
            try
            {
                failure ??= releaseFailure;
                await control.WaitAsync();
                try
                {
                    bool revoked = connection?.Revoked.IsCancellationRequested == true;
                    if (!revoked) await Drain(FlushProgressAsync());
                    if (current is { } retiring) retiring.Retiring = true;
                    if (current?.NativeScope is { } scope && media.Scope == scope) await Drain(media.StopAsync(scope));
                    if (snapshot is { } state && rpc is { } client)
                    {
                        if (pending is { } unaccepted) await Drain(ReleaseInputAsync(unaccepted, client, state.SessionId));
                        if (current is { } accepted) await Drain(ReleaseInputAsync(accepted, client, state.SessionId));
                        if (!revoked) await Drain(client.ReleaseAsync(state.SessionId));
                    }
                    pending = null; current = null; processingId = ""; revokedPublicationId = ""; continuingFrom = "";
                    snapshot = null; rpc = null; connection = null; retainedWindow = null; presenting = false;
                    rate = 1; aspect = null;
                    runtimeRevoke.Dispose();
                    pendingPublication = null;
                    observation?.Dispose(); observation = null; observing = null;
                    NotifyMedia();
                    if (failure is not null) throw failure;
                }
                finally { control.Release(); }
            }
            finally { nativeWork.Release(); }
        }
        finally { lifecycle.Release(); }
    }

    private Task Track(Task task)
    {
        operations.Add(task);
        _ = SettleAsync(task);
        return task;
    }
    private async Task SettleAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception error) { OperationFailed?.Invoke(this, Code(error)); }
        finally { operations.Remove(task); }
    }
    private static string Code(Exception error) => error switch
    {
        FilmPlaybackException film => film.Code,
        PlaybackException native => native.Code,
        MeowFilm.Core.Runtime.BridgeException bridge => bridge.Code,
        OperationCanceledException => "CANCELLED",
        _ => "FAILED"
    };

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        closed = true;
        Exception? failure = null;
        try { await CloseSessionAsync(); } catch (Exception error) { failure = error; }
        try
        {
            while (operations.Count > 0)
            {
                var draining = operations.ToArray();
                try { await Task.WhenAll(draining); }
                catch (Exception error) { failure ??= error; }
                finally { operations.ExceptWith(draining.Where(task => task.IsCompleted)); }
            }
        }
        finally
        {
            media.PropertyChanged -= MediaChanged; media.Failed -= MediaFailed; media.Ended -= MediaEnded;
        }
        if (failure is not null) throw failure;
    }
}
