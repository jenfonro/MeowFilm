using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>
/// Lean Layer-2 playback orchestrator owning transport commands, queue playback state, clock,
/// artwork, and current favorite, delegating source resolution, recovery, caching, quality switching,
/// chorus, feedback, and playback rate to standalone cohesive services.
/// </summary>
public sealed class MusicPlaybackController : MusicController
{
    internal sealed record QueueSnapshot(SongRow? Current, int Mode, bool Saved, MusicPlayContextRecord? FeedbackContext = null, MusicQueueSourceRecord? Source = null);

    private readonly MusicSettingsController settings;
    private readonly MusicCatalogActionState catalogActions;
    private readonly MusicContentState contentState;
    private readonly MusicPlaybackPresentation playbackPresentation;
    private readonly MusicPlaybackCacheService cacheService;
    private readonly MusicPlaybackSourceService sourceService;
    private readonly MusicPlaybackMetadataService metadataService;
    private readonly MusicPlaybackRecoveryService recoveryService;
    private readonly MusicPlaybackQualityService qualityService;

    private SongRow? current;
    private MusicQueueSourceRecord? queueSource;
    private long playbackEvent;
    private int mode = 1;
    private long queueEpoch, queueRead, playbackEpoch, endedEpoch = -1;
    private bool recorded, queueSaved = true, playbackSubscribed;
    private string? openedEntry;
    private bool favoriteSubmitting, addToPlaylistSubmitting;
    private bool currentFavorite;
    private long favoriteRead;
    private string currentArtwork = "";
    private string? artworkSongId;

    internal MusicPlaybackController(
        MusicRuntimeContext ctx,
        MusicSettingsController settings,
        MusicCatalogActionState catalogActions,
        MusicContentState contentState) : base(ctx)
    {
        this.settings = settings;
        this.catalogActions = catalogActions;
        this.contentState = contentState;
        Clock = new MusicPlaybackClock();
        playbackPresentation = new(this, property => Changed(property));

        Action<string> notify = property => Changed(property);
        cacheService = new(ctx, settings);
        sourceService = new(ctx, settings, cacheService.TryGetVerifiedPreparedReceipt);
        metadataService = new(ctx, settings, CaptureTelemetry, notify, playbackPresentation.Refresh);
        recoveryService = new(ctx, settings, sourceService, cacheService, metadataService, CaptureTelemetry,
            (entry, isRecorded) => { openedEntry = entry; recorded = isRecorded; });
        qualityService = new(ctx, settings, sourceService, cacheService, recoveryService, metadataService,
            CaptureTelemetry, () => CurrentSource, notify);

        recoveryService.PlaybackSourceBindingsRequested += PlaybackSourceBindings;
        recoveryService.UpdateSourceChoicesRequested += qualityService.UpdateSourceChoicesIfOpen;
        recoveryService.CloseSourceChoicesRequested += qualityService.CloseSourceChoices;
        recoveryService.SourcesUpdatedForLyrics += () => SourcesUpdatedForLyrics?.Invoke();
        recoveryService.RestoreLyricsIfNeeded += () => RestoreLyricsRequested?.Invoke();
        recoveryService.OfficialSongAccepted += AcceptOfficialSong;
        recoveryService.SearchRecommendationCleared += () => SearchRecommendationCleared?.Invoke();
        recoveryService.PlaybackStartedAccepted += AcceptPlaybackStarted;
        recoveryService.CanLoadMoreSourcesChanged += () => Changed(nameof(CanLoadMoreSources));
        recoveryService.SyncClockCacheRequested += SyncPlaybackClockCache;

        qualityService.PlaybackSourceBindingsRequested += PlaybackSourceBindings;
        qualityService.SourcesUpdatedForLyrics += () => SourcesUpdatedForLyrics?.Invoke();
        qualityService.RestoreLyricsIfNeeded += () => RestoreLyricsRequested?.Invoke();
        qualityService.OfficialSongAccepted += AcceptOfficialSong;
    }

    // Explicit cross-domain events wired at MusicCapability composition root.
    internal event Action? PositionRefreshed;
    internal event Action? LyricsPreviewAvailabilityChanged;
    internal event Action? SourcesUpdatedForLyrics;
    internal event Action? PlaybackStopped;
    internal event Action? RestoreLyricsRequested;
    internal event Action? DesktopLyricsStateChanged;
    internal event Action? PlaybackModeAvailabilityChanged;
    internal event Action? ExplicitPauseRequested;
    internal event Func<bool, CancellationToken, Task<bool>>? SteppingNextOverride;
    internal event Action? QueueMutated;
    internal event Action? PlaybackModeChanged;
    internal event Action? SearchRecommendationCleared;
    internal event Action? QueueApplied;
    internal event Action? PlaybackStateObserved;
    internal event Action? PlaybackStateChanged;
    internal event Action? PlaybackStartedRecorded;
    internal event Action? PlaybackCommitted;
    internal event Func<long, Task>? LibraryCollectionEdited;

