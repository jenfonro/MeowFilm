using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public enum MusicContentPage { Catalog, Discovery, Archives, Songs, SearchSongs }

// Exactly one body participates in layout, input and Loaded/Unloaded at a time.
// The page's own state survives navigation without keeping hidden pages mounted.
internal sealed class MusicPagePresenter : ContentControl
{
    private readonly Dictionary<MusicContentPage, FrameworkElement> pages;
    internal MusicPagePresenter(Dictionary<MusicContentPage, FrameworkElement> pages)
    {
        this.pages = pages;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }
    internal void Show(MusicContentPage page)
    {
        var next = pages[page];
        if (!ReferenceEquals(Content, next)) Content = next;
    }
}
