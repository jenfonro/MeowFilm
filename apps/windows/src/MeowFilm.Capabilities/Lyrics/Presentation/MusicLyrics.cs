using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private sealed record LyricRequestIdentity(string EntryId, string Song, string PlaybackSong,
        string SourceKey, long SourcesRevision, bool Enabled);

    private CancellationTokenSource? lyricCancellation;
    private Task lyricLoadTail = Task.CompletedTask;
    private long lyricEpoch;
    private long lyricSourcesRevision;
    private LyricRequestIdentity? lyricLoadingIdentity, lyricPublishedIdentity;
    private MusicLyricSourceRow? selectedLyricSource;
    private string? lyricSongIdentity;
    private MusicLyricRow? lyricPreview;
    private MusicLyricRow? publishedLyric;
    private int publishedLyricIndex = -1, publishedLyricDisplayIndex = -1;
    private bool publishedCanConfirmLyric;
    private bool hasLocalLyrics;
    public ObservableCollection<MusicLyricSourceRow> LyricSources { get; } = [];
    public bool CanCancelLyrics => lyricCancellation is not null;
    public bool CanImportLocalLyrics => CanEdit && current is not null;
    public bool CanRemoveLocalLyrics => CanImportLocalLyrics && hasLocalLyrics;
    public bool HasLyricPreview => lyricPreview is not null;
    public MusicLyricRow? FirstLyric => Lyrics.Count > 0 ? Lyrics[0] : null;
    public MusicLyricRow? LastLyric => Lyrics.Count > 0 ? Lyrics[^1] : null;
    public bool CanConfirmLyricPreview => HasLyricPreview && CanSeek;
    public string LyricPreviewText => lyricPreview is { } row ? Clock(row.Time) + " · " + row.Text : "";
    public int LyricDisplayIndex => lyricPreview is null ? ActiveLyricIndex : Lyrics.IndexOf(lyricPreview);
    public IReadOnlyList<MusicLyricWord> CurrentLyricWords => ActiveLyricIndex is var index && index >= 0 && index < Lyrics.Count
        ? Lyrics[index].Words ?? [] : [];
    public string LyricTimingLabel => ActiveLyricIndex is var index && index >= 0 && index < Lyrics.Count &&
        Lyrics[index].Timing == "estimated" ? "估算逐字" : "";
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
    }
    internal void RefreshLyricPosition() => LyricTimingBindings();
    private void LyricTimingBindings()
    {
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
            Changed(nameof(CurrentLyricWords)); Changed(nameof(LyricTimingLabel));
        }
        int display = lyricPreview is null ? index : Lyrics.IndexOf(lyricPreview);
        if (publishedLyricDisplayIndex != display)
        {
            publishedLyricDisplayIndex = display;
            Changed(nameof(LyricDisplayIndex));
        }
        if (publishedCanConfirmLyric != CanConfirmLyricPreview)
        {
            publishedCanConfirmLyric = CanConfirmLyricPreview;
            Changed(nameof(CanConfirmLyricPreview));
        }
    }
    public void PreviewLyric(MusicLyricRow row)
    {
        if (stopping || !Lyrics.Contains(row)) return;
        lyricPreview = row; PreviewBindings();
    }
    public void CancelLyricPreview() { lyricPreview = null; PreviewBindings(); }
    private void PreviewBindings()
    {
        Changed(nameof(HasLyricPreview)); Changed(nameof(LyricPreviewText));
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
            RetireLyrics(); selectedLyricSource = value; Changed();
            _ = RestoreLyricsAsync();
        }
    }
    private void UpdateLyricSources()
    {
        // UI editions are metadata choices for the playing song, never the row
        // highlighted elsewhere in the library or the chosen delivery platform.
        var signature = current is null ? null : current.Id + "\0" + PlaybackSong(current).GetRawText();
        if (signature == lyricSongIdentity) return;
        string? previousChoice = current is not null &&
            lyricSongIdentity?.StartsWith(current.Id + "\0", StringComparison.Ordinal) == true
                ? selectedLyricSource?.SourceKey : null;
        lyricSongIdentity = signature; RetireLyrics();
        LyricSources.Clear(); selectedLyricSource = new("", "自动");
        LyricSources.Add(selectedLyricSource);
        if (current is not null && PlaybackSong(current).TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
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
    private void RetireLyrics()
    {
        lyricEpoch++;
        lyricLoadingIdentity = null;
        lyricPublishedIdentity = null;
        lyricCancellation?.Cancel();
        hasLocalLyrics = false; Changed(nameof(CanRemoveLocalLyrics));
        CancelLyricPreview();
        Lyrics.Clear(); lyricStatus = ""; OnlineBindings(); LyricTimingBindings();
    }
    private void NotifyMusicSourcesChanged()
    {
        Interlocked.Increment(ref selectionSourcesRevision);
        lyricSourcesRevision = checked(lyricSourcesRevision + 1);
        RetireLyrics();
        if (IsLyrics && !stopping) _ = RestoreLyricsAsync();
    }
    public void CancelLyrics()
    {
        if (lyricCancellation is null &&
            (lyricLoadingIdentity is null || lyricLoadTail.IsCompleted)) return;
        lyricEpoch++;
        lyricLoadingIdentity = null;
        lyricPublishedIdentity = null;
        lyricCancellation?.Cancel();
        lyricStatus = ""; OnlineBindings();
    }
    public Task RefreshLyricsAsync() => LoadLyricsAsync(restore: false);
    public Task RestoreLyricsAsync() => LoadLyricsAsync(restore: true);
    public Task ImportLocalLyricsAsync()
    {
        if (!CanImportLocalLyrics || current is not { } selected) return Task.CompletedTask;
        var epoch = lyricEpoch;
        return RunAsync(async () =>
        {
            if (epoch != lyricEpoch || selected != current || stopping) return;
            if (dialogs.SelectLyricFile() is not { } path) return;
            RetireLyrics(); epoch = lyricEpoch;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lyricCancellation = request; Changed(nameof(CanCancelLyrics));
            try
            {
                var snapshot = await SelectedLyricSnapshot.ReadAsync(path, request.Token);
                if (epoch != lyricEpoch || selected != current || stopping) return;
                if (!dialogs.Confirm("将所选歌词用于“" + selected.Title + "”？", "导入歌词")) return;
                request.Token.ThrowIfCancellationRequested();
                if (epoch != lyricEpoch || selected != current || stopping) return;
                // Once the sole native writer admits CAS, cancellation must not
                // invent a rollback or lose a known receipt.
                await Call("music.lyrics.local.save", new { song = selected.Raw, text = snapshot.Text });
                if (epoch != lyricEpoch || selected != current || stopping) return;
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
        if (!CanRemoveLocalLyrics || current is not { } selected) return Task.CompletedTask;
        var epoch = lyricEpoch;
        return RunAsync(async () =>
        {
            if (epoch != lyricEpoch || selected != current || stopping ||
                !dialogs.Confirm("移除“" + selected.Title + "”的本地歌词？", "移除本地歌词")) return;
            RetireLyrics();
            await Call("music.lyrics.local.remove", new { song = selected.Raw });
            if (selected == current && !stopping) await RestoreLyricsAsync();
        });
    }
    private Task LoadLyricsAsync(bool restore)
    {
        if (stopping) return Task.CompletedTask;
        var selected = current;
        var sourceKey = selectedLyricSource?.SourceKey;
        if (selected is null)
        {
            RetireLyrics();
            return Task.CompletedTask;
        }
        var rawSong = selected.Raw.Clone();
        var playbackSong = PlaybackSong(selected).Clone();
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
                        words, line.TryGetProperty("timing", out var timing) ? timing.GetString() ?? "" : "");
                }).ToArray();
                if (!CurrentRequest()) return;
                foreach (var row in rows) Lyrics.Add(row);
                lyricStatus = MusicDto.Text(reply, "state") switch {
                    "none" => "暂无歌词", _ => ""
                };
                if (!restored && Lyrics.Count > 0 && MusicDto.Text(reply, "state") != "local")
                {
                    try
                    {
                        await CallSettled("music.lyrics.display.save", new { song = rawSong,
                            sourceKey = MusicDto.Text(reply, "sourceKey"), lines = reply.GetProperty("lines") }, request.Token);
                    }
                    catch (Exception error)
                    {
                        if (CurrentRequest())
                            lyricStatus = error is OperationCanceledException ? "" : "歌词未保存：" + ErrorText(error);
                    }
                }
            }
            catch (OperationCanceledException) { if (CurrentRequest()) lyricStatus = ""; }
            catch (MusicOperationException error)
            {
                if (CurrentRequest())
                    lyricStatus = error.Code switch {
                        "CAPABILITY_UNAVAILABLE" => "当前音源不提供歌词",
                        "LYRIC_IDENTITY_MISMATCH" => "歌词录音不匹配",
                        "CANCELLED" => "", _ => ErrorText(error)
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
                Changed(nameof(CanCancelLyrics)); OnlineBindings(); LyricTimingBindings();
            }
            bool CurrentRequest()
            {
                if (stopping || epoch != lyricEpoch || selected != current) return false;
                var currentPlayback = PlaybackSong(selected);
                return identity == new LyricRequestIdentity(selected.EntryId ?? "",
                    selected.Raw.GetRawText(), currentPlayback.GetRawText(),
                    selectedLyricSource?.SourceKey ?? "", lyricSourcesRevision, LyricsEnabled);
            }
        }
    }
    public Task SeekLyricAsync(MusicLyricRow row)
    {
        if (!Lyrics.Contains(row) || !CanSeek) return Task.CompletedTask;
        // Normal seek retains playback intent. Confirming a lyric position has
        // the original distinct behavior: resume if it was paused.
        var epoch = playbackEpoch;
        return RunAsync(async () =>
        {
            // RunAsync intentionally disables new UI commands while executing.
            // Recheck playback identity here, not CanEdit/CanSeek's UI gate,
            // otherwise an admitted seek would reject itself as "busy".
            if (epoch != playbackEpoch || !Lyrics.Contains(row) || !OwnsCurrent || !playback.HasMedia || !queueSaved) return;
            await playback.SeekAsync(row.Time);
            if (epoch == playbackEpoch && playback.OwnerId == Id && playback.HasMedia && !playback.IsPlaying)
                await ResumePlaybackAsync();
        });
    }
}
