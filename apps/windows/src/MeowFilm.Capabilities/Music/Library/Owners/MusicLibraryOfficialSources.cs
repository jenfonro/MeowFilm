using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal static readonly string[] OfficialSourcePlatforms = ["wy", "tx", "kg"];
    private static readonly MusicOfficialSourcesRecord DefaultOfficialSources =
        new(0, OfficialSourcePlatforms, OfficialSourcePlatforms);
    private readonly object officialSourcesGate = new();
    private OfficialSourcesLease? officialSourcesLease;

    private static MusicOfficialSourcesRecord OfficialSources(MusicLibrarySession doc) =>
        doc.ReadOfficialSources() ?? DefaultOfficialSources;

    private static void ValidateOfficialSourceSet(string[] enabled)
    {
        MusicRules.Require(enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(OfficialSourcePlatforms.Contains));
    }

    public Task<MusicOfficialSourcesRecord> GetOfficialSourcesAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var settings = OfficialSources(doc);
            RefreshOfficialSources(doc);
            return settings;
        }, cancellationToken);

    public async Task<MusicOfficialSourcesRecord> SaveOfficialSourcesAsync(
        long ifRevision,
        string[] enabled,
        CancellationToken cancellationToken = default)
    {
        bool mutated = false;
        var saved = await RunLibraryWriteAsync(doc =>
        {
            var settings = OfficialSources(doc);
            var nextEnabled = enabled.ToArray();
            ValidateOfficialSourceSet(nextEnabled);
            MusicRules.Require(ifRevision == settings.Revision, "REVISION_CONFLICT");
            var next = new MusicOfficialSourcesRecord(checked(settings.Revision + 1), OfficialSourcePlatforms, nextEnabled);
            cancellationToken.ThrowIfCancellationRequested();
            InvalidateOfficialSources();
            doc.SetOfficialSources(next);
            return (next, true);
        }, cancellationToken, onCommitted: () => mutated = true, refreshOfficialSources: true).ConfigureAwait(false);
        if (mutated) InvokeSafe(OfficialSourcesChanged, saved);
        return saved;
    }

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
            catch (AggregateException) { }
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
            return RefreshOfficialSources(session!).Retain();
        }
        catch (StorageException error)
        {
            throw new MusicLibraryException(error.Error == StorageError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE");
        }
        finally { gate.Release(); }
    }
}
