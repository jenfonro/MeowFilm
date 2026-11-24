using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for music playback transport, queue playback state, clock, artwork,
/// current favorite, source selection/recovery/preload/file-cache, chorus, rate, and feedback tracking.
/// </summary>
public sealed partial class MusicPlaybackController : MusicController,
    IMusicPlaybackCoordinator
{
    internal sealed record QueueSnapshot(SongRow? Current, int Mode, bool Saved, MusicPlayContextRecord? FeedbackContext = null, MusicQueueSourceRecord? Source = null);

    private readonly MusicSettingsController settings;
    private readonly MusicLyricsController lyrics;
    private readonly MusicPlaybackPresentation playbackPresentation;
    private readonly MusicPlaybackSourceService sourceService;
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
    private CancellationTokenSource? onlineCancellation;
    private string? playbackInstallation;

    internal MusicPlaybackController(MusicRuntimeContext ctx, MusicSettingsController settings, MusicLyricsController lyrics) : base(ctx)
    {
        this.settings = settings;
        this.lyrics = lyrics;
        Clock = new MusicPlaybackClock();
        playbackPresentation = new(this, property => Changed(property));
        sourceService = new(ctx, settings, TryGetVerifiedPreparedReceipt);
    }

    private IMusicQueueCoordinator queuePort = null!;
    private IMusicCatalogCoordinator catalogPort = null!;
    private IMusicLibraryCoordinator libraryPort = null!;
    private IMusicSearchCoordinator searchPort = null!;

    internal void AttachPorts(
        IMusicQueueCoordinator queue,
        IMusicCatalogCoordinator catalog,
        IMusicLibraryCoordinator library,
        IMusicSearchCoordinator search)
    {
        queuePort = queue;
        catalogPort = catalog;
        libraryPort = library;
        searchPort = search;
    }

    SongRow? IMusicPlaybackCoordinator.CurrentSong => CurrentSong;
    bool IMusicPlaybackCoordinator.CanSeek => CanSeek;
    bool IMusicPlaybackCoordinator.OwnsCurrent => OwnsCurrent;
    long IMusicPlaybackCoordinator.PlaybackEpoch => PlaybackEpoch;
    double IMusicPlaybackCoordinator.PositionSeconds => PositionSeconds;
    bool IMusicPlaybackCoordinator.QueueSaved => QueueSaved;
    string? IMusicPlaybackCoordinator.PreparedAudioReceiptSignature => PreparedAudioReceiptSignature;
    void IMusicPlaybackCoordinator.AdmitSourceExclusion(string songId, string provider, string platform) => AdmitSourceExclusion(songId, provider, platform);
    void IMusicPlaybackCoordinator.CancelMusicPreload() => CancelMusicPreload();
    void IMusicPlaybackCoordinator.NotifyVolumeChanged() => NotifyVolumeChanged();
    void IMusicPlaybackCoordinator.OnSourceExclusionAdded(string songId, string provider, string platform) => OnSourceExclusionAdded(songId, provider, platform);
    Task IMusicPlaybackCoordinator.OnSourceExclusionRemovedAsync(string songId, CancellationToken token) => OnSourceExclusionRemovedAsync(songId, token);
    void IMusicPlaybackCoordinator.PlaybackSourceBindings() => PlaybackSourceBindings();
    void IMusicPlaybackCoordinator.QualityPolicyChanged(string signature) => QualityPolicyChanged(signature);
    Task IMusicPlaybackCoordinator.RevokeChangedMusicSourceAsync(string? installation) => RevokeChangedMusicSource(installation);
    Task IMusicPlaybackCoordinator.ResumePlaybackAsync() => ResumePlaybackAsync();
    Task IMusicPlaybackCoordinator.SeekWithFeedbackGuardAsync(double seconds, long epoch) => SeekWithFeedbackGuardAsync(seconds, epoch);

    internal void NotifyLyricsPosition() => lyrics.RefreshLyricPosition();
    internal void NotifyLyricsPreviewAvailability() => lyrics.RefreshLyricPreviewAvailability();
    internal void NotifyPlaybackModeAvailability() => queuePort.NotifyPlaybackModeAvailability();

    public MusicPlaybackClock Clock { get; }

    internal SongRow? CurrentSong => current;
    internal MusicQueueSourceRecord? QueueSource => queueSource;
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
    public string CurrentSource => musicRecovery?.Media?.Target is { Official: true } official
        ? official.Platform switch { "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐", _ => "" }
        : playbackInstallation is not null ? settings.Sources.FirstOrDefault(s => s.InstallationId == playbackInstallation)?.Label ??
            (musicRecovery?.Media?.Target?.ProviderName is { Length: > 0 } name ? name : "JS 音源")
        : current is { IsOnline: false } ? "本地音乐" : "";
    public bool IsPlaying => OwnsCurrent && playback.IsPlaying;
    public bool HasCurrent => current is not null;
    public bool CanStep => !stopping && HasCurrent && queueSaved;
    public bool CanPlay => !stopping && HasCurrent && queueSaved;
    public bool CanTogglePlayback => !stopping && HasCurrent && queueSaved;
    public bool CanFavoriteCurrent => !stopping && HasCurrent && !favoriteSubmitting;
    public bool CanAddCurrentToPlaylist => !stopping && HasCurrent && !addToPlaylistSubmitting;
    public string PlaybackPresentationKey => (current?.EntryId ?? "") + ":" + playbackEpoch;
    public bool CanSeek => CanPlay && OwnsCurrent && playback.HasMedia;
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
    public double PlaybackCacheFraction => OwnsCurrent && musicRecovery is { } state
        ? state.Cached ? 1 : state.Media?.Bytes?.BufferedFraction ?? 0 : 0;

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
    internal void CancelOnline() => onlineCancellation?.Cancel();
    internal void IncrementSelectionSourcesRevision() => sourceService.IncrementSelectionSourcesRevision();
    internal void AdvanceQueueEpoch() => queueEpoch++;
    internal void AdvanceQueueEpochAndRead() { queueEpoch++; queueRead++; }
    internal void MarkQueueUnsaved() { queueSaved = false; ctx.RefreshBindings(); }
    internal void RefreshPresentationAvailability() => playbackPresentation.RefreshAvailability();
    internal void RefreshPresentation() => playbackPresentation.Refresh();
    internal void AdvancePlaybackEpochForShutdown() { playbackEpoch++; Changed(nameof(PlaybackPresentationKey)); }

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
        SyncChorus();
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

    private CancellationTokenSource BeginOnline(CancellationToken token)
    {
        onlineCancellation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        onlineCancellation = source; settings.SourceBindings(); return source;
    }

    private void EndOnline(CancellationTokenSource source)
    {
        if (ReferenceEquals(onlineCancellation, source)) onlineCancellation = null;
        source.Dispose(); settings.SourceBindings();
    }

    private async Task<ResolvedMusic> ResolveOnline(MusicRecovery state)
    {
        await settings.ReadOnlineSettings(state.Token);
        state.Session = await sourceService.BeginSessionAsync(state.Song, state.Token);
        return await state.Session.AutomaticAsync(state.Token);
    }

    internal void SubscribePlayback()
    {
        if (playbackSubscribed) return;
        if (current is not null) current.PropertyChanged += CurrentSongChanged;
        application.ChorusInvalidated += ChorusAccountChanged;
        application.AccountDetailsInvalidated += OfficialAccountChanged;
        playback.PropertyChanged += PlaybackChanged;
        playback.Started += PlaybackStarted; playback.Ended += PlaybackEnded; playback.Failed += PlaybackFailed;
        playbackSubscribed = true;
    }

    internal void UnsubscribePlayback()
    {
        if (!playbackSubscribed) return;
        if (current is not null) current.PropertyChanged -= CurrentSongChanged;
        application.ChorusInvalidated -= ChorusAccountChanged;
        application.AccountDetailsInvalidated -= OfficialAccountChanged;
        ClearChorus();
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
                // A read failure cannot invent a successful favorite. Mutations
                // reread under the command lane and report their actual errors.
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
                await libraryPort.RefreshAfterEditAsync(origin);
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
        if (song is null || !CanAddCurrentToPlaylist || !libraryPort.Playlists.Any(p => p.Id == id)) return Task.CompletedTask;
        addToPlaylistSubmitting = true;
        Changed(nameof(CanAddCurrentToPlaylist));
        return RunAsync(async () =>
        {
            try
            {
                var detail = await application.GetCollectionDetailAsync(id, 0, 1, lifetime.Token);
                await application.AddSongsToCollectionAsync(id,
                    detail.Collection.Revision, [song.Record], lifetime.Token);
                await libraryPort.RefreshAfterEditAsync(origin);
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
        catalogPort.InvalidateCatalogAction();
        if (!stopping && current is not null && playback.OwnerId == Id && playback.Identity == current.EntryId &&
            (playback.HasMedia || musicRecovery?.Recovering == true))
        {
            // Admission is synchronous: a queued/awaiting resolver cannot delay Pause.
            if (!playback.PauseRequested) queuePort.ObserveExplicitRecommendationPause();
            var command = playback.PauseRequested ? ResumePlaybackAsync() : playback.PauseAsync();
            return RunPlaybackActionAsync(_ => command, supersede: false);
        }
        if (CurrentPlaybackExcluded) return ResumePlaybackAsync();
        onlineCancellation?.Cancel();
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

    private bool CurrentPlaybackExcluded =>
        musicRecovery is { Session: { } session, Media.Target: { } target } && session.Excluded(target);

    /// <summary>Every resume entry, including system Play, keeps durable wrong-source exclusions.</summary>
    public Task ResumePlaybackAsync()
    {
        if (stopping || !OwnsCurrent) return Task.CompletedTask;
        if (CurrentPlaybackExcluded)
            return Notify("当前音源已排除，请切换音源或在错源管理中恢复");
        // Keep admission synchronous: a Pause during an awaiting operation must still win.
        return playback.ResumeAsync();
    }

    public Task StepAsync(bool next, string reason = "manual")
    {
        catalogPort.InvalidateCatalogAction();
        TrackMusicRecovery(RetireMusicRecovery());
        onlineCancellation?.Cancel();
        return RunPlaybackActionAsync(generation => StepCore(next, reason, () => generation == ctx.PlaybackActionGeneration), supersede: true);
    }

    public Task StepFromPresentationAsync(bool next, string presentationKey)
    {
        if (stopping || string.IsNullOrEmpty(presentationKey) || presentationKey != PlaybackPresentationKey)
            return Task.CompletedTask;
        catalogPort.InvalidateCatalogAction();
        TrackMusicRecovery(RetireMusicRecovery());
        onlineCancellation?.Cancel();
        return RunPlaybackActionAsync(async generation =>
        {
            if (presentationKey != PlaybackPresentationKey || current is null || !queueSaved) return;
            await StepCore(next, "manual", () => generation == ctx.PlaybackActionGeneration);
        }, supersede: true);
    }

    private async Task StepCore(bool next, string reason, Func<bool>? admission = null)
    {
        if (admission is not null && !admission()) return;
        if (next && await queuePort.AdvanceRecommendationAsync(reason == "ended"))
        {
            await queuePort.RefreshRecommendationQueueAsync(lifetime.Token);
            return;
        }
        var reply = await QueueCallAsync(ct => application.StepQueueAsync(next ? 1 : -1, reason: next ? reason : "manual", ct: ct));
        if (reply.TargetId is null)
        {
            if (reason == "ended") await StopOwnedPlayback();
            return;
        }
        await ReadQueue(lifetime.Token);
        if (admission is not null && !admission()) return;
        await StartCurrent(lifetime.Token, reason == "ended", admission: admission);
        queuePort.InvalidateQueueSurface();
    }

    public Task ChangeModeAsync(int value)
    {
        catalogPort.InvalidateCatalogAction();
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
                queuePort.NotifyModeChanged();
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
        SuspendFeedback();
        try { await playback.SeekAsync(seconds); }
        finally { ResumeFeedback(epoch); }
    }

    internal async Task<T> QueueCallAsync<T>(Func<CancellationToken, Task<T>> action, bool clearsSearchRecommendation = false)
        where T : IMusicQueuePosition
    {
        queueEpoch++;
        if (clearsSearchRecommendation) queuePort.ClearSearchRecommendationStart();
        try
        {
            var result = await action(lifetime.Token);
            ObservePreloadQueueMutation(result);
            queuePort.InvalidateQueueSurface();
            return result;
        }
        catch (Exception error)
        {
            // A failed command may leave a pending candidate in the native owner. Playback
            // stays gated until a later queue snapshot explicitly reports persisted state.
            // Pure request validation rejections occur before mutating storage and never leave a pending write.
            if (error is not MusicOperationException { Code: "INVALID_REQUEST" })
            {
                queueSaved = false; ctx.RefreshBindings();
            }
            queuePort.InvalidateQueueSurface();
            throw;
        }
    }

    internal async Task ReadQueue(CancellationToken cancellationToken)
    {
        if (stopping) return;
        var epoch = queueEpoch; var serial = ++queueRead;
        var result = await application.GetQueueAsync(0, 1, cancellationToken);
        ObservePreloadQueueSnapshot(result);
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
        // Background recommendation append/read must not replace the same live
        // SongRow reference while an unchanged occurrence is resolving media.
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
        currentFeedbackContext = state.FeedbackContext; UpdateFeedbackSelection();
        if (SourceChoices.Count > 0) UpdateSourceChoices();
        lyrics.UpdateLyricSources(); queuePort.RefreshQueueSurfaceCurrent(); ctx.RefreshBindings();
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
        await RetireMusicRecovery();
        // Retiring an uncooperative resolver can yield while a newer selection
        // opens media. That newer input and its handles do not belong to this stop.
        if (epoch != playbackEpoch || openedEntry != entry || admission is not null && !admission() ||
            playback.OwnerId == Id && playback.Identity != outputIdentity && playback.Identity != entry)
            return false;
        FinishFeedback("ui");
        ClearChorus();
        onlineCancellation?.Cancel();
        playbackEpoch++; openedEntry = null; recorded = false; queuePort.ClearSearchRecommendationStart();
        Changed(nameof(PlaybackPresentationKey));
        bool hadInstallation = playbackInstallation is not null;
        playbackInstallation = null;
        lyrics.ClearForPlaybackStop();
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
        // A newly selected unavailable file must not leave the previous audio
        // running under the new song's metadata or seek controls.
        bool Admitted() => !stopping && !disposed && selected == current && (admission is null || admission());
        if (!await StopOwnedPlayback(Admitted) || !Admitted()) throw new OperationCanceledException();
        SelectFeedback(selected, automatic, recommendation);
        var epoch = playbackEpoch;
        ownsEpoch?.Invoke(epoch);
        if (selected.IsOnline)
        {
            await OpenOnlineCurrent(selected, epoch, cancellationToken, admission);
            await RestorePlaybackRateAsync(selected, epoch, cancellationToken);
            if (lyrics.NeedsLyrics && epoch == playbackEpoch) _ = lyrics.RestoreLyricsAsync();
            return;
        }
        using var request = BeginOnline(cancellationToken);
        try
        {
            var media = new ResolvedMusic(await files.ResolveAsync(selected.Record.LocalFileId ?? "", request.Token), new Dictionary<string, string>());
            request.Token.ThrowIfCancellationRequested();
            if (stopping || epoch != playbackEpoch || selected != current || admission is not null && !admission())
                throw new OperationCanceledException();
            openedEntry = selected.EntryId; recorded = false;
            await playback.OpenAsync(Id, selected.EntryId, media.Uri!, selected.Title, media.Headers, request.Token);
            await RestorePlaybackRateAsync(selected, epoch, request.Token);
            if (lyrics.NeedsLyrics && epoch == playbackEpoch) _ = lyrics.RestoreLyricsAsync();
        }
        finally { EndOnline(request); }
    }

    private void PlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (stopping) return;
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName is nameof(NativeAudioPlayback.DurationSeconds) or nameof(NativeAudioPlayback.HasMedia))
            CheckMediaDuration();
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName is nameof(NativeAudioPlayback.PositionSeconds) or nameof(NativeAudioPlayback.DurationSeconds) or
            nameof(NativeAudioPlayback.IsPlaying) or nameof(NativeAudioPlayback.HasMedia) or
            nameof(NativeAudioPlayback.OwnerId) or nameof(NativeAudioPlayback.Identity))
            SampleFeedback();
        if (!OwnsCurrent || !playback.HasMedia) playbackRateReceipt = null;
        if ((!OwnsCurrent || !playback.HasMedia) && musicRecovery is not { Opening: true } ||
            args.PropertyName == nameof(NativeAudioPlayback.OwnerId) && playback.OwnerId is not null && playback.OwnerId != Id)
            queuePort.ClearSearchRecommendationStart();
        if (args.PropertyName == nameof(NativeAudioPlayback.OwnerId) && playback.OwnerId != Id &&
            musicRecovery is { Opening: false })
            TrackMusicRecovery(RetireMusicRecovery());
        ObserveMusicPreload();
        if (args.PropertyName == nameof(NativeAudioPlayback.PositionSeconds))
            playbackPresentation.RefreshPosition();
        else
        {
            if (args.PropertyName != nameof(NativeAudioPlayback.Volume))
            {
                queuePort.ObserveRecommendationPlayback();
                SyncChorus();
            }
            playbackPresentation.Refresh();
            lyrics.DesktopLyricBindings();
            queuePort.NotifyRecommendationPlaybackChanged();
        }
    }

    private void PlaybackStarted(object? sender, EventArgs args)
    {
        if (stopping || playback.OwnerId != Id || playback.Identity != openedEntry || openedEntry is null) return;
        var state = musicRecovery;
        if (state is not null)
        {
            if (!CurrentRecovery(state)) return;
            if (state.Pending is { } pending)
            {
                if (CurrentPendingMedia(state, pending)) pending.SourceStarted = true;
                return;
            }
        }
        AcceptPlaybackStarted(state);
    }

    private void AcceptPlaybackStarted(MusicRecovery? state)
    {
        if (state is not null && !CurrentRecovery(state)) return;
        CheckMediaDuration();
        if (state?.PendingFailure == "RECORDING_MISMATCH") return;
        catalogPort.CommitCatalogPlayback();
        if (recorded)
        {
            if (state is { ConfirmSourceOnStart: true })
            {
                state.ConfirmSourceOnStart = false;
                ConfirmOfficialPlayback();
                ConfirmPlaybackFileCache();
                StartFeedback(); SampleFeedback();
            }
            return;
        }
        if (state is not null)
            state.ConfirmSourceOnStart = false;
        var entry = openedEntry; var epoch = playbackEpoch; var stamp = ctx.StatusEpoch;
        recorded = true; playbackEvent++;
        ConfirmOfficialPlayback();
        ConfirmPlaybackFileCache();
        StartFeedback(observedStart: true);
        queuePort.ObserveRecommendationPlayback();
        queuePort.StartPendingSearchRecommendationPreview();
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
        TrackMusicRecovery(RetireMusicRecovery());
        FinishFeedback("end");
        var epoch = endedEpoch = playbackEpoch; var entry = openedEntry;
        _ = RunPlaybackActionAsync(async _ =>
        {
            // Preserve one terminal event while busy, never advance a replacement session.
            if (!stopping && epoch == playbackEpoch && current?.EntryId == entry && playback.OwnerId == Id && playback.Identity == entry)
                await StepCore(true, "ended");
        }, supersede: false);
    }

    private void PlaybackFailed(object? sender, EventArgs args)
    {
        if (playback.OwnerId == Id && playback.Identity == openedEntry && !recorded)
            queuePort.ClearSearchRecommendationStart();
        if (TryMusicRecovery()) return;
        if (musicRecovery is { Opening: false })
            TrackMusicRecovery(RetireMusicRecovery());
        if (!stopping && playback.OwnerId == Id && playback.Identity == openedEntry)
        { FinishFeedback("error"); ctx.SetStatus("播放失败"); }
    }

    internal void AdmitSourceExclusion(string songId, string provider, string platform)
    {
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            session.Exclude(provider, platform);
            if (state.Pending?.Media.Target is { } pendingTarget && session.Excluded(pendingTarget))
                CancelPendingMedia(state, "SOURCE_EXCLUDED");
            if (state.Media?.Target is { } activeTarget && session.Excluded(activeTarget))
            {
                state.Switching?.Cancel();
                state.Expiry?.Cancel();
                InvalidateFeedback();
                if (OwnsCurrent) TrackMusicRecovery(playback.PauseAsync());
            }
            if (SourceChoices.Count > 0) UpdateSourceChoices();
        }
    }

    internal void OnSourceExclusionAdded(string songId, string provider, string platform)
    {
        sourceService.IncrementExclusionsRevision();
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            session.Exclude(provider, platform);
            if (SourceChoices.Count > 0) UpdateSourceChoices();
        }
        sourceService.ForgetRecording(songId);
    }

    internal async Task OnSourceExclusionRemovedAsync(string songId, CancellationToken token)
    {
        sourceService.IncrementExclusionsRevision();
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            await session.LoadExclusionsAsync(token);
            if (SourceChoices.Count > 0) UpdateSourceChoices();
        }
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, MusicSongRecord?> read, long publication)
    {
        if (current is { } song && ids.Contains(song.Id))
            song.ApplyRepository(read(song.Id), publication);
    }
}
