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
        s.Loading = true; s.Error = ""; s.Status = "loading"; s.Candidates = null;
        int generation = ++s.Generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        s.Cancellation = cancellation;
        long epoch = queue.Epoch;
        int modeIntent = s.ModeIntent;
        s.Flight = LaunchLocked(() => FetchAsync(s, generation, epoch, initial, automatic, modeIntent, cancellation));
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
    private async Task FetchAsync(Session s, int generation, long epoch, bool initial, bool automatic, int modeIntent,
        CancellationTokenSource cancellation)
    {
        bool entered = false;
        int navigation; lock (gate) navigation = s.Navigation;
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
            if (initial && s.Config.Kind is "queue" or "heartbeat")
            {
                seed = s.Config.Seed;
                string nativeSeed = s.Config.Heartbeat?.SongId ?? "";
                if (seed.Length == 0 && s.Config.Heartbeat?.Entry != MusicHeartbeatEntry.Favorites &&
                    before.Index >= 0 && NativeId(before.Entries[before.Index].Song) == nativeSeed)
                    seed = before.CurrentId;
                Require(s.Config.Heartbeat?.Entry != MusicHeartbeatEntry.CurrentSong || s.Config.Seed.Length == 0 ||
                    before.Index >= 0 && NativeId(before.Entries[before.Index].Song) == nativeSeed, "STALE_QUEUE");
                Require(seed.Length == 0 || before.Entries.Any(e => e.Id == seed) &&
                    (before.CurrentId == seed || s.Config.Kind == "queue" && !s.Config.Restore && s.FixedSeed.Length > 0), "STALE_QUEUE");
                if (s.Config.Kind == "queue")
                {
                    Require(seed.Length > 0, "STALE_QUEUE");
                    if (s.FixedSeed.Length == 0) s.FixedSeed = NativeId(before.Entries[before.Index].Song);
                    Require(s.FixedSeed.Length > 0, "NATIVE_ID_REQUIRED");
                }
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
            IEnumerable<string> known = initial && s.InitialAppend && s.Config.Kind == "fm" ? [] :
                initial && !s.InitialAppend && (s.Config.Kind != "queue" || s.Config.Restore)
                ? before.Entries.Where(e => e.Id == seed).Select(e => NativeId(e.Song))
                : before.Entries.Select(e => NativeId(e.Song));
            var songs = ParseSongs(response, request, known).ToArray();
            bool hasMore = request.Method != "queue-recommendations" ||
                Bool(response, "hasMore");
            int offset = request.QueueRecommendations is null ? 0 : checked((int)Number(response, "offset"));
            Require(!hasMore || request.QueueRecommendations is null || offset > request.QueueRecommendations.Offset, "PAGINATION_STALLED");
            var heartbeat = request.Heartbeat is { } intent ? ParseHeartbeat(response, intent) : s.Heartbeat;
            if (initial && s.Config.Kind == "heartbeat" && seed.Length == 0)
            {
                Require(response.TryGetProperty("entrySong", out var entrySong), "INVALID_RESPONSE");
                Require(s.Config.Heartbeat!.SongId is null || NativeId(entrySong) == s.Config.Heartbeat.SongId, "IDENTITY_MISMATCH");
                string entryId = NativeId(entrySong);
                var first = songs.FirstOrDefault(song => song.NativeId == entryId) is { } returned
                    ? returned with { Value = entrySong.Clone() }
                    : new Song(entrySong.Clone(), entryId, "", "",
                        response.GetProperty("context").Clone(), Json(new { id = entryId, source = "heartbeat" }));
                songs = new[] { first }.Concat(songs.Where(song => song.NativeId != entryId)).ToArray();
            }
            if (initial && songs.Length == 0) throw new RecommendationException("EMPTY_RECOMMENDATIONS");
            if (s.Config.Kind == "queue" && !s.Config.Restore && !s.RecommendationStarted ||
                !initial && (!s.Continue || s.ModeIntent != modeIntent || songs.Length == 0))
            {
                await queue.InLaneAsync(async (owner, ct) =>
                {
                    Check(s, generation, epoch, ct);
                    var actual = await ReadAsync(owner, ct).ConfigureAwait(false);
                    lock (gate)
                    {
                        Check(s, generation, epoch, ct);
                        s.Candidates = s.Config.Kind == "queue" && songs.Length > 0
                            ? new(epoch, request, songs.Select(song => new Candidate(Guid.NewGuid().ToString("N"), song)).ToArray(), hasMore, offset, modeIntent)
                            : null;
                        s.Exhausted = songs.Length == 0;
                        if (songs.Length == 0) s.Offset = offset;
                        s.Queue = actual; s.InitialPending = false; s.Failures = 0;
                        CommitSourceLocked(s);
                        s.Status = songs.Length == 0 ? "empty" : "ready"; s.Error = ""; PublishLocked();
                    }
                    return true;
                }, token).ConfigureAwait(false);
                return;
            }
            if (songs.Length == 0) throw new RecommendationException("EMPTY_RECOMMENDATIONS");
            var operation = new PendingOperation(Guid.NewGuid().ToString("D"),
                initial && !s.InitialAppend ? "enter" : "append", songs, request, seed,
                initial && (seed.Length == 0 || s.InitialAppend), automatic, epoch,
                initial && s.InitialAppend ? 0 : null, hasMore, heartbeat, offset, navigation,
                !initial ? modeIntent : null);
            bool committed = await CommitBatchAsync(s, operation, before, generation, epoch, token).ConfigureAwait(false);
            MusicRecommendationSelection? selection = null;
            lock (gate)
            {
                if (committed && ValidLocked(s, generation, epoch) && s.Navigation == navigation && operation.Select && s.Queue is { } q)
                    selection = SelectionLocked(s, q.CurrentId, s.Play, automatic, false);
            }
            if (selection is not null) await SelectAsync(s, selection, token).ConfigureAwait(false);
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
                        s.Failures++;
                    }
                    if (s.Pending is null && s.PreviousSession is { } previous)
                    {
                        s.PreviousSession = null;
                        s.Active = false; s.Checkpoint = "";
                        session = previous;
                        previous.Error = s.Error; previous.Status = "error";
                        PublishLocked();
                    }
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
                    s.Cancellation = null; s.Loading = false; s.CommittingInitial = false;
                    if (s.Status == "loading") s.Status = s.Active ? "ready" : "cancelled";
                    if (ReferenceEquals(s, session)) PublishLocked();
                }
            }
            cancellation.Dispose();
        }
    }
    private static MusicRecommendationRequest Request(Session s, QueueSnapshot snapshot, bool initial)
    {
        string[] known = snapshot.Entries.Select(e => NativeId(e.Song)).Where(id => id.Length > 0).Distinct().ToArray();
        if (s.Config.Kind == "fm") return new(s.Config.Fm! with { UnplaySongIds = initial ? [] : known });
        if (s.Config.Kind == "similar") return new(new MusicSimilarRequest(s.Config.Platform, s.Config.SourceIds,
            s.Config.SourceType, initial ? [] : known));
        if (s.Config.Kind == "heartbeat")
        {
            if (initial) return new(s.Config.Heartbeat!);
            Require(s.Heartbeat is not null && snapshot.Entries.Length > 0, "NATIVE_ID_REQUIRED");
            var context = s.Heartbeat!;
            string song = NativeId(snapshot.Entries[^1].Song);
            Require(ValidId(song), "NATIVE_ID_REQUIRED");
            return new(s.Config.Heartbeat! with { SongId = song, PlaylistId = context.PlaylistId,
                Continuation = new(context.PlaylistId, context.StartMusicId, context.AccountId, context.Count) });
        }
        Require(s.Config.Kind == "queue" && ValidId(s.FixedSeed), "NATIVE_ID_REQUIRED");
        return new(new MusicQueueRecommendationsRequest(s.Config.Platform, s.FixedSeed, s.Offset));
    }
    private static IEnumerable<Song> ParseSongs(JsonElement response, MusicRecommendationRequest request, IEnumerable<string> known)
    {
        Require(response.ValueKind == JsonValueKind.Object &&
            System.Text.Encoding.UTF8.GetByteCount(response.GetRawText()) <= 512 * 1024, "INVALID_RESPONSE");
        Require(Text(response, "platform") == request.Platform && Text(response, "kind") == request.Method, "IDENTITY_MISMATCH");
        Require(response.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array, "INVALID_RESPONSE");
        if (request.Method == "fm")
            Require(rows.GetArrayLength() <= (request.Fm!.RuleId is not null || request.Fm.TabTagId is not null ? 5 : 3), "INVALID_RESPONSE");
        if (request.Method == "similar") Require(rows.GetArrayLength() <= 3, "INVALID_RESPONSE");
        Require(Number(response, "count") == rows.GetArrayLength(), "INVALID_RESPONSE");
        Require(response.TryGetProperty("recommendations", out var metadata) && metadata.ValueKind == JsonValueKind.Array &&
            metadata.GetArrayLength() == rows.GetArrayLength(), "INVALID_RESPONSE");
        JsonElement? context = response.TryGetProperty("context", out var sourceContext) &&
            sourceContext.ValueKind == JsonValueKind.Object ? sourceContext.Clone() : null;
        var provenance = new Dictionary<string, (string Algorithm, string FeedbackSource, JsonElement Metadata)>(StringComparer.Ordinal);
        foreach (var item in metadata.EnumerateArray())
        {
            string id = Text(item, "id");
            string feedbackSource = Text(item, "feedbackSource", "", 512);
            Require(ValidId(id) && Text(item, "source") == request.Method &&
                provenance.TryAdd(id, (Text(item, "alg", "", 4096), feedbackSource, item.Clone())), "IDENTITY_MISMATCH");
        }
        var seen = new HashSet<string>(known.Where(id => id.Length > 0), StringComparer.Ordinal);
        var result = new List<Song>();
        foreach (var song in rows.EnumerateArray())
        {
            Require(song.ValueKind == JsonValueKind.Object && Text(song, "title", "", 2048).Length > 0 &&
                Text(song, "artist", "", 2048).Length > 0, "INVALID_RESPONSE");
            string id = NativeId(song);
            Require(ValidId(id) && provenance.ContainsKey(id), "IDENTITY_MISMATCH");
            // Identity is the native recording, never just a normalized title/artist.
            if (seen.Add(id)) result.Add(new(song.Clone(), id, provenance[id].Algorithm, provenance[id].FeedbackSource,
                context, provenance[id].Metadata));
        }
        return result;
    }
    private static HeartbeatContext ParseHeartbeat(JsonElement response, MusicHeartbeatRequest request)
    {
        Require(response.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
        string playlist = Text(context, "playlistId"), start = Text(context, "startMusicId"), song = Text(context, "songId"),
            account = Text(context, "accountId");
        bool fromHome = Bool(context, "fromHome");
        long count = Number(context, "count");
        Require(ValidId(playlist) && ValidId(start) && ValidId(song) &&
            account.Length is > 0 and <= 20 && account[0] != '0' && account.All(char.IsAsciiDigit) && count > 0 &&
            fromHome == (request.Entry == MusicHeartbeatEntry.Favorites) &&
            (request.SongId is null || song == request.SongId) &&
            (request.PlaylistId is null || playlist == request.PlaylistId) &&
            (request.Continuation is null ? start == song : playlist == request.Continuation.PlaylistId &&
                start == request.Continuation.StartMusicId && account == request.Continuation.AccountId &&
                count == request.Continuation.Count), "IDENTITY_MISMATCH");
        return new(playlist, start, song, fromHome, count, account);
    }
    private static JsonElement Context(Session s, HeartbeatContext? heartbeat) =>
        Json(new { sourceTitle = s.Config.Kind switch { "fm" => "私人漫游", "queue" => "单曲推荐",
                "heartbeat" => "心动", _ => s.Config.SourceType == "artist" ? "相似艺人" : "相似歌曲" },
            sourcePlatform = s.Config.Platform,
            sourceType = s.Config.Kind == "heartbeat" ? "playlist" : s.Config.Kind,
            sourceId = s.Config.Kind == "fm" ? s.Id :
                heartbeat?.PlaylistId ?? s.Config.SourceIds.FirstOrDefault() ?? s.FixedSeed,
            queueSource = new { id = s.Id, title = s.Config.Kind switch { "fm" => "私人漫游", "queue" => "单曲推荐",
                "heartbeat" => "心动", _ => s.Config.SourceType == "artist" ? "相似艺人" : "相似歌曲" },
                kind = s.Config.Kind, platform = s.Config.Platform } });
    private Task<bool> CommitBatchAsync(Session s, PendingOperation operation, QueueSnapshot before, int generation,
        long epoch, CancellationToken token) => queue.InLaneAsync(async (owner, ct) =>
    {
        Check(s, generation, epoch, ct);
        var actual = await ReadAsync(owner, ct).ConfigureAwait(false);
        lock (gate)
        {
            Check(s, generation, epoch, ct);
            if (WithholdBatchLocked(s, operation, actual, epoch)) return false;
        }
        if (operation.Action == "enter")
            Require(actual.Version == before.Version && actual.CurrentId == before.CurrentId, "STALE_QUEUE");
        if (operation.Action == "append" && !(s.Config.Kind == "fm" && s.InitialAppend && s.InitialPending))
        {
            var known = actual.Entries.Select(entry => NativeId(entry.Song)).ToHashSet(StringComparer.Ordinal);
            string? selected = operation.SelectIndex is { } index ? operation.Songs[index].NativeId : null;
            var songs = operation.Songs.Where(song => !known.Contains(song.NativeId)).ToArray();
            if (songs.Length == 0)
            {
                lock (gate)
                {
                    Check(s, generation, epoch, ct);
                    Require(!s.InitialPending, "EMPTY_RECOMMENDATIONS");
                    s.Candidates = null; s.Exhausted = true; s.Offset = operation.Offset;
                    s.Queue = actual; s.Status = "empty"; s.Error = ""; PublishLocked();
                }
                return false;
            }
            int? selection = selected is null ? null : Array.FindIndex(songs, song => song.NativeId == selected);
            Require(selection is null or >= 0, "STALE_CANDIDATE");
            operation = operation with { Songs = songs, SelectIndex = selection };
        }
        lock (gate)
            if (operation.Navigation is { } navigation && s.Navigation != navigation)
                operation = operation with { Select = false, SelectIndex = null };
        var parameters = new JsonObject
        {
            ["nonce"] = operation.Nonce, ["expectedVersion"] = actual.Version,
            ["songs"] = new JsonArray(operation.Songs.Select(x => JsonNode.Parse(x.Value.GetRawText())).ToArray()),
            ["context"] = JsonNode.Parse(Context(s, operation.Heartbeat).GetRawText())
        };
        if (operation.Action == "enter")
        {
            parameters["capture"] = s.Config.Restore;
            if (operation.Seed.Length > 0) parameters["seedEntryId"] = operation.Seed;
            if (s.Checkpoint.Length > 0) parameters["checkpoint"] = s.Checkpoint;
        }
        else if (operation.SelectIndex is { } index) parameters["selectIndex"] = index;
        lock (gate)
            if (s.InitialPending && s.Continue) parameters["sequential"] = true;
        Task<JsonElement> admission;
        lock (gate)
        {
            Check(s, generation, epoch, ct);
            if (WithholdBatchLocked(s, operation, actual, epoch)) return false;
            s.CommittingInitial = s.InitialPending;
            admission = owner.ExecuteAsync("music.queue.recommendation." + operation.Action, parameters, ct);
        }
        var result = await admission.ConfigureAwait(false);
        if (!result.GetProperty("ok").GetBoolean())
        {
            string code = Text(result, "code");
            if (code is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT") MarkUnknown(s, operation, code);
            throw new RecommendationException(code);
        }
        var after = await ReadAsync(owner, CancellationToken.None).ConfigureAwait(false);
        ApplyCommitted(s, operation, result, after, epoch);
        return true;
    }, token);

    private bool WithholdBatchLocked(Session s, PendingOperation operation, QueueSnapshot actual, long epoch)
    {
        if (operation.ModeIntent is not { } intent || s.Continue && s.ModeIntent == intent) return false;
        if (s.Config.Kind == "queue")
            s.Candidates = new(epoch, operation.Request!, operation.Songs.Select(song =>
                new Candidate(Guid.NewGuid().ToString("N"), song)).ToArray(), operation.HasMore, operation.Offset, intent);
        s.Queue = actual; s.Status = "ready"; PublishLocked();
        return true;
    }

    private Task BeginChooseLocked(Session s, string candidateId, CancellationToken caller)
    {
        var batch = s.Candidates;
        Require(batch is not null, "STALE_CANDIDATE");
        int index = Array.FindIndex(batch!.Items, c => c.Id == candidateId);
        Require(index >= 0, "STALE_CANDIDATE");
        return BeginCandidatesLocked(s, batch, index, caller);
    }
    private Task BeginCandidatesLocked(Session s, CandidateBatch batch, int? index, CancellationToken caller, bool automatic = false)
    {
        Require(batch.Epoch == queue.Epoch, "STALE_CANDIDATE");
        s.Loading = true; s.Error = ""; s.Status = "loading";
        if (index is not null) { s.Play = true; s.MediaIntent++; s.Navigation++; }
        int generation = ++s.Generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        s.Cancellation = cancellation;
        int modeIntent = s.ModeIntent;
        s.Flight = LaunchLocked(() => CommitCandidatesAsync(s, batch, index, generation, automatic, modeIntent, cancellation));
        PublishLocked();
        return s.Flight;
    }
    private async Task CommitCandidatesAsync(Session s, CandidateBatch batch, int? index, int generation, bool automatic,
        int modeIntent, CancellationTokenSource cancellation)
    {
        bool entered = false;
        int navigation; lock (gate) navigation = s.Navigation;
        try
        {
            await physical.WaitAsync(cancellation.Token).ConfigureAwait(false); entered = true;
            var operation = new PendingOperation(Guid.NewGuid().ToString("D"), "append",
                batch.Items.Select(c => c.Song).ToArray(), batch.Request, "", index is not null, index is null,
                batch.Epoch, index, batch.HasMore, Offset: batch.Offset, Navigation: navigation,
                ModeIntent: automatic ? modeIntent : null);
            var before = await queue.InLaneAsync(ReadAsync, cancellation.Token).ConfigureAwait(false);
            lock (gate) if (automatic && !s.Continue) return;
            bool committed = await CommitBatchAsync(s, operation, before, generation, batch.Epoch, cancellation.Token).ConfigureAwait(false);
            MusicRecommendationSelection? selection = null;
            lock (gate)
                if (committed && index is not null && ValidLocked(s, generation, batch.Epoch) &&
                    s.Navigation == navigation && s.Queue is { } q)
                    selection = SelectionLocked(s, q.CurrentId, s.Play, false, false);
            if (selection is not null) await SelectAsync(s, selection, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (gate)
                if (ValidLocked(s, generation, batch.Epoch))
                {
                    s.Error = ErrorCode(error); s.Status = s.Pending is null ? "error" : "commit-unknown";
                    s.Candidates = null; PublishLocked();
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
                }
            }
            cancellation.Dispose();
        }
    }
    private void MarkUnknown(Session s, PendingOperation operation, string code)
    {
        lock (gate)
        {
            s.Pending = operation; s.Error = code; s.Status = "commit-unknown";
            unresolved.Add(s);
            if (ReferenceEquals(s, session)) PublishLocked();
        }
    }
    private void ApplyCommitted(Session s, PendingOperation operation, JsonElement result, QueueSnapshot after, long epoch)
    {
        lock (gate)
        {
            if (result.TryGetProperty("checkpoint", out var checkpoint)) s.Checkpoint = checkpoint.GetString()!;
            if (operation.Action == "restore") { s.Checkpoint = ""; return; }
            if (operation.Action == "select") { s.Queue = after; return; }
            // Remember the checkpoint even if the admitted CAS outlived retirement;
            // cleanup must release it without restoring stale feedback or media.
            if (!CurrentLocked(s) || s.Returning || queue.Epoch != epoch) return;
            var ids = result.GetProperty("entryIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var entries = after.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            Require(ids.Length == operation.Songs.Length && ids.All(entries.Contains), "INVALID_RESPONSE");
            string batch = s.Id + ":" + ++s.Batch;
            s.Heartbeat = operation.Heartbeat ?? s.Heartbeat;
            CommitSourceLocked(s);
            for (int i = 0; i < ids.Length; i++)
            {
                var request = operation.Request!;
                s.Feedback[ids[i]] = new(s.Id, batch, ids[i], s.Config.Platform, s.Config.Kind,
                    request.Method == "fm" && (request.Fm!.RuleId is not null || request.Fm.TabTagId is not null) ? "rule" : request.Method,
                    s.Config.Kind == "fm" ? "fm" : s.Config.Kind == "heartbeat" ? "playlist" : "song",
                    request.Fm?.RuleId ?? request.Fm?.TabTagId ?? s.Heartbeat?.PlaylistId ??
                        s.Config.SourceIds.FirstOrDefault() ?? request.QueueRecommendations?.SeedSongId ?? "",
                    operation.Songs[i].Algorithm, request.Fm?.Mode.ToString().ToLowerInvariant() ?? "",
                    request.Fm?.Submode ?? "", true, s.Heartbeat?.FromHome ?? false, operation.Songs[i].FeedbackSource)
                { Context = operation.Songs[i].Context, Metadata = operation.Songs[i].Metadata };
            }
            s.Anchor = ids[^1]; s.Queue = after; s.QueueEpoch = epoch; s.Failures = 0;
            s.InitialPending = false; s.PreviousConfiguration = null;
            if (s.Config.Kind == "queue")
            {
                s.RecommendationStarted |= operation.Action == "enter" || s.Feedback.ContainsKey(after.CurrentId);
                s.Offset = operation.Offset;
            }
            s.Candidates = null; s.Exhausted = !operation.HasMore;
            s.Status = "ready"; s.Error = "";
            PublishLocked();
        }
    }
    private void CommitSourceLocked(Session s)
    {
        if (s.PreviousSession is not { } previous) return;
        s.PreviousSession = null;
        if (s.Checkpoint == previous.Checkpoint) previous.Checkpoint = "";
        RetireLocked(previous, "replaced");
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
                ? [s.Flight, next.Flight] : [s.Flight];
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
                        { recipient.Config = recipient.Config with { Fm = fm }; recipient.PreviousConfiguration = null; }
                    ApplyCommitted(recipient, pending!, result, after, pending!.QueueEpoch);
                    if (CurrentLocked(recipient)) recipient.Status = "ready";
                    if (pending.Action == "restore")
                    {
                        s.Returning = false; s.Active = false; s.Status = "returned"; s.Feedback.Clear();
                        s.Continue = false;
                    }
                }
                else
                {
                    recipient.Status = "not-applied"; recipient.Returning = false;
                    if (recipient.PreviousConfiguration is { } previousConfig)
                    { recipient.Config = previousConfig; recipient.PreviousConfiguration = null; recipient.InitialPending = false; }
                }
                if (!result.GetProperty("applied").GetBoolean() && recipient.PreviousSession is { } previous)
                {
                    recipient.PreviousSession = null; recipient.Active = false; recipient.Checkpoint = "";
                    session = previous; recipient = previous;
                }
                s.Pending = null; unresolved.Remove(s); s.Queue = after; s.Error = "";
                recipient.Queue = after; recipient.QueueEpoch = queue.Epoch; recipient.Error = "";
                if (pending!.Action != "restore")
                {
                    s.Play = false; recipient.Play = false; recipient.MediaIntent++;
                }
                if (!ReferenceEquals(recipient, s) && !s.Active && s.Checkpoint.Length > 0 &&
                    CurrentLocked(recipient) && recipient.Config.Restore && recipient.Checkpoint.Length == 0 &&
                    queue.Epoch == pending.QueueEpoch)
                { recipient.Checkpoint = s.Checkpoint; s.Checkpoint = ""; }
                if (ReferenceEquals(recipient, session)) PublishLocked();
            }
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
