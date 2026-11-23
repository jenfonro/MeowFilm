using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for music playback transport, queue playback state, clock, artwork,
/// current favorite, source selection/recovery/preload/file-cache, chorus, rate, and feedback tracking.
/// </summary>
public sealed partial class MusicPlaybackController : MusicController
{
    internal sealed record QueueSnapshot(SongRow? Current, int Mode, bool Saved, JsonElement? FeedbackContext = null, JsonElement? Source = null);

    private readonly MusicSettingsController settings;
    private readonly MusicLyricsController lyrics;
    private readonly MusicPlaybackPresentation playbackPresentation;
    private SongRow? current;
    private JsonElement? queueSource;
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
    private readonly Dictionary<string, string> recordingChoices = [];
    private readonly Dictionary<string, string> recordingPlatforms = [];

    internal MusicPlaybackController(MusicRuntimeContext ctx, MusicSettingsController settings, MusicLyricsController lyrics) : base(ctx)
    {
        this.settings = settings;
        this.lyrics = lyrics;
        Clock = new MusicPlaybackClock();
        playbackPresentation = new(this, property => Changed(property));
    }

    internal Action OnNotifyPlaybackModeAvailability { get; set; } = null!;
    internal Func<bool, Task<bool>> OnAdvanceRecommendationAsync { get; set; } = _ => Task.FromResult(false);
    internal Action OnClearSearchRecommendationStart { get; set; } = null!;
    internal Action OnInvalidateQueueSurface { get; set; } = null!;
    internal Action OnNotifyQueueModeChanged { get; set; } = null!;
    internal Action OnNotifyRecommendationPlaybackChanged { get; set; } = null!;
    internal Action OnObserveExplicitRecommendationPause { get; set; } = null!;
    internal Action OnObserveRecommendationPlayback { get; set; } = null!;
    internal Action OnRefreshQueueSurfaceCurrent { get; set; } = null!;
    internal Func<CancellationToken, Task> OnRefreshRecommendationQueueAsync { get; set; } = null!;
    internal Action OnStartPendingSearchRecommendationPreview { get; set; } = null!;
    internal Func<string> GetCatalogLoadedPlatform { get; set; } = null!;
    internal Func<CatalogTarget?> GetCatalogLoadedTarget { get; set; } = null!;
    internal Func<string> GetCatalogQuery { get; set; } = null!;
    internal Action OnCommitCatalogPlayback { get; set; } = null!;
    internal Action OnInvalidateCatalogAction { get; set; } = null!;
    internal Func<bool> GetIsCatalogPage { get; set; } = null!;
    internal Func<ObservableCollection<PlaylistRow>> GetPlaylists { get; set; } = null!;
    internal Func<long, Task> OnRefreshLibraryAfterEditAsync { get; set; } = null!;
    internal Func<bool> GetIsSearchPage { get; set; } = null!;
    internal Func<string> GetSearchKeyword { get; set; } = null!;

    internal void NotifyLyricsPosition() => lyrics.RefreshLyricPosition();
    internal void NotifyLyricsPreviewAvailability() => lyrics.RefreshLyricPreviewAvailability();
    internal void NotifyPlaybackModeAvailability() => OnNotifyPlaybackModeAvailability();

    public MusicPlaybackClock Clock { get; }

