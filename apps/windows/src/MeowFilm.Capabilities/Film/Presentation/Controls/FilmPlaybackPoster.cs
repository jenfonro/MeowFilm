using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

/// <summary>Catalog artwork wrapper retaining the shared artwork lifecycle.</summary>
public sealed class FilmPlaybackPoster : Grid
{
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address), typeof(string), typeof(FilmPlaybackPoster),
        new PropertyMetadata("", (value, _) => ((FilmPlaybackPoster)value).UpdateAddress()));

    private readonly ArtworkImage artwork = new() { ShowRetry = false };

    public string Address
    {
        get => (string)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public FilmPlaybackPoster()
    {
        Children.Add(artwork);
    }

    internal static IEnumerable<ArtworkItemResource> ArtworkResources(string? address) =>
        string.IsNullOrEmpty(address) ? [] : [new("cover", address)];

    private void UpdateAddress()
    {
        artwork.Address = Address ?? "";
    }
}
