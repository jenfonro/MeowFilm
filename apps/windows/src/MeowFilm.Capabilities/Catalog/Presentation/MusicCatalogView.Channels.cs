using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Capabilities;

public partial class MusicCatalogView
{
    private void ChannelMore_Click(object sender, RoutedEventArgs e)
    {
        ChannelMoreContent.DataContext = Owner;
        ChannelMoreContent.Width = Math.Min(540, Math.Max(0, XamlRoot.Size.Width - 60));
        ChannelMorePopup.ShowAt(ChannelMoreButton);
    }
    private void ChannelMore_Opened(object? sender, object e)
        => ChannelMoreChevron.Angle = 180;
    private void ChannelMore_Closed(object? sender, object e) => ChannelMoreChevron.Angle = 0;

    private void Channel_Loaded(object sender, RoutedEventArgs e) =>
        ((MusicCatalogButton)sender).SetBinding(MusicCatalogButton.ChannelKeyProperty,
            new Binding { Source = this, Path = new PropertyPath($"{nameof(DataContext)}.{nameof(MusicCapability.CurrentChannelKey)}") });
}
