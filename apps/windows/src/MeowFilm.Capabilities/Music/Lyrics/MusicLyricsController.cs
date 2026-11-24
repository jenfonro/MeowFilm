using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

/// <summary>Standalone owner for lyrics loading, local import/remove, offset, preview, and desktop lyrics window.</summary>
public sealed partial class MusicLyricsController : MusicController,
    IMusicLyricsCoordinator
{
    private readonly MusicSettingsController settings;
    private sealed class LyricRows : ObservableCollection<MusicLyricRow>
    {
        internal void Publish(IReadOnlyList<MusicLyricRow> rows)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private readonly LyricRows lyricRows = [];
    private sealed record LyricRequestIdentity(string EntryId, string Song, string PlaybackSong,
        string SourceKey, long SourcesRevision, bool Enabled);

    private CancellationTokenSource? lyricCancellation;
    private Task lyricLoadTail = Task.CompletedTask;
    private long lyricEpoch;
    private long lyricSourcesRevision;
    private LyricRequestIdentity? lyricLoadingIdentity, lyricPublishedIdentity;
    private MusicLyricSourceRow? selectedLyricSource;
    private string? lyricSongIdentity;
    private string? lyricResolvedSource;
    private MusicLyricRow? lyricPreview;
    private MusicLyricRow? publishedLyric;
    private int publishedLyricIndex = -1, publishedLyricDisplayIndex = -1;
    private bool publishedCanConfirmLyric;
    private bool hasLocalLyrics;
    private bool suppressRowTimingNotifications;
    private string lyricStatus = "";
    private bool playerDetailOpen;

    internal MusicLyricsController(MusicRuntimeContext ctx, MusicSettingsController settings) : base(ctx)
    {
        this.settings = settings;
        lyricRows.CollectionChanged += OnLyricRowsChanged;
    }

    private IMusicPlaybackCoordinator playbackPort = null!;

    internal void AttachPorts(IMusicPlaybackCoordinator playbackHost) => playbackPort = playbackHost;

    bool IMusicLyricsCoordinator.IsLyrics => IsLyrics;
    bool IMusicLyricsCoordinator.NeedsLyrics => NeedsLyrics;
    void IMusicLyricsCoordinator.DesktopLyricBindings() => DesktopLyricBindings();
    void IMusicLyricsCoordinator.PublishOnlineSettings(bool lyricsEnabled) => PublishOnlineSettings(lyricsEnabled);
    Task IMusicLyricsCoordinator.RestoreLyricsAsync() => RestoreLyricsAsync();
    void IMusicLyricsCoordinator.RetireLyrics() => RetireLyrics();

    public ObservableCollection<MusicLyricRow> Lyrics => lyricRows;
    public ObservableCollection<MusicLyricSourceRow> LyricSources { get; } = [];
    public bool LyricsEnabled { get; set; } = true;
    public bool IsLyrics => playerDetailOpen;
    public string LyricStatus => lyricStatus;
    public bool CanCancelLyrics => lyricCancellation is not null;
    public bool CanImportLocalLyrics => CanEdit && playbackPort.CurrentSong is not null;
    public bool CanRemoveLocalLyrics => CanImportLocalLyrics && hasLocalLyrics;
    public bool HasLyricPreview => lyricPreview is not null;
    public MusicLyricRow? FirstLyric => Lyrics.Count > 0 ? Lyrics[0] : null;
    public MusicLyricRow? LastLyric => Lyrics.Count > 0 ? Lyrics[^1] : null;
    public bool CanConfirmLyricPreview => HasLyricPreview && playbackPort.CanSeek;
    public string LyricPreviewTime => lyricPreview is { } row
        ? MusicPlaybackClock.FormatClock(LyricAudioPosition(row)).PadLeft(5, '0') : "";
    public int LyricDisplayIndex => lyricPreview is null ? ActiveLyricIndex : Lyrics.IndexOf(lyricPreview);

    public int ActiveLyricIndex
    {
        get
        {
            var position = LyricPositionSeconds;
            int low = 0, high = Lyrics.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Lyrics[middle].Time <= position) low = middle + 1;
                else high = middle;
            }
            return low - 1;
        }
    }

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        Changed(nameof(CanImportLocalLyrics));
        Changed(nameof(CanRemoveLocalLyrics));
        RefreshLyricPreviewAvailability();
    }

    internal void SetPlayerDetailOpen(bool open)
    {
        if (playerDetailOpen == open) return;
        playerDetailOpen = open;
        Changed(nameof(IsLyrics));
    }

    internal void PublishOnlineSettings(bool lyricsEnabled)
    {
        LyricsEnabled = lyricsEnabled;
        Changed(nameof(LyricsEnabled));
        LyricStatusBindings();
    }

    internal void ClearForPlaybackStop() => RetireLyrics();

    internal void LyricStatusBindings()
    {
        Changed(nameof(LyricStatus));
        LyricTimingBindings();
        DesktopLyricBindings();
        settings.SourceBindings();
    }

    private void OnLyricRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewStartingIndex == 0 || e.OldStartingIndex == 0)
            Changed(nameof(FirstLyric));
        var newTail = e.NewStartingIndex >= 0 && e.NewStartingIndex + (e.NewItems?.Count ?? 0) == Lyrics.Count;
        var removedTail = e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex == Lyrics.Count;
        var movedTail = e.Action == NotifyCollectionChangedAction.Move && e.OldStartingIndex == Lyrics.Count - 1;
        if (e.Action == NotifyCollectionChangedAction.Reset || newTail || removedTail || movedTail)
            Changed(nameof(LastLyric));
        if (suppressRowTimingNotifications) return;
        LyricTimingBindings();
        DesktopLyricBindings();
    }

    internal void RefreshLyricPosition()
    {
        var position = LyricPositionSeconds;
        if (publishedLyricPosition != position)
        {
            publishedLyricPosition = position;
            Changed(nameof(LyricPositionSeconds));
        }
        LyricTimingBindings();
    }

    private void LyricTimingBindings()
    {
        if (publishedCanAdjustLyricOffset != CanAdjustLyricOffset)
        {
            publishedCanAdjustLyricOffset = CanAdjustLyricOffset;
            Changed(nameof(CanAdjustLyricOffset));
        }
        int index = ActiveLyricIndex;
        var row = index >= 0 ? Lyrics[index] : null;
        if (publishedLyricIndex != index)
        {
            publishedLyricIndex = index;
            Changed(nameof(ActiveLyricIndex));
        }
        if (!ReferenceEquals(publishedLyric, row))
        {
            publishedLyric = row;
            DesktopLyricBindings();
        }
        int display = lyricPreview is null ? index : Lyrics.IndexOf(lyricPreview);
        if (publishedLyricDisplayIndex != display)
        {
            publishedLyricDisplayIndex = display;
            Changed(nameof(LyricDisplayIndex));
        }
        RefreshLyricPreviewAvailability();
    }

    internal void RefreshLyricPreviewAvailability()
    {
        if (publishedCanConfirmLyric != CanConfirmLyricPreview)
        {
            publishedCanConfirmLyric = CanConfirmLyricPreview;
            Changed(nameof(CanConfirmLyricPreview));
        }
    }

    public void PreviewLyric(MusicLyricRow row)
    {
        if (stopping || ReferenceEquals(lyricPreview, row) || !Lyrics.Contains(row)) return;
        lyricPreview = row; PreviewBindings();
    }

    public void CancelLyricPreview()
    {
        if (lyricPreview is null) return;
        lyricPreview = null; PreviewBindings();
    }

    private void PreviewBindings()
    {
        Changed(nameof(HasLyricPreview)); Changed(nameof(LyricPreviewTime));
        LyricTimingBindings();
    }

    public async Task ConfirmLyricPreviewAsync()
    {
        var selected = lyricPreview;
        if (selected is null || !CanConfirmLyricPreview) return;
        await SeekLyricAsync(selected);
        if (ReferenceEquals(lyricPreview, selected)) CancelLyricPreview();
    }

    public MusicLyricSourceRow? SelectedLyricSource
    {
        get => selectedLyricSource;
        set
        {
            if (value is null || !LyricSources.Contains(value) || value == selectedLyricSource) return;
            ResetLyricOffset();
            RetireLyrics(); selectedLyricSource = value; Changed();
            if (NeedsLyrics) _ = RestoreLyricsAsync();
        }
    }

    private static string StableLyricSongIdentity(SongRow song, MusicSongRecord playbackSong)
    {
        var keys = new List<string>(playbackSong.Sources.Length);
        foreach (var src in playbackSong.Sources)
            if (src.SourceKey.Length > 0) keys.Add(src.SourceKey);
        keys.Sort(StringComparer.Ordinal);
        string localFile = playbackSong.LocalFileId ?? "";
        return song.Id + "\0" + localFile + "\0" + string.Join('\u001f', keys);
    }

    internal void UpdateLyricSources()
    {
        var current = playbackPort.CurrentSong;
        // Key by stable songId + source locators (NOT full serialized song JSON) so background
        // artwork or metadata refreshes never wipe & reload live lyrics.
        var playbackSong = current?.Record;
        var signature = current is null ? null : StableLyricSongIdentity(current, playbackSong!);
        if (lyricOffsetEntry != current?.EntryId)
        {
            lyricOffsetEntry = current?.EntryId;
            ResetLyricOffset();
        }
        if (signature == lyricSongIdentity) return;
        ResetLyricOffset();
        string? previousChoice = current is not null &&
            lyricSongIdentity?.StartsWith(current.Id + "\0", StringComparison.Ordinal) == true
                ? selectedLyricSource?.SourceKey : null;
        lyricSongIdentity = signature; RetireLyrics();
        LyricSources.Clear(); selectedLyricSource = new("", "自动");
        LyricSources.Add(selectedLyricSource);
        if (playbackSong is not null)
            foreach (var source in playbackSong.Sources)
            {
                string platform = source.Platform, key = source.SourceKey;
                string id = platform == "kg" && !string.IsNullOrEmpty(source.Hash) ? source.Hash :
                    !string.IsNullOrEmpty(source.SongMid) ? source.SongMid : key;
                LyricSources.Add(new(key, platform + " · " + id));
            }
        if (previousChoice is not null)
            selectedLyricSource = LyricSources.FirstOrDefault(row => row.SourceKey == previousChoice) ?? selectedLyricSource;
        Changed(nameof(SelectedLyricSource));
    }

    internal void RetireLyrics(string status = "")
    {
        lyricEpoch++;
        lyricLoadingIdentity = null;
        lyricPublishedIdentity = null;
        lyricCancellation?.Cancel();
        if (hasLocalLyrics) { hasLocalLyrics = false; Changed(nameof(CanRemoveLocalLyrics)); }
        if (lyricPreview is not null)
        {
            lyricPreview = null;
            Changed(nameof(HasLyricPreview));
            Changed(nameof(LyricPreviewTime));
        }
        if (Lyrics.Count > 0)
        {
            suppressRowTimingNotifications = true;
            try { Lyrics.Clear(); }
            finally { suppressRowTimingNotifications = false; }
        }
        lyricStatus = status;
        LyricStatusBindings();
    }

    internal void NotifySourcesChanged()
    {
        lyricSourcesRevision = checked(lyricSourcesRevision + 1);
        ResetLyricOffset();
        RetireLyrics();
        if (NeedsLyrics) _ = RestoreLyricsAsync();
    }

    public void CancelLyrics()
    {
        if (lyricCancellation is null &&
            (lyricLoadingIdentity is null || lyricLoadTail.IsCompleted)) return;
        lyricEpoch++;
        lyricLoadingIdentity = null;
        lyricPublishedIdentity = null;
        lyricCancellation?.Cancel();
        lyricStatus = ""; LyricStatusBindings();
    }

    public Task RefreshLyricsAsync() => LoadLyricsAsync(restore: false);
    public Task RestoreLyricsAsync() => LoadLyricsAsync(restore: true);

    public Task ImportLocalLyricsAsync()
    {
        var selected = playbackPort.CurrentSong;
        if (!CanImportLocalLyrics || selected is null) return Task.CompletedTask;
        var epoch = lyricEpoch;
        return RunAsync(async () =>
        {
            if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping) return;
            if (await MusicDialogs.SelectLyricFileAsync(ctx.ModalOwner(MusicModalAnchor.Player)) is not { } path) return;
            if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping) return;
            RetireLyrics(); epoch = lyricEpoch;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lyricCancellation = request; Changed(nameof(CanCancelLyrics));
            DesktopLyricBindings();
            try
            {
                var snapshot = await SelectedLyricSnapshot.ReadAsync(path, request.Token);
                if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping) return;
                if (!await ConfirmAsync("将所选歌词用于“" + selected.Title + "”？", "导入歌词", request.Token)) return;
                request.Token.ThrowIfCancellationRequested();
                if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping) return;
                // Once the sole native writer admits CAS, cancellation must not
                // invent a rollback or lose a known receipt.
                await application.SaveLocalLyricsAsync(selected.Record, snapshot.Text);
                if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping) return;
                ResetLyricOffset();
            }
            finally
            {
                if (ReferenceEquals(lyricCancellation, request)) lyricCancellation = null;
                Changed(nameof(CanCancelLyrics));
                DesktopLyricBindings();
            }
            // Imported text is exact to the current song, independent of an
            // earlier explicit provider selection. Reopening grants no network.
            selectedLyricSource = LyricSources.FirstOrDefault();
            Changed(nameof(SelectedLyricSource));
            await RestoreLyricsAsync();
        });
    }

    public Task RemoveLocalLyricsAsync()
    {
        var selected = playbackPort.CurrentSong;
        if (!CanRemoveLocalLyrics || selected is null) return Task.CompletedTask;
        var epoch = lyricEpoch;
        return RunAsync(async () =>
        {
            if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping ||
                !await ConfirmAsync("移除“" + selected.Title + "”的本地歌词？", "移除本地歌词", lifetime.Token)) return;
            if (epoch != lyricEpoch || selected != playbackPort.CurrentSong || stopping) return;
            RetireLyrics();
            await application.RemoveLocalLyricsAsync(selected.Record);
            if (selected == playbackPort.CurrentSong) ResetLyricOffset();
            if (selected == playbackPort.CurrentSong && NeedsLyrics) await RestoreLyricsAsync();
        });
    }

    private Task LoadLyricsAsync(bool restore)
    {
        if (stopping) return Task.CompletedTask;
        var selected = playbackPort.CurrentSong;
        var sourceKey = selectedLyricSource?.SourceKey;
        if (selected is null)
        {
            RetireLyrics("暂无歌词");
            return Task.CompletedTask;
        }
        var rawSong = selected.Record;
        var playbackSong = selected.Record;
        var stableRaw = StableLyricSongIdentity(selected, rawSong);
        var stablePlayback = StableLyricSongIdentity(selected, playbackSong);
        var identity = new LyricRequestIdentity(selected.EntryId ?? "", stableRaw,
            stablePlayback, sourceKey ?? "", lyricSourcesRevision, LyricsEnabled);
        if (restore)
        {
            if (identity == lyricLoadingIdentity && !lyricLoadTail.IsCompleted)
                return lyricLoadTail;
            if (identity == lyricPublishedIdentity && lyricLoadTail.IsCompleted)
                return Task.CompletedTask;
        }

        var previous = lyricLoadTail;
        RetireLyrics("加载中…");
        var epoch = lyricEpoch;
        lyricLoadingIdentity = identity;
        var task = Read();
        lyricLoadTail = task;
        loadDrains.Add(task);
        _ = Observe();
        return task;
        async Task Observe() { try { await task; } finally { loadDrains.Remove(task); } }
        async Task Read()
        {
            await Task.Yield();
            await previous;
            if (!CurrentRequest()) return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lyricCancellation = request; Changed(nameof(CanCancelLyrics));
            DesktopLyricBindings();
            bool published = false;
            try
            {
                string? requestedSourceKey = string.IsNullOrEmpty(sourceKey) ? null : sourceKey;
                var local = await application.GetLocalLyricsAsync(rawSong, request.Token);
                if (!CurrentRequest()) return;
                hasLocalLyrics = local.Available;
                Changed(nameof(CanRemoveLocalLyrics));
                bool useLocal = requestedSourceKey is null && local.State is "local" or "disabled";
                MusicLyricLookupResult reply = useLocal ? local : new("none", []);
                if (!useLocal && restore)
                    reply = await application.GetDisplayLyricsAsync(playbackSong, requestedSourceKey, request.Token);
                if (!CurrentRequest()) return;
                bool restored = useLocal || restore && reply.State is "restored" or "disabled";
                if (!restored)
                {
                    // A saved display is local text, not permission to re-use a
                    // media URL, account or script. Refresh uses current grants.
                    reply = await application.LookupLyricsAsync(playbackSong, requestedSourceKey, request.Token);
                }
                if (!CurrentRequest()) return;
                var rows = reply.Lines.Select(line =>
                {
                    var words = line.Words.Length > 0
                        ? line.Words.Select(word => new MusicLyricWord(word.Text, word.TimeSec, word.DurationSec)).ToArray()
                        : [];
                    return new MusicLyricRow(line.TimeSec, line.Text, line.Translation ?? "", line.Romanization ?? "", words);
                }).ToArray();
                if (!CurrentRequest()) return;
                // Empty/disabled results have no resolved source; they do not select a new one.
                if (rows.Length > 0)
                {
                    string resolvedSource = useLocal ? "local"
                        : !string.IsNullOrEmpty(reply.SourceKey) ? reply.SourceKey : throw new MusicOperationException("INVALID_RESPONSE");
                    if (lyricResolvedSource is not null && lyricResolvedSource != resolvedSource)
                        ResetLyricOffset();
                    lyricResolvedSource = resolvedSource;
                }
                // Publish one complete snapshot, not thousands of synchronous row
                // and edge-binding updates during the player's entrance.
                suppressRowTimingNotifications = true;
                try { lyricRows.Publish(rows); }
                finally { suppressRowTimingNotifications = false; }
                lyricStatus = rows.Length == 0 ? "暂无歌词" : "";
                LyricStatusBindings();
                published = true;
                if (!restored && Lyrics.Count > 0 && reply.State != "local")
                {
                    try
                    {
                        string saveKey = !string.IsNullOrEmpty(reply.SourceKey) ? reply.SourceKey
                            : throw new MusicOperationException("INVALID_RESPONSE");
                        await application.SaveDisplayLyricsAsync(rawSong, saveKey, reply.Lines, request.Token);
                    }
                    catch (Exception error)
                    {
                        // The lyric result is already published. An optional
                        // display snapshot cannot change its successful status.
                        MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.MusicPlayback,
                            "lyrics", "lyric_display_cache_save_failed", new { error = error.Message });
                    }
                }
            }
            catch (OperationCanceledException) { if (CurrentRequest()) lyricStatus = "已取消"; }
            catch (MusicOperationException error)
            {
                if (CurrentRequest())
                    lyricStatus = error.Code switch {
                        "CAPABILITY_UNAVAILABLE" => "当前音源不提供歌词",
                        "CANCELLED" => "已取消", _ => ErrorText(error)
                    };
            }
            catch (Exception error) { if (CurrentRequest()) lyricStatus = ErrorText(error); }
            finally
            {
                bool clearedCancellation = ReferenceEquals(lyricCancellation, request);
                if (clearedCancellation) lyricCancellation = null;
                if (CurrentRequest())
                {
                    lyricLoadingIdentity = null;
                    lyricPublishedIdentity = identity;
                }
                Changed(nameof(CanCancelLyrics));
                if (!published) LyricStatusBindings();
                else if (clearedCancellation && Lyrics.Count == 0) DesktopLyricBindings();
            }
            bool CurrentRequest()
            {
                if (stopping || epoch != lyricEpoch || selected != playbackPort.CurrentSong) return false;
                var currentPlayback = selected.Record;
                return identity == new LyricRequestIdentity(selected.EntryId ?? "",
                    StableLyricSongIdentity(selected, selected.Record), StableLyricSongIdentity(selected, currentPlayback),
                    selectedLyricSource?.SourceKey ?? "", lyricSourcesRevision, LyricsEnabled);
            }
        }
    }

    public Task SeekLyricAsync(MusicLyricRow row)
    {
        if (!Lyrics.Contains(row) || !playbackPort.CanSeek) return Task.CompletedTask;
        // Normal seek retains playback intent. Confirming a lyric position has
        // the original distinct behavior: resume if it was paused.
        var epoch = playbackPort.PlaybackEpoch;
        return RunPlaybackActionAsync(async _ =>
        {
            if (epoch != playbackPort.PlaybackEpoch || !Lyrics.Contains(row) ||
                !playbackPort.OwnsCurrent || !playback.HasMedia || !playbackPort.QueueSaved) return;
            await playbackPort.SeekWithFeedbackGuardAsync(LyricAudioPosition(row), epoch);
            if (epoch == playbackPort.PlaybackEpoch && playback.OwnerId == Id && playback.HasMedia && !playback.IsPlaying)
                await playbackPort.ResumePlaybackAsync();
        }, supersede: false);
    }
}
