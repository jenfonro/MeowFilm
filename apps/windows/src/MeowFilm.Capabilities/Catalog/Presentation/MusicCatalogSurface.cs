using System.Windows;

namespace MeowFilm.Capabilities;

// Inherited presentation roles belong to the retained host, not the current navigation route.
public static class MusicCatalogSurface
{
    public static readonly DependencyProperty IsHomeProperty = DependencyProperty.RegisterAttached(
        "IsHome", typeof(bool), typeof(MusicCatalogSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsHome(DependencyObject value) => (bool)value.GetValue(IsHomeProperty);
    public static void SetIsHome(DependencyObject value, bool home) => value.SetValue(IsHomeProperty, home);
    public static readonly DependencyProperty IsLandingProperty = DependencyProperty.RegisterAttached(
        "IsLanding", typeof(bool), typeof(MusicCatalogSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsLanding(DependencyObject value) => (bool)value.GetValue(IsLandingProperty);
    public static void SetIsLanding(DependencyObject value, bool landing) => value.SetValue(IsLandingProperty, landing);
    public static readonly DependencyProperty IsOverviewProperty = DependencyProperty.RegisterAttached(
        "IsOverview", typeof(bool), typeof(MusicCatalogSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsOverview(DependencyObject value) => (bool)value.GetValue(IsOverviewProperty);
    public static void SetIsOverview(DependencyObject value, bool overview) => value.SetValue(IsOverviewProperty, overview);
}
