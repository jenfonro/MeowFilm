using System.Text;

namespace MeowFilm.Media;

internal sealed record HlsSegment(ulong Sequence, ulong Discontinuity, decimal Duration, string Path, string? Map);
internal sealed record HlsWindow(ulong First, ulong Discontinuity, long Target, string? Type, bool Ended,
    List<HlsSegment> Segments, HashSet<string> Paths)
{
    internal ulong Last => Segments[^1].Sequence;
}

internal sealed partial class AdaptiveManifest
{
    // Called under AdaptiveLiveSession's catalog lock, before exposing any resource.
    internal void PrepareLive(IEnumerable<AdaptiveResource>? previous = null)
    {
        var states = new Dictionary<string, AdaptiveResource>(StringComparer.Ordinal);
        if (previous is not null)
            foreach (var resource in previous.Where(r => r.Kind != AdaptiveResourceKind.Manifest))
                states.TryAdd(RepresentationIdentity(resource), resource.Representation ?? resource);
        foreach (var resource in _resources.Values)
        {
            resource.Independent = true;
            if (resource.Kind == AdaptiveResourceKind.Manifest) continue;
            string identity = RepresentationIdentity(resource);
            if (states.TryGetValue(identity, out var state) && !ReferenceEquals(resource, state))
                resource.Representation = state.Retain();
            else states.TryAdd(identity, resource);
        }
        foreach (var key in _keys.Values) key.SetIndependent();
    }
    internal void PrepareLiveInitializations()
    {
        foreach (var window in _resources.Values.Select(r => r.Window).OfType<HlsWindow>())
        {
            var maps = window.Segments.Where(s => s.Map is not null).Select(s => _resources[s.Map!]).ToArray();
            if (maps.Length != 0 && maps.Any(m => !SamePayload(maps[0], m)))
                throw new PlaybackException(PlaybackError.AdaptiveUnsupported);
            for (int i = 1; i < window.Segments.Count; i++)
            {
                var segment = window.Segments[i];
                var payload = _resources[segment.Path];
                if (segment.Map is not null && segment.Discontinuity != window.Segments[i - 1].Discontinuity &&
                    payload.LiveInitialization is null)
                    payload.LiveInitialization = _resources[segment.Map].Retain();
            }
        }
    }
    private static string RepresentationIdentity(AdaptiveResource resource) =>
        resource.OriginalUri.AbsoluteUri;

    internal void SeedLiveKeys(IEnumerable<AdaptiveResource> resources)
    {
        foreach (var resource in resources)
            if (resource.Encryption?.Key is { } key && !_keys.ContainsKey(key.Identity))
                _keys.Add(key.Identity, key.Retain());
    }
    internal void ReleaseKeyOwners()
    {
        foreach (var key in _keys.Values) key.Clear();
        _keys.Clear(); // Resource and admitted-handler references now own the keys.
    }
    internal void RenameLive(string from, string to, AdaptiveResource? reuse = null)
    {
        var resource = _resources[from];
        _resources.Remove(from);
        if (reuse is not null) { resource.Release(); resource = reuse.Retain(); }
        _resources.Add(to, resource);
        foreach (var entry in _resources.Values)
        {
            if (entry.Manifest is { } bytes)
            {
                string old = new Uri(localOrigin, from).AbsoluteUri, replacement = new Uri(localOrigin, to).AbsoluteUri;
                entry.Manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(old, replacement, StringComparison.Ordinal));
                Array.Clear(bytes);
            }
            if (entry.Window is { } window)
            {
                if (window.Paths.Remove(from)) window.Paths.Add(to);
                for (int i = 0; i < window.Segments.Count; i++)
                {
                    var s = window.Segments[i];
                    window.Segments[i] = s with { Path = s.Path == from ? to : s.Path, Map = s.Map == from ? to : s.Map };
                }
            }
        }
    }
    internal void PublishLive(AdaptiveManifest candidate, IEnumerable<string> retired)
    {
        string[] paths = retired.ToArray();
        int count = _resources.Count - paths.Length + candidate._resources.Count;
        long bytes = _resources.Where(p => !paths.Contains(p.Key, StringComparer.Ordinal)).Sum(p => p.Value.Manifest?.LongLength ?? 0) +
            candidate._resources.Values.Sum(r => r.Manifest?.LongLength ?? 0);
        if (count > 4096 || bytes > 4 * MaximumBytes) throw new PlaybackException(PlaybackError.AdaptiveBudget);
        foreach (string path in paths)
            if (_resources.Remove(path, out var old)) old.Release();
        foreach (var (path, resource) in candidate._resources) _resources.Add(path, resource.Retain());
    }
    internal static bool SamePayload(AdaptiveResource a, AdaptiveResource b) =>
        a.OriginalUri == b.OriginalUri &&
        a.Kind == b.Kind && a.Offset == b.Offset && a.Count == b.Count &&
        (a.Encryption is null && b.Encryption is null ||
         a.Encryption is { } ae && b.Encryption is { } be && ae.Key.Identity == be.Key.Identity &&
         ae.IV.AsSpan().SequenceEqual(be.IV));
}
