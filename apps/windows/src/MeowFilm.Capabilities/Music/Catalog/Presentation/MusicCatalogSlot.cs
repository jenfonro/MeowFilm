using MeowFilm.Desktop;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

// A displayed card admits optional resources on its first viewport entry.
// Cards with no optional read demand attach zero visibility or viewport observers.
internal sealed class MusicCatalogSlot
{
    private readonly FrameworkElement element;
    private DesktopViewport? viewport;
    private MusicCatalogContext? context;

    internal MusicCatalogSlot(FrameworkElement element)
    {
        this.element = element;
        element.Loaded += (_, _) => Sync(reset: false);
        element.Unloaded += (_, _) =>
        {
            if (element.IsLoaded) { Sync(reset: false); return; }
            context?.UpdateVisibleSlot(element, null);
            viewport?.Dispose(); viewport = null;
        };
        element.DataContextChanged += (_, _) => Sync(reset: true);
    }

    internal void SetContext(MusicCatalogContext? next)
    {
        if (ReferenceEquals(context, next)) return;
        context?.UpdateVisibleSlot(element, null);
        context = next;
        Sync(reset: false);
    }

    private void Sync(bool reset)
    {
        if (element.DataContext is not MusicCatalogCard { NeedsOptionalRead: true } || !element.IsLoaded)
        {
            viewport?.Dispose(); viewport = null;
            context?.UpdateVisibleSlot(element, null);
            return;
        }
        if (viewport is null) viewport = new DesktopViewport(element, Report);
        else if (reset) viewport.Reset();
        Report();
    }

    private void Report() =>
        context?.UpdateVisibleSlot(element, viewport is { Admitted: true, IsVisible: true } ? element.DataContext as MusicCatalogCard : null);
}
