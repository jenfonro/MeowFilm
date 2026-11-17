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
        var songs = new List<JsonObject>();
        if (current is not null)
            foreach (var pair in Repository(current))
                try { songs.Add(SongReference(current, JsonValue.Create(pair.Key))); }
                catch (MusicLibraryException) { /* A failed candidate is not a recording match. */ }
        return songs;
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
    private JsonObject SongReference(JsonObject doc, JsonNode? songId)
    {
        var song = LJ.Object(Repository(doc)[LJ.S(songId)] ?? throw new MusicLibraryException("SONG_NOT_FOUND"), "INVALID_SONG");
        LJ.Require(LJ.S(song["songId"]) == LJ.S(songId), "INVALID_SONG_ID");
        return NativeMusicSong.Normalize(song);
    }
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
            store[record.Id] = MergeStoredSong(store[record.Id] as JsonObject, record.ToSong()); ids.Add(record.Id);
            if (authoritative && accepted is not null)
                foreach (string platform in observedPlatforms) accepted[(record.Id, platform)] = observation;
        }
        Set(doc, "library", state);
        return ids.Select(id => NativeMusicSong.Normalize(store[id])).ToArray();
    }
    private void ApplyArtworkPolicy(JsonObject doc, bool force = false, IEnumerable<string>? songIds = null)
    {
        if (!HasValue(doc, "library")) return;
        var state = LibraryState(doc);
        var songs = LJ.Object(state["library"]!["songs"]);
        var policy = MetadataPolicy(doc);
        var platforms = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
        foreach (var id in songIds ?? songs.Select(pair => pair.Key).ToArray())
        {
            var record = new NativeMusicSong.Recordings(SongReference(doc, JsonValue.Create(id)));
            record.SelectArtwork(platforms, enabled, force);
            songs[id] = MergeStoredSong(songs[id] as JsonObject, record.ToSong());
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
            if (card["display"] is JsonValue display && display.TryGetValue<bool>(out bool shown) && !shown) yield break;
            if (card.ContainsKey("sources") && card.ContainsKey("title"))
            { yield return card; yield break; }
            if (card["song"] is JsonObject song)
            { yield return song; yield break; }
            if (card["preview"] is JsonArray preview)
                foreach (var row in preview.Take(3).OfType<JsonObject>())
                    if (row["song"] is JsonObject previewSong) yield return previewSong;
        }
        static IEnumerable<JsonObject> Items(JsonObject container, string name)
        {
            if (container[name] is not JsonArray rows) yield break;
            int rejected = 0, hidden = 0, original = rows.Count;
            foreach (var row in rows.ToArray())
            {
                JsonObject[] songs;
                try
                {
                    LJ.Require(row is JsonObject, "INVALID_RESPONSE");
                    if (row!["display"] is JsonValue display && display.TryGetValue<bool>(out bool shown) && !shown)
                    { hidden++; continue; }
                    songs = Card((JsonObject)row!).ToArray();
                    foreach (var song in songs) _ = new NativeMusicSong.Recordings(NativeMusicSong.Normalize(song));
                }
                catch (MusicLibraryException)
                {
                    rows.Remove(row); rejected++; continue;
                }
                foreach (var song in songs) yield return song;
            }
            LJ.Require(original == hidden || rejected < original - hidden, "INVALID_RESPONSE");
        }
        switch (shape)
        {
            case PlatformSongShape.Song: yield return result; break;
            case PlatformSongShape.SongDetail:
                if (result["song"] is JsonObject detailSong) yield return detailSong;
                break;
            case PlatformSongShape.Page:
                LJ.Require(result["items"] is JsonArray, "INVALID_RESPONSE");
                foreach (var song in Items(result, "items")) yield return song;
                break;
            case PlatformSongShape.Catalog:
                LJ.Require(result["items"] is JsonArray || result["entries"] is JsonArray || result["sections"] is JsonArray,
                    "INVALID_RESPONSE");
                bool valid = false;
                foreach (string name in new[] { "items", "entries" })
                {
                    if (result[name] is not JsonArray) continue;
                    JsonObject[] songs;
                    try { songs = Items(result, name).ToArray(); }
                    catch (MusicLibraryException) { result[name] = null; continue; }
                    valid = true;
                    foreach (var song in songs) yield return song;
                }
                if (result["sections"] is JsonArray sections)
                {
                    if (sections.Count == 0) valid = true;
                    foreach (var section in sections.ToArray())
                    {
                        if (section is JsonObject hidden && hidden["display"] is JsonValue display &&
                            display.TryGetValue<bool>(out bool shown) && !shown)
                        { valid = true; continue; }
                        JsonObject[] songs;
                        try
                        {
                            LJ.Require(section is JsonObject group && group["items"] is JsonArray, "INVALID_RESPONSE");
                            songs = Items((JsonObject)section!, "items").ToArray();
                        }
                        catch (MusicLibraryException)
                        {
                            // Leave the failed section as unavailable for the display
                            // consumer, which can retain other sections.
                            if (section is JsonObject group) group["items"] = null;
                            continue;
                        }
                        valid = true;
                        foreach (var song in songs) yield return song;
                    }
                }
                LJ.Require(valid, "INVALID_RESPONSE");
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
        if (songs.Length == 0) return LJ.Element(output);
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

    private static JsonObject MergeStoredSong(JsonObject? previous, JsonObject song)
    {
        if (previous is null) return song;
        var projection = NativeMusicSong.Normalize(previous);
        // Missing projected fields belong to the current observation, not to stored extensions.
        static void KeepExtensions(JsonObject raw, JsonObject projected, JsonObject current)
        {
            foreach (var field in raw)
                if (!projected.ContainsKey(field.Key) && !current.ContainsKey(field.Key))
                    current[field.Key] = field.Value?.DeepClone();
        }
        foreach (var source in LJ.Array(song["sources"]).OfType<JsonObject>())
        {
            var old = (previous["sources"] as JsonArray)?.OfType<JsonObject>()
                .SingleOrDefault(item => LJ.S(item["sourceKey"]) == LJ.S(source["sourceKey"]));
            if (old is null) continue;
            var projected = LJ.Array(projection["sources"]).OfType<JsonObject>()
                .Single(item => LJ.S(item["sourceKey"]) == LJ.S(source["sourceKey"]));
            if (old["musicInfo"] is JsonObject oldInfo && source["musicInfo"] is JsonObject info)
                KeepExtensions(oldInfo, LJ.Object(projected["musicInfo"]), info);
            KeepExtensions(old, projected, source);
        }
        KeepExtensions(previous, projection, song);
        return song;
    }
}
