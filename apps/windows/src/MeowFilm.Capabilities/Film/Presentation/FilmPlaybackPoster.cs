using System.Windows;
using System.Windows.Controls;
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

    private readonly Image bundledImage = new() { Stretch = Stretch.UniformToFill };
    private readonly ArtworkImage artwork = new() { ShowRetry = false };

    public string Address
    {
        get => (string)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public FilmPlaybackPoster()
    {
        ClipToBounds = true;
        Children.Add(bundledImage);
        Children.Add(artwork);
    }

    private void UpdateAddress()
    {
        bool bundled = Address?.StartsWith(BundledPrefix, StringComparison.Ordinal) == true;
        bundledImage.Source = bundled ? new BitmapImage(new Uri(Address!, UriKind.Absolute)) : null;
        bundledImage.Visibility = bundled ? Visibility.Visible : Visibility.Collapsed;
        artwork.Address = bundled ? "" : Address ?? "";
        artwork.Visibility = bundled ? Visibility.Collapsed : Visibility.Visible;
    }
}