    internal SongRow? CurrentSong => current;
    internal JsonElement? QueueSource => queueSource;
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
    internal void IncrementSelectionSourcesRevision() => Interlocked.Increment(ref selectionSourcesRevision);
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
        state.Session = await BeginSourceSession(state.Song, state.Token);
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
                var result = await Call("music.library.membership", new { id = "favorites", song = song.Raw }, lifetime.Token);
                if (!stopping && generation == favoriteRead && ReferenceEquals(song, current))
                    PublishFavorite(result.GetProperty("present").GetBoolean());
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
                var membership = await Call("music.library.membership", new { id = "favorites", song = song.Raw }, lifetime.Token);
                var revision = MusicDto.Integer(membership, "revision");
                if (membership.GetProperty("present").GetBoolean())
                    await Call("music.library.removeSong", new { id = "favorites", ifRevision = revision,
                        songId = membership.GetProperty("songId").GetString() }, lifetime.Token);
                else
                    await Call("music.library.addSongs", new { id = "favorites", ifRevision = revision, songs = new[] { song.Raw } }, lifetime.Token);
                await OnRefreshLibraryAfterEditAsync(origin);
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
        if (song is null || !CanAddCurrentToPlaylist || !GetPlaylists().Any(p => p.Id == id)) return Task.CompletedTask;
        addToPlaylistSubmitting = true;
        Changed(nameof(CanAddCurrentToPlaylist));
        return RunAsync(async () =>
        {
            try
            {
                var detail = await Call("music.library.detail", new { id, limit = 1 }, lifetime.Token);
                await Call("music.library.addSongs", new { id,
                    ifRevision = MusicDto.Integer(detail.GetProperty("collection"), "revision"),
                    songs = new[] { song.Raw } }, lifetime.Token);
                await OnRefreshLibraryAfterEditAsync(origin);
            }
            finally
            {
                addToPlaylistSubmitting = false;
                if (!disposed) Changed(nameof(CanAddCurrentToPlaylist));
            }
        });
    }

    public Task PlayOnlineSongAsync(string platform, string title, string artist, string album, JsonElement musicInfo)
    {
        TrackMusicRecovery(RetireMusicRecovery());
        OnInvalidateCatalogAction();
        title = title.Trim(); artist = artist.Trim(); album = album.Trim();
        if (musicInfo.ValueKind != JsonValueKind.Object) throw new MusicOperationException("INVALID_REQUEST");
        var locator = musicInfo.Clone();
        onlineCancellation?.Cancel();
        return RunPlaybackActionAsync(async generation =>
        {
            var response = await Call("music.online.song", new { platform, title, artist, album,
                musicInfo = locator }, lifetime.Token);
            var song = response.GetProperty("song").Clone();
            await QueueCall("music.queue.replace", new { songs = new[] { song }, index = 0 });
            await ReadQueue(lifetime.Token);
            if (generation != ctx.PlaybackActionGeneration) return;
            await StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration);
        }, supersede: true);
    }

    public Task OpenPlayerDetailAsync() => ctx.OpenPlayerDetailAsync();
    public Task OpenPlayQueueAsync() => ctx.OpenPlayQueueAsync();
    public Task ExecuteMediaShortcutAsync(MeowFilm.Desktop.MediaShortcutAction action) => ctx.ExecuteMediaShortcutAsync(action);

    public Task TogglePlaybackAsync()
    {
        OnInvalidateCatalogAction();
        if (!stopping && current is not null && playback.OwnerId == Id && playback.Identity == current.EntryId &&
            (playback.HasMedia || musicRecovery?.Recovering == true))
        {
            // Admission is synchronous: a queued/awaiting resolver cannot delay Pause.
            if (!playback.PauseRequested) OnObserveExplicitRecommendationPause();
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
        OnInvalidateCatalogAction();
        TrackMusicRecovery(RetireMusicRecovery());
        onlineCancellation?.Cancel();
        return RunPlaybackActionAsync(generation => StepCore(next, reason, () => generation == ctx.PlaybackActionGeneration), supersede: true);
    }

    public Task StepFromPresentationAsync(bool next, string presentationKey)
    {
        if (stopping || string.IsNullOrEmpty(presentationKey) || presentationKey != PlaybackPresentationKey)
            return Task.CompletedTask;
        OnInvalidateCatalogAction();
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
        if (next && await OnAdvanceRecommendationAsync(reason == "ended"))
        {
            await OnRefreshRecommendationQueueAsync(lifetime.Token);
            return;
        }
        var reply = await QueueCall(next ? "music.queue.next" : "music.queue.previous", next ? new { reason } : new { });
        var target = reply.GetProperty("targetId");
        if (target.ValueKind == JsonValueKind.Null)
        {
            if (reason == "ended") await StopOwnedPlayback();
            return;
        }
        MusicDto.Require(target.ValueKind == JsonValueKind.String);
        await ReadQueue(lifetime.Token);
        if (admission is not null && !admission()) return;
        await StartCurrent(lifetime.Token, reason == "ended", admission: admission);
        OnInvalidateQueueSurface();
    }

    public Task ChangeModeAsync(int value)
    {
        OnInvalidateCatalogAction();
        return RunPlaybackActionAsync(async _ =>
        {
            if (value is < 1 or > 3) throw new MusicOperationException("INVALID_REQUEST");
            var reply = await QueueCall("music.queue.mode", new { mode = value });
            int next = MusicDto.Mode(reply);
            if (mode != next)
            {
                mode = next;
                Changed(nameof(Mode)); Changed(nameof(ModeText));
                OnNotifyQueueModeChanged();
            }
        }, supersede: false);
    }

    public Task SeekAsync(double seconds)
    {
        var epoch = playbackEpoch;
        return RunPlaybackActionAsync(async _ =>
        {
            if (epoch != playbackEpoch || playback.OwnerId != Id || !playback.HasMedia) return;
            // Explicit discontinuity: never count a short seek as a listened interval.
            SuspendFeedback();
            try { await playback.SeekAsync(seconds); }
            finally { ResumeFeedback(epoch); }
        }, supersede: false);
    }

    internal async Task<JsonElement> QueueCall(string method, object? parameters = null)
    {
        queueEpoch++;
        if (method is "music.queue.replace" or "music.queue.playCollection" or "music.queue.clear" or "music.history.restorePlaylist" ||
            method == "music.queue.append" && (!JsonSerializer.SerializeToElement(parameters ?? new { }).TryGetProperty("next", out var next) ||
                next.ValueKind != JsonValueKind.True))
            OnClearSearchRecommendationStart();
        try
        {
            var result = await Call(method, parameters, lifetime.Token);
            ObservePreloadQueueMutation(result);
            OnInvalidateQueueSurface();
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
            OnInvalidateQueueSurface();
            throw;
        }
    }

    internal async Task ReadQueue(CancellationToken cancellationToken)
    {
        if (stopping) return;
        var epoch = queueEpoch; var serial = ++queueRead;
        var result = await Call("music.queue.get", new { offset = 0, limit = 1 }, cancellationToken);
        ObservePreloadQueueSnapshot(result);
        var state = ParseQueue(result);
        if (stopping || epoch != queueEpoch || serial != queueRead) return;
        ApplyQueue(state);
    }

    internal static QueueSnapshot ParseQueue(JsonElement reply)
    {
        var mode = MusicDto.Mode(reply);
        var persistence = MusicDto.Text(reply.GetProperty("persistence"), "state");
        MusicDto.Require(persistence is "saved" or "pending");
        var value = reply.GetProperty("current");
        SongRow? current = value.ValueKind == JsonValueKind.Null ? null
            : SongRow.Read(value.GetProperty("song"), MusicDto.Text(value, "id"));
        var id = reply.GetProperty("currentId");
        MusicDto.Require(current is null ? id.ValueKind == JsonValueKind.Null
            : id.ValueKind == JsonValueKind.String && id.GetString() == current.EntryId);
        JsonElement? feedbackContext = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("context", out var sourceContext) && sourceContext.ValueKind == JsonValueKind.Object
            ? sourceContext.Clone() : null;
        return new(current, mode, persistence == "saved", feedbackContext,
            reply.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object ? source.Clone() : null);
    }

    internal void ApplyQueue(QueueSnapshot state)
    {
        // Background recommendation append/read must not replace the same live
        // SongRow reference while an unchanged occurrence is resolving media.
        if (current is not null && state.Current is not null && current.EntryId == state.Current.EntryId &&
            current.Raw.GetRawText() == state.Current.Raw.GetRawText())
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
        lyrics.UpdateLyricSources(); OnRefreshQueueSurfaceCurrent(); ctx.RefreshBindings();
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
        playbackEpoch++; openedEntry = null; recorded = false; OnClearSearchRecommendationStart();
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
            var media = new ResolvedMusic(await files.ResolveAsync(SongJson.Text(selected.Raw, "localFileId"), request.Token), new Dictionary<string, string>());
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
            OnClearSearchRecommendationStart();
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
                OnObserveRecommendationPlayback();
                SyncChorus();
            }
            playbackPresentation.Refresh();
            lyrics.DesktopLyricBindings();
            OnNotifyRecommendationPlaybackChanged();
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
        OnCommitCatalogPlayback();
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
        OnObserveRecommendationPlayback();
        OnStartPendingSearchRecommendationPreview();
        var task = Record();
        eventWrites.Add(task); _ = Observe();
        async Task Record()
        {
            try { await Call("music.history.recordPlayed", new { entryId = entry, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch
            {
                if (!stopping && epoch == playbackEpoch && stamp == ctx.StatusEpoch)
                    ctx.SetStatus("播放记录保存失败");
            }
        }
        async Task Observe() { try { await task; } finally { eventWrites.Remove(task); } }
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
            OnClearSearchRecommendationStart();
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
        exclusionsRevision++;
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            session.Exclude(provider, platform);
            if (SourceChoices.Count > 0) UpdateSourceChoices();
        }
        ForgetRecording(songId);
    }

    internal async Task OnSourceExclusionRemovedAsync(string songId, CancellationToken token)
    {
        exclusionsRevision++;
        if (musicRecovery is { Song.Id: var id, Session: { } session } state && id == songId)
        {
            await session.LoadExclusionsAsync(token);
            if (SourceChoices.Count > 0) UpdateSourceChoices();
        }
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, JsonElement?> read, long publication)
    {
        if (current is { } song && ids.Contains(song.Id))
            song.ApplyRepository(read(song.Id), publication);
    }
}
