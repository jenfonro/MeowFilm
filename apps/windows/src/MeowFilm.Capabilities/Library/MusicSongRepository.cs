using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal enum PlatformSongShape { Song, SongDetail, Page, Catalog, Discovery, None }
    private readonly object repositoryGate = new();
    private readonly Dictionary<string, HashSet<string>> failedArtwork = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SongId, string Platform), long> songObservations = new();
    private long songObservation;
    internal event Action<IReadOnlySet<string>>? SongMetadataChanged;
    internal long BeginSongObservation() => Interlocked.Increment(ref songObservation);

    internal JsonElement? SongSnapshot(string id)
    {
        lock (repositoryGate)
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            return visibleSongs?.Read(id) is { } song ? LJ.Element(song) : null;
        }
    }
    internal IReadOnlyList<JsonObject> SongSnapshots(string query)
    {
        lock (repositoryGate)
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            if (visibleSongs is null) return [];
            var songs = new List<JsonObject>();
            foreach (string id in visibleSongs.Query(query))
                try { if (visibleSongs.Read(id) is { } song) songs.Add(NativeMusicSong.Normalize(song)); }
                catch (MusicLibraryException) { /* A failed candidate is not a recording match. */ }
                catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Corrupt) { }
            return songs;
        }
    }

    internal async Task ArtworkLoadCompletedAsync(string address, bool succeeded)
    {
        await EnterAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (closed || preparing) return;
            if (succeeded && failedArtwork.Count == 0) return;
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (pending is not null || recommendationUnknown is not null) return;
            var ids = document!.Songs.Artwork(address);
            if (ids.Count == 0) return;
            if (succeeded)
            {
                foreach (string id in ids) failedArtwork.Remove(id);
                return;
            }
            var candidate = document.Candidate(true);
            var policy = MetadataPolicy(candidate);
            var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
            var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
            bool changed = false;
            foreach (string id in ids)
            {
                var song = candidate.Songs.Read(id)!;
                if (!failedArtwork.TryGetValue(id, out var failed))
                    failedArtwork[id] = failed = new(StringComparer.Ordinal);
                failed.Add(address);
                var next = MusicArtworkPolicy.Sources(LJ.Element(song), platforms, enabled)
                    .FirstOrDefault(source => !failed.Contains(source.Address));
                if (next.Address is null) continue;
                song["artworkSourceKey"] = next.SourceKey;
                song["artworkSourceImage"] = next.Address;
                song["cover_url"] = next.Address;
                candidate.Songs.Set(song);
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

    private static JsonObject SongReference(MusicLibraryData doc, JsonNode? songId)
    {
        string id = LJ.S(songId);
        var song = doc.Songs.Read(id) ?? throw new MusicLibraryException("SONG_NOT_FOUND");
        LJ.Require(LJ.S(song["songId"]) == id, "INVALID_SONG_ID");
        return NativeMusicSong.Normalize(song);
    }
    private static JsonObject EntrySong(MusicLibraryData doc, JsonObject entry) => SongReference(doc, entry["songId"]);

    private JsonObject[] RegisterSongs(MusicLibraryData doc, IEnumerable<JsonObject> input, bool authoritative = false,
        long observation = 0, Dictionary<(string SongId, string Platform), long>? accepted = null)
    {
        var policy = MetadataPolicy(doc);
        var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var song in input)
        {
            var incoming = NativeMusicSong.Normalize(song);
            string? existing = doc.Songs.Find(incoming);
            var observedPlatforms = LJ.Array(incoming["sources"]).OfType<JsonObject>()
                .Select(source => LJ.S(source["source"])).Distinct().ToArray();
            if (authoritative && existing is not null &&
                observedPlatforms.Any(platform => songObservations.GetValueOrDefault((existing, platform)) > observation))
            { ids.Add(existing); continue; }
            var record = new NativeMusicSong.Recordings(doc.Songs.Register(incoming, existing, authoritative));
            record.SelectArtwork(platforms, enabled);
            doc.Songs.Set(record.ToSong()); ids.Add(record.Id);
            if (authoritative && accepted is not null)
                foreach (string platform in observedPlatforms) accepted[(record.Id, platform)] = observation;
        }
        if (doc.Songs.Changes.Count > 0 && !HasValue(doc, "library")) Set(doc, "library", LibraryState(doc));
        return ids.Select(id => doc.Songs.Read(id)!).ToArray();
    }
    private void ApplyArtworkPolicy(MusicLibraryData doc, bool force = false)
    {
        if (!HasValue(doc, "library")) return;
        var policy = MetadataPolicy(doc);
        var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
        foreach (string id in doc.Songs.Ids())
        {
            var record = new NativeMusicSong.Recordings(doc.Songs.Read(id)!);
            record.SelectArtwork(platforms, enabled, force);
            doc.Songs.Set(record.ToSong());
        }
    }
    internal JsonObject ProjectRepository(JsonObject value, PlatformSongShape shape)
    {
        lock (repositoryGate)
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            if (visibleSongs is null) return value.DeepClone().AsObject();
            var output = value.DeepClone().AsObject();
            foreach (var song in PlatformSongs(output, shape))
            {
                // Registered identity is enough for a projection. Only an unknown
                // source needs metadata normalization and recording comparison.
                string? id = visibleSongs.Registered(song);
                var normalized = id is null ? PlatformRecording(song) : null;
                if (id is null && normalized is null) continue;
                id ??= visibleSongs.Find(normalized!);
                var projected = id is null ? normalized! : NativeMusicSong.Normalize(visibleSongs.Read(id)!);
                foreach (var pair in projected) song[pair.Key] = pair.Value?.DeepClone();
            }
            return output;
        }
    }
    // Only adoption is conditional on a real recording identity. Display containers
    // are never rewritten or rejected by the permanent song repository.
    private static JsonObject? PlatformRecording(JsonObject song)
    {
        var sources = (song["sources"] as JsonArray)?.OfType<JsonObject>().Where(source =>
            source["musicInfo"] is JsonObject info && NativeMusicSong.InfoId(info).Length > 0 &&
            NativeMusicSong.Platforms.Contains(LJ.S(source["source"])) &&
            LJ.S(info["source"]) == LJ.S(source["source"]) &&
            LJ.S(source["sourceKey"]) == NativeMusicSong.InfoKey(info)).ToArray() ?? [];
        if (sources.Length == 0 && LJ.S(song["localFileId"]).Length == 0) return null;
        var material = song.DeepClone().AsObject();
        material["sources"] = LJ.Arr(sources);
        return NativeMusicSong.Normalize(material);
    }
    private static IEnumerable<JsonObject> PlatformSongs(JsonObject result, PlatformSongShape shape)
    {
        static IEnumerable<JsonObject> Items(JsonObject container, string name, bool cards)
        {
            if (container[name] is not JsonArray rows) yield break;
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (!cards) { yield return row; continue; }
                if (row["song"] is JsonObject song) yield return song;
                if (row["preview"] is JsonArray preview)
                    foreach (var entry in preview.Take(3).OfType<JsonObject>())
                        if (entry["song"] is JsonObject nested) yield return nested;
            }
        }
        switch (shape)
        {
            case PlatformSongShape.Song: yield return result; break;
            case PlatformSongShape.SongDetail:
                if (result["song"] is JsonObject detailSong) yield return detailSong;
                break;
            case PlatformSongShape.Page:
                foreach (var song in Items(result, "items", false)) yield return song;
                break;
            case PlatformSongShape.Catalog:
                if (result["sections"] is JsonArray sections)
                    foreach (var section in sections.OfType<JsonObject>())
                    {
                        foreach (var song in Items(section, "items", true)) yield return song;
                        if (section["tabs"] is JsonArray tabs)
                            foreach (var tab in tabs.OfType<JsonObject>())
                                foreach (var song in Items(tab, "items", true)) yield return song;
                    }
                break;
            case PlatformSongShape.Discovery:
                if (result["charts"] is JsonArray charts)
                    foreach (var chart in charts.OfType<JsonObject>())
                        foreach (var song in Items(chart, "songs", false)) yield return song;
                break;
        }
    }
    internal async Task<JsonElement> AdoptPlatformResultAsync(JsonElement result, CancellationToken token,
        bool authoritative = true, long observation = 0, PlatformSongShape shape = PlatformSongShape.Page)
    {
        token.ThrowIfCancellationRequested();
        if (shape == PlatformSongShape.None) return result.Clone();
        if (JsonNode.Parse(result.GetRawText()) is not JsonObject output) return result.Clone();
        var recordings = PlatformSongs(output, shape).Select(song => (Original: song, Recording: PlatformRecording(song)))
            .Where(pair => pair.Recording is not null).ToArray();
        var songs = recordings.Select(pair => pair.Recording!).ToArray();
        if (songs.Length == 0) return LJ.Element(output);
        if (authoritative && observation == 0) observation = BeginSongObservation();
        await EnterAsync(token).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var candidate = document!.Candidate(true);
            var accepted = new Dictionary<(string SongId, string Platform), long>();
            var adopted = RegisterSongs(candidate, songs, authoritative, observation, accepted);
            for (int i = 0; i < songs.Length; i++)
            {
                foreach (var pair in adopted[i]) recordings[i].Original[pair.Key] = pair.Value?.DeepClone();
            }
            token.ThrowIfCancellationRequested();
            await CommitAsync(candidate, PendingOwner.SongMetadata, token, accepted).ConfigureAwait(false);
            return LJ.Element(output);
        }
        finally { gate.Release(); }
    }
    internal async Task<JsonObject> AdoptSongAsync(JsonObject song, CancellationToken token) =>
        JsonNode.Parse((await AdoptPlatformResultAsync(LJ.Element(song), token, authoritative: false,
            shape: PlatformSongShape.Song).ConfigureAwait(false)).GetRawText())!.AsObject();

}
