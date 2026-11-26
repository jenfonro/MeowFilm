namespace MeowFilm.Capabilities.Library;

internal sealed record MusicRecommendationQueueReceipt(
    bool Applied,
    string State,
    string? CurrentId,
    long Version,
    int Mode,
    string? Checkpoint,
    string[] EntryIds,
    bool Restored);

public sealed partial class MusicLibraryOwner
{
    private sealed record RecommendationCheckpoint(MusicQueueStateRecord Queue, string[] History, string[] Failed);

    private readonly Dictionary<string, RecommendationCheckpoint> recommendationCheckpoints = [];
    private bool recommendationCheckpointsLoaded;

    private void EnsureRecommendationCheckpointsLoaded(MusicLibrarySession doc)
    {
        if (recommendationCheckpointsLoaded) return;
        recommendationCheckpoints.Clear();
        foreach (var saved in doc.ReadRecommendationCheckpoints())
            recommendationCheckpoints[saved.CheckpointId] = new RecommendationCheckpoint(saved.Queue, saved.History, saved.Failed);
        recommendationCheckpointsLoaded = true;
    }

    internal Task<MusicRecommendationSessionRecord?> ReadRecommendationSessionAsync(CancellationToken token = default) =>
        RunLibraryReadAsync(doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            return doc.ReadRecommendationSession();
        }, token);

    internal async Task SaveRecommendationSessionAsync(MusicRecommendationSessionRecord? value, CancellationToken token = default)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (closed || preparing) return;
            await LoadAsync(token).ConfigureAwait(false);
            var candidate = session!.Candidate(true);
            candidate.SetRecommendationSession(value);
            await CommitAsync(candidate).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal Task<bool> HasRecommendationCheckpointAsync(string checkpointId, CancellationToken token = default) =>
        string.IsNullOrEmpty(checkpointId)
            ? Task.FromResult(false)
            : RunLibraryReadAsync(doc =>
            {
                EnsureRecommendationCheckpointsLoaded(doc);
                return recommendationCheckpoints.ContainsKey(checkpointId);
            }, token);

    private RecommendationCheckpoint CaptureRecommendation(MusicLibrarySession doc) =>
        new(Queue(doc), shuffleHistory.ToArray(), failedEntries.ToArray());

    private void ClearRecommendations()
    {
        recommendationCheckpoints.Clear();
        recommendationCheckpointsLoaded = false;
    }

    internal Task<bool> DiscardRecommendationCheckpointAsync(string checkpointId, CancellationToken token = default) =>
        RunLibraryWriteAsync(doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(MusicRules.IsValidId(checkpointId, 36));
            bool discarded = recommendationCheckpoints.ContainsKey(checkpointId);
            if (discarded)
                doc.StageRecommendationCheckpoint(new MusicRecommendationCheckpointMutation(checkpointId, null));
            return (discarded, discarded);
        }, token, onCommitted: () => recommendationCheckpoints.Remove(checkpointId));

    internal Task<MusicRecommendationQueueReceipt> EnterRecommendationQueueAsync(
        long expectedVersion,
        IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null,
        MusicQueueSourceRecord? queueSource = null,
        string? seedEntryId = null,
        string? checkpoint = null,
        bool capture = true,
        bool sequential = false,
        CancellationToken token = default)
    {
        string? capturedKey = null;
        RecommendationCheckpoint? capturedValue = null;
        return RunLibraryWriteAsync(doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(songs is not null && songs.Count > 0, "INVALID_REQUEST");
            ValidateQueueSource(queueSource, "INVALID_REQUEST");
            MusicRules.Require(expectedVersion == queueVersion, "REVISION_CONFLICT");

            string cp = checkpoint is { Length: > 0 } ? MusicRules.RequireId(checkpoint, 36) : "";
            var parsedSongs = RegisterSongs(doc, songs);
            var parsedContext = context ?? MusicPlayContextRecord.Empty;
            var old = Queue(doc);
            string seed = seedEntryId is { Length: > 0 } ? MusicRules.RequireId(seedEntryId) : "";
            var retained = seed.Length == 0 ? null
                : old.Entries.SingleOrDefault(e => e.EntryId == seed && old.CurrentId == seed);
            MusicRules.Require(seed.Length == 0 || retained is not null, "STALE_QUEUE");
            if (cp.Length == 0 && capture)
            {
                capturedValue = CaptureRecommendation(doc);
                cp = Guid.NewGuid().ToString("D");
                capturedKey = cp;
                doc.StageRecommendationCheckpoint(new MusicRecommendationCheckpointMutation(
                    cp,
                    new MusicRecommendationCheckpointRecord(cp, capturedValue.Queue, capturedValue.History, capturedValue.Failed)));
            }
            else if (cp.Length > 0)
            {
                MusicRules.Require(recommendationCheckpoints.ContainsKey(cp), "CHECKPOINT_NOT_FOUND");
            }

            Archive(old, doc);
            var added = parsedSongs.Select(s => MakeEntry(s, parsedContext)).ToArray();
            var entries = retained is null ? added : [retained, .. added];
            shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
            var finalQueue = new MusicQueueStateRecord(
                retained?.EntryId ?? added[0].EntryId, serial, queueVersion, queueSource, entries, []);
            doc.SetQueue(finalQueue);
            if (sequential) doc.SetPlaybackMode(new MusicPlaybackModeRecord(1));

            var addedIds = added.Select(e => e.EntryId).ToArray();
            return (new MusicRecommendationQueueReceipt(
                true, "applied", finalQueue.CurrentId, queueVersion, Mode(doc), cp, addedIds, false), true);
        }, token, mutatesQueue: true, onCommitted: () =>
        {
            if (capturedKey is not null && capturedValue is not null)
                recommendationCheckpoints[capturedKey] = capturedValue;
        });
    }

    internal Task<MusicRecommendationQueueReceipt> AppendRecommendationQueueAsync(
        long expectedVersion,
        IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null,
        MusicQueueSourceRecord? queueSource = null,
        int? selectIndex = null,
        bool sequential = false,
        CancellationToken token = default) =>
        RunLibraryWriteAsync(doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(songs is not null && songs.Count > 0, "INVALID_REQUEST");
            ValidateQueueSource(queueSource, "INVALID_REQUEST");
            MusicRules.Require(expectedVersion == queueVersion, "REVISION_CONFLICT");

            var q = Queue(doc);
            var parsedContext = context ?? MusicPlayContextRecord.Empty;
            var (updated, added) = AppendEntries(doc, q, songs, parsedContext, next: false);
            var entryIds = added.Select(e => e.EntryId).ToArray();
            if (selectIndex is { } index)
            {
                MusicRules.Require(index >= 0 && index < entryIds.Length);
                updated = SelectEntry(updated, entryIds[index], true, shuffleHistory);
                updated = ApplyQueueSelectionSource(updated);
                if (queueSource is not null) updated = updated with { Source = queueSource };
            }
            doc.SetQueue(updated);
            if (sequential) doc.SetPlaybackMode(new MusicPlaybackModeRecord(1));

            return (new MusicRecommendationQueueReceipt(
                true, "applied", updated.CurrentId, queueVersion, Mode(doc), null, entryIds, false), true);
        }, token, mutatesQueue: true);

    internal Task<MusicRecommendationQueueReceipt> RestoreRecommendationQueueAsync(
        long expectedVersion,
        string checkpoint,
        bool retainCurrent = false,
        CancellationToken token = default)
    {
        string cp = MusicRules.RequireId(checkpoint, 36);
        return RunLibraryWriteAsync(doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(expectedVersion == queueVersion, "REVISION_CONFLICT");
            MusicRules.Require(recommendationCheckpoints.TryGetValue(cp, out var saved), "CHECKPOINT_NOT_FOUND");

            var restored = saved!.Queue;
            var currentQueue = Queue(doc);
            if (retainCurrent && currentQueue.Entries.FirstOrDefault(e => e.EntryId == currentQueue.CurrentId) is { } retained)
            {
                var entries = restored.Entries.ToList();
                if (!entries.Any(e => e.EntryId == retained.EntryId))
                {
                    int anchor = entries.FindIndex(e => e.EntryId == restored.CurrentId);
                    entries.Insert(anchor + 1, retained);
                }
                restored = restored with { Entries = entries.ToArray() };
                restored = SelectEntry(restored, retained.EntryId, false, shuffleHistory);
            }
            Archive(currentQueue, doc);
            shuffleHistory = saved.History.ToList(); failedEntries.Clear(); failedEntries.UnionWith(saved.Failed);
            queueVersion++;
            restored = restored with { Serial = serial, Revision = queueVersion };
            doc.SetQueue(restored);
            doc.StageRecommendationCheckpoint(new MusicRecommendationCheckpointMutation(cp, null));

            return (new MusicRecommendationQueueReceipt(
                true, "applied", restored.CurrentId, queueVersion, Mode(doc), cp, [], true), true);
        }, token, mutatesQueue: true, onCommitted: () => recommendationCheckpoints.Remove(cp));
    }
}
