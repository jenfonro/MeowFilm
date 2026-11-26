using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Debug;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;

namespace MeowFilm.Capabilities.Recommendations;

public sealed partial class MusicRecommendationCoordinator
{
    private Task BeginBatchLocked(Session s, bool initial, bool automatic, CancellationToken caller = default)
    {
        if (s.Loading) return s.Flight;
        Require(CurrentLocked(s) && !s.Returning, "STALE_SESSION");
        s.Loading = true; s.Error = ""; s.Status = "loading"; s.Candidates = null;
        int generation = ++s.Generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        s.Cancellation = cancellation;
        long epoch = library.QueueEpoch;
        int modeIntent = s.ModeIntent;
        s.Flight = LaunchLocked(() => FetchAsync(s, generation, epoch, initial, automatic, modeIntent, cancellation));
        PublishLocked();
        return s.Flight;
    }
    private bool ValidLocked(Session s, int generation, long epoch) =>
        CurrentLocked(s) && !s.Returning && s.Generation == generation && library.QueueEpoch == epoch;
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
            await physical.WaitAsync(token).ConfigureAwait(false); entered = true;
            Check(s, generation, epoch, token);
            var before = await library.ReadRecommendationQueueSnapshotAsync(token).ConfigureAwait(false);
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
            var response = await readAsync(request, token).ConfigureAwait(false);
            Check(s, generation, epoch, token);
            IEnumerable<string> known = initial && s.InitialAppend && s.Config.Kind == "fm" ? [] :
                initial && !s.InitialAppend && (s.Config.Kind != "queue" || s.Config.Restore)
                ? before.Entries.Where(e => e.Id == seed).Select(e => NativeId(e.Song))
                : before.Entries.Select(e => NativeId(e.Song));
            var songs = ParseSongs(response, known).ToArray();
            bool hasMore = request.Method != "queue-recommendations" ||
                Bool(response, "hasMore");
            int offset = request.QueueRecommendations is null ? 0 : checked((int)Number(response, "offset"));
            Require(!hasMore || request.QueueRecommendations is null || offset > request.QueueRecommendations.Offset, "PAGINATION_STALLED");
            if (initial && s.Config.Kind == "heartbeat")
            {
                bool hasEntrySongProp = response.TryGetProperty("entrySong", out var entrySong);
                bool hasEntrySong = hasEntrySongProp && entrySong.ValueKind == JsonValueKind.Object;
                if (songs.Length == 0 && (seed.Length > 0 || !hasEntrySong))
                    throw new RecommendationException("EMPTY_RECOMMENDATIONS");
                if (seed.Length == 0)
                {
                    if (!hasEntrySong)
                        throw new RecommendationException(hasEntrySongProp && entrySong.ValueKind == JsonValueKind.Null
                            ? "EMPTY_RECOMMENDATIONS"
                            : "INVALID_RESPONSE");
                    var entryRecord = NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(entrySong));
                    string entryId = NativeId(entryRecord);
                    Require(response.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
                    var first = songs.FirstOrDefault(song => song.NativeId == entryId) is { } returned
                        ? returned with { Record = entryRecord }
                        : new Song(entryRecord, entryId, "", "", ctxEl.Clone(), null);
                    songs = new[] { first }.Concat(songs.Where(song => song.NativeId != entryId)).ToArray();
                }
            }
            if (initial && songs.Length == 0) throw new RecommendationException("EMPTY_RECOMMENDATIONS");
            var heartbeat = request.Heartbeat is not null ? ParseHeartbeat(response) : s.Heartbeat;
            bool searchPreview = s.Config.Kind == "queue" && !s.Config.Restore && !s.RecommendationStarted;
            if (searchPreview || !initial && (!s.Continue || s.ModeIntent != modeIntent || songs.Length == 0))
            {
                Check(s, generation, epoch, token);
                var actual = await library.ReadRecommendationQueueSnapshotAsync(token).ConfigureAwait(false);
                lock (gate)
                {
                    Check(s, generation, epoch, token);
                    int candidateIntent = searchPreview && s.Continue ? s.ModeIntent : modeIntent;
                    s.Candidates = s.Config.Kind == "queue" && songs.Length > 0
                        ? new(epoch, request, songs.Select(song => new Candidate(Guid.NewGuid().ToString("N"), song)).ToArray(), hasMore, offset, candidateIntent)
                        : null;
                    s.Exhausted = songs.Length == 0;
                    if (songs.Length == 0) s.Offset = offset;
                    s.Queue = actual; s.InitialPending = false; s.Failures = 0;
                    CommitSourceLocked(s);
                    s.Status = songs.Length == 0 ? "empty" : "ready"; s.Error = "";
                    DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "preview_settled",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = s.Id, ["kind"] = s.Config.Kind, ["songs"] = songs.Length,
                            ["continue"] = s.Continue, ["searchPreview"] = searchPreview
                        });
                    PublishLocked();
                }
                return;
            }
            if (songs.Length == 0) throw new RecommendationException("EMPTY_RECOMMENDATIONS");
            var operation = new PendingOperation(
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
                    s.Status = s.Error == "EMPTY_RECOMMENDATIONS" ? "empty" : "error";
                    if (s.PreviousConfiguration is { } previousConfig)
                    { s.Config = previousConfig; s.PreviousConfiguration = null; s.InitialPending = false; }
                    s.Failures++;
                    if (s.PreviousSession is { } previous)
                    {
                        s.PreviousSession = null;
                        s.Active = false; s.Checkpoint = "";
                        session = previous;
                        previous.Error = s.Error; previous.Status = "error";
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
                    if (ReferenceEquals(s, session))
                    {
                        PublishLocked();
                        if (s.Continue && s.Candidates is not null)
                            _ = MaybeBeginLocked(s);
                    }
                }
            }
            cancellation.Dispose();
        }
    }
    private static string[] QueueNativeIds(MusicRecommendationQueueSnapshot snapshot) =>
        snapshot.Entries.Select(e => NativeId(e.Song)).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    private static MusicRecommendationRequest Request(Session s, MusicRecommendationQueueSnapshot snapshot, bool initial)
    {
        if (s.Config.Kind == "fm") return new(s.Config.Fm! with { UnplaySongIds = initial ? [] : QueueNativeIds(snapshot) });
        if (s.Config.Kind == "similar") return new(new MusicSimilarRequest(s.Config.Platform, s.Config.SourceIds,
            s.Config.SourceType, initial ? [] : QueueNativeIds(snapshot)));
        if (s.Config.Kind == "heartbeat")
        {
            if (initial) return new(s.Config.Heartbeat!);
            Require(s.Heartbeat is not null && snapshot.Entries.Length > 0, "NATIVE_ID_REQUIRED");
            var context = s.Heartbeat!;
            string song = NativeId(snapshot.Entries[^1].Song);
            Require(song.Length > 0, "NATIVE_ID_REQUIRED");
            return new(s.Config.Heartbeat! with { SongId = song, PlaylistId = context.PlaylistId,
                Continuation = new(context.PlaylistId, context.StartMusicId, context.AccountId, context.Count) });
        }
        Require(s.Config.Kind == "queue" && s.FixedSeed.Length > 0, "NATIVE_ID_REQUIRED");
        return new(new MusicQueueRecommendationsRequest(s.Config.Platform, s.FixedSeed, s.Offset));
    }
    private static IEnumerable<Song> ParseSongs(JsonElement response, IEnumerable<string> known)
    {
        Require(response.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array, "INVALID_RESPONSE");
        JsonElement? context = response.TryGetProperty("context", out var sourceContext) &&
            sourceContext.ValueKind == JsonValueKind.Object ? sourceContext.Clone() : null;
        var provenance = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (response.TryGetProperty("recommendations", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
        foreach (var item in metadata.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("id", out var key) && key.ValueKind == JsonValueKind.String)
                provenance[key.GetString()!] = item;
        }
        var seen = new HashSet<string>(known.Where(id => id.Length > 0), StringComparer.Ordinal);
        var result = new List<Song>();
        foreach (var song in rows.EnumerateArray())
        {
            var record = NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(song));
            string id = NativeId(record);
            Require(id.Length > 0, "NATIVE_ID_REQUIRED");
            if (seen.Add(id))
            {
                bool hasMetadata = provenance.TryGetValue(id, out var info);
                result.Add(new(record, id, hasMetadata ? Text(info, "alg") : "",
                    hasMetadata ? Text(info, "feedbackSource") : "", context,
                    hasMetadata ? info.Clone() : null));
            }
        }
        return result;
    }
    private static HeartbeatContext ParseHeartbeat(JsonElement response)
    {
        Require(response.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
        string playlist = Text(context, "playlistId"), start = Text(context, "startMusicId"), song = Text(context, "songId"),
            account = Text(context, "accountId");
        bool fromHome = Bool(context, "fromHome");
        long count = Number(context, "count");
        Require(playlist.Length > 0 && start.Length > 0 && song.Length > 0 && account.Length > 0, "INVALID_RESPONSE");
        return new(playlist, start, song, fromHome, count, account);
    }
    private static (MusicPlayContextRecord PlayContext, MusicQueueSourceRecord QueueSource) BuildContext(Session s, HeartbeatContext? heartbeat)
    {
        string title = s.Config.Kind switch
        {
            "fm" => "私人漫游",
            "queue" => "单曲推荐",
            "heartbeat" => "心动",
            _ => s.Config.SourceType == "artist" ? "相似艺人" : "相似歌曲"
        };
        string sourceType = s.Config.Kind == "heartbeat" ? "playlist" : s.Config.Kind;
        string sourceId = s.Config.Kind == "fm" ? s.Id :
            heartbeat?.PlaylistId ?? s.Config.SourceIds.FirstOrDefault() ?? s.FixedSeed;
        var playContext = MusicPlayContextRecord.Empty with
        {
            SourceTitle = title,
            SourcePlatform = s.Config.Platform,
            SourceType = sourceType,
            SourceId = sourceId
        };
        var queueSource = new MusicQueueSourceRecord(s.Id, title, s.Config.Kind, s.Config.Platform);
        return (playContext, queueSource);
    }
    private async Task<bool> CommitBatchAsync(Session s, PendingOperation operation, MusicRecommendationQueueSnapshot before, int generation,
        long epoch, CancellationToken token)
    {
        Check(s, generation, epoch, token);
        var actual = await library.ReadRecommendationQueueSnapshotAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            Check(s, generation, epoch, token);
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
                    Check(s, generation, epoch, token);
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
        var songRecords = operation.Songs.Select(x => x.Record).ToArray();
        var (playContext, queueSource) = BuildContext(s, operation.Heartbeat);
        bool sequential = false;
        lock (gate)
            if (s.InitialPending && s.Continue) sequential = true;
        Task<MusicRecommendationQueueReceipt> admission;
        lock (gate)
        {
            Check(s, generation, epoch, token);
            if (WithholdBatchLocked(s, operation, actual, epoch)) return false;
            s.CommittingInitial = s.InitialPending;
            admission = operation.Action == "enter"
                ? library.EnterRecommendationQueueAsync(
                    actual.Version, songRecords, playContext, queueSource,
                    seedEntryId: operation.Seed.Length > 0 ? operation.Seed : null,
                    checkpoint: s.Checkpoint.Length > 0 ? s.Checkpoint : null,
                    capture: s.Config.Restore, sequential: sequential, token: token)
                : library.AppendRecommendationQueueAsync(
                    actual.Version, songRecords, playContext, queueSource,
                    selectIndex: operation.SelectIndex, sequential: sequential, token: token);
        }
        MusicRecommendationQueueReceipt result;
        try
        {
            result = await admission.ConfigureAwait(false);
        }
        catch (MusicLibraryException error)
        {
            throw new RecommendationException(error.Code);
        }
        var after = await library.ReadRecommendationQueueSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        ApplyCommitted(s, operation, result, after, epoch);
        return true;
    }

    private bool WithholdBatchLocked(Session s, PendingOperation operation, MusicRecommendationQueueSnapshot actual, long epoch)
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
        Require(batch.Epoch == library.QueueEpoch, "STALE_CANDIDATE");
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
            var operation = new PendingOperation("append",
                batch.Items.Select(c => c.Song).ToArray(), batch.Request, "", index is not null, index is null,
                batch.Epoch, index, batch.HasMore, Offset: batch.Offset, Navigation: navigation,
                ModeIntent: automatic ? modeIntent : null);
            var before = await library.ReadRecommendationQueueSnapshotAsync(cancellation.Token).ConfigureAwait(false);
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
                    s.Error = ErrorCode(error); s.Status = "error";
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
    private void ApplyCommitted(Session s, PendingOperation operation, MusicRecommendationQueueReceipt result, MusicRecommendationQueueSnapshot after, long epoch)
    {
        lock (gate)
        {
            if (result.Checkpoint is { } checkpoint) s.Checkpoint = checkpoint;
            if (operation.Action == "restore") { s.Checkpoint = ""; return; }
            if (!CurrentLocked(s) || s.Returning || library.QueueEpoch != epoch) return;
            if (s.Status == "account-invalidated")
            {
                s.Queue = after; s.QueueEpoch = epoch; s.Anchor = after.Tail;
                s.Candidates = null;
                PublishLocked();
                return;
            }
            var ids = result.EntryIds;
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
            DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "batch_committed",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = s.Id, ["kind"] = s.Config.Kind, ["action"] = operation.Action,
                    ["added"] = ids.Length, ["checkpoint"] = s.Checkpoint, ["continue"] = s.Continue
                });
            SchedulePersistSessionLocked(s);
            PublishLocked();
        }
    }
    private void CommitSourceLocked(Session s)
    {
        if (s.PreviousSession is not { } previous) return;
        s.PreviousSession = null;
        if (s.Checkpoint == previous.Checkpoint) s.Checkpoint = "";
        RetireLocked(previous, "replaced");
    }
    private async Task DiscardAsync(Session s)
    {
        string checkpoint;
        lock (gate) checkpoint = s.Checkpoint;
        if (checkpoint.Length == 0) return;
        try
        {
            _ = await library.DiscardRecommendationCheckpointAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
            lock (gate) if (s.Checkpoint == checkpoint) s.Checkpoint = "";
        }
        catch (MusicLibraryException) { }
    }
}
