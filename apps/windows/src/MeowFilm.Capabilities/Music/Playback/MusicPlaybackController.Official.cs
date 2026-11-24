namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private long officialAccountEpoch;

    private void ConfirmOfficialPlayback()
    {
        if (musicRecovery is not { Media.Target.Official: true } state || !CurrentRecovery(state)) return;
        var target = state.Media!.Target!;
        TrackMusicRecovery(sourceService.ConfirmOfficialPlaybackAsync(state.Song, target, state.Token));
    }

    private void OfficialAccountChanged(string platform)
    {
        // Account notifications may arrive off-dispatcher. Fence the selection snapshot
        // even when a script is playing, so its next explicit search uses new authority.
        sourceService.IncrementSelectionSourcesRevision();
        // Native authority is already fenced. Inspect and retire UI-owned resources
        // on their dispatcher, against the authority current when cleanup runs.
        if (!ctx.Dispatcher.IsClosing)
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (stopping || disposed) return;
                bool Revoked(MusicOnlineTarget? target) =>
                    target is { Official: true, AccountScope: { } scope } && target.Platform == platform &&
                    !application.IsOfficialPlaybackCurrent(platform, scope, target.Generation);
                var state = musicRecovery;
                bool revokeActive = Revoked(state?.Media?.Target);
                if (revokeActive || preparedAudio is { } prepared &&
                    prepared.Receipt.Info is { Kind: "official", Platform: var p, Generation: { } gen } && p == platform &&
                    !application.IsOfficialPlaybackCurrent(platform, prepared.Scope, gen))
                    CancelMusicPreload();
                if (state is not null && Revoked(state.Pending?.Media.Target))
                    CancelPendingMedia(state, "AUTH_REQUIRED");
                Changed(nameof(CanLoadMoreSources));
                if (!revokeActive || state is null) return;
                Interlocked.Increment(ref officialAccountEpoch);
                try { state.Access.Cancel(); } catch (ObjectDisposedException) { }
                TrackMusicRecovery(StopRevoked());
                async Task StopRevoked()
                {
                    var epoch = state.Epoch;
                    await RetireMusicRecovery();
                    if (epoch == playbackEpoch && playback.OwnerId == Id && playback.Identity == state.Song.EntryId)
                        await playback.StopAsync();
                }
            });
    }
}
