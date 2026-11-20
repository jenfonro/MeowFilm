using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

// A displayed card admits optional resources on its first viewport entry.
// Scrolling back over the same mounted content creates no new business demand.
internal sealed class MusicCatalogSlot
{
    private readonly FrameworkElement element;
    private readonly DesktopViewport viewport;
    private MusicCatalogView? owner;
    private bool seen;

    internal MusicCatalogSlot(FrameworkElement element)
    {
        this.element = element;
        viewport = new DesktopViewport(element, Report);
        element.Loaded += (_, _) =>
        {
            for (DependencyObject? node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicCatalogView view) { owner = view; break; }
            Report();
        };
        element.Unloaded += (_, _) => { owner?.UpdateVisibleSlot(element, null); owner = null; seen = false; };
        element.DataContextChanged += (_, _) => { seen = false; Report(); };
    }
    private void Report()
    {
        if (viewport?.IsInViewport == true) seen = true;
        owner?.UpdateVisibleSlot(element, seen && viewport?.IsVisible == true ? element.DataContext as MusicCatalogCard : null);
    }
}
