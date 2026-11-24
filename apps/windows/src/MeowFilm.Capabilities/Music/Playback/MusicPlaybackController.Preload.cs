using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private sealed class MusicPreloadAttempt(MusicRecovery origin, CancellationTokenSource cancellation)
    {
        internal readonly MusicRecovery Origin = origin;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal bool QueueObserved;
        internal long QueueVersion;
        internal int QueueMode;
        internal string? QueueCurrent;
        internal string? TargetEntry;
    }

    private MusicRecovery? preloadedFrom;
    private MusicPreloadAttempt? musicPreload;
    private PlaybackCache.MusicAudioFileCache.PreparedFile? preparedAudio;
    private string? preparedEntry;

    internal string? PreparedAudioReceiptSignature =>
        preparedAudio is { } prepared ? prepared.Receipt.Info.QualityPolicySignature : null;

    private PlaybackCache.AudioFileInfoRecord? TryGetVerifiedPreparedReceipt(SongRow song, string scope, string signature) =>
        preparedAudio is { } prepared && preparedEntry == song.EntryId &&
        prepared.Matches(scope, song.Id, signature) && prepared.Ready.Task.IsCompletedSuccessfully &&
        prepared.Ready.Task.Result is not null
            ? prepared.VerifiedReceipt
            : null;

    private void CancelPreparedAudio()
    {
        var previous = preparedAudio; preparedAudio = null; preparedEntry = null;
        previous?.Dispose();
    }

    internal void CancelMusicPreload()
    {
        var attempt = musicPreload;
        if (attempt is not null)
        {
            try { attempt.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        CancelPreparedAudio();
    }

    internal void ObservePreloadQueueMutation(IMusicQueuePosition snapshot)
    {
        var attempt = musicPreload;
        if (attempt is null && preparedAudio is null) return;
        string? current = snapshot.CurrentId;
        if (preparedEntry is not null && current == preparedEntry) return;
        if (attempt is null || !attempt.QueueObserved ||
            attempt.QueueVersion != snapshot.Version ||
            attempt.QueueMode != snapshot.Mode ||
            attempt.QueueCurrent != current)
            CancelMusicPreload();
    }

    private void ObservePreloadQueueSnapshot(IMusicQueuePosition snapshot)
    {
        var attempt = musicPreload;
        if (attempt is null || !attempt.QueueObserved) return;
        string? current = snapshot.CurrentId;
        if (preparedEntry is not null && current == preparedEntry) return;
        if (attempt.QueueVersion != snapshot.Version ||
            attempt.QueueMode != snapshot.Mode ||
            attempt.QueueCurrent != current)
            CancelMusicPreload();
    }

    private void ObserveMusicPreload()
    {
        if (musicRecovery is not { Media.Target: not null } state || !CurrentRecovery(state) ||
            state.Pending is not null || state.Recovering ||
            !IsPlaying || DurationSeconds <= 0 || DurationSeconds - PositionSeconds is <= 0 or > 20 ||
            ReferenceEquals(preloadedFrom, state)) return;
        preloadedFrom = state;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        var attempt = new MusicPreloadAttempt(state, cancellation);
        musicPreload = attempt;
        TrackPlaybackCache(Preload(attempt));
    }

    private async Task Preload(MusicPreloadAttempt attempt)
    {
        PlaybackCache.MusicAudioFileCache.PreparedFile? ownedPrepared = null;
        try
        {
            var state = attempt.Origin;
            var next = await application.PeekNextQueueAsync(attempt.Cancellation.Token);
            if (!ReferenceEquals(musicPreload, attempt) || !CurrentRecovery(state)) return;
            attempt.QueueVersion = next.Version;
            attempt.QueueMode = next.Mode;
            attempt.QueueCurrent = next.CurrentId;
            if (attempt.QueueCurrent != state.Song.EntryId) return;
            if (next.Next is not { } entry) return;
            attempt.TargetEntry = entry.EntryId;
            attempt.QueueObserved = true;
            var song = SongRow.FromRecord(entry.Song, attempt.TargetEntry);
            if (sourceService.HasRememberedRecording(song.Id)) return;
            // Built-in, already-mapped recordings only: preparing a file never searches or runs scripts.
            await using var session = await sourceService.BeginSessionAsync(song, attempt.Cancellation.Token, officialOnly: true);
            if (session.Signature != settings.SavedQualitySignature) return;
            var media = await session.AutomaticAsync(attempt.Cancellation.Token);
            if (media.Uri is null || media.Receipt is not { } receipt || media.Target is not { } selected) return;
            string platform = selected.Platform;
            if (!ReferenceEquals(musicPreload, attempt) || !CurrentRecovery(state) || audioFileCache is null) return;
            CancelPreparedAudio();
            if (receipt.Scope is not { Length: > 0 } scope ||
                receipt.QualityCacheSignature is not { Length: > 0 } signature ||
                receipt.SongId is not { Length: > 0 } songId ||
                receipt.Generation is not { } generation) return;
            if (await audioFileCache.ReadInfoAsync(scope, songId, signature, attempt.Cancellation.Token) is not null) return;
            if (!ReferenceEquals(musicPreload, attempt) || !CurrentRecovery(state)) return;
            preparedEntry = attempt.TargetEntry;
            ownedPrepared = audioFileCache.Prepare(scope, songId, signature, FileReceipt(song, media),
                () => !stopping && settings.SavedQualitySignature == session.Signature &&
                    application.IsOfficialPlaybackCurrent(platform, scope, generation), lifetime.Token);
            preparedAudio = ownedPrepared;
            // Complete validated bytes remain provisional until this exact queue entry really starts.
            await ownedPrepared.Work;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicPlayback, "preload", "speculative-preload-failed",
                new { error = error.Message });
        }
        finally
        {
            if (ReferenceEquals(musicPreload, attempt)) musicPreload = null;
            attempt.Cancellation.Dispose();
            if (ownedPrepared is not null && ReferenceEquals(preparedAudio, ownedPrepared))
            {
                preparedAudio = null;
                preparedEntry = null;
                ownedPrepared.Dispose();
            }
        }
    }
}
