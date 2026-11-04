using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string OfficialSourcesArea = "native-official-sources";
    internal static readonly string[] OfficialSourcePlatforms = ["wy", "tx", "kg"];
    private readonly object officialSourcesGate = new();
    private OfficialSourcesLease? officialSourcesLease;

    private JsonObject OfficialSources(JsonObject doc) =>
        Value(doc, OfficialSourcesArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("enabled", LJ.Arr(OfficialSourcePlatforms)));

    private static void ValidateOfficialSources(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision enabled", "schema revision enabled");
        LJ.Require(LJ.I(settings["schema"]) == 1);
        _ = Revision(settings);
        ValidateOfficialSourceSet(settings["enabled"]);
    }

    private static void ValidateOfficialSourceSet(JsonNode? value)
    {
        var enabled = LJ.Array(value).Select(item => LJ.Text(item, 8)).ToArray();
        LJ.Require(enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(OfficialSourcePlatforms.Contains));
    }

    private (JsonObject Result, bool Mutate) OfficialSourcesCommand(
        string method, JsonObject p, JsonObject doc, CancellationToken token)
    {
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var settings = OfficialSources(doc);
        if (method == "music.officialSources.settings.get")
        {
            LJ.Fields(p, "");
            RefreshOfficialSources(doc);
            return (LJ.Obj(("settings", settings)), false);
        }

        LJ.Require(method == "music.officialSources.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision enabled", "ifRevision enabled");
        long expected = LJ.I(p["ifRevision"]);
        ValidateOfficialSourceSet(p["enabled"]);
        LJ.Require(expected == Revision(settings), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(settings) + 1)),
            ("enabled", p["enabled"]));
        ValidateOfficialSources(next);
        token.ThrowIfCancellationRequested();
        // Revoke before CAS admission, including a write whose receipt is later unknown.
        InvalidateOfficialSources();
        Set(doc, OfficialSourcesArea, next);
        return (LJ.Obj(("settings", next)), true);
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
                LJ.Require(!disposed, "OFFICIAL_SOURCES_CHANGED");
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
                LJ.Require(references > 0, "OFFICIAL_SOURCES_CHANGED");
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
            if (platform is not null) LJ.Require(IsEnabled(platform), "OFFICIAL_SOURCE_DISABLED");
            var current = owner.officialSourcesLease;
            LJ.Require(ReferenceEquals(current, this),
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

    private OfficialSourcesLease RefreshOfficialSources(JsonObject doc)
    {
        var settings = OfficialSources(doc);
        long revision = Revision(settings);
        var enabled = LJ.Array(settings["enabled"]).Select(value => LJ.S(value)).ToArray();
        OfficialSourcesLease? previous;
        OfficialSourcesLease current;
        lock (officialSourcesGate)
        {
            if (officialSourcesLease is { } existing && existing.Matches(revision, enabled)) return existing;
            previous = officialSourcesLease;
            current = officialSourcesLease = new(this, revision, enabled);
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
        await EnterAsync(token).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Native consumers must not use the public authoritative read to implicitly
            // settle an unknown CAS. Only an explicit get/reconcile may reopen admission.
            LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            return RefreshOfficialSources(document!).Retain();
        }
        catch (ProtectedDocumentException error)
        {
            throw new MusicLibraryException(error.Error == DocumentStoreError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE");
        }
        finally { gate.Release(); }
    }
}
