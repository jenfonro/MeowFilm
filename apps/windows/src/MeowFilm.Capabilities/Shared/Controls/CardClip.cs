using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace MeowFilm.Capabilities;

// A native geometry clip on the existing visual; no offscreen capture or second surface.
public static class CardClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(CardClip), new PropertyMetadata(0d, Changed));
    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double radius) => element.SetValue(RadiusProperty, radius);
    private static readonly ConditionalWeakTable<FrameworkElement, CompositionRoundedRectangleGeometry> Geometry = new();
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not FrameworkElement element) return;
        if (!Geometry.TryGetValue(element, out _))
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            Geometry.Add(element, geometry); visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
            element.SizeChanged += (_, _) => Refresh(element);
        }
        Refresh(element);
    }
    private static void Refresh(FrameworkElement element)
    {
        var geometry = Geometry.GetValue(element, _ => throw new InvalidOperationException());
        geometry.Size = new Vector2((float)Math.Max(0, element.ActualWidth), (float)Math.Max(0, element.ActualHeight));
        float radius = (float)Math.Max(0, Math.Min(GetRadius(element), Math.Min(element.ActualWidth, element.ActualHeight) / 2));
        geometry.CornerRadius = new Vector2(radius);
    }
}
