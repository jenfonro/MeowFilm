using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal enum PlatformSongShape { Song, SongDetail, Page, Catalog, Discovery, None }
    private readonly object repositoryGate = new();
    private JsonObject? indexedDocument;
    private NativeMusicSongGroups? repositoryIndex;
    private readonly Dictionary<string, HashSet<string>> failedArtwork = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SongId, string Platform), long> songObservations = new();
    private long songObservation;
    internal event Action? SongMetadataChanged;
    internal long BeginSongObservation() => Interlocked.Increment(ref songObservation);

    internal JsonElement? SongSnapshot(string id)
    {
        var current = document;
        return current is not null && Repository(current)[id] is JsonObject song ? LJ.Element(song) : null;
    }
    internal IReadOnlyList<JsonObject> SongSnapshots()
    {
        var current = document;
        return current is null ? [] : Repository(current)
            .Select(pair => NativeMusicSong.Normalize(pair.Value)).ToArray();
    }

    internal async Task ArtworkLoadCompletedAsync(string address, bool succeeded)
    {
        await EnterAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (closed || preparing) return;
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (pending is not null || recommendationUnknown is not null) return;
            var candidate = document!.DeepClone().AsObject();
            var policy = MetadataPolicy(candidate);
            var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
            var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
            bool changed = false;
            foreach (var pair in Repository(candidate))
            {
                var song = LJ.Object(pair.Value);
                if (LJ.S(song["cover_url"]) != address) continue;
                if (!failedArtwork.TryGetValue(pair.Key, out var failed))
                    failedArtwork[pair.Key] = failed = new(StringComparer.Ordinal);
                if (succeeded) { failedArtwork.Remove(pair.Key); continue; }
                if (!failed.Add(address)) continue;
                var next = MusicArtworkPolicy.Sources(LJ.Element(song), platforms, enabled)
                    .FirstOrDefault(source => !failed.Contains(source.Address));
                if (next.Address is null) continue;
                song["artworkSourceKey"] = next.SourceKey;
                song["artworkSourceImage"] = next.Address;
                song["cover_url"] = next.Address;
                changed = true;
            }
            if (changed) await CommitAsync(candidate, PendingOwner.SongMetadata, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private void NotifySongMetadataChanged()
    {
        if (SongMetadataChanged is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch { /* An observer cannot reverse the durable receipt. */ }
    }

    private JsonObject Repository(JsonObject doc) => LJ.Object(LJ.Object(LibraryState(doc)["library"])["songs"]);
    private JsonObject SongReference(JsonObject doc, JsonNode? songId) =>
        NativeMusicSong.Normalize(Repository(doc)[LJ.S(songId)] ?? throw new MusicLibraryException("SONG_NOT_FOUND"));
    private JsonObject EntrySong(JsonObject doc, JsonObject entry) => SongReference(doc, entry["songId"]);

    private JsonObject[] RegisterSongs(JsonObject doc, IEnumerable<JsonObject> input, bool authoritative = false,
        long observation = 0, Dictionary<(string SongId, string Platform), long>? accepted = null)
    {
        var state = LibraryState(doc); var library = LJ.Object(state["library"]); var store = LJ.Object(library["songs"]);
        var groups = IndexLibrarySongs(library);
        var policy = MetadataPolicy(doc);
        var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var song in input)
        {
            var incoming = new NativeMusicSong.Recordings(NativeMusicSong.Normalize(song));
            var existing = groups.Find(incoming);
            var observedPlatforms = incoming.Sources.Select(source => LJ.S(source["source"])).Distinct().ToArray();
            if (authoritative && existing is not null &&
                observedPlatforms.Any(platform => songObservations.GetValueOrDefault((existing.Id, platform)) > observation))
            { ids.Add(existing.Id); continue; }
            var record = groups.Add(incoming, preferIncoming: authoritative);
            record.SelectArtwork(platforms, enabled);
            store[record.Id] = record.ToSong(); ids.Add(record.Id);
            if (authoritative && accepted is not null)
                foreach (string platform in observedPlatforms) accepted[(record.Id, platform)] = observation;
        }
        Set(doc, "library", state);
        return ids.Select(id => NativeMusicSong.Normalize(store[id])).ToArray();
    }
    private void ApplyArtworkPolicy(JsonObject doc, bool force = false)
    {
        if (!HasValue(doc, "library")) return;
        var state = LibraryState(doc);
        var songs = LJ.Object(state["library"]!["songs"]);
        var policy = MetadataPolicy(doc);
        var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in songs.ToArray())
        {
            var record = new NativeMusicSong.Recordings(LJ.Object(pair.Value));
            record.SelectArtwork(platforms, enabled, force);
            songs[pair.Key] = record.ToSong();
        }
        Set(doc, "library", state);
    }
    internal JsonObject ProjectRepository(JsonObject value, PlatformSongShape shape)
    {
        lock (repositoryGate)
        {
            var current = document;
            if (current is null) return value.DeepClone().AsObject();
            if (!ReferenceEquals(current, indexedDocument))
            {
                repositoryIndex = IndexLibrarySongs(LJ.Object(LibraryState(current)["library"]));
                indexedDocument = current;
            }
            var output = value.DeepClone().AsObject();
            foreach (var song in PlatformSongs(output, shape))
            {
                var normalized = NativeMusicSong.Normalize(song);
                var projected = repositoryIndex!.Find(new(normalized))?.ToSong() ?? normalized;
                song.Clear();
                foreach (var pair in projected) song[pair.Key] = pair.Value?.DeepClone();
            }
            return output;
        }
    }
    // These are the song positions consumed by catalog, search and discovery.
    // Unused response branches are neither interpreted nor adopted.
    private static IEnumerable<JsonObject> PlatformSongs(JsonObject result, PlatformSongShape shape)
    {
        static IEnumerable<JsonObject> Card(JsonObject card)
        {
            if (card["sources"] is JsonArray && card.ContainsKey("title"))
            { yield return card; yield break; }
            if (card["song"] is JsonObject song)
            { yield return song; yield break; }
            if (card["preview"] is JsonArray preview)
                foreach (var row in preview.OfType<JsonObject>())
                    if (row["song"] is JsonObject previewSong) yield return previewSong;
        }
        static IEnumerable<JsonObject> Items(JsonObject container, string name)
        {
            if (container[name] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                    foreach (var song in Card(row)) yield return song;
        }
        switch (shape)
        {
            case PlatformSongShape.Song: yield return result; break;
            case PlatformSongShape.SongDetail:
                if (result["song"] is JsonObject detailSong) yield return detailSong;
                break;
            case PlatformSongShape.Page:
                foreach (var song in Items(result, "items")) yield return song;
                break;
            case PlatformSongShape.Catalog:
                foreach (string name in new[] { "items", "entries" })
                    foreach (var song in Items(result, name)) yield return song;
                if (result["sections"] is JsonArray sections)
                    foreach (var section in sections.OfType<JsonObject>())
                        foreach (var song in Items(section, "items")) yield return song;
                break;
            case PlatformSongShape.Discovery:
                if (result["charts"] is JsonArray charts)
                    foreach (var chart in charts.OfType<JsonObject>())
                        foreach (var song in Items(chart, "songs")) yield return song;
                break;
        }
    }
    internal async Task<JsonElement> AdoptPlatformResultAsync(JsonElement result, CancellationToken token,
        bool authoritative = true, long observation = 0, PlatformSongShape shape = PlatformSongShape.Page)
    {
        token.ThrowIfCancellationRequested();
        if (shape == PlatformSongShape.None) return result.Clone();
        var output = JsonNode.Parse(result.GetRawText())!.AsObject();
        var songs = PlatformSongs(output, shape).ToArray();
        if (songs.Length == 0) return result.Clone();
        if (authoritative && observation == 0) observation = BeginSongObservation();
        await EnterAsync(token).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(token).ConfigureAwait(false);
            LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var candidate = document!.DeepClone().AsObject();
            var accepted = new Dictionary<(string SongId, string Platform), long>();
            var adopted = RegisterSongs(candidate, songs, authoritative, observation, accepted);
            for (int i = 0; i < songs.Length; i++)
            {
                songs[i].Clear();
                foreach (var pair in adopted[i]) songs[i][pair.Key] = pair.Value?.DeepClone();
            }
            token.ThrowIfCancellationRequested();
            if (!JsonNode.DeepEquals(candidate, document))
                await CommitAsync(candidate, PendingOwner.SongMetadata, token).ConfigureAwait(false);
            foreach (var pair in accepted) songObservations[pair.Key] = pair.Value;
            return LJ.Element(output);
        }
        finally { gate.Release(); }
    }
    internal async Task<JsonObject> AdoptSongAsync(JsonObject song, CancellationToken token) =>
        JsonNode.Parse((await AdoptPlatformResultAsync(LJ.Element(song), token, authoritative: false,
            shape: PlatformSongShape.Song).ConfigureAwait(false)).GetRawText())!.AsObject();
}
