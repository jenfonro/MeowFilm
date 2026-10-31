using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private CancellationTokenSource? onlineCancellation;
    private MusicSourceRow? selectedSource;
    private string? playbackInstallation, targetInstallation, targetPlatform;
    private long onlineSettingsRevision, onlineGeneration;
    private string lyricStatus = "";
    private MusicRecordingRow? selectedRecording;
    private readonly Dictionary<string, string> recordingChoices = [];
    private readonly Dictionary<string, string> recordingPlatforms = [];
    public ObservableCollection<MusicSourceRow> Sources { get; } = [];
    public ObservableCollection<MusicSourcePlatformRow> SourcePlatformRows { get; } = [];
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
                var data = source.GetProperty("source_data");
                var native = data.TryGetProperty("musicId", out var nativeId) ? nativeId.ToString() :
                    data.TryGetProperty("songmid", out var mid) ? mid.ToString() : key;
                var row = new MusicRecordingRow(subject.Id, key, platform, platform + " · " + native);
                RecordingCandidates.Add(row);
                if (recordingPlatforms.GetValueOrDefault(subject.Id) == platform &&
                    recordingChoices.GetValueOrDefault(subject.Id + "\0" + platform) == key) selectedRecording = row;
            }
        }
        Changed(nameof(SelectedRecording)); Changed(nameof(HasRecordingCandidates));
        UpdateCurrentRecordings();
    }
    public bool LyricsEnabled { get; set; } = true;
    public string SelectedPlatform { get; private set; } = "";
    public MusicSourceRow? SelectedSource
    {
        get => selectedSource;
        set
        {
            selectedSource = value;
            SelectedPlatform = value?.InstallationId == targetInstallation ? targetPlatform ?? "" : "";
            RebuildSourcePlatforms();
            Changed();
            OnlineBindings();
        }
    }
    public bool HasSource => selectedSource is not null;
    public bool CanEditSource => CanEdit && selectedSource is not null;
    public bool CanLoadSource => CanEdit && selectedSource is { Enabled: true, State: "idle" };
    public bool CanRollbackSource => CanEdit && selectedSource?.RollbackAvailable == true;
    public bool CanUseSelectedSource => CanEdit && selectedSource is { Enabled: true } &&
        selectedSource.Platforms.Length > 0;
    public bool UsesOfficialSources => targetInstallation is null;
    public bool IsLyrics => playerDetailOpen;
    public string LyricStatus => lyricStatus;
    public int ActiveLyricIndex
    {
        get
        {
            var result = -1;
            for (var i = 0; i < Lyrics.Count && Lyrics[i].Time <= PositionSeconds; i++) result = i;
            return result;
        }
    }
    public string SourceCapabilities => selectedSource?.Capabilities ?? "";
    public string[] SourcePlatforms => selectedSource?.Platforms ?? [];
    public string PlaybackTarget => targetInstallation is null ? "官方音源" :
        $"{Sources.FirstOrDefault(source => source.InstallationId == targetInstallation)?.Name ?? "JS 音源"} · {PlatformName(targetPlatform)}";
    public bool CanCancelOnline => onlineCancellation is not null;
    private void OnlineBindings()
    {
        foreach (var property in new[] { nameof(HasSource), nameof(CanEditSource), nameof(CanLoadSource),
            nameof(CanRollbackSource), nameof(CanUseSelectedSource),
            nameof(UsesOfficialSources), nameof(IsLyrics), nameof(LyricStatus),
            nameof(ActiveLyricIndex), nameof(SourceCapabilities), nameof(SourcePlatforms), nameof(SelectedPlatform),
            nameof(PlaybackTarget), nameof(CanCancelOnline) })
            Changed(property);
    }
    private static string PlatformName(string? platform) => platform switch
    {
        "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐",
        "kw" => "酷我音乐", "mg" => "咪咕音乐", "local" => "本地", _ => ""
    };
    private void RebuildSourcePlatforms()
    {
        SourcePlatformRows.Clear();
        if (selectedSource is not null)
            foreach (var platform in selectedSource.Platforms)
                SourcePlatformRows.Add(new(platform, PlatformName(platform),
                    selectedSource.InstallationId == targetInstallation && platform == targetPlatform));
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
    public async Task CancelOnlineAsync()
    {
        if (stopping) return;
        CancelMusicPreload();
        await RetireMusicRecovery();
        onlineCancellation?.Cancel(); onlineGeneration++;
        try { await Call("music.online.cancel", cancellationToken: lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { if (!stopping) { status = "取消未确认"; Changed(nameof(Status)); } }
    }
    private async Task<string> UploadScript(byte[] bytes, string hash, string basename, CancellationToken token)
    {
        if (bytes.Length < 1)
            throw new MusicOperationException("LIMIT");
        if (!StringComparer.Ordinal.Equals(hash, Convert.ToHexStringLower(SHA256.HashData(bytes))))
            throw new MusicOperationException("SCRIPT_INTEGRITY");
        var begin = await Call("music.transfer.begin", new
        {
            kind = "script",
            length = bytes.Length,
            sha256 = hash,
            basename
        }, token);
        var transfer = MusicDto.Text(begin, "token");
        var chunkBytes = MusicDto.Count(begin, "chunkBytes");
        MusicDto.Require(chunkBytes is > 0 and <= 16 * 1024);
        try
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkBytes, bytes.Length - offset);
                var reply = await Call("music.transfer.chunk", new { token = transfer, offset,
                    data = Convert.ToBase64String(bytes, offset, count) }, token);
                MusicDto.Require(MusicDto.Integer(reply, "next") == offset + count);
                offset += count;
            }
            token.ThrowIfCancellationRequested(); return transfer;
        }
        catch
        {
            try { await Call("music.transfer.cancel", new { token = transfer }); } catch { }
            throw;
        }
    }
    private static object SourcePermissions() => new
    {
        storage = Array.Empty<string>()
    };
    private static JsonElement ParseObject(string json)
    {
        if (json.Length > 16384) throw new MusicOperationException("LIMIT");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new MusicOperationException("INVALID_REQUEST");
        return document.RootElement.Clone();
    }
    public Task RefreshOnlineAsync() => RunAsync(async () =>
    {
        await ReadOnlineSettings(lifetime.Token);
        await RefreshSources(lifetime.Token);
    });
    private async Task ReadOnlineSettings(CancellationToken token)
    {
        var reply = await QualityCall("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        AcceptQuality(reply);
        PublishOnlineSettings(reply.GetProperty("onlineSettings"));
    }
    private void PublishOnlineSettings(JsonElement value)
    {
        onlineSettingsRevision = MusicDto.Integer(value, "revision");
        LyricsEnabled = value.GetProperty("lyrics").GetBoolean();
        var target = value.GetProperty("target");
        targetInstallation = target.ValueKind == JsonValueKind.Null ? null : MusicDto.Text(target, "installationId");
        targetPlatform = target.ValueKind == JsonValueKind.Null ? null : MusicDto.Text(target, "platform");
        SelectedPlatform = selectedSource?.InstallationId == targetInstallation ? targetPlatform ?? "" : "";
        RebuildSourcePlatforms();
        Changed(nameof(LyricsEnabled)); OnlineBindings();
    }
    private async Task RefreshSources(CancellationToken token)
    {
        var chosen = selectedSource?.InstallationId ?? targetInstallation;
        var reply = await Call("music.sources.list", cancellationToken: token);
        var rows = reply.GetProperty("items").EnumerateArray().Select(MusicSourceRow.Read).ToArray();
        Sources.Clear(); foreach (var row in rows) Sources.Add(row);
        SelectedSource = rows.SingleOrDefault(r => r.InstallationId == chosen);
    }
    public Task LoadSourceAsync(MusicSourceRow? source) => RunAsync(async () =>
    {
        if (source is null || !Sources.Contains(source)) return;
        await LoadSourceCore(source, lifetime.Token);
    });
    private async Task LoadSourceCore(MusicSourceRow source, CancellationToken token)
    {
        var reply = await Call("music.sources.detail", new { installationId = source.InstallationId }, token);
        if (SelectedSource?.InstallationId != source.InstallationId) return;
        var refreshed = MusicSourceRow.Read(reply.GetProperty("item"));
        var index = Sources.IndexOf(source);
        if (index < 0) return;
        Sources[index] = refreshed;
        SelectedSource = refreshed;
    }
    public Task ImportSourceAsync()
    {
        if (dialogs.SelectScriptFiles() is not { Length: > 0 } paths) return Task.CompletedTask;
        return Sources.Count + paths.Length <= 32
            ? ChangeSourceFilesAsync(paths, null)
            : RunAsync(() => throw new MusicOperationException("LIMIT"));
    }
    public Task UpdateSourceAsync()
    {
        var source = selectedSource;
        return source is not null && dialogs.SelectScriptFile() is { } path
            ? ChangeSourceFilesAsync([path], source)
            : Task.CompletedTask;
    }
    private Task ChangeSourceFilesAsync(string[] paths, MusicSourceRow? update)
    {
        return RunAsync(async () =>
        {
            string? choose = update?.InstallationId;
            using var request = BeginOnline(lifetime.Token);
            try
            {
                foreach (var path in paths)
                {
                    string? transfer = null;
                    var snapshot = await SelectedScriptSnapshot.ReadAsync(path, request.Token);
                    try
                    {
                        var inspection = MusicSourceScriptInspector.Inspect(snapshot.Bytes, snapshot.Basename, snapshot.Sha256);
                        transfer = await UploadScript(snapshot.Bytes, snapshot.Sha256, snapshot.Basename, request.Token);
                        if (update is not null)
                        {
                            var changed = await Call("music.sources.update", new { installationId = update.InstallationId,
                                token = transfer, version = inspection.Version,
                                permissions = SourcePermissions() }, request.Token);
                            choose = MusicDto.Text(changed.GetProperty("item"), "installationId");
                            await RevokeChangedMusicSource(update.InstallationId);
                        }
                        else
                        {
                            var installed = await Call("music.sources.install", new { token = transfer,
                                name = inspection.Name, version = inspection.Version,
                                permissions = SourcePermissions() }, request.Token);
                            choose = MusicDto.Text(installed.GetProperty("item"), "installationId");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(snapshot.Bytes);
                        if (transfer is not null)
                        {
                            try { await Call("music.transfer.cancel", new { token = transfer }); }
                            catch { }
                        }
                    }
                }
                await RefreshSources(lifetime.Token);
                if (choose is not null && Sources.FirstOrDefault(source => source.InstallationId == choose) is { } selected &&
                    !ReferenceEquals(selectedSource, selected))
                {
                    SelectedSource = selected;
                }
            }
            finally { EndOnline(request); }
        });
    }
    public Task SourceActionAsync(string action)
    {
        var source = selectedSource;
        if (action is not ("enable" or "disable" or "rollback" or "remove")) return Task.CompletedTask;
        onlineCancellation?.Cancel();
        return RunAsync(async () =>
        {
            if (source is null) return;
            if (action == "remove" && !dialogs.Confirm($"删除音源“{source.Name}”？", "删除音源")) return;
            if (action == "rollback" && !dialogs.Confirm($"回退音源“{source.Name}”？", "回退音源")) return;
            if (playbackInstallation == source.InstallationId && action != "enable") await StopOwnedPlayback();
            await Call("music.sources." + action, new { installationId = source.InstallationId }, lifetime.Token);
            await ReadOnlineSettings(lifetime.Token);
            await RefreshSources(lifetime.Token);
        });
    }
    public Task SaveOnlineSettingsAsync() => SaveOnlineTargetAsync(selectedSource, SelectedPlatform);
    private Task SaveOnlineTargetAsync(MusicSourceRow? source, string platform)
    {
        var lyrics = LyricsEnabled; var revision = onlineSettingsRevision;
        if (!CanEdit) return Task.CompletedTask;
        if (source is not null && (!source.Enabled || !source.Platforms.Contains(platform)))
            return RunAsync(() => throw new MusicOperationException("TARGET_REQUIRED"));
        return RunAsync(async () =>
        {
            var reply = await Call("music.settings.save", new
            {
                ifRevision = revision,
                lyrics,
                target = source is null ? null : new { installationId = source.InstallationId, platform }
            }, lifetime.Token);
            PublishOnlineSettings(reply.GetProperty("settings"));
        });
    }
    public Task UseOfficialSourcesAsync()
    {
        if (!CanEdit) return Task.CompletedTask;
        return SaveOnlineTargetAsync(null, "");
    }
    public Task UseSourcePlatformAsync(string platform)
    {
        var source = selectedSource;
        if (!CanEdit || source is null) return Task.CompletedTask;
        if (!source.Enabled || !source.Platforms.Contains(platform))
            return RunAsync(() => throw new MusicOperationException("TARGET_REQUIRED"));
        return SaveOnlineTargetAsync(source, platform);
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
    private async Task<ResolvedMusic> ResolveOnline(SongRow song, CancellationToken token)
    {
        await ReadOnlineSettings(token);
        if (targetInstallation is null)
        {
            var raw = System.Text.Json.Nodes.JsonNode.Parse(song.Raw.GetRawText())!.AsObject();
            var native = Library.NativeMusicSong.SourceData(raw);
            string platform = recordingPlatforms.GetValueOrDefault(song.Id) ?? Library.LJ.S(native["source"]);
            recordingChoices.TryGetValue(song.Id + "\0" + platform, out var chosen);
            if (chosen is null)
                return await ResolveAutomaticOfficial(song, Library.LJ.S(savedQualityPolicy!["request"]),
                    savedQualitySignature!, token);
            if (platform is not ("wy" or "tx" or "kg")) throw new MusicOperationException("CAPABILITY_UNAVAILABLE");
            return await ResolveMusicTarget(song, new(null, platform, 0, chosen,
                PolicyRequest: Library.LJ.S(savedQualityPolicy!["request"]),
                Quality: Library.LJ.S(savedQualityPolicy!["request"]),
                QualityPolicySignature: savedQualitySignature,
                RecordingSong: KnownOfficialSong(song)), token, initial: true);
        }
        if (targetPlatform is null) throw new MusicOperationException("TARGET_REQUIRED");
        var source = (await Call("music.sources.detail", new { installationId = targetInstallation }, token)).GetProperty("item");
        recordingChoices.TryGetValue(song.Id + "\0" + targetPlatform, out var sourceKey);
        return await ResolveMusicTarget(song, new(targetInstallation, targetPlatform,
            MusicDto.Integer(source, "generation"), sourceKey,
            PolicyRequest: Library.LJ.S(savedQualityPolicy!["request"]),
            Quality: Library.LJ.S(savedQualityPolicy!["request"]),
            QualityPolicySignature: savedQualitySignature), token, initial: true);
    }
    public Task ShowLyricsAsync() => OpenPlayerDetailAsync();
}
