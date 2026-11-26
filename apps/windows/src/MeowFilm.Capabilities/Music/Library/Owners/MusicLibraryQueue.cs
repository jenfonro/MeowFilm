using System.Globalization;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities.Library;

internal sealed record MusicRecommendationQueueEntry(string Id, MusicSongRecord Song);

internal sealed record MusicRecommendationQueueSnapshot(
    long Version,
    int Mode,
    string CurrentId,
    int Index,
    string Tail,
    bool HasPriority,
    MusicRecommendationQueueEntry[] Entries);

public sealed partial class MusicLibraryOwner
{
    internal Func<string, MusicQueueSourceRecord?>? QueueSelectionSource { get; set; }
    internal event Action<MusicRecommendationQueueChange>? QueueChanging;
    internal event Action<MusicRecommendationQueueChange>? QueueChanged;
    internal event Action<string>? QueueEntrySelected;

    private readonly HashSet<string> failedEntries = [];
    private (long Version, string Current, int Mode, string Target)? preloadNext;
    private bool queueLoaded;
    private long queueEpoch = 1;

    internal long QueueEpoch => Interlocked.Read(ref queueEpoch);

    private static readonly MusicQueueStateRecord EmptyQueue = new(null, 0, 0, null, [], []);

    private MusicQueueStateRecord Queue(MusicLibrarySession doc)
    {
        var saved = doc.ReadQueue();
        var queue = saved ?? EmptyQueue;
        if (!queueLoaded)
        {
            if (saved is not null) RestoreRuntime(queue.Entries, [], false);
            queueLoaded = true;
        }
        return queue;
    }

    private static int Mode(MusicLibrarySession doc) =>
        doc.ReadPlaybackMode() is { Mode: >= 1 and <= 3 } saved ? (int)saved.Mode : 1;

    private static void ValidateQueueSource(MusicQueueSourceRecord? source, string code = "CORRUPT_QUEUE")
    {
        if (source is null) return;
        MusicRules.Require(source.Id is not null && source.Title is not null, code);
    }

