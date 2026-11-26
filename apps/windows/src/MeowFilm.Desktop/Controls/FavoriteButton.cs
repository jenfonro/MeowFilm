using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

/// <summary>
/// Single application-wide favorite icon button matching the playlist song-row heart glyph.
/// </summary>
public sealed class FavoriteButton : Button
{
    public static readonly DependencyProperty IsFavoritedProperty = DependencyProperty.Register(
        nameof(IsFavorited), typeof(bool), typeof(FavoriteButton),
        new PropertyMetadata(false, (owner, _) => ((FavoriteButton)owner).RefreshGlyph()));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FavoriteButton),
        new PropertyMetadata(18d, (owner, args) => ((FavoriteButton)owner).icon.FontSize = (double)args.NewValue));

    [ThreadStatic] private static Brush? activeBrush;
    private readonly FontIcon icon = new()
    {
        Glyph = "\uEB51", FontSize = 18,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private IDisposable? themeObserver;

    public bool IsFavorited { get => (bool)GetValue(IsFavoritedProperty); set => SetValue(IsFavoritedProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    public FavoriteButton()
    {
        Width = Height = MinWidth = MinHeight = 32;
        Padding = Margin = BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(16);
        Content = icon;
        if (DesktopVisual.TryAppStyle("MeowFilm.GhostButton") is { } ghost) Style = ghost;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => { if (IsFavorited) RefreshGlyph(); });
        Loaded += (_, _) =>
        {
            themeObserver?.Dispose();
            themeObserver = DesktopTheme.Observe(this, RefreshGlyph);
        };
        Unloaded += (_, _) => { if (IsLoaded) return; themeObserver?.Dispose(); themeObserver = null; };
        RefreshGlyph();
    }

    private void RefreshGlyph()
    {
        if (IsFavorited)
        {
            icon.Glyph = "\uEB52";
            icon.Foreground = DesktopTheme.GetIsHighContrast(this)
                ? Foreground
                : activeBrush ??= (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.FavoriteActive");
        }
        else
        {
            icon.Glyph = "\uEB51";
            icon.ClearValue(IconElement.ForegroundProperty);
        }
    }
}
