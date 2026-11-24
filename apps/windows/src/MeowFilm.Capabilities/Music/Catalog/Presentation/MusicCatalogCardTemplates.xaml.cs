using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogCardTemplates : ResourceDictionary
{
    [ThreadStatic] private static MusicCatalogCardTemplates? shared;
    internal static MusicCatalogCardTemplates Shared => shared ??= new();

    public MusicCatalogCardTemplates() => InitializeComponent();
    internal DataTemplate GetCardTemplate(MusicCatalogCardLayout layout) => (DataTemplate)this[layout + "Template"];
    internal ItemsPanelTemplate GetPanelTemplate(string key) => (ItemsPanelTemplate)this[key];

    private static MusicCatalogCardView? FindCard(object sender)
    {
        for (var node = sender as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is MusicCatalogCardView card) return card;
        return null;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => FindCard(sender)?.HandleOpenClick();
    private void Play_Click(object sender, RoutedEventArgs e) => FindCard(sender)?.HandlePlayClick();
    private void Favorite_Click(object sender, RoutedEventArgs e) => FindCard(sender)?.HandleFavoriteClick();
    private void Heartbeat_Click(object sender, RoutedEventArgs e) => FindCard(sender)?.HandleHeartbeatClick();
    private void More_Click(object sender, RoutedEventArgs e) => FindCard(sender)?.HandleMoreClick(sender);
    private void FavoriteButton_Loaded(object sender, RoutedEventArgs e) => FindCard(sender)?.HandleFavoriteButtonLoaded(sender);
}
