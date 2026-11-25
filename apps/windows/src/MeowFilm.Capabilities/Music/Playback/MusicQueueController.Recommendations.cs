using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities;

public sealed record MusicRecommendationCandidate(string SessionId, string Id, SongRow Song) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshSong(MusicSongRecord? value, long publication = -1)
    {
        if (value is null) return;
        Song.ApplyRepository(value, publication);
        foreach (var name in new[] { nameof(Title), nameof(Artist), nameof(Cover), nameof(Duration) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    public string Cover => Song.Cover;
    public string Duration => Song.Duration;
}

public sealed partial class MusicQueueController
{
    private IDisposable? recommendationBinding;
    private readonly HashSet<Task> recommendationWork = [];
    private CancellationTokenSource recommendationLifetime = new();
    private MusicRecommendationSnapshot? recommendationSnapshot;
    private long recommendationRevision = -1, recommendationViewEpoch, recommendationCommandEpoch;
    private string recommendationFailureNotice = "";
    private MusicRecommendationPlaybackState? recommendationObservation;
    private Task recommendationPreparation = Task.CompletedTask;
    private long recommendationDisplayedVersion = -1, recommendationDesiredVersion = -1;
    private bool recommendationDisplayRunning;
    private (bool Active, string Kind, string Id, MusicRecommendationCandidateSnapshot[] Items) recommendationCandidateCache = (false, "", "", []);
    private sealed record SearchRecommendationStart(string Entry, long Playback, long Command, long Account, long Observation);
    private SearchRecommendationStart? searchRecommendationStart;

    public IReadOnlyList<MusicRecommendationCandidate> RecommendationCandidates { get; private set; } = [];
    public bool HasRecommendationSession => recommendationSnapshot is { } value &&
        (value.Id.Length > 0 || value.ReconcileIds.Length > 0);
    public bool HasQueueRecommendations => HasActiveRecommendation &&
        recommendationSnapshot!.Kind == "queue";
    public bool CanChooseRecommendation => !stopping && HasQueueRecommendations && !RecommendationLoading &&
        !CanReconcileRecommendation;
    public bool HasActiveRecommendation => recommendationSnapshot is { Active: true };
    public bool RecommendationLoading => recommendationSnapshot is { Loading: true };
    public string RecommendationTitle => recommendationSnapshot is not { } value ? "" :
        value.Kind switch { "fm" => "私人 FM", "similar" => value.SourceType == "artist" ? "相似艺人" : "相似歌曲", "heartbeat" => "心动模式",
            "queue" => "为你推荐", _ => "推荐" };
    public bool HasPlaybackFm => !stopping && HasActiveRecommendation && recommendationSnapshot is { Platform: "wy", Kind: "fm", Mode.Length: > 0 };
    public bool HasPlaybackHeartbeat => !stopping && HasActiveRecommendation && recommendationSnapshot is { Platform: "wy", Kind: "heartbeat", Batch: > 0 };
    public string PlaybackFmMode => HasPlaybackFm ? recommendationSnapshot!.Mode : "";
    public string PlaybackFmScene => HasPlaybackFm ? recommendationSnapshot!.Submode : "";
    public string PlaybackFmRuleId => HasPlaybackFm ? recommendationSnapshot!.RuleId : "";
    public string PlaybackFmTabTagId => HasPlaybackFm ? recommendationSnapshot!.TabTagId : "";
    public string PlaybackFmActionKey => HasPlaybackFm
        ? recommendationSnapshot!.Id + ":" + PlaybackSourceActionKey +
            ":" + PlaybackFmMode + ":" + PlaybackFmScene + ":" + PlaybackFmRuleId + ":" + PlaybackFmTabTagId
        : "";
    public string PlaybackSpecialModeText => HasPlaybackHeartbeat ? "心动模式" : HasPlaybackFm ? "私人漫游" :
        recommendationSnapshot is { Kind: "similar" } ? "相似模式" : "推荐模式";
    public string PlaybackSpecialModeIcon => HasPlaybackHeartbeat ? "heartbeat" :
        HasPlaybackFm || recommendationSnapshot is { Kind: "similar" } ? "fm" : "sparkles";
    public string PlaybackModeText => HasActiveRecommendation && RecommendationContinue ? PlaybackSpecialModeText : playbackController.ModeText;
    public string PlaybackModeIcon => HasActiveRecommendation && RecommendationContinue ? PlaybackSpecialModeIcon :
        playbackController.Mode switch { 1 => "repeat", 2 => "shuffle", _ => "single" };
    public string PlaybackModeActionKey => PlaybackSourceActionKey + ":" + playbackController.Mode + ":" + RecommendationContinue;
    public string PlaybackFmTitle => PlaybackFmRuleId.Length > 0 || PlaybackFmTabTagId.Length > 0
        ? "频道漫游" : PlaybackFmMode == "scene"
            ? MusicCatalogDispatcher.FmScenes.FirstOrDefault(choice => choice.Key == PlaybackFmScene)?.Name ?? "私人漫游"
            : PlaybackFmModes.FirstOrDefault(choice => choice.Key == PlaybackFmMode)?.Name ?? "私人漫游";
    public bool CanChangePlaybackMode => !stopping && playbackController.QueueSaved;
    public MusicCatalogChoice[] PlaybackFmModes { get; } =
        [new("default", "默认模式"), new("familiar", "熟悉模式"), new("explore", "探索模式"), new("puzzle", "拼图模式")];

    private string CurrentWyId => playbackController.CurrentSong is { } current ? RecommendationJson.NativeId(current.Record) : "";
    private string HeartbeatPlaylistId => playbackController.CurrentFeedbackContext?.HeartbeatPlaylistId ?? "";
    private bool HasHeartbeatContext => HeartbeatPlaylistId.Length > 0 &&
        playbackController.CurrentFeedbackContext?.HeartbeatAccountId is { Length: > 0 } hbAccount &&
        accounts.Accounts.Any(account => account.Platform == "wy" &&
            (account.UserId.Length == 0 || account.UserId == hbAccount));
    private bool CurrentFromLocalFavorites => playbackController.CurrentFeedbackContext?.PlaylistId == "favorites";
    public bool HasCurrentRecommendationIdentity => CurrentWyId.Length > 0;
    public bool CanRecommendCurrent => playbackController.CanPlay && playbackController.OwnsCurrent && playback.HasMedia && HasCurrentRecommendationIdentity &&
        !CanReconcileRecommendation && !RecommendationLoading;
    public bool CanHeartbeatCurrent => playbackController.CanPlay && playbackController.OwnsCurrent && playback.HasMedia &&
        HasCurrentRecommendationIdentity && (HasHeartbeatContext || CurrentFromLocalFavorites) &&
        !CanReconcileRecommendation && !RecommendationLoading;
    public string PlaybackQueueSourceText => HasActiveRecommendation &&
        (recommendationSnapshot!.Kind != "queue" || recommendationSnapshot.Started)
            ? RecommendationTitle : playbackController.QueueSource is { } source ? source.Kind switch
            { "search" => "搜索结果", "favorites" => "本地收藏", "playlist" when !string.IsNullOrEmpty(source.Platform) => "网络歌单",
              "playlist" => "本地歌单", _ => source.Title ?? "" } : "普通列表";
    public string PlaybackSourceActionKey
    {
        get
        {
            var account = accounts.Accounts.Single(value => value.Platform == "wy");
            return recommendationCommandEpoch + ":" + recommendationRevision + ":" + catalogActions.Generation + ":" +
                accounts.AccountEpoch + ":" + account.UserId + ":" + account.Generation + ":" +
                playbackController.QueueEpoch + ":" + playbackController.PlaybackPresentationKey;
        }
    }
    public bool RecommendationContinue => recommendationSnapshot is { Continue: true };
    public bool CanChangeRecommendationContinue => !stopping && HasActiveRecommendation && !RecommendationLoading &&
        !CanReconcileRecommendation;
    public bool HasRecommendationPanel => HasQueueRecommendations || CanRetryRecommendation || CanReconcileRecommendation;
    public bool CanRetryRecommendation => !stopping && recommendationSnapshot is { CanRetry: true };
    public bool CanReturnRecommendation => !stopping && recommendationSnapshot is { CanReturn: true };
    public bool CanReconcileRecommendation => !stopping && recommendationSnapshot is { } value &&
        (value.RequiresReconcile || value.ReconcileIds.Length > 0);

    private static bool CandidatesEqual(MusicRecommendationCandidateSnapshot[] a, MusicRecommendationCandidateSnapshot[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Id != b[i].Id || !a[i].Song.ContentEquals(b[i].Song)) return false;
        return true;
    }

    private void InitializeRecommendations()
    {
        recommendationBinding = application.BindSelection(SelectRecommendationAsync);
        application.Changed += RecommendationChanged;
    }

    internal void RecommendationBindings()
    {
        foreach (var name in new[] { nameof(HasRecommendationSession), nameof(HasQueueRecommendations),
            nameof(CanChooseRecommendation), nameof(RecommendationCandidates), nameof(HasPlaybackFm),
            nameof(PlaybackFmMode), nameof(PlaybackFmScene), nameof(PlaybackFmRuleId), nameof(PlaybackFmTabTagId),
            nameof(PlaybackFmActionKey), nameof(PlaybackModeText), nameof(PlaybackModeIcon), nameof(CanChangePlaybackMode),
            nameof(HasPlaybackHeartbeat), nameof(PlaybackSpecialModeText), nameof(PlaybackSpecialModeIcon), nameof(PlaybackModeActionKey), nameof(PlaybackFmTitle),
            nameof(CanHeartbeatCurrent), nameof(CanRecommendCurrent), nameof(HasCurrentRecommendationIdentity),
            nameof(PlaybackQueueSourceText), nameof(PlaybackSourceActionKey), nameof(HasActiveRecommendation),
            nameof(RecommendationTitle), nameof(RecommendationLoading), nameof(RecommendationContinue),
            nameof(CanChangeRecommendationContinue), nameof(CanRetryRecommendation), nameof(CanReturnRecommendation),
            nameof(CanReconcileRecommendation), nameof(HasRecommendationPanel) })
            Changed(name);
    }

    private void RecommendationChanged(MusicRecommendationSnapshot snapshot)
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(new Action(() =>
        {
            if (stopping || disposed) return;
            PublishRecommendation(snapshot);
        }));
    }

    private void PublishRecommendation(MusicRecommendationSnapshot value)
    {
        if (value.Revision < recommendationRevision) return;
        recommendationRevision = value.Revision;
        if (recommendationSnapshot is not { } prior || prior.Id != value.Id)
            recommendationObservation = null;
        recommendationSnapshot = value;
        if (recommendationCandidateCache.Active != value.Active ||
            recommendationCandidateCache.Kind != value.Kind ||
            recommendationCandidateCache.Id != value.Id ||
            !CandidatesEqual(recommendationCandidateCache.Items, value.Candidates))
        {
            recommendationCandidateCache = (value.Active, value.Kind, value.Id, value.Candidates);
            RecommendationCandidates = value.Active && value.Kind == "queue"
                ? value.Candidates.Select(item => new MusicRecommendationCandidate(
                    value.Id, item.Id, SongRow.FromRecord(item.Song))).ToArray()
                : [];
        }
        string failure = value.Error;
        string failureKey = $"{value.Id}:{failure}:{value.Failures}";
        if (failure.Length > 0 && failure is not ("CANCELLED" or "STALE_SESSION" or "STALE_QUEUE") &&
            failureKey != recommendationFailureNotice)
        { recommendationFailureNotice = failureKey; _ = Notify(ErrorText(new MusicOperationException(failure))); }
        RecommendationBindings();
        if (!value.Loading && value.QueueVersion > recommendationDesiredVersion)
        {
            recommendationDesiredVersion = value.QueueVersion;
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
        catch (OperationCanceledException) when (recommendationLifetime.IsCancellationRequested) { }
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
                if (!stopping && !disposed) { await Notify(ErrorText(error)); RecommendationBindings(); }
            }
            finally { recommendationWork.Remove(task); }
        }
    }

    // These controls must not wait behind commandTail: a start/next command may
    // itself be waiting for the batch that Cancel/Return has to retire.
    private Task RecommendationCommand(bool isStart, Func<CancellationToken, Task<MusicRecommendationSnapshot>> action) =>
        TrackRecommendation(ExecuteRecommendationCommand(isStart, action));

    private async Task ExecuteRecommendationCommand(bool isStart, Func<CancellationToken, Task<MusicRecommendationSnapshot>> action)
    {
        if (stopping) return;
        var token = recommendationLifetime.Token;
        long command = ++recommendationCommandEpoch;
        if (!isStart)
            catalogActions.ClearPreparingCardIf(card => card.Kind is "fm" or "similar" or "heartbeat");
        RecommendationBindings();
        try
        {
            _ = await action(token);
            token.ThrowIfCancellationRequested();
            // Command replies may describe a retired session. Only the actual
            // current snapshot may replace the shared playback controls.
            var actual = await application.GetRecommendationAsync(cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (!stopping) PublishRecommendation(actual);
            if (!actual.RequiresReconcile && actual.ReconcileIds.Length == 0)
                await RefreshRecommendationQueue(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && command == recommendationCommandEpoch)
            { await Notify(ErrorText(error)); RecommendationBindings(); }
        }
    }

    public Task ReturnRecommendationAsync() => CanReturnRecommendation
        ? RecommendationCommand(false, token => application.ReturnRecommendationAsync(recommendationSnapshot!.Id, token))
        : Task.CompletedTask;
    public Task RetryRecommendationAsync() => CanRetryRecommendation
        ? RecommendationCommand(false, token => application.RetryRecommendationAsync(recommendationSnapshot!.Id, token))
        : Task.CompletedTask;
    public Task ChangeRecommendationContinueAsync(bool enabled) => CanChangeRecommendationContinue
        ? RecommendationCommand(false, token => application.SetRecommendationContinueAsync(recommendationSnapshot!.Id, enabled, token))
        : Task.CompletedTask;
    public Task ChooseRecommendationAsync(MusicRecommendationCandidate candidate) =>
        CanChooseRecommendation && candidate.SessionId == recommendationSnapshot!.Id &&
        RecommendationCandidates.Any(item => ReferenceEquals(item, candidate))
            ? RecommendationCommand(false, token => application.ChooseRecommendationCandidateAsync(candidate.SessionId, candidate.Id, token))
            : Task.CompletedTask;
    public Task ChangePlaybackFmAsync(string mode, string submode, string actionKey)
    {
        if (!HasPlaybackFm || !CanChangePlaybackMode || actionKey != PlaybackFmActionKey ||
            !(mode == "scene" ? MusicCatalogDispatcher.FmScenes.Any(choice => choice.Key == submode) :
                submode.Length == 0 && PlaybackFmModes.Any(choice => choice.Key == mode))) return Task.CompletedTask;
        return StartRecommendation(MusicCatalogDispatcher.Target("fm", "wy", "私人漫游", new(Mode: mode, Submode: submode)));
    }
    public Task HeartbeatCurrentAsync(string presentationKey)
    {
        if (!CanHeartbeatCurrent || presentationKey != playbackController.PlaybackPresentationKey) return Task.CompletedTask;
        var entryId = playbackController.CurrentSong!.EntryId;
        return StartRecommendation(MusicCatalogDispatcher.Target("heartbeat", "wy", "心动模式",
            new(Entry: "current", SongId: CurrentWyId, PlaylistId: HasHeartbeatContext ? HeartbeatPlaylistId : null, SeedEntryId: entryId)));
    }
    public Task RecommendCurrentAsync(string presentationKey)
    {
        if (!CanRecommendCurrent || presentationKey != playbackController.PlaybackPresentationKey) return Task.CompletedTask;
        return StartRecommendation(MusicCatalogDispatcher.Target("queue", "wy", "单曲推荐",
            new(SeedEntryId: playbackController.CurrentSong!.EntryId, Restore: true)));
    }

    internal void AwaitSearchRecommendationStart(long playbackEpoch)
    {
        searchRecommendationStart = new(playbackController.CurrentSong!.EntryId!, playbackEpoch, recommendationCommandEpoch,
            accounts.AccountEpoch, accounts.PlatformObservation("wy"));
    }

    internal void ClearSearchRecommendationStart() => searchRecommendationStart = null;

    // Only the explicit search playback action's first real Started consumes
    // this intent. Open completion, later observations and browsing cannot arm it.
    private async Task StartQueueRecommendationPreviewAsync(SearchRecommendationStart intent)
    {
        var token = recommendationLifetime.Token;
        bool Current() => !stopping && ReferenceEquals(searchRecommendationStart, intent) &&
            intent.Command == recommendationCommandEpoch && intent.Account == accounts.AccountEpoch &&
            intent.Observation == accounts.PlatformObservation("wy") && intent.Playback == playbackController.PlaybackEpoch &&
            intent.Entry == playbackController.CurrentSong?.EntryId && playbackController.OwnsCurrent && playback.HasMedia;
        try
        {
            if (!Current() || CurrentWyId.Length == 0) return;
            await Task.Yield();
            if (!Current()) return;
            var actual = await application.GetRecommendationAsync(cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return;
            PublishRecommendation(actual);
            if (!Current() || actual.Active || actual.RequiresReconcile || actual.ReconcileIds.Length > 0) return;
            var request = new MusicRecommendationStartRequest(
                Kind: "queue",
                Platform: "wy",
                SeedEntryId: intent.Entry,
                Local: true,
                Restore: false,
                Continue: true,
                Play: playback.IsPlaying);
            await RecommendationCommand(true, ct => application.StartRecommendationAsync(request, ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (Current())
            { await Notify(ErrorText(error)); RecommendationBindings(); }
        }
        finally
        {
            if (ReferenceEquals(searchRecommendationStart, intent)) searchRecommendationStart = null;
        }
    }

    public Task ReconcileRecommendationAsync()
    {
        if (!CanReconcileRecommendation) return Task.CompletedTask;
        string[] ids = recommendationSnapshot!.ReconcileIds;
        if (ids.Length == 0) ids = [recommendationSnapshot.Id];
        return TrackRecommendation(Reconcile());
        async Task Reconcile()
        {
            foreach (string id in ids)
            {
                if (stopping) break;
                await ExecuteRecommendationCommand(false, ct => application.ReconcileRecommendationAsync(id, ct));
            }
        }
    }

    internal async Task StartRecommendation(CatalogTarget target, MusicCatalogCard? card = null)
    {
        if (stopping) return;
        catalogActions.Invalidate();
        long command = ++recommendationCommandEpoch;
        long action = catalogActions.Generation, navigation = ctx.NavigationSubmission;
        long observation = accounts.PlatformObservation(target.Platform);
        bool Current() => !stopping && command == recommendationCommandEpoch &&
            action == catalogActions.Generation && navigation == ctx.NavigationSubmission &&
            observation == accounts.PlatformObservation(target.Platform);
        catalogActions.SetPreparingCard(card);
        RecommendationBindings();
        try
        {
            await accounts.EnsureAccountsForRequestAsync(recommendationLifetime.Token);
            if (!Current()) return;
            if (target.Operation == "heartbeat" &&
                accounts.Accounts.Single(a => a.Platform == target.Platform).State is not ("saved" or "verified"))
                throw new MusicOperationException("AUTH_REQUIRED");
            var p = target.Params;
            if (target.Operation == "heartbeat" && p.Entry == "current" &&
                !HasHeartbeatContext && !CurrentFromLocalFavorites)
                throw new MusicOperationException("UNSUPPORTED");
            bool hasSeed = p.SeedEntryId is not null;
            var request = new MusicRecommendationStartRequest(
                Kind: target.Operation,
                Platform: target.Platform,
                Mode: p.Mode,
                Submode: p.Submode,
                RuleId: p.RuleId,
                TabTagId: p.TabTagId,
                SourceIds: p.SourceIds,
                SourceType: p.SourceType,
                Entry: p.Entry,
                SongId: p.SongId,
                PlaylistId: p.PlaylistId,
                SeedEntryId: p.SeedEntryId,
                Local: true,
                Restore: p.Restore,
                Continue: target.Operation != "heartbeat" || p.Continue != false,
                Play: hasSeed ? playback.IsPlaying : true);
            await RecommendationCommand(true, ct => application.StartRecommendationAsync(request, ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current()) RecommendationActionError?.Invoke(error); }
        finally
        {
            if (action == catalogActions.Generation && ReferenceEquals(catalogActions.PreparingCard, card))
                catalogActions.ClearPreparingCard();
        }
    }

    internal async Task RefreshRecommendationQueue(CancellationToken token)
    {
        if (stopping) return;
        long epoch = ++recommendationViewEpoch;
        await playbackController.ReadQueue(token);
        if (stopping || token.IsCancellationRequested || epoch != recommendationViewEpoch) return;
        InvalidateQueueSurface();
        ObserveRecommendationPlayback();
    }

    private Task<bool> SelectRecommendationAsync(MusicRecommendationSelection selection, CancellationToken token)
    {
        if (ctx.Dispatcher.IsClosing) return Task.FromResult(false);
        // Do not enqueue RunAsync here: the invoking recommendation command may
        // already be inside that lane and must await this exact media callback.
        return ctx.Dispatcher.EnqueueAsync(() => SelectRecommendationOnUI(selection, token));
    }

    private async Task<bool> SelectRecommendationOnUI(MusicRecommendationSelection selection, CancellationToken token)
    {
        bool Current() => !stopping && !disposed && !token.IsCancellationRequested && selection.IsCurrent();
        if (!Current()) return false;
        if (selection.Action == MusicRecommendationPlaybackAction.Stop)
        {
            if (playback.OwnerId == Id && playback.Identity == selection.EntryId)
                return await playbackController.StopOwnedPlayback(Current) && Current();
            return Current();
        }
        long epoch = -1;
        try
        {
            var response = await application.GetQueueAsync(0, 1, token);
            if (!Current() || response.Version != selection.QueueVersion) return false;
            var state = MusicPlaybackController.ParseQueue(response);
            if (state.Current?.EntryId != selection.EntryId || !state.Saved) return false;
            playbackController.AdvanceQueueEpochAndRead(); ++recommendationViewEpoch;
            if (selection.RestoreOnly || !selection.Play)
            {
                if (!await playbackController.StopOwnedPlayback(Current) || !Current()) return false;
                playbackController.ApplyQueue(state);
            }
            else
            {
                bool sameMedia = playbackController.OpenedEntry == selection.EntryId && playback.OwnerId == Id &&
                    playback.Identity == selection.EntryId && playback.HasMedia;
                playbackController.ApplyQueue(state);
                if (!Current()) return false;
                if (sameMedia)
                {
                    epoch = playbackController.PlaybackEpoch;
                    if (!playback.IsPlaying) await playbackController.ResumePlaybackAsync();
                }
                else await playbackController.StartCurrent(token, selection.Automatic, Current, value => epoch = value, selection.Feedback);
            }
            if (!Current())
            {
                if (playback.OwnerId == Id && playback.Identity == selection.EntryId && epoch == playbackController.PlaybackEpoch)
                    await playbackController.StopOwnedPlayback(() => playback.OwnerId == Id &&
                        playback.Identity == selection.EntryId && epoch == playbackController.PlaybackEpoch);
                return false;
            }
            ObserveRecommendationPlayback();
            return true;
        }
        catch (Exception error)
        {
            // Never stop a newer user's selection while retiring a stale resolver.
            if (playbackController.CurrentSong?.EntryId == selection.EntryId && playback.OwnerId == Id &&
                playback.Identity == selection.EntryId && epoch == playbackController.PlaybackEpoch)
                await playbackController.StopOwnedPlayback(() => playbackController.CurrentSong?.EntryId == selection.EntryId &&
                    playback.OwnerId == Id && playback.Identity == selection.EntryId && epoch == playbackController.PlaybackEpoch);
            if (Current()) { await Notify(ErrorText(error)); RecommendationBindings(); }
            return false;
        }
    }

    internal void ObserveRecommendationPlayback()
    {
        if (stopping || playbackController.CurrentSong?.EntryId is not { } id) return;
        // Opening an input publishes "not playing" before its first frame. That
        // is not a user Pause and must not invalidate this very selection.
        if (!playbackController.Recorded || playbackController.OpenedEntry != id) return;
        bool owns = playback.OwnerId == Id && playback.Identity == id;
        var state = new MusicRecommendationPlaybackState(id, owns && playback.IsPlaying,
            owns && playbackController.Recorded && playbackController.OpenedEntry == id, owns && playback.HasMedia,
            IsLocal: true, PlaybackEvent: playbackController.PlaybackEvent);
        if (recommendationObservation == state) return;
        recommendationObservation = state;
        TrackRecommendation(application.OnPlaybackAsync(state, recommendationLifetime.Token));
    }

    internal void ObserveExplicitRecommendationPause()
    {
        if (stopping || playbackController.CurrentSong?.EntryId is not { } id) return;
        var state = new MusicRecommendationPlaybackState(id, false, playbackController.Recorded && playbackController.OpenedEntry == id,
            playback.OwnerId == Id && playback.Identity == id && playback.HasMedia, PlaybackEvent: playbackController.PlaybackEvent);
        recommendationObservation = state;
        TrackRecommendation(application.OnPlaybackAsync(state, recommendationLifetime.Token));
    }

    internal Task<bool> AdvanceRecommendation(bool naturalEnd) => playbackController.CurrentSong?.EntryId is { } id
        ? application.TryAdvanceAsync(id, naturalEnd, true, recommendationLifetime.Token)
        : Task.FromResult(false);

    internal async Task<bool> TryAdvanceRecommendationForStepAsync(bool naturalEnd, CancellationToken token)
    {
        if (!await AdvanceRecommendation(naturalEnd)) return false;
        await RefreshRecommendationQueue(token);
        return true;
    }

    internal void SealRecommendations()
    {
        searchRecommendationStart = null;
        ++recommendationViewEpoch;
        recommendationLifetime.Cancel();
        recommendationPreparation = application.PrepareRecommendationsAsync();
        RecommendationBindings();
    }

    internal async Task DrainRecommendations()
    {
        await recommendationPreparation;
        await Task.WhenAll(recommendationWork.ToArray());
    }

    internal void StartPendingSearchRecommendationPreview()
    {
        if (searchRecommendationStart is { } searchPreview)
            _ = TrackRecommendation(StartQueueRecommendationPreviewAsync(searchPreview));
    }
    internal void NotifyModeChanged()
    {
        Changed(nameof(PlaybackModeText));
        Changed(nameof(PlaybackModeIcon));
        Changed(nameof(PlaybackModeActionKey));
    }
    internal void NotifyPlaybackModeAvailability() => Changed(nameof(CanChangePlaybackMode));
    internal void NotifyRecommendationPlaybackChanged()
    {
        Changed(nameof(CanHeartbeatCurrent));
        Changed(nameof(CanRecommendCurrent));
    }

    internal void ReleaseRecommendations()
    {
        application.Changed -= RecommendationChanged;
        recommendationBinding?.Dispose(); recommendationBinding = null;
        recommendationLifetime.Cancel(); recommendationLifetime.Dispose();
    }
}
