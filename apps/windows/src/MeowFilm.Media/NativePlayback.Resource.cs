using System.Windows.Threading;

namespace MeowFilm.Media;

public sealed partial class NativePlayback
{
    /// <summary>
    /// Consumes one explicit native capability, never a source-supplied loopback exception.
    /// Owns lease immediately, including dispatcher/preflight failure. The capability remains
    /// attached through native retirement; a successful return means accepted Play(), not output.
    /// </summary>
    public async Task OpenResourceAsync(string ownerId, string identity, INativeMediaResourceLease lease,
        string title, CancellationToken token = default)
    {
        if (lease is null) throw Error(PlaybackError.InvalidResourceLease);
        var resource = new MediaResourceSession(lease);
        try
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                throw Error(PlaybackError.DispatcherUnavailable);
            if (_dispatcher.CheckAccess()) await Open();
            else
            {
                try
                {
                    await _dispatcher.InvokeAsync(Open, DispatcherPriority.Normal, token)
                        .Task.Unwrap().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested &&
                    (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished))
                { throw Error(PlaybackError.DispatcherUnavailable); }
            }
        }
        catch
        {
            await resource.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        async Task Open()
        {
            ThrowIfDisposed();
            ValidateIdentity(ownerId, identity, title);
            token.ThrowIfCancellationRequested();
            resource.Prepare(token);
            await OpenCoreAsync(ownerId, identity, resource.Address, title,
                resource.Headers, resource.Token, resource);
        }
    }
}

/// <summary>Owns exactly one release even if open, cancellation and retirement fail together.</summary>
internal sealed class MediaResourceSession(INativeMediaResourceLease lease) : IAsyncDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? lifetime;
    private CancellationToken revoked, caller;
    private Task? release;
    private int cancelled;
    internal Uri Address { get; private set; } = null!;
    internal IReadOnlyDictionary<string, string> Headers { get; private set; } = null!;
    internal CancellationToken Token { get; private set; }

    internal void Prepare(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            Address = lease.Address;
            if (Address is null || !Address.IsAbsoluteUri || Address.Scheme != "http" || Address.IdnHost != "127.0.0.1" ||
                Address.Port is <= 0 or > 65535)
                throw new PlaybackException(PlaybackError.InvalidResourceLease);
            NativeHttpMediaOptions.ValidateSyntax(Address);
            revoked = lease.Revoked;
            if (!revoked.CanBeCanceled) throw new PlaybackException(PlaybackError.InvalidResourceLease);
            caller = token;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, revoked);
            Token = lifetime.Token;
            Authorize(Address);
            // Snapshot before any await; a later caller mutation cannot change the request.
            Headers = HttpMediaInput.ValidateRequest(Address, lease.Headers,
                NativeHttpMediaOptions.ForResource(this), allowAdaptive: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException(PlaybackError.InvalidResourceLease); }
    }

    internal void Authorize(Uri address)
    {
        caller.ThrowIfCancellationRequested();
        if (revoked.IsCancellationRequested || Volatile.Read(ref cancelled) != 0)
            throw new PlaybackException(PlaybackError.ResourceRevoked);
        bool authorized;
        try { authorized = lease.Authorizes(address); }
        catch { throw new PlaybackException(PlaybackError.InvalidResourceLease); }
        caller.ThrowIfCancellationRequested();
        if (revoked.IsCancellationRequested || Volatile.Read(ref cancelled) != 0)
            throw new PlaybackException(PlaybackError.ResourceRevoked);
        if (!authorized) throw new PlaybackException(PlaybackError.HttpAddressBlocked);
    }

    internal void Cancel()
    {
        Interlocked.Exchange(ref cancelled, 1);
        try { lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new(release ??= Release());
    }

    private async Task Release()
    {
        // Leave the synchronization lock before invoking external cleanup.
        await Task.Yield();
        Cancel();
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch { throw new PlaybackException(PlaybackError.ReleaseFailed); }
        finally { lifetime?.Dispose(); }
    }
}
