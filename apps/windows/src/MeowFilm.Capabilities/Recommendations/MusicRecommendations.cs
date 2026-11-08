using System.Text.Json;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities;

public sealed record MusicRecommendationCandidate(string SessionId, string Id, SongRow Song)
{
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    public string Cover => Song.Cover;
    public string Duration => Song.Duration;
}

public sealed partial class MusicCapability
{
    private IMusicRecommendationSource RecommendationSource => application;
    private IDisposable? recommendationBinding;
    private readonly HashSet<Task> recommendationWork = [];
    private CancellationTokenSource recommendationLifetime = new();
    private JsonElement? recommendationSnapshot;
    private long recommendationRevision = -1, recommendationViewEpoch, recommendationCommandEpoch;
    private string recommendationError = "";
    private MusicRecommendationPlaybackState? recommendationObservation;
    private string? recommendationRetryObservation;
    private Task recommendationPreparation = Task.CompletedTask;
    private long recommendationDisplayedVersion = -1, recommendationDesiredVersion = -1;
    private bool recommendationDisplayRunning;
    private string recommendationCandidateContent = "";
    public IReadOnlyList<MusicRecommendationCandidate> RecommendationCandidates { get; private set; } = [];

    public bool HasRecommendationSession => recommendationSnapshot is { } value &&
        (RecText(value, "id").Length > 0 || RecIds(value).Length > 0);
    public bool HasRecommendationControls => HasRecommendationSession &&
        (RecText(recommendationSnapshot!.Value, "kind") != "queue" || CanReconcileRecommendation);
    public bool HasQueueRecommendations => HasActiveRecommendation &&
        RecText(recommendationSnapshot!.Value, "kind") == "queue";
    public bool CanChooseRecommendation => !stopping && HasQueueRecommendations && !RecommendationLoading &&
        !CanReconcileRecommendation;
    public string QueueRecommendationStatus => recommendationSnapshot is { } value &&
        RecText(value, "kind") == "queue" ? RecommendationStatus : "";
    public string QueueRecommendationEmptyText => !HasQueueRecommendations || RecommendationCandidates.Count > 0 ? "" :
        RecommendationLoading ? "正在加载推荐" : recommendationSnapshot is { } value && RecBool(value, "exhausted")
            ? "推荐已结束" : "暂无推荐";
    public bool HasActiveRecommendation => recommendationSnapshot is { } value && RecBool(value, "active");
    public bool RecommendationLoading => recommendationSnapshot is { } value && RecBool(value, "loading");
    public string RecommendationTitle => recommendationSnapshot is not { } value ? "" :
        RecText(value, "kind") switch { "fm" => "私人 FM", "similar" => "相似推荐", "heartbeat" => "心动模式",
            "queue" => "为你推荐", _ => "推荐" };
    public bool HasPlaybackFm => !stopping && HasActiveRecommendation && recommendationSnapshot is { } value &&
        RecText(value, "platform") == "wy" && RecText(value, "kind") == "fm" && RecText(value, "mode").Length > 0;
    public bool HasPlaybackHeartbeat => !stopping && HasActiveRecommendation && recommendationSnapshot is { } value &&
        RecText(value, "platform") == "wy" && RecText(value, "kind") == "heartbeat" &&
        value.TryGetProperty("batch", out var batch) && batch.TryGetInt32(out int count) && count > 0;
    public string PlaybackFmMode => HasPlaybackFm ? RecText(recommendationSnapshot!.Value, "mode") : "";
    public string PlaybackFmScene => HasPlaybackFm ? RecText(recommendationSnapshot!.Value, "submode") : "";
    public string PlaybackFmRuleId => HasPlaybackFm ? RecText(recommendationSnapshot!.Value, "ruleId") : "";
    public string PlaybackFmTabTagId => HasPlaybackFm ? RecText(recommendationSnapshot!.Value, "tabTagId") : "";
    public string PlaybackFmActionKey => HasPlaybackFm
        ? RecText(recommendationSnapshot!.Value, "id") + ":" + recommendationCommandEpoch + ":" + accountEpoch +
            ":" + PlaybackFmMode + ":" + PlaybackFmScene + ":" + PlaybackFmRuleId + ":" + PlaybackFmTabTagId
        : "";
    public string PlaybackModeText => !HasPlaybackFm ? HasPlaybackHeartbeat ? "心动模式" : ModeText :
        PlaybackFmRuleId.Length > 0 || PlaybackFmTabTagId.Length > 0 ? "频道漫游" :
        PlaybackFmMode == "scene" ? CatalogFmScenes.FirstOrDefault(choice => choice.Key == PlaybackFmScene)?.Name ?? "场景模式" :
        PlaybackFmModes.FirstOrDefault(choice => choice.Key == PlaybackFmMode)?.Name ?? "私人漫游";
    public string PlaybackModeIcon => HasPlaybackFm ? "fm" : HasPlaybackHeartbeat ? "heartbeat" :
        Mode switch { 0 => "order", 1 => "repeat", 2 => "shuffle", _ => "single" };
    public string PlaybackModeActionKey => recommendationCommandEpoch + ":" + accountEpoch + ":" + queueEpoch +
        ":" + PlaybackPresentationKey + ":" + (recommendationSnapshot is { } value ? RecText(value, "id") : "") +
        ":" + HasPlaybackFm + ":" + HasPlaybackHeartbeat;
    public string PlaybackFmTitle => PlaybackFmRuleId.Length > 0 || PlaybackFmTabTagId.Length > 0
        ? "频道漫游" : "私人漫游 · " + PlaybackModeText;
    public bool CanChangePlaybackMode => HasPlaybackFm ? !CanReconcileRecommendation : CanEdit;
    public bool CanTogglePlaybackHeartbeat => !CanReconcileRecommendation &&
        (HasPlaybackHeartbeat ? CanReturnRecommendation : CanHeartbeatCurrent && !RecommendationLoading);
    public MusicCatalogChoice[] PlaybackFmModes { get; } =
        [new("default", "默认模式"), new("familiar", "熟悉模式"), new("explore", "探索模式"), new("puzzle", "拼图模式")];
    public bool CanHeartbeatCurrent => CanPlay && OwnsCurrent && playback.HasMedia &&
        SongJson.Platform(current!.Raw) == "wy" && SongJson.NativeId(current.Raw).Length > 0;
    public string RecommendationStatus
    {
        get
        {
            if (recommendationError.Length > 0) return recommendationError;
            if (recommendationSnapshot is not { } value) return "";
            if (RecBool(value, "requiresReconcile") || RecIds(value).Length > 0) return "保存结果待核对";
            if (RecBool(value, "waiting")) return "正在等待推荐";
            if (RecBool(value, "loading")) return "正在加载推荐";
            if (RecText(value, "error") is { Length: > 0 } code) return ErrorText(new MusicOperationException(code));
            return RecText(value, "status") switch
            {
                "returned" => "已恢复原队列", "cancelled" => "已结束推荐",
                "queue-replaced" or "queue-empty" => "推荐已结束",
                "account-invalidated" or "AUTH_EXPIRED" or "AUTH_REQUIRED" => "账号已失效",
                "empty" => "暂无推荐", "remote" => "已停止本机推荐", _ => ""
            };
        }
    }
    public bool RecommendationContinue => recommendationSnapshot is { } value && RecBool(value, "continue");
    public bool CanChangeRecommendationContinue => !stopping && HasActiveRecommendation &&
        RecText(recommendationSnapshot!.Value, "kind") != "fm";
    public bool CanRetryRecommendation => !stopping && recommendationSnapshot is { } value && RecBool(value, "canRetry");
    public bool CanReturnRecommendation => !stopping && recommendationSnapshot is { } value && RecBool(value, "canReturn");
    public bool CanCancelRecommendation => !stopping && HasActiveRecommendation;
    public bool CanReconcileRecommendation => !stopping && recommendationSnapshot is { } value &&
        (RecBool(value, "requiresReconcile") || RecIds(value).Length > 0);

