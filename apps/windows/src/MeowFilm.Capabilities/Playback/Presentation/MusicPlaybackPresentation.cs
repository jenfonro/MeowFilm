namespace MeowFilm.Capabilities;

// A publication boundary, not another playback clock. Only changed display
// values cross into WinUI bindings; the media port remains the source of truth.
internal sealed class MusicPlaybackPresentation(MusicPlaybackController controller, Action<string> changed)
{
    private bool playing, canPlay, canStep, canSeek, canRate, canToggle, canFavorite, canAddToPlaylist, hasQuality;
    private double? rate;
    private int volume;
    private string source = "", quality = "", sourceQuality = "", sourceSymbol = "", sourceDescription = "",
        glyph = "▶";

    internal void RefreshPosition()
    {
        controller.SyncPlaybackClockPosition();
        controller.HostLyrics.RefreshLyricPosition();
    }

    internal void Refresh()
    {
        controller.SyncPlaybackClock();
        Publish(ref playing, controller.IsPlaying, nameof(MusicPlaybackController.IsPlaying));
        Publish(ref source, controller.CurrentSource, nameof(MusicPlaybackController.CurrentSource));
        Publish(ref sourceQuality, controller.CurrentSourceQuality, nameof(MusicPlaybackController.CurrentSourceQuality));
        Publish(ref sourceSymbol, controller.CurrentSourceSymbol, nameof(MusicPlaybackController.CurrentSourceSymbol));
        Publish(ref sourceDescription, controller.CurrentSourceDescription, nameof(MusicPlaybackController.CurrentSourceDescription));
        Publish(ref quality, controller.CurrentQualityLabel, nameof(MusicPlaybackController.CurrentQualityLabel));
        Publish(ref hasQuality, controller.HasQualityButton, nameof(MusicPlaybackController.HasQualityButton));
        Publish(ref glyph, controller.PlayGlyph, nameof(MusicPlaybackController.PlayGlyph));
        Publish(ref volume, controller.Volume, nameof(MusicPlaybackController.Volume));
        Publish(ref rate, controller.AcceptedPlaybackRate, nameof(MusicPlaybackController.AcceptedPlaybackRate));
        RefreshAvailability();
        RefreshPosition();
    }

    internal void RefreshAvailability()
    {
        Publish(ref canPlay, controller.CanPlay, nameof(MusicPlaybackController.CanPlay));
        Publish(ref canStep, controller.CanStep, nameof(MusicPlaybackController.CanStep));
        Publish(ref canSeek, controller.CanSeek, nameof(MusicPlaybackController.CanSeek));
        Publish(ref canRate, controller.CanChangePlaybackRate, nameof(MusicPlaybackController.CanChangePlaybackRate));
        Publish(ref canToggle, controller.CanTogglePlayback, nameof(MusicPlaybackController.CanTogglePlayback));
        Publish(ref canFavorite, controller.CanFavoriteCurrent, nameof(MusicPlaybackController.CanFavoriteCurrent));
        Publish(ref canAddToPlaylist, controller.CanAddCurrentToPlaylist, nameof(MusicPlaybackController.CanAddCurrentToPlaylist));
        controller.HostQueue.NotifyPlaybackModeAvailability();
        controller.HostLyrics.RefreshLyricPreviewAvailability();
    }

    private void Publish<T>(ref T prior, T next, string property)
    {
        if (EqualityComparer<T>.Default.Equals(prior, next)) return;
        prior = next;
        changed(property);
    }
}
