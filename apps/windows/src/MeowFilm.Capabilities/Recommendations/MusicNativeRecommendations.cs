using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities;

/// <summary>Current in-process recommendation and media-selection contract.</summary>
public interface IMusicRecommendationSource
{
    event Action<JsonElement>? Changed;
    IDisposable BindSelection(Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync);
    Task OnPlaybackAsync(MusicRecommendationPlaybackState state, CancellationToken ct = default);
    Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play, CancellationToken ct = default);
    MusicRecommendationFeedback? GetFeedback(string entryId);
    Task PrepareRecommendationsAsync(CancellationToken ct = default);
}

public sealed partial class MusicNativeApplication
{
    private readonly MusicRecommendationQueuePort recommendationQueue;
    private readonly MusicRecommendationCoordinator recommendations;
    private RecommendationBinding? recommendationBinding;
    private long recommendationBindingEpoch;
    private int recommendationBindingChanges;
    private bool recommendationPreparing;
    private bool recommendationReconciliationPending;
    private long recommendationPreparationEpoch;
    private CancellationTokenSource recommendationLifetime = new();
    private Task? recommendationCancellation;
    private int recommendationPreludes;
    private TaskCompletionSource recommendationPreludesDrained = Completed();

    public event Action<JsonElement>? Changed
    {
        add => recommendations.Changed += value;
        remove => recommendations.Changed -= value;
    }

    /// <summary>
    /// Exactly one media consumer. Disposal fences its callbacks immediately,
    /// without synchronously waiting for media or Dispatcher work.
    /// </summary>
    public IDisposable BindSelection(Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync)
    {
        ArgumentNullException.ThrowIfNull(selectAsync);
        lock (gate)
        {
            if (closed || stopping || recommendationPreparing) throw new MusicOperationException("CLOSED");
            if (recommendationBindingChanges != 0) throw new MusicOperationException("BUSY");
            if (recommendationBinding is not null) throw new MusicOperationException("PLAYBACK_ALREADY_BOUND");
            return recommendationBinding = new(this, selectAsync, ++recommendationBindingEpoch);
        }
    }

