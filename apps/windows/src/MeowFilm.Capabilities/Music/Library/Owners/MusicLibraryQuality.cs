namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private readonly object qualityLeaseGate = new();
    private readonly HashSet<QualityLease> qualityLeases = [];

    private static MusicQualityPolicyRecord QualityPolicy(MusicLibrarySession doc)
    {
        var policy = doc.ReadQualityPolicy() ?? MusicQualityPolicy.Default();
        try { MusicQualityPolicy.Validate(policy); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return policy;
    }

    private string QualitySignature(MusicQualityPolicyRecord policy) =>
        MusicRules.CanonicalDigest(MusicNodeSongCodec.BuildObject(w =>
        {
            w.WriteString("contract", "music.quality.v1");
            w.WriteString("scope", scope);
            w.WritePropertyName("policy");
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteNumber("revision", policy.Revision);
            w.WriteString("request", policy.Request);
            MusicNodeSongCodec.WriteStringArray(w, "qualityOrder", policy.QualityOrder);
            MusicNodeSongCodec.WriteStringArray(w, "qualityEnabled", policy.QualityEnabled);
            MusicNodeSongCodec.WriteStringArray(w, "spatialOrder", policy.SpatialOrder);
            MusicNodeSongCodec.WriteStringArray(w, "spatialEnabled", policy.SpatialEnabled);
            w.WriteEndObject();
        }));

    private string QualitySignature(MusicLibrarySession doc) =>
        QualitySignature(QualityPolicy(doc));

    public Task<MusicQualitySettingsResult> GetQualityPolicyAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var current = QualityPolicy(doc);
            return new MusicQualitySettingsResult(current, QualitySignature(current));
        }, cancellationToken, authoritativeFor: PendingOwner.Quality);

    public Task<MusicQualitySettingsResult> SaveQualityPolicyAsync(
        long ifRevision,
        string request,
        string[] qualityOrder,
        string[] qualityEnabled,
        string[] spatialOrder,
        string[] spatialEnabled,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Quality, doc =>
        {
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
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
            RevokeQualityLeases(); // Fail closed even when the admitted CAS receipt is unknown.
            doc.SetQualityPolicy(next);
            return (new MusicQualitySettingsResult(next, QualitySignature(next)), true);
        }, cancellationToken);

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
            catch (AggregateException) { /* Every registered operation is still revoked. */ }
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
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var policy = QualityPolicy(session!);
            var lease = new QualityLease(this, policy, QualitySignature(policy));
            lock (qualityLeaseGate)
            {
                qualityLeases.Add(lease);
            }
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
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            MusicRules.Require(!lease.Token.IsCancellationRequested && lease.Signature == QualitySignature(session!),
                "QUALITY_POLICY_CHANGED");
        }
        finally { gate.Release(); }
    }
}
