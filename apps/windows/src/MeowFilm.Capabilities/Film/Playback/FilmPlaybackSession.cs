using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;
using MeowFilm.Threading;
using Microsoft.UI.Xaml;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities;

/// <summary>
/// Stable native film owner. Node is the only matching/cache/library owner; this class
/// observes snapshots, fences native publication and reports exact media occurrences.
/// </summary>
internal sealed partial class FilmPlaybackSession : IAsyncDisposable
{
    private readonly record struct PageMediaState(FilmPlaybackPage? Page, FrameworkElement? View, string? Identity,
        bool HasOutput, bool OwnsNativeMedia, bool IsPlaying, bool Buffering,
        double Position, double Duration, double Rate, string? Aspect, int Volume);
    private readonly record struct PublishedMediaState(string? Identity, string Title,
        bool HasOutput, bool IsPlaying, double Position, double Duration);
    private sealed class Input(FilmMediaReceipt receipt, FilmCandidateItem candidate, FilmPlaybackTarget target, string contentKind, JsonElement source,
        CancellationTokenSource lifetime, CancellationTokenSource publication, CancellationToken runtimeRevoked)
    {
        internal readonly FilmMediaReceipt Receipt = receipt;
        internal readonly FilmCandidateItem Candidate = candidate;
        internal readonly FilmPlaybackTarget Target = target;
        internal readonly string ContentKind = contentKind;
        internal readonly JsonElement Source = source;
        internal readonly CancellationTokenSource Lifetime = lifetime;
        internal readonly CancellationTokenSource Publication = publication;
        internal readonly CancellationToken RuntimeRevoked = runtimeRevoked;
        internal RuntimeMediaAuthority? Authority;
        internal INativeMediaResourceLease? Lease;
        internal NativePlaybackScope? NativeScope;
        internal FilmPlaybackClient? ReleaseClient;
        internal Guid ReleaseSessionId;
        internal readonly object ReleaseGate = new();
        internal Task? Release, Stop;
        internal volatile bool Retiring;
        internal bool Started, TerminalReported, AuthorityDisposed, ResourceReleased, TokensDisposed;
        internal bool IsDrained => (Authority is null || AuthorityDisposed) &&
            (ResourceReleased || RuntimeRevoked.IsCancellationRequested);
    }

    private readonly Func<FilmPlaybackPage, JsonElement, bool, CancellationToken, Task<FilmPlaybackOpen>> prepareOpen;
    private readonly FilmPlayback media;
    private readonly OwnerDispatcher dispatcher;
    private readonly SemaphoreSlim control = new(1, 1);
    private readonly SemaphoreSlim nativeWork = new(1, 1);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly HashSet<Task> operations = [];
    private readonly object undrainedGate = new();
    private readonly List<Input> undrainedInputs = [];
    private readonly List<RuntimeMediaPlaybackIntent> undrainedDiscards = [];
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
    private CancellationTokenSource? opening;
    private CancellationTokenRegistration runtimeRevoke;
    private Task? observing, closing, disposal;
    private Input? pending, current;
    private CancellationTokenSource? pendingPublication;
    private string processingId = "", revokedPublicationId = "", blockedAttemptId = "", continuingFrom = "";
    private long ownerGeneration, intentGeneration;
    private bool closed, progressDirty, presenting;
    private int playbackCommands;
    private TaskCompletionSource? playbackAdmission;
    private MeowFilm.Desktop.DesktopWindow? retainedWindow;
    private Task? progress;
    private Exception? releaseFailure;
    private bool executionErrorFromRelease;
    private PageMediaState? pageMediaState;
    private PublishedMediaState? publishedMediaState;
    internal string ExecutionError { get; private set; } = "";
    private double rate = 1;
    private string? aspect;
    private TaskCompletionSource browseChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FilmPlaybackSession(FilmPlayback media,
        Func<FilmPlaybackPage, JsonElement, bool, CancellationToken, Task<FilmPlaybackOpen>> prepareOpen)
    {
        this.media = media;
        this.prepareOpen = prepareOpen;
        dispatcher = OwnerDispatcher.Current;
        media.PropertyChanged += MediaChanged;
        media.Failed += MediaFailed;
        media.Ended += MediaEnded;
    }