    private sealed class RecommendationBinding(MusicNativeApplication owner,
        Func<MusicRecommendationSelection, CancellationToken, Task<bool>> callback, long epoch) : IDisposable
    {
        internal readonly Func<MusicRecommendationSelection, CancellationToken, Task<bool>> Callback = callback;
        internal readonly long Epoch = epoch;
        internal int Revoked;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref Revoked, 1) == 0) owner.ReleaseRecommendationBinding(this);
        }
        public override string ToString() => "Music recommendation media binding";
    }

    private void ReleaseRecommendationBinding(RecommendationBinding binding)
    {
        lock (gate)
        {
            if (!ReferenceEquals(recommendationBinding, binding)) return;
            recommendationBinding = null; recommendationBindingEpoch++; recommendationBindingChanges++;
        }
        try
        {
            // Invalidate only fences and schedules cancellation. It does not wait
            // for an admitted selection callback; the coordinator retains that work.
            recommendations.Invalidate();
        }
        finally { lock (gate) recommendationBindingChanges--; }
    }

    private async Task<bool> SelectRecommendationAsync(MusicRecommendationSelection selection, CancellationToken token)
    {
        RecommendationBinding? binding;
        lock (gate) binding = recommendationBinding;
        if (binding is null) return false;
        bool Current()
        {
            if (!selection.IsCurrent()) return false;
            lock (gate)
                return !closed && !stopping && !recommendationPreparing && !accountReceiptUnknown && recommendationBindingChanges == 0 &&
                    Volatile.Read(ref binding.Revoked) == 0 && ReferenceEquals(recommendationBinding, binding) &&
                    recommendationBindingEpoch == binding.Epoch;
        }
        // The coordinator's selection already names the committed occurrence.
        // Add the app/binding fence to the SAME selection; never issue queue.select.
        var guarded = new MusicRecommendationSelection(selection.Action, selection.SessionId, selection.EntryId,
            selection.QueueVersion, selection.Play, selection.Automatic, selection.RestoreOnly, selection.Feedback, Current);
        if (!guarded.IsCurrent() || token.IsCancellationRequested) return false;
        // No WaitAsync: a callback ignoring cancellation still owns real media work.
        bool accepted = await binding.Callback(guarded, token).ConfigureAwait(false);
        return accepted && guarded.IsCurrent() && !token.IsCancellationRequested;
    }

    public Task OnPlaybackAsync(MusicRecommendationPlaybackState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return RecommendationCallAsync(async token =>
        {
            await recommendations.OnPlaybackAsync(state, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    public Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        return RecommendationCallAsync(token => recommendations.TryAdvanceAsync(entryId, naturalEnd, play, token), ct);
    }

    public MusicRecommendationFeedback? GetFeedback(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return null;
        RecommendationBinding? binding;
        lock (gate) binding = recommendationBinding;
        if (binding is null) return null;
        var feedback = recommendations.GetFeedback(entryId);
        lock (gate)
            return closed || stopping || recommendationPreparing || accountReceiptUnknown ||
                !ReferenceEquals(recommendationBinding, binding) || recommendationBindingChanges != 0 ||
                Volatile.Read(ref binding.Revoked) != 0 ? null : feedback;
    }

    /// <summary>Seal recommendations before waiting a UI command tail; do not prepare the library yet.</summary>
    public Task PrepareRecommendationsAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            if (resumption is { IsCompleted: false } recovering)
                return PrepareRecommendationsAfterResumeAsync(recovering, ct);
        }
        var drain = PrepareRecommendationCoreAsync();
        return ct.CanBeCanceled ? drain.WaitAsync(ct) : drain;
    }

    private async Task PrepareRecommendationsAfterResumeAsync(Task recovering, CancellationToken token)
    {
        await recovering.ConfigureAwait(false);
        await PrepareRecommendationsAsync(token).ConfigureAwait(false);
    }

    private Task PrepareRecommendationCoreAsync()
    {
        Task prelude, cancel;
        lock (gate)
        {
            recommendationPreparing = true;
            recommendationReconciliationPending = false;
            recommendationPreparationEpoch++;
            cancel = recommendationCancellation ??= CancelRecommendationLifetimeAsync(recommendationLifetime);
            prelude = recommendationPreludesDrained.Task;
        }
        // The coordinator owns its cached drain and observer-safe notification
        // barrier. The facade adds ONLY its own not-yet-registered startup reads.
        // WhenAll must settle every side even when one preparation fails.
        return Task.WhenAll(cancel, prelude, recommendations.PrepareCloseAsync());
    }

    private static async Task CancelRecommendationLifetimeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* Callback errors cannot undo cancellation or leak into diagnostics. */ }
    }

    private async Task<T> RecommendationCallAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        CancellationToken generation, recommendationGeneration;
        lock (gate)
        {
            if (closed || stopping || recommendationPreparing) throw new MusicOperationException("CLOSED");
            if (accountReceiptUnknown) throw new MusicOperationException("COMMIT_UNKNOWN");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
            recommendationGeneration = recommendationLifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, generation, recommendationGeneration);
            request.Token.ThrowIfCancellationRequested();
            return await operation(request.Token).ConfigureAwait(false);
        }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }

    private async Task<JsonElement> ExecuteRecommendationsAsync(string method, object? parameters, CancellationToken token)
    {
        if (method == "music.recommendations.start")
        {
            CancellationToken generation;
            RecommendationBinding binding;
            lock (gate)
            {
                if (closed || stopping) return Failed("CLOSED");
                if (recommendationPreparing)
                    return Failed(recommendationReconciliationPending ? "COMMIT_UNKNOWN" : "CLOSED");
                if (accountReceiptUnknown) return Failed("COMMIT_UNKNOWN");
                if (recommendationBindingChanges != 0) return Failed("BUSY");
                if (recommendationBinding is null || Volatile.Read(ref recommendationBinding.Revoked) != 0)
                    return Failed("PLAYBACK_UNAVAILABLE");
                binding = recommendationBinding;
                generation = recommendationLifetime.Token;
                if (recommendationPreludes++ == 0) recommendationPreludesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var start = CancellationTokenSource.CreateLinkedTokenSource(token, generation);
            Task<JsonElement> work;
            try
            {
                // Reconcile BEFORE a new coordinator session is registered. A
                // first saved-account notification must not cancel that new session.
                var restored = await accounts.EnsureInitialObservationAsync(start.Token).ConfigureAwait(false);
                if (!Okay(restored)) return restored;
                start.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (closed || stopping || recommendationPreparing) return Failed("CLOSED");
                    if (accountReceiptUnknown) return Failed("COMMIT_UNKNOWN");
                    if (recommendationBindingChanges != 0 || !ReferenceEquals(recommendationBinding, binding) ||
                        Volatile.Read(ref binding.Revoked) != 0) return Failed("PLAYBACK_UNAVAILABLE");
                    // Register before releasing the same gate that seals startup.
                    work = recommendations.ExecuteAsync(method, parameters, start.Token);
                }
            }
            finally
            {
                lock (gate) if (--recommendationPreludes == 0) recommendationPreludesDrained.TrySetResult();
            }
            // Keep the token link alive until the coordinator's physical flight
            // settles, but do not count that flight again as a startup prelude.
            return await work.ConfigureAwait(false);
        }
        lock (gate)
        {
            if (closed || stopping) return Failed("CLOSED");
            if (recommendationReconciliationPending &&
                method is not ("music.recommendations.get" or "music.recommendations.cancel" or "music.recommendations.reconcile"))
                return Failed("COMMIT_UNKNOWN");
            if (accountReceiptUnknown && method is not ("music.recommendations.get" or "music.recommendations.cancel"))
                return Failed("COMMIT_UNKNOWN");
        }
        var result = await recommendations.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
        if (method == "music.recommendations.reconcile" && Okay(result))
            await ResumeReconciledRecommendationsAsync().ConfigureAwait(false);
        return result;
    }

    private async Task ResumeReconciledRecommendationsAsync()
    {
        Task physical;
        long epoch;
        lock (gate)
        {
            if (!recommendationReconciliationPending || closed || stopping || resumption is not null) return;
            epoch = recommendationPreparationEpoch;
            // Retry only the drain, never the uncertain write. Reconciliation has
            // already read the actual nonce receipt through the sole Library owner.
            physical = recommendations.PrepareCloseAsync();
        }
        try { await physical.ConfigureAwait(false); }
        catch (RecommendationException error) when (error.Code == "COMMIT_UNKNOWN")
        { return; } // Other unresolved operations still need explicit user action.
        lock (gate)
        {
            // A fresh narrow/full close wins over a reconciliation completing late.
            if (!recommendationReconciliationPending || closed || stopping || resumption is not null ||
                recommendationPreparationEpoch != epoch) return;
            recommendations.Resume();
            recommendationLifetime.Dispose(); recommendationLifetime = new();
            recommendationCancellation = null;
            recommendationReconciliationPending = false; recommendationPreparing = false;
        }
    }

    private async Task<JsonElement> ReadRecommendationsAsync(MusicRecommendationRequest request, CancellationToken token)
    {
        lock (gate)
        {
            if (closed || stopping || recommendationPreparing) throw new MusicPlatformException("CLOSED", request.Platform, request.Method);
            if (accountReceiptUnknown) throw new MusicPlatformException("COMMIT_UNKNOWN", request.Platform, request.Method);
        }
        // Each real batch captures only the lease actually acquired by this catalog
        // call. The ordinary CatalogSessions consumers keep their prior semantics.
        using var frame = sessions.Begin(awaitPhysical: true);
        try { return await request.ReadAsync(catalog, token).ConfigureAwait(false); }
        catch (MusicPlatformException error) when (error.Code is "AUTH_REQUIRED" or "AUTH_EXPIRED")
        {
            if (frame.Session is { } failed) accounts.Revoke(failed);
            throw;
        }
    }

    private void RecommendationAccountChanged(string platform) => recommendations.Invalidate(platform);

    private Task<JsonElement> ExecuteCatalogWithRecommendationLaneAsync(string method, object? parameters,
        CancellationToken token)
    {
        if (method != "music.catalog.playlist.commit")
            return nativeCatalog.ExecuteAsync(method, parameters, token);
        var input = parameters is JsonElement element ? element : JsonSerializer.SerializeToElement(parameters);
        // Saving a playlist does not replace the active queue. Both commit forms
        // share the lane, but only an actual play intent fences recommendations.
        return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("action", out var action) &&
            action.ValueKind == JsonValueKind.String && action.GetString() == "play"
            ? recommendationQueue.ExecuteExternalAsync(MusicRecommendationQueueChange.Replace,
                ct => nativeCatalog.ExecuteAsync(method, parameters, ct), token)
            : recommendationQueue.InLaneAsync((_, ct) => nativeCatalog.ExecuteAsync(method, parameters, ct), token);
    }
}
