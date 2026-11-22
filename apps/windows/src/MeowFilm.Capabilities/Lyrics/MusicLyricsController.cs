using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

/// <summary>Standalone owner for lyrics loading, local import/remove, offset, preview, and desktop lyrics window.</summary>
public sealed partial class MusicLyricsController : MusicDomainController
{
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
    private string lyricStatus = "";
    private bool playerDetailOpen;

    internal MusicLyricsController(MusicCapability host) : base(host)
    {
        lyricRows.CollectionChanged += OnLyricRowsChanged;
    }

    public ObservableCollection<MusicLyricRow> Lyrics => lyricRows;
    public ObservableCollection<MusicLyricSourceRow> LyricSources { get; } = [];
    public bool LyricsEnabled { get; set; } = true;
    public bool IsLyrics => playerDetailOpen;
    public string LyricStatus => lyricStatus;
    public bool CanCancelLyrics => lyricCancellation is not null;
    public bool CanImportLocalLyrics => CanEdit && host.Playback.CurrentSong is not null;
    public bool CanRemoveLocalLyrics => CanImportLocalLyrics && hasLocalLyrics;
    public bool HasLyricPreview => lyricPreview is not null;
    public MusicLyricRow? FirstLyric => Lyrics.Count > 0 ? Lyrics[0] : null;
    public MusicLyricRow? LastLyric => Lyrics.Count > 0 ? Lyrics[^1] : null;
    public bool CanConfirmLyricPreview => HasLyricPreview && host.Playback.CanSeek;
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

    internal void PublishOnlineSettings(JsonElement value)
    {
        LyricsEnabled = value.GetProperty("lyrics").GetBoolean();
        Changed(nameof(LyricsEnabled));
        LyricStatusBindings();
    }

    internal void ClearForPlaybackStop()
    {
        RetireLyrics();
        Lyrics.Clear();
        lyricStatus = "";
        LyricStatusBindings();
    }

