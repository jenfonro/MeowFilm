using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities.Library;

/// <summary>Recoverable source checks, not a second song authority. Only changed song rows are written.</summary>
internal sealed class MusicSongMatchStore(IStorageDatabase database)
{
    internal const string Namespace = "music.source-discovery.v1";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, JsonObject>? previous;
    private StorageRevision revision = StorageRevision.Empty;
    internal async Task<IReadOnlyList<JsonObject>> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await database.ReadAsync(db =>
            {
                previous = ReadEntries(db, repair: false);
                revision = db.Revision(Namespace);
                return (IReadOnlyList<JsonObject>)previous.Values.Select(row => { var value = row.DeepClone().AsObject(); value.Remove("storedBytes"); return value; }).ToArray();
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { previous = null; return []; }
        finally { gate.Release(); }
    }
    internal async Task WriteAsync(IReadOnlyList<JsonObject> entries, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var order = new Queue<string>(); long bytes = 0;
            foreach (var entry in entries)
            {
                var value = Entry(entry); string id = value["songId"]!.GetValue<string>();
                if (previous?.TryGetValue(id, out var old) == true)
                {
                    var known = old.DeepClone().AsObject(); known.Remove("storedBytes");
                    if (JsonNode.DeepEquals(value, known)) { value = old; bytes += LJ.I(old["storedBytes"]); }
                    else { int size = JsonSerializer.SerializeToUtf8Bytes(value).Length; value["storedBytes"] = size; bytes += size; }
                }
                else { int size = JsonSerializer.SerializeToUtf8Bytes(value).Length; value["storedBytes"] = size; bytes += size; }
                next.Add(id, value); order.Enqueue(id);
                while (bytes > MaximumBytes && next.Count > 0)
                {
                    string first = order.Dequeue(); bytes -= LJ.I(next[first]["storedBytes"]); next.Remove(first);
                }
            }
            next = order.ToDictionary(id => id, id => next[id], StringComparer.Ordinal);
            var committed = await database.WriteAsync(db =>
            {
                if (previous is null || db.Revision(Namespace) != revision)
                {
                    previous = ReadEntries(db, repair: true);
                    revision = db.Revision(Namespace);
                }
                foreach (var (id, value) in previous.Where(pair => !next.ContainsKey(pair.Key)))
                    MatchStorageRecords.Entry.Save(db, null, value, [id]);
                var positions = previous.Keys.Select((id, index) => (id, index))
                    .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
                foreach (var ((id, value), ordinal) in next.Select((item, index) => (item, index)))
                {
                    previous.TryGetValue(id, out var old);
                    MatchStorageRecords.Entry.Save(db, value, old, [id], ordinal, positions.GetValueOrDefault(id, -1));
                }
                return db.CommitRevision(Namespace, revision, Guid.NewGuid().ToString("D"));
            }, token, checked((int)bytes)).ConfigureAwait(false);
            previous = next; revision = committed;
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { previous = null; }
        finally { gate.Release(); }
    }
    private static Dictionary<string, JsonObject> ReadEntries(DatabaseSession db, bool repair)
    {
        var values = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in MatchStorageRecords.Entry.ReadMany(db, []))
        {
            try { _ = Entry(row); values.Add(row["songId"]!.GetValue<string>(), row); }
            catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
            {
                if (!repair) throw;
                db.Execute("DELETE FROM windows_source_match WHERE song_id=$id", ("$id", LJ.S(row["songId"])));
            }
        }
        return values;
    }
    private static JsonObject Entry(JsonObject entry)
    {
        LJ.Require(Guid.TryParseExact(LJ.S(entry["songId"]), "D", out _) &&
            entry["platforms"] is JsonObject, "CORRUPT_STORAGE");
        var platforms = new JsonObject();
        foreach (var pair in LJ.Object(entry["platforms"]))
        {
            var state = LJ.Object(pair.Value);
            LJ.Require(pair.Key is "wy" or "tx" or "kw" or "kg" or "mg" &&
                LJ.S(state["state"]) is "matched" or "notFound" or "failed" &&
                state["checkedAt"] is JsonValue && LJ.I(state["checkedAt"]) >= 0, "CORRUPT_STORAGE");
            platforms[pair.Key] = LJ.Obj(("state", LJ.Text(state["state"])),
                ("checkedAt", LJ.I(state["checkedAt"])), ("context", LJ.S(state["context"])),
                ("query", LJ.S(state["query"])), ("code", LJ.S(state["code"])),
                ("matchedAt", state["matchedAt"] is null ? null : JsonValue.Create(LJ.I(state["matchedAt"]))));
        }
        return LJ.Obj(("songId", LJ.Text(entry["songId"])), ("platforms", platforms));
    }
}
