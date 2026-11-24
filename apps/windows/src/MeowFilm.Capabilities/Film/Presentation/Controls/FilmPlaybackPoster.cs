using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

/// <summary>Catalog artwork wrapper retaining the shared artwork lifecycle.</summary>
public sealed class FilmPlaybackPoster : Grid
{
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address), typeof(string), typeof(FilmPlaybackPoster),
        new PropertyMetadata("", (value, _) => ((FilmPlaybackPoster)value).UpdateAddress()));

    private readonly ArtworkImage artwork = new() { ShowRetry = false };
    private readonly RectangleGeometry clip = new();

    public string Address
    {
        get => (string)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public FilmPlaybackPoster()
    {
        Clip = clip;
        SizeChanged += (_, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        Children.Add(artwork);
    }

    internal static IEnumerable<ArtworkItemResource> ArtworkResources(string? address) =>
        string.IsNullOrEmpty(address) ? [] : [new("cover", address)];

    private void UpdateAddress()
    {
        artwork.Address = Address ?? "";
    }
}
