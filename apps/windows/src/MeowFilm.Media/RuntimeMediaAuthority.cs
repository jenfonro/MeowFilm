using System.Collections.ObjectModel;

namespace MeowFilm.Media;

/// <summary>
/// Owns exact media receipts for one runtime generation, not URL permissions.
/// releaseResource waits for actual media.release on that same session, never reconnects.
/// A rejected receipt remains the caller's responsibility.
/// </summary>
public sealed class RuntimeMediaAuthority : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken revoked;
    private readonly Func<string, ValueTask> releaseResource;
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private bool closed, releaseFailed;
    private Task? disposal;

    public RuntimeMediaAuthority(CancellationToken runtimeRevoked, Func<string, ValueTask> releaseResource)
    {
        if (!runtimeRevoked.CanBeCanceled || releaseResource is null)
            throw new PlaybackException(PlaybackError.InvalidResourceLease);
        this.releaseResource = releaseResource;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(runtimeRevoked);
        revoked = lifetime.Token;
    }

    /// <summary>
    /// One exclusive lease per prepare receipt. scopeRevoked belongs to the source/account
    /// generation, not UI visibility or smart-search completion. Node remains the release
    /// owner of this resource ID; it does not define a native URL access graph.
    /// </summary>
    public INativeMediaResourceLease CreateLease(string resourceId, Uri address, CancellationToken scopeRevoked)
    {
        lock (sync)
        {
            if (closed || revoked.IsCancellationRequested || scopeRevoked.IsCancellationRequested)
                throw new PlaybackException(PlaybackError.ResourceRevoked);
            if (releaseFailed) throw new PlaybackException(PlaybackError.ReleaseFailed);
            if (!scopeRevoked.CanBeCanceled || !IsToken(resourceId) ||
                leases.ContainsKey(resourceId))
                throw new PlaybackException(PlaybackError.InvalidResourceLease);
            NativeHttpMediaOptions.ValidateSyntax(address);
            var lease = new Lease(this, resourceId, address, scopeRevoked);
            leases.Add(resourceId, lease);
            return lease;
        }
    }

    // Existing media.release RPC resource ID: a 24-byte base64url token.
    private static bool IsToken(string? value) => value is { Length: 32 } &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private void Released(Lease lease, bool failed)
    {
        lock (sync)
        {
            releaseFailed |= failed;
            if (leases.TryGetValue(lease.Id, out var current) && ReferenceEquals(current, lease))
                leases.Remove(lease.Id);
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
        // Leave the owner lock without capturing a UI synchronization context.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool failed = false;
        try { lifetime.Cancel(); } catch { failed = true; }
        try { await Task.WhenAll(accepted.Select(lease => lease.DisposeAsync().AsTask())).ConfigureAwait(false); }
        catch { failed = true; }
        finally { lifetime.Dispose(); }
        lock (sync) failed |= releaseFailed;
        if (failed) throw new PlaybackException(PlaybackError.ReleaseFailed);
    }

    private sealed class Lease : INativeMediaResourceLease
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        private readonly object sync = new();
        private readonly RuntimeMediaAuthority owner;
        private readonly CancellationTokenSource lifetime;
        private Task? release;
        internal string Id { get; }
        public Uri Address { get; }
        public IReadOnlyDictionary<string, string> Headers => NoHeaders;
        public CancellationToken Revoked { get; }

        internal Lease(RuntimeMediaAuthority owner, string id, Uri address, CancellationToken scope)
        {
            this.owner = owner;
            Id = id;
            Address = address;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.revoked, scope);
            Revoked = lifetime.Token;
        }

        public ValueTask DisposeAsync()
        {
            lock (sync) return new(release ??= ReleaseAsync());
        }

        private async Task ReleaseAsync()
        {
            // External cleanup runs outside either lock and never needs the UI dispatcher.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            bool failed = false;
            try { lifetime.Cancel(); } catch { failed = true; }
            try { await owner.releaseResource(Id).ConfigureAwait(false); }
            catch { failed = true; }
            finally
            {
                lifetime.Dispose();
                owner.Released(this, failed);
            }
            if (failed) throw new PlaybackException(PlaybackError.ReleaseFailed);
        }
    }
}
