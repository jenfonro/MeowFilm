using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

// Only the existing native artwork/background is sampled. Text and interaction
// remain in the normal XAML tree; this is not a screenshot of a hosted UI.
public sealed class MusicArtworkFade : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(FrameworkElement), typeof(MusicArtworkFade), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty DecorationProperty = DependencyProperty.Register(nameof(Decoration), typeof(bool), typeof(MusicArtworkFade), new PropertyMetadata(false, (owner, _) => ((MusicArtworkFade)owner).Refresh()));
    public FrameworkElement? Source { get => (FrameworkElement?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool Decoration { get => (bool)GetValue(DecorationProperty); set => SetValue(DecorationProperty, value); }
    private CompositionVisualSurface? surface;
    private CompositionSurfaceBrush? sourceBrush;
    private CompositionLinearGradientBrush? mask;
    private CompositionMaskBrush? brush;
    private SpriteVisual? visual;
    private IDisposable? visibility, sourceLease;
    private ArtworkImage? preparedSource;
    private CancellationTokenSource? preparation;
    internal CompositionLinearGradientBrush? Mask => mask;
    public MusicArtworkFade()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { visibility = DesktopVisual.ObserveVisibility(this, _ => Refresh()); LayoutUpdated += LayoutChanged; Refresh(); };
        SizeChanged += (_, _) => Refresh();
        EffectiveViewportChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { LayoutUpdated -= LayoutChanged; visibility?.Dispose(); visibility = null; Release(); };
    }
    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var fade = (MusicArtworkFade)owner;
        fade.RetireSource();
        if (args.OldValue is FrameworkElement previous) { previous.SizeChanged -= fade.SourceResized; previous.Loaded -= fade.SourceLoaded; }
        if (args.NewValue is FrameworkElement next) { next.SizeChanged += fade.SourceResized; next.Loaded += fade.SourceLoaded; }
        fade.Refresh();
    }
    private void SourceLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void SourceResized(object sender, SizeChangedEventArgs e) => Refresh();
    private void LayoutChanged(object? sender, object args)
    {
        if (InViewport() != (surface is not null)) Refresh();
    }
    private bool InViewport()
    {
        if (!DesktopVisual.IsVisible(this) || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            Rect visible = new(0, 0, ActualWidth, ActualHeight);
            UIElement child = this;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not UIElement visual) continue;
                visible = child.TransformToVisual(visual).TransformBounds(visible); child = visual;
                if (visual is FrameworkElement element && (element.Clip is not null || element is ScrollContentPresenter or ScrollViewer || ReferenceEquals(element, XamlRoot?.Content)))
                {
                    visible.Intersect(element.Clip?.Rect ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
                }
            }
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void Refresh()
    {
        if (!InViewport() || Source is not { IsLoaded: true } source) { Release(); return; }
        if (Decoration && source is ArtworkImage artwork)
        {
            if (!ReferenceEquals(preparedSource, artwork)) PrepareSource(artwork);
        }
        else RetireSource();
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        surface ??= compositor.CreateVisualSurface(); surface.SourceVisual = ElementCompositionPreview.GetElementVisual(source);
        var offset = TransformToVisual(source).TransformPoint(new Point());
        surface.SourceOffset = new Vector2((float)offset.X, (float)offset.Y);
        surface.SourceSize = new Vector2((float)ActualWidth, (float)ActualHeight);
        sourceBrush ??= compositor.CreateSurfaceBrush(surface);
        mask ??= compositor.CreateLinearGradientBrush(); mask.StartPoint = Vector2.Zero; mask.EndPoint = new Vector2(0, 1);
        mask.ColorStops.Clear();
        foreach (var (at, alpha) in Decoration ? new[] { (0f, (byte)255), (.45f, (byte)255), (1f, (byte)0) }
            : new[] { (0f, (byte)255), (.1f, (byte)0), (.9f, (byte)0), (1f, (byte)255) })
            mask.ColorStops.Add(compositor.CreateColorGradientStop(at, Windows.UI.Color.FromArgb(alpha, 0, 0, 0)));
        brush ??= compositor.CreateMaskBrush(); brush.Source = sourceBrush; brush.Mask = mask;
        visual ??= compositor.CreateSpriteVisual(); visual.Brush = brush; visual.Size = surface.SourceSize;
        ElementCompositionPreview.SetElementChildVisual(this, visual);
    }
    private void Release()
    {
        RetireSource();
        ElementCompositionPreview.SetElementChildVisual(this, null);
        visual?.Dispose(); brush?.Dispose(); mask?.Dispose(); sourceBrush?.Dispose(); surface?.Dispose();
        visual = null; brush = null; mask = null; sourceBrush = null; surface = null;
    }
    private async void PrepareSource(ArtworkImage artwork)
    {
        RetireSource(); preparedSource = artwork;
        var current = new CancellationTokenSource(); preparation = current;
        try
        {
            var lease = await artwork.PrepareForPresentationAsync(current.Token);
            if (ReferenceEquals(preparation, current)) sourceLease = lease;
            else lease.Dispose();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(preparation, current)) preparation = null;
            current.Dispose();
        }
    }
    private void RetireSource()
    {
        preparedSource = null;
        var pending = preparation; preparation = null;
        var lease = sourceLease; sourceLease = null;
        pending?.Cancel(); lease?.Dispose();
    }
}
