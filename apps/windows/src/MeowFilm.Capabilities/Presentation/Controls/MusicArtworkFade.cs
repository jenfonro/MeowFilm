using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
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
    private IDisposable? visibility, artworkLease;
    private ArtworkImage? demandedArtwork;
    private CancellationTokenSource? preparation;
    internal CompositionLinearGradientBrush? Mask => mask;
    public MusicArtworkFade()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { visibility ??= DesktopVisual.ObserveVisibility(this, _ => Refresh()); Refresh(); };
        SizeChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { visibility?.Dispose(); visibility = null; RetireArtwork(); Release(); };
    }
    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var fade = (MusicArtworkFade)owner;
        if (args.OldValue is FrameworkElement previous) { previous.SizeChanged -= fade.SourceResized; previous.Loaded -= fade.SourceLoaded; previous.Unloaded -= fade.SourceUnloaded; }
        if (args.NewValue is FrameworkElement next) { next.SizeChanged += fade.SourceResized; next.Loaded += fade.SourceLoaded; next.Unloaded += fade.SourceUnloaded; }
        fade.Refresh();
    }
    private void SourceLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void SourceUnloaded(object sender, RoutedEventArgs e) { RetireArtwork(); Release(); }
    private void SourceResized(object sender, SizeChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || Source is not { IsLoaded: true } source || ActualWidth <= 0 || ActualHeight <= 0)
        { RetireArtwork(); Release(); return; }
        var artwork = Decoration ? source as ArtworkImage : null;
        if (!ReferenceEquals(artwork, demandedArtwork))
        {
            RetireArtwork(); demandedArtwork = artwork;
            if (artwork is not null)
            {
                preparation = new CancellationTokenSource();
                PrepareArtwork(artwork, preparation);
            }
        }
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
    private async void PrepareArtwork(ArtworkImage artwork, CancellationTokenSource current)
    {
        try
        {
            var acquired = await artwork.PrepareForPresentationAsync(current.Token);
            if (ReferenceEquals(preparation, current) && ReferenceEquals(demandedArtwork, artwork) && !current.IsCancellationRequested)
                artworkLease = acquired;
            else acquired.Dispose();
        }
        // ArtworkImage also revokes preparation when its own loaded lifetime ends.
        // That cancellation can complete before this consumer handles Unloaded.
        catch (OperationCanceledException) when (current.IsCancellationRequested || !artwork.IsLoaded || !IsLoaded) { }
        finally
        {
            if (ReferenceEquals(preparation, current)) preparation = null;
            current.Dispose();
        }
    }
    private void RetireArtwork()
    {
        var pending = preparation; preparation = null;
        demandedArtwork = null; pending?.Cancel();
        artworkLease?.Dispose(); artworkLease = null;
    }
    private void Release()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        visual?.Dispose(); brush?.Dispose(); mask?.Dispose(); sourceBrush?.Dispose(); surface?.Dispose();
        visual = null; brush = null; mask = null; sourceBrush = null; surface = null;
    }
}
