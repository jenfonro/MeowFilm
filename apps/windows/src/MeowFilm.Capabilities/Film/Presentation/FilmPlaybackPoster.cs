using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

/// <summary>Bundled preview posters stay local; catalog URLs retain the shared artwork lifecycle.</summary>
public sealed class FilmPlaybackPoster : Grid
{
    private const string PreviewPrefix =
        "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/";
    // Catalog artwork does not expose its decoded size; film posters are treated as two-to-three.
    private const double PosterRatio = 2d / 3;
    private const double FeatherLeft = .42, FeatherRight = .16, FeatherTop = .16, FeatherBottom = .30;

    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address), typeof(string), typeof(FilmPlaybackPoster),
        new PropertyMetadata("", (value, _) => ((FilmPlaybackPoster)value).UpdateAddress()));

    /// <summary>Backdrop posters keep their ratio against the right edge, fade on every side and never size their host.</summary>
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

    private readonly Image preview = new() { Stretch = Stretch.UniformToFill };
    private readonly ArtworkImage artwork = new() { ShowRetry = false };
    private Size maskSize;
    private double maskRatio;

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
        Children.Add(preview);
        Children.Add(artwork);
        SetBinding(ArtworkPaletteProperty, new Binding(nameof(ArtworkImage.RawPaletteBrush))
            { Source = artwork, Mode = BindingMode.OneWay });
    }

    private void UpdateAddress()
    {
        bool bundled = Address?.StartsWith(PreviewPrefix, StringComparison.Ordinal) == true;
        // Clear first: a cached catalog image may publish its palette while the address is assigned.
        ClearValue(PaletteBrushPropertyKey);
        preview.Source = bundled ? new BitmapImage(new Uri(Address!, UriKind.Absolute)) : null;
        preview.Visibility = bundled ? Visibility.Visible : Visibility.Collapsed;
        artwork.Address = bundled ? "" : Address ?? "";
        artwork.Visibility = bundled ? Visibility.Collapsed : Visibility.Visible;
        UpdateBackdrop();
    }

    private void UpdateBackdrop()
    {
        if (!IsBackdrop)
        {
            preview.ClearValue(OpacityMaskProperty);
            artwork.ClearValue(OpacityMaskProperty);
            ClearValue(OpacityMaskProperty);
            ClearValue(PaletteBrushPropertyKey);
            maskSize = default;
            return;
        }
        if (preview.Source is BitmapSource local)
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
        if (IsBackdrop && preview.Source is null && artwork.RawPaletteBrush is SolidColorBrush brush &&
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
        double ratio = preview.Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } local
            ? (double)local.PixelWidth / local.PixelHeight
            : PosterRatio;
        // Fit the panel height at the poster ratio, anchored right; a wider poster is cropped from its left.
        double width = arrangeSize.Height * ratio;
        var slot = new Rect(arrangeSize.Width - width, 0, width, arrangeSize.Height);
        foreach (UIElement child in InternalChildren) child.Arrange(slot);
        if (width > 0 && arrangeSize.Width > 0 && (arrangeSize != maskSize || ratio != maskRatio))
        {
            maskSize = arrangeSize;
            maskRatio = ratio;
            // Child coordinates start at the slot; the horizontal fade spans only the visible poster part.
            OpacityMask = Feather(new Point(0, 0), new Point(0, arrangeSize.Height), FeatherTop, FeatherBottom);
            preview.OpacityMask = artwork.OpacityMask = Feather(
                new Point(Math.Max(0, -slot.X), 0), new Point(width, 0), FeatherLeft, FeatherRight);
        }
        return arrangeSize;
    }

    // Absolute mapping in element coordinates; five stops approximate a smoothstep edge.
    private static Brush Feather(Point start, Point end, double first, double last)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end };
        double[] alpha = [0, .156, .5, .844, 1];
        for (int i = 0; i < alpha.Length; i++)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha[i] * 255), 0, 0, 0), first * i / 4));
        for (int i = alpha.Length - 1; i >= 0; i--)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha[i] * 255), 0, 0, 0), 1 - last * i / 4));
        brush.Freeze();
        return brush;
    }
}
