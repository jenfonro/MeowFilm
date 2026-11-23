using MeowFilm.Desktop;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

// A displayed card admits optional resources on its first viewport entry.
// Scrolling back over the same mounted content creates no new business demand.
internal sealed class MusicCatalogSlot
{
    private readonly FrameworkElement element;
    private readonly DesktopViewport viewport;
    private MusicCatalogContext? context;
    private bool seen;

    internal MusicCatalogSlot(FrameworkElement element)
    {
        this.element = element;
        viewport = new DesktopViewport(element, Report);
        element.Loaded += (_, _) => Report();
        element.Unloaded += (_, _) =>
        {
            if (element.IsLoaded) return;
            context?.UpdateVisibleSlot(element, null);
            seen = false;
        };
        element.DataContextChanged += (_, _) => { seen = false; Report(); };
    }

    internal void SetContext(MusicCatalogContext? next)
    {
        if (ReferenceEquals(context, next)) return;
        context?.UpdateVisibleSlot(element, null);
        context = next;
        Report();
    }

    private void Report()
    {
        if (viewport?.IsInViewport == true) seen = true;
        context?.UpdateVisibleSlot(element, seen && viewport?.IsVisible == true ? element.DataContext as MusicCatalogCard : null);
    }
}