    private void RestoreRuntime(MusicQueueEntryRecord[] entries, string[] history, bool archive)
    {
        foreach (var e in entries)
        {
            if (e.EntryId.StartsWith("entry-", StringComparison.Ordinal) &&
                long.TryParse(e.EntryId.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                serial = Math.Max(serial, parsed);
        }
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
        if (previous.Entries.Length < 2 || previous.Source?.Kind == "fm") return;
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

    internal Task<MusicRecommendationQueueSnapshot> ReadRecommendationQueueSnapshotAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var q = Queue(doc);
            string current = q.CurrentId ?? "";
            var entries = new MusicRecommendationQueueEntry[q.Entries.Length];
            int index = -1;
            for (int i = 0; i < q.Entries.Length; i++)
            {
                var entry = q.Entries[i];
                if (entry.EntryId == current) index = i;
                entries[i] = new MusicRecommendationQueueEntry(entry.EntryId, EntrySong(doc, entry));
            }
            string tail = entries.Length > 0 ? entries[^1].Id : "";
            return new MusicRecommendationQueueSnapshot(
                queueVersion, Mode(doc), current, index, tail, q.Priority.Length > 0, entries);
        }, cancellationToken);

    private async Task<T> RunExternalQueueWriteAsync<T>(
        MusicRecommendationQueueChange? change,
        Func<MusicLibrarySession, (T Result, bool Mutate)> write,
        CancellationToken cancellationToken,
        Func<T, string?>? selectedEntry = null)
    {
        if (change is { } kind) InvokeSafe(QueueChanging, kind);
        bool mutated = false;
        try
        {
            var result = await RunLibraryWriteAsync(write, cancellationToken, mutatesQueue: true, onCommitted: () =>
            {
                mutated = true;
                if (change is not null) Interlocked.Increment(ref queueEpoch);
            }).ConfigureAwait(false);
            if (mutated && change is { } committedKind)
            {
                if (selectedEntry?.Invoke(result) is { Length: > 0 } selected)
                    InvokeSafe(QueueEntrySelected, selected);
                InvokeSafe(QueueChanged, committedKind);
            }
            return result;
        }
        catch
        {
            if (change is not null) InvokeSafe(QueueChanged, MusicRecommendationQueueChange.Selection);
            throw;
        }
    }

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
        }, cancellationToken);

    public Task<MusicQueuePositionResult> SetPlaybackModeAsync(int mode, CancellationToken cancellationToken = default, bool notifyExternal = true) =>
        RunExternalQueueWriteAsync(notifyExternal ? MusicRecommendationQueueChange.Mode : null, doc =>
        {
            MusicRules.Require(mode is >= 1 and <= 3);
            var q = Queue(doc);
            doc.SetPlaybackMode(new MusicPlaybackModeRecord(mode));
            return (new MusicQueuePositionResult(q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken);

    public Task<MusicQueuePeekResult> PeekNextQueueAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
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
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Replace, doc =>
        {
            var q = Queue(doc);
            var library = LibraryState(doc);
            var c = FindCollection(library, MusicRules.RequireId(id));
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
        }, cancellationToken);

    public Task<MusicQueuePositionResult> ReplaceQueueAsync(
        IReadOnlyList<MusicSongRecord> songs,
        int index,
        MusicPlayContextRecord? context = null,
        MusicQueueSourceRecord? queueSource = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Replace, doc =>
        {
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
        }, cancellationToken);

    public Task<MusicQueueAppendResult> AppendQueueAsync(
        IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null,
        bool next = false,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(next ? MusicRecommendationQueueChange.PlayNext : MusicRecommendationQueueChange.Append, doc =>
        {
            var q = Queue(doc);
            if (expectedVersion is { } expected)
                MusicRules.Require(expected == queueVersion, "REVISION_CONFLICT");
            var (updated, added) = AppendEntries(doc, q, songs, context ?? MusicPlayContextRecord.Empty, next);
            doc.SetQueue(updated);
            var ids = added.Select(e => e.EntryId).ToArray();
            return (new MusicQueueAppendResult(ids, updated.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken);

    public Task<MusicQueuePositionResult> SelectQueueEntryAsync(string id, bool remember = true, CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Selection, doc =>
        {
            var q = SelectEntry(Queue(doc), MusicRules.RequireId(id), remember, shuffleHistory);
            q = ApplyQueueSelectionSource(q);
            doc.SetQueue(q);
            return (new MusicQueuePositionResult(q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken, selectedEntry: r => r.CurrentId);

    public Task<MusicQueueRemoveResult> RemoveQueueEntryAsync(string id, CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Remove, doc =>
        {
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
        }, cancellationToken);

    public Task<MusicQueuePositionResult> ClearQueueAsync(CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Clear, doc =>
        {
            _ = Queue(doc);
            queueVersion++;
            shuffleHistory.Clear(); failedEntries.Clear();
            var q = EmptyQueue with { Serial = serial, Revision = queueVersion };
            doc.SetQueue(q);
            return (new MusicQueuePositionResult(q.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken);

    public Task<MusicQueueStepResult> StepQueueAsync(int direction, int? modeOverride = null, string reason = "manual",
        CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Selection, doc =>
        {
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
        }, cancellationToken, selectedEntry: r => r.TargetId);

    public Task<MusicHistoryPlaylistsPage> ListHistoryPlaylistsAsync(long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var rows = doc.ReadHistoryPlaylistSummaries();
            var items = SlicePage(rows, offset, limit, row =>
                new MusicHistoryPlaylistSummary(row.PlaylistId, row.Title, row.PlayedAtMs, row.EntryCount, row.CurrentId));
            return new MusicHistoryPlaylistsPage(items, rows.Count, offset, limit);
        }, cancellationToken);

    public Task<MusicHistoryDaysPage> ListHistoryDaysAsync(long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var rows = doc.ReadHistoryDaySummaries();
            var items = SlicePage(rows, offset, limit, row =>
                new MusicHistoryDaySummary(row.DayDate, row.DayDate, row.SongCount));
            return new MusicHistoryDaysPage(items, rows.Count, offset, limit);
        }, cancellationToken);

    public Task<MusicHistoryDayDetail> GetHistoryDayDetailAsync(string id, long offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            string validId = MusicRules.RequireId(id);
            _ = doc.ReadHistoryDaySummaries(validId).SingleOrDefault()
                ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var members = doc.ReadHistoryDaySongs(validId);
            var items = SlicePage(members, offset, limit, item =>
                new MusicHistoryDaySongView(SongReference(doc, item.SongId), item.Context, item.PlayedAtMs));
            return new MusicHistoryDayDetail(validId, validId, items, members.Count, offset, limit);
        }, cancellationToken);

    public Task RemoveHistoryPlaylistAsync(string id, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            string validId = MusicRules.RequireId(id);
            var summary = doc.ReadHistoryPlaylistSummaries(validId).SingleOrDefault();
            if (summary is null) return (true, false);
            doc.StageHistoryPlaylist(new MusicHistoryPlaylistMutation(validId, null, null, summary.Ordinal));
            return (true, true);
        }, cancellationToken);

    public Task<MusicQueuePositionResult> RestoreHistoryPlaylistAsync(string id, CancellationToken cancellationToken = default) =>
        RunExternalQueueWriteAsync(MusicRecommendationQueueChange.Restore, doc =>
        {
            string validId = MusicRules.RequireId(id);
            _ = doc.ReadHistoryPlaylistSummaries(validId).SingleOrDefault()
                ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            var archive = doc.ReadHistoryPlaylist(validId) ?? throw new MusicLibraryException("HISTORY_NOT_FOUND");
            Archive(Queue(doc), doc);
            RestoreRuntime(archive.Entries, archive.History, true);
            var restored = new MusicQueueStateRecord(
                archive.CurrentId, serial, queueVersion, archive.Source, archive.Entries, archive.Priority);
            doc.SetQueue(restored);
            return (new MusicQueuePositionResult(restored.CurrentId, queueVersion, Mode(doc)), true);
        }, cancellationToken);

    public Task RecordPlayedHistoryAsync(string entryId, long atMs, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            var queue = Queue(doc);
            string validId = MusicRules.RequireId(entryId);
            var currentEntry = queue.Entries.FirstOrDefault(e => e.EntryId == validId && queue.CurrentId == validId)
                ?? throw new MusicLibraryException("ENTRY_NOT_FOUND");
            MusicRules.Require(atMs is >= 1 and <= MusicRules.MaxInteger);
            string date = DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var days = doc.ReadHistoryDaySummaries();
            var priorSummary = days.SingleOrDefault(row => row.DayDate == date);
            var priorDay = priorSummary is null ? null : doc.ReadHistoryDay(date);

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
