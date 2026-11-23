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

    private readonly FontIcon icon = new()
    {
        Glyph = "\uEB51",
        FontSize = 18,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private IDisposable? themeObserver;

    public bool IsFavorited
    {
        get => (bool)GetValue(IsFavoritedProperty);
        set => SetValue(IsFavoritedProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public FavoriteButton()
    {
        Width = 32;
        Height = 32;
        MinWidth = 32;
        MinHeight = 32;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(0);
        Content = icon;
        if (ManagementList.TryFindAppStyle("MeowFilm.GhostButton") is { } ghost)
            Style = ghost;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => RefreshGlyph());
        Loaded += (_, _) =>
        {
            themeObserver ??= DesktopTheme.Observe(this, RefreshGlyph);
            RefreshGlyph();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            themeObserver?.Dispose();
            themeObserver = null;
        };
        RefreshGlyph();
    }

    private void RefreshGlyph()
    {
        if (IsFavorited)
        {
            icon.Glyph = "\uEB52";
            icon.Foreground = DesktopTheme.GetIsHighContrast(this)
                ? Foreground
                : (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.FavoriteActive");
        }
        else
        {
            icon.Glyph = "\uEB51";
            icon.ClearValue(IconElement.ForegroundProperty);
        }
    }
}
