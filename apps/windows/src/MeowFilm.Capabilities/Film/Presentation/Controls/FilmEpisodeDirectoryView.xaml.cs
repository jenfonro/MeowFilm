using MeowFilm.Desktop.Artwork;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class FilmEpisodeDirectoryView : UserControl
{
    public FilmPlaybackPage Page { get; private set; }
    public FilmEpisodeDirectory Directory { get; }
    internal ListView EpisodeListControl => EpisodeList;

    internal event Action<FilmEpisodeItem>? EpisodeActivated;
    internal event Action<FilmCandidateItem>? CandidateActivated;

    internal FilmEpisodeDirectoryView(FilmPlaybackPage page, FilmEpisodeDirectory directory)
    {
        Page = page;
        Directory = directory;
        InitializeComponent();
        object EpisodeKey(object item) => (directory, ((FilmEpisodeItem)item).Identity);
        EpisodeShelf.SetArtworkItemKeySelector(EpisodeKey);
        EpisodeShelf.SetArtworkItemResourcesSelector(item => FilmPlaybackPoster.ArtworkResources(((FilmEpisodeItem)item).Image));
        ArtworkItemsScope.SetItemKeySelector(EpisodeList, EpisodeKey);
        ArtworkItemsScope.SetItemResourcesSelector(EpisodeList,
            item => FilmPlaybackPoster.ArtworkResources(((FilmEpisodeItem)item).Image));
    }

    internal void BindPage(FilmPlaybackPage page)
    {
        if (ReferenceEquals(Page, page)) return;
        Page = page;
        Bindings?.Update();
    }

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FilmEpisodeItem episode })
            EpisodeActivated?.Invoke(episode);
    }

    private void Candidate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FilmCandidateItem candidate })
            CandidateActivated?.Invoke(candidate);
    }
}
