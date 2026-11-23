using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal static readonly string[] OfficialSourcePlatforms = ["wy", "tx", "kg"];
    private static readonly MusicOfficialSourcesRecord DefaultOfficialSources =
        new(0, OfficialSourcePlatforms, OfficialSourcePlatforms);
    private readonly object officialSourcesGate = new();
    private OfficialSourcesLease? officialSourcesLease;

    private static MusicOfficialSourcesRecord OfficialSources(MusicLibrarySession doc)
    {
        var value = doc.ReadOfficialSources() ?? DefaultOfficialSources;
        try { ValidateOfficialSources(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateOfficialSources(MusicOfficialSourcesRecord settings)
    {
        MusicRules.Require(settings.Revision is >= 0 and <= MusicRules.MaxInteger);
        ValidateOfficialSourceSet(settings.Enabled);
    }

    private static void ValidateOfficialSourceSet(string[] enabled)
    {
        MusicRules.Require(enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(OfficialSourcePlatforms.Contains));
    }

    private (JsonElement Result, bool Mutate) OfficialSourcesCommand(
        string method, JsonElement p, MusicLibrarySession doc, CancellationToken token)
    {
        MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var settings = OfficialSources(doc);
        if (method == "music.officialSources.settings.get")
        {
            RefreshOfficialSources(doc);
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteOfficialSources(w, settings);
            }), false);
        }

        MusicRules.Require(method == "music.officialSources.settings.save", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, "ifRevision enabled");
        long expected = MusicWireCodec.RequireIntProp(p, "ifRevision");
        string[] enabled = MusicWireCodec.RequireIdArrayProp(p, "enabled", 8);
        ValidateOfficialSourceSet(enabled);
        MusicRules.Require(expected == settings.Revision, "REVISION_CONFLICT");
        var next = new MusicOfficialSourcesRecord(checked(settings.Revision + 1), OfficialSourcePlatforms, enabled);
        ValidateOfficialSources(next);
        token.ThrowIfCancellationRequested();
        // Revoke before CAS admission, including a write whose receipt is later unknown.
        InvalidateOfficialSources();
        doc.SetOfficialSources(next);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteOfficialSources(w, next);
        }), true);
    }

    // A generation is shared by readers until the sole owner changes/reconciles the policy.
    // Its immutable token remains safe to observe after the owner disposes the revoked CTS.
    internal sealed class OfficialSourcesLease : IDisposable
    {
        private readonly MusicLibraryOwner owner;
        private readonly string[] enabled;
        private readonly CancellationTokenSource revoked = new();
        private readonly object lifetimeGate = new();
        private int references;
        private bool retired, revoking, disposed;
        internal OfficialSourcesLease(MusicLibraryOwner owner, long revision, string[] enabled)
        { this.owner = owner; Revision = revision; this.enabled = enabled; Token = revoked.Token; }
        internal long Revision { get; }
        internal CancellationToken Token { get; }
        internal bool IsEnabled(string platform) => enabled.Contains(platform, StringComparer.Ordinal);
        internal bool Matches(long revision, string[] platforms) =>
            Revision == revision && enabled.SequenceEqual(platforms, StringComparer.Ordinal);
        internal OfficialSourcesLease Retain()
        {
            lock (lifetimeGate)
            {
                MusicRules.Require(!disposed, "OFFICIAL_SOURCES_CHANGED");
                references++;
                return this;
            }
        }
        internal void Revoke()
        {
            lock (lifetimeGate)
            {
                if (retired) return;
                retired = true;
                revoking = true;
            }
            try { revoked.Cancel(); }
            catch (AggregateException) { /* All linked official operations are still revoked. */ }
            finally
            {
                lock (lifetimeGate) revoking = false;
                DisposeIfUnused();
            }
        }
        public void Dispose()
        {
            lock (lifetimeGate)
            {
                MusicRules.Require(references > 0, "OFFICIAL_SOURCES_CHANGED");
                references--;
            }
            DisposeIfUnused();
        }
        private void DisposeIfUnused()
        {
            bool release;
            lock (lifetimeGate)
            {
                release = retired && !revoking && references == 0 && !disposed;
                if (release) disposed = true;
            }
            if (release) revoked.Dispose();
        }
        private void RequireCurrent(string? platform)
        {
            if (platform is not null) MusicRules.Require(IsEnabled(platform), "OFFICIAL_SOURCE_DISABLED");
            var current = owner.officialSourcesLease;
            MusicRules.Require(ReferenceEquals(current, this),
                platform is not null && current is not null && !current.IsEnabled(platform)
                    ? "OFFICIAL_SOURCE_DISABLED" : "OFFICIAL_SOURCES_CHANGED");
        }
        internal void Check(string? platform = null)
        { lock (owner.officialSourcesGate) RequireCurrent(platform); }
        internal T Publish<T>(string? platform, Func<T> result)
        {
            // Final validation and publication cannot straddle policy revocation.
            lock (owner.officialSourcesGate) { RequireCurrent(platform); return result(); }
        }
    }

    private OfficialSourcesLease RefreshOfficialSources(MusicLibrarySession doc)
    {
        var settings = OfficialSources(doc);
        long rev = settings.Revision;
        var enabled = settings.Enabled;
        OfficialSourcesLease? previous;
        OfficialSourcesLease current;
        lock (officialSourcesGate)
        {
            if (officialSourcesLease is { } existing && existing.Matches(rev, enabled)) return existing;
            previous = officialSourcesLease;
            current = officialSourcesLease = new(this, rev, enabled);
        }
        previous?.Revoke();
        return current;
    }

    private void InvalidateOfficialSources()
    {
        OfficialSourcesLease? previous;
        lock (officialSourcesGate) { previous = officialSourcesLease; officialSourcesLease = null; }
        previous?.Revoke();
    }

    internal bool IsOfficialSourceEnabled(string platform)
    {
        lock (officialSourcesGate) return officialSourcesLease?.IsEnabled(platform) == true;
    }

    internal async Task<OfficialSourcesLease> AcquireOfficialSourcesAsync(CancellationToken token)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Native consumers must not use the public authoritative read to implicitly
            // settle an unknown CAS. Only an explicit get/reconcile may reopen admission.
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            return RefreshOfficialSources(session!).Retain();
        }
        catch (StorageException error)
        {
            throw new MusicLibraryException(error.Error == StorageError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE");
        }
        finally { gate.Release(); }
    }
}