    private MusicPlaybackTelemetry CaptureTelemetry()
    {
        var active = recoveryService?.ActiveRecovery;
        return new(
            Current: current,
            OpenedEntry: openedEntry,
            OwnsCurrent: OwnsCurrent,
            Recorded: recorded,
            PlaybackEpoch: playbackEpoch,
            Mode: mode,
            DurationSeconds: DurationSeconds,
            PresentationKey: PlaybackPresentationKey,
            ActiveTarget: active?.Media?.Target,
            ReopeningPending: active?.Pending is { Reopening: true },
            HasPending: active?.Pending is not null);
    }

    internal void NotifyLyricsPosition() => PositionRefreshed?.Invoke();
    internal void NotifyLyricsPreviewAvailability() => LyricsPreviewAvailabilityChanged?.Invoke();
    internal void NotifyPlaybackModeAvailability() => PlaybackModeAvailabilityChanged?.Invoke();

    public MusicPlaybackClock Clock { get; }

    internal SongRow? CurrentSong => current;
    internal MusicQueueSourceRecord? QueueSource => queueSource;
    internal MusicPlayContextRecord? CurrentFeedbackContext => metadataService.CurrentFeedbackContext;
    internal long QueueEpoch => queueEpoch;
    internal long PlaybackEpoch => playbackEpoch;
    internal long PlaybackEvent => playbackEvent;
    internal bool QueueSaved => queueSaved;
    internal string? OpenedEntry => openedEntry;
    internal bool Recorded => recorded;
    internal bool OwnsCurrent => current is not null && playback.OwnerId == Id && playback.Identity == current.EntryId;

