using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;
using static MeowFilm.Capabilities.Recommendations.MusicRecommendationQueuePort;

namespace MeowFilm.Capabilities.Recommendations;

public sealed partial class MusicRecommendationCoordinator
{
    private Task BeginBatchLocked(Session s, bool initial, bool automatic, CancellationToken caller = default)
    {
        if (s.Loading) return s.Flight;
        Require(CurrentLocked(s) && !s.Returning, "STALE_SESSION");
        Require(s.Pending is null, "COMMIT_UNKNOWN");
        s.Loading = true; s.Error = ""; s.Status = "loading";
        int generation = ++s.Generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        s.Cancellation = cancellation;
        long epoch = queue.Epoch;
        s.Flight = LaunchLocked(() => FetchAsync(s, generation, epoch, initial, automatic, cancellation));
        PublishLocked();
        return s.Flight;
    }
    private bool ValidLocked(Session s, int generation, long epoch) =>
        CurrentLocked(s) && !s.Returning && s.Generation == generation && queue.Epoch == epoch;
    private void Check(Session s, int generation, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) Require(ValidLocked(s, generation, epoch), "STALE_SESSION");
    }
    private async Task FetchAsync(Session s, int generation, long epoch, bool initial, bool automatic,
        CancellationTokenSource cancellation)
    {
        bool entered = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25), clock);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token);
        var token = combined.Token;
        try
        {
            await s.Predecessor.ConfigureAwait(false);
            lock (gate) Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            await physical.WaitAsync(token).ConfigureAwait(false); entered = true;
            Check(s, generation, epoch, token);
            var before = await queue.InLaneAsync(ReadAsync, token).ConfigureAwait(false);
            Check(s, generation, epoch, token);
            string seed = "";
            if (initial && s.Config.Kind != "fm")
            {
                seed = s.Config.Seed;
                string nativeSeed = s.Config.Heartbeat?.SongId ?? s.Config.SourceIds.FirstOrDefault() ?? "";
                if (seed.Length == 0 && s.Config.Heartbeat?.FromHome != true &&
                    before.Index >= 0 && NativeId(before.Entries[before.Index].Song) == nativeSeed)
                    seed = before.CurrentId;
                Require(seed.Length == 0 || before.CurrentId == seed && before.Entries.Any(e => e.Id == seed), "STALE_QUEUE");
            }
            var request = Request(s, before, initial);
            lock (gate)
            {
                if (!ValidLocked(s, generation, epoch)) return;
                s.Queue = before; s.QueueEpoch = epoch;
            }
            // No WaitAsync: cancellation is not evidence that the provider/HTTP physically settled.
            var response = await readAsync(request, token).ConfigureAwait(false);
            Check(s, generation, epoch, token);
            var known = initial && !s.InitialAppend
                ? before.Entries.Where(e => e.Id == seed).Select(e => NativeId(e.Song))
                : before.Entries.Select(e => NativeId(e.Song));
            var songs = ParseSongs(response, request, known).ToArray();
            if (songs.Length == 0) throw new RecommendationException("EMPTY_RECOMMENDATIONS");
            var operation = new PendingOperation(Guid.NewGuid().ToString("D"),
                initial && !s.InitialAppend ? "enter" : "append", songs, request, seed,
                initial && seed.Length == 0 || initial && s.InitialAppend, automatic, epoch);
            await queue.InLaneAsync(async (owner, ct) =>
            {
                Check(s, generation, epoch, ct);
                var actual = await ReadAsync(owner, ct).ConfigureAwait(false);
                Require(actual.Version == before.Version && actual.CurrentId == before.CurrentId, "STALE_QUEUE");
                var parameters = new JsonObject
                {
                    ["nonce"] = operation.Nonce, ["expectedVersion"] = actual.Version,
                    ["songs"] = new JsonArray(songs.Select(x => JsonNode.Parse(x.Value.GetRawText())).ToArray()),
                    ["context"] = JsonNode.Parse(Context(s).GetRawText())
                };
                if (operation.Action == "enter")
                {
                    if (seed.Length > 0) parameters["seedEntryId"] = seed;
                    if (s.Checkpoint.Length > 0) parameters["checkpoint"] = s.Checkpoint;
                }
                else parameters["selectFirst"] = operation.Select;
                Check(s, generation, epoch, ct);
                var result = await owner.ExecuteAsync("music.queue.recommendation." + operation.Action, parameters, ct).ConfigureAwait(false);
                if (!result.GetProperty("ok").GetBoolean())
                {
                    string code = Text(result, "code");
                    if (code is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT") MarkUnknown(s, operation, code);
                    throw new RecommendationException(code);
                }
                try
                {
                    var after = await ReadAsync(owner, CancellationToken.None).ConfigureAwait(false);
                    ApplyCommitted(s, operation, result, after, generation, epoch);
                    await owner.AcknowledgeRecommendationAsync(operation.Nonce).ConfigureAwait(false);
                }
                catch
                {
                    MarkUnknown(s, operation, "COMMIT_UNKNOWN");
                    throw;
                }
                return true;
            }, token).ConfigureAwait(false);
            MusicRecommendationSelection? selection = null;
            lock (gate)
            {
                if (ValidLocked(s, generation, epoch) && operation.Select && s.Queue is { } q)
                    selection = SelectionLocked(s, q.CurrentId, s.Play, automatic, false);
            }
            if (selection is not null) await SelectAsync(s, selection, token).ConfigureAwait(false);
            if (s.Config.Kind == "fm") await DiscardAsync(s).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (ValidLocked(s, generation, epoch))
                {
                    s.Error = deadline.IsCancellationRequested ? "TIMEOUT" : ErrorCode(error);
                    s.Status = s.Pending is not null ? "commit-unknown" :
                        s.Error == "EMPTY_RECOMMENDATIONS" ? "empty" : "error";
                    if (s.Pending is null)
                    {
                        if (s.PreviousConfiguration is { } previousConfig)
                        { s.Config = previousConfig; s.PreviousConfiguration = null; s.InitialPending = false; }
                        s.Failures = Math.Min(2, s.Failures + 1);
                        s.RetryAt = clock.GetUtcNow().AddSeconds(15);
                    }
                    if (s.Error is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "FORBIDDEN")
                        RetireLocked(s, s.Error, stopPlayback: s.Batch > 0);
                    PublishLocked();
                }
            }
        }
        finally
        {
            if (entered) physical.Release();
            lock (gate)
            {
                if (ReferenceEquals(s.Cancellation, cancellation))
                {
                    s.Cancellation = null; s.Loading = false;
                    if (s.Status == "loading") s.Status = s.Active ? "ready" : "cancelled";
                    if (ReferenceEquals(s, session)) PublishLocked();
                    if (CurrentLocked(s) && s.Status == "ready") MaybeBeginLocked(s);
                }
            }
            cancellation.Dispose();
        }
    }
    private static MusicRecommendationRequest Request(Session s, QueueSnapshot snapshot, bool initial)
    {
        if (s.Config.Kind == "fm") return new(s.Config.Fm!);
        if (s.Config.Kind == "similar") return new(new MusicSimilarRequest(s.Config.Platform, s.Config.SourceIds));
        if (initial) return new(s.Config.Heartbeat!);
        string[] ids = snapshot.Entries.Select(e => NativeId(e.Song)).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        string current = snapshot.Index < 0 ? "" : NativeId(snapshot.Entries[snapshot.Index].Song);
        Require(ids.Length is > 0 and <= 1000, "LIMIT");
        Require(current.Length > 0 && ids.Contains(current, StringComparer.Ordinal), "NATIVE_ID_REQUIRED");
        return new(new MusicQueueRecommendationsRequest(s.Config.Platform, ids, current));
    }
    private static IEnumerable<Song> ParseSongs(JsonElement response, MusicRecommendationRequest request, IEnumerable<string> known)
    {
        Require(response.ValueKind == JsonValueKind.Object &&
            System.Text.Encoding.UTF8.GetByteCount(response.GetRawText()) <= 512 * 1024, "INVALID_RESPONSE");
        Require(Text(response, "platform") == request.Platform && Text(response, "kind") == request.Method, "IDENTITY_MISMATCH");
        Require(response.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array, "INVALID_RESPONSE");
        int providerLimit = request.Method switch { "fm" => request.Fm!.RuleId is not null || request.Fm.TabTagId is not null ? 5 : 3,
            "similar" => 3, "heartbeat" => 100, _ => 1000 };
        Require(rows.GetArrayLength() <= providerLimit && Number(response, "count") == rows.GetArrayLength(), "INVALID_RESPONSE");
        Require(response.TryGetProperty("recommendations", out var metadata) && metadata.ValueKind == JsonValueKind.Array &&
            metadata.GetArrayLength() == rows.GetArrayLength(), "INVALID_RESPONSE");
        var algorithms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in metadata.EnumerateArray())
        {
            string id = Text(item, "id");
            Require(ValidId(id) && Text(item, "source") == request.Method && algorithms.TryAdd(id, Text(item, "alg", "", 4096)), "IDENTITY_MISMATCH");
        }
        var seen = new HashSet<string>(known.Where(id => id.Length > 0), StringComparer.Ordinal);
        var result = new List<Song>();
        foreach (var song in rows.EnumerateArray())
        {
            Require(song.ValueKind == JsonValueKind.Object && Text(song, "title", "", 2048).Length > 0 &&
                Text(song, "artist", "", 2048).Length > 0, "INVALID_RESPONSE");
            string id = NativeId(song);
            Require(ValidId(id) && algorithms.ContainsKey(id), "IDENTITY_MISMATCH");
            // Identity is the native recording, never just a normalized title/artist.
            if (seen.Add(id)) result.Add(new(song.Clone(), id, algorithms[id]));
        }
        return result.Take(request.Method == "fm" ? providerLimit : 3);
    }
    private static JsonElement Context(Session s) => s.Config.Kind == "fm" ? Json(new { sourceTitle = "FM" }) :
        Json(new { sourceTitle = s.Config.Kind == "heartbeat" ? "心动模式" : "相似推荐",
            sourceType = s.Config.Kind == "heartbeat" ? "playlist" : "song",
            sourceId = s.Config.Heartbeat?.PlaylistId ?? s.Config.SourceIds[0] });
    private void MarkUnknown(Session s, PendingOperation operation, string code)
    {
        lock (gate)
        {
            s.Pending = operation; s.Error = code; s.Status = "commit-unknown";
            unresolved.Add(s);
            if (ReferenceEquals(s, session)) PublishLocked();
        }
    }
    private void ApplyCommitted(Session s, PendingOperation operation, JsonElement result, QueueSnapshot after,
        int generation, long epoch)
    {
        lock (gate)
        {
            if (result.TryGetProperty("checkpoint", out var checkpoint)) s.Checkpoint = checkpoint.GetString()!;
            if (operation.Action == "restore") { s.Checkpoint = ""; return; }
            if (operation.Action == "select") { s.Queue = after; return; }
            // Remember the checkpoint even if the admitted CAS outlived retirement;
            // cleanup must then release it, but no stale feedback/selection is admitted.
            if (!ValidLocked(s, generation, epoch)) return;
            var ids = result.GetProperty("entryIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
            Require(ids.Length == operation.Songs.Length && ids.All(id => after.Entries.Any(e => e.Id == id)), "INVALID_RESPONSE");
            string batch = s.Id + ":" + ++s.Batch;
            for (int i = 0; i < ids.Length; i++)
            {
                var request = operation.Request!;
                s.Feedback[ids[i]] = new(s.Id, batch, ids[i], s.Config.Platform, s.Config.Kind,
                    request.Method == "fm" && (request.Fm!.RuleId is not null || request.Fm.TabTagId is not null) ? "rule" : request.Method,
                    s.Config.Kind == "fm" ? "fm" : s.Config.Kind == "heartbeat" ? "playlist" : "song",
                    request.Fm?.RuleId ?? request.Fm?.TabTagId ?? s.Config.Heartbeat?.PlaylistId ?? s.Config.SourceIds.FirstOrDefault() ?? "",
                    operation.Songs[i].Algorithm, request.Fm?.Mode.ToString().ToLowerInvariant() ?? "",
                    request.Fm?.Submode ?? "", true, s.Config.Heartbeat?.FromHome ?? false);
            }
            s.Anchor = ids[^1]; s.Queue = after; s.QueueEpoch = epoch; s.Failures = 0;
            s.InitialPending = false; s.PreviousConfiguration = null;
            s.RetryAt = DateTimeOffset.UnixEpoch; s.Status = "ready"; s.Error = "";
            PublishLocked();
        }
    }
    private async Task ReconcileOperationAsync(Session s, CancellationToken token)
    {
        PendingOperation? pending; Task[] settle;
        lock (gate)
        {
            pending = s.Pending;
            // An explicit read reconciliation cannot race the catch/finally of
            // the write whose ACK was lost, or a same-lineage replacement.
            settle = session is { } next && !ReferenceEquals(next, s) && next.Id == s.Id
                ? [s.Flight, s.Advance, next.Flight, next.Advance] : [s.Flight, s.Advance];
        }
        Require(pending is not null, "OPERATION_NOT_FOUND");
        await Task.WhenAll(settle).ConfigureAwait(false);
        await queue.InLaneAsync(async (owner, ct) =>
        {
            var result = Okay(await owner.ExecuteAsync("music.queue.recommendation.reconcile", new { nonce = pending!.Nonce }, ct).ConfigureAwait(false));
            var after = await ReadAsync(owner, ct).ConfigureAwait(false);
            lock (gate)
            {
                if (!ReferenceEquals(pending, s.Pending)) return false;
                var recipient = session is { } current && current.Id == s.Id && CurrentLocked(current) ? current : s;
                if (result.GetProperty("applied").GetBoolean())
                {
                    // A superseded request is never allowed to publish by itself.
                    // This explicit, physically settled reconciliation instead
                    // adopts the confirmed facts into the still-live FM lineage.
                    if (pending!.Request?.Fm is { } fm && CurrentLocked(recipient) && queue.Epoch == pending.QueueEpoch)
                        recipient.Config = recipient.Config with { Fm = fm };
                    ApplyCommitted(recipient, pending!, result, after, recipient.Generation, pending!.QueueEpoch);
                    if (CurrentLocked(recipient)) recipient.Status = "ready";
                    if (pending.Action == "restore") { s.Returning = false; s.Active = false; s.Status = "returned"; s.Feedback.Clear(); }
                }
                else
                {
                    recipient.Status = "not-applied"; recipient.Returning = false;
                    if (recipient.PreviousConfiguration is { } previousConfig)
                    { recipient.Config = previousConfig; recipient.PreviousConfiguration = null; recipient.InitialPending = false; }
                }
                s.Pending = null; unresolved.Remove(s); s.Queue = after; s.Error = ""; s.Play = false;
                recipient.Queue = after; recipient.QueueEpoch = queue.Epoch; recipient.Error = ""; recipient.Play = false;
                recipient.MediaIntent++;
                if (!s.Active && s.TransferTo is { } next && CurrentLocked(next) && next.Checkpoint.Length == 0)
                { next.Checkpoint = s.Checkpoint; s.Checkpoint = ""; }
                if (ReferenceEquals(recipient, session)) PublishLocked();
            }
            await owner.AcknowledgeRecommendationAsync(pending!.Nonce).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
        if (!s.Active || s.Config.Kind == "fm") await DiscardAsync(s).ConfigureAwait(false);
    }
    private async Task DiscardAsync(Session s)
    {
        string checkpoint;
        lock (gate) { if (s.Pending is not null) return; checkpoint = s.Checkpoint; }
        if (checkpoint.Length == 0) return;
        await queue.InLaneAsync(async (owner, token) =>
        {
            var result = await owner.ExecuteAsync("music.queue.recommendation.discard", new { checkpoint }, token).ConfigureAwait(false);
            if (result.GetProperty("ok").GetBoolean())
                lock (gate) if (s.Checkpoint == checkpoint) s.Checkpoint = "";
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
