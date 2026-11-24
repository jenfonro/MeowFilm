using System.Collections.ObjectModel;
using MeowFilm.Core.Debug;

namespace MeowFilm.Media;

/// <summary>
/// Owns exact media receipts for one runtime session generation, not URL permissions.
/// Each lease's releaseResource waits for actual media.release on that same session, never reconnects.
/// Remote RPC release failures are logged and surfaced without permanently bricking
/// future leases once the runtime recovers.
/// </summary>
public sealed class RuntimeMediaAuthority : IAsyncDisposable
{
    private static int activeAuthorities;
    private static int activeLeases;
    internal static int ActiveAuthorityCount => Volatile.Read(ref activeAuthorities);
    internal static int ActiveLeaseCount => Volatile.Read(ref activeLeases);

    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken revoked;
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private bool closed;
    private Task? disposal;

    public RuntimeMediaAuthority(CancellationToken runtimeRevoked)
    {
        if (!runtimeRevoked.CanBeCanceled)
            throw new PlaybackException(PlaybackError.InvalidResourceLease);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(runtimeRevoked);
        revoked = lifetime.Token;
        Interlocked.Increment(ref activeAuthorities);
    }

    public INativeMediaResourceLease CreateLease(string resourceId, Uri address, CancellationToken scopeRevoked,
        Func<string, ValueTask> releaseResource)
    {
        lock (sync)
        {
            if (closed || revoked.IsCancellationRequested || scopeRevoked.IsCancellationRequested)
                throw new PlaybackException(PlaybackError.ResourceRevoked);
            if (!scopeRevoked.CanBeCanceled || string.IsNullOrEmpty(resourceId) ||
                releaseResource is null || leases.ContainsKey(resourceId))
                throw new PlaybackException(PlaybackError.InvalidResourceLease);
            NativeHttpMediaOptions.ValidateSyntax(address);
            var lease = new Lease(this, resourceId, address, scopeRevoked, releaseResource);
            leases.Add(resourceId, lease);
            Interlocked.Increment(ref activeLeases);
            return lease;
        }
    }

    private void Released(Lease lease)
    {
        bool removed;
        lock (sync)
        {
            removed = leases.TryGetValue(lease.Id, out var current) && ReferenceEquals(current, lease) &&
                leases.Remove(lease.Id);
        }
        if (removed) Interlocked.Decrement(ref activeLeases);
    }

    private void LogReleaseFailed(Lease lease)
    {
        if (DebugLog.IsEnabled)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "media-authority", "lease.release_failed",
                new { resourceId = lease.Id });
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            closed = true;
            return new(disposal ??= CloseAsync(leases.Values.ToArray()));
        }
    }

    private async Task CloseAsync(Lease[] accepted)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool failed = false;
        try { lifetime.Cancel(); } catch { failed = true; }
        try { await Task.WhenAll(accepted.Select(lease => lease.CloseForAuthorityAsync())).ConfigureAwait(false); }
        catch { failed = true; }
        finally
        {
            lifetime.Dispose();
            Interlocked.Decrement(ref activeAuthorities);
        }
        if (failed) throw new PlaybackException(PlaybackError.ReleaseFailed);
    }

    private sealed class Lease : INativeMediaResourceLease
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        private readonly object sync = new();
        private readonly RuntimeMediaAuthority owner;
        private readonly Func<string, ValueTask> releaseResource;
        private readonly CancellationTokenSource lifetime;
        private Task? release;
        private bool released;
        private bool lifetimeCancelled;
        private bool lifetimeCancelling;
        private bool lifetimeDisposePending;
        private bool lifetimeDisposed;
        internal string Id { get; }
        public Uri Address { get; }
        public IReadOnlyDictionary<string, string> Headers => NoHeaders;
        public CancellationToken Revoked { get; }

        internal Lease(RuntimeMediaAuthority owner, string id, Uri address, CancellationToken scope,
            Func<string, ValueTask> releaseResource)
        {
            this.owner = owner;
            this.releaseResource = releaseResource;
            Id = id;
            Address = address;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.revoked, scope);
            Revoked = lifetime.Token;
        }

        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                if (released) return ValueTask.CompletedTask;
                if (release is { IsCompleted: false } inFlight) return new(inFlight);
                var task = ReleaseAsync();
                release = task;
                return new(task);
            }
        }

        internal async Task CloseForAuthorityAsync()
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Detach authority accounting and local CTS even if remote RPC release failed;
                // `released` stays false so a caller-held retry can still invoke releaseResource(Id).
                owner.Released(this);
                DisposeLifetimeOnce();
            }
        }

        private bool TryBeginCancelLifetime()
        {
            lock (sync)
            {
                if (lifetimeCancelled || lifetimeDisposed) return false;
                lifetimeCancelled = true;
                lifetimeCancelling = true;
                return true;
            }
        }

        private void EndCancelLifetime()
        {
            bool shouldDispose;
            lock (sync)
            {
                lifetimeCancelling = false;
                shouldDispose = lifetimeDisposePending && !lifetimeDisposed;
                if (shouldDispose)
                {
                    lifetimeDisposePending = false;
                    lifetimeDisposed = true;
                }
            }
            if (shouldDispose) lifetime.Dispose();
        }

        private void DisposeLifetimeOnce()
        {
            bool shouldDispose;
            lock (sync)
            {
                if (lifetimeDisposed) return;
                if (lifetimeCancelling)
                {
                    lifetimeDisposePending = true;
                    return;
                }
                lifetimeDisposed = true;
                shouldDispose = true;
            }
            if (shouldDispose) lifetime.Dispose();
        }

        private async Task ReleaseAsync()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            bool cancelFailed = false;
            if (TryBeginCancelLifetime())
            {
                try { lifetime.Cancel(); }
                catch (ObjectDisposedException) { }
                catch { cancelFailed = true; }
                finally { EndCancelLifetime(); }
            }
            bool resourceFailed = false;
            try { await releaseResource(Id).ConfigureAwait(false); }
            catch { resourceFailed = true; }
            if (resourceFailed)
            {
                lock (sync) release = null;
                owner.LogReleaseFailed(this);
                throw new PlaybackException(PlaybackError.ReleaseFailed);
            }
            lock (sync) released = true;
            DisposeLifetimeOnce();
            owner.Released(this);
            if (cancelFailed) throw new PlaybackException(PlaybackError.ReleaseFailed);
        }
    }
}
