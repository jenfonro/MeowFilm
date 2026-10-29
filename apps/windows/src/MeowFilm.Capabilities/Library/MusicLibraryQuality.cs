using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string QualityPolicyArea = "native-quality-policy";
    private readonly object qualityLeaseGate = new();
    private readonly HashSet<QualityLease> qualityLeases = [];

    private JsonObject QualityPolicy(JsonObject doc)
    {
        var policy = Value(doc, QualityPolicyArea) as JsonObject ?? MusicQualityPolicy.Default();
        MusicQualityPolicy.Validate(policy);
        return policy;
    }
    private static void ValidateQualityPolicy(JsonObject policy) => MusicQualityPolicy.Validate(policy);
    private string QualitySignature(JsonObject doc) => LJ.Digest(LJ.Obj(
        ("contract", "music.quality.v1"), ("scope", scope), ("policy", QualityPolicy(doc)), ("online", Settings(doc))));
    private JsonObject QualityReply(JsonObject doc) => LJ.Obj(("settings", QualityPolicy(doc)),
        ("signature", QualitySignature(doc)), ("onlineSettings", Settings(doc)));

    private async Task<(JsonObject Result, bool Mutate)> QualityCommandAsync(
        string method, JsonObject p, JsonObject doc, CancellationToken token)
    {
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        if (method == "music.quality.settings.get")
        {
            LJ.Fields(p, "");
            return (QualityReply(doc), false);
        }
        LJ.Require(method == "music.quality.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision request qualityOrder qualityEnabled spatialOrder spatialEnabled",
            "ifRevision request qualityOrder qualityEnabled spatialOrder spatialEnabled");
        var current = QualityPolicy(doc);
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(current), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(current) + 1)), ("request", p["request"]),
            ("qualityOrder", p["qualityOrder"]), ("qualityEnabled", p["qualityEnabled"]),
            ("spatialOrder", p["spatialOrder"]), ("spatialEnabled", p["spatialEnabled"]));
        MusicQualityPolicy.Validate(next);
        token.ThrowIfCancellationRequested();
        RevokeQualityLeases(); // Fail closed even when the admitted CAS receipt is unknown.
        Set(doc, QualityPolicyArea, next);
        return (QualityReply(doc), true);
    }

    internal sealed class QualityLease : IDisposable
    {
        private readonly MusicLibraryOwner owner;
        private readonly object sync = new();
        private readonly CancellationTokenSource revoked = new();
        private bool disposed;
        internal QualityLease(MusicLibraryOwner owner, JsonObject policy, string signature)
        { this.owner = owner; Policy = policy; Signature = signature; Token = revoked.Token; }
        internal JsonObject Policy { get; }
        internal string Signature { get; }
        internal CancellationToken Token { get; }
        internal void Revoke()
        {
            lock (sync)
            {
                if (disposed) return;
                try { revoked.Cancel(); } catch (AggregateException) { /* Every registered operation is still revoked. */ }
            }
        }
        public void Dispose()
        {
            lock (owner.qualityLeaseGate) owner.qualityLeases.Remove(this);
            lock (sync) { if (disposed) return; disposed = true; revoked.Dispose(); }
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
        await EnterAsync(token).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var lease = new QualityLease(this, QualityPolicy(document!), QualitySignature(document!));
            lock (qualityLeaseGate)
            {
                if (qualityLeases.Count >= 24) { lease.Dispose(); throw new MusicLibraryException("LIMIT"); }
                qualityLeases.Add(lease);
            }
            return lease;
        }
        finally { gate.Release(); }
    }
    internal async Task CheckQualityAsync(QualityLease lease, CancellationToken token)
    {
        await EnterAsync(token).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            LJ.Require(!lease.Token.IsCancellationRequested && lease.Signature == QualitySignature(document!),
                "QUALITY_POLICY_CHANGED");
        }
        finally { gate.Release(); }
    }
}
