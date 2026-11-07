namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private void PlaybackSourceBindings()
    {
        // A replacement receipt can be committed after the native state events.
        // Publish from that commit, not from a later unrelated position tick.
        SyncChorus();
        playbackPresentation.Refresh();
    }
}

// A publication boundary, not another playback clock. Only changed display
// values cross into WPF bindings; the media port remains the source of truth.
internal sealed class MusicPlaybackPresentation(MusicCapability module, Action<string> changed)
{
    private bool playing, canSeek, canRate, canToggle, hasQuality;
    private double position, duration = 1;
    private double? rate;
    private int volume;
    private string source = "", quality = "", sourceQuality = "", glyph = "▶", positionText = "0:00", durationText = "0:00";

    internal void RefreshPosition()
    {
        Publish(ref position, module.PositionSeconds, nameof(MusicCapability.PositionSeconds));
        Publish(ref positionText, module.PositionText, nameof(MusicCapability.PositionText));
        module.RefreshLyricPosition();
    }

    internal void Refresh()
    {
        Publish(ref playing, module.IsPlaying, nameof(MusicCapability.IsPlaying));
        Publish(ref source, module.CurrentSource, nameof(MusicCapability.CurrentSource));
        Publish(ref sourceQuality, module.CurrentSourceQuality, nameof(MusicCapability.CurrentSourceQuality));
        Publish(ref quality, module.CurrentQualityLabel, nameof(MusicCapability.CurrentQualityLabel));
        Publish(ref hasQuality, module.HasQualityButton, nameof(MusicCapability.HasQualityButton));
        Publish(ref glyph, module.PlayGlyph, nameof(MusicCapability.PlayGlyph));
        Publish(ref duration, module.DurationSeconds, nameof(MusicCapability.DurationSeconds));
        Publish(ref durationText, module.DurationText, nameof(MusicCapability.DurationText));
        Publish(ref volume, module.Volume, nameof(MusicCapability.Volume));
        Publish(ref canSeek, module.CanSeek, nameof(MusicCapability.CanSeek));
        Publish(ref canRate, module.CanChangePlaybackRate, nameof(MusicCapability.CanChangePlaybackRate));
        Publish(ref rate, module.AcceptedPlaybackRate, nameof(MusicCapability.AcceptedPlaybackRate));
        Publish(ref canToggle, module.CanTogglePlayback, nameof(MusicCapability.CanTogglePlayback));
        RefreshPosition();
    }

    private void Publish<T>(ref T prior, T next, string property)
    {
        if (EqualityComparer<T>.Default.Equals(prior, next)) return;
        prior = next;
        changed(property);
    }
}
