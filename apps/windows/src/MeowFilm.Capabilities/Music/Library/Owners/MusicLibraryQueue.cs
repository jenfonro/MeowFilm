using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Live provenance is queried at the sole selection writer, never restored
    // from an entry's durable context as a new recommendation session.
    internal Func<string, JsonElement?>? QueueSelectionSource { get; set; }
    private readonly HashSet<string> failedEntries = [];
    private (long Version, string Current, int Mode, string Target)? preloadNext;
    private bool queueLoaded;

    private static readonly MusicQueueStateRecord EmptyQueue = new(null, 0, 0, null, [], []);

    private MusicQueueStateRecord Queue(MusicLibrarySession doc)
    {
        var saved = doc.ReadQueue();
        var queue = saved ?? EmptyQueue;
        ValidateQueueState(queue, doc.Songs.Contains);
        if (!queueLoaded)
        {
            if (saved is not null) RestoreRuntime(queue.Entries, [], false);
            queueLoaded = true;
        }
        return queue;
    }

    private static int Mode(MusicLibrarySession doc)
    {
        var saved = doc.ReadPlaybackMode();
        if (saved is null) return 1;
        MusicRules.Require(saved.Mode is >= 1 and <= 3, "CORRUPT_STORAGE");
        return (int)saved.Mode;
    }

    private void WritePositionFields(Utf8JsonWriter w, MusicQueueStateRecord q, MusicLibrarySession doc)
    {
        if (q.CurrentId is not null) w.WriteString("currentId", q.CurrentId); else w.WriteNull("currentId");
        w.WriteNumber("version", queueVersion);
        w.WriteNumber("mode", Mode(doc));
    }

    private static void ValidateQueueSource(MusicQueueSourceRecord? source)
    {
        if (source is null) return;
        MusicRules.Require(source.Id is not null && source.Title is not null, "CORRUPT_QUEUE");
    }

    private static void ValidateQueueEntries(
        MusicQueueEntryRecord[] entries,
        string? currentId,
        string[] priority,
        MusicQueueSourceRecord? source,
        Func<string, bool> containsSong,
        string[]? history = null)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            MusicRules.Require(Regex.IsMatch(entry.EntryId, "^entry-[1-9][0-9]*$", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout) &&
                long.TryParse(entry.EntryId[6..], NumberStyles.None, CultureInfo.InvariantCulture, out long s) &&
                s <= MusicRules.MaxInteger && ids.Add(entry.EntryId), "CORRUPT_QUEUE");
            MusicRules.Require(containsSong(entry.SongId), "CORRUPT_QUEUE");
        }
        MusicRules.Require(entries.Length == 0 ? currentId is null : currentId is not null && ids.Contains(currentId), "CORRUPT_QUEUE");
        MusicRules.Require(priority.Distinct(StringComparer.Ordinal).Count() == priority.Length &&
            priority.All(id => ids.Contains(id) && id != currentId), "CORRUPT_QUEUE");
        ValidateQueueSource(source);
        if (entries.Length == 0) MusicRules.Require(source is null, "CORRUPT_QUEUE");
        if (history is not null)
            MusicRules.Require(history.All(ids.Contains), "CORRUPT_QUEUE");
    }

    private static void ValidateQueueState(MusicQueueStateRecord q, Func<string, bool> containsSong)
    {
        try { ValidateQueueEntries(q.Entries, q.CurrentId, q.Priority, q.Source, containsSong); }
        catch (MusicLibraryException e) when (e.Code != "LIMIT") { throw new MusicLibraryException("CORRUPT_QUEUE"); }
    }

    private static void ValidateHistoryPlaylist(MusicHistoryPlaylistRecord archive, Func<string, bool> containsSong)
    {
        try
        {
            MusicRules.Require(Guid.TryParseExact(archive.PlaylistId, "D", out var parsed) &&
                archive.PlaylistId == parsed.ToString("D") &&
                archive.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_QUEUE");
            ValidateQueueEntries(archive.Entries, archive.CurrentId, archive.Priority, archive.Source, containsSong, archive.History);
        }
        catch (MusicLibraryException e) when (e.Code != "LIMIT") { throw new MusicLibraryException("CORRUPT_QUEUE"); }
    }

    private static void ValidateHistoryDay(MusicHistoryDayRecord day, Func<string, bool> containsSong)
    {
        MusicRules.Require(DateOnly.TryParseExact(day.DayDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), "CORRUPT_HISTORY");
        long previous = long.MaxValue;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in day.Songs)
        {
            MusicRules.Require(containsSong(row.SongId) && ids.Add(row.SongId), "CORRUPT_HISTORY");
            MusicRules.Require(row.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger && row.PlayedAtMs <= previous, "CORRUPT_HISTORY");
            previous = row.PlayedAtMs;
        }
    }

    private void RestoreRuntime(MusicQueueEntryRecord[] entries, string[] history, bool archive)
    {
        foreach (var e in entries)
            serial = Math.Max(serial, long.Parse(e.EntryId[6..], CultureInfo.InvariantCulture));
        shuffleHistory = archive ? history.ToList() : [];
        failedEntries.Clear();
        queueVersion++;
    }

    private MusicQueueEntryRecord MakeEntry(MusicSongRecord song, MusicPlayContextRecord context)
    {
        MusicRules.Require(serial < MusicRules.MaxInteger, "LIMIT");
        return new MusicQueueEntryRecord("entry-" + ++serial, song.SongId, context);
    }

    private void Archive(MusicQueueStateRecord previous, MusicLibrarySession doc)
    {
        if (previous.Entries.Length < 2) return;
        string title = previous.Source?.Title ?? "";
        if (title.Length == 0) title = previous.Entries[0].Context.SourceTitle ?? "";
        if (title.Length == 0) title = previous.Entries[0].Context.Playlist ?? "";
        if (title.Length == 0) title = "播放列表";
        string archiveId = Guid.NewGuid().ToString("D");
        var archive = new MusicHistoryPlaylistRecord(
            archiveId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            title,
            previous.CurrentId,
            previous.Source,
            previous.Entries,
            previous.Priority,
            shuffleHistory.ToArray());
        doc.StageHistoryPlaylist(new MusicHistoryPlaylistMutation(archiveId, null, archive, 0, Insert: true));
    }

    private static MusicQueueStateRecord SelectEntry(MusicQueueStateRecord q, string id, bool remember, List<string> history)
    {
        MusicRules.Require(q.Entries.Any(e => e.EntryId == id), "ENTRY_NOT_FOUND");
        if (remember && !string.IsNullOrEmpty(q.CurrentId) && q.CurrentId != id)
            history.Add(q.CurrentId);
        return q with
        {
            CurrentId = id,
            Priority = q.Priority.Where(s => s != id).ToArray()
        };
    }

    private MusicQueueStateRecord ApplyQueueSelectionSource(MusicQueueStateRecord q)
    {
        if (QueueSelectionSource?.Invoke(q.CurrentId ?? "") is { } sourceEl)
            return q with { Source = MusicWireCodec.ReadQueueSource(sourceEl, "INVALID_REQUEST") };
        return q;
    }

    private (MusicQueueStateRecord Next, MusicQueueEntryRecord[] Added) AppendEntries(
        MusicLibrarySession doc, MusicQueueStateRecord q, MusicSongRecord[] rawSongs, MusicPlayContextRecord context, bool next)
    {
        var songs = RegisterSongs(doc, rawSongs);
        var added = songs.Select(s => MakeEntry(s, context)).ToArray();
        string current = q.CurrentId ?? "";
        int index = next && current.Length > 0
            ? Array.FindIndex(q.Entries, e => e.EntryId == current) + 1
            : q.Entries.Length;
        var nextEntries = q.Entries.Take(index).Concat(added).Concat(q.Entries.Skip(index)).ToArray();
        string? nextCurrent = current.Length == 0 ? added[0].EntryId : q.CurrentId;
        string[] nextPriority = current.Length > 0 && next
            ? added.Select(e => e.EntryId).Concat(q.Priority).ToArray()
            : q.Priority;
        queueVersion++;
        var updated = q with
        {
            Entries = nextEntries,
            CurrentId = nextCurrent,
            Priority = nextPriority,
            Serial = serial,
            Revision = queueVersion
        };
        return (updated, added);
    }

    private (JsonElement Result, bool Mutate) QueueCommand(string method, JsonElement p, MusicLibrarySession doc, bool full)
    {
        var q = Queue(doc);
        string current = q.CurrentId ?? "";

        if (method == "music.queue.get")
        {
            var currentEntry = q.Entries.FirstOrDefault(e => e.EntryId == current);
            return (MusicWireCodec.Ok(w =>
            {
                MusicWireCodec.WritePage(w, q.Entries, p, (writer, entry) =>
                    MusicWireCodec.WriteEntryView(writer, entry, EntrySong(doc, entry)), "entries");
                WritePositionFields(w, q, doc);
                w.WritePropertyName("current");
                if (currentEntry is null) w.WriteNullValue();
                else MusicWireCodec.WriteEntryView(w, currentEntry, EntrySong(doc, currentEntry));
                w.WritePropertyName("source");
                MusicWireCodec.WriteQueueSourceView(w, q.Source);
                w.WriteBoolean("hasPriority", q.Priority.Length > 0);
                w.WritePropertyName("persistence");
                w.WriteStartObject();
                w.WriteString("state", "saved");
                w.WriteEndObject();
            }), false);
        }

        if (method == "music.queue.mode" && !MusicWireCodec.HasNonNull(p, "mode"))
            return (MusicWireCodec.Ok(w => WritePositionFields(w, q, doc)), false);

        if (method == "music.queue.peekNext")
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            int mode = Mode(doc);
            var ids = q.Entries.Select(e => e.EntryId).ToArray();
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            bool Valid(string id) => id != current && idSet.Contains(id) && !failedEntries.Contains(id);
            string? next = mode == 3 ? null : q.Priority.FirstOrDefault(Valid);
            if (next is null && mode != 3 && preloadNext is { } prior && prior.Version == queueVersion &&
                prior.Current == current && prior.Mode == mode && Valid(prior.Target))
                next = prior.Target;
            if (next is null && mode == 2)
            {
                var pool = ids.Where(Valid).ToArray();
                if (pool.Length > 0) next = pool[Random.Shared.Next(pool.Length)];
            }
            if (next is null && mode == 1 && ids.Length > 0)
            {
                int index = Array.IndexOf(ids, current);
                for (int i = 1; i <= ids.Length; i++)
                {
                    string candidate = ids[(index + i) % ids.Length];
                    if (Valid(candidate)) { next = candidate; break; }
                }
            }
            preloadNext = next is null ? null : (queueVersion, current, mode, next);
            var nextEntry = next is null ? null : q.Entries.Single(e => e.EntryId == next);
            return (MusicWireCodec.Ok(w =>
            {
                WritePositionFields(w, q, doc);
                w.WritePropertyName("next");
                if (nextEntry is null) w.WriteNullValue();
                else MusicWireCodec.WriteEntryView(w, nextEntry, EntrySong(doc, nextEntry));
            }), false);
        }

        MusicRules.Require(pending is null, "PENDING_WRITE");
        Action<Utf8JsonWriter>? extraFields = null;

        switch (method)
        {
            case "music.queue.mode":
                doc.SetPlaybackMode(new MusicPlaybackModeRecord(MusicWireCodec.RequireIntProp(p, "mode", 1, 3)));
                return (MusicWireCodec.Ok(w => WritePositionFields(w, q, doc)), true);

            case "music.queue.playCollection":
            case "music.queue.replace":
            {
                MusicSongRecord[] songs;
                MusicPlayContextRecord context;
                MusicQueueSourceRecord? queueSource;
                int index;
                if (method == "music.queue.playCollection")
                {
                    MusicWireCodec.RequireFields(p, "id ifRevision index");
                    var library = LibraryState(doc);
                    var c = FindCollection(doc, library, MusicWireCodec.RequireIdProp(p, "id"));
                    MusicRules.Require(c.Revision == MusicWireCodec.RequireIntProp(p, "ifRevision"), "REVISION_CONFLICT");
                    songs = c.Order.Select(id => SongReference(doc, id)).ToArray();
                    index = (int)MusicWireCodec.RequireIntProp(p, "index", 0, songs.Length - 1);
                    context = MusicPlayContextRecord.Empty with { PlaylistId = c.CollectionId, SourceTitle = c.Name };
                    queueSource = new MusicQueueSourceRecord(c.CollectionId, c.Name, c.Kind, null);
                }
                else
                {
                    MusicWireCodec.RequireFields(p, full ? "songs index expectedVersion" : "songs index");
                    if (full) MusicRules.Require(MusicWireCodec.RequireIntProp(p, "expectedVersion") == queueVersion, "REVISION_CONFLICT");
                    songs = ReadSongsParam(p, empty: false);
                    context = MusicWireCodec.TryGet(p, "context", out var ctxEl)
                        ? MusicWireCodec.ReadContext(ctxEl, out queueSource)
                        : (queueSource = null, MusicPlayContextRecord.Empty).Item2;
                    index = (int)MusicWireCodec.RequireIntProp(p, "index", 0, songs.Length - 1);
                }
                songs = RegisterSongs(doc, songs);
                Archive(q, doc);
                var entries = songs.Select(s => MakeEntry(s, context)).ToArray();
                shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
                q = new MusicQueueStateRecord(entries[index].EntryId, serial, queueVersion, queueSource, entries, []);
                break;
            }

            case "music.queue.append":
            {
                MusicWireCodec.RequireFields(p, full ? "songs expectedVersion" : "songs");
                if (full) MusicRules.Require(MusicWireCodec.RequireIntProp(p, "expectedVersion") == queueVersion, "REVISION_CONFLICT");
                bool next = MusicWireCodec.OptionalBoolProp(p, "next");
                var context = MusicWireCodec.TryGet(p, "context", out var ctxEl)
                    ? MusicWireCodec.ReadContext(ctxEl, out _)
                    : MusicPlayContextRecord.Empty;
                var (updated, added) = AppendEntries(doc, q, ReadSongsParam(p, empty: false), context, next);
                q = updated;
                if (!full)
                {
                    var ids = added.Select(e => e.EntryId).ToArray();
                    extraFields = w => MusicWireCodec.WriteStringArray(w, "entryIds", ids);
                }
                break;
            }

            case "music.queue.select":
                MusicWireCodec.RequireFields(p, "id");
                q = SelectEntry(q, MusicWireCodec.RequireIdProp(p, "id"),
                    MusicWireCodec.OptionalBoolProp(p, "remember", fallback: true), shuffleHistory);
                break;

            case "music.queue.remove":
            {
                MusicWireCodec.RequireFields(p, "id");
                string id = MusicWireCodec.RequireIdProp(p, "id");
                int index = Array.FindIndex(q.Entries, e => e.EntryId == id);
                MusicRules.Require(index >= 0, "ENTRY_NOT_FOUND");
                var remaining = q.Entries.Where((_, i) => i != index).ToArray();
                bool wasCurrent = current == id;
                string? nextCurrent = wasCurrent
                    ? (remaining.Length == 0 ? null : remaining[Math.Min(index, remaining.Length - 1)].EntryId)
                    : q.CurrentId;
                shuffleHistory.RemoveAll(s => s == id);
                failedEntries.Remove(id);
                queueVersion++;
                q = q with
                {
                    Entries = remaining,
                    CurrentId = nextCurrent,
                    Priority = q.Priority.Where(s => s != id).ToArray(),
                    Source = remaining.Length == 0 ? null : q.Source,
                    Revision = queueVersion
                };
                extraFields = w =>
                {
                    w.WriteBoolean("wasCurrent", wasCurrent);
                    if (nextCurrent is not null) w.WriteString("nextId", nextCurrent); else w.WriteNull("nextId");
                };
                break;
            }

            case "music.queue.reorder":
            {
                MusicWireCodec.RequireFields(p, "ids");
                var ids = MusicWireCodec.RequireIdArrayProp(p, "ids");
                var map = q.Entries.ToDictionary(e => e.EntryId, StringComparer.Ordinal);
                MusicRules.Require(ids.Length == q.Entries.Length &&
                    ids.Distinct(StringComparer.Ordinal).Count() == ids.Length &&
                    ids.All(map.ContainsKey), "INVALID_ORDER");
                if (!ids.SequenceEqual(q.Entries.Select(e => e.EntryId), StringComparer.Ordinal))
                {
                    queueVersion++;
                    q = q with { Entries = ids.Select(id => map[id]).ToArray(), Revision = queueVersion };
                }
                break;
            }

            case "music.queue.clear":
                queueVersion++;
                shuffleHistory.Clear(); failedEntries.Clear();
                q = EmptyQueue with { Serial = serial, Revision = queueVersion };
                break;

            case "music.queue.next":
            case "music.queue.previous":
            case "music.queue.step":
            {
                bool step = method.EndsWith(".step", StringComparison.Ordinal);
                if (step) MusicWireCodec.RequireFields(p, "direction mode");
                int direction = step ? (int)MusicWireCodec.RequireIntProp(p, "direction", -1, 1)
                    : method.EndsWith(".previous", StringComparison.Ordinal) ? -1 : 1;
                MusicRules.Require(direction != 0);
                int mode = step ? (int)MusicWireCodec.RequireIntProp(p, "mode", 1, 3) : Mode(doc);
                string reason = MusicWireCodec.Str(p, "reason", "manual");
                MusicRules.Require(reason is "manual" or "ended");
                var ids = q.Entries.Select(e => e.EntryId).ToArray();
                var idSet = ids.ToHashSet(StringComparer.Ordinal);
                bool Valid(string id) => idSet.Contains(id) && !failedEntries.Contains(id);
                string? target = null;
                if (direction > 0) target = q.Priority.FirstOrDefault(Valid);
                if (target is null && direction > 0 && mode != 3 && preloadNext is { } planned &&
                    planned.Version == queueVersion && planned.Current == current && planned.Mode == mode && Valid(planned.Target))
                    target = planned.Target;
                if (target is null && direction > 0 && mode == 3 && reason == "ended" && Valid(current)) target = current;
                if (target is null && mode == 2)
                {
                    if (direction < 0)
                        while (shuffleHistory.Count > 0)
                        {
                            var id = shuffleHistory[^1];
                            shuffleHistory.RemoveAt(shuffleHistory.Count - 1);
                            if (id != current && Valid(id)) { target = id; break; }
                        }
                    else
                    {
                        var pool = ids.Where(id => id != current && Valid(id)).ToArray();
                        target = pool.Length == 0 ? ids.FirstOrDefault(Valid) : pool[Random.Shared.Next(pool.Length)];
                    }
                }
                else if (target is null && ids.Length > 0)
                {
                    int index = Array.IndexOf(ids, current);
                    for (int i = 1; i <= ids.Length; i++)
                    {
                        int nextIdx = ((index + i * direction) % ids.Length + ids.Length) % ids.Length;
                        if (Valid(ids[nextIdx])) { target = ids[nextIdx]; break; }
                    }
                }
                if (target is not null) q = SelectEntry(q, target, direction > 0 || mode != 2, shuffleHistory);
                extraFields = w => { if (target is not null) w.WriteString("targetId", target); else w.WriteNull("targetId"); };
                break;
            }

            default:
                throw new MusicLibraryException("INVALID_REQUEST");
        }

        if (method is "music.queue.select" or "music.queue.next" or "music.queue.previous" or
            "music.queue.step" or "music.queue.remove")
            q = ApplyQueueSelectionSource(q);

        doc.SetQueue(q);
        return (MusicWireCodec.Ok(w =>
        {
            extraFields?.Invoke(w);
            WritePositionFields(w, q, doc);
        }), true);
    }

    private (JsonElement Result, bool Mutate) HistoryCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        if (method is "music.history.list" or "music.history.detail" or "music.history.get")
        {
            if (method.EndsWith(".detail", StringComparison.Ordinal)) MusicWireCodec.RequireFields(p, "kind id");
            else if (method.EndsWith(".list", StringComparison.Ordinal)) MusicWireCodec.RequireFields(p, "kind");
            string kind = MusicWireCodec.Str(p, "kind", "days");
            MusicRules.Require(kind is "playlists" or "days");

            if (!MusicWireCodec.HasNonNull(p, "id"))
            {
                if (kind == "playlists")
                {
                    var rows = doc.ReadHistoryPlaylistSummaries();
                    return (MusicWireCodec.Ok(w =>
                    {
                        MusicWireCodec.WritePage(w, rows, p, (writer, row) =>
                        {
                            MusicRules.Require(Guid.TryParseExact(row.PlaylistId, "D", out _) &&
                                row.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_HISTORY");
                            writer.WriteStartObject();
                            writer.WriteString("id", row.PlaylistId);
                            writer.WriteString("title", row.Title);
                            writer.WriteNumber("at", row.PlayedAtMs);
                            writer.WriteNumber("count", row.EntryCount);
                            if (row.CurrentId is not null) writer.WriteString("currentId", row.CurrentId); else writer.WriteNull("currentId");
                            writer.WriteEndObject();
                        });
                    }), false);
                }
                else
                {
                    var rows = doc.ReadHistoryDaySummaries();
                    return (MusicWireCodec.Ok(w =>
                    {
                        MusicWireCodec.WritePage(w, rows, p, (writer, row) =>
                        {
                            MusicRules.Require(DateOnly.TryParseExact(row.DayDate, "yyyy-MM-dd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "CORRUPT_HISTORY");
                            writer.WriteStartObject();
                            writer.WriteString("id", row.DayDate);
                            writer.WriteString("date", row.DayDate);
                            writer.WriteNumber("count", row.SongCount);
                            writer.WriteEndObject();
                        });
                    }), false);
                }
            }

            string id = MusicWireCodec.RequireIdProp(p, "id");
            if (kind == "playlists")
            {
                var summary = doc.ReadHistoryPlaylistSummaries(id).SingleOrDefault()
                    ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
                MusicRules.Require(summary.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_HISTORY");
                ValidateQueueSource(summary.Source);
                var members = doc.ReadHistoryPlaylistEntries(id);
                var seenEntries = new HashSet<string>(StringComparer.Ordinal);
                return (MusicWireCodec.Ok(w =>
                {
                    MusicWireCodec.WritePage(w, members, p, (writer, entry) =>
                    {
                        MusicRules.Require(doc.Songs.Contains(entry.SongId) &&
                            Regex.IsMatch(entry.EntryId, "^entry-[1-9][0-9]*$", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout) &&
                            long.TryParse(entry.EntryId[6..], NumberStyles.None, CultureInfo.InvariantCulture, out long s) &&
                            s <= MusicRules.MaxInteger && seenEntries.Add(entry.EntryId), "CORRUPT_HISTORY");
                        MusicWireCodec.WriteEntryView(writer, entry, EntrySong(doc, entry));
                    }, "entries");
                    w.WriteString("id", id);
                    w.WriteString("title", summary.Title);
                    w.WriteNumber("at", summary.PlayedAtMs);
                    if (summary.CurrentId is not null) w.WriteString("currentId", summary.CurrentId); else w.WriteNull("currentId");
                    w.WritePropertyName("source");
                    MusicWireCodec.WriteQueueSourceView(w, summary.Source);
                }), false);
            }
            else
            {
                MusicRules.Require(DateOnly.TryParseExact(id, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "CORRUPT_HISTORY");
                var summary = doc.ReadHistoryDaySummaries(id).SingleOrDefault()
                    ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
                _ = summary;
                var members = doc.ReadHistoryDaySongs(id);
                return (MusicWireCodec.Ok(w =>
                {
                    MusicWireCodec.WritePage(w, members, p, (writer, item) =>
                    {
                        MusicRules.Require(doc.Songs.Contains(item.SongId) &&
                            item.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_HISTORY");
                        writer.WriteStartObject();
                        writer.WritePropertyName("song");
                        MusicWireCodec.WriteSong(writer, SongReference(doc, item.SongId));
                        writer.WritePropertyName("context");
                        MusicWireCodec.WriteContextView(writer, item.Context);
                        writer.WriteNumber("at", item.PlayedAtMs);
                        writer.WriteEndObject();
                    }, "items");
                    w.WriteString("id", id);
                    w.WriteString("date", id);
                }), false);
            }
        }

        MusicRules.Require(pending is null, "PENDING_WRITE");
        if (method is "music.history.removePlaylist" or "music.history.restorePlaylist")
        {
            MusicWireCodec.RequireFields(p, "id");
            string id = MusicWireCodec.RequireIdProp(p, "id");
            var summary = doc.ReadHistoryPlaylistSummaries(id).SingleOrDefault();
            if (method == "music.history.removePlaylist")
            {
                if (summary is null) return (MusicWireCodec.Ok(), false);
                doc.StageHistoryPlaylist(new MusicHistoryPlaylistMutation(id, null, null, summary.Ordinal));
                return (MusicWireCodec.Ok(), true);
            }
            if (summary is null) throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var archive = doc.ReadHistoryPlaylist(id) ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            ValidateHistoryPlaylist(archive, doc.Songs.Contains);
            Archive(Queue(doc), doc);
            RestoreRuntime(archive.Entries, archive.History, true);
            var restored = new MusicQueueStateRecord(
                archive.CurrentId, serial, queueVersion, archive.Source, archive.Entries, archive.Priority);
            doc.SetQueue(restored);
            return (MusicWireCodec.Ok(w => WritePositionFields(w, restored, doc)), true);
        }

        MusicRules.Require(method == "music.history.recordPlayed", "INVALID_REQUEST");
        MusicWireCodec.RequireFields(p, "entryId at");
        var queue = Queue(doc);
        string entryId = MusicWireCodec.RequireIdProp(p, "entryId");
        var currentEntry = queue.Entries.FirstOrDefault(e => e.EntryId == entryId && queue.CurrentId == entryId)
            ?? throw new MusicLibraryException("ENTRY_NOT_FOUND");
        long at = MusicWireCodec.RequireIntProp(p, "at", 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        string date = DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var days = doc.ReadHistoryDaySummaries();
        var priorSummary = days.SingleOrDefault(row => row.DayDate == date);
        var priorDay = priorSummary is null ? null : doc.ReadHistoryDay(date);
        if (priorDay is not null) ValidateHistoryDay(priorDay, doc.Songs.Contains);

        var played = new MusicHistoryDaySongRecord(currentEntry.SongId, at, currentEntry.Context);
        var merged = new Dictionary<string, MusicHistoryDaySongRecord>(StringComparer.Ordinal);
        foreach (var row in new[] { played }.Concat(priorDay?.Songs ?? []).OrderByDescending(row => row.PlayedAtMs))
            merged.TryAdd(row.SongId, row);

        var nextDay = new MusicHistoryDayRecord(
            date,
            priorDay?.DayId ?? Guid.NewGuid().ToString("D"),
            at,
            merged.Values.ToArray());

        int ordinal = priorSummary?.Ordinal ?? days
            .Where(row => StringComparer.Ordinal.Compare(row.DayDate, date) < 0)
            .Select(row => row.Ordinal)
            .DefaultIfEmpty(days.Count == 0 ? 0 : days.Max(row => row.Ordinal) + 1)
            .Min();
        doc.StageHistoryDay(new MusicHistoryDayMutation(date, priorDay, nextDay, ordinal, Insert: priorDay is null));
        return (MusicWireCodec.Ok(), true);
    }
}
