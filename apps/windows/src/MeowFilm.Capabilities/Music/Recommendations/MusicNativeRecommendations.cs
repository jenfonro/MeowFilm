using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly MusicRecommendationCoordinator recommendations;
    private readonly MusicFmPreferences fmPreferences;
    private RecommendationBinding? recommendationBinding;
    private long recommendationBindingEpoch;
    private int recommendationBindingChanges;
    private bool recommendationPreparing;
    private CancellationTokenSource recommendationLifetime = new();
    private Task? recommendationCancellation;
    private int recommendationPreludes;
    private TaskCompletionSource recommendationPreludesDrained = Completed();

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
                return !closed && !stopping && !recommendationPreparing && recommendationBindingChanges == 0 &&
                    Volatile.Read(ref binding.Revoked) == 0 && ReferenceEquals(recommendationBinding, binding) &&
                    recommendationBindingEpoch == binding.Epoch;
        }
        var guarded = new MusicRecommendationSelection(selection.Action, selection.SessionId, selection.EntryId,
            selection.QueueVersion, selection.Play, selection.Automatic, selection.RestoreOnly, selection.Feedback, Current);
        if (!guarded.IsCurrent() || token.IsCancellationRequested) return false;
        bool accepted = await binding.Callback(guarded, token).ConfigureAwait(false);
        return accepted && guarded.IsCurrent() && !token.IsCancellationRequested;
    }

    public MusicRecommendationFeedback? GetFeedback(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return null;
        RecommendationBinding? binding;
        lock (gate) binding = recommendationBinding;
        if (binding is null) return null;
        var feedback = recommendations.GetFeedback(entryId);
        lock (gate)
            return closed || stopping || recommendationPreparing ||
                !ReferenceEquals(recommendationBinding, binding) || recommendationBindingChanges != 0 ||
                Volatile.Read(ref binding.Revoked) != 0 ? null : feedback;
    }

    /// <summary>Seal recommendations before waiting a UI command tail; do not prepare the library yet.</summary>
    public Task PrepareRecommendationsAsync(CancellationToken ct = default)
    {
        var drain = PrepareRecommendationCoreAsync();
        return ct.CanBeCanceled ? drain.WaitAsync(ct) : drain;
    }

    private Task PrepareRecommendationCoreAsync()
    {
        Task prelude, cancel;
        lock (gate)
        {
            recommendationPreparing = true;
            cancel = recommendationCancellation ??= CancelRecommendationLifetimeAsync(recommendationLifetime);
            prelude = recommendationPreludesDrained.Task;
        }
        return Task.WhenAll(cancel, DrainStartupAsync(), recommendations.PrepareCloseAsync());

        async Task DrainStartupAsync()
        {
            await prelude.ConfigureAwait(false);
            await accounts.DrainInitialObservationAsync().ConfigureAwait(false);
        }
    }

    private static async Task CancelRecommendationLifetimeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* Callback errors cannot undo cancellation or leak into diagnostics. */ }
    }

    public Task<MusicRecommendationSnapshot> StartRecommendationAsync(MusicRecommendationStartRequest input, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            CancellationToken generation;
            RecommendationBinding binding;
            lock (gate)
            {
                if (closed || stopping || recommendationPreparing) throw new MusicOperationException("CLOSED");
                if (recommendationBindingChanges != 0) throw new MusicOperationException("BUSY");
                if (recommendationBinding is null || Volatile.Read(ref recommendationBinding.Revoked) != 0)
                    throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
                binding = recommendationBinding;
                generation = recommendationLifetime.Token;
                if (recommendationPreludes++ == 0) recommendationPreludesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var start = CancellationTokenSource.CreateLinkedTokenSource(token, generation);
            using var frame = sessions.Begin(awaitPhysical: true);
            Task<MusicRecommendationSnapshot>? work = null;
            MusicPlatformSession? fmAccount = null;
            string? fmPreferenceScope = null;
            try
            {
                await accounts.EnsureInitialObservationAsync(start.Token).ConfigureAwait(false);
                var effective = input;
                if (effective.Kind == "fm")
                {
                    start.Token.ThrowIfCancellationRequested();
                    RequireRecommendationStartGate(binding);
                    work = recommendations.TryReuseFmStart(effective, start.Token);
                    if (work is null)
                    {
                        start.Token.ThrowIfCancellationRequested();
                        fmAccount = await sessions.AcquireAsync("wy", CancellationToken.None).ConfigureAwait(false);
                        fmPreferenceScope = fmAccount?.UserId is { Length: > 0 } uid ? uid : "guest";
                        start.Token.ThrowIfCancellationRequested();
                        if (effective.Mode is null)
                        {
                            var saved = await fmPreferences.ReadAsync(fmPreferenceScope, CancellationToken.None).ConfigureAwait(false);
                            effective = effective with
                            {
                                Mode = saved.Mode,
                                Submode = saved.Submode,
                                RuleId = saved.RuleId.Length > 0 ? saved.RuleId : null,
                                TabTagId = saved.TabTagId.Length > 0 ? saved.TabTagId : null
                            };
                        }
                    }
                }
                if (work is null)
                {
                    start.Token.ThrowIfCancellationRequested();
                    if (fmAccount is not null && !accounts.IsCurrent(fmAccount)) throw new MusicOperationException("STALE_SESSION");
                    lock (gate)
                    {
                        RequireRecommendationStartGateLocked(binding);
                        work = recommendations.StartAsync(effective, start.Token);
                    }
                }
            }
            finally
            {
                lock (gate) if (--recommendationPreludes == 0) recommendationPreludesDrained.TrySetResult();
            }
            var started = await work.ConfigureAwait(false);
            if (fmPreferenceScope is not null && started.Error.Length == 0 && started.Mode.Length > 0)
            {
                await SaveFmSelectionAsync(fmAccount, fmPreferenceScope, started, start.Token).ConfigureAwait(false);
            }
            return started;
        }, cancellationToken);

    private void RequireRecommendationStartGate(RecommendationBinding binding)
    {
        lock (gate) RequireRecommendationStartGateLocked(binding);
    }

    private void RequireRecommendationStartGateLocked(RecommendationBinding binding)
    {
        if (closed || stopping || recommendationPreparing) throw new MusicOperationException("CLOSED");
        if (recommendationBindingChanges != 0 || !ReferenceEquals(recommendationBinding, binding) ||
            Volatile.Read(ref binding.Revoked) != 0) throw new MusicOperationException("PLAYBACK_UNAVAILABLE");
    }

    private async Task SaveFmSelectionAsync(MusicPlatformSession? account, string? preferenceScope,
        MusicRecommendationSnapshot snapshot, CancellationToken token)
    {
        bool Current() => !token.IsCancellationRequested && (account is null || accounts.IsCurrent(account)) &&
            recommendations.IsFmSelectionCurrent(snapshot);
        lock (gate)
        {
            if (closed || stopping || recommendationPreparing || !Current()) return;
            if (recommendationPreludes++ == 0) recommendationPreludesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            if (preferenceScope is null)
                account ??= await accounts.AcquireSettledAsync("wy", CancellationToken.None).ConfigureAwait(false);
            string resolvedScope = account?.UserId is { Length: > 0 } uid ? uid : preferenceScope ?? "guest";
            if (!Current()) return;
            var selection = new MusicFmSelection(snapshot.Mode, snapshot.Submode, snapshot.RuleId, snapshot.TabTagId);
            await fmPreferences.SaveAsync(resolvedScope, selection, Current, CancellationToken.None).ConfigureAwait(false);
        }
        finally { lock (gate) if (--recommendationPreludes == 0) recommendationPreludesDrained.TrySetResult(); }
    }

    private async Task<JsonElement> ReadRecommendationsAsync(MusicRecommendationRequest request, CancellationToken token)
    {
        lock (gate)
        {
            if (closed || stopping || recommendationPreparing) throw new MusicPlatformException("CLOSED", request.Platform, request.Method);
        }
        token.ThrowIfCancellationRequested();
        using var frame = sessions.Begin(awaitPhysical: true, inheritLease: true);
        try { return await request.ReadAsync(catalog, CancellationToken.None).ConfigureAwait(false); }
        catch (MusicPlatformException error)
        {
            if (error.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED" && frame.Session is { } failed)
                accounts.Revoke(failed);
            throw;
        }
    }

    private void RecommendationAccountChanged(string platform) => recommendations.Invalidate(platform);
}
