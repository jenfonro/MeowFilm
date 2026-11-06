using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
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

    private void CancelPreparedAudio()
    {
        var previous = preparedAudio; preparedAudio = null; preparedEntry = null;
        previous?.Dispose();
    }

    private void CancelMusicPreload()
    {
        var attempt = musicPreload;
        if (attempt is not null)
        {
            try { attempt.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        CancelPreparedAudio();
    }

    private static string? QueueCurrent(JsonElement snapshot)
    {
        MusicDto.Require(snapshot.ValueKind == JsonValueKind.Object);
        MusicDto.Require(snapshot.TryGetProperty("currentId", out var current) &&
            current.ValueKind is JsonValueKind.Null or JsonValueKind.String);
        var value = current.ValueKind == JsonValueKind.Null ? null : current.GetString();
        MusicDto.Require(current.ValueKind == JsonValueKind.Null || !string.IsNullOrEmpty(value));
        return value;
    }

    private void ObservePreloadQueueMutation(JsonElement snapshot)
    {
        var attempt = musicPreload;
        if (attempt is null && preparedAudio is null) return;
        string? current = QueueCurrent(snapshot);
        if (preparedEntry is not null && current == preparedEntry) return;
        if (attempt is null || !attempt.QueueObserved ||
            attempt.QueueVersion != MusicDto.Integer(snapshot, "version") ||
            attempt.QueueMode != MusicDto.Mode(snapshot) ||
            attempt.QueueCurrent != current)
            CancelMusicPreload();
    }

    private void ObservePreloadQueueSnapshot(JsonElement snapshot)
    {
        var attempt = musicPreload;
        if (attempt is null || !attempt.QueueObserved) return;
        string? current = QueueCurrent(snapshot);
        if (preparedEntry is not null && current == preparedEntry) return;
        if (attempt.QueueVersion != MusicDto.Integer(snapshot, "version") ||
            attempt.QueueMode != MusicDto.Mode(snapshot) ||
            attempt.QueueCurrent != current)
            CancelMusicPreload();
    }

    private void ObserveMusicPreload()
    {
        if (musicRecovery is not { Media.Target: not null } state || !CurrentRecovery(state) ||
            state.Pending is not null || state.Recovering ||
            !IsPlaying || DurationSeconds <= 0 || DurationSeconds - PositionSeconds is <= 0 or > 20 ||
            mode == 3 || ReferenceEquals(preloadedFrom, state)) return;
        preloadedFrom = state;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        var attempt = new MusicPreloadAttempt(state, cancellation);
        musicPreload = attempt;
        TrackPlaybackCache(Preload(attempt), state);
    }

    private async Task Preload(MusicPreloadAttempt attempt)
    {
        PlaybackCache.MusicAudioFileCache.PreparedFile? ownedPrepared = null;
        try
        {
            var state = attempt.Origin;
            var next = await Call("music.queue.peekNext", cancellationToken: attempt.Cancellation.Token);
            if (!ReferenceEquals(musicPreload, attempt) || !CurrentRecovery(state)) return;
            attempt.QueueVersion = MusicDto.Integer(next, "version");
            attempt.QueueMode = MusicDto.Mode(next);
            attempt.QueueCurrent = QueueCurrent(next);
            if (attempt.QueueCurrent != state.Song.EntryId) return;
            var entry = next.GetProperty("next");
            if (entry.ValueKind != JsonValueKind.Object) return;
            attempt.TargetEntry = MusicDto.Text(entry, "id");
            attempt.QueueObserved = true;
            var song = SongRow.Read(entry.GetProperty("song"), attempt.TargetEntry);
            if (recordingChoices.Keys.Any(key =>
                key.StartsWith(song.Id + "\0", StringComparison.Ordinal))) return;
            // Built-in, already-mapped recordings only: preparing a file never searches or runs scripts.
            var session = await BeginSourceSession(song, attempt.Cancellation.Token, officialOnly: true);
            if (session.Signature != savedQualitySignature) return;
            var media = await session.AutomaticAsync(attempt.Cancellation.Token);
            if (media.Uri is null || media.Receipt is not { } receipt || media.Target is not { } selected) return;
            string platform = selected.Platform;
            if (!ReferenceEquals(musicPreload, attempt) || !CurrentRecovery(state) || audioFileCache is null) return;
            CancelPreparedAudio();
            string scope = MusicDto.Text(receipt, "scope"), signature = MusicDto.Text(receipt, "qualityCacheSignature"),
                songId = MusicDto.Text(receipt, "songId");
            long generation = MusicDto.Integer(receipt, "generation");
            if (await audioFileCache.ReadInfoAsync(scope, songId, signature, attempt.Cancellation.Token) is not null) return;
            if (!ReferenceEquals(musicPreload, attempt) || !CurrentRecovery(state)) return;
            preparedEntry = attempt.TargetEntry;
            ownedPrepared = audioFileCache.Prepare(scope, songId, signature, FileReceipt(song, receipt),
                () => !stopping && savedQualitySignature == session.Signature &&
                    application.IsOfficialPlaybackCurrent(platform, scope, generation), lifetime.Token);
            preparedAudio = ownedPrepared;
            // Complete validated bytes remain provisional until this exact queue entry really starts.
            await ownedPrepared.Work;
        }
        catch (Exception error) when (error is PlaybackCache.MusicPlaybackCacheDataException or
            PlaybackCache.MusicPlaybackCacheCommitException) { throw; }
        catch (OperationCanceledException) { }
        catch { /* Speculative failure is retried by the actual playback request, not a background loop. */ }
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
