using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Native, owner-local capabilities. They are never serialized into the library
    // document or restored as a live recommendation/account session.
    private sealed record RecommendationCheckpoint(JsonObject Queue, string[] History, string[] Failed, int Bytes);
    private sealed record RecommendationReceipt(string Nonce, string Hash, JsonObject Result);
    private sealed record RecommendationCommit(string Nonce, string Hash, string Checkpoint,
        RecommendationCheckpoint? Capture, bool Consume, JsonObject Result,
        string[] BeforeHistory, string[] BeforeFailed, string[] AfterHistory, string[] AfterFailed);
    private readonly Dictionary<string, RecommendationCheckpoint> recommendationCheckpoints = [];
    private RecommendationReceipt? recommendationReceipt;
    private RecommendationCommit? recommendationStage;
    private Pending? recommendationUnknown;
    private const int RecommendationMemoryLimit = 8 * 1024 * 1024;

    private static string RecommendationNonce(JsonObject p)
    {
        string value = LJ.Text(p["nonce"], 36);
        LJ.Require(Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty &&
            value == id.ToString("D"), "INVALID_NONCE");
        return value;
    }
    private static string RecommendationHash(string method, JsonObject p) =>
        Convert.ToHexString(SHA256.HashData(LJ.Bytes(LJ.Obj(("method", method), ("parameters", p)))));
    private bool RecommendationReplay(string nonce, string hash, out JsonObject result)
    {
        if (recommendationReceipt is { } receipt && receipt.Nonce == nonce)
        {
            LJ.Require(receipt.Hash == hash, "INVALID_NONCE");
            result = (JsonObject)receipt.Result.DeepClone();
            return true;
        }
        result = new();
        return false;
    }
    private RecommendationCheckpoint CaptureRecommendation(JsonObject doc)
    {
        var q = (JsonObject)Queue(doc).DeepClone();
        int bytes = LJ.Bytes(q).Length;
        LJ.Require(recommendationCheckpoints.Values.Sum(c => c.Bytes) + bytes <= RecommendationMemoryLimit, "LIMIT");
        return new(q, shuffleHistory.ToArray(), failedEntries.ToArray(), bytes);
    }
    private static JsonObject RecommendationResult(JsonObject result, string nonce, bool applied = true) =>
        LJ.Merge(result, LJ.Obj(("nonce", nonce), ("applied", applied)));
    private void AcceptRecommendation(RecommendationCommit commit, bool applied)
    {
        // Force-loading a durable envelope deliberately clears runtime navigation.
        // Reconciliation must reinstate the runtime belonging to the observed side,
        // not the speculative side of an unacknowledged CAS.
        shuffleHistory = (applied ? commit.AfterHistory : commit.BeforeHistory).ToList();
        failedEntries.Clear();
        failedEntries.UnionWith(applied ? commit.AfterFailed : commit.BeforeFailed);
        if (applied)
        {
            if (commit.Capture is { } capture) recommendationCheckpoints.Add(commit.Checkpoint, capture);
            if (commit.Consume) recommendationCheckpoints.Remove(commit.Checkpoint);
        }
        var result = applied ? (JsonObject)commit.Result.DeepClone() :
            LJ.Obj(("ok", true), ("nonce", commit.Nonce), ("applied", false), ("state", "not-applied"));
        if (applied)
        {
            result["version"] = queueVersion;
            result["currentId"] = Queue(document!)["currentId"]?.DeepClone();
        }
        recommendationReceipt = new(commit.Nonce, commit.Hash, result);
    }
    private void SettleRecommendation(Pending? attempt)
    {
        if (attempt?.Recommendation is not { } change) return;
        LJ.Require(fingerprint == attempt.Before || fingerprint == attempt.After, "STORAGE_CONFLICT");
        if (recommendationReceipt?.Nonce != change.Nonce)
            AcceptRecommendation(change, fingerprint == attempt.After);
        if (ReferenceEquals(recommendationUnknown, attempt)) recommendationUnknown = null;
    }
    private void ClearRecommendations()
    {
        recommendationCheckpoints.Clear();
        recommendationReceipt = null;
        recommendationStage = null;
        recommendationUnknown = null;
    }
    private async Task<(JsonObject Result, bool Mutate)> RecommendationCommandAsync(
        string method, JsonObject p, JsonObject doc, CancellationToken token)
    {
        const string prefix = "music.queue.recommendation.";
        string action = method[prefix.Length..];
        if (action == "reconcile")
        {
            LJ.Fields(p, "nonce", "nonce"); string nonce = RecommendationNonce(p);
            if (recommendationReceipt is { } receipt && receipt.Nonce == nonce)
                return ((JsonObject)receipt.Result.DeepClone(), false);
            var unknown = recommendationUnknown;
            LJ.Require(unknown?.Recommendation?.Nonce == nonce, "OPERATION_NOT_FOUND");
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            SettleRecommendation(unknown);
            return ((JsonObject)recommendationReceipt!.Result.DeepClone(), false);
        }
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        if (action == "discard")
        {
            LJ.Fields(p, "checkpoint", "checkpoint");
            return (LJ.Obj(("discarded", recommendationCheckpoints.Remove(LJ.Text(p["checkpoint"], 36)))), false);
        }
        if (action == "capture")
        {
            LJ.Fields(p, "nonce expectedVersion", "nonce expectedVersion");
            string nonce = RecommendationNonce(p), hash = RecommendationHash(method, p);
            if (RecommendationReplay(nonce, hash, out var previous)) return (previous, false);
            LJ.Require(LJ.I(p["expectedVersion"]) == queueVersion, "REVISION_CONFLICT");
            var capture = CaptureRecommendation(doc); string key = Guid.NewGuid().ToString("D");
            var result = RecommendationResult(LJ.Merge(Position(Queue(doc), doc), LJ.Obj(("checkpoint", key))), nonce);
            recommendationCheckpoints.Add(key, capture);
            recommendationReceipt = new(nonce, hash, (JsonObject)result.DeepClone());
            return (result, false);
        }
        LJ.Require(action is "enter" or "append" or "select" or "restore", "INVALID_REQUEST");
        LJ.Fields(p, action switch
        {
            "enter" => "nonce expectedVersion checkpoint seedEntryId songs context",
            "append" => "nonce expectedVersion songs context selectIndex",
            "select" => "nonce expectedVersion id",
            _ => "nonce expectedVersion checkpoint retainCurrent"
        }, action is "enter" or "append" ? "nonce expectedVersion songs" :
            action == "select" ? "nonce expectedVersion id" : "nonce expectedVersion checkpoint");
        if (action == "restore" && p.ContainsKey("retainCurrent"))
            LJ.Require(LJ.IsBool(p["retainCurrent"]));
        string operation = RecommendationNonce(p), signature = RecommendationHash(method, p);
        if (RecommendationReplay(operation, signature, out var replay)) return (replay, false);
        LJ.Require(LJ.I(p["expectedVersion"]) == queueVersion, "REVISION_CONFLICT");
        string checkpoint = p["checkpoint"] is null ? "" : LJ.Text(p["checkpoint"], 36);
        RecommendationCheckpoint? captured = null;
        var beforeHistory = shuffleHistory.ToArray(); var beforeFailed = failedEntries.ToArray();
        JsonObject response;
        if (action == "select")
        {
            response = RecommendationResult(QueueCommand("music.queue.select", LJ.Obj(("id", p["id"])), doc, false).Result, operation);
        }
        else if (action == "append")
        {
            // The existing queue algorithm remains the only append implementation.
            // The nonce only makes an ACK-lost append explicitly reconcilable.
            int offset = LJ.Array(Queue(doc)["entries"]).Count;
            var arguments = LJ.Obj(("songs", p["songs"]), ("context", p["context"]),
                ("expectedVersion", p["expectedVersion"]));
            var appended = QueueCommand("music.queue.append", arguments, doc, true).Result;
            // Full catalog appends omit IDs; this private receipt needs the new tail
            // occurrences for selection and live recommendation provenance.
            var entryIds = LJ.Arr(LJ.Array(Queue(doc)["entries"]).Skip(offset).Select(e => LJ.S(e?["id"])));
            response = RecommendationResult(LJ.Merge(appended, LJ.Obj(("entryIds", entryIds))), operation);
            if (p["selectIndex"] is not null)
            {
                var ids = LJ.Array(response["entryIds"]);
                int index = (int)LJ.I(p["selectIndex"], 0, ids.Count - 1);
                var selected = QueueCommand("music.queue.select", LJ.Obj(("id", ids[index])), doc, false);
                response = LJ.Merge(response, selected.Result);
            }
        }
        else if (action == "enter")
        {
            var songs = Songs(p, false, true);
            var context = Context(p["context"]); var old = Queue(doc);
            string seed = p["seedEntryId"] is null ? "" : LJ.Text(p["seedEntryId"]);
            var retained = seed.Length == 0 ? null : LJ.Array(old["entries"]).OfType<JsonObject>()
                .SingleOrDefault(e => LJ.S(e["id"]) == seed && LJ.S(old["currentId"]) == seed);
            LJ.Require(seed.Length == 0 || retained is not null, "STALE_QUEUE");
            if (checkpoint.Length == 0)
            {
                captured = CaptureRecommendation(doc);
                checkpoint = Guid.NewGuid().ToString("D");
            }
            else LJ.Require(recommendationCheckpoints.ContainsKey(checkpoint), "CHECKPOINT_NOT_FOUND");
            Archive(old, doc);
            var added = songs.Select(s => MakeEntry(s, context)).ToArray();
            var entries = new JsonArray();
            if (retained is not null) entries.Add(retained.DeepClone());
            foreach (var entry in added) entries.Add(entry.DeepClone());
            var q = LJ.Obj(("schema", 1), ("entries", entries), ("currentId", retained?["id"] ?? added[0]["id"]),
                ("priority", new JsonArray()), ("source", context["queueSource"]));
            Set(doc, "queue", q);
            shuffleHistory.Clear(); failedEntries.Clear(); queueVersion++;
            response = RecommendationResult(LJ.Merge(Position(q, doc),
                LJ.Obj(("checkpoint", checkpoint), ("entryIds", LJ.Arr(added.Select(e => LJ.S(e["id"])))))),
                operation);
        }
        else
        {
            LJ.Require(recommendationCheckpoints.TryGetValue(checkpoint, out var saved), "CHECKPOINT_NOT_FOUND");
            var restored = (JsonObject)saved!.Queue.DeepClone();
            var currentQueue = Queue(doc);
            if (LJ.B(p["retainCurrent"]) && LJ.Array(currentQueue["entries"]).FirstOrDefault(
                e => LJ.S(e?["id"]) == LJ.S(currentQueue["currentId"])) is { } retained)
            {
                var entries = LJ.Array(restored["entries"]);
                string current = LJ.S(retained["id"]);
                if (!entries.Any(e => LJ.S(e?["id"]) == current))
                {
                    int anchor = entries.Select(e => LJ.S(e?["id"])).ToList().IndexOf(LJ.S(restored["currentId"]));
                    entries.Insert(anchor + 1, retained.DeepClone());
                }
                Select(restored, current, false, shuffleHistory);
            }
            ValidateQueue(restored, true);
            Archive(currentQueue, doc);
            Set(doc, "queue", restored);
            shuffleHistory = saved.History.ToList(); failedEntries.Clear(); failedEntries.UnionWith(saved.Failed);
            queueVersion++;
            response = RecommendationResult(LJ.Merge(Position(restored, doc),
                LJ.Obj(("checkpoint", checkpoint), ("restored", true))), operation);
        }
        recommendationStage = new(operation, signature, checkpoint, captured, action == "restore", response,
            beforeHistory, beforeFailed, shuffleHistory.ToArray(), failedEntries.ToArray());
        return (response, true);
    }
}