    private static string RecText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static bool RecBool(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static string[] RecIds(JsonElement value) =>
        value.TryGetProperty("reconcileIds", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];

    private void InitializeRecommendations()
    {
        recommendationBinding = RecommendationSource.BindSelection(SelectRecommendationAsync);
        RecommendationSource.Changed += RecommendationChanged;
    }
    private void RecommendationBindings()
    {
        foreach (var name in new[] { nameof(HasRecommendationSession), nameof(HasRecommendationControls),
            nameof(HasQueueRecommendations), nameof(CanChooseRecommendation), nameof(RecommendationCandidates),
            nameof(QueueRecommendationStatus), nameof(QueueRecommendationEmptyText), nameof(HasPlaybackFm),
            nameof(PlaybackFmMode), nameof(PlaybackFmScene), nameof(PlaybackFmRuleId), nameof(PlaybackFmTabTagId),
            nameof(PlaybackFmActionKey), nameof(PlaybackModeText), nameof(PlaybackModeIcon), nameof(CanChangePlaybackMode),
            nameof(HasPlaybackHeartbeat), nameof(PlaybackModeActionKey), nameof(PlaybackFmTitle),
            nameof(CanTogglePlaybackHeartbeat), nameof(CanHeartbeatCurrent), nameof(HasActiveRecommendation),
            nameof(RecommendationTitle), nameof(RecommendationStatus), nameof(RecommendationLoading),
            nameof(RecommendationContinue), nameof(CanChangeRecommendationContinue), nameof(CanRetryRecommendation),
            nameof(CanReturnRecommendation), nameof(CanCancelRecommendation), nameof(CanReconcileRecommendation) })
            Changed(name);
    }
    private void RecommendationChanged(JsonElement snapshot)
    {
        var copy = snapshot.Clone();
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return;
        _ = Presentation.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (stopping || disposed) return;
            PublishRecommendation(copy);
        }));
    }
    private void PublishRecommendation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !RecBool(value, "ok") ||
            !value.TryGetProperty("revision", out var number) || !number.TryGetInt64(out long revision) ||
            revision < recommendationRevision) return;
        recommendationRevision = revision;
        recommendationSnapshot = value.Clone();
        string content = RecText(value, "id") + ":" +
            (value.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array
                ? candidates.GetRawText() : "");
        if (content != recommendationCandidateContent)
        {
            recommendationCandidateContent = content;
            RecommendationCandidates = RecText(value, "kind") == "queue" && RecBool(value, "active") &&
                candidates.ValueKind == JsonValueKind.Array
                ? candidates.EnumerateArray().Select(item => new MusicRecommendationCandidate(
                    RecText(value, "id"), RecText(item, "id"), SongRow.Read(item.GetProperty("song")))).ToArray() : [];
        }
        if (!RecBool(value, "active")) RecommendationCandidates = [];
        recommendationError = "";
        RecommendationBindings();
        if (!RecBool(value, "loading") && value.TryGetProperty("queueVersion", out var queueVersion) &&
            queueVersion.TryGetInt64(out long version) && version > recommendationDesiredVersion)
        {
            recommendationDesiredVersion = version;
            if (!recommendationDisplayRunning) TrackRecommendation(RefreshRecommendationDisplay());
        }
    }
    private async Task RefreshRecommendationDisplay()
    {
        recommendationDisplayRunning = true;
        try
        {
            await Task.Yield();
            while (!stopping && recommendationDisplayedVersion < recommendationDesiredVersion)
            {
                long wanted = recommendationDesiredVersion;
                await RefreshRecommendationQueue(recommendationLifetime.Token);
                recommendationDisplayedVersion = wanted;
            }
        }
        finally { recommendationDisplayRunning = false; }
    }

    private Task TrackRecommendation(Task task)
    {
        recommendationWork.Add(task);
        _ = Observe();
        return task;
        async Task Observe()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!stopping && !disposed) { recommendationError = ErrorText(error); RecommendationBindings(); }
            }
            finally { recommendationWork.Remove(task); }
        }
    }

    // These controls must not wait behind commandTail: a start/next command may
    // itself be waiting for the batch that Cancel/Return has to retire.
    private Task RecommendationCommand(string method, object parameters) =>
        TrackRecommendation(ExecuteRecommendationCommand(method, parameters));
    private async Task ExecuteRecommendationCommand(string method, object parameters)
    {
        if (stopping) return;
        var token = recommendationLifetime.Token;
        long command = ++recommendationCommandEpoch;
        RecommendationBindings();
        try
        {
            var response = await application.ExecuteAsync(method, parameters, token);
            token.ThrowIfCancellationRequested();
            if (!RecBool(response, "ok")) throw new MusicOperationException(RecText(response, "code"));
            // Command replies may describe a retired session. Only the actual
            // current snapshot may replace the shared playback controls.
            var actual = method == "music.recommendations.get" ? response :
                await application.ExecuteAsync("music.recommendations.get", new { }, token);
            token.ThrowIfCancellationRequested();
            if (!stopping) PublishRecommendation(actual);
            // Until the sole queue owner has reconciled an uncertain commit, its
            // projection is not authoritative and must not replace the explicit
            // recommendation reconciliation state with a generic read failure.
            if (!RecBool(actual, "requiresReconcile") && RecIds(actual).Length == 0)
                await RefreshRecommendationQueue(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && command == recommendationCommandEpoch)
            { recommendationError = ErrorText(error); RecommendationBindings(); }
        }
    }
    public Task CancelRecommendationAsync() => CanCancelRecommendation
        ? RecommendationCommand("music.recommendations.cancel", new { id = RecText(recommendationSnapshot!.Value, "id") })
        : Task.CompletedTask;
    public Task ReturnRecommendationAsync() => CanReturnRecommendation
        ? RecommendationCommand("music.recommendations.return", new { id = RecText(recommendationSnapshot!.Value, "id") })
        : Task.CompletedTask;
    public Task RetryRecommendationAsync() => CanRetryRecommendation
        ? RecommendationCommand("music.recommendations.retry", new { id = RecText(recommendationSnapshot!.Value, "id") })
        : Task.CompletedTask;
    public Task ChangeRecommendationContinueAsync(bool enabled) => CanChangeRecommendationContinue
        ? RecommendationCommand("music.recommendations.continue", new { id = RecText(recommendationSnapshot!.Value, "id"), enabled })
        : Task.CompletedTask;
    public Task ChooseRecommendationAsync(MusicRecommendationCandidate candidate) =>
        CanChooseRecommendation && candidate.SessionId == RecText(recommendationSnapshot!.Value, "id") &&
        RecommendationCandidates.Any(item => ReferenceEquals(item, candidate))
            ? RecommendationCommand("music.recommendations.choose", new { id = candidate.SessionId, candidateId = candidate.Id })
            : Task.CompletedTask;
    public Task ChangePlaybackFmAsync(string mode, string submode, string actionKey)
    {
        if (!HasPlaybackFm || !CanChangePlaybackMode || actionKey != PlaybackFmActionKey ||
            !(mode == "scene" ? CatalogFmScenes.Any(choice => choice.Key == submode) :
                submode.Length == 0 && PlaybackFmModes.Any(choice => choice.Key == mode))) return Task.CompletedTask;
        return StartRecommendation(Target("fm", "wy", "私人漫游", new { mode, submode }));
    }
    public Task HeartbeatCurrentAsync(string presentationKey)
    {
        if (!CanHeartbeatCurrent || presentationKey != PlaybackPresentationKey) return Task.CompletedTask;
        return StartRecommendation(Target("heartbeat", "wy", "心动模式",
            new { entry = "current", songId = SongJson.NativeId(current!.Raw), seedEntryId = current.EntryId }));
    }
    public Task TogglePlaybackHeartbeatAsync(string actionKey)
    {
        if (!CanTogglePlaybackHeartbeat || HasPlaybackFm || actionKey != PlaybackModeActionKey) return Task.CompletedTask;
        return HasPlaybackHeartbeat ? ReturnRecommendationAsync() : HeartbeatCurrentAsync(PlaybackPresentationKey);
    }

    // Invoked only after an explicit ordinary playback action established a new
    // queue. Playback observations and surface activation never start a preview.
    private async Task StartQueueRecommendationPreviewAsync()
    {
        if (stopping || !OwnsCurrent || !playback.HasMedia || SongJson.Platform(current!.Raw) != "wy") return;
        long command = recommendationCommandEpoch, queue = queueEpoch, account = accountEpoch;
        string presentation = PlaybackPresentationKey;
        var token = recommendationLifetime.Token;
        bool Current() => !stopping && command == recommendationCommandEpoch && queue == queueEpoch &&
            account == accountEpoch && presentation == PlaybackPresentationKey && OwnsCurrent && playback.HasMedia;
        try
        {
            var actual = await application.ExecuteAsync("music.recommendations.get", new { }, token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return;
            if (!RecBool(actual, "ok")) throw new MusicOperationException(RecText(actual, "code"));
            PublishRecommendation(actual);
            if (!Current() || RecBool(actual, "active") || RecBool(actual, "requiresReconcile") || RecIds(actual).Length > 0) return;
            await RecommendationCommand("music.recommendations.start",
                new { kind = "queue", platform = "wy", @continue = false, local = true, play = true });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (Current())
            { recommendationError = ErrorText(error); RecommendationBindings(); }
        }
    }
    public Task ReconcileRecommendationAsync()
    {
        if (!CanReconcileRecommendation) return Task.CompletedTask;
        string[] ids = RecIds(recommendationSnapshot!.Value);
        if (ids.Length == 0) ids = [RecText(recommendationSnapshot.Value, "id")];
        return TrackRecommendation(Reconcile());
        async Task Reconcile()
        {
            foreach (string id in ids)
            {
                if (stopping) break;
                await ExecuteRecommendationCommand("music.recommendations.reconcile", new { id });
            }
            // A reply for an old unresolved id is not the current session.
            if (!stopping) await ExecuteRecommendationCommand("music.recommendations.get", new { });
        }
    }

    private async Task StartRecommendation(CatalogTarget target)
    {
        if (stopping) return;
        InvalidateCatalogAction();
        long command = ++recommendationCommandEpoch;
        long action = catalogActionGeneration, navigation = navigationSubmission;
        long observation = CatalogAccountObservation(target.Platform);
        bool Current() => !stopping && command == recommendationCommandEpoch &&
            action == catalogActionGeneration && navigation == navigationSubmission &&
            observation == CatalogAccountObservation(target.Platform);
        RecommendationBindings();
        try
        {
            await EnsureAccountsForRequestAsync(recommendationLifetime.Token);
            if (!Current()) return;
            if (Accounts.Single(a => a.Platform == target.Platform).State is not ("saved" or "verified"))
                throw new MusicOperationException("AUTH_REQUIRED");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { if (Current()) PublishCatalogActionError(error); return; }
        var input = target.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        input["kind"] = target.Operation; input["platform"] = target.Platform; input["local"] = true; input["play"] = true;
        // Session identity and native recording IDs, never a synthesized title.
        if (target.Operation != "fm") input["continue"] = false;
        await RecommendationCommand("music.recommendations.start", input);
    }

    private async Task RefreshRecommendationQueue(CancellationToken token)
    {
        if (stopping) return;
        long epoch = ++recommendationViewEpoch;
        await ReadQueue(token);
        if (stopping || token.IsCancellationRequested || epoch != recommendationViewEpoch) return;
        InvalidateQueueSurface();
        ObserveRecommendationPlayback();
    }

    private Task<bool> SelectRecommendationAsync(MusicRecommendationSelection selection, CancellationToken token)
    {
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return Task.FromResult(false);
        // Do not enqueue RunAsync here: the invoking recommendation command may
        // already be inside that lane and must await this exact media callback.
        return Presentation.Dispatcher.InvokeAsync(() => SelectRecommendationOnUI(selection, token)).Task.Unwrap();
    }
    private async Task<bool> SelectRecommendationOnUI(MusicRecommendationSelection selection, CancellationToken token)
    {
        bool Current() => !stopping && !disposed && !token.IsCancellationRequested && selection.IsCurrent();
        if (!Current()) return false;
        if (selection.Action == MusicRecommendationPlaybackAction.Stop)
        {
            if (playback.OwnerId == Id && playback.Identity == selection.EntryId)
                return await StopOwnedPlayback(Current) && Current();
            return Current();
        }
        long epoch = -1;
        try
        {
            var response = await application.ExecuteAsync("music.queue.get", new { offset = 0, limit = 1 }, token);
            if (!Current() || !RecBool(response, "ok") ||
                MusicDto.Integer(response, "version") != selection.QueueVersion) return false;
            var state = ParseQueue(response);
            if (state.Current?.EntryId != selection.EntryId || !state.Saved) return false;
            ++queueEpoch; ++queueRead; ++recommendationViewEpoch;
            if (selection.RestoreOnly || !selection.Play)
            {
                if (!await StopOwnedPlayback(Current) || !Current()) return false;
                ApplyQueue(state);
            }
            else
            {
                bool sameMedia = openedEntry == selection.EntryId && playback.OwnerId == Id &&
                    playback.Identity == selection.EntryId && playback.HasMedia;
                ApplyQueue(state);
                if (!Current()) return false;
                if (sameMedia)
                {
                    epoch = playbackEpoch;
                    if (!playback.IsPlaying) await ResumePlaybackAsync();
                }
                else await StartCurrent(token, selection.Automatic, Current, value => epoch = value, selection.Feedback);
            }
            if (!Current())
            {
                if (playback.OwnerId == Id && playback.Identity == selection.EntryId && epoch == playbackEpoch)
                    await StopOwnedPlayback(() => playback.OwnerId == Id &&
                        playback.Identity == selection.EntryId && epoch == playbackEpoch);
                return false;
            }
            ObserveRecommendationPlayback();
            return true;
        }
        catch (Exception error)
        {
            // Never stop a newer user's selection while retiring a stale resolver.
            if (current?.EntryId == selection.EntryId && playback.OwnerId == Id &&
                playback.Identity == selection.EntryId && epoch == playbackEpoch)
                await StopOwnedPlayback(() => current?.EntryId == selection.EntryId &&
                    playback.OwnerId == Id && playback.Identity == selection.EntryId && epoch == playbackEpoch);
            if (Current()) { recommendationError = ErrorText(error); RecommendationBindings(); }
            return false;
        }
    }

    private void ObserveRecommendationPlayback()
    {
        if (stopping || current?.EntryId is not { } id) return;
        // Opening an input publishes "not playing" before its first frame. That
        // is not a user Pause and must not invalidate this very selection.
        if (!recorded || openedEntry != id) return;
        bool owns = playback.OwnerId == Id && playback.Identity == id;
        var state = new MusicRecommendationPlaybackState(id, owns && playback.IsPlaying,
            owns && recorded && openedEntry == id, owns && playback.HasMedia, IsLocal: true);
        string? retry = null;
        if (state is { IsPlaying: true, HasStarted: true, MediaReady: true } &&
            recommendationSnapshot is { } snapshot && RecBool(snapshot, "active") &&
            RecText(snapshot, "kind") == "fm" && RecBool(snapshot, "continue") &&
            !RecBool(snapshot, "loading") && !RecBool(snapshot, "requiresReconcile") &&
            RecText(snapshot, "error").Length > 0 &&
            snapshot.TryGetProperty("failures", out var failures) && failures.TryGetInt32(out int count) && count == 1 &&
            snapshot.TryGetProperty("retryAt", out var due) && due.TryGetInt64(out long at) &&
            at > 0 && at <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            retry = RecText(snapshot, "id") + ":" + at;
        bool retryDue = retry is not null && retry != recommendationRetryObservation;
        if (recommendationObservation == state && !retryDue) return;
        // Native position events already arrive while playing. Forward one
        // observation when this backoff expires, without adding a polling loop.
        if (retryDue) recommendationRetryObservation = retry;
        recommendationObservation = state;
        TrackRecommendation(RecommendationSource.OnPlaybackAsync(state, recommendationLifetime.Token));
    }
    private void ObserveExplicitRecommendationPause()
    {
        if (stopping || current?.EntryId is not { } id) return;
        var state = new MusicRecommendationPlaybackState(id, false, recorded && openedEntry == id,
            playback.OwnerId == Id && playback.Identity == id && playback.HasMedia);
        recommendationObservation = state;
        TrackRecommendation(RecommendationSource.OnPlaybackAsync(state, recommendationLifetime.Token));
    }
    private Task<bool> AdvanceRecommendation(bool naturalEnd) => current?.EntryId is { } id
        ? RecommendationSource.TryAdvanceAsync(id, naturalEnd, true, recommendationLifetime.Token)
        : Task.FromResult(false);

    private void SealRecommendations()
    {
        ++recommendationViewEpoch;
        recommendationLifetime.Cancel();
        recommendationPreparation = RecommendationSource.PrepareRecommendationsAsync();
        RecommendationBindings();
    }
    private async Task DrainRecommendations()
    {
        await recommendationPreparation;
        await Task.WhenAll(recommendationWork.ToArray());
    }
    private void ResumeRecommendations()
    {
        recommendationLifetime.Dispose(); recommendationLifetime = new();
        recommendationSnapshot = null; recommendationError = ""; recommendationObservation = null;
        recommendationCandidateContent = ""; RecommendationCandidates = [];
        recommendationRetryObservation = null;
        recommendationDisplayedVersion = recommendationDesiredVersion = -1;
        RecommendationBindings();
    }
    private void ReleaseRecommendations()
    {
        RecommendationSource.Changed -= RecommendationChanged;
        recommendationBinding?.Dispose(); recommendationBinding = null;
        recommendationLifetime.Cancel(); recommendationLifetime.Dispose();
    }
}
