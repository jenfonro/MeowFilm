namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    public bool CanClearPlaybackCache => CanEdit && audioFileCache is not null;
    public Task ClearPlaybackCacheAsync() => RunAsync(async () =>
    {
        if (audioFileCache is null || !dialogs.Confirm("清空播放缓存？", "播放缓存")) return;
        CancelMusicPreload();
        await audioFileCache.ClearAsync(lifetime.Token);
        status = "播放缓存已清空"; Changed(nameof(Status));
    });

    private void ConfirmPlaybackFileCache()
    {
        if (audioFileCache is null || musicRecovery is not { Media.Target.Official: true } state ||
            !CurrentRecovery(state) || state.Media?.Target is not { AccountScope: not null, QualityCacheSignature: not null } target)
            return;
        if (preparedAudio is { } prepared && preparedEntry == state.Song.EntryId &&
            prepared.Matches(target.AccountScope, state.Song.Id, target.QualityCacheSignature) &&
            !prepared.Work.IsCompleted)
        {
            prepared.Played.TrySetResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }
        if (state.Cached)
            TrackPlaybackCache(Touch(), state);
        else if (state.Media.Receipt is { } receipt)
            TrackPlaybackCache(Store(receipt), state);
        async Task Store(System.Text.Json.JsonElement receipt)
        {
            var accepted = state.Media;
            await audioFileCache.StorePlayedAsync(target.AccountScope!, state.Song.Id,
                target.QualityCacheSignature!, receipt, () => CurrentRecovery(state) && ReferenceEquals(state.Media, accepted), state.Token);
            var info = await audioFileCache.ReadInfoAsync(target.AccountScope!, state.Song.Id,
                target.QualityCacheSignature!, state.Token);
            if (info is not null && CurrentRecovery(state) && ReferenceEquals(state.Media, accepted) && state.Media is { } media)
            {
                state.Media = media with { Receipt = System.Text.Json.JsonSerializer.SerializeToElement(info) };
                Changed(nameof(CurrentQualityLabel));
            }
        }
        Task Touch() => audioFileCache.TouchAsync(target.AccountScope!, state.Song.Id,
            target.QualityCacheSignature!, state.Token);
    }
    private async Task InvalidatePlaybackFileCache(MusicRecovery state)
    {
        if (audioFileCache is null || state.Media?.Target is not { AccountScope: not null } target) return;
        long stamp = statusEpoch;
        try { await audioFileCache.EraseAsync(target.AccountScope, state.Song.Id, lifetime.Token); }
        catch (PlaybackCache.MusicPlaybackCacheDataException error)
        {
            PublishPlaybackCacheFailure(error, state, stamp);
        }
    }
    private void TrackPlaybackCache(Task task, MusicRecovery state)
    {
        long stamp = statusEpoch;
        var observed = Observe();
        eventWrites.Add(observed);
        _ = Remove();
        async Task Observe()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { PublishPlaybackCacheFailure(error, state, stamp); }
        }
        async Task Remove() { await observed; eventWrites.Remove(observed); }
    }
    private void PublishPlaybackCacheFailure(Exception error, MusicRecovery state, long stamp)
    {
        if (stopping || disposed || stamp != statusEpoch || !ReferenceEquals(musicRecovery, state)) return;
        status = ErrorText(error);
        Changed(nameof(Status));
    }
}
