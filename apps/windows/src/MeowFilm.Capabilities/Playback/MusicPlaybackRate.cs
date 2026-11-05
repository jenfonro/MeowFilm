using System.Globalization;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // Original player behavior is session-scoped: a fresh App starts at 1x.
    // This is intent, not a measured output rate or a second media session.
    private double playbackRate = 1;
    private MusicPlaybackRate? playbackRateReceipt;
    public double PlaybackRate => playbackRate;
    public string PlaybackRateText => playbackRate.ToString("0.0", CultureInfo.InvariantCulture) + "×";
    public bool CanChangePlaybackRate => CanSeek && playback is IMusicPlaybackRate;
    public double? AcceptedPlaybackRate => OwnsCurrent && playback.HasMedia &&
        playbackRateReceipt?.Scope.Identity == current?.EntryId ? playbackRateReceipt?.RequestedRate : null;
    public bool EffectivePlaybackRateVerified => false;

    public Task ChangePlaybackRateAsync(double value)
    {
        if (!CanChangePlaybackRate) return Task.CompletedTask;
        var song = current!;
        var epoch = playbackEpoch;
        return RunAsync(async () =>
        {
            if (!double.IsFinite(value) || value is < 0.5 or > 3 ||
                Math.Abs(value * 10 - Math.Round(value * 10)) > 0.000001)
                throw new MusicOperationException(PlaybackError.InvalidRate);
            if (!CurrentRateTarget(song, epoch)) return;
            var requested = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            SampleFeedback(false);
            MusicPlaybackRate receipt;
            try { receipt = await ApplyRateToCurrentAsync(song, epoch, requested, lifetime.Token); }
            catch { SampleFeedback(); throw; }
            if (!CurrentRateTarget(song, epoch)) return;
            playbackRate = requested;
            playbackRateReceipt = receipt;
            SampleFeedback();
            PlaybackRateBindings();
        });
    }

    private bool CurrentRateTarget(SongRow song, long epoch) =>
        !stopping && !disposed && epoch == playbackEpoch && ReferenceEquals(song, current) &&
        playback.OwnerId == Id && playback.Identity == song.EntryId && playback.HasMedia;

    private async Task<MusicPlaybackRate> ApplyRateToCurrentAsync(SongRow song, long epoch,
        double rate, CancellationToken token)
    {
        if (playback is not IMusicPlaybackRate port) throw new MusicOperationException("RATE_UNSUPPORTED");
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch)) throw new OperationCanceledException();
        var snapshot = await port.ReadRateAsync(token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch) || snapshot is null || snapshot.Scope.OwnerId != Id ||
            snapshot.Scope.Identity != song.EntryId || snapshot.Scope.Generation <= 0)
            throw new OperationCanceledException();
        var receipt = await port.SetRateAsync(snapshot.Scope, rate, token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch) || receipt.Scope != snapshot.Scope)
            throw new OperationCanceledException();
        if (!double.IsFinite(receipt.RequestedRate) || Math.Abs(receipt.RequestedRate - rate) > 0.001)
            throw new MusicOperationException(PlaybackError.RateUnsupported);
        return receipt;
    }

    private async Task RestorePlaybackRateAsync(SongRow song, long epoch, CancellationToken token)
    {
        playbackRateReceipt = null;
        if (playback is not IMusicPlaybackRate || !CurrentRateTarget(song, epoch)) return;
        try
        {
            var receipt = await ApplyRateToCurrentAsync(song, epoch, playbackRate, token);
            if (CurrentRateTarget(song, epoch)) playbackRateReceipt = receipt;
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        catch
        {
            // An unsupported rate does not silently claim success or discard the
            // chosen song. Retain intent for the next input, expose this failure.
            if (CurrentRateTarget(song, epoch)) { status = "当前音频未接受播放倍速"; Changed(nameof(Status)); }
        }
        finally { PlaybackRateBindings(); }
    }

    private void PlaybackRateBindings()
    {
        Changed(nameof(PlaybackRate)); Changed(nameof(PlaybackRateText));
        playbackPresentation.Refresh();
    }
}
