using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private readonly HashSet<string> failedEntries = [];
    private (long Version, string Current, int Mode, string Target)? preloadNext;
    private JsonObject Queue(JsonObject doc) => Value(doc, "queue") as JsonObject ??
        LJ.Obj(("schema", 1), ("entries", new JsonArray()), ("currentId", null), ("priority", new JsonArray()), ("source", null));
    private JsonObject History(JsonObject doc) => Value(doc, "history") as JsonObject ?? LJ.Obj(("schema", 1), ("playlists", new JsonArray()), ("days", new JsonArray()));
    private int Mode(JsonObject doc) => Value(doc, "playback-settings") is JsonObject m ? (int)LJ.I(m["mode"], 0, 3) : 1;
    private JsonObject Position(JsonObject q, JsonObject doc) => LJ.Obj(("currentId", q["currentId"]), ("version", queueVersion), ("mode", Mode(doc)));
    private static JsonObject Context(JsonNode? value)
    {
        var p = value is null ? new JsonObject() : LJ.Object(value);
        LJ.Fields(p, "playlistId sourceTitle playlist queueSource sourcePlatform sourceType sourceId searchKeyword dailyBranch"); var output = new JsonObject();
        foreach (string k in new[] { "playlistId", "sourceTitle", "playlist" }) if (p[k] is not null) output[k] = LJ.Text(p[k]);
        if (p["queueSource"] is not null)
        {
            var source = LJ.Object(p["queueSource"]); LJ.Fields(source, "id title kind platform", "id title");
            output["queueSource"] = new JsonObject(source.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(LJ.Text(s.Value)))));
        }
        CopyFeedbackContext(p, output);
        return output;
    }
    private static void CopyFeedbackContext(JsonObject input, JsonObject output)
    {
        foreach (string field in new[] { "sourcePlatform", "sourceType", "sourceId", "searchKeyword", "dailyBranch" })
            if (input[field] is not null)
            {
                string text = LJ.Text(input[field], field == "searchKeyword" ? 512 : 128);
                LJ.Require(!text.Any(char.IsControl));
                output[field] = text;
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
        var canonical = Context(context);
        canonical.Remove("queueSource");
        LJ.Require(JsonNode.DeepEquals(context, canonical), "CORRUPT_QUEUE");
    }
    private static void ValidateSource(JsonNode? value)
    {
        if (value is null) return;
        var source = LJ.Object(value, "CORRUPT_QUEUE");
        LJ.Fields(source, "id title kind platform", "id title");
        foreach (var field in source) _ = LJ.Text(field.Value);
    }
    private static JsonObject EntryView(JsonNode? n)
    {
        var e = LJ.Object(n);
        return LJ.Obj(("id", e["id"]), ("key", e["key"]), ("song", NativeMusicSong.View(e["song"])), ("context", ContextView(e["context"])));
    }
    private static void ValidateQueue(JsonObject q, bool schema)
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
                LJ.Fields(q, "entries currentId priority source history at id title",
                    "entries currentId priority source history at id title");
                string archiveId = LJ.Text(q["id"], 36);
                LJ.Require(Guid.TryParseExact(archiveId, "D", out var parsed) &&
                    archiveId == parsed.ToString("D"), "CORRUPT_QUEUE");
                _ = LJ.I(q["at"], 1);
                _ = LJ.Text(q["title"], 2048);
            }
            var entries = LJ.Array(q["entries"]); LJ.Require(entries.Count <= 10000, "LIMIT");
            var ids = new HashSet<string>();
            foreach (var n in entries)
            {
                var entry = LJ.Object(n); LJ.Fields(entry, "id key song context", "id key song context");
                string id = LJ.S(entry["id"]);
                LJ.Require(Regex.IsMatch(id, "^entry-[1-9][0-9]*$", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout) &&
                    long.TryParse(id[6..], out long serial) && serial <= LJ.MaxInteger && ids.Add(id), "CORRUPT_QUEUE");
                var song = LJ.Object(entry["song"], "CORRUPT_QUEUE");
                LJ.Require(JsonNode.DeepEquals(song, NativeMusicSong.Normalize(song)) &&
                    LJ.S(entry["key"]) == LJ.S(song["songId"]), "CORRUPT_QUEUE");
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
                LJ.Require(history.Count <= 100 && history.All(value =>
                    value is JsonValue scalar && scalar.TryGetValue<string>(out string? id) && ids.Contains(id)),
                    "CORRUPT_QUEUE");
            }
        }
        catch (MusicLibraryException e) when (e.Code != "LIMIT") { throw new MusicLibraryException("CORRUPT_QUEUE"); }
    }
    private static void ValidateHistory(JsonObject history)
    {
        try
        {
            LJ.Fields(history, "schema playlists days", "schema playlists days");
            LJ.Require(LJ.I(history["schema"]) == 1, "CORRUPT_HISTORY");
            var playlists = LJ.Array(history["playlists"]); var days = LJ.Array(history["days"]);
            LJ.Require(playlists.Count <= 5 && days.Count <= 7, "CORRUPT_HISTORY");
            var ids = new HashSet<string>();
            foreach (var a in playlists.OfType<JsonObject>())
            {
                ValidateQueue(a, false);
                LJ.Require(ids.Add(LJ.S(a["id"])), "CORRUPT_HISTORY");
            }
            LJ.Require(playlists.All(p => p is JsonObject), "CORRUPT_HISTORY"); ids.Clear();
            foreach (var n in days)
            {
                var day = LJ.Object(n); LJ.Fields(day, "date songs", "date songs"); string date = LJ.S(day["date"]);
                LJ.Require(DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _) && ids.Add(date), "CORRUPT_HISTORY");
                var songs = LJ.Array(day["songs"]); LJ.Require(songs.Count <= 100000, "LIMIT");
                long previous = long.MaxValue;
                foreach (var row in songs.OfType<JsonObject>())
                {
                    LJ.Fields(row, "song context at", "song context at");
                    var song = LJ.Object(row["song"], "CORRUPT_HISTORY");
                    LJ.Require(JsonNode.DeepEquals(song, NativeMusicSong.Normalize(song)), "CORRUPT_HISTORY");
                    ValidateContext(LJ.Object(row["context"], "CORRUPT_HISTORY"));
                    long at = LJ.I(row["at"], 1);
                    LJ.Require(at <= previous, "CORRUPT_HISTORY");
                    previous = at;
                }
                LJ.Require(songs.All(s => s is JsonObject), "CORRUPT_HISTORY");
            }
        }
        catch (MusicLibraryException e) when (e.Code != "LIMIT") { throw new MusicLibraryException("CORRUPT_HISTORY"); }
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
        return LJ.Obj(("id", "entry-" + ++serial), ("key", LJ.S(song["songId"])), ("song", song), ("context", c));
    }
    private void Archive(JsonObject previous, JsonObject doc)
    {
        var entries = LJ.Array(previous["entries"]); if (entries.Count < 2) return;
        var history = History(doc);
        string title = LJ.S(previous["source"]?["title"]);
        if (title.Length == 0) title = LJ.S(entries[0]?["context"]?["sourceTitle"]);
        if (title.Length == 0) title = LJ.S(entries[0]?["context"]?["playlist"], "播放列表");
        var archive = (JsonObject)NativeMusicSong.Durable(previous)!; archive.Remove("schema");
        archive["history"] = LJ.Arr(shuffleHistory); archive["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); archive["id"] = Guid.NewGuid().ToString(); archive["title"] = title;
        history["playlists"] = LJ.Arr(new[] { archive }.Concat(LJ.Array(history["playlists"]).OfType<JsonObject>()).Take(5)); Set(doc, "history", history);
    }
    private static void Select(JsonObject q, string id, bool remember, List<string> history)
    {
        string current = LJ.S(q["currentId"]); LJ.Require(LJ.Array(q["entries"]).Any(e => LJ.S(e?["id"]) == id), "ENTRY_NOT_FOUND");
        if (remember && current.Length > 0 && current != id) { history.Add(current); if (history.Count > 100) history.RemoveAt(0); }
        q["currentId"] = id; q["priority"] = LJ.Arr((q["priority"] as JsonArray ?? []).Select(n => LJ.S(n)).Where(s => s != id));
    }
    private (JsonObject Result, bool Mutate) QueueCommand(string method, JsonObject p, JsonObject doc, bool full)
    {
        var q = Queue(doc); var entries = LJ.Array(q["entries"]); string current = LJ.S(q["currentId"]);
        if (method == "music.queue.get")
        {
            LJ.Fields(p, "offset limit");
            return (LJ.Merge(LJ.Page(entries.Select(EntryView), p, "entries"),
                LJ.Merge(Position(q, doc), LJ.Obj(("current", entries.FirstOrDefault(e => LJ.S(e?["id"]) == current) is { } e ? EntryView(e) : null),
                    ("source", SourceView(q["source"])), ("persistence", LJ.Obj(("state", "saved")))))), false);
        }
        if (method == "music.queue.mode" && p["mode"] is null) { LJ.Fields(p, "mode"); return (Position(q, doc), false); }
        if (method == "music.queue.peekNext")
        {
            LJ.Fields(p, "");
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
            if (next is null && mode is 0 or 1)
            {
                int index = System.Array.IndexOf(ids, current);
                for (int i = 1; i <= ids.Length; i++)
                {
                    int at = index + i;
                    if (at >= ids.Length && mode == 0) break;
                    string candidate = ids[at % ids.Length];
                    if (Valid(candidate)) { next = candidate; break; }
                }
            }
            preloadNext = next is null ? null : (queueVersion, current, mode, next);
            return (LJ.Merge(Position(q, doc), LJ.Obj(("next", next is null ? null :
                EntryView(entries.Single(e => LJ.S(e?["id"]) == next))))), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE"); JsonObject? result = null;
        switch (method)
        {
            case "music.queue.mode":
                LJ.Fields(p, "mode"); Set(doc, "playback-settings", LJ.Obj(("schema", 1), ("mode", LJ.I(p["mode"], 0, 3)))); break;
            case "music.queue.playCollection":
            case "music.queue.replace":
            {
                JsonObject[] songs; JsonObject context; int index;
                if (method == "music.queue.playCollection")
                {
                    LJ.Fields(p, "id ifRevision index", "id ifRevision index"); var library = LJ.Object(LibraryState(doc)["library"]); var c = FindCollection(library, LJ.Text(p["id"]));
                    LJ.Require(Revision(c) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
                    songs = Order(c).Select(id => NativeMusicSong.Normalize(LJ.Object(library["songs"])[id])).ToArray();
                    index = (int)LJ.I(p["index"], 0, songs.Length - 1);
                    context = LJ.Obj(("playlistId", c["id"]), ("sourceTitle", c["name"]), ("queueSource", LJ.Obj(("id", c["id"]), ("title", c["name"]), ("kind", c["kind"]))));
                }
                else
                {
                    LJ.Fields(p, full ? "songs index context expectedVersion" : "songs index context", "songs index");
                    if (full) LJ.Require(LJ.I(p["expectedVersion"]) == queueVersion, "REVISION_CONFLICT");
                    songs = Songs(p, false, full); context = Context(p["context"]); index = (int)LJ.I(p["index"], 0, songs.Length - 1);
                }
                Archive(q, doc);
                q["entries"] = LJ.Arr(songs.Select(s => MakeEntry(s, context))); q["currentId"] = q["entries"]![index]!["id"]!.DeepClone();
                q["priority"] = new JsonArray(); q["source"] = context["queueSource"]?.DeepClone();
                shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++; break;
            }
            case "music.queue.append":
            {
                LJ.Fields(p, "songs context next", "songs"); LJ.Require(p["next"] is null || LJ.IsBool(p["next"])); var songs = Songs(p, false, full); var context = Context(p["context"]);
                LJ.Require(entries.Count + songs.Length <= 10000, "LIMIT");
                var added = songs.Select(s => MakeEntry(s, context)).ToArray();
                int index = LJ.B(p["next"]) && current.Length > 0 ? entries.Select(e => LJ.S(e?["id"])).ToList().IndexOf(current) + 1 : entries.Count;
                foreach (var entry in added) entries.Insert(index++, entry.DeepClone());
                if (current.Length == 0) q["currentId"] = added[0]["id"]!.DeepClone();
                else if (LJ.B(p["next"])) q["priority"] = LJ.Arr(added.Select(e => LJ.S(e["id"])).Concat(LJ.Array(q["priority"]).Select(n => LJ.S(n))));
                queueVersion++; result = LJ.Obj(("entryIds", LJ.Arr(added.Select(e => LJ.S(e["id"]))))); break;
            }
            case "music.queue.select":
                LJ.Fields(p, "id remember", "id"); LJ.Require(p["remember"] is null || LJ.IsBool(p["remember"]));
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
                LJ.Fields(p, ""); q = LJ.Obj(("schema", 1), ("entries", new JsonArray()), ("currentId", null), ("priority", new JsonArray()), ("source", null));
                queueVersion++; shuffleHistory.Clear(); failedEntries.Clear(); break;
            case "music.queue.next":
            case "music.queue.previous":
            case "music.queue.step":
            {
                bool step = method.EndsWith(".step", StringComparison.Ordinal);
                LJ.Fields(p, step ? "direction mode reason" : method.EndsWith(".next", StringComparison.Ordinal) ? "reason" : "", step ? "direction mode" : "");
                int direction = step ? (int)LJ.I(p["direction"], -1, 1) : method.EndsWith(".previous", StringComparison.Ordinal) ? -1 : 1;
                LJ.Require(direction != 0); int mode = step ? (int)LJ.I(p["mode"], 0, 3) : Mode(doc); string reason = LJ.S(p["reason"], "manual");
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
                        if ((next < 0 || next >= ids.Length) && mode == 0) break;
                        next = (next % ids.Length + ids.Length) % ids.Length;
                        if (Valid(ids[next])) { target = ids[next]; break; }
                    }
                }
                if (target is not null) Select(q, target, direction > 0 || mode != 2, shuffleHistory);
                result = LJ.Obj(("targetId", target)); break;
            }
            default: throw new MusicLibraryException("INVALID_REQUEST");
        }
        Set(doc, "queue", q);
        return (LJ.Merge(result ?? new JsonObject(), Position(q, doc)), true);
    }
    private (JsonObject Result, bool Mutate) HistoryCommand(string method, JsonObject p, JsonObject doc)
    {
        var h = History(doc);
        if (method is "music.history.list" or "music.history.detail" or "music.history.get")
        {
            LJ.Fields(p, "kind id offset limit", method.EndsWith(".detail", StringComparison.Ordinal) ? "kind id" : method.EndsWith(".list", StringComparison.Ordinal) ? "kind" : "");
            string kind = LJ.S(p["kind"], "days"); LJ.Require(kind is "playlists" or "days"); var list = LJ.Array(h[kind]);
            if (p["id"] is null)
                return (LJ.Page(list.OfType<JsonObject>().Select(v => kind == "playlists" ?
                    LJ.Obj(("id", v["id"]), ("title", v["title"]), ("at", v["at"]), ("count", LJ.Array(v["entries"]).Count), ("currentId", v["currentId"])) :
                    LJ.Obj(("id", v["date"]), ("date", v["date"]), ("count", LJ.Array(v["songs"]).Count))), p), false);
            string id = LJ.Text(p["id"]); var item = list.OfType<JsonObject>().FirstOrDefault(i => LJ.S(i[kind == "playlists" ? "id" : "date"]) == id)
                ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            return kind == "days" ? (LJ.Merge(LJ.Page(LJ.Array(item["songs"]).OfType<JsonObject>().Select(v =>
                LJ.Obj(("song", NativeMusicSong.View(v["song"])), ("context", ContextView(v["context"])), ("at", v["at"]))), p), LJ.Obj(("id", id), ("date", id))), false) :
                (LJ.Merge(LJ.Page(LJ.Array(item["entries"]).Select(EntryView), p, "entries"), LJ.Obj(("id", id), ("title", item["title"]), ("at", item["at"]), ("currentId", item["currentId"]), ("source", SourceView(item["source"])))), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE");
        switch (method)
        {
            case "music.history.removePlaylist":
                LJ.Fields(p, "id", "id"); string remove = LJ.Text(p["id"]);
                h["playlists"] = LJ.Arr(LJ.Array(h["playlists"]).Where(p => LJ.S(p?["id"]) != remove)); break;
            case "music.history.restorePlaylist":
            {
                LJ.Fields(p, "id", "id"); string id = LJ.Text(p["id"]); var archive = LJ.Array(h["playlists"]).OfType<JsonObject>().FirstOrDefault(a => LJ.S(a["id"]) == id)
                    ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
                var q = (JsonObject)archive.DeepClone(); q.Remove("id"); q.Remove("at"); q.Remove("title"); q["schema"] = 1;
                Archive(Queue(doc), doc); RestoreRuntime(q, true); q.Remove("history");
                Set(doc, "queue", q); return (Position(q, doc), true);
            }
            case "music.history.recordPlayed":
            {
                LJ.Fields(p, "entryId at", "entryId at"); var q = Queue(doc); string entryId = LJ.Text(p["entryId"]);
                var current = LJ.Array(q["entries"]).OfType<JsonObject>().FirstOrDefault(e => LJ.S(e["id"]) == entryId && LJ.S(q["currentId"]) == entryId)
                    ?? throw new MusicLibraryException("ENTRY_NOT_FOUND");
                long at = LJ.I(p["at"], 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                string date = DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var days = LJ.Array(h["days"]); var day = days.OfType<JsonObject>().FirstOrDefault(d => LJ.S(d["date"]) == date);
                if (day is null) { day = LJ.Obj(("date", date), ("songs", new JsonArray())); days.Add(day); }
                var played = LJ.Obj(("song", current["song"]), ("context", current["context"]), ("at", at));
                var groups = new NativeMusicSongGroups();
                var merged = new Dictionary<string, (JsonObject Row, NativeMusicSong.Recordings Song)>(StringComparer.Ordinal);
                foreach (var row in new[] { played }.Concat(LJ.Array(day["songs"]).OfType<JsonObject>()).OrderByDescending(r => LJ.I(r["at"])))
                {
                    var group = groups.Add(new(LJ.Object(row["song"])));
                    // Newest occurrence owns the timestamp, context, display and equal-key recordings.
                    merged.TryAdd(group.Id, (row, group));
                }
                day["songs"] = LJ.Arr(merged.Values.Select(item =>
                    LJ.Obj(("song", item.Song.ToSong()), ("context", item.Row["context"]), ("at", item.Row["at"]))));
                h["days"] = LJ.Arr(days.OfType<JsonObject>().OrderByDescending(d => LJ.S(d["date"]), StringComparer.Ordinal).Take(7)); break;
            }
            default: throw new MusicLibraryException("INVALID_REQUEST");
        }
        Set(doc, "history", h); return (new JsonObject(), true);
    }
}
