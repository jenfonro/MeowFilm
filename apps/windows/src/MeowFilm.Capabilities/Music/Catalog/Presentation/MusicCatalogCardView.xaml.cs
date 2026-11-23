using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public enum MusicCatalogCardLayout { Playlist, RankCover, Row, AlbumRank, Banner, Ranking, Song, Artist }

// Card layout owns only its native presentation and delegates every action to domain controllers.
public partial class MusicCatalogCardView : UserControl
{
    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(nameof(Presentation),
        typeof(MusicCatalogCardLayout), typeof(MusicCatalogCardView),
        new PropertyMetadata(MusicCatalogCardLayout.Playlist, (owner, _) => ((MusicCatalogCardView)owner).ApplyCardTemplate()));
    public MusicCatalogCardLayout Presentation { get => (MusicCatalogCardLayout)GetValue(PresentationProperty); set => SetValue(PresentationProperty, value); }

    private readonly MusicCatalogSlot slot;
    private MusicCatalogCard? observed;
    private MusicCatalogContext? context;
    private FavoriteButton? favoriteButton;
    private long favoriteRequest;
    private MenuFlyout? menu;
    private MusicCatalogCardLayout? appliedPresentation;

    public MusicCatalogCardView()
    {
        InitializeComponent();
        slot = new MusicCatalogSlot(this);
        DataContextChanged += (_, _) => { menu?.Hide(); Observe(); };
        Loaded += (_, _) => { SubscribeContext(); Observe(); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            menu?.Hide();
            observed = null;
            UnsubscribeContext();
        };
        RightTapped += (_, e) => { if (DataContext is MusicCatalogCard card) { ShowMenu(this, card); e.Handled = true; } };
    }

    internal void AttachContext(MusicCatalogContext? next)
    {
        if (ReferenceEquals(context, next)) return;
        UnsubscribeContext();
        context = next;
        slot.SetContext(next);
        if (IsLoaded) SubscribeContext();
        SyncFavorite();
    }

    internal static IEnumerable<ArtworkItemResource> ArtworkResources(MusicCatalogCard card)
    {
        yield return new("cover", card.Cover, card.Song?.Id ?? "");
        yield return new("decoration", card.Decoration);
        yield return new("first", card.FirstPreviewCover, card.FirstPreviewIdentity);
        yield return new("second", card.SecondPreviewCover, card.SecondPreviewIdentity);
        yield return new("third", card.ThirdPreviewCover, card.ThirdPreviewIdentity);
    }

    private bool subscribed;

    private void SubscribeContext()
    {
        if (subscribed || context is null) return;
        subscribed = true;
        context.Library.SongFavoritesChanged += OnFavoritesChanged;
        context.Library.Playlists.CollectionChanged += OnPlaylistsChanged;
        context.Playback.PropertyChanged += OnPlaybackChanged;
    }

    private void UnsubscribeContext()
    {
        if (!subscribed || context is null) return;
        subscribed = false;
        context.Library.SongFavoritesChanged -= OnFavoritesChanged;
        context.Library.Playlists.CollectionChanged -= OnPlaylistsChanged;
        context.Playback.PropertyChanged -= OnPlaybackChanged;
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlaybackController.IsCurrentFavorite)) SyncFavorite();
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncFavorite();
    private void OnFavoritesChanged() => SyncFavorite();

    private async void SyncFavorite()
    {
        var button = favoriteButton;
        var card = observed;
        var currentLibrary = context?.Library;
        if (button is null || card?.Song is not { } song || currentLibrary is null) return;
        long request = ++favoriteRequest;
        bool favorited = await currentLibrary.IsSongFavoritedAsync(song);
        if (request != favoriteRequest || !IsLoaded || !ReferenceEquals(favoriteButton, button) || !ReferenceEquals(observed, card)) return;
        button.IsFavorited = favorited;
        AutomationProperties.SetName(button, favorited ? "取消收藏" : "收藏");
    }

    private void Observe()
    {
        var next = DataContext as MusicCatalogCard;
        bool sameCard = ReferenceEquals(observed, next);
        observed = next;
        ApplyCardTemplate();
        if (!sameCard) CardHost.Content = next;
        SyncFavorite();
    }

    private void ApplyCardTemplate()
    {
        if (observed is null)
        {
            if (CardHost.ContentTemplate is not null)
            {
                favoriteButton = null;
                appliedPresentation = null;
                CardHost.ContentTemplate = null;
                CardHost.Content = null;
            }
            return;
        }
        if (appliedPresentation == Presentation && CardHost.ContentTemplate is not null) return;
        favoriteButton = null;
        appliedPresentation = Presentation;
        CardHost.ContentTemplate = (DataTemplate)Resources[Presentation + "Template"];
        CardHost.Content = observed;
    }

    private void FavoriteButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FavoriteButton button) return;
        favoriteButton = button;
        SyncFavorite();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card)
            await owner.OpenCatalogCardAsync(card);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card)
            await owner.PlayCatalogCardAsync(card);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card)
            await owner.ToggleCatalogCardFavoriteAsync(card);
    }

    private async void Heartbeat_Click(object sender, RoutedEventArgs e)
    {
        if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card)
            await owner.HeartbeatFavoritesCatalogCardAsync(card);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor && DataContext is MusicCatalogCard card)
            ShowMenu(anchor, card);
    }

    internal MenuFlyout? ShowMenu(FrameworkElement host, MusicCatalogCard card)
    {
        var currentLibrary = context?.Library;
        if (currentLibrary is null || card.Kind != "playlist" && card.Song is null) return null;
        menu?.Hide();
        var next = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        bool Current() => IsLoaded && ReferenceEquals(DataContext, card) && ReferenceEquals(context?.Library, currentLibrary);
        if (card.Song is not null) currentLibrary.PopulateSongMenu(next, card.Song, card, Current);
        else currentLibrary.PopulatePlaylistMenu(next, card, Current);
        MusicLibraryController.ApplyContentMenuStyles(host, next);
        next.Closed += (_, _) => { if (ReferenceEquals(menu, next)) menu = null; };
        menu = next;
        next.ShowAt(host);
        return next;
    }
}