    internal event EventHandler<string>? OperationFailed;
    internal FrameworkElement? MediaView => media.VideoView;
    internal bool IsPlaying => HasOutput && !MediaPaused;
    internal bool IsBuffering => HasOutput && media.IsBuffering;
    internal bool HasOutput => current is { Retiring: false, TerminalReported: false } input &&
        HoldsNativeScope(input) && media.HasOutput;
    internal bool OwnsNativeMedia => current is { Retiring: false, TerminalReported: false } input &&
        HoldsNativeScope(input);
    private bool HoldsNativeScope(Input input) => input.NativeScope is { } scope && media.Scope == scope;
    private bool MediaPaused => media.IsPaused;
    internal double Position => HasOutput ? media.PositionSeconds : 0;
    internal double Duration => HasOutput ? media.DurationSeconds : 0;
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

    private void WakeBrowsing()
    {
        var previous = browseChanged;
        browseChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private void NotifyMedia()
    {
        var nextPage = new PageMediaState(page, MediaView, MediaIdentity, page?.HasOutput == true,
            page?.OwnsNativeMedia == true, page?.IsPlaying == true, page?.PlayerBuffering == true,
            page?.Position ?? 0, page?.Duration ?? 0, Rate, Aspect, Volume);
        if (nextPage != pageMediaState)
        {
            var previous = pageMediaState;
            pageMediaState = nextPage;
            if (previous is not { } old || !ReferenceEquals(old.Page, nextPage.Page)) page?.NotifyMedia();
            else
            {
                if (!ReferenceEquals(old.View, nextPage.View)) page?.NotifyMedia(nameof(FilmPlaybackPage.MediaView));
                if (old.Identity != nextPage.Identity) page?.NotifyMedia(nameof(FilmPlaybackPage.MediaIdentity));
                if (old.HasOutput != nextPage.HasOutput)
                    page?.NotifyMedia(nameof(FilmPlaybackPage.HasOutput), nameof(FilmPlaybackPage.NoOutput), nameof(FilmPlaybackPage.CanSeek));
                if (old.OwnsNativeMedia != nextPage.OwnsNativeMedia)
                    page?.NotifyMedia(nameof(FilmPlaybackPage.OwnsNativeMedia));
                if (old.IsPlaying != nextPage.IsPlaying)
                    page?.NotifyMedia(nameof(FilmPlaybackPage.IsPlaying));
                if (old.Position != nextPage.Position)
                    page?.NotifyMedia(nameof(FilmPlaybackPage.Position), nameof(FilmPlaybackPage.ProgressLabel));
                if (old.Duration != nextPage.Duration)
                    page?.NotifyMedia(nameof(FilmPlaybackPage.Duration), nameof(FilmPlaybackPage.CanSeek), nameof(FilmPlaybackPage.ProgressLabel));
                if (old.Rate != nextPage.Rate) page?.NotifyMedia(nameof(FilmPlaybackPage.PlaybackRate));
                if (old.Aspect != nextPage.Aspect) page?.NotifyMedia(nameof(FilmPlaybackPage.PlaybackAspect));
                if (old.Volume != nextPage.Volume) page?.NotifyMedia(nameof(FilmPlaybackPage.Volume));
                if (old.HasOutput != nextPage.HasOutput || old.IsPlaying != nextPage.IsPlaying || old.Buffering != nextPage.Buffering)
                    page?.NotifyMedia(nameof(FilmPlaybackPage.PlayerBuffering), nameof(FilmPlaybackPage.PlayerStatusVisible),
                        nameof(FilmPlaybackPage.PlayerStatusText), nameof(FilmPlaybackPage.StatusText));
            }
        }
        var next = new PublishedMediaState(MediaIdentity, MediaTitle, HasOutput, IsPlaying, Position, Duration);
        if (next == publishedMediaState) return;
        publishedMediaState = next;
        MediaStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetExecutionError(string code, bool fromRelease = false)
    {
        executionErrorFromRelease = code.Length > 0 && fromRelease;
        if (ExecutionError == code) return;
        ExecutionError = code;
        page?.NotifyExecutionError();
    }

    internal void RequirePage(FilmPlaybackPage value)
    {
        if (!IsCurrentPage(value) || !HasSession) throw new FilmPlaybackException("STALE_SESSION");
    }

    internal bool IsCurrentPage(FilmPlaybackPage value) => ReferenceEquals(page, value) && !closed && closing is null;

    internal bool IsOpeningPage(FilmPlaybackPage value) =>
        opening is not null && openRequest is { } request && ReferenceEquals(request.Page, value);

    internal FilmPlaybackSnapshot? SnapshotFor(FilmPlaybackPage value) => IsCurrentPage(value) ? snapshot : null;

    internal void RetainOwner(FilmPlaybackPage value, MeowFilm.Desktop.DesktopWindow? owner)
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
        DebugLog.Info(DebugCategories.FilmPlayback, "session", "session.open",
            new { generation, targetId = targetPage.EntryTarget.Id, kind = targetPage.EntryTarget.Kind });
        return Track(PrepareAsync(targetPage, ownedParameters, token, retirement, generation, reconnect: false));
    }

    private async Task PrepareAsync(FilmPlaybackPage targetPage, JsonElement parameters, CancellationToken token,
        Task retirement, long generation, bool reconnect)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
        opening = preparation;
        try { await OpenCoreAsync(targetPage, parameters, preparation.Token, retirement, generation, reconnect); }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested &&
            !token.IsCancellationRequested && generation != ownerGeneration) { }
        finally { if (ReferenceEquals(opening, preparation)) opening = null; }
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
            await nativeWork.WaitAsync(token);
            try
            {
                if (await DrainUndrainedAsync() is { } pendingDrainFailure)
                    throw pendingDrainFailure;
            }
            finally { nativeWork.Release(); }
            if (generation != ownerGeneration || closed) return;
            page = targetPage; presenting = true;
            var opening = await prepareOpen(targetPage, openParameters, reconnect, token);
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
                if (!captured.Revoked.IsCancellationRequested)
                    await client.ReleaseAsync(opened.SessionId, token: captured.Revoked);
                token.ThrowIfCancellationRequested();
                return;
            }
            connection = captured; rpc = client;
            blockedAttemptId = "";
            searchScope = FilmJson.Array(opening.Parameters, "sources");
            searchSelection = opened.EntryTarget;
            targetPage.FreezeSearchScope(FilmPlaybackOpen.OriginalSources(openParameters, searchScope));
            runtimeRevoke = captured.Revoked.Register(() =>
            {
                if (!dispatcher.IsClosing)
                    _ = dispatcher.EnqueueAsync(new Action(() =>
                    {
                        if (generation == ownerGeneration)
                        {
                            DebugLog.Warn(DebugCategories.FilmPlayback, "session", "runtime.revoked", new { generation });
                            page?.SetConnectionError("CLOSED");
                            RevokeMedia();
                        }
                    }));
            });
            DebugLog.Info(DebugCategories.FilmPlayback, "session", "session.opened",
                new { generation, sessionId = opened.SessionId, sequence = opened.Sequence, reconnect });
            Apply(opened);
            StartObserving();
            targetPage.PublishSearch();
        }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "session", "session.open.failed",
                new { generation, code = Code(error), reconnect }, error: error);
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
            {
                DebugLog.Warn(DebugCategories.FilmPlayback, "session", "observe.disconnected", new { generation, sessionId = id });
                page?.SetConnectionError("DISCONNECTED");
                WakeBrowsing();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == ownerGeneration && connection?.Revoked.IsCancellationRequested == true) RevokeMedia();
        }
        catch (Exception error)
        {
            if (generation == ownerGeneration)
            {
                DebugLog.Warn(DebugCategories.FilmPlayback, "session", "observe.failed",
                    new { generation, sessionId = id, code = Code(error) }, error: error);
                page?.SetConnectionError(Code(error));
                WakeBrowsing();
                if (connection?.Revoked.IsCancellationRequested == true) RevokeMedia();
            }
        }
    }

    internal async Task ReconnectAsync()
    {
        if (!HasSession)
        {
            if (openRequest is not { } request || closed) throw new FilmPlaybackException("STALE_SESSION");
            DebugLog.Info(DebugCategories.FilmPlayback, "session", "session.reconnect.reopen",
                new { targetId = request.Page.EntryTarget.Id });
            request = (request.Page, FilmPlaybackOpen.RememberResults(request.Parameters, snapshot));
            var restore = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
            restore["restoreOnly"] = true;
            if (request.Page.CaptureRequest.Browsing is { } browsing)
                restore["browsing"] = JsonNode.Parse(browsing.GetRawText());
            request = (request.Page, JsonSerializer.SerializeToElement(restore, FilmJson.Options));
            openRequest = request;
            var retirement = CloseSessionAsync();
            long reopeningGeneration = ++ownerGeneration;
            await Track(PrepareAsync(request.Page, request.Parameters, CancellationToken.None, retirement, reopeningGeneration,
                reconnect: true));
            return;
        }
        var client = rpc ?? throw new FilmPlaybackException("STALE_SESSION");
        var state = snapshot ?? throw new FilmPlaybackException("STALE_SESSION");
        var activeConnection = connection ?? throw new FilmPlaybackException("STALE_SESSION");
        long generation = ownerGeneration;
        DebugLog.Info(DebugCategories.FilmPlayback, "session", "session.reconnect",
            new { generation, sessionId = state.SessionId });
        var oldObservation = observation; var oldObserving = observing;
        if (oldObservation is not null) await oldObservation.CancelAsync();
        if (oldObserving is not null) await oldObserving;
        var latest = new FilmPlaybackSnapshot(await client.CallAsync("session.snapshot",
            new { sessionId = state.SessionId.ToString() }, activeConnection.Revoked));
        if (generation != ownerGeneration || closing is not null) throw new FilmPlaybackException("STALE_SESSION");
        blockedAttemptId = "";
        page?.SetConnectionError("");
        Apply(latest, scheduleMedia: false);
        if (current is { Started: false, Retiring: false, TerminalReported: false } input && HasOutput)
        {
            if (latest.CurrentId == input.Receipt.AttemptId) { input.Started = true; QueueProgress(); }
            else if (latest.AttemptId == input.Receipt.AttemptId && !FilmJson.Bool(latest.Attempt, "prepareOnly"))
            {
                var confirmed = await client.CommandAsync(state.SessionId, "media.started",
                    new { attemptId = input.Receipt.AttemptId }, activeConnection.Revoked);
                input.Started = true;
                if (generation != ownerGeneration) throw new FilmPlaybackException("STALE_SESSION");
                latest = new(confirmed);
                Apply(latest, scheduleMedia: false); QueueProgress();
            }
        }
        if (ExecutionError.Length > 0 || releaseFailure is not null)
        {
            // Only explicit recovery verifies both the accepted occurrence and physical drain.
            await nativeWork.WaitAsync();
            Exception? pendingDrainFailure;
            try { pendingDrainFailure = await DrainUndrainedAsync(); }
            finally { nativeWork.Release(); }
            if (generation != ownerGeneration) throw new FilmPlaybackException("STALE_SESSION");
            if (pendingDrainFailure is not null) throw pendingDrainFailure;
            if (current is { Started: true, Retiring: false, TerminalReported: false } accepted &&
                HasOutput && latest.CurrentId == accepted.Receipt.AttemptId || current is null)
                SetExecutionError("");
        }
        SchedulePendingMedia(latest);
        StartObserving();
    }

    internal Task ResumePresentationAsync()
    {
        presenting = true;
        if (rpc is null || snapshot is null) return Task.CompletedTask;
        return ResumeCoreAsync();

        async Task ResumeCoreAsync()
        {
            if (observing is not { IsCompleted: false }) await ReconnectAsync();
        }
    }

    internal void UpdateSearch(FilmPlaybackPage targetPage, long revision)
    {
        if (!IsCurrentPage(targetPage) || rpc is null || snapshot is null || connection is null ||
            searchSelection is null || connection.Revoked.IsCancellationRequested) return;
        if (FilmJson.Text(snapshot.Raw, "stage") == "stopped" ||
            snapshot.Target.Kind == "group" && snapshot.Target.Members!.Value.GetArrayLength() == 0 &&
            !targetPage.IsUnmatchedDoubanEntry) return;
        if (targetPage.SearchContext is not { } context ||
            !FilmPlaybackOpen.SameOwner(targetPage.Request.DataOwner, connection)) return;
        var parameters = FilmJson.Encode(new
        {
            selectionId = searchSelection.Id, revision, searchReference = context.Reference, sources = searchScope
        });
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

    private void Apply(FilmPlaybackSnapshot next, bool scheduleMedia = true)
    {
        if (snapshot is { } previous &&
            (next.SessionId != previous.SessionId || next.Sequence <= previous.Sequence)) return;
        string error = DisplayError(next);
        string previousError = snapshot is { } old ? DisplayError(old) : "";
        snapshot = next;
        page?.Apply(next);
        page?.SetConnectionError("");
        WakeBrowsing();
        DebugLog.Debug(DebugCategories.FilmPlayback, "session", "session.snapshot",
            new { sessionId = next.SessionId, sequence = next.Sequence, stage = FilmJson.Text(next.Raw, "stage"),
                attemptId = next.AttemptId, currentId = next.CurrentId });
        if (error.Length > 0 && error != previousError)
            OperationFailed?.Invoke(this, error);
        NotifyMedia();
        string id = next.AttemptId;
        if (blockedAttemptId.Length > 0 && id != blockedAttemptId) blockedAttemptId = "";
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
        if (scheduleMedia) SchedulePendingMedia(next);
    }

    private static string DisplayError(FilmPlaybackSnapshot value) =>
        FilmJson.Text(value.Raw, "errorStage") is "prepare" or "playback" &&
            FilmJson.Text(value.Raw, "error") is { Length: > 0 } native ? native :
        FilmJson.Text(value.Raw, "metadataError") is { Length: > 0 } metadata ? metadata :
        value.MetadataCache?.ErrorCode is { Length: > 0 } cache ? cache :
        FilmJson.Text(value.Browsing, "error") is { Length: > 0 } browsing ? browsing :
        FilmJson.Text(value.Raw, "error");

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
        if (snapshot is { } state && rpc is { } client && connection is { Revoked.IsCancellationRequested: false } activeConnection)
            await client.ReleaseAsync(state.SessionId, keepMedia: true, activeConnection.Revoked);
    }

    internal Task CloseSessionAsync()
    {
        ownerGeneration++; intentGeneration++;
        searchUpdate = null; searchScope = []; searchSelection = null;
        opening?.Cancel();
        pendingPublication?.Cancel();
        if (closing is not null) return closing;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closing = completion.Task;
        NotifyMedia();
        _ = FinishAsync();
        return completion.Task;

        async Task FinishAsync()
        {
            try
            {
                await CloseCoreAsync();
                closing = null;
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                closing = null;
                completion.TrySetException(error);
            }
        }
    }

    private async Task CloseCoreAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            DebugLog.Info(DebugCategories.FilmPlayback, "session", "session.close",
                new { sessionId = snapshot?.SessionId });
            Exception? failure = null;
            async Task Drain(Task work)
            {
                try { await work; } catch (Exception error) { failure ??= error; }
            }
            async Task ReleaseRuntime(Func<Task> action)
            {
                if (connection?.Revoked.IsCancellationRequested == true) return;
                try { await action(); }
                catch (Exception error) when (connection?.Revoked.IsCancellationRequested == true &&
                    Code(error) is "CLOSED" or "CANCELLED") { }
                catch (Exception error) { failure ??= error; }
            }
            if (observation is not null) await Drain(observation.CancelAsync());
            if (observing is not null) await Drain(observing);
            await nativeWork.WaitAsync();
            try
            {
                await control.WaitAsync();
                try
                {
                    await ReleaseRuntime(FlushProgressLockedAsync);
                    if (current is { } retiring) retiring.Retiring = true;
                    if (current?.NativeScope is { } scope && media.Scope == scope) await Drain(media.StopAsync(scope));
                    if (snapshot is { } state && rpc is { } client)
                    {
                        if (pending is { } unaccepted) await Drain(ReleaseInputAsync(unaccepted, client, state.SessionId));
                        if (current is { } accepted) await Drain(ReleaseInputAsync(accepted, client, state.SessionId));
                        if (connection is { } activeConnection)
                            await ReleaseRuntime(() => client.ReleaseAsync(state.SessionId, token: activeConnection.Revoked));
                    }
                    failure ??= await DrainUndrainedAsync();
                    pending = null; current = null; processingId = ""; revokedPublicationId = "";
                    blockedAttemptId = ""; continuingFrom = "";
                    snapshot = null; rpc = null; connection = null; retainedWindow = null; presenting = false;
                    page?.ExecutionEnded();
                    rate = 1; aspect = null;
                    runtimeRevoke.Dispose();
                    pendingPublication = null;
                    observation?.Dispose(); observation = null; observing = null;
                    NotifyMedia();
                    if (failure is not null) throw failure;
                    SetExecutionError("");
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
        BridgeException bridge => bridge.Code,
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
            control.Dispose();
            nativeWork.Dispose();
            lifecycle.Dispose();
        }
        if (failure is not null) throw failure;
    }
}
