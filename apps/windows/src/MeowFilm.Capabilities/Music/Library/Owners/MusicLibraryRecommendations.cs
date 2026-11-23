using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Native, owner-local capabilities. They are never serialized into the library
    // document or restored as a live recommendation/account session.
    private sealed record RecommendationCheckpoint(MusicQueueStateRecord Queue, string[] History, string[] Failed);
    private sealed record RecommendationReceipt(string Nonce, string Hash, JsonElement Result);
    private sealed record RecommendationCommit(
        string Nonce, string Hash, string Checkpoint,
        RecommendationCheckpoint? Capture, bool Consume, JsonElement Result,
        string[] BeforeHistory, string[] BeforeFailed, string[] AfterHistory, string[] AfterFailed);

    private readonly Dictionary<string, RecommendationCheckpoint> recommendationCheckpoints = [];
    private RecommendationReceipt? recommendationReceipt;
    private RecommendationCommit? recommendationStage;
    private Pending? recommendationUnknown;

    private static string RecommendationNonce(JsonElement p)
    {
        string value = MusicWireCodec.RequireIdProp(p, "nonce", 36, "INVALID_NONCE");
        MusicRules.Require(Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty &&
            value == id.ToString("D"), "INVALID_NONCE");
        return value;
    }

    private static string RecommendationHash(string method, JsonElement p)
    {
        var envelope = MusicWireCodec.BuildObject(w =>
        {
            w.WriteString("method", method);
            w.WritePropertyName("parameters");
            p.WriteTo(w);
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MusicRules.CanonicalElement(envelope))));
    }

    private bool RecommendationReplay(string nonce, string hash, out JsonElement result)
    {
        if (recommendationReceipt is { } receipt && receipt.Nonce == nonce)
        {
            MusicRules.Require(receipt.Hash == hash, "INVALID_NONCE");
            result = receipt.Result.Clone();
            return true;
        }
        result = default;
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
            if (commit.Capture is { } capture) recommendationCheckpoints.Add(commit.Checkpoint, capture);
            if (commit.Consume) recommendationCheckpoints.Remove(commit.Checkpoint);
        }
        JsonElement result;
        if (!applied)
        {
            result = MusicWireCodec.Ok(w =>
            {
                w.WriteString("nonce", commit.Nonce);
                w.WriteBoolean("applied", false);
                w.WriteString("state", "not-applied");
            });
        }
        else
        {
            string? currentId = currentQueue.CurrentId;
            result = MusicWireCodec.BuildObject(w =>
            {
                foreach (var prop in commit.Result.EnumerateObject())
                    if (prop.Name is not ("version" or "currentId"))
                        prop.WriteTo(w);
                w.WriteNumber("version", queueVersion);
                if (currentId is not null) w.WriteString("currentId", currentId); else w.WriteNull("currentId");
            });
        }
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
        recommendationReceipt = null;
        recommendationStage = null;
        recommendationUnknown = null;
    }

    private async Task<(JsonElement Result, bool Mutate)> RecommendationCommandAsync(
        string method, JsonElement p, MusicLibrarySession doc, CancellationToken token)
    {
        const string prefix = "music.queue.recommendation.";
        string action = method[prefix.Length..];

        if (action == "reconcile")
        {
            MusicWireCodec.RequireFields(p, "nonce");
            string nonce = RecommendationNonce(p);
            if (recommendationReceipt is { } receipt && receipt.Nonce == nonce)
                return (receipt.Result.Clone(), false);
            var unknown = recommendationUnknown;
            MusicRules.Require(unknown?.Recommendation?.Nonce == nonce, "OPERATION_NOT_FOUND");
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            SettleRecommendation(unknown);
            return (recommendationReceipt!.Result.Clone(), false);
        }

        MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");

        if (action == "discard")
        {
            MusicWireCodec.RequireFields(p, "checkpoint");
            bool discarded = recommendationCheckpoints.Remove(MusicWireCodec.RequireIdProp(p, "checkpoint", 36));
            return (MusicWireCodec.Ok(w => w.WriteBoolean("discarded", discarded)), false);
        }

        if (action == "capture")
        {
            MusicWireCodec.RequireFields(p, "nonce expectedVersion");
            string nonce = RecommendationNonce(p), hash = RecommendationHash(method, p);
            if (RecommendationReplay(nonce, hash, out var previous)) return (previous, false);
            MusicRules.Require(MusicWireCodec.RequireIntProp(p, "expectedVersion") == queueVersion, "REVISION_CONFLICT");
            var capture = CaptureRecommendation(doc);
            string key = Guid.NewGuid().ToString("D");
            var q = Queue(doc);
            var result = MusicWireCodec.Ok(w =>
            {
                WritePositionFields(w, q, doc);
                w.WriteString("checkpoint", key);
                w.WriteString("nonce", nonce);
                w.WriteBoolean("applied", true);
            });
            recommendationCheckpoints.Add(key, capture);
            recommendationReceipt = new RecommendationReceipt(nonce, hash, result.Clone());
            return (result, false);
        }

        MusicRules.Require(action is "enter" or "append" or "select" or "restore", "INVALID_REQUEST");
        MusicWireCodec.RequireFields(p, action is "enter" or "append" ? "nonce expectedVersion songs" :
            action == "select" ? "nonce expectedVersion id" : "nonce expectedVersion checkpoint");
        bool retainCurrent = action == "restore" && MusicWireCodec.OptionalBoolProp(p, "retainCurrent");
        bool sequential = MusicWireCodec.OptionalBoolProp(p, "sequential");

        string operation = RecommendationNonce(p), signature = RecommendationHash(method, p);
        if (RecommendationReplay(operation, signature, out var replay)) return (replay, false);
        MusicRules.Require(MusicWireCodec.RequireIntProp(p, "expectedVersion") == queueVersion, "REVISION_CONFLICT");

        string checkpoint = MusicWireCodec.HasNonNull(p, "checkpoint")
            ? MusicWireCodec.RequireIdProp(p, "checkpoint", 36) : "";
        RecommendationCheckpoint? captured = null;
        var beforeHistory = shuffleHistory.ToArray();
        var beforeFailed = failedEntries.ToArray();

        MusicQueueStateRecord finalQueue;
        Action<Utf8JsonWriter>? extraFields = null;

        if (action == "select")
        {
            var q = SelectEntry(Queue(doc), MusicWireCodec.RequireIdProp(p, "id"), true, shuffleHistory);
            q = ApplyQueueSelectionSource(q);
            doc.SetQueue(q);
            finalQueue = q;
        }
        else if (action == "append")
        {
            var q = Queue(doc);
            var context = MusicWireCodec.TryGet(p, "context", out var ctxEl)
                ? MusicWireCodec.ReadContext(ctxEl, out var queueSource)
                : (queueSource = null, MusicPlayContextRecord.Empty).Item2;
            var (updated, added) = AppendEntries(doc, q, ReadSongsParam(p, empty: false), context, next: false);
            var entryIds = added.Select(e => e.EntryId).ToArray();
            if (MusicWireCodec.HasNonNull(p, "selectIndex"))
            {
                int index = (int)MusicWireCodec.RequireIntProp(p, "selectIndex", 0, entryIds.Length - 1);
                updated = SelectEntry(updated, entryIds[index], true, shuffleHistory);
                updated = ApplyQueueSelectionSource(updated);
                if (queueSource is not null) updated = updated with { Source = queueSource };
            }
            doc.SetQueue(updated);
            finalQueue = updated;
            extraFields = w => MusicWireCodec.WriteStringArray(w, "entryIds", entryIds);
        }
        else if (action == "enter")
        {
            var songs = RegisterSongs(doc, ReadSongsParam(p, empty: false));
            var context = MusicWireCodec.TryGet(p, "context", out var ctxEl)
                ? MusicWireCodec.ReadContext(ctxEl, out var queueSource)
                : (queueSource = null, MusicPlayContextRecord.Empty).Item2;
            var old = Queue(doc);
            string seed = MusicWireCodec.HasNonNull(p, "seedEntryId") ? MusicWireCodec.RequireIdProp(p, "seedEntryId") : "";
            var retained = seed.Length == 0 ? null
                : old.Entries.SingleOrDefault(e => e.EntryId == seed && old.CurrentId == seed);
            MusicRules.Require(seed.Length == 0 || retained is not null, "STALE_QUEUE");
            bool capture = MusicWireCodec.OptionalBoolProp(p, "capture", fallback: true);
            if (checkpoint.Length == 0 && capture)
            {
                captured = CaptureRecommendation(doc);
                checkpoint = Guid.NewGuid().ToString("D");
            }
            else if (checkpoint.Length > 0)
                MusicRules.Require(recommendationCheckpoints.ContainsKey(checkpoint), "CHECKPOINT_NOT_FOUND");

            Archive(old, doc);
            var added = songs.Select(s => MakeEntry(s, context)).ToArray();
            var entries = retained is null ? added : [retained, .. added];
            shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
            var q = new MusicQueueStateRecord(
                retained?.EntryId ?? added[0].EntryId, serial, queueVersion, queueSource, entries, []);
            doc.SetQueue(q);
            finalQueue = q;
            var addedIds = added.Select(e => e.EntryId).ToArray();
            string cp = checkpoint;
            extraFields = w =>
            {
                w.WriteString("checkpoint", cp);
                MusicWireCodec.WriteStringArray(w, "entryIds", addedIds);
            };
        }
        else
        {
            MusicRules.Require(recommendationCheckpoints.TryGetValue(checkpoint, out var saved), "CHECKPOINT_NOT_FOUND");
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
            finalQueue = restored;
            string cp = checkpoint;
            extraFields = w =>
            {
                w.WriteString("checkpoint", cp);
                w.WriteBoolean("restored", true);
            };
        }

        if (sequential)
            doc.SetPlaybackMode(new MusicPlaybackModeRecord(1));

        var response = MusicWireCodec.Ok(w =>
        {
            WritePositionFields(w, finalQueue, doc);
            extraFields?.Invoke(w);
            w.WriteString("nonce", operation);
            w.WriteBoolean("applied", true);
        });

        recommendationStage = new RecommendationCommit(
            operation, signature, checkpoint, captured, action == "restore", response,
            beforeHistory, beforeFailed, shuffleHistory.ToArray(), failedEntries.ToArray());
        return (response, true);
    }
}
