using System.Windows;
using System.Windows.Media;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private bool ReducedMotion => !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast;

    private void InitializePresentationMotion()
    {
    }

    private void OnPresentationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // DWM owns the outer corner shape. Only rectangular transition bounds
        // belong to WPF; a second rounded clip produces a fuzzy/double fringe.
        PresentationSurface.Clip = new RectangleGeometry(new Rect(new Point(), e.NewSize));
    }

    private void DisposePresentationMotion()
    {
    }
}
