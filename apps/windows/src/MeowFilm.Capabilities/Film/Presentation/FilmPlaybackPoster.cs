using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

/// <summary>Explicit bundled artwork stays local; catalog URLs retain the shared artwork lifecycle.</summary>
public sealed class FilmPlaybackPoster : Grid
{
    private const string BundledPrefix =
        "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/";

    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address), typeof(string), typeof(FilmPlaybackPoster),
        new PropertyMetadata("", (value, _) => ((FilmPlaybackPoster)value).UpdateAddress()));

    /// <summary>Backdrop posters fill their host at the original ratio without sizing the foreground.</summary>
    public static readonly DependencyProperty IsBackdropProperty = DependencyProperty.Register(
        nameof(IsBackdrop), typeof(bool), typeof(FilmPlaybackPoster),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (value, _) => ((FilmPlaybackPoster)value).UpdateBackdrop()));

    private static readonly DependencyPropertyKey PaletteBrushPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PaletteBrush), typeof(SolidColorBrush), typeof(FilmPlaybackPoster), new PropertyMetadata(null));

    /// <summary>Average poster color of a backdrop, retained while catalog artwork is offscreen; null until known.</summary>
    public static readonly DependencyProperty PaletteBrushProperty = PaletteBrushPropertyKey.DependencyProperty;

    private static readonly DependencyProperty ArtworkPaletteProperty = DependencyProperty.Register(
        "ArtworkPalette", typeof(Brush), typeof(FilmPlaybackPoster),
        new PropertyMetadata(null, (value, _) => ((FilmPlaybackPoster)value).LatchArtworkPalette()));

    private readonly Image bundledImage = new() { Stretch = Stretch.UniformToFill };
    private readonly ArtworkImage artwork = new() { ShowRetry = false };

    public string Address
    {
        get => (string)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public bool IsBackdrop
    {
        get => (bool)GetValue(IsBackdropProperty);
        set => SetValue(IsBackdropProperty, value);
    }

    public SolidColorBrush? PaletteBrush => (SolidColorBrush?)GetValue(PaletteBrushProperty);

    public FilmPlaybackPoster()
    {
        ClipToBounds = true;
        Children.Add(bundledImage);
        Children.Add(artwork);
        SetBinding(ArtworkPaletteProperty, new Binding(nameof(ArtworkImage.RawPaletteBrush))
            { Source = artwork, Mode = BindingMode.OneWay });
    }

    private void UpdateAddress()
    {
        bool bundled = Address?.StartsWith(BundledPrefix, StringComparison.Ordinal) == true;
        // Clear first: a cached catalog image may publish its palette while the address is assigned.
        ClearValue(PaletteBrushPropertyKey);
        bundledImage.Source = bundled ? new BitmapImage(new Uri(Address!, UriKind.Absolute)) : null;
        bundledImage.Visibility = bundled ? Visibility.Visible : Visibility.Collapsed;
        artwork.Address = bundled ? "" : Address ?? "";
        artwork.Visibility = bundled ? Visibility.Collapsed : Visibility.Visible;
        UpdateBackdrop();
    }

    private void UpdateBackdrop()
    {
        if (!IsBackdrop)
        {
            ClearValue(PaletteBrushPropertyKey);
            return;
        }
        if (bundledImage.Source is BitmapSource local)
        {
            // Fully transparent images sample to the shared DimGray placeholder: no poster color.
            if (ArtworkImage.SamplePalette(local).Raw is SolidColorBrush sampled && !ReferenceEquals(sampled, Brushes.DimGray))
                SetValue(PaletteBrushPropertyKey, sampled);
        }
        else
            LatchArtworkPalette();
        // Child desired sizes stay empty in backdrop mode, so a new source ratio must re-arrange explicitly.
        InvalidateArrange();
    }

    private void LatchArtworkPalette()
    {
        // Retiring offscreen artwork resets to the shared DimGray placeholder; keep the last real color.
        if (IsBackdrop && bundledImage.Source is null && artwork.RawPaletteBrush is SolidColorBrush brush &&
            !ReferenceEquals(brush, Brushes.DimGray))
            SetValue(PaletteBrushPropertyKey, brush);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!IsBackdrop) return base.MeasureOverride(constraint);
        foreach (UIElement child in InternalChildren) child.Measure(default);
        return default;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (!IsBackdrop) return base.ArrangeOverride(arrangeSize);
        var slot = new Rect(arrangeSize);
        if (bundledImage.Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } local)
        {
            double scale = Math.Max(arrangeSize.Width / local.PixelWidth, arrangeSize.Height / local.PixelHeight);
            double width = local.PixelWidth * scale, height = local.PixelHeight * scale;
            slot = new Rect((arrangeSize.Width - width) / 2, (arrangeSize.Height - height) / 2, width, height);
        }
        bundledImage.Arrange(slot);
        // ArtworkImage owns the decoded catalog size and its UniformToFill crop; no guessed poster ratio.
        artwork.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
