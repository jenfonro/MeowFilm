using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal enum PlatformSongShape { Song, SongDetail, Page, Catalog, Discovery, None }
    private readonly object repositoryGate = new();
    private readonly Dictionary<string, HashSet<string>> failedArtwork = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SongId, string Platform), long> songObservations = new();
    private long songObservation, songPublication;
    private readonly object snapshotGate = new();
    private int snapshotPending;
    private bool snapshotSealed;
    private TaskCompletionSource snapshotDrained = CompletedSnapshots();

    internal sealed record SongSnapshotBatch(long Publication, IReadOnlyDictionary<string, JsonElement?> Songs);

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
            lock (repositoryGate)
            {
                MusicRules.Require(!closed && !preparing, "CLOSED");
                var songs = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
                foreach (string id in requested)
                {
                    token.ThrowIfCancellationRequested();
                    songs[id] = visibleSongs?.Read(id) is { } song ? MusicWireCodec.SongToElement(song) : null;
                }
                return new SongSnapshotBatch(songPublication, songs);
            }
        }
        finally { lock (snapshotGate) if (--snapshotPending == 0) snapshotDrained.TrySetResult(); }
    }

    private Task SealSongSnapshots()
    {
        lock (snapshotGate) { snapshotSealed = true; return snapshotDrained.Task; }
    }

    internal event Action<IReadOnlySet<string>>? SongMetadataChanged;
    internal long BeginSongObservation() => Interlocked.Increment(ref songObservation);

    internal JsonElement? SongSnapshot(string id)
    {
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            return visibleSongs?.Read(id) is { } song ? MusicWireCodec.SongToElement(song) : null;
        }
    }

    internal IReadOnlyList<MusicSongRecord> SongSnapshots(string query)
    {
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            if (visibleSongs is null) return [];
            var songs = new List<MusicSongRecord>();
            foreach (string id in visibleSongs.Query(query))
                try { if (visibleSongs.Read(id) is { } song) songs.Add(NativeMusicSong.Normalize(song)); }
                catch (MusicLibraryException) { /* A failed candidate is not a recording match. */ }
                catch (StorageException error) when (error.Error == StorageError.Corrupt) { }
            return songs;
        }
    }

    internal async Task ArtworkLoadCompletedAsync(string address, bool succeeded)
    {
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
                    failedArtwork[id] = failed = new(StringComparer.Ordinal);
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

    // Only adoption is conditional on a real recording identity. Display containers
    // are never rewritten or rejected by the permanent song repository.
    private static MusicSongRecord? PlatformRecording(JsonElement song)
    {
        if (song.ValueKind != JsonValueKind.Object) return null;
        var validSources = new List<MusicSongSourceRecord>();
        if (MusicWireCodec.TryGet(song, "sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var srcEl in sourcesEl.EnumerateArray())
            {
                if (srcEl.ValueKind != JsonValueKind.Object) continue;
                if (!MusicWireCodec.TryGet(srcEl, "musicInfo", out var infoEl) || infoEl.ValueKind != JsonValueKind.Object) continue;
                string platform = MusicWireCodec.Str(srcEl, "source");
                string sourceKey = MusicWireCodec.Str(srcEl, "sourceKey");
                if (!NativeMusicSong.Platforms.Contains(platform)) continue;
                var src = MusicWireCodec.ReadSourceFromMusicInfo(sourceKey, platform, infoEl);
                if (src.InfoSource != platform || NativeMusicSong.InfoId(src).Length == 0 ||
                    sourceKey != NativeMusicSong.InfoKey(src))
                    continue;
                validSources.Add(NativeMusicSong.DurableSource(src));
            }
        }
        string? localFileId = MusicWireCodec.TryGet(song, "localFileId", out var lf) && lf.ValueKind == JsonValueKind.String
            ? lf.GetString() : null;
        if (validSources.Count == 0 && string.IsNullOrEmpty(localFileId)) return null;

        string[] artists = MusicWireCodec.TryGet(song, "artistNames", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array
            ? MusicWireCodec.StringArray(artistsEl) : [];
        double? duration = NativeMusicSong.ValidDuration(MusicWireCodec.TryGet(song, "duration", out var dEl) ? MusicWireCodec.Num(dEl, double.NaN) : null);
        double? baseline = NativeMusicSong.ValidDuration(MusicWireCodec.TryGet(song, "baselineDuration", out var bEl) ? MusicWireCodec.Num(bEl, double.NaN) : null) ?? duration;

        var record = new MusicSongRecord(
            MusicWireCodec.Str(song, "songId"),
            MusicWireCodec.Str(song, "title"),
            MusicWireCodec.Str(song, "artist"),
            MusicWireCodec.Str(song, "album"),
            duration,
            baseline,
            NativeMusicSong.PublicUrl(MusicWireCodec.Str(song, "cover_url")),
            localFileId,
            MusicWireCodec.OptionalStr(song, "artworkSourceKey"),
            MusicWireCodec.OptionalStr(song, "artworkSourceImage"),
            artists,
            validSources.ToArray());
        return NativeMusicSong.Normalize(record);
    }

    internal MusicSongRecord ProjectSong(MusicSongRecord song)
    {
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            if (visibleSongs is null) return song;
            string? id = visibleSongs.Registered(song) ?? visibleSongs.Find(song);
            return id is null ? song : NativeMusicSong.Normalize(visibleSongs.Read(id)!);
        }
    }

    internal JsonElement ProjectRepository(JsonElement value, PlatformSongShape shape)
    {
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            if (visibleSongs is null || shape == PlatformSongShape.None || value.ValueKind != JsonValueKind.Object)
                return value.Clone();

            MusicSongRecord? ProjectElement(JsonElement songEl)
            {
                if (songEl.ValueKind != JsonValueKind.Object) return null;
                var rec = PlatformRecording(songEl);
                if (rec is null) return null;
                string? id = visibleSongs.Registered(rec) ?? visibleSongs.Find(rec);
                return id is null ? rec : NativeMusicSong.Normalize(visibleSongs.Read(id)!);
            }

            return RewritePlatformResult(value, shape, ProjectElement);
        }
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

    internal async Task<MusicSongRecord> AdoptSongAsync(MusicSongRecord song, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized = NativeMusicSong.Normalize(song);
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            if (recommendationUnknown is null && pending is { Owner: PendingOwner.SongMetadata })
                await ReconcileLockedAsync(token).ConfigureAwait(false);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var candidate = session!.Candidate(true);
            var adopted = RegisterSongs(candidate, [normalized], authoritative: false);
            token.ThrowIfCancellationRequested();
            await CommitAsync(candidate, PendingOwner.SongMetadata, token).ConfigureAwait(false);
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
            if (!MusicWireCodec.TryGet(container, name, out var rows) || rows.ValueKind != JsonValueKind.Array) return;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!cards) { VisitSong(row); continue; }
                if (MusicWireCodec.TryGet(row, "song", out var s)) VisitSong(s);
                if (MusicWireCodec.TryGet(row, "preview", out var preview) && preview.ValueKind == JsonValueKind.Array)
                {
                    int taken = 0;
                    foreach (var entry in preview.EnumerateArray())
                    {
                        if (taken++ >= 3) break;
                        if (entry.ValueKind == JsonValueKind.Object && MusicWireCodec.TryGet(entry, "song", out var nested))
                            VisitSong(nested);
                    }
                }
            }
        }

        switch (shape)
        {
            case PlatformSongShape.Song: VisitSong(root); break;
            case PlatformSongShape.SongDetail:
                if (MusicWireCodec.TryGet(root, "song", out var detailSong)) VisitSong(detailSong);
                break;
            case PlatformSongShape.Page:
                VisitItems(root, "items", false);
                break;
            case PlatformSongShape.Catalog:
                if (MusicWireCodec.TryGet(root, "sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (section.ValueKind != JsonValueKind.Object) continue;
                        VisitItems(section, "items", true);
                        if (MusicWireCodec.TryGet(section, "tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
                            foreach (var tab in tabs.EnumerateArray())
                                if (tab.ValueKind == JsonValueKind.Object)
                                    VisitItems(tab, "items", true);
                    }
                break;
            case PlatformSongShape.Discovery:
                if (MusicWireCodec.TryGet(root, "charts", out var charts) && charts.ValueKind == JsonValueKind.Array)
                    foreach (var chart in charts.EnumerateArray())
                        if (chart.ValueKind == JsonValueKind.Object)
                            VisitItems(chart, "songs", false);
                break;
        }
    }

    private static readonly HashSet<string> SongOverlayFields = new(StringComparer.Ordinal)
    {
        "songId", "title", "artist", "album", "duration", "baselineDuration",
        "cover_url", "localFileId", "artworkSourceKey", "artworkSourceImage",
        "artistNames", "sources"
    };

    private static void WriteMergedSong(Utf8JsonWriter w, JsonElement original, MusicSongRecord? replacement)
    {
        if (replacement is null || original.ValueKind != JsonValueKind.Object)
        {
            original.WriteTo(w);
            return;
        }
        var projectedEl = MusicWireCodec.SongToElement(replacement);
        w.WriteStartObject();
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in original.EnumerateObject())
        {
            if (SongOverlayFields.Contains(prop.Name) && projectedEl.TryGetProperty(prop.Name, out var repProp))
            {
                w.WritePropertyName(prop.Name);
                repProp.WriteTo(w);
                written.Add(prop.Name);
            }
            else
            {
                prop.WriteTo(w);
                written.Add(prop.Name);
            }
        }
        foreach (var prop in projectedEl.EnumerateObject())
            if (written.Add(prop.Name))
                prop.WriteTo(w);
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
                w.WriteStartObject();
                foreach (var prop in row.EnumerateObject())
                {
                    if (prop.Name == "song" && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        w.WritePropertyName("song");
                        WriteMergedSong(w, prop.Value, transform(prop.Value));
                    }
                    else if (prop.Name == "preview" && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        w.WritePropertyName("preview");
                        w.WriteStartArray();
                        int taken = 0;
                        foreach (var entry in prop.Value.EnumerateArray())
                        {
                            if (taken++ < 3 && entry.ValueKind == JsonValueKind.Object && MusicWireCodec.Has(entry, "song"))
                            {
                                w.WriteStartObject();
                                foreach (var ep in entry.EnumerateObject())
                                {
                                    if (ep.Name == "song" && ep.Value.ValueKind == JsonValueKind.Object)
                                    {
                                        w.WritePropertyName("song");
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

        return MusicWireCodec.Build(w =>
        {
            if (shape == PlatformSongShape.Song)
            {
                WriteMergedSong(w, root, transform(root));
                return;
            }
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (shape == PlatformSongShape.SongDetail && prop.Name == "song" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    w.WritePropertyName("song");
                    WriteMergedSong(w, prop.Value, transform(prop.Value));
                }
                else if (shape == PlatformSongShape.Page && prop.Name == "items" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("items");
                    WriteItems(w, prop.Value, false);
                }
                else if (shape == PlatformSongShape.Catalog && prop.Name == "sections" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("sections");
                    w.WriteStartArray();
                    foreach (var section in prop.Value.EnumerateArray())
                    {
                        if (section.ValueKind != JsonValueKind.Object) { section.WriteTo(w); continue; }
                        w.WriteStartObject();
                        foreach (var sp in section.EnumerateObject())
                        {
                            if (sp.Name == "items" && sp.Value.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("items");
                                WriteItems(w, sp.Value, true);
                            }
                            else if (sp.Name == "tabs" && sp.Value.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("tabs");
                                w.WriteStartArray();
                                foreach (var tab in sp.Value.EnumerateArray())
                                {
                                    if (tab.ValueKind != JsonValueKind.Object) { tab.WriteTo(w); continue; }
                                    w.WriteStartObject();
                                    foreach (var tp in tab.EnumerateObject())
                                    {
                                        if (tp.Name == "items" && tp.Value.ValueKind == JsonValueKind.Array)
                                        {
                                            w.WritePropertyName("items");
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
                else if (shape == PlatformSongShape.Discovery && prop.Name == "charts" && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("charts");
                    w.WriteStartArray();
                    foreach (var chart in prop.Value.EnumerateArray())
                    {
                        if (chart.ValueKind != JsonValueKind.Object) { chart.WriteTo(w); continue; }
                        w.WriteStartObject();
                        foreach (var cp in chart.EnumerateObject())
                        {
                            if (cp.Name == "songs" && cp.Value.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("songs");
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
