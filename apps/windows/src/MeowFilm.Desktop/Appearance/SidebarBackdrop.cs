using System.Windows;
using System.Windows.Media;

namespace MeowFilm.Desktop;

// Samples only the dedicated background sibling, never a shell ancestor or foreground.
public sealed class SidebarBackdrop : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(FrameworkElement), typeof(SidebarBackdrop),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, SourceChanged));

    public FrameworkElement? Source
    {
        get => (FrameworkElement?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private VisualBrush? brush;

    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (SidebarBackdrop)sender;
        if (e.OldValue is FrameworkElement previous) previous.SizeChanged -= backdrop.SourceSizeChanged;
        if (e.NewValue is FrameworkElement source)
        {
            source.SizeChanged += backdrop.SourceSizeChanged;
            backdrop.brush = new VisualBrush(source) { AutoLayoutContent = false, Stretch = Stretch.Fill };
        }
        else backdrop.brush = null;
    }

    private void SourceSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Source is not { ActualWidth: > 0, ActualHeight: > 0 } source || brush is null) return;
        // Draw the whole source before blurring. The parent clips the result to the
        // sidebar, so neither the gradient nor its blur kernel restarts at its edge.
        drawingContext.DrawRectangle(brush, null,
            new Rect(source.TranslatePoint(new Point(), this), source.RenderSize));
    }
}
