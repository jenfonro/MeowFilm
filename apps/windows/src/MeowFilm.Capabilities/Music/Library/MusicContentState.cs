using System.Collections.ObjectModel;

namespace MeowFilm.Capabilities;

/// <summary>
/// Shared Layer-0 content-page collections, summary metadata, search section projections,
/// and song selection state shared across Library, Catalog, Search, and Playback without cross-controller coupling.
/// </summary>
internal sealed class MusicContentState(MusicRuntimeContext ctx, MusicCatalogActionState catalogActions)
{
    private SongRow? selectedSong;
    private string heading = "推荐";
    private int total;

    internal ObservableCollection<PlaylistRow> Playlists { get; } = [];
    internal ObservableCollection<PlaylistRow> NetworkPlaylists { get; } = [];
    internal ObservableCollection<SongRow> CatalogSongsSource { get; } = [];

    internal ObservableCollection<MusicCatalogSection> SearchOverviewSections { get; } = [];
    internal ObservableCollection<MusicCatalogSection> SearchSongSections { get; } = [];
    internal ObservableCollection<MusicCatalogSection> SearchPlaylistSections { get; } = [];
    internal string SearchKeyword { get; set; } = "";
    internal string SearchView { get; set; } = "overview";
    internal string SearchKindLabel { get; set; } = "歌曲";
    internal long SearchAccountEpoch { get; set; }
    internal bool HasSearchSession { get; set; }

    internal ObservableCollection<MusicCatalogSection> SearchActiveSections => SearchView switch
    {
        "overview" => SearchOverviewSections,
        "songs" => SearchSongSections,
        _ => SearchPlaylistSections
    };
    internal bool IsDiscovery => ctx.CurrentRoute == "catalog-discovery";
    internal bool IsSearchPage => ctx.CurrentRoute == "catalog-search";
    internal bool IsSearchSongs => IsSearchPage && SearchView == "songs";

    internal string Heading { get => heading; set => heading = value; }
    internal int Total { get => total; set => total = value; }
    internal SongRow? SelectedSong => selectedSong;

    internal event Action<SongRow?>? SelectionChanged;
    internal event Action? SelectionRejected;
    internal event Action? SelectionCleared;
    internal event Action? DetailBindingsInvalidated;
    internal event Action? SummaryChanged;
    internal event Action? SelectionBindingInvalidated;

    internal void SetSelectedSong(SongRow? value)
    {
        if (ctx.Stopping) return;
        if (value is not null && !ctx.ActiveSongs.Any(row => ReferenceEquals(row, value)))
        {
            SelectionRejected?.Invoke();
            return;
        }
        if (ReferenceEquals(value, selectedSong)) return;
        if (!ctx.Publishing && value is not null) catalogActions.Invalidate();
        selectedSong = value;
        SelectionChanged?.Invoke(value);
    }

    internal void ClearSelectionOnly()
    {
        selectedSong = null;
        SelectionCleared?.Invoke();
    }

    internal void ClearSelectionForCatalog(string nextHeading)
    {
        heading = nextHeading;
        total = 0;
        ClearSelectionOnly();
    }

    internal void ClearTotal() => total = 0;

    internal void PublishSearchSummary(string nextHeading, int nextTotal)
    {
        heading = nextHeading;
        total = nextTotal;
        SummaryChanged?.Invoke();
    }

    internal void PublishCatalogSummary(int nextTotal, bool clearSelection)
    {
        total = nextTotal;
        if (clearSelection) ClearSelectionOnly();
    }

    internal void NotifyDetailBindings() => DetailBindingsInvalidated?.Invoke();
    internal void NotifySelectionChanged() => SelectionBindingInvalidated?.Invoke();
}
