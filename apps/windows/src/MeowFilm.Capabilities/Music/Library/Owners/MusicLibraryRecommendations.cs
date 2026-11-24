using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

internal sealed record MusicRecommendationQueueReceipt(
    string Nonce,
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
    // Native, owner-local capabilities. They are never serialized into the library
    // document or restored as a live recommendation/account session.
    private sealed record RecommendationCheckpoint(MusicQueueStateRecord Queue, string[] History, string[] Failed);
    private sealed record RecommendationReceipt(string Nonce, string Hash, MusicRecommendationQueueReceipt Result);
    private sealed record RecommendationCommit(
        string Nonce, string Hash, string Checkpoint,
        RecommendationCheckpoint? Capture, bool Consume, MusicRecommendationQueueReceipt Result,
        string[] BeforeHistory, string[] BeforeFailed, string[] AfterHistory, string[] AfterFailed);

    private readonly Dictionary<string, RecommendationCheckpoint> recommendationCheckpoints = [];
    private bool recommendationCheckpointsLoaded;
    private RecommendationReceipt? recommendationReceipt;
    private RecommendationCommit? recommendationStage;
    private Pending? recommendationUnknown;

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
        }, token, authoritativeFor: PendingOwner.Recommendation);

    internal async Task SaveRecommendationSessionAsync(MusicRecommendationSessionRecord? value, CancellationToken token = default)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (closed || preparing) return;
            await LoadAsync(token).ConfigureAwait(false);
            if (pending is { } attempt && IsAutoReconcilablePending(attempt))
                await ReconcileLockedAsync(token).ConfigureAwait(false);
            if (pending is not null || recommendationUnknown is not null) return;
            var candidate = session!.Candidate(true);
            candidate.SetRecommendationSession(value);
            await CommitAsync(candidate, PendingOwner.Recommendation, token).ConfigureAwait(false);
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
            }, token, authoritativeFor: PendingOwner.Recommendation);

    private static string RequireRecommendationNonce(string value)
    {
        MusicRules.Require(MusicRules.IsValidId(value, 36) &&
            Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty &&
            value == id.ToString("D"), "INVALID_NONCE");
        return value;
    }

    private static string RecommendationHash(string action, Action<Utf8JsonWriter> writePayload)
    {
        var envelope = MusicNodeSongCodec.BuildObject(w =>
        {
            w.WriteString("action", action);
            w.WritePropertyName("parameters");
            w.WriteStartObject();
            writePayload(w);
            w.WriteEndObject();
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MusicRules.CanonicalElement(envelope))));
    }

    private static void WriteRecommendationContextHash(Utf8JsonWriter w, MusicPlayContextRecord? context, MusicQueueSourceRecord? queueSource)
    {
        if (context is null && queueSource is null) return;
        w.WritePropertyName("context");
        w.WriteStartObject();
        if (context is not null)
        {
            MusicNodeSongCodec.WriteOptionalString(w, "playlistId", context.PlaylistId);
            MusicNodeSongCodec.WriteOptionalString(w, "sourceTitle", context.SourceTitle);
            MusicNodeSongCodec.WriteOptionalString(w, "playlist", context.Playlist);
            MusicNodeSongCodec.WriteOptionalString(w, "sourcePlatform", context.SourcePlatform);
            MusicNodeSongCodec.WriteOptionalString(w, "sourceType", context.SourceType);
            MusicNodeSongCodec.WriteOptionalString(w, "sourceId", context.SourceId);
            MusicNodeSongCodec.WriteOptionalString(w, "searchKeyword", context.SearchKeyword);
            MusicNodeSongCodec.WriteOptionalString(w, "dailyBranch", context.DailyBranch);
            MusicNodeSongCodec.WriteOptionalString(w, "heartbeatPlaylistId", context.HeartbeatPlaylistId);
            MusicNodeSongCodec.WriteOptionalString(w, "heartbeatAccountId", context.HeartbeatAccountId);
        }
        if (queueSource is not null)
        {
            w.WritePropertyName("queueSource");
            w.WriteStartObject();
            MusicNodeSongCodec.WriteOptionalString(w, "id", queueSource.Id);
            MusicNodeSongCodec.WriteOptionalString(w, "title", queueSource.Title);
            MusicNodeSongCodec.WriteOptionalString(w, "kind", queueSource.Kind);
            MusicNodeSongCodec.WriteOptionalString(w, "platform", queueSource.Platform);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private bool RecommendationReplay(string nonce, string hash, out MusicRecommendationQueueReceipt result)
    {
        if (recommendationReceipt is { } receipt && receipt.Nonce == nonce)
        {
            MusicRules.Require(receipt.Hash == hash, "INVALID_NONCE");
            result = receipt.Result;
            return true;
        }
        result = null!;
        return false;
    }

    private RecommendationCheckpoint CaptureRecommendation(MusicLibrarySession doc) =>
        new(Queue(doc), shuffleHistory.ToArray(), failedEntries.ToArray());

    private void AcceptRecommendation(RecommendationCommit commit, bool applied)
    {
        // Load durable queue first so its lazy RestoreRuntime cannot wipe the
        // reconciled runtime navigation or bump queueVersion afterwards.
        var currentQueue = Queue(session!);
        shuffleHistory = (applied ? commit.AfterHistory : commit.BeforeHistory).ToList();
        failedEntries.Clear();
        failedEntries.UnionWith(applied ? commit.AfterFailed : commit.BeforeFailed);
        if (applied)
        {
            if (commit.Capture is { } capture) recommendationCheckpoints[commit.Checkpoint] = capture;
            if (commit.Consume) recommendationCheckpoints.Remove(commit.Checkpoint);
        }
        MusicRecommendationQueueReceipt result = applied
            ? commit.Result with { Version = queueVersion, CurrentId = currentQueue.CurrentId }
            : new MusicRecommendationQueueReceipt(commit.Nonce, false, "not-applied", null, 0, 0, null, [], false);
        recommendationReceipt = new RecommendationReceipt(commit.Nonce, commit.Hash, result);
    }

    private void SettleRecommendation(Pending? attempt)
    {
        if (attempt?.Recommendation is not { } change) return;
        MusicRules.Require(revision == attempt.Before || revision == attempt.After, "STORAGE_CONFLICT");
        if (recommendationReceipt?.Nonce != change.Nonce)
            AcceptRecommendation(change, revision == attempt.After);
        if (ReferenceEquals(recommendationUnknown, attempt)) recommendationUnknown = null;
    }

    private void ClearRecommendations()
    {
        recommendationCheckpoints.Clear();
        recommendationCheckpointsLoaded = false;
        recommendationReceipt = null;
        recommendationStage = null;
        recommendationUnknown = null;
    }

    internal async Task<MusicRecommendationQueueReceipt> ReconcileRecommendationQueueAsync(string nonce, CancellationToken token = default)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            recommendationStage = null;
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            EnsureRecommendationCheckpointsLoaded(session!);
            string validNonce = RequireRecommendationNonce(nonce);
            if (recommendationReceipt is { } receipt && receipt.Nonce == validNonce)
                return receipt.Result;
            var unknown = recommendationUnknown;
            MusicRules.Require(unknown?.Recommendation?.Nonce == validNonce, "OPERATION_NOT_FOUND");
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            SettleRecommendation(unknown);
            return recommendationReceipt!.Result;
        }
        finally
        {
            recommendationStage = null;
            gate.Release();
        }
    }

    internal Task<bool> DiscardRecommendationCheckpointAsync(string checkpointId, CancellationToken token = default) =>
        RunLibraryWriteAsync(PendingOwner.Recommendation, doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            MusicRules.Require(MusicRules.IsValidId(checkpointId, 36));
            bool discarded = recommendationCheckpoints.ContainsKey(checkpointId);
            if (discarded)
                doc.StageRecommendationCheckpoint(new MusicRecommendationCheckpointMutation(checkpointId, null));
            return (discarded, discarded);
        }, token, onCommitted: () => recommendationCheckpoints.Remove(checkpointId));

    internal Task<MusicRecommendationQueueReceipt> EnterRecommendationQueueAsync(
        string nonce,
        long expectedVersion,
        IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null,
        MusicQueueSourceRecord? queueSource = null,
        string? seedEntryId = null,
        string? checkpoint = null,
        bool capture = true,
        bool sequential = false,
        CancellationToken token = default) =>
        RunLibraryWriteAsync(PendingOwner.Recommendation, doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            string operation = RequireRecommendationNonce(nonce);
            MusicRules.Require(songs is not null && songs.Count > 0, "INVALID_REQUEST");
            ValidateQueueSource(queueSource, "INVALID_REQUEST");
            string signature = RecommendationHash("enter", w =>
            {
                w.WriteString("nonce", operation);
                w.WriteNumber("expectedVersion", expectedVersion);
                w.WritePropertyName("songs");
                w.WriteStartArray();
                foreach (var s in songs) MusicNodeSongCodec.WriteSong(w, s);
                w.WriteEndArray();
                WriteRecommendationContextHash(w, context, queueSource);
                if (seedEntryId is not null) w.WriteString("seedEntryId", seedEntryId);
                if (checkpoint is not null) w.WriteString("checkpoint", checkpoint);
                w.WriteBoolean("capture", capture);
                w.WriteBoolean("sequential", sequential);
            });
            if (RecommendationReplay(operation, signature, out var replay)) return (replay, false);
            MusicRules.Require(expectedVersion == queueVersion, "REVISION_CONFLICT");

            string cp = checkpoint is { Length: > 0 } ? MusicRules.RequireId(checkpoint, 36) : "";
            RecommendationCheckpoint? captured = null;
            var beforeHistory = shuffleHistory.ToArray();
            var beforeFailed = failedEntries.ToArray();

            var parsedSongs = RegisterSongs(doc, songs);
            var parsedContext = context ?? MusicPlayContextRecord.Empty;
            var old = Queue(doc);
            string seed = seedEntryId is { Length: > 0 } ? MusicRules.RequireId(seedEntryId) : "";
            var retained = seed.Length == 0 ? null
                : old.Entries.SingleOrDefault(e => e.EntryId == seed && old.CurrentId == seed);
            MusicRules.Require(seed.Length == 0 || retained is not null, "STALE_QUEUE");
            if (cp.Length == 0 && capture)
            {
                captured = CaptureRecommendation(doc);
                cp = Guid.NewGuid().ToString("D");
                doc.StageRecommendationCheckpoint(new MusicRecommendationCheckpointMutation(
                    cp,
                    new MusicRecommendationCheckpointRecord(cp, captured.Queue, captured.History, captured.Failed)));
            }
            else if (cp.Length > 0)
                MusicRules.Require(recommendationCheckpoints.ContainsKey(cp), "CHECKPOINT_NOT_FOUND");

            Archive(old, doc);
            var added = parsedSongs.Select(s => MakeEntry(s, parsedContext)).ToArray();
            var entries = retained is null ? added : [retained, .. added];
            shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
            var finalQueue = new MusicQueueStateRecord(
                retained?.EntryId ?? added[0].EntryId, serial, queueVersion, queueSource, entries, []);
            doc.SetQueue(finalQueue);
            if (sequential) doc.SetPlaybackMode(new MusicPlaybackModeRecord(1));

            var addedIds = added.Select(e => e.EntryId).ToArray();
            var response = new MusicRecommendationQueueReceipt(
                operation, true, "applied", finalQueue.CurrentId, queueVersion, Mode(doc), cp, addedIds, false);
            recommendationStage = new RecommendationCommit(
                operation, signature, cp, captured, false, response,
                beforeHistory, beforeFailed, shuffleHistory.ToArray(), failedEntries.ToArray());
            return (response, true);
        }, token, mutatesQueue: true);

    internal Task<MusicRecommendationQueueReceipt> AppendRecommendationQueueAsync(
        string nonce,
        long expectedVersion,
        IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null,
        MusicQueueSourceRecord? queueSource = null,
        int? selectIndex = null,
        bool sequential = false,
        CancellationToken token = default) =>
        RunLibraryWriteAsync(PendingOwner.Recommendation, doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            string operation = RequireRecommendationNonce(nonce);
            MusicRules.Require(songs is not null && songs.Count > 0, "INVALID_REQUEST");
            ValidateQueueSource(queueSource, "INVALID_REQUEST");
            string signature = RecommendationHash("append", w =>
            {
                w.WriteString("nonce", operation);
                w.WriteNumber("expectedVersion", expectedVersion);
                w.WritePropertyName("songs");
                w.WriteStartArray();
                foreach (var s in songs) MusicNodeSongCodec.WriteSong(w, s);
                w.WriteEndArray();
                WriteRecommendationContextHash(w, context, queueSource);
                if (selectIndex is { } idx) w.WriteNumber("selectIndex", idx);
                w.WriteBoolean("sequential", sequential);
            });
            if (RecommendationReplay(operation, signature, out var replay)) return (replay, false);
            MusicRules.Require(expectedVersion == queueVersion, "REVISION_CONFLICT");

            var beforeHistory = shuffleHistory.ToArray();
            var beforeFailed = failedEntries.ToArray();
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

            var response = new MusicRecommendationQueueReceipt(
                operation, true, "applied", updated.CurrentId, queueVersion, Mode(doc), null, entryIds, false);
            recommendationStage = new RecommendationCommit(
                operation, signature, "", null, false, response,
                beforeHistory, beforeFailed, shuffleHistory.ToArray(), failedEntries.ToArray());
            return (response, true);
        }, token, mutatesQueue: true);

    internal Task<MusicRecommendationQueueReceipt> RestoreRecommendationQueueAsync(
        string nonce,
        long expectedVersion,
        string checkpoint,
        bool retainCurrent = false,
        CancellationToken token = default) =>
        RunLibraryWriteAsync(PendingOwner.Recommendation, doc =>
        {
            EnsureRecommendationCheckpointsLoaded(doc);
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            string operation = RequireRecommendationNonce(nonce);
            string cp = MusicRules.RequireId(checkpoint, 36);
            string signature = RecommendationHash("restore", w =>
            {
                w.WriteString("nonce", operation);
                w.WriteNumber("expectedVersion", expectedVersion);
                w.WriteString("checkpoint", cp);
                w.WriteBoolean("retainCurrent", retainCurrent);
            });
            if (RecommendationReplay(operation, signature, out var replay)) return (replay, false);
            MusicRules.Require(expectedVersion == queueVersion, "REVISION_CONFLICT");
            MusicRules.Require(recommendationCheckpoints.TryGetValue(cp, out var saved), "CHECKPOINT_NOT_FOUND");

            var beforeHistory = shuffleHistory.ToArray();
            var beforeFailed = failedEntries.ToArray();
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
            ValidateQueueState(restored, doc.Songs.Contains);
            Archive(currentQueue, doc);
            shuffleHistory = saved.History.ToList(); failedEntries.Clear(); failedEntries.UnionWith(saved.Failed);
            queueVersion++;
            restored = restored with { Serial = serial, Revision = queueVersion };
            doc.SetQueue(restored);
            doc.StageRecommendationCheckpoint(new MusicRecommendationCheckpointMutation(cp, null));

            var response = new MusicRecommendationQueueReceipt(
                operation, true, "applied", restored.CurrentId, queueVersion, Mode(doc), cp, [], true);
            recommendationStage = new RecommendationCommit(
                operation, signature, cp, null, true, response,
                beforeHistory, beforeFailed, shuffleHistory.ToArray(), failedEntries.ToArray());
            return (response, true);
        }, token, mutatesQueue: true);
}
