using System.Globalization;
using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal enum PlatformSongShape { Page, Catalog, Discovery, None }
    private readonly object repositoryGate = new();
    private readonly Dictionary<string, HashSet<string>> failedArtwork = new(StringComparer.Ordinal);
    private int failedArtworkCount;
    private readonly Dictionary<(string SongId, string Platform), long> songObservations = new();
    private long songObservation, songPublication;
    private readonly object snapshotGate = new();
    private int snapshotPending;
    private bool snapshotSealed;
    private TaskCompletionSource snapshotDrained = CompletedSnapshots();

    internal sealed record SongSnapshotBatch(long Publication, IReadOnlyDictionary<string, MusicSongRecord?> Songs);

    private static TaskCompletionSource CompletedSnapshots()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }

    internal async Task<SongSnapshotBatch> SongSnapshotsAsync(IEnumerable<string> ids, CancellationToken token = default)
    {
        string[] requested;
        lock (snapshotGate)
        {
            MusicRules.Require(!snapshotSealed, "CLOSED");
            requested = ids.Distinct(StringComparer.Ordinal).ToArray();
            if (snapshotPending++ == 0) snapshotDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            token.ThrowIfCancellationRequested();
            MusicSongSession? snapshotSession;
            long publication;
            lock (repositoryGate)
            {
                MusicRules.Require(!closed && !preparing, "CLOSED");
                snapshotSession = visibleSongs;
                publication = songPublication;
            }
            var songs = new Dictionary<string, MusicSongRecord?>(requested.Length, StringComparer.Ordinal);
            if (snapshotSession is not null && requested.Length > 0)
            {
                token.ThrowIfCancellationRequested();
                var loaded = snapshotSession.ReadBatch(requested);
                foreach (string id in requested)
                {
                    token.ThrowIfCancellationRequested();
                    songs[id] = loaded.TryGetValue(id, out var song) && song is not null
                        ? NativeMusicSong.Normalize(song)
                        : null;
                }
            }
            else
            {
                foreach (string id in requested) songs[id] = null;
            }
            return new SongSnapshotBatch(publication, songs);
        }
        finally { lock (snapshotGate) if (--snapshotPending == 0) snapshotDrained.TrySetResult(); }
    }

    private Task SealSongSnapshots()
    {
        lock (snapshotGate) { snapshotSealed = true; return snapshotDrained.Task; }
    }

    internal event Action<IReadOnlySet<string>>? SongMetadataChanged;
    internal long BeginSongObservation() => Interlocked.Increment(ref songObservation);

    internal MusicSongRecord? ReadSongRecord(string id)
    {
        MusicSongSession? snapshotSession;
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            snapshotSession = visibleSongs;
        }
        return snapshotSession?.Read(id) is { } song ? NativeMusicSong.Normalize(song) : null;
    }

    internal async Task ArtworkLoadCompletedAsync(string address, bool succeeded)
    {
        if (succeeded && Volatile.Read(ref failedArtworkCount) == 0) return;
        await EnterAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (closed || preparing) return;
            if (succeeded && failedArtwork.Count == 0) return;
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (recommendationUnknown is null && pending is { Owner: PendingOwner.SongMetadata })
                await ReconcileLockedAsync(CancellationToken.None).ConfigureAwait(false);
            if (pending is not null || recommendationUnknown is not null) return;
            var ids = session!.Songs.Artwork(address);
            if (ids.Count == 0) return;
            if (succeeded)
            {
                foreach (string id in ids) failedArtwork.Remove(id);
                Volatile.Write(ref failedArtworkCount, failedArtwork.Count);
                return;
            }
            var candidate = session.Candidate(true);
            var policy = MetadataPolicy(candidate);
            var platforms = MetadataOrder(policy);
            var enabled = policy.Enabled.ToHashSet(StringComparer.Ordinal);
            bool changed = false;
            foreach (string id in ids)
            {
                var song = candidate.Songs.Read(id)!;
                if (!failedArtwork.TryGetValue(id, out var failed))
                {
                    failedArtwork[id] = failed = new(StringComparer.Ordinal);
                    Volatile.Write(ref failedArtworkCount, failedArtwork.Count);
                }
                failed.Add(address);
                var next = MusicArtworkPolicy.Sources(song, platforms, enabled)
                    .FirstOrDefault(source => !failed.Contains(source.Address));
                if (next.Address is null) continue;
                candidate.Songs.Set(song with
                {
                    ArtworkSourceKey = next.SourceKey,
                    ArtworkSourceImage = next.Address,
                    CoverUrl = next.Address
                });
                changed = true;
            }
            if (changed) await CommitAsync(candidate, PendingOwner.SongMetadata, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private void NotifySongMetadataChanged(IReadOnlySet<string> ids)
    {
        if (SongMetadataChanged is not { } changed) return;
        foreach (Action<IReadOnlySet<string>> observer in changed.GetInvocationList())
            try { observer(ids); } catch { /* An observer cannot reverse the durable receipt. */ }
    }

    private void AcceptSongObservations(IReadOnlyDictionary<(string SongId, string Platform), long>? accepted)
    {
        if (accepted is null) return;
        foreach (var pair in accepted)
            songObservations[pair.Key] = Math.Max(songObservations.GetValueOrDefault(pair.Key), pair.Value);
    }

    private static MusicSongRecord SongReference(MusicLibrarySession doc, string id)
    {
        var song = doc.Songs.Read(id) ?? throw new MusicLibraryException("SONG_NOT_FOUND");
        MusicRules.Require(song.SongId == id, "INVALID_SONG_ID");
        return NativeMusicSong.Normalize(song);
    }

    private static MusicSongRecord EntrySong(MusicLibrarySession doc, MusicQueueEntryRecord entry) =>
        SongReference(doc, entry.SongId);

    private MusicSongRecord[] RegisterSongs(MusicLibrarySession doc, IEnumerable<MusicSongRecord> input,
        bool authoritative = false, long observation = 0,
        Dictionary<(string SongId, string Platform), long>? accepted = null)
    {
        var policy = MetadataPolicy(doc);
        var platforms = MetadataOrder(policy);
        var enabled = policy.Enabled.ToHashSet(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var song in input)
        {
            var incoming = NativeMusicSong.Normalize(song);
            string? existing = doc.Songs.Find(incoming);
            var observedPlatforms = incoming.Sources.Select(s => s.Platform).Distinct(StringComparer.Ordinal).ToArray();
            if (authoritative && existing is not null &&
                observedPlatforms.Any(platform => songObservations.GetValueOrDefault((existing, platform)) > observation))
            {
                ids.Add(existing);
                continue;
            }
            var record = new NativeMusicSong.Recordings(doc.Songs.Register(incoming, existing, authoritative));
            record.SelectArtwork(platforms, enabled);
            doc.Songs.Set(record.ToSong());
            ids.Add(record.Id);
            if (authoritative && accepted is not null)
                foreach (string platform in observedPlatforms) accepted[(record.Id, platform)] = observation;
        }
        if (doc.Songs.Changes.Count > 0 && doc.ReadLibrary() is null)
            doc.SetLibrary(LibraryState(doc));
        return ids.Select(id => doc.Songs.Read(id)!).ToArray();
    }

    private void ApplyArtworkPolicy(MusicLibrarySession doc, bool force = false)
    {
        if (doc.ReadLibrary() is null) return;
        var policy = MetadataPolicy(doc);
        var platforms = MetadataOrder(policy);
        var enabled = policy.Enabled.ToHashSet(StringComparer.Ordinal);
        foreach (string id in doc.Songs.Ids())
        {
            var record = new NativeMusicSong.Recordings(doc.Songs.Read(id)!);
            record.SelectArtwork(platforms, enabled, force);
            doc.Songs.Set(record.ToSong());
        }
    }

    private static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value)) return true;
        value = default;
        return false;
    }

    private static string? OptionalStr(JsonElement obj, string key) =>
        TryGet(obj, key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Str(JsonElement obj, string key) => OptionalStr(obj, key) ?? "";

    private static double Num(JsonElement value, double fallback = 0) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d
        : fallback;

    // Only adoption is conditional on a real recording identity. Display containers
    // are never rewritten or rejected by the permanent song repository.
    private static MusicSongRecord? PlatformRecording(JsonElement song)
    {
        if (song.ValueKind != JsonValueKind.Object) return null;
        var validSources = new List<MusicSongSourceRecord>();
        if (TryGet(song, "sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var srcEl in sourcesEl.EnumerateArray())
            {
                if (srcEl.ValueKind != JsonValueKind.Object) continue;
                if (!TryGet(srcEl, "musicInfo", out var infoEl) || infoEl.ValueKind != JsonValueKind.Object) continue;
                string platform = Str(srcEl, "source");
                string sourceKey = Str(srcEl, "sourceKey");
                if (!NativeMusicSong.Platforms.Contains(platform)) continue;
                var src = MusicNodeSongCodec.ReadSourceFromMusicInfo(sourceKey, platform, infoEl);
                if (src.InfoSource != platform || NativeMusicSong.InfoId(src).Length == 0 ||
                    sourceKey != NativeMusicSong.InfoKey(src))
                    continue;
                validSources.Add(NativeMusicSong.DurableSource(src));
            }
        }
        string? localFileId = TryGet(song, "localFileId", out var lf) && lf.ValueKind == JsonValueKind.String
            ? lf.GetString() : null;
        if (validSources.Count == 0 && string.IsNullOrEmpty(localFileId)) return null;

        string[] artists = TryGet(song, "artistNames", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array
            ? artistsEl.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String && item.GetString() is not null).Select(item => item.GetString()!).ToArray()
            : [];
        double? duration = NativeMusicSong.ValidDuration(TryGet(song, "duration", out var dEl) ? Num(dEl, double.NaN) : null);
        double? baseline = NativeMusicSong.ValidDuration(TryGet(song, "baselineDuration", out var bEl) ? Num(bEl, double.NaN) : null) ?? duration;

        var record = new MusicSongRecord(
            Str(song, "songId"),
            Str(song, "title"),
            Str(song, "artist"),
            Str(song, "album"),
            duration,
            baseline,
            NativeMusicSong.PublicUrl(Str(song, "cover_url")),
            localFileId,
            OptionalStr(song, "artworkSourceKey"),
            OptionalStr(song, "artworkSourceImage"),
            artists,
            validSources.ToArray());
        return NativeMusicSong.Normalize(record);
    }

    internal MusicSongRecord ProjectSong(MusicSongRecord song)
    {
        MusicSongSession? snapshotSession;
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            snapshotSession = visibleSongs;
        }
        if (snapshotSession is null) return song;
        string? id = snapshotSession.Registered(song) ?? snapshotSession.Find(song);
        return id is null ? song : NativeMusicSong.Normalize(snapshotSession.Read(id)!);
    }

    internal MusicSongRecord[] ProjectSongs(IReadOnlyList<MusicSongRecord> songs)
    {
        MusicSongSession? snapshotSession;
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            snapshotSession = visibleSongs;
        }
        var result = new MusicSongRecord[songs.Count];
        if (snapshotSession is null)
        {
            for (int i = 0; i < songs.Count; i++) result[i] = songs[i];
            return result;
        }
        for (int i = 0; i < songs.Count; i++)
        {
            var song = songs[i];
            string? id = snapshotSession.Registered(song) ?? snapshotSession.Find(song);
            result[i] = id is null ? song : NativeMusicSong.Normalize(snapshotSession.Read(id)!);
        }
        return result;
    }

    internal async Task<JsonElement> AdoptPlatformResultAsync(JsonElement result, CancellationToken token,
        bool authoritative = true, long observation = 0, PlatformSongShape shape = PlatformSongShape.Page)
    {
        token.ThrowIfCancellationRequested();
        if (shape == PlatformSongShape.None || result.ValueKind != JsonValueKind.Object) return result.Clone();

        var recordings = new List<MusicSongRecord>();
        CollectPlatformRecordings(result, shape, recordings);
        if (recordings.Count == 0) return result.Clone();

        if (authoritative && observation == 0) observation = BeginSongObservation();
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            if (recommendationUnknown is null && pending is { Owner: PendingOwner.SongMetadata })
                await ReconcileLockedAsync(token).ConfigureAwait(false);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var candidate = session!.Candidate(true);
            var accepted = new Dictionary<(string SongId, string Platform), long>();
            var adopted = RegisterSongs(candidate, recordings, authoritative, observation, accepted);
            token.ThrowIfCancellationRequested();
            await CommitAsync(candidate, PendingOwner.SongMetadata, token, accepted).ConfigureAwait(false);

            int index = 0;
            return RewritePlatformResult(result, shape, el =>
                PlatformRecording(el) is null ? null : adopted[index++]);
        }
        finally { gate.Release(); }
    }

    internal async Task<MusicSongRecord> AdoptSongAsync(MusicSongRecord song, CancellationToken token,
        bool authoritative = false, long observation = 0)
    {
        token.ThrowIfCancellationRequested();
        var normalized = NativeMusicSong.Normalize(song);
        if (authoritative && observation == 0) observation = BeginSongObservation();
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            if (recommendationUnknown is null && pending is { Owner: PendingOwner.SongMetadata })
                await ReconcileLockedAsync(token).ConfigureAwait(false);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var candidate = session!.Candidate(true);
            var accepted = authoritative ? new Dictionary<(string SongId, string Platform), long>() : null;
            var adopted = RegisterSongs(candidate, [normalized], authoritative: authoritative, observation: observation, accepted: accepted);
            token.ThrowIfCancellationRequested();
            await CommitAsync(candidate, PendingOwner.SongMetadata, token, accepted).ConfigureAwait(false);
            return adopted[0];
        }
        finally { gate.Release(); }
    }

    private static void CollectPlatformRecordings(JsonElement root, PlatformSongShape shape, List<MusicSongRecord> target)
    {
        void VisitSong(JsonElement el)
        {
            if (PlatformRecording(el) is { } rec) target.Add(rec);
        }
        void VisitItems(JsonElement container, string name, bool cards)
        {
            if (!TryGet(container, name, out var rows) || rows.ValueKind != JsonValueKind.Array) return;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!cards) { VisitSong(row); continue; }
                if (TryGet(row, "song", out var s)) VisitSong(s);
                if (TryGet(row, "preview", out var preview) && preview.ValueKind == JsonValueKind.Array)
                {
                    int taken = 0;
                    foreach (var entry in preview.EnumerateArray())
                    {
                        if (taken++ >= 3) break;
                        if (entry.ValueKind == JsonValueKind.Object && TryGet(entry, "song", out var nested))
                            VisitSong(nested);
                    }
                }
            }
        }

        switch (shape)
        {
            case PlatformSongShape.Page:
                VisitItems(root, "items", false);
                break;
            case PlatformSongShape.Catalog:
                if (TryGet(root, "sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (section.ValueKind != JsonValueKind.Object) continue;
                        VisitItems(section, "items", true);
                        if (TryGet(section, "tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
                            foreach (var tab in tabs.EnumerateArray())
                                if (tab.ValueKind == JsonValueKind.Object)
                                    VisitItems(tab, "items", true);
                    }
                break;
            case PlatformSongShape.Discovery:
                if (TryGet(root, "charts", out var charts) && charts.ValueKind == JsonValueKind.Array)
                    foreach (var chart in charts.EnumerateArray())
                        if (chart.ValueKind == JsonValueKind.Object)
                            VisitItems(chart, "songs", false);
                break;
        }
    }

    private static void WriteMergedSong(Utf8JsonWriter w, JsonElement original, MusicSongRecord? replacement)
    {
        if (replacement is null || original.ValueKind != JsonValueKind.Object) { original.WriteTo(w); return; }
        w.WriteStartObject();
        foreach (var prop in original.EnumerateObject())
        {
            if (prop.NameEquals("songId"u8) || prop.NameEquals("title"u8) || prop.NameEquals("artist"u8) ||
                prop.NameEquals("album"u8) || prop.NameEquals("duration"u8) || prop.NameEquals("baselineDuration"u8) ||
                prop.NameEquals("cover_url"u8) || prop.NameEquals("sources"u8) ||
                prop.NameEquals("localFileId"u8) && replacement.LocalFileId is not null ||
                prop.NameEquals("artworkSourceKey"u8) && replacement.ArtworkSourceKey is not null ||
                prop.NameEquals("artworkSourceImage"u8) && replacement.ArtworkSourceImage is not null ||
                prop.NameEquals("artistNames"u8) && replacement.ArtistNames.Length > 0)
                continue;
            prop.WriteTo(w);
        }
        w.WriteString("songId"u8, replacement.SongId);
        w.WriteString("title"u8, replacement.Title);
        w.WriteString("artist"u8, replacement.Artist);
        w.WriteString("album"u8, replacement.Album);
        if (replacement.Duration is { } dur) w.WriteNumber("duration"u8, dur); else w.WriteNull("duration"u8);
        if (replacement.BaselineDuration is { } bdur) w.WriteNumber("baselineDuration"u8, bdur); else w.WriteNull("baselineDuration"u8);
        w.WriteString("cover_url"u8, replacement.CoverUrl);
        if (replacement.LocalFileId is not null) w.WriteString("localFileId"u8, replacement.LocalFileId);
        if (replacement.ArtworkSourceKey is not null) w.WriteString("artworkSourceKey"u8, replacement.ArtworkSourceKey);
        if (replacement.ArtworkSourceImage is not null) w.WriteString("artworkSourceImage"u8, replacement.ArtworkSourceImage);
        if (replacement.ArtistNames.Length > 0) MusicNodeSongCodec.WriteStringArray(w, "artistNames", replacement.ArtistNames);
        w.WritePropertyName("sources"u8);
        w.WriteStartArray();
        foreach (var s in replacement.Sources) MusicNodeSongCodec.WriteSource(w, s);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static JsonElement RewritePlatformResult(JsonElement root, PlatformSongShape shape, Func<JsonElement, MusicSongRecord?> transform)
    {
        void WriteItems(Utf8JsonWriter w, JsonElement arr, bool cards)
        {
            w.WriteStartArray();
            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) { row.WriteTo(w); continue; }
                if (!cards) { WriteMergedSong(w, row, transform(row)); continue; }
                if (!row.TryGetProperty("song"u8, out _) && !row.TryGetProperty("preview"u8, out _))
                {
                    row.WriteTo(w);
                    continue;
                }
                w.WriteStartObject();
                foreach (var prop in row.EnumerateObject())
                {
                    if (prop.NameEquals("song"u8) && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        w.WritePropertyName("song"u8);
                        WriteMergedSong(w, prop.Value, transform(prop.Value));
                    }
                    else if (prop.NameEquals("preview"u8) && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        w.WritePropertyName("preview"u8);
                        w.WriteStartArray();
                        int taken = 0;
                        foreach (var entry in prop.Value.EnumerateArray())
                        {
                            if (taken++ < 3 && entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("song"u8, out _))
                            {
                                w.WriteStartObject();
                                foreach (var ep in entry.EnumerateObject())
                                {
                                    if (ep.NameEquals("song"u8) && ep.Value.ValueKind == JsonValueKind.Object)
                                    {
                                        w.WritePropertyName("song"u8);
                                        WriteMergedSong(w, ep.Value, transform(ep.Value));
                                    }
                                    else ep.WriteTo(w);
                                }
                                w.WriteEndObject();
                            }
                            else entry.WriteTo(w);
                        }
                        w.WriteEndArray();
                    }
                    else prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        return MusicNodeSongCodec.Build(w =>
        {
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (shape == PlatformSongShape.Page && prop.NameEquals("items"u8) && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("items"u8);
                    WriteItems(w, prop.Value, false);
                }
                else if (shape == PlatformSongShape.Catalog && prop.NameEquals("sections"u8) && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("sections"u8);
                    w.WriteStartArray();
                    foreach (var section in prop.Value.EnumerateArray())
                    {
                        if (section.ValueKind != JsonValueKind.Object) { section.WriteTo(w); continue; }
                        w.WriteStartObject();
                        foreach (var sp in section.EnumerateObject())
                        {
                            if (sp.NameEquals("items"u8) && sp.Value.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("items"u8);
                                WriteItems(w, sp.Value, true);
                            }
                            else if (sp.NameEquals("tabs"u8) && sp.Value.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("tabs"u8);
                                w.WriteStartArray();
                                foreach (var tab in sp.Value.EnumerateArray())
                                {
                                    if (tab.ValueKind != JsonValueKind.Object) { tab.WriteTo(w); continue; }
                                    w.WriteStartObject();
                                    foreach (var tp in tab.EnumerateObject())
                                    {
                                        if (tp.NameEquals("items"u8) && tp.Value.ValueKind == JsonValueKind.Array)
                                        {
                                            w.WritePropertyName("items"u8);
                                            WriteItems(w, tp.Value, true);
                                        }
                                        else tp.WriteTo(w);
                                    }
                                    w.WriteEndObject();
                                }
                                w.WriteEndArray();
                            }
                            else sp.WriteTo(w);
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                else if (shape == PlatformSongShape.Discovery && prop.NameEquals("charts"u8) && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("charts"u8);
                    w.WriteStartArray();
                    foreach (var chart in prop.Value.EnumerateArray())
                    {
                        if (chart.ValueKind != JsonValueKind.Object) { chart.WriteTo(w); continue; }
                        w.WriteStartObject();
                        foreach (var cp in chart.EnumerateObject())
                        {
                            if (cp.NameEquals("songs"u8) && cp.Value.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("songs"u8);
                                WriteItems(w, cp.Value, false);
                            }
                            else cp.WriteTo(w);
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                else prop.WriteTo(w);
            }
            w.WriteEndObject();
        });
    }
}
