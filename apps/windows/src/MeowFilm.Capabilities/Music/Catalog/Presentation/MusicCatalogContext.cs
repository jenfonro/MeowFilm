using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

internal sealed class MusicCatalogContext
{
    private readonly Action<FrameworkElement, MusicCatalogCard?> updateVisibleSlot;

    internal MusicCatalogController Catalog { get; }
    internal MusicSearchController Search { get; }
    internal MusicLibraryController Library { get; }
    internal MusicSettingsController Settings { get; }
    internal MusicPlaybackController Playback { get; }

    internal MusicCatalogContext(
        MusicCatalogController catalog,
        MusicSearchController search,
        MusicLibraryController library,
        MusicSettingsController settings,
        MusicPlaybackController playback,
        Action<FrameworkElement, MusicCatalogCard?> updateVisibleSlot)
    {
        Catalog = catalog;
        Search = search;
        Library = library;
        Settings = settings;
        Playback = playback;
        this.updateVisibleSlot = updateVisibleSlot;
    }

    internal void UpdateVisibleSlot(FrameworkElement slot, MusicCatalogCard? card) => updateVisibleSlot(slot, card);
}