    public string CurrentSongId => current?.Id ?? "";
    public string CurrentTitle => current?.Title ?? "未在播放";
    public string CurrentArtist => current?.Artist ?? "";
    public string CurrentAlbum => current?.Album ?? "";
    public string CurrentArtwork => !stopping ? current?.Cover ?? "" : "";
    public string CurrentArtworkIdentity => current?.Id ?? "";
    public string CurrentTrackIdentity => current is null ? "" : (current.EntryId ?? "") + "\0" + current.Id;
    public string CurrentSource => recoveryService.ActiveRecovery?.Media?.Target is { Official: true } official
        ? official.Platform switch { "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐", _ => "" }
        : recoveryService.PlaybackInstallation is not null ? settings.Sources.FirstOrDefault(s => s.InstallationId == recoveryService.PlaybackInstallation)?.Label ??
            (recoveryService.ActiveRecovery?.Media?.Target?.ProviderName is { Length: > 0 } name ? name : "JS 音源")
        : current is { IsOnline: false } ? "本地音乐" : "";
    public bool IsPlaying => OwnsCurrent && playback.IsPlaying;
    public bool HasCurrent => current is not null;
    public bool CanStep => !stopping && HasCurrent && queueSaved;
    public bool CanPlay => !stopping && HasCurrent && queueSaved;
    public bool CanTogglePlayback => !stopping && HasCurrent && queueSaved;
    public bool CanFavoriteCurrent => !stopping && HasCurrent && !favoriteSubmitting;
    public bool CanAddCurrentToPlaylist => !stopping && HasCurrent && !addToPlaylistSubmitting;
    public string PlaybackPresentationKey => (current?.EntryId ?? "") + ":" + playbackEpoch;
    public bool CanSeek => CanPlay && OwnsCurrent && playback.HasMedia && playback.CanSeek;
    public bool IsBuffering => OwnsCurrent && playback.IsBuffering;
    public double BufferedSeconds => OwnsCurrent ? playback.BufferedSeconds : 0;
    public string? LastPlaybackError => OwnsCurrent ? playback.LastErrorCode : null;
    public double PositionSeconds => OwnsCurrent ? playback.PositionSeconds : 0;
    public double DurationSeconds => OwnsCurrent ? Math.Max(1, playback.DurationSeconds) : 1;
    public int Volume
    {
        get => playback.Volume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (clamped == playback.Volume) return;
            if (!stopping) { settings.NoteVolumeEdited(); playback.Volume = clamped; }
            Changed();
        }
    }
    public int Mode => mode;
    public string ModeText => mode switch { 1 => "列表循环", 2 => "随机播放", _ => "单曲循环" };
    public Brush PlayerBackground => ctx.PlayerBackground;
    public Brush PlayerProgressBrush => ctx.PlayerProgress;
    public bool IsCurrentFavorite => currentFavorite;
    public string CurrentFavoriteLabel => currentFavorite ? "取消收藏当前歌曲" : "收藏当前歌曲";
    public double PlaybackCacheFraction => OwnsCurrent && recoveryService.ActiveRecovery is { } state
        ? state.Cached ? 1 : state.Media?.Bytes?.BufferedFraction ?? 0 : 0;

    // Chorus & Playback rate projection
    public bool HasChorus => metadataService.HasChorus;
    public string ChorusLabel => metadataService.ChorusLabel;
    public double ChorusPositionSeconds => metadataService.ChorusPositionSeconds;
    public double PlaybackRate => metadataService.PlaybackRate;
    public string PlaybackRateText => metadataService.PlaybackRateText;
    public bool CanChangePlaybackRate => CanSeek;
    public double? AcceptedPlaybackRate => metadataService.AcceptedPlaybackRate;
    public Task ChangePlaybackRateAsync(double value) => metadataService.ChangePlaybackRateAsync(value, CanChangePlaybackRate);

    // Source & quality projection
    public ObservableCollection<MusicSourceChoice> SourceChoices => qualityService.SourceChoices;
    public bool HasSourceChoices => qualityService.HasSourceChoices;
    public bool HasConfirmedSourceChoices => qualityService.HasConfirmedSourceChoices;
    public bool HasEmptySources => qualityService.HasEmptySources;
    public MusicSourceChoice? AutomaticSourceChoice => qualityService.AutomaticSourceChoice;
    public bool CanLoadMoreSources => qualityService.CanLoadMoreSources;
    public bool CanSwitchSource => qualityService.CanSwitchSource;
    public bool CanExcludeCurrentRecording => qualityService.CanExcludeCurrentRecording;
    public string CurrentQualityLabel => qualityService.CurrentQualityLabel;
    public bool HasQualityButton => qualityService.HasQualityButton;
    public string CurrentSourceQuality => qualityService.CurrentSourceQuality;
    public string CurrentSourceSymbol => qualityService.CurrentSourceSymbol;
    public string CurrentSourceDescription => qualityService.CurrentSourceDescription;
    public bool IsLoadingSources => qualityService.IsLoadingSources;
    public IReadOnlyList<MusicQualityChoice> QualityChoices() => qualityService.QualityChoices();
    public IReadOnlyList<MusicQualityChoice> SpatialChoices() => qualityService.SpatialChoices();
    public void OpenSourceChoices() => qualityService.OpenSourceChoices();
    public void CloseSourceChoices() => qualityService.CloseSourceChoices();
    public Task LoadMoreSourcesAsync() => qualityService.LoadMoreSourcesAsync();
    public Task SelectSourceChoiceAsync(MusicSourceChoice choice) => qualityService.SelectSourceChoiceAsync(choice);
    public Task SwitchQualityAsync(string choice) => qualityService.SwitchQualityAsync(choice);
    public Task ReportWrongSourceAsync() => qualityService.ReportWrongSourceAsync();
    public Task ReportWrongRecordingAsync() => qualityService.ReportWrongRecordingAsync();

    internal void SyncPlaybackClockPosition() => Clock.UpdatePosition(PositionSeconds);
    internal void SyncPlaybackClockCache() => Clock.UpdateCacheFraction(PlaybackCacheFraction);
    internal void SyncPlaybackClock() =>
        Clock.Update(PositionSeconds, OwnsCurrent ? playback.DurationSeconds : 0, PlaybackCacheFraction);

    internal void RefreshPlayerBackground()
    {
        Changed(nameof(PlayerBackground));
        Changed(nameof(PlayerProgressBrush));
    }

    public void RefreshPlayerAppearance(Brush sampled, bool animate = true) =>
        ctx.RefreshAppearance(sampled, animate);

    internal void NotifyVolumeChanged() => Changed(nameof(Volume));
    internal void CancelOnline() => recoveryService.CancelOnline();
    internal void CancelMusicPreload() => cacheService.CancelPreload();
    internal void ObservePreloadQueueMutation(IMusicQueuePosition snapshot) => cacheService.ObserveQueueMutation(snapshot);
    internal void IncrementSelectionSourcesRevision() => sourceService.IncrementSelectionSourcesRevision();
    internal void AdvanceQueueEpoch() => queueEpoch++;
    internal void AdvanceQueueEpochAndRead() { queueEpoch++; queueRead++; }
    internal void MarkQueueUnsaved() { queueSaved = false; ctx.RefreshBindings(); }
    internal void RefreshPresentationAvailability() => playbackPresentation.RefreshAvailability();
    internal void RefreshPresentation() => playbackPresentation.Refresh();
    internal void AdvancePlaybackEpochForShutdown() { playbackEpoch++; Changed(nameof(PlaybackPresentationKey)); }
    internal void FinishFeedback(string reason) => metadataService.FinishFeedback(reason);
    internal void TrackMusicRecovery(Task task) => recoveryService.TrackRecovery(task);
    internal Task RetireMusicRecovery() => recoveryService.RetireAsync();
    internal Task RevokeChangedMusicSource(string? installation) => recoveryService.RevokeChangedSourceAsync(installation);

    internal void OnQualityPolicyAccepted(string signature, bool signatureChanged)
    {
        if (signatureChanged)
            recoveryService.QualityPolicyChanged(signature);
        else if (cacheService.PreparedAudioReceiptSignature is { } preparedSig && preparedSig != signature)
            cacheService.CancelPreload();
    }

    internal async Task RefreshCurrentSongSnapshotAsync(CancellationToken token)
    {
        if (stopping || disposed || current is not { } song) return;
        var snapshot = await application.SongSnapshotsAsync([song.Id], token);
        token.ThrowIfCancellationRequested();
        if (stopping || disposed || current is not { } currentSong || currentSong.Id != song.Id) return;
        currentSong.ApplyRepository(snapshot.Songs.GetValueOrDefault(song.Id), snapshot.Publication);
    }

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        playbackPresentation.RefreshAvailability();
        Changed(nameof(CanSwitchSource));
        Changed(nameof(CanLoadMoreSources));
    }

    internal void PlaybackSourceBindings()
    {
        // A replacement receipt can be committed after the native state events.
        // Publish from that commit, not from a later unrelated position tick.
        metadataService.SyncChorus();
        playbackPresentation.Refresh();
    }

    internal void RefreshCurrentArtwork()
    {
        bool changedSong = artworkSongId != current?.Id;
        artworkSongId = current?.Id;
        if (changedSong) Changed(nameof(CurrentArtworkIdentity));
        string next = CurrentArtwork;
        if (currentArtwork == next) return;
        currentArtwork = next;
        Changed(nameof(CurrentArtwork));
    }

    private void AcceptOfficialSong(SongRow song, MusicOnlineTarget? target)
    {
        if (target?.RecordingSong is null) return;
        if (current?.EntryId == song.EntryId && current?.Id == song.Id) RefreshCurrentArtwork();
    }

    internal void SubscribePlayback()
    {
        if (playbackSubscribed) return;
        if (current is not null) current.PropertyChanged += CurrentSongChanged;
        application.ChorusInvalidated += metadataService.OnChorusAccountChanged;
        application.AccountDetailsInvalidated += recoveryService.OnOfficialAccountChanged;
        playback.PropertyChanged += PlaybackChanged;
        playback.Started += PlaybackStarted; playback.Ended += PlaybackEnded; playback.Failed += PlaybackFailed;
        playbackSubscribed = true;
    }

    internal void UnsubscribePlayback()
    {
        if (!playbackSubscribed) return;
        if (current is not null) current.PropertyChanged -= CurrentSongChanged;
        application.ChorusInvalidated -= metadataService.OnChorusAccountChanged;
        application.AccountDetailsInvalidated -= recoveryService.OnOfficialAccountChanged;
        metadataService.ClearChorus();
        playback.PropertyChanged -= PlaybackChanged;
        playback.Started -= PlaybackStarted; playback.Ended -= PlaybackEnded; playback.Failed -= PlaybackFailed;
        playbackSubscribed = false;
    }

    internal Task RefreshCurrentFavoriteAsync()
    {
        var song = current;
        var generation = ++favoriteRead;
        if (song is null) { PublishFavorite(false); return Task.CompletedTask; }
        var task = Read();
        loadDrains.Add(task);
        _ = Observe();
        return task;
        async Task Observe() { try { await task; } finally { loadDrains.Remove(task); } }
        async Task Read()
        {
            try
            {
                var result = await application.GetCollectionMembershipAsync("favorites", song.Record, lifetime.Token);
                if (!stopping && generation == favoriteRead && ReferenceEquals(song, current))
                    PublishFavorite(result.Present);
            }
            catch
            {
                if (!stopping && generation == favoriteRead && ReferenceEquals(song, current)) PublishFavorite(false);
            }
        }
    }

    private void PublishFavorite(bool value)
    {
        currentFavorite = value;
        Changed(nameof(IsCurrentFavorite)); Changed(nameof(CurrentFavoriteLabel));
    }

    public Task ToggleCurrentFavoriteAsync()
    {
        var song = current; var origin = ctx.LoadGeneration;
        if (song is null || !CanFavoriteCurrent) return Task.CompletedTask;
        favoriteSubmitting = true;
        Changed(nameof(CanFavoriteCurrent));
        return RunAsync(async () =>
        {
            try
            {
                var membership = await application.GetCollectionMembershipAsync("favorites", song.Record, lifetime.Token);
                if (membership.Present)
                    await application.RemoveSongFromCollectionAsync("favorites", membership.Revision,
                        membership.SongId!, lifetime.Token);
                else
                    await application.AddSongsToCollectionAsync("favorites", membership.Revision, [song.Record], lifetime.Token);
                if (LibraryCollectionEdited is { } edited) await edited(origin);
                await RefreshCurrentFavoriteAsync();
            }
            finally
            {
                favoriteSubmitting = false;
                if (!disposed) Changed(nameof(CanFavoriteCurrent));
            }
        });
    }

    // The footer acts on the captured playing song, not the content-page selection.
    public Task AddCurrentToPlaylistAsync(string id)
    {
        var song = current; var origin = ctx.LoadGeneration;
        if (song is null || !CanAddCurrentToPlaylist || !contentState.Playlists.Any(p => p.Id == id)) return Task.CompletedTask;
        addToPlaylistSubmitting = true;
        Changed(nameof(CanAddCurrentToPlaylist));
        return RunAsync(async () =>
        {
            try
            {
                var detail = await application.GetCollectionDetailAsync(id, 0, 1, lifetime.Token);
                await application.AddSongsToCollectionAsync(id,
                    detail.Collection.Revision, [song.Record], lifetime.Token);
                if (LibraryCollectionEdited is { } edited) await edited(origin);
            }
            finally
            {
                addToPlaylistSubmitting = false;
                if (!disposed) Changed(nameof(CanAddCurrentToPlaylist));
            }
        });
    }

    public Task OpenPlayerDetailAsync() => ctx.OpenPlayerDetailAsync();
    public Task OpenPlayQueueAsync() => ctx.OpenPlayQueueAsync();
    public Task ExecuteMediaShortcutAsync(MeowFilm.Desktop.MediaShortcutAction action) => ctx.ExecuteMediaShortcutAsync(action);

    public Task TogglePlaybackAsync()
    {
        catalogActions.Invalidate();
        if (!stopping && current is not null && playback.OwnerId == Id && playback.Identity == current.EntryId &&
            (playback.HasMedia || recoveryService.ActiveRecovery?.Recovering == true))
        {
            // Admission is synchronous: a queued/awaiting resolver cannot delay Pause.
            if (!playback.PauseRequested) ExplicitPauseRequested?.Invoke();
            var command = playback.PauseRequested ? ResumePlaybackAsync() : playback.PauseAsync();
            return RunPlaybackActionAsync(_ => command, supersede: false);
        }
        if (recoveryService.CurrentPlaybackExcluded) return ResumePlaybackAsync();
        recoveryService.CancelOnline();
        return RunPlaybackActionAsync(async generation =>
        {
            if (current is null || !queueSaved) return;
            if (playback.OwnerId == Id && playback.Identity == current.EntryId && playback.HasMedia)
            {
                if (playback.IsPlaying) await playback.PauseAsync(); else await ResumePlaybackAsync();
            }
            else await StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration);
        }, supersede: true);
    }

    /// <summary>Every resume entry, including system Play, keeps durable wrong-source exclusions.</summary>
    public Task ResumePlaybackAsync()
    {
        if (stopping || !OwnsCurrent) return Task.CompletedTask;
        if (recoveryService.CurrentPlaybackExcluded)
            return Notify("当前音源已排除，请切换音源或在错源管理中恢复");
        // Keep admission synchronous: a Pause during an awaiting operation must still win.
        return playback.ResumeAsync();
    }

    public Task StepAsync(bool next, string reason = "manual")
    {
        catalogActions.Invalidate();
        recoveryService.TrackRecovery(recoveryService.RetireAsync());
        recoveryService.CancelOnline();
        return RunPlaybackActionAsync(generation => StepCore(next, reason, () => generation == ctx.PlaybackActionGeneration), supersede: true);
    }

    public Task StepFromPresentationAsync(bool next, string presentationKey)
    {
        if (stopping || string.IsNullOrEmpty(presentationKey) || presentationKey != PlaybackPresentationKey)
            return Task.CompletedTask;
        catalogActions.Invalidate();
        recoveryService.TrackRecovery(recoveryService.RetireAsync());
        recoveryService.CancelOnline();
        return RunPlaybackActionAsync(async generation =>
        {
            if (presentationKey != PlaybackPresentationKey || current is null || !queueSaved) return;
            await StepCore(next, "manual", () => generation == ctx.PlaybackActionGeneration);
        }, supersede: true);
    }

    private async Task StepCore(bool next, string reason, Func<bool>? admission = null)
    {
        if (admission is not null && !admission()) return;
        if (next && SteppingNextOverride is { } stepNext && await stepNext(reason == "ended", lifetime.Token))
            return;
        var reply = await QueueCallAsync(ct => application.StepQueueAsync(next ? 1 : -1, reason: next ? reason : "manual", ct: ct));
        if (reply.TargetId is null)
        {
            if (reason == "ended") await StopOwnedPlayback();
            return;
        }
        await ReadQueue(lifetime.Token);
        if (admission is not null && !admission()) return;
        await StartCurrent(lifetime.Token, reason == "ended", admission: admission);
        QueueMutated?.Invoke();
    }

    public Task ChangeModeAsync(int value)
    {
        catalogActions.Invalidate();
        return RunPlaybackActionAsync(async _ =>
        {
            if (value is < 1 or > 3) throw new MusicOperationException("INVALID_REQUEST");
            var reply = await QueueCallAsync(ct => application.SetPlaybackModeAsync(value, ct));
            int next = reply.Mode;
            MusicDto.Require(next is >= 1 and <= 3);
            if (mode != next)
            {
                mode = next;
                Changed(nameof(Mode)); Changed(nameof(ModeText));
                PlaybackModeChanged?.Invoke();
            }
        }, supersede: false);
    }

    public Task SeekAsync(double seconds)
    {
        var epoch = playbackEpoch;
        return RunPlaybackActionAsync(_ => SeekWithFeedbackGuardAsync(seconds, epoch), supersede: false);
    }

    internal async Task SeekWithFeedbackGuardAsync(double seconds, long epoch)
    {
        if (epoch != playbackEpoch || playback.OwnerId != Id || !playback.HasMedia) return;
        // Explicit discontinuity: never count a seek (including lyric-tap seek) as listened time.
        metadataService.SuspendFeedback();
        try { await playback.SeekAsync(seconds); }
        finally { metadataService.ResumeFeedback(epoch); }
    }

    internal async Task<T> QueueCallAsync<T>(Func<CancellationToken, Task<T>> action, bool clearsSearchRecommendation = false)
        where T : IMusicQueuePosition
    {
        queueEpoch++;
        if (clearsSearchRecommendation) SearchRecommendationCleared?.Invoke();
        try
        {
            var result = await action(lifetime.Token);
            cacheService.ObserveQueueMutation(result);
            QueueMutated?.Invoke();
            return result;
        }
        catch (Exception error)
        {
            if (error is not MusicOperationException { Code: "INVALID_REQUEST" })
            {
                queueSaved = false; ctx.RefreshBindings();
            }
            QueueMutated?.Invoke();
            throw;
        }
    }

    internal async Task ReadQueue(CancellationToken cancellationToken)
    {
        if (stopping) return;
        var epoch = queueEpoch; var serial = ++queueRead;
        var result = await application.GetQueueAsync(0, 1, cancellationToken);
        cacheService.ObserveQueueSnapshot(result);
        var state = ParseQueue(result);
        if (stopping || epoch != queueEpoch || serial != queueRead) return;
        ApplyQueue(state);
    }

    internal static QueueSnapshot ParseQueue(MusicQueuePageResult reply)
    {
        MusicDto.Require(reply.Mode is >= 1 and <= 3);
        MusicDto.Require(reply.PersistenceState is "saved" or "pending");
        SongRow? current = reply.Current is null ? null
            : SongRow.FromRecord(reply.Current.Song, reply.Current.EntryId);
        MusicDto.Require(current is null ? reply.CurrentId is null : reply.CurrentId == current.EntryId);
        var feedbackContext = reply.Current is { Context.IsEmpty: false } entry ? entry.Context : null;
        return new(current, reply.Mode, reply.PersistenceState == "saved", feedbackContext, reply.Source);
    }

    internal void ApplyQueue(QueueSnapshot state)
    {
        if (current is not null && state.Current is not null && current.EntryId == state.Current.EntryId &&
            current.Record.ContentEquals(state.Current.Record))
            state = state with { Current = current };
        var before = (CurrentSongId, CurrentTitle, CurrentArtist, CurrentAlbum, HasCurrent,
            CurrentTrackIdentity, PlaybackPresentationKey, Mode, Saved: queueSaved);
        var changedCurrent = !ReferenceEquals(current, state.Current);
        if (changedCurrent && current is not null) current.PropertyChanged -= CurrentSongChanged;
        current = state.Current; queueSource = state.Source; mode = state.Mode; queueSaved = state.Saved;
        if (changedCurrent && current is not null && playbackSubscribed) current.PropertyChanged += CurrentSongChanged;
        if (before.CurrentSongId != CurrentSongId) Changed(nameof(CurrentSongId));
        if (before.CurrentTitle != CurrentTitle) Changed(nameof(CurrentTitle));
        if (before.CurrentArtist != CurrentArtist) Changed(nameof(CurrentArtist));
        if (before.CurrentAlbum != CurrentAlbum) Changed(nameof(CurrentAlbum));
        if (before.HasCurrent != HasCurrent) Changed(nameof(HasCurrent));
        if (before.CurrentTrackIdentity != CurrentTrackIdentity) Changed(nameof(CurrentTrackIdentity));
        if (before.PlaybackPresentationKey != PlaybackPresentationKey) Changed(nameof(PlaybackPresentationKey));
        if (before.Mode != Mode) { Changed(nameof(Mode)); Changed(nameof(ModeText)); }
        RefreshCurrentArtwork();
        if (changedCurrent || before.Saved != queueSaved) playbackPresentation.Refresh();
        metadataService.CurrentFeedbackContext = state.FeedbackContext;
        metadataService.UpdateFeedbackSelection();
        qualityService.UpdateSourceChoicesIfOpen();
        SourcesUpdatedForLyrics?.Invoke();
        QueueApplied?.Invoke();
        ctx.RefreshBindings();
        if (changedCurrent) { PublishFavorite(false); _ = RefreshCurrentFavoriteAsync(); }
    }

    private void CurrentSongChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (stopping || !ReferenceEquals(sender, current)) return;
        switch (args.PropertyName)
        {
            case nameof(SongRow.Title): Changed(nameof(CurrentTitle)); break;
            case nameof(SongRow.Artist): Changed(nameof(CurrentArtist)); break;
            case nameof(SongRow.Album): Changed(nameof(CurrentAlbum)); break;
            case nameof(SongRow.Cover): RefreshCurrentArtwork(); break;
            case nameof(SongRow.IsOnline): playbackPresentation.Refresh(); break;
        }
    }

    internal async Task<bool> StopOwnedPlayback(Func<bool>? admission = null)
    {
        if (admission is not null && !admission()) return false;
        var epoch = playbackEpoch; var entry = openedEntry;
        var outputIdentity = playback.OwnerId == Id ? playback.Identity : null;
        await recoveryService.RetireAsync();
        if (epoch != playbackEpoch || openedEntry != entry || admission is not null && !admission() ||
            playback.OwnerId == Id && playback.Identity != outputIdentity && playback.Identity != entry)
            return false;
        metadataService.FinishFeedback("ui");
        metadataService.ClearChorus();
        recoveryService.CancelOnline();
        playbackEpoch++; openedEntry = null; recorded = false;
        SearchRecommendationCleared?.Invoke();
        Changed(nameof(PlaybackPresentationKey));
        bool hadInstallation = recoveryService.ClearInstallationIfPresent();
        PlaybackStopped?.Invoke();
        if (hadInstallation) PlaybackSourceBindings();
        if (playback.OwnerId == Id) await playback.StopAsync();
        return true;
    }

    internal async Task StartCurrent(CancellationToken cancellationToken, bool automatic = false,
        Func<bool>? admission = null, Action<long>? ownsEpoch = null,
        Recommendations.MusicRecommendationFeedback? recommendation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (admission is not null && !admission()) throw new OperationCanceledException();
        if (stopping) throw new MusicOperationException("SHUTTING_DOWN");
        if (current is not { EntryId: not null } selected) throw new MusicOperationException("NO_PLAYBACK_TARGET");
        if (!queueSaved) throw new MusicOperationException("PENDING_WRITE");
        bool Admitted() => !stopping && !disposed && selected == current && (admission is null || admission());
        if (!await StopOwnedPlayback(Admitted) || !Admitted()) throw new OperationCanceledException();
        metadataService.SelectFeedback(selected, automatic, recommendation);
        var epoch = playbackEpoch;
        ownsEpoch?.Invoke(epoch);
        if (selected.IsOnline)
        {
            await recoveryService.OpenOnlineCurrentAsync(selected, epoch, cancellationToken, admission);
            await metadataService.RestorePlaybackRateAsync(selected, epoch, cancellationToken);
            if (epoch == playbackEpoch) RestoreLyricsRequested?.Invoke();
            return;
        }
        using var request = recoveryService.BeginLocalOnlineRequest(cancellationToken);
        try
        {
            var media = new ResolvedMusic(await files.ResolveAsync(selected.Record.LocalFileId ?? "", request.Token), new Dictionary<string, string>());
            request.Token.ThrowIfCancellationRequested();
            if (stopping || epoch != playbackEpoch || selected != current || admission is not null && !admission())
                throw new OperationCanceledException();
            openedEntry = selected.EntryId; recorded = false;
            await playback.OpenAsync(Id, selected.EntryId, media.Uri!, selected.Title, media.Headers, request.Token);
            await metadataService.RestorePlaybackRateAsync(selected, epoch, request.Token);
            if (epoch == playbackEpoch) RestoreLyricsRequested?.Invoke();
        }
        finally { recoveryService.EndLocalOnlineRequest(request); }
    }

    private void PlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (stopping) return;
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName is nameof(NativeAudioPlayback.DurationSeconds) or nameof(NativeAudioPlayback.HasMedia))
            recoveryService.CheckMediaDuration();
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName is nameof(NativeAudioPlayback.PositionSeconds) or nameof(NativeAudioPlayback.DurationSeconds) or
            nameof(NativeAudioPlayback.IsPlaying) or nameof(NativeAudioPlayback.HasMedia) or
            nameof(NativeAudioPlayback.OwnerId) or nameof(NativeAudioPlayback.Identity))
            metadataService.SampleFeedback();
        if (!OwnsCurrent || !playback.HasMedia) metadataService.ClearRateReceipt();
        if ((!OwnsCurrent || !playback.HasMedia) && recoveryService.ActiveRecovery is not { Opening: true } ||
            args.PropertyName == nameof(NativeAudioPlayback.OwnerId) && playback.OwnerId is not null && playback.OwnerId != Id)
            SearchRecommendationCleared?.Invoke();
        if (args.PropertyName == nameof(NativeAudioPlayback.OwnerId) && playback.OwnerId != Id &&
            recoveryService.ActiveRecovery is { Opening: false })
            recoveryService.TrackRecovery(recoveryService.RetireAsync());
        cacheService.ObserveMusicPreload(recoveryService.ActiveRecovery, recoveryService.CurrentRecovery,
            sourceService, IsPlaying, PositionSeconds, DurationSeconds);
        if (args.PropertyName == nameof(NativeAudioPlayback.PositionSeconds))
            playbackPresentation.RefreshPosition();
        else
        {
            if (args.PropertyName != nameof(NativeAudioPlayback.Volume) &&
                args.PropertyName != nameof(NativeAudioPlayback.IsBuffering))
            {
                PlaybackStateObserved?.Invoke();
                metadataService.SyncChorus();
            }
            playbackPresentation.Refresh();
            DesktopLyricsStateChanged?.Invoke();
            PlaybackStateChanged?.Invoke();
        }
    }

    private void PlaybackStarted(object? sender, EventArgs args)
    {
        if (stopping || playback.OwnerId != Id || playback.Identity != openedEntry || openedEntry is null) return;
        var state = recoveryService.ActiveRecovery;
        if (state is not null)
        {
            if (!recoveryService.CurrentRecovery(state)) return;
            if (state.Pending is { } pending)
            {
                if (recoveryService.CurrentPendingMedia(state, pending)) pending.SourceStarted = true;
                return;
            }
        }
        AcceptPlaybackStarted(state);
    }

    private void AcceptPlaybackStarted(MusicRecovery? state)
    {
        if (state is not null && !recoveryService.CurrentRecovery(state)) return;
        recoveryService.CheckMediaDuration();
        if (state?.PendingFailure == "RECORDING_MISMATCH") return;
        PlaybackCommitted?.Invoke();
        if (recorded)
        {
            if (state is { ConfirmSourceOnStart: true })
            {
                state.ConfirmSourceOnStart = false;
                recoveryService.ConfirmOfficialPlayback();
                cacheService.ConfirmFileCache(current, OwnsCurrent, CurrentArtwork, state, recoveryService.CurrentRecovery);
                metadataService.StartFeedback(); metadataService.SampleFeedback();
            }
            return;
        }
        if (state is not null)
            state.ConfirmSourceOnStart = false;
        var entry = openedEntry; var epoch = playbackEpoch; var stamp = ctx.StatusEpoch;
        recorded = true; playbackEvent++;
        recoveryService.ConfirmOfficialPlayback();
        cacheService.ConfirmFileCache(current, OwnsCurrent, CurrentArtwork, state, recoveryService.CurrentRecovery);
        metadataService.StartFeedback(observedStart: true);
        PlaybackStartedRecorded?.Invoke();
        var task = Record();
        AddEventWrite(task); _ = Observe();
        async Task Record()
        {
            try { await application.RecordPlayedHistoryAsync(entry!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lifetime.Token); }
            catch (OperationCanceledException) { }
            catch
            {
                if (!stopping && epoch == playbackEpoch && stamp == ctx.StatusEpoch)
                    ctx.SetStatus("播放记录保存失败");
            }
        }
        async Task Observe() { try { await task; } finally { RemoveEventWrite(task); } }
    }

    private void PlaybackEnded(object? sender, EventArgs args)
    {
        if (stopping || playback.OwnerId != Id || openedEntry is null || playback.Identity != openedEntry || endedEpoch == playbackEpoch) return;
        recoveryService.TrackRecovery(recoveryService.RetireAsync());
        metadataService.FinishFeedback("end");
        var epoch = endedEpoch = playbackEpoch; var entry = openedEntry;
        _ = RunPlaybackActionAsync(async _ =>
        {
            if (!stopping && epoch == playbackEpoch && current?.EntryId == entry && playback.OwnerId == Id && playback.Identity == entry)
                await StepCore(true, "ended");
        }, supersede: false);
    }

    private void PlaybackFailed(object? sender, EventArgs args)
    {
        if (playback.OwnerId == Id && playback.Identity == openedEntry && !recorded)
            SearchRecommendationCleared?.Invoke();
        if (recoveryService.TryRecover()) return;
        if (recoveryService.ActiveRecovery is { Opening: false })
            recoveryService.TrackRecovery(recoveryService.RetireAsync());
        if (!stopping && playback.OwnerId == Id && playback.Identity == openedEntry)
        {
            metadataService.FinishFeedback("error");
            ctx.SetStatus(playback.LastErrorCode == PlaybackError.AudioDeviceUnavailable
                ? MusicRuntimeContext.ErrorText(new MusicOperationException(PlaybackError.AudioDeviceUnavailable))
                : "播放失败");
        }
    }

    internal void AdmitSourceExclusion(string songId, string provider, string platform) =>
        recoveryService.AdmitSourceExclusion(songId, provider, platform);

    internal void OnSourceExclusionAdded(string songId, string provider, string platform) =>
        recoveryService.OnSourceExclusionAdded(songId, provider, platform);

    internal Task OnSourceExclusionRemovedAsync(string songId, CancellationToken token) =>
        recoveryService.OnSourceExclusionRemovedAsync(songId, token);

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, MusicSongRecord?> read, long publication)
    {
        if (current is { } song && ids.Contains(song.Id))
            song.ApplyRepository(read(song.Id), publication);
    }
}
