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
    private IDisposable? sourceLease;
    private readonly DesktopViewport viewport;
    private FrameworkElement? sampledSource;
    private bool maskDecoration, presented;
    private ArtworkImage? preparedSource;
    private CancellationTokenSource? preparation;
    internal CompositionLinearGradientBrush? Mask => mask;
    public MusicArtworkFade()
    {
        IsHitTestVisible = false;
        viewport = new DesktopViewport(this, Refresh);
        Loaded += (_, _) => Refresh();
        SizeChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { if (IsLoaded) return; presented = false; Release(); };
    }
    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var fade = (MusicArtworkFade)owner;
        fade.presented = false; fade.Release();
        if (args.OldValue is FrameworkElement previous) { previous.SizeChanged -= fade.SourceResized; previous.Loaded -= fade.SourceLoaded; }
        if (args.NewValue is FrameworkElement next) { next.SizeChanged += fade.SourceResized; next.Loaded += fade.SourceLoaded; }
        fade.Refresh();
    }
    private void SourceLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void SourceResized(object sender, SizeChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (viewport?.IsInViewport == true) presented = true;
        if (!IsLoaded || Source is not { IsLoaded: true } source) { Release(); return; }
        if (!presented || viewport?.IsVisible != true) return;
        if (Decoration && source is ArtworkImage artwork)
        {
            if (!ReferenceEquals(preparedSource, artwork)) PrepareSource(artwork);
        }
        else RetireSource();
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        surface ??= compositor.CreateVisualSurface();
        if (!ReferenceEquals(sampledSource, source))
        { sampledSource = source; surface.SourceVisual = ElementCompositionPreview.GetElementVisual(source); }
        var offset = TransformToVisual(source).TransformPoint(new Point());
        var position = new Vector2((float)offset.X, (float)offset.Y);
        var size = new Vector2((float)ActualWidth, (float)ActualHeight);
        if (surface.SourceOffset != position) surface.SourceOffset = position;
        if (surface.SourceSize != size) surface.SourceSize = size;
        sourceBrush ??= compositor.CreateSurfaceBrush(surface);
        if (mask is null || maskDecoration != Decoration)
        {
            mask ??= compositor.CreateLinearGradientBrush(); maskDecoration = Decoration;
            mask.StartPoint = Vector2.Zero; mask.EndPoint = new Vector2(0, 1);
            mask.ColorStops.Clear();
            foreach (var (at, alpha) in Decoration ? new[] { (0f, (byte)255), (.45f, (byte)255), (1f, (byte)0) }
                : new[] { (0f, (byte)255), (.1f, (byte)0), (.9f, (byte)0), (1f, (byte)255) })
                mask.ColorStops.Add(compositor.CreateColorGradientStop(at, Windows.UI.Color.FromArgb(alpha, 0, 0, 0)));
        }
        if (brush is null) { brush = compositor.CreateMaskBrush(); brush.Source = sourceBrush; brush.Mask = mask; }
        if (visual is null)
        {
            visual = compositor.CreateSpriteVisual(); visual.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(this, visual);
        }
        if (visual.Size != size) visual.Size = size;
    }
    private void Release()
    {
        RetireSource();
        if (visual is not null) ElementCompositionPreview.SetElementChildVisual(this, null);
        visual?.Dispose(); brush?.Dispose(); mask?.Dispose(); sourceBrush?.Dispose(); surface?.Dispose();
        visual = null; brush = null; mask = null; sourceBrush = null; surface = null; sampledSource = null;
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
