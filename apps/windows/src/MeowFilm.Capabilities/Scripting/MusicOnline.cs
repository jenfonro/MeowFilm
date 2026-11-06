using System.Collections.ObjectModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private CancellationTokenSource? onlineCancellation;
    private string? playbackInstallation;
    private long onlineGeneration;
    private string lyricStatus = "";
    private MusicRecordingRow? selectedRecording;
    private readonly Dictionary<string, string> recordingChoices = [];
    private readonly Dictionary<string, string> recordingPlatforms = [];
    public ObservableCollection<MusicLyricRow> Lyrics { get; } = [];
    public ObservableCollection<MusicRecordingRow> RecordingCandidates { get; } = [];
    public bool HasRecordingCandidates => RecordingCandidates.Count > 0;
    public MusicRecordingRow? SelectedRecording
    {
        get => selectedRecording;
        set
        {
            if (value is not null && !RecordingCandidates.Contains(value)) return;
            selectedRecording = value;
            if (value is not null)
            {
                RememberRecording(value);
            }
            Changed();
        }
    }
    private void UpdateRecordingCandidates()
    {
        selectedRecording = null;
        var subject = selectedSong ?? current;
        RecordingCandidates.Clear();
        if (subject is not null && PlaybackSong(subject).TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                var platform = MusicDto.Text(source, "source");
                var key = MusicDto.Text(source, "sourceKey");
                var native = SongJson.InfoId(source.GetProperty("musicInfo")) is { Length: > 0 } id ? id : key;
                var row = new MusicRecordingRow(subject.Id, key, platform, platform + " · " + native);
                RecordingCandidates.Add(row);
                if (recordingPlatforms.GetValueOrDefault(subject.Id) == platform &&
                    recordingChoices.GetValueOrDefault(subject.Id + "\0" + platform) == key) selectedRecording = row;
            }
        }
        Changed(nameof(SelectedRecording)); Changed(nameof(HasRecordingCandidates));
        if (SourceChoices.Count > 0) UpdateSourceChoices();
    }
    public bool LyricsEnabled { get; set; } = true;
    public bool IsLyrics => playerDetailOpen;
    public string LyricStatus => lyricStatus;
    public int ActiveLyricIndex
    {
        get
        {
            var position = PositionSeconds;
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
    private void OnlineBindings()
    {
        Changed(nameof(IsLyrics)); Changed(nameof(LyricStatus));
        LyricTimingBindings();
        SourceBindings();
    }
    private CancellationTokenSource BeginOnline(CancellationToken token)
    {
        onlineCancellation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        onlineCancellation = source; onlineGeneration++; OnlineBindings(); return source;
    }
    private void EndOnline(CancellationTokenSource source)
    {
        if (ReferenceEquals(onlineCancellation, source)) onlineCancellation = null;
        source.Dispose(); OnlineBindings();
    }
    private static JsonElement ParseObject(string json)
    {
        if (json.Length > 16384) throw new MusicOperationException("LIMIT");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new MusicOperationException("INVALID_REQUEST");
        return document.RootElement.Clone();
    }
    private async Task ReadOnlineSettings(CancellationToken token)
    {
        var reply = await QualityCall("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        AcceptQuality(reply);
        PublishOnlineSettings(reply.GetProperty("onlineSettings"));
    }
    private void PublishOnlineSettings(JsonElement value)
    {
        LyricsEnabled = value.GetProperty("lyrics").GetBoolean();
        Changed(nameof(LyricsEnabled)); OnlineBindings();
    }
    public Task PlayOnlineSongAsync(string platform, string title, string artist, string album, JsonElement musicInfo)
    {
        TrackMusicRecovery(RetireMusicRecovery());
        InvalidateCatalogAction();
        title = title.Trim(); artist = artist.Trim(); album = album.Trim();
        var locator = ParseObject(musicInfo.GetRawText());
        onlineCancellation?.Cancel();
        return RunAsync(async () =>
        {
            var response = await Call("music.online.song", new { platform, title, artist, album,
                musicInfo = locator }, lifetime.Token);
            var song = response.GetProperty("song").Clone();
            await QueueCall("music.queue.replace", new { songs = new[] { song }, index = 0 });
            await ReadQueue(lifetime.Token); await StartCurrent(lifetime.Token);
        });
    }
    public Task AddSelectionToPlaylistAsync(string id)
    {
        var song = selectedSong; var destination = Playlists.FirstOrDefault(row => row.Id == id); var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (song is null || destination is null) return;
            await Call("music.library.addSongs", new { id = destination.Id, ifRevision = destination.Revision,
                songs = new[] { song.Raw } }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }
    private async Task<ResolvedMusic> ResolveOnline(MusicRecovery state)
    {
        await ReadOnlineSettings(state.Token);
        state.Session = await BeginSourceSession(state.Song, state.Token);
        return await state.Session.AutomaticAsync(state.Token);
    }
    public Task ShowLyricsAsync() => OpenPlayerDetailAsync();
}
