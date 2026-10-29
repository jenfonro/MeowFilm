using System.Diagnostics;

namespace MeowFilm.Media;

/// <summary>Request-driven live windows. No timer, media cache, native calls or alternate player.</summary>
internal sealed class AdaptiveLiveSession
{
    private readonly AdaptiveManifest graph;
    private readonly AdaptiveHttpTransport transport;
    private readonly Uri local;
    private readonly CancellationToken lifetime;
    private readonly TimeSpan timeout;
    private readonly object catalog = new();
    private readonly Dictionary<string, Playlist> playlists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Playlist Playlist, ulong Sequence)> removed = new(StringComparer.Ordinal);
    private readonly Queue<string> removedOrder = new();
    private long recoveryVersion, completedPayloads;
    private Recovery? pendingRecovery;
    internal sealed record Recovery(long Version, string Playlist, ulong First, ulong Last);
    private sealed class Playlist(string path, AdaptiveResource resource)
    {
        internal readonly string Path = path;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal AdaptiveResource Current = resource;
        internal long Next = Stopwatch.GetTimestamp() + Ticks(resource.Window!.Target);
        internal int Failures, Stale, Gaps;
        internal bool Selected;
    }
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
    internal static AdaptiveLiveSession? Create(AdaptiveManifest graph, AdaptiveHttpTransport transport,
        Uri local, TimeSpan timeout, CancellationToken lifetime)
    {
        if (!graph.Resources.Values.Any(r => r.Window is { Ended: false })) return null;
        return new(graph, transport, local, timeout, lifetime);
    }
    private AdaptiveLiveSession(AdaptiveManifest graph, AdaptiveHttpTransport transport,
        Uri local, TimeSpan timeout, CancellationToken lifetime)
    {
        this.graph = graph; this.transport = transport; this.local = local;
        this.timeout = timeout; this.lifetime = lifetime;
        foreach (var (path, resource) in graph.Resources)
            if (resource.Window is { Ended: false })
            {
                if (playlists.Count == 8) throw new PlaybackException(PlaybackError.AdaptiveBudget);
                playlists.Add(path, new(path, resource));
            }
        graph.PrepareLive(); graph.PrepareLiveInitializations(); graph.ReleaseKeyOwners();
    }
    internal async Task<AdaptiveResource?> AcquireAsync(string path, CancellationToken token)
    {
        if (playlists.TryGetValue(path, out var playlist))
            await RefreshAsync(playlist, false, token).ConfigureAwait(false);
        lock (catalog)
        {
            token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested();
            return graph.Resources.TryGetValue(path, out var resource) ? resource.Retain() : null;
        }
    }
    private async Task RefreshAsync(Playlist playlist, bool wait, CancellationToken token)
    {
        await playlist.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = playlist.Current.Window!;
            if (previous.Ended) return;
            long now = Stopwatch.GetTimestamp();
            if (now < playlist.Next)
            {
                if (!wait) return;
                await Task.Delay(TimeSpan.FromSeconds((double)(playlist.Next - now) / Stopwatch.Frequency), token).ConfigureAwait(false);
            }
            long began = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            deadline.CancelAfter(timeout);
            // Mutable playlist reload has no If-Match/If-Range. Every attempt revalidates all
            // redirect hops, starting at the last resolved URL and stripped credential scope.
            var request = new AdaptiveResource(playlist.Current.Uri, playlist.Current.Credentials,
                AdaptiveResourceKind.Manifest) { Independent = true };
            var candidate = new AdaptiveManifest(transport, local, allowLive: true, singleMedia: true);
            byte[]? bytes = null;
            try
            {
                lock (catalog) candidate.SeedLiveKeys(previous.Paths.Select(p => graph.Resources[p]));
                bytes = await transport.ManifestAsync(request, deadline.Token).ConfigureAwait(false);
                Uri root = await candidate.BuildAsync(bytes, request.Uri, request.Credentials, deadline.Token, hlsChild: true).ConfigureAwait(false);
                var next = candidate.Resources[root.AbsolutePath];
                var window = next.Window ?? throw new PlaybackException(PlaybackError.AdaptiveInvalid);
                // A media endpoint cannot transform into a master and discover new authority.
                if (candidate.Resources.Values.Count(r => r.Manifest is not null) != 1)
                    throw new PlaybackException(PlaybackError.AdaptiveInvalid);
                lock (catalog)
                {
                    token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested();
                    Validate(previous, window, candidate);
                    bool advanced = window.Last > previous.Last || window.Ended;
                    if (!advanced && ++playlist.Stale > 8) throw new PlaybackException(PlaybackError.HttpExpired);
                    if (advanced) playlist.Stale = 0;
                    candidate.PrepareLive(previous.Paths.Select(p => graph.Resources[p]));
                    Reconcile(previous, window, candidate);
                    candidate.PrepareLiveInitializations();
                    candidate.RenameLive(root.AbsolutePath, playlist.Path);
                    next = candidate.Resources[playlist.Path];
                    var retired = previous.Paths.Append(playlist.Path).ToArray();
                    foreach (var segment in previous.Segments.Where(s => !next.Window!.Paths.Contains(s.Path)))
                    {
                        if (removed.TryAdd(segment.Path, (playlist, segment.Sequence))) removedOrder.Enqueue(segment.Path);
                        while (removed.Count > 2048) removed.Remove(removedOrder.Dequeue());
                    }
                    graph.PublishLive(candidate, retired);
                    playlist.Current = next;
                    playlist.Failures = 0;
                    playlist.Next = began + Ticks(advanced ? window.Target : window.Target / 2.0);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested &&
                Transient(HttpMediaInput.Classify(error)) && ++playlist.Failures <= 2)
            {
                playlist.Next = began + Ticks(previous.Target / 2.0);
                // Keep the last validated window for at most two scheduled retry attempts.
            }
            finally { if (bytes is not null) Array.Clear(bytes); request.Release(); candidate.Clear(); }
        }
        finally { playlist.Gate.Release(); }
    }
    private static bool Transient(string code) => code is PlaybackError.HttpServerFailed or
        PlaybackError.HttpNetworkFailed or PlaybackError.HttpDeadline or PlaybackError.HttpThrottled;
    private void Validate(HlsWindow old, HlsWindow next, AdaptiveManifest candidate)
    {
        if (old.Target != next.Target || old.Type != next.Type || next.First < old.First ||
            next.Last < old.Last || next.Discontinuity < old.Discontinuity ||
            old.Type == "EVENT" && next.First != old.First || next.Segments.Count > 1024)
            throw new PlaybackException(PlaybackError.AdaptiveInvalid);
        var previous = old.Segments.ToDictionary(s => s.Sequence);
        var oldMap = old.Segments[0].Map;
        var nextMap = next.Segments[0].Map;
        if ((oldMap is null) != (nextMap is null) || oldMap is not null &&
            !AdaptiveManifest.SamePayload(graph.Resources[oldMap], candidate.Resources[nextMap!]))
            throw new PlaybackException(PlaybackError.AdaptiveUnsupported);
        foreach (var segment in next.Segments)
            if (previous.TryGetValue(segment.Sequence, out var before))
            {
                if (before.Discontinuity != segment.Discontinuity || before.Duration != segment.Duration ||
                    !AdaptiveManifest.SamePayload(graph.Resources[before.Path], candidate.Resources[segment.Path]) ||
                    (before.Map is null) != (segment.Map is null) ||
                    before.Map is not null && !AdaptiveManifest.SamePayload(graph.Resources[before.Map], candidate.Resources[segment.Map!]))
                    throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
            }
        if (next.First > old.Last && next.Discontinuity < old.Segments[^1].Discontinuity)
            throw new PlaybackException(PlaybackError.AdaptiveInvalid);
    }
    private void Reconcile(HlsWindow old, HlsWindow next, AdaptiveManifest candidate)
    {
        // Preserve exact local resource URLs and validators for overlapping media and maps.
        var previous = old.Segments.ToDictionary(s => s.Sequence);
        foreach (var segment in next.Segments.ToArray())
            if (previous.TryGetValue(segment.Sequence, out var before))
                candidate.RenameLive(segment.Path, before.Path, graph.Resources[before.Path]);
        foreach (string path in next.Paths.ToArray())
        {
            if (!candidate.Resources.TryGetValue(path, out var resource) || resource.Kind != AdaptiveResourceKind.Mp4Init) continue;
            string? same = old.Paths.FirstOrDefault(p => graph.Resources[p].Kind == AdaptiveResourceKind.Mp4Init &&
                AdaptiveManifest.SamePayload(graph.Resources[p], resource));
            if (same is not null && same != path) candidate.RenameLive(path, same, graph.Resources[same]);
        }
    }
    internal async Task<bool> RecoverExpiredAsync(string path, AdaptiveResource? resource, CancellationToken token)
    {
        Playlist? playlist = null; ulong sequence = 0;
        lock (catalog)
        {
            if (removed.TryGetValue(path, out var tombstone)) (playlist, sequence) = tombstone;
            else foreach (var slot in playlists.Values)
                if (slot.Current.Window!.Segments.FirstOrDefault(s => s.Path == path) is { } segment)
                { playlist = slot; sequence = segment.Sequence; break; }
        }
        if (playlist is null || resource?.Kind == AdaptiveResourceKind.Mp4Init) return false;
        await RefreshAsync(playlist, true, token).ConfigureAwait(false);
        lock (catalog)
        {
            if (sequence >= playlist.Current.Window!.First) return false;
            if (++playlist.Gaps > 2) throw new PlaybackException(PlaybackError.HttpExpired);
            var window = playlist.Current.Window!;
            if (pendingRecovery is null || pendingRecovery.Playlist != playlist.Path || pendingRecovery.First != window.First)
                pendingRecovery = new(checked(++recoveryVersion), playlist.Path, window.First, window.Last);
            return true; // Not alternate bytes: libVLC receives a real gap and reloads its stable playlist URL.
        }
    }
    internal void PayloadCompleted(string path)
    {
        lock (catalog)
            foreach (var playlist in playlists.Values)
                if (playlist.Current.Window!.Segments.Any(s => s.Path == path))
                {
                    playlist.Selected = true; playlist.Gaps = 0;
                    completedPayloads = checked(completedPayloads + 1);
                }
    }
    internal bool HasOpenWindow
    {
        get
        {
            lock (catalog)
            {
                bool selected = playlists.Values.Any(p => p.Selected);
                return playlists.Values.Any(p => (!selected || p.Selected) && !p.Current.Window!.Ended);
            }
        }
    }
    internal Recovery? PendingRecovery { get { lock (catalog) return pendingRecovery; } }
    internal long CompletedPayloads { get { lock (catalog) return completedPayloads; } }
    internal void Recovered(Recovery ticket)
    {
        lock (catalog)
            if (pendingRecovery?.Version == ticket.Version) pendingRecovery = null;
    }
    internal void Clear()
    {
        // Session joins every handler (including refresh/retired payload holders) first.
        foreach (var playlist in playlists.Values) playlist.Gate.Dispose();
        playlists.Clear(); removed.Clear(); removedOrder.Clear(); pendingRecovery = null;
    }
}
