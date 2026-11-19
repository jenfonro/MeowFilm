using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using System.Numerics;

namespace MeowFilm.Desktop;

/// <summary>Blurs only the supplied background visual inside the same compositor.</summary>
public sealed class SidebarBackdrop : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(FrameworkElement), typeof(SidebarBackdrop), new PropertyMetadata(null, Changed));
    public FrameworkElement? Source { get => (FrameworkElement?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    private FrameworkElement? observedSource;
    private IDisposable? visibility, sourceVisibility;
    private CompositionVisualSurface? surface;
    private CompositionSurfaceBrush? sourceBrush;
    private CompositionEffectBrush? effectBrush;
    private SpriteVisual? visual;
    private ContainerVisual? container;
    private InsetClip? clip;
    public SidebarBackdrop()
    {
        Loaded += (_, _) =>
        {
            ObserveSource();
            visibility = DesktopVisual.ObserveVisibility(this, visible => { if (visible) Refresh(); else Release(); });
        };
        SizeChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { visibility?.Dispose(); visibility = null; ObserveSource(null); Release(); };
    }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var backdrop = (SidebarBackdrop)owner;
        backdrop.ObserveSource();
        backdrop.Refresh();
    }
    private void ObserveSource() => ObserveSource(IsLoaded ? Source : null);
    private void ObserveSource(FrameworkElement? source)
    {
        if (ReferenceEquals(observedSource, source)) return;
        sourceVisibility?.Dispose(); sourceVisibility = null;
        if (observedSource is { } previous)
        {
            previous.SizeChanged -= SourceChanged;
        }
        observedSource = source;
        if (source is not null)
        {
            source.SizeChanged += SourceChanged;
            sourceVisibility = DesktopVisual.ObserveVisibility(source, visible => { if (visible) Refresh(); else Release(); });
        }
    }
    private void SourceChanged(object sender, SizeChangedEventArgs args) => Refresh();
    private void Refresh()
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) ||
            Source is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } source ||
            !DesktopVisual.IsVisible(source) || ActualWidth <= 0 || ActualHeight <= 0) { Release(); return; }
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        if (surface is null)
        {
            surface = compositor.CreateVisualSurface();
            sourceBrush = compositor.CreateSurfaceBrush(surface);
            sourceBrush.Stretch = CompositionStretch.None;
            sourceBrush.HorizontalAlignmentRatio = sourceBrush.VerticalAlignmentRatio = 0;
            using var effect = new GaussianBlurEffect
            {
                BlurAmount = 12,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Speed,
                Source = new CompositionEffectSourceParameter("Background")
            };
            using var factory = compositor.CreateEffectFactory(effect);
            effectBrush = factory.CreateBrush();
            effectBrush.SetSourceParameter("Background", sourceBrush);
            visual = compositor.CreateSpriteVisual();
            visual.Brush = effectBrush;
            clip = compositor.CreateInsetClip();
            container = compositor.CreateContainerVisual();
            container.Clip = clip;
            container.Children.InsertAtTop(visual);
            ElementCompositionPreview.SetElementChildVisual(this, container);
        }
        // Blur the whole dedicated background before restricting its output to the sidebar.
        // Cropping the input would restart the blur kernel at the sidebar's edge.
        surface.SourceVisual = ElementCompositionPreview.GetElementVisual(source);
        surface.SourceSize = new Vector2((float)source.ActualWidth, (float)source.ActualHeight);
        var offset = source.TransformToVisual(this).TransformPoint(new Point());
        visual!.Size = surface.SourceSize;
        visual.Offset = new Vector3((float)offset.X, (float)offset.Y, 0);
        container!.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
    }
    private void Release()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        container?.Children.RemoveAll();
        visual?.Dispose(); container?.Dispose(); clip?.Dispose();
        effectBrush?.Dispose(); sourceBrush?.Dispose(); surface?.Dispose();
        visual = null; container = null; clip = null; effectBrush = null; sourceBrush = null; surface = null;
    }
}
