using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly NativeMusicSearchDiscovery discovery;
    private long discoveryAccountEpoch, issuedDiscoveryGeneration, discoveryReadSerial;
    private int discoveryInvalidations;
    public event Action? DiscoveryInvalidated;

    private void DiscoveryAccountChanged(string platform)
    {
        if (platform != "wy") return;
        lock (gate) { discoveryInvalidations++; discoveryAccountEpoch++; issuedDiscoveryGeneration = 0; }
        try { discovery.Invalidate(platform); }
        finally
        {
            // The service can synchronously cancel arbitrary pending native work.
            // Do not hold app gate across it, but fence both ends so a concurrent
            // anonymous-cache read cannot reissue the old generation in the gap.
            lock (gate) { discoveryAccountEpoch++; issuedDiscoveryGeneration = 0; discoveryInvalidations--; }
        }
        foreach (var observer in DiscoveryInvalidated?.GetInvocationList() ?? [])
        {
            try { ((Action)observer)(); }
            catch { /* A display listener cannot prevent account invalidation. */ }
        }
    }

    public bool IsDiscoveryCurrent(long generation)
    {
        lock (gate) return !closed && !stopping && !accountReceiptUnknown && discoveryInvalidations == 0 &&
            generation > 0 && generation == issuedDiscoveryGeneration;
    }

    public Task<MusicSearchDiscoverySnapshot> ReadDiscoveryAsync(bool refresh, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => ExecuteDiscoveryCoreAsync(rotate: false, refresh, 0, token), cancellationToken);

    public Task<MusicSearchDiscoverySnapshot> RotateDiscoveryAsync(long generation, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => ExecuteDiscoveryCoreAsync(rotate: true, false, generation, token), cancellationToken);

    private async Task<MusicSearchDiscoverySnapshot> ExecuteDiscoveryCoreAsync(bool rotate, bool refresh, long generation, CancellationToken token)
    {
        if (!rotate)
        {
            // Initial durable-load notifications must run before a new discovery flight is admitted.
            await accounts.RestoreForDiscoveryAsync(token).ConfigureAwait(false);
        }
        else if (generation < 1)
        {
            throw new MusicOperationException("INVALID_REQUEST");
        }
        long account, serial, previousIssued;
        lock (gate)
        {
            if (accountReceiptUnknown) throw new MusicOperationException("COMMIT_UNKNOWN");
            if (discoveryInvalidations > 0) throw new MusicOperationException("STALE_SESSION");
            if (rotate && generation != issuedDiscoveryGeneration) throw new MusicOperationException("STALE_SESSION");
            previousIssued = issuedDiscoveryGeneration;
            serial = ++discoveryReadSerial;
            if (refresh) issuedDiscoveryGeneration = 0;
            account = discoveryAccountEpoch;
        }
        try
        {
            var snapshot = !rotate
                ? await discovery.ReadAsync(refresh, token).ConfigureAwait(false)
                : discovery.Rotate(generation);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (closed || stopping) throw new MusicOperationException("CLOSED");
                if (accountReceiptUnknown) throw new MusicOperationException("COMMIT_UNKNOWN");
                if (discoveryInvalidations > 0 || account != discoveryAccountEpoch || serial != discoveryReadSerial)
                    throw new MusicOperationException("STALE_SESSION");
                issuedDiscoveryGeneration = snapshot.Generation;
            }
            return snapshot;
        }
        catch (MusicPlatformException) { RestoreFailedRefresh(); throw; }
        catch (OperationCanceledException) { RestoreFailedRefresh(); throw; }

        void RestoreFailedRefresh()
        {
            lock (gate)
                if (refresh && !closed && !stopping && !accountReceiptUnknown && discoveryInvalidations == 0 &&
                    account == discoveryAccountEpoch && serial == discoveryReadSerial)
                    issuedDiscoveryGeneration = previousIssued;
        }
    }

    private sealed class DiscoverySessions(Accounts.MusicAccountOwner owner) : IMusicPlatformSessionSource
    {
        // The ordinary account API permits abandoning a read wait while its
        // owner retains physical work. Discovery's service-level drain is stricter.
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken) =>
            owner.AcquireSettledAsync(platform, cancellationToken);
        public bool IsCurrent(MusicPlatformSession session) => owner.IsCurrent(session);
    }
}
