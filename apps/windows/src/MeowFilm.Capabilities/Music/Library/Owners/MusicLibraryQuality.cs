using System.Globalization;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private readonly object qualityLeaseGate = new();
    private readonly HashSet<QualityLease> qualityLeases = [];

    private static MusicQualityPolicyRecord QualityPolicy(MusicLibrarySession doc) =>
        doc.ReadQualityPolicy() ?? MusicQualityPolicy.Default();

    private static string QualitySignature(MusicQualityPolicyRecord policy) =>
        policy.Revision.ToString(CultureInfo.InvariantCulture);

    private static string QualitySignature(MusicLibrarySession doc) =>
        QualitySignature(QualityPolicy(doc));

    public Task<MusicQualitySettingsResult> GetQualityPolicyAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var current = QualityPolicy(doc);
            return new MusicQualitySettingsResult(current, QualitySignature(current));
        }, cancellationToken);

    public async Task<MusicQualitySettingsResult> SaveQualityPolicyAsync(
        long ifRevision,
        string request,
        string[] qualityOrder,
        string[] qualityEnabled,
        string[] spatialOrder,
        string[] spatialEnabled,
        CancellationToken cancellationToken = default)
    {
        bool mutated = false;
        var saved = await RunLibraryWriteAsync(doc =>
        {
            var current = QualityPolicy(doc);
            MusicRules.Require(ifRevision == current.Revision, "REVISION_CONFLICT");
            var next = new MusicQualityPolicyRecord(
                checked(current.Revision + 1),
                request,
                qualityOrder.ToArray(),
                qualityEnabled.ToArray(),
                spatialOrder.ToArray(),
                spatialEnabled.ToArray());
            MusicQualityPolicy.Validate(next);
            cancellationToken.ThrowIfCancellationRequested();
            RevokeQualityLeases();
            doc.SetQualityPolicy(next);
            return (new MusicQualitySettingsResult(next, QualitySignature(next)), true);
        }, cancellationToken, onCommitted: () => mutated = true).ConfigureAwait(false);
        if (mutated) InvokeSafe(QualityPolicyChanged, saved);
        return saved;
    }

    internal sealed class QualityLease : IDisposable
    {
        private readonly MusicLibraryOwner owner;
        private readonly object sync = new();
        private readonly CancellationTokenSource revoked = new();
        private bool retired, revoking, released, disposed;
        internal QualityLease(MusicLibraryOwner owner, MusicQualityPolicyRecord policy, string signature)
        { this.owner = owner; Policy = policy; Signature = signature; Token = revoked.Token; }
        internal MusicQualityPolicyRecord Policy { get; }
        internal string Signature { get; }
        internal CancellationToken Token { get; }
        internal void Revoke()
        {
            lock (sync)
            {
                if (retired || disposed) return;
                retired = true;
                revoking = true;
            }
            try { revoked.Cancel(); }
            catch (AggregateException) { }
            finally
            {
                lock (sync) revoking = false;
                DisposeIfReady();
            }
        }
        public void Dispose()
        {
            lock (owner.qualityLeaseGate) owner.qualityLeases.Remove(this);
            lock (sync) { if (released) return; released = true; }
            DisposeIfReady();
        }
        private void DisposeIfReady()
        {
            bool shouldDispose;
            lock (sync)
            {
                shouldDispose = released && !revoking && !disposed;
                if (shouldDispose) disposed = true;
            }
            if (shouldDispose) revoked.Dispose();
        }
    }

    private void RevokeQualityLeases()
    {
        QualityLease[] leases;
        lock (qualityLeaseGate) leases = qualityLeases.ToArray();
        foreach (var lease in leases) lease.Revoke();
    }

    internal async Task<QualityLease> AcquireQualityAsync(CancellationToken token)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var policy = QualityPolicy(session!);
            var lease = new QualityLease(this, policy, QualitySignature(policy));
            lock (qualityLeaseGate) qualityLeases.Add(lease);
            return lease;
        }
        finally { gate.Release(); }
    }

    internal async Task CheckQualityAsync(QualityLease lease, CancellationToken token)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            MusicRules.Require(!lease.Token.IsCancellationRequested && lease.Signature == QualitySignature(session!),
                "QUALITY_POLICY_CHANGED");
        }
        finally { gate.Release(); }
    }
}
