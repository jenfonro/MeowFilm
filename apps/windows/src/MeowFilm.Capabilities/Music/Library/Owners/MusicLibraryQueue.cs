using System.Globalization;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Live provenance is queried at the sole selection writer, never restored
    // from an entry's durable context as a new recommendation session.
    internal Func<string, MusicQueueSourceRecord?>? QueueSelectionSource { get; set; }
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

    private static void ValidateQueueSource(MusicQueueSourceRecord? source, string code = "CORRUPT_QUEUE")
    {
        if (source is null) return;
        MusicRules.Require(source.Id is not null && source.Title is not null, code);
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

    private static MusicQueueEntryView MakeEntryView(MusicLibrarySession doc, MusicQueueEntryRecord entry) =>
        new(entry.EntryId, entry.SongId, EntrySong(doc, entry), entry.Context);

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
        if (QueueSelectionSource?.Invoke(q.CurrentId ?? "") is { } source)
        {
            ValidateQueueSource(source, "INVALID_REQUEST");
            return q with { Source = source };
        }
        return q;
    }

    private (MusicQueueStateRecord Next, MusicQueueEntryRecord[] Added) AppendEntries(
        MusicLibrarySession doc, MusicQueueStateRecord q, IReadOnlyList<MusicSongRecord> rawSongs, MusicPlayContextRecord context, bool next)
    {
        MusicRules.Require(rawSongs is not null && rawSongs.Count > 0, "INVALID_REQUEST");
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

    // ---- Queue reads & mutations ----

    public Task<MusicQueuePageResult> GetQueueAsync(long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var q = Queue(doc);
            string current = q.CurrentId ?? "";
            var currentEntry = q.Entries.FirstOrDefault(e => e.EntryId == current);
            var entries = SlicePage(q.Entries, offset, limit, entry => MakeEntryView(doc, entry));
            return new MusicQueuePageResult(
                entries,
                q.Entries.Length,
                offset,
                limit,
                q.CurrentId,
                queueVersion,
                Mode(doc),
                currentEntry is null ? null : MakeEntryView(doc, currentEntry),
                q.Source,
                q.Priority.Length > 0,
                "saved");
        }, cancellationToken, authoritativeFor: PendingOwner.Queue);

    public Task<MusicQueuePositionResult> SetPlaybackModeAsync(int mode, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            MusicRules.Require(mode is >= 1 and <= 3);
            var q = Queue(doc);
            doc.SetPlaybackMode(new MusicPlaybackModeRecord(mode));
            return (new MusicQueuePositionResult(q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueuePeekResult> PeekNextQueueAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var q = Queue(doc);
            string current = q.CurrentId ?? "";
            int mode = Mode(doc);
            var ids = q.Entries.Select(e => e.EntryId).ToArray();
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            bool Valid(string id) => id != current && idSet.Contains(id) && !failedEntries.Contains(id);
            string? next = q.Priority.FirstOrDefault(Valid);
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
            return new MusicQueuePeekResult(
                q.CurrentId,
                queueVersion,
                mode,
                nextEntry is null ? null : MakeEntryView(doc, nextEntry));
        }, cancellationToken);

    public Task<MusicQueuePositionResult> PlayCollectionAsync(string id, long ifRevision, int index, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var q = Queue(doc);
            var library = LibraryState(doc);
            var c = FindCollection(doc, library, MusicRules.RequireId(id));
            MusicRules.Require(c.Revision == ifRevision, "REVISION_CONFLICT");
            var songs = c.Order.Select(songId => SongReference(doc, songId)).ToArray();
            MusicRules.Require(songs.Length > 0 && index >= 0 && index < songs.Length);
            var context = MusicPlayContextRecord.Empty with { PlaylistId = c.CollectionId, SourceTitle = c.Name };
            var queueSource = new MusicQueueSourceRecord(c.CollectionId, c.Name, c.Kind, null);
            songs = RegisterSongs(doc, songs);
            Archive(q, doc);
            var entries = songs.Select(s => MakeEntry(s, context)).ToArray();
            shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
            var next = new MusicQueueStateRecord(entries[index].EntryId, serial, queueVersion, queueSource, entries, []);
            doc.SetQueue(next);
            return (new MusicQueuePositionResult(next.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueuePositionResult> ReplaceQueueAsync(
        IReadOnlyList<MusicSongRecord> songs,
        int index,
        MusicPlayContextRecord? context = null,
        MusicQueueSourceRecord? queueSource = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var q = Queue(doc);
            if (expectedVersion is { } expected)
                MusicRules.Require(expected == queueVersion, "REVISION_CONFLICT");
            MusicRules.Require(songs is not null && songs.Count > 0 && index >= 0 && index < songs.Count);
            ValidateQueueSource(queueSource, "INVALID_REQUEST");
            var registered = RegisterSongs(doc, songs);
            var playContext = context ?? MusicPlayContextRecord.Empty;
            Archive(q, doc);
            var entries = registered.Select(s => MakeEntry(s, playContext)).ToArray();
            shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
            var next = new MusicQueueStateRecord(entries[index].EntryId, serial, queueVersion, queueSource, entries, []);
            doc.SetQueue(next);
            return (new MusicQueuePositionResult(next.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueueAppendResult> AppendQueueAsync(
        IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null,
        bool next = false,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var q = Queue(doc);
            if (expectedVersion is { } expected)
                MusicRules.Require(expected == queueVersion, "REVISION_CONFLICT");
            var (updated, added) = AppendEntries(doc, q, songs, context ?? MusicPlayContextRecord.Empty, next);
            doc.SetQueue(updated);
            var ids = added.Select(e => e.EntryId).ToArray();
            return (new MusicQueueAppendResult(ids, updated.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueuePositionResult> SelectQueueEntryAsync(string id, bool remember = true, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var q = SelectEntry(Queue(doc), MusicRules.RequireId(id), remember, shuffleHistory);
            q = ApplyQueueSelectionSource(q);
            doc.SetQueue(q);
            return (new MusicQueuePositionResult(q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueueRemoveResult> RemoveQueueEntryAsync(string id, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var q = Queue(doc);
            string current = q.CurrentId ?? "";
            string validId = MusicRules.RequireId(id);
            int index = Array.FindIndex(q.Entries, e => e.EntryId == validId);
            MusicRules.Require(index >= 0, "ENTRY_NOT_FOUND");
            var remaining = q.Entries.Where((_, i) => i != index).ToArray();
            bool wasCurrent = current == validId;
            string? nextCurrent = wasCurrent
                ? (remaining.Length == 0 ? null : remaining[Math.Min(index, remaining.Length - 1)].EntryId)
                : q.CurrentId;
            shuffleHistory.RemoveAll(s => s == validId);
            failedEntries.Remove(validId);
            queueVersion++;
            q = q with
            {
                Entries = remaining,
                CurrentId = nextCurrent,
                Priority = q.Priority.Where(s => s != validId).ToArray(),
                Source = remaining.Length == 0 ? null : q.Source,
                Revision = queueVersion
            };
            q = ApplyQueueSelectionSource(q);
            doc.SetQueue(q);
            return (new MusicQueueRemoveResult(wasCurrent, nextCurrent, q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueuePositionResult> ClearQueueAsync(CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            _ = Queue(doc);
            queueVersion++;
            shuffleHistory.Clear(); failedEntries.Clear();
            var q = EmptyQueue with { Serial = serial, Revision = queueVersion };
            doc.SetQueue(q);
            return (new MusicQueuePositionResult(q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task<MusicQueueStepResult> StepQueueAsync(int direction, int? modeOverride = null, string reason = "manual",
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Queue, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            MusicRules.Require(direction is -1 or 1);
            if (modeOverride is { } m) MusicRules.Require(m is >= 1 and <= 3);
            MusicRules.Require(reason is "manual" or "ended");
            var q = Queue(doc);
            string current = q.CurrentId ?? "";
            int mode = modeOverride ?? Mode(doc);
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
            q = ApplyQueueSelectionSource(q);
            doc.SetQueue(q);
            return (new MusicQueueStepResult(target, q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    // ---- History reads & mutations ----

    public Task<MusicHistoryPlaylistsPage> ListHistoryPlaylistsAsync(long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var rows = doc.ReadHistoryPlaylistSummaries();
            var items = SlicePage(rows, offset, limit, row =>
            {
                MusicRules.Require(Guid.TryParseExact(row.PlaylistId, "D", out _) &&
                    row.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_HISTORY");
                return new MusicHistoryPlaylistSummary(row.PlaylistId, row.Title, row.PlayedAtMs, row.EntryCount, row.CurrentId);
            });
            return new MusicHistoryPlaylistsPage(items, rows.Count, offset, limit);
        }, cancellationToken);

    public Task<MusicHistoryDaysPage> ListHistoryDaysAsync(long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var rows = doc.ReadHistoryDaySummaries();
            var items = SlicePage(rows, offset, limit, row =>
            {
                MusicRules.Require(DateOnly.TryParseExact(row.DayDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "CORRUPT_HISTORY");
                return new MusicHistoryDaySummary(row.DayDate, row.DayDate, row.SongCount);
            });
            return new MusicHistoryDaysPage(items, rows.Count, offset, limit);
        }, cancellationToken);

    public Task<MusicHistoryDayDetail> GetHistoryDayDetailAsync(string id, long offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            string validId = MusicRules.RequireId(id);
            MusicRules.Require(DateOnly.TryParseExact(validId, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "CORRUPT_HISTORY");
            _ = doc.ReadHistoryDaySummaries(validId).SingleOrDefault()
                ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var members = doc.ReadHistoryDaySongs(validId);
            var items = SlicePage(members, offset, limit, item =>
            {
                MusicRules.Require(doc.Songs.Contains(item.SongId) &&
                    item.PlayedAtMs is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_HISTORY");
                return new MusicHistoryDaySongView(SongReference(doc, item.SongId), item.Context, item.PlayedAtMs);
            });
            return new MusicHistoryDayDetail(validId, validId, items, members.Count, offset, limit);
        }, cancellationToken);

    public Task RemoveHistoryPlaylistAsync(string id, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.History, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            string validId = MusicRules.RequireId(id);
            var summary = doc.ReadHistoryPlaylistSummaries(validId).SingleOrDefault();
            if (summary is null) return (true, false);
            doc.StageHistoryPlaylist(new MusicHistoryPlaylistMutation(validId, null, null, summary.Ordinal));
            return (true, true);
        }, cancellationToken);

    public Task<MusicQueuePositionResult> RestoreHistoryPlaylistAsync(string id, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.History, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            string validId = MusicRules.RequireId(id);
            _ = doc.ReadHistoryPlaylistSummaries(validId).SingleOrDefault()
                ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var archive = doc.ReadHistoryPlaylist(validId) ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            ValidateHistoryPlaylist(archive, doc.Songs.Contains);
            Archive(Queue(doc), doc);
            RestoreRuntime(archive.Entries, archive.History, true);
            var restored = new MusicQueueStateRecord(
                archive.CurrentId, serial, queueVersion, archive.Source, archive.Entries, archive.Priority);
            doc.SetQueue(restored);
            return (new MusicQueuePositionResult(restored.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, mutatesQueue: true);

    public Task RecordPlayedHistoryAsync(string entryId, long atMs, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.History, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var queue = Queue(doc);
            string validId = MusicRules.RequireId(entryId);
            var currentEntry = queue.Entries.FirstOrDefault(e => e.EntryId == validId && queue.CurrentId == validId)
                ?? throw new MusicLibraryException("ENTRY_NOT_FOUND");
            MusicRules.Require(atMs is >= 1 and <= MusicRules.MaxInteger);
            string date = DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var days = doc.ReadHistoryDaySummaries();
            var priorSummary = days.SingleOrDefault(row => row.DayDate == date);
            var priorDay = priorSummary is null ? null : doc.ReadHistoryDay(date);
            if (priorDay is not null) ValidateHistoryDay(priorDay, doc.Songs.Contains);

            var played = new MusicHistoryDaySongRecord(currentEntry.SongId, atMs, currentEntry.Context);
            var merged = new Dictionary<string, MusicHistoryDaySongRecord>(StringComparer.Ordinal);
            foreach (var row in new[] { played }.Concat(priorDay?.Songs ?? []).OrderByDescending(row => row.PlayedAtMs))
                merged.TryAdd(row.SongId, row);

            var nextDay = new MusicHistoryDayRecord(
                date,
                priorDay?.DayId ?? Guid.NewGuid().ToString("D"),
                atMs,
                merged.Values.ToArray());

            int ordinal = priorSummary?.Ordinal ?? days
                .Where(row => StringComparer.Ordinal.Compare(row.DayDate, date) < 0)
                .Select(row => row.Ordinal)
                .DefaultIfEmpty(days.Count == 0 ? 0 : days.Max(row => row.Ordinal) + 1)
                .Min();
            doc.StageHistoryDay(new MusicHistoryDayMutation(date, priorDay, nextDay, ordinal, Insert: priorDay is null));
            return (true, true);
        }, cancellationToken);
}