    internal void LyricStatusBindings()
    {
        Changed(nameof(LyricStatus));
        LyricTimingBindings();
        DesktopLyricBindings();
        host.Settings.SourceBindings();
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

    internal void UpdateLyricSources()
    {
        var current = host.Playback.CurrentSong;
        // UI editions are metadata choices for the playing song, never the row
        // highlighted elsewhere in the library or the chosen delivery platform.
        var signature = current is null ? null : current.Id + "\0" + host.Playback.PlaybackSong(current).GetRawText();
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
        if (current is not null && host.Playback.PlaybackSong(current).TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            foreach (var source in sources.EnumerateArray())
            {
                string platform = MusicDto.Text(source, "source"), key = MusicDto.Text(source, "sourceKey");
                var data = source.GetProperty("musicInfo");
                var id = platform == "kg" && data.TryGetProperty("hash", out var hash) ? hash.ToString() :
                    SongJson.InfoId(data) is { Length: > 0 } native ? native : key;
                LyricSources.Add(new(key, platform + " · " + id));
            }
        if (previousChoice is not null)
            selectedLyricSource = LyricSources.FirstOrDefault(row => row.SourceKey == previousChoice) ?? selectedLyricSource;
        Changed(nameof(SelectedLyricSource));
    }

    internal void RetireLyrics()
    {
        lyricEpoch++;
        lyricLoadingIdentity = null;
        lyricPublishedIdentity = null;
        lyricCancellation?.Cancel();
        hasLocalLyrics = false; Changed(nameof(CanRemoveLocalLyrics));
        CancelLyricPreview();
        Lyrics.Clear(); lyricStatus = ""; LyricStatusBindings();
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
        var selected = host.Playback.CurrentSong;
        if (!CanImportLocalLyrics || selected is null) return Task.CompletedTask;
        var epoch = lyricEpoch;
        return RunAsync(async () =>
        {
            if (epoch != lyricEpoch || selected != host.Playback.CurrentSong || stopping) return;
            if (await dialogs.SelectLyricFileAsync(PlayerView) is not { } path) return;
            if (epoch != lyricEpoch || selected != host.Playback.CurrentSong || stopping) return;
            RetireLyrics(); epoch = lyricEpoch;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lyricCancellation = request; Changed(nameof(CanCancelLyrics));
            DesktopLyricBindings();
            try
            {
                var snapshot = await SelectedLyricSnapshot.ReadAsync(path, request.Token);
                if (epoch != lyricEpoch || selected != host.Playback.CurrentSong || stopping) return;
                if (!await ConfirmAsync("将所选歌词用于“" + selected.Title + "”？", "导入歌词", request.Token)) return;
                request.Token.ThrowIfCancellationRequested();
                if (epoch != lyricEpoch || selected != host.Playback.CurrentSong || stopping) return;
                // Once the sole native writer admits CAS, cancellation must not
                // invent a rollback or lose a known receipt.
                await Call("music.lyrics.local.save", new { song = selected.Raw, text = snapshot.Text });
                if (epoch != lyricEpoch || selected != host.Playback.CurrentSong || stopping) return;
                ResetLyricOffset();
            }
            finally
            {
                if (ReferenceEquals(lyricCancellation, request)) lyricCancellation = null;
                Changed(nameof(CanCancelLyrics));
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
        var selected = host.Playback.CurrentSong;
        if (!CanRemoveLocalLyrics || selected is null) return Task.CompletedTask;
        var epoch = lyricEpoch;
        return RunAsync(async () =>
        {
            if (epoch != lyricEpoch || selected != host.Playback.CurrentSong || stopping ||
                !await ConfirmAsync("移除“" + selected.Title + "”的本地歌词？", "移除本地歌词", lifetime.Token)) return;
            RetireLyrics();
            await Call("music.lyrics.local.remove", new { song = selected.Raw });
            if (selected == host.Playback.CurrentSong) ResetLyricOffset();
            if (selected == host.Playback.CurrentSong && NeedsLyrics) await RestoreLyricsAsync();
        });
    }

    private Task LoadLyricsAsync(bool restore)
    {
        if (stopping) return Task.CompletedTask;
        var selected = host.Playback.CurrentSong;
        var sourceKey = selectedLyricSource?.SourceKey;
        if (selected is null)
        {
            RetireLyrics();
            lyricStatus = "暂无歌词"; LyricStatusBindings();
            return Task.CompletedTask;
        }
        var rawSong = selected.Raw.Clone();
        var playbackSong = host.Playback.PlaybackSong(selected).Clone();
        var identity = new LyricRequestIdentity(selected.EntryId ?? "", rawSong.GetRawText(),
            playbackSong.GetRawText(), sourceKey ?? "", lyricSourcesRevision, LyricsEnabled);
        if (restore)
        {
            if (identity == lyricLoadingIdentity && !lyricLoadTail.IsCompleted)
                return lyricLoadTail;
            if (identity == lyricPublishedIdentity && lyricLoadTail.IsCompleted)
                return Task.CompletedTask;
        }

        var previous = lyricLoadTail;
        RetireLyrics();
        var epoch = lyricEpoch;
        lyricLoadingIdentity = identity;
        lyricStatus = "加载中…"; LyricStatusBindings();
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
            try
            {
                var parameters = new Dictionary<string, object> { ["song"] = playbackSong };
                if (!string.IsNullOrEmpty(sourceKey)) parameters["sourceKey"] = sourceKey;
                var local = await CallSettled("music.lyrics.local.get", new { song = rawSong }, request.Token);
                if (!CurrentRequest()) return;
                hasLocalLyrics = local.GetProperty("available").GetBoolean();
                Changed(nameof(CanRemoveLocalLyrics));
                bool useLocal = string.IsNullOrEmpty(sourceKey) && MusicDto.Text(local, "state") is "local" or "disabled";
                JsonElement reply = useLocal ? local : default;
                if (!useLocal && restore)
                    reply = await CallSettled("music.lyrics.display.get", parameters, request.Token);
                if (!CurrentRequest()) return;
                bool restored = useLocal || restore && MusicDto.Text(reply, "state") is "restored" or "disabled";
                if (!restored)
                {
                    // A saved display is local text, not permission to re-use a
                    // media URL, account or script. Refresh uses current grants.
                    reply = await CallSettled("music.lyrics.lookup", parameters, request.Token);
                }
                if (!CurrentRequest()) return;
                var rows = reply.GetProperty("lines").EnumerateArray().Select(line =>
                {
                    var words = line.TryGetProperty("words", out var wordRows) ? wordRows.EnumerateArray()
                        .Select(word => new MusicLyricWord(MusicDto.Text(word, "text"),
                            word.GetProperty("time").GetDouble(), word.GetProperty("duration").GetDouble())).ToArray() : [];
                    return new MusicLyricRow(line.GetProperty("time").GetDouble(), MusicDto.Text(line, "text"),
                        line.TryGetProperty("translation", out var translated) ? translated.GetString() ?? "" : "",
                        line.TryGetProperty("romanization", out var romanized) ? romanized.GetString() ?? "" : "",
                        words);
                }).ToArray();
                if (!CurrentRequest()) return;
                // Empty/disabled results have no resolved source; they do not select a new one.
                if (rows.Length > 0)
                {
                    var resolvedSource = useLocal ? "local" : MusicDto.Text(reply, "sourceKey");
                    if (lyricResolvedSource is not null && lyricResolvedSource != resolvedSource)
                        ResetLyricOffset();
                    lyricResolvedSource = resolvedSource;
                }
                // Publish one complete snapshot, not thousands of synchronous row
                // and edge-binding updates during the player's entrance.
                lyricRows.Publish(rows);
                lyricStatus = rows.Length == 0 ? "暂无歌词" : "";
                LyricStatusBindings();
                if (!restored && Lyrics.Count > 0 && MusicDto.Text(reply, "state") != "local")
                {
                    try
                    {
                        await CallSettled("music.lyrics.display.save", new { song = rawSong,
                            sourceKey = MusicDto.Text(reply, "sourceKey"), lines = reply.GetProperty("lines") }, request.Token);
                    }
                    catch (Exception error)
                    {
                        // The lyric result is already published. An optional
                        // display snapshot cannot change its successful status.
                        System.Diagnostics.Trace.TraceWarning("LYRIC_DISPLAY_CACHE_UNAVAILABLE: {0}", error.GetType().Name);
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
                if (ReferenceEquals(lyricCancellation, request)) lyricCancellation = null;
                if (CurrentRequest())
                {
                    lyricLoadingIdentity = null;
                    lyricPublishedIdentity = identity;
                }
                Changed(nameof(CanCancelLyrics)); LyricStatusBindings();
            }
            bool CurrentRequest()
            {
                if (stopping || epoch != lyricEpoch || selected != host.Playback.CurrentSong) return false;
                var currentPlayback = host.Playback.PlaybackSong(selected);
                return identity == new LyricRequestIdentity(selected.EntryId ?? "",
                    selected.Raw.GetRawText(), currentPlayback.GetRawText(),
                    selectedLyricSource?.SourceKey ?? "", lyricSourcesRevision, LyricsEnabled);
            }
        }
    }

    public Task SeekLyricAsync(MusicLyricRow row)
    {
        if (!Lyrics.Contains(row) || !host.Playback.CanSeek) return Task.CompletedTask;
        // Normal seek retains playback intent. Confirming a lyric position has
        // the original distinct behavior: resume if it was paused.
        var epoch = host.Playback.PlaybackEpoch;
        return RunPlaybackActionAsync(async _ =>
        {
            if (epoch != host.Playback.PlaybackEpoch || !Lyrics.Contains(row) ||
                !host.Playback.OwnsCurrent || !playback.HasMedia || !host.Playback.QueueSaved) return;
            await playback.SeekAsync(LyricAudioPosition(row));
            if (epoch == host.Playback.PlaybackEpoch && playback.OwnerId == Id && playback.HasMedia && !playback.IsPlaying)
                await host.Playback.ResumePlaybackAsync();
        }, supersede: false);
    }
}
