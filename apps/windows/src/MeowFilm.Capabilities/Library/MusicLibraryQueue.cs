using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Live provenance is queried at the sole selection writer, never restored
    // from an entry's durable context as a new recommendation session.
    internal Func<string, JsonElement?>? QueueSelectionSource { get; set; }
    private readonly HashSet<string> failedEntries = [];
    private (long Version, string Current, int Mode, string Target)? preloadNext;
    private bool queueLoaded;
    private JsonObject Queue(MusicLibraryData doc)
    {
        var saved = Area(doc, "queue");
        var queue = saved ?? LJ.Obj(("schema", 1), ("entries", new JsonArray()), ("currentId", null), ("priority", new JsonArray()), ("source", null));
        ValidateQueue(queue, true, doc.Songs.Contains);
        if (!queueLoaded) { if (saved is not null) RestoreRuntime(queue, false); queueLoaded = true; }
        return queue;
    }
    private void History(MusicLibraryData doc, string kind)
    {
        var history = doc.Select("history", "schema");
        if (history is null)
        {
            if (kind == "write") Set(doc, "history", LJ.Obj(("schema", 1), ("playlists", new JsonArray()), ("days", new JsonArray())));
            return;
        }
        try { LJ.Require(LJ.I(history["schema"]) == 1); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_HISTORY"); }
        if (kind != "write")
            try { LJ.Require(doc.HasRelation("history", [], [doc.Scope], kind), "CORRUPT_HISTORY"); }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Corrupt)
            { throw new MusicLibraryException("CORRUPT_HISTORY"); }
    }
    private int Mode(MusicLibraryData doc) => Area(doc, "playback-settings") is { } m ? (int)LJ.I(m["mode"], 1, 3) : 1;
    private JsonObject Position(JsonObject q, MusicLibraryData doc) => LJ.Obj(("currentId", q["currentId"]), ("version", queueVersion), ("mode", Mode(doc)));
    private static string QueueText(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text : throw new MusicLibraryException("INVALID_REQUEST");
    private static JsonObject Context(JsonNode? value)
    {
        var p = value is null ? new JsonObject() : LJ.Object(value);
        var output = new JsonObject();
        foreach (string k in new[] { "playlistId", "sourceTitle", "playlist" }) if (p[k] is not null) output[k] = QueueText(p[k]);
        if (p["queueSource"] is not null)
        {
            var source = LJ.Object(p["queueSource"]); LJ.Fields(source, "id title kind platform", "id title");
            output["queueSource"] = new JsonObject(source.Where(s => s.Key is "id" or "title" or "kind" or "platform")
                .Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(QueueText(s.Value)))));
        }
        CopyFeedbackContext(p, output);
        return output;
    }
    private static void CopyFeedbackContext(JsonObject input, JsonObject output)
    {
        foreach (string field in new[] { "sourcePlatform", "sourceType", "sourceId", "searchKeyword", "dailyBranch", "heartbeatPlaylistId", "heartbeatAccountId" })
            if (input[field] is not null)
            {
                output[field] = QueueText(input[field]);
            }
    }
    private static JsonObject ContextView(JsonNode? value)
    {
        var result = new JsonObject((value as JsonObject ?? new()).Where(p =>
            p.Key is "playlistId" or "sourceTitle" or "playlist" && p.Value is JsonValue v && v.TryGetValue<string>(out _))
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        if (value is JsonObject context) CopyFeedbackContext(context, result);
        return result;
    }
    private static JsonNode? SourceView(JsonNode? value) => value is JsonObject source ? new JsonObject(source.Where(p =>
        p.Key is "id" or "title" or "kind" or "platform" && p.Value is JsonValue v && v.TryGetValue<string>(out _))
        .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))) : null;
    private static void ValidateContext(JsonObject context)
    {
        foreach (string field in new[] { "playlistId", "sourceTitle", "playlist", "sourcePlatform", "sourceType",
            "sourceId", "searchKeyword", "dailyBranch", "heartbeatPlaylistId", "heartbeatAccountId" })
            if (context[field] is not null) _ = QueueText(context[field]);
    }
    private static void ValidateSource(JsonNode? value)
    {
        if (value is null) return;
        var source = LJ.Object(value, "CORRUPT_QUEUE");
        LJ.Fields(source, "id title kind platform", "id title");
        foreach (var field in source.Where(p => p.Key is "id" or "title" or "kind" or "platform")) _ = QueueText(field.Value);
    }
    private JsonObject EntryView(JsonNode? n, MusicLibraryData doc)
    {
        var e = LJ.Object(n);
        return LJ.Obj(("id", e["id"]), ("key", e["songId"]), ("song", EntrySong(doc, e)), ("context", ContextView(e["context"])));
    }
    private static void ValidateQueue(JsonObject q, bool schema, Func<string, bool> containsSong)
    {
        try
        {
            if (schema)
            {
                LJ.Fields(q, "schema entries currentId priority source", "schema entries currentId priority source");
                LJ.Require(LJ.I(q["schema"]) == 1, "CORRUPT_QUEUE");
            }
            else
            {
                LJ.Fields(q, "entries currentId priority source history at id title", "entries currentId priority source history at id title");
                string archiveId = LJ.Text(q["id"], 36);
                LJ.Require(Guid.TryParseExact(archiveId, "D", out var parsed) &&
                    archiveId == parsed.ToString("D"), "CORRUPT_QUEUE");
                _ = LJ.I(q["at"], 1);
                _ = QueueText(q["title"]);
            }
            var entries = LJ.Array(q["entries"]);
            var ids = new HashSet<string>();
            foreach (var n in entries)
            {
                var entry = LJ.Object(n); LJ.Fields(entry, "id songId context", "id songId context");
                string id = LJ.S(entry["id"]);
                LJ.Require(Regex.IsMatch(id, "^entry-[1-9][0-9]*$", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout) &&
                    long.TryParse(id[6..], out long serial) && serial <= LJ.MaxInteger && ids.Add(id), "CORRUPT_QUEUE");
                LJ.Require(containsSong(LJ.S(entry["songId"])), "CORRUPT_QUEUE");
                ValidateContext(LJ.Object(entry["context"], "CORRUPT_QUEUE"));
            }
            string current = LJ.S(q["currentId"]);
            LJ.Require(entries.Count == 0 ? q["currentId"] is null :
                q["currentId"] is JsonValue currentValue && currentValue.TryGetValue<string>(out _) && ids.Contains(current),
                "CORRUPT_QUEUE");
            var priority = LJ.Array(q["priority"], "CORRUPT_QUEUE");
            var priorityIds = priority.Select(value => LJ.S(value)).ToArray();
            LJ.Require(priority.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out _)) &&
                priorityIds.Distinct(StringComparer.Ordinal).Count() == priorityIds.Length &&
                priorityIds.All(id => ids.Contains(id) && id != current), "CORRUPT_QUEUE");
            ValidateSource(q["source"]);
            if (entries.Count == 0) LJ.Require(q["source"] is null, "CORRUPT_QUEUE");
            if (!schema)
            {
                var history = LJ.Array(q["history"], "CORRUPT_QUEUE");
                LJ.Require(history.All(value =>
                    value is JsonValue scalar && scalar.TryGetValue<string>(out string? id) && ids.Contains(id)),
                    "CORRUPT_QUEUE");
            }
        }
        catch (MusicLibraryException e) when (e.Code != "LIMIT") { throw new MusicLibraryException("CORRUPT_QUEUE"); }
    }
    private static void ValidateHistoryDay(JsonObject day, Func<string, bool> containsSong)
    {
        LJ.Require(DateOnly.TryParseExact(LJ.S(day["date"]), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _), "CORRUPT_HISTORY");
        long previous = long.MaxValue; var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in LJ.Array(day["songs"], "CORRUPT_HISTORY"))
        {
            var row = LJ.Object(node, "CORRUPT_HISTORY");
            LJ.Require(containsSong(LJ.S(row["songId"])) && ids.Add(LJ.S(row["songId"])), "CORRUPT_HISTORY");
            ValidateContext(LJ.Object(row["context"], "CORRUPT_HISTORY"));
            long at = LJ.I(row["at"], 1); LJ.Require(at <= previous, "CORRUPT_HISTORY"); previous = at;
        }
    }
    private void RestoreRuntime(JsonObject q, bool archive)
    {
        var entries = LJ.Array(q["entries"]); var ids = entries.OfType<JsonObject>().Select(e => LJ.S(e["id"])).ToHashSet();
        foreach (string id in ids) serial = Math.Max(serial, long.Parse(id[6..], System.Globalization.CultureInfo.InvariantCulture));
        shuffleHistory = archive ? LJ.Array(q["history"]).Select(value => LJ.S(value)).ToList() : [];
        failedEntries.Clear(); queueVersion++;
    }
    private JsonObject MakeEntry(JsonObject song, JsonObject context)
    {
        LJ.Require(serial < LJ.MaxInteger, "LIMIT"); var c = (JsonObject)context.DeepClone(); c.Remove("queueSource");
        return LJ.Obj(("id", "entry-" + ++serial), ("songId", LJ.S(song["songId"])), ("context", c));
    }
    private void Archive(JsonObject previous, MusicLibraryData doc)
    {
        var entries = LJ.Array(previous["entries"]); if (entries.Count < 2) return;
        History(doc, "write");
        string title = LJ.S(previous["source"]?["title"]);
        if (title.Length == 0) title = LJ.S(entries[0]?["context"]?["sourceTitle"]);
        if (title.Length == 0) title = LJ.S(entries[0]?["context"]?["playlist"], "播放列表");
        var archive = (JsonObject)NativeMusicSong.Durable(previous)!; archive.Remove("schema");
        archive["history"] = LJ.Arr(shuffleHistory); archive["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); archive["id"] = Guid.NewGuid().ToString(); archive["title"] = title;
        doc.ChangeRow("history", ["playlists"], [doc.Scope, LJ.S(archive["id"])], archive,
            0, insert: true);
    }
    private static void Select(JsonObject q, string id, bool remember, List<string> history)
    {
        string current = LJ.S(q["currentId"]); LJ.Require(LJ.Array(q["entries"]).Any(e => LJ.S(e?["id"]) == id), "ENTRY_NOT_FOUND");
        if (remember && current.Length > 0 && current != id) history.Add(current);
        q["currentId"] = id; q["priority"] = LJ.Arr((q["priority"] as JsonArray ?? []).Select(n => LJ.S(n)).Where(s => s != id));
    }
    private (JsonObject Result, bool Mutate) QueueCommand(string method, JsonObject p, MusicLibraryData doc, bool full)
    {
        var q = Queue(doc); var entries = LJ.Array(q["entries"]); string current = LJ.S(q["currentId"]);
        if (method == "music.queue.get")
        {
            return (LJ.Merge(LJ.Page(entries, p, e => EntryView(e, doc), "entries"),
                LJ.Merge(Position(q, doc), LJ.Obj(("current", entries.FirstOrDefault(e => LJ.S(e?["id"]) == current) is { } e ? EntryView(e, doc) : null),
                    ("source", SourceView(q["source"])), ("hasPriority", LJ.Array(q["priority"]).Count > 0),
                    ("persistence", LJ.Obj(("state", "saved")))))), false);
        }
        if (method == "music.queue.mode" && p["mode"] is null) { return (Position(q, doc), false); }
        if (method == "music.queue.peekNext")
        {
            LJ.Require(pending is null, "PENDING_WRITE");
            int mode = Mode(doc);
            var ids = entries.Select(e => LJ.S(e?["id"])).ToArray();
            bool Valid(string id) => id != current && ids.Contains(id) && !failedEntries.Contains(id);
            string? next = mode == 3 ? null : LJ.Array(q["priority"]).Select(n => LJ.S(n)).FirstOrDefault(Valid);
            if (next is null && mode != 3 && preloadNext is { } prior && prior.Version == queueVersion &&
                prior.Current == current && prior.Mode == mode && Valid(prior.Target)) next = prior.Target;
            if (next is null && mode == 2)
            {
                var pool = ids.Where(Valid).ToArray();
                if (pool.Length > 0) next = pool[Random.Shared.Next(pool.Length)];
            }
            if (next is null && mode == 1)
            {
                int index = System.Array.IndexOf(ids, current);
                for (int i = 1; i <= ids.Length; i++)
                {
                    int at = index + i;
                    string candidate = ids[at % ids.Length];
                    if (Valid(candidate)) { next = candidate; break; }
                }
            }
            preloadNext = next is null ? null : (queueVersion, current, mode, next);
            return (LJ.Merge(Position(q, doc), LJ.Obj(("next", next is null ? null :
                EntryView(entries.Single(e => LJ.S(e?["id"]) == next), doc)))), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE"); JsonObject? result = null;
        switch (method)
        {
            case "music.queue.mode":
                Set(doc, "playback-settings", LJ.Obj(("schema", 1), ("mode", LJ.I(p["mode"], 1, 3)))); break;
            case "music.queue.playCollection":
            case "music.queue.replace":
            {
                JsonObject[] songs; JsonObject context; int index;
                if (method == "music.queue.playCollection")
                {
                    LJ.Fields(p, "id ifRevision index", "id ifRevision index"); var library = LJ.Object(LibraryState(doc)["library"]); var c = FindCollection(doc, library, LJ.Text(p["id"]));
                    LJ.Require(Revision(c) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
                    songs = Order(c).Select(id => SongReference(doc, JsonValue.Create(id))).ToArray();
                    index = (int)LJ.I(p["index"], 0, songs.Length - 1);
                    context = LJ.Obj(("playlistId", c["id"]), ("sourceTitle", c["name"]), ("queueSource", LJ.Obj(("id", c["id"]), ("title", c["name"]), ("kind", c["kind"]))));
                }
                else
                {
                    LJ.Fields(p, full ? "songs index context expectedVersion" : "songs index context", "songs index");
                    if (full) LJ.Require(LJ.I(p["expectedVersion"]) == queueVersion, "REVISION_CONFLICT");
                    songs = Songs(p, false); context = Context(p["context"]); index = (int)LJ.I(p["index"], 0, songs.Length - 1);
                }
                songs = RegisterSongs(doc, songs);
                Archive(q, doc);
                q["entries"] = LJ.Arr(songs.Select(s => MakeEntry(s, context))); q["currentId"] = q["entries"]![index]!["id"]!.DeepClone();
                q["priority"] = new JsonArray(); q["source"] = context["queueSource"]?.DeepClone();
                shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++; break;
            }
            case "music.queue.append":
            {
                LJ.Fields(p, full ? "songs context next expectedVersion" : "songs context next", "songs");
                if (full) LJ.Require(LJ.I(p["expectedVersion"]) == queueVersion, "REVISION_CONFLICT");
                LJ.Require(p["next"] is null || LJ.IsBool(p["next"])); var songs = RegisterSongs(doc, Songs(p, false)); var context = Context(p["context"]);
                var added = songs.Select(s => MakeEntry(s, context)).ToArray();
                int index = LJ.B(p["next"]) && current.Length > 0 ? entries.Select(e => LJ.S(e?["id"])).ToList().IndexOf(current) + 1 : entries.Count;
                q["entries"] = LJ.Arr(entries.Take(index).Concat(added).Concat(entries.Skip(index)));
                if (current.Length == 0) q["currentId"] = added[0]["id"]!.DeepClone();
                else if (LJ.B(p["next"])) q["priority"] = LJ.Arr(added.Select(e => LJ.S(e["id"])).Concat(LJ.Array(q["priority"]).Select(n => LJ.S(n))));
                queueVersion++;
                if (!full) result = LJ.Obj(("entryIds", LJ.Arr(added.Select(e => LJ.S(e["id"])))));
                break;
            }
            case "music.queue.select":
                LJ.Fields(p, "id", "id"); LJ.Require(p["remember"] is null || LJ.IsBool(p["remember"]));
                Select(q, LJ.Text(p["id"]), p["remember"] is null || LJ.B(p["remember"]), shuffleHistory); break;
            case "music.queue.remove":
            {
                LJ.Fields(p, "id", "id"); string id = LJ.Text(p["id"]); int index = entries.Select(e => LJ.S(e?["id"])).ToList().IndexOf(id);
                LJ.Require(index >= 0, "ENTRY_NOT_FOUND"); entries.RemoveAt(index); bool wasCurrent = current == id;
                if (wasCurrent) q["currentId"] = entries.Count == 0 ? null : entries[Math.Min(index, entries.Count - 1)]!["id"]!.DeepClone();
                q["priority"] = LJ.Arr(LJ.Array(q["priority"]).Select(n => LJ.S(n)).Where(s => s != id)); shuffleHistory.RemoveAll(s => s == id); failedEntries.Remove(id);
                if (entries.Count == 0) q["source"] = null;
                queueVersion++; result = LJ.Obj(("wasCurrent", wasCurrent), ("nextId", q["currentId"])); break;
            }
            case "music.queue.reorder":
            {
                LJ.Fields(p, "ids", "ids"); var ids = LJ.Array(p["ids"]).Select(n => LJ.Text(n)).ToArray();
                var map = entries.OfType<JsonObject>().ToDictionary(e => LJ.S(e["id"]));
                LJ.Require(ids.Length == entries.Count && ids.Distinct().Count() == ids.Length && ids.All(map.ContainsKey), "INVALID_ORDER");
                if (!ids.SequenceEqual(entries.Select(e => LJ.S(e?["id"])))) { q["entries"] = LJ.Arr(ids.Select(id => map[id])); queueVersion++; }
                break;
            }
            case "music.queue.clear":
                q = LJ.Obj(("schema", 1), ("entries", new JsonArray()), ("currentId", null), ("priority", new JsonArray()), ("source", null));
                queueVersion++; shuffleHistory.Clear(); failedEntries.Clear(); break;
            case "music.queue.next":
            case "music.queue.previous":
            case "music.queue.step":
            {
                bool step = method.EndsWith(".step", StringComparison.Ordinal);
                LJ.Fields(p, step ? "direction mode reason" : method.EndsWith(".next", StringComparison.Ordinal) ? "reason" : "", step ? "direction mode" : "");
                int direction = step ? (int)LJ.I(p["direction"], -1, 1) : method.EndsWith(".previous", StringComparison.Ordinal) ? -1 : 1;
                LJ.Require(direction != 0); int mode = step ? (int)LJ.I(p["mode"], 1, 3) : Mode(doc); string reason = LJ.S(p["reason"], "manual");
                LJ.Require(reason is "manual" or "ended"); var ids = entries.Select(e => LJ.S(e?["id"])).ToArray();
                bool Valid(string id) => ids.Contains(id) && !failedEntries.Contains(id);
                string? target = null;
                if (direction > 0) target = LJ.Array(q["priority"]).Select(n => LJ.S(n)).FirstOrDefault(Valid);
                if (target is null && direction > 0 && mode != 3 && preloadNext is { } planned &&
                    planned.Version == queueVersion && planned.Current == current && planned.Mode == mode && Valid(planned.Target))
                    target = planned.Target;
                if (target is null && direction > 0 && mode == 3 && reason == "ended" && Valid(current)) target = current;
                if (target is null && mode == 2)
                {
                    if (direction < 0) while (shuffleHistory.Count > 0)
                        { var id = shuffleHistory[^1]; shuffleHistory.RemoveAt(shuffleHistory.Count - 1); if (id != current && Valid(id)) { target = id; break; } }
                    else
                    {
                        var pool = ids.Where(id => id != current && Valid(id)).ToArray();
                        target = pool.Length == 0 ? ids.FirstOrDefault(Valid) : pool[Random.Shared.Next(pool.Length)];
                    }
                }
                else if (target is null)
                {
                    int index = System.Array.IndexOf(ids, current);
                    for (int i = 1; i <= ids.Length; i++)
                    {
                        int next = index + i * direction;
                        next = (next % ids.Length + ids.Length) % ids.Length;
                        if (Valid(ids[next])) { target = ids[next]; break; }
                    }
                }
                if (target is not null) Select(q, target, direction > 0 || mode != 2, shuffleHistory);
                result = LJ.Obj(("targetId", target)); break;
            }
            default: throw new MusicLibraryException("INVALID_REQUEST");
        }
        if ((method is "music.queue.select" or "music.queue.next" or "music.queue.previous" or
            "music.queue.step" or "music.queue.remove") &&
            QueueSelectionSource?.Invoke(LJ.S(q["currentId"])) is { } source)
            q["source"] = JsonNode.Parse(source.GetRawText());
        Set(doc, "queue", q);
        return (LJ.Merge(result ?? new JsonObject(), Position(q, doc)), true);
    }
    private (JsonObject Result, bool Mutate) HistoryCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        if (method is "music.history.list" or "music.history.detail" or "music.history.get")
        {
            LJ.Fields(p, "kind id offset limit", method.EndsWith(".detail", StringComparison.Ordinal) ? "kind id" : method.EndsWith(".list", StringComparison.Ordinal) ? "kind" : "");
            string kind = LJ.S(p["kind"], "days"); LJ.Require(kind is "playlists" or "days");
            History(doc, kind);
            string identity = kind == "playlists" ? "id" : "date", contents = kind == "playlists" ? "entries" : "songs";
            var rows = doc.Rows("history", [kind], [doc.Scope], identity);
            if (p["id"] is null)
            {
                return (LJ.Page(rows, p, row =>
                {
                    var summary = doc.SelectRow("history", [kind], row.Key, kind == "playlists" ? ["id", "title", "at", "currentId"] : ["date"]);
                    LJ.Require(doc.HasRelation("history", [kind], row.Key, contents), "CORRUPT_HISTORY");
                    int count = doc.Rows("history", [kind, contents], row.Key).Count;
                    if (kind == "playlists")
                    {
                        LJ.Require(Guid.TryParseExact(LJ.S(summary["id"]), "D", out _), "CORRUPT_HISTORY");
                        _ = QueueText(summary["title"]); _ = LJ.I(summary["at"], 1);
                        return LJ.Obj(("id", summary["id"]), ("title", summary["title"]), ("at", summary["at"]), ("count", count), ("currentId", summary["currentId"]));
                    }
                    LJ.Require(DateOnly.TryParseExact(LJ.S(summary["date"]), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _), "CORRUPT_HISTORY");
                    return LJ.Obj(("id", summary["date"]), ("date", summary["date"]), ("count", count));
                }), false);
            }
            string id = LJ.Text(p["id"]);
            var selected = rows.SingleOrDefault(row => LJ.S(row.Value[identity]) == id) ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var header = doc.SelectRow("history", [kind], selected.Key, kind == "playlists" ? ["id", "title", "at", "currentId", "source"] : ["date"]);
            LJ.Require(doc.HasRelation("history", [kind], selected.Key, contents), "CORRUPT_HISTORY");
            if (kind == "playlists")
            {
                _ = QueueText(header["title"]); _ = LJ.I(header["at"], 1); ValidateSource(header["source"]);
            }
            else LJ.Require(DateOnly.TryParseExact(id, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _), "CORRUPT_HISTORY");
            var members = doc.Rows("history", [kind, contents], selected.Key);
            var seenEntries = new HashSet<string>(StringComparer.Ordinal);
            var page = LJ.Page(members, p, row =>
            {
                var value = doc.Row("history", [kind, contents], row.Key);
                LJ.Require(doc.Songs.Contains(LJ.S(value["songId"])), "CORRUPT_HISTORY");
                ValidateContext(LJ.Object(value["context"], "CORRUPT_HISTORY"));
                if (kind == "playlists")
                {
                    string entryId = LJ.S(value["id"]);
                    LJ.Require(Regex.IsMatch(entryId, "^entry-[1-9][0-9]*$", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout) &&
                        long.TryParse(entryId[6..], out long serial) && serial <= LJ.MaxInteger && seenEntries.Add(entryId), "CORRUPT_HISTORY");
                    return EntryView(value, doc);
                }
                _ = LJ.I(value["at"], 1);
                return LJ.Obj(("song", SongReference(doc, value["songId"])), ("context", ContextView(value["context"])), ("at", value["at"]));
            }, kind == "playlists" ? "entries" : "items");
            return (LJ.Merge(page, kind == "days" ? LJ.Obj(("id", id), ("date", id)) :
                LJ.Obj(("id", id), ("title", header["title"]), ("at", header["at"]), ("currentId", header["currentId"]), ("source", SourceView(header["source"])))), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE");
        if (method is "music.history.removePlaylist" or "music.history.restorePlaylist")
        {
            LJ.Fields(p, "id", "id"); string id = LJ.Text(p["id"]);
            History(doc, "playlists");
            var selected = doc.Rows("history", ["playlists"], [doc.Scope], "id")
                .SingleOrDefault(row => LJ.S(row.Value["id"]) == id);
            if (method == "music.history.removePlaylist")
            {
                if (selected is null) return (new JsonObject(), false);
                doc.ChangeRow("history", ["playlists"], selected.Key, null, selected.Ordinal);
                return (new JsonObject(), true);
            }
            if (selected is null) throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var archive = doc.Row("history", ["playlists"], selected.Key);
            ValidateQueue(archive, false, doc.Songs.Contains);
            var q = archive.DeepClone().AsObject(); q.Remove("id"); q.Remove("at"); q.Remove("title"); q["schema"] = 1;
            Archive(Queue(doc), doc); RestoreRuntime(q, true); q.Remove("history");
            Set(doc, "queue", q); return (Position(q, doc), true);
        }
        LJ.Require(method == "music.history.recordPlayed", "INVALID_REQUEST");
        LJ.Fields(p, "entryId at", "entryId at"); var queue = Queue(doc); string entryId = LJ.Text(p["entryId"]);
        var current = LJ.Array(queue["entries"]).OfType<JsonObject>().FirstOrDefault(e => LJ.S(e["id"]) == entryId && LJ.S(queue["currentId"]) == entryId)
            ?? throw new MusicLibraryException("ENTRY_NOT_FOUND");
        long at = LJ.I(p["at"], 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        string date = DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        History(doc, "days"); History(doc, "write");
        var days = doc.Rows("history", ["days"], [doc.Scope], "date");
        var prior = days.SingleOrDefault(row => LJ.S(row.Value["date"]) == date);
        var day = prior is null ? LJ.Obj(("date", date), ("songs", new JsonArray())) : doc.Row("history", ["days"], prior.Key);
        ValidateHistoryDay(day, doc.Songs.Contains);
        var played = LJ.Obj(("songId", current["songId"]), ("context", current["context"]), ("at", at));
        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in new[] { played }.Concat(LJ.Array(day["songs"]).OfType<JsonObject>()).OrderByDescending(row => LJ.I(row["at"])))
            merged.TryAdd(LJ.S(row["songId"]), row);
        day["songs"] = LJ.Arr(merged.Values);
        doc.ChangeRow("history", ["days"], prior?.Key ?? [doc.Scope, Guid.NewGuid().ToString("D")], day,
            prior?.Ordinal ?? days.Where(row => StringComparer.Ordinal.Compare(LJ.S(row.Value["date"]), date) < 0)
                .Select(row => row.Ordinal).DefaultIfEmpty(days.Count == 0 ? 0 : days.Max(row => row.Ordinal) + 1).Min(), insert: prior is null);
        return (new JsonObject(), true);
    }
}
