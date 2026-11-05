using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private CancellationTokenSource? recordingDiscovery;
    private int recordingPage;
    private string? recordingEntry;
    private bool recordingMore;
    public ObservableCollection<MusicRecordingRow> CurrentRecordings { get; } = [];
    public bool CanSelectCurrentRecording => CanEdit && musicRecovery is { Recovering: false } state &&
        CurrentRecovery(state) && state.Media?.Target is not null &&
        playback is INativePlaybackContinuation && OwnsCurrent;
    public bool HasCurrentRecordings => CurrentRecordings.Count > 0;

    private void RememberRecording(MusicRecordingRow row)
    {
        if (!recordingPlatforms.ContainsKey(row.SongId) && recordingPlatforms.Count >= 256)
        {
            string oldest = recordingPlatforms.Keys.First();
            recordingPlatforms.Remove(oldest);
            foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(oldest + "\0", StringComparison.Ordinal)).ToArray())
                recordingChoices.Remove(key);
        }
        recordingPlatforms[row.SongId] = row.Platform;
        recordingChoices[row.SongId + "\0" + row.Platform] = row.SourceKey;
    }
    private void ForgetRecording(string songId)
    {
        recordingPlatforms.Remove(songId);
        foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(songId + "\0", StringComparison.Ordinal)).ToArray())
            recordingChoices.Remove(key);
    }
    private void UpdateCurrentRecordings()
    {
        CurrentRecordings.Clear();
        if (musicRecovery is { Media.Target: not null } state && current is { } song && CurrentRecovery(state))
        {
            CurrentRecordings.Add(new(song.Id, "", "", state.Media.Target.Automatic ? "自动选择 ✓" : "自动选择"));
            var snapshot = KnownOfficialSong(song);
            if (snapshot.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                foreach (var source in sources.EnumerateArray())
                {
                    string platform = MusicDto.Text(source, "source"), key = MusicDto.Text(source, "sourceKey");
                    if (!Library.NativeMusicSong.Platforms.Contains(platform)) continue;
                    string name = MusicSourceRow.PlatformName(platform);
                    string locator = SongJson.InfoId(source.GetProperty("musicInfo"));
                    CurrentRecordings.Add(new(song.Id, key, platform,
                        name + " · " + locator + (state.Media.Target.SourceKey == key ? " ✓" : "")));
                }
        }
        Changed(nameof(HasCurrentRecordings)); Changed(nameof(CanSelectCurrentRecording));
    }
    public void CloseCurrentRecordings()
    {
        recordingDiscovery?.Cancel();
        recordingDiscovery = null;
        recordingEntry = null; recordingMore = false; recordingPage = 0;
    }
    public Task RefreshCurrentRecordingsAsync()
    {
        CloseCurrentRecordings();
        recordingEntry = current?.EntryId; recordingMore = true;
        UpdateCurrentRecordings();
        return LoadCurrentRecordingsAsync();
    }
    public Task LoadCurrentRecordingsAsync()
    {
        if (recordingDiscovery is not null || !recordingMore || recordingPage >= 3 ||
            musicRecovery is not { Media.Target: not null } state || !CurrentRecovery(state) ||
            state.Song.EntryId != recordingEntry) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        recordingDiscovery = request;
        int pageNumber = recordingPage + 1;
        return RunAsync(async () =>
        {
            try
            {
                var target = state.Media!.Target!;
                var result = await QualityCall("music.official.candidates", new {
                    song = KnownOfficialSong(state.Song), quality = target.PolicyRequest,
                    qualityPolicySignature = target.QualityPolicySignature, discover = true, page = pageNumber
                }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!CurrentRecovery(state) || !ReferenceEquals(recordingDiscovery, request) ||
                    state.Song.EntryId != recordingEntry) return;
                MusicDto.Require(MusicDto.Text(result, "qualityPolicySignature") == target.QualityPolicySignature &&
                    MusicDto.Text(result.GetProperty("song"), "songId") == state.Song.Id);
                AcceptOfficialSong(state.Song, target with { RecordingSong = result.GetProperty("song").Clone() });
                recordingPage = pageNumber;
                recordingMore = result.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True;
                UpdateRecordingCandidates();
            }
            finally
            {
                if (ReferenceEquals(recordingDiscovery, request)) recordingDiscovery = null;
                request.Dispose();
            }
        });
    }

    // Only the player operation layer calls this. Selecting a row in a library
    // remains an intent for that row, never switches an unrelated current song.
    public Task SelectCurrentRecordingAsync(MusicRecordingRow choice)
    {
        if (!CanSelectCurrentRecording || !CurrentRecordings.Contains(choice) ||
            musicRecovery is not { } state || state.Song.Id != choice.SongId)
            return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (!CurrentRecovery(state) || state.Recovering || playback is not INativePlaybackContinuation port) return;
            state.Recovering = true; Changed(nameof(CanSelectCurrentRecording));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Work = done.Task;
            var old = state.Media!;
            bool replacementCompleted = false, committed = false;
            try
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
                request.CancelAfter(TimeSpan.FromSeconds(40));
                var target = old.Target!;
                // Resolve/validate before capturing or retiring the still-playing input.
                var next = await ResolveAutomaticSources(state.Song, target.PolicyRequest,
                    target.QualityPolicySignature!, request.Token, requireNetwork: true,
                    recordingPlatform: choice.SourceKey.Length == 0 ? null : choice.Platform,
                    recordingKey: choice.SourceKey.Length == 0 ? null : choice.SourceKey);
                request.Token.ThrowIfCancellationRequested();
                if (!CurrentRecovery(state)) throw new OperationCanceledException(request.Token);
                if (state.RecoveryDisabled ||
                    next.Target?.QualityPolicySignature is { } policy && policy != savedQualitySignature)
                    throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                var point = port.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                bool changedSource = SourceIdentity(old.Target!) != SourceIdentity(next.Target!);
                var pending = BeginPendingMedia(state, next, changedSource, request.Token);
                await ReopenPendingMedia(port, point, state, pending);
                replacementCompleted = true;
                request.Token.ThrowIfCancellationRequested();
                EnsurePendingMedia(state, pending);
                await RestorePlaybackRateAsync(state.Song, state.Epoch, pending.Access.Token);
                EnsurePendingMedia(state, pending);
                if (!playback.HasMedia || port.LastErrorCode is not null || state.PendingFailure is not null)
                    throw new MusicOperationException(state.PendingFailure ?? port.LastErrorCode ?? PlaybackError.OpenFailed);
                if (!CommitPendingMedia(state, pending, out bool alreadyStarted))
                    throw new OperationCanceledException(request.Token);
                committed = true;
                if (changedSource && recorded) CompleteFeedback("source-change");
                if (choice.SourceKey.Length == 0) ForgetRecording(state.Song.Id); else RememberRecording(choice);
                AcceptOfficialSong(state.Song, next.Target);
                if (changedSource) SelectFeedback(state.Song, currentFeedbackAutomatic, currentRecommendationFeedback);
                UpdateFeedbackSelection();
                state.Attempted = false; state.SourceFallbacks = 0;
                state.RejectedSources.Clear(); state.RejectedUrls.Clear();
                ScheduleMusicExpiry(state);
                UpdateRecordingCandidates(); UpdateLyricSources();
                PlaybackSourceBindings();
                if (IsLyrics) _ = RestoreLyricsAsync();
                if (alreadyStarted) ConfirmRecoveredSourceStarted(state);
            }
            catch
            {
                // A bad selected URL is not permission to select a different song.
                // Restore the last accepted recording once, with its original intent.
                bool pendingStarted = state.Pending?.SourceStarted == true;
                DiscardPendingMedia(state);
                state.PendingFailure = null;
                bool restoreRequired = !committed && CurrentRecovery(state) &&
                    (replacementCompleted || pendingStarted || !playback.HasMedia || port.LastErrorCode is not null);
                if (restoreRequired)
                {
                    try
                    {
                        var point = port.CaptureContinuation() ?? throw new MusicOperationException("CONTINUATION_UNAVAILABLE");
                        using var restore = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
                        restore.CancelAfter(TimeSpan.FromSeconds(20));
                        var restored = await ResolveMusicTarget(state.Song, old.Target!, restore.Token);
                        if (!CurrentRecovery(state)) throw new OperationCanceledException(restore.Token);
                        var pending = BeginPendingMedia(state, restored, restartFeedback: false, restore.Token);
                        await ReopenPendingMedia(port, point, state, pending);
                        EnsurePendingMedia(state, pending);
                        await RestorePlaybackRateAsync(state.Song, state.Epoch, pending.Access.Token);
                        EnsurePendingMedia(state, pending);
                        if (!playback.HasMedia || port.LastErrorCode is not null || state.PendingFailure is not null)
                            throw new MusicOperationException(state.PendingFailure ?? port.LastErrorCode ?? PlaybackError.OpenFailed);
                        if (!CommitPendingMedia(state, pending, out bool alreadyStarted))
                            throw new OperationCanceledException(restore.Token);
                        AcceptOfficialSong(state.Song, restored.Target);
                        UpdateFeedbackSelection();
                        ScheduleMusicExpiry(state);
                        UpdateRecordingCandidates(); UpdateLyricSources();
                        PlaybackSourceBindings();
                        if (alreadyStarted) ConfirmRecoveredSourceStarted(state);
                    }
                    catch
                    {
                        DiscardPendingMedia(state);
                        if (CurrentRecovery(state) && OwnsCurrent) await playback.StopAsync();
                    }
                }
                throw;
            }
            finally
            {
                DiscardPendingMedia(state);
                state.Recovering = false; done.TrySetResult();
                Changed(nameof(CanSelectCurrentRecording));
            }
        });
    }
}
