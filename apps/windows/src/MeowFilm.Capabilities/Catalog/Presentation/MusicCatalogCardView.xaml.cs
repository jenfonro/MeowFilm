using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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

    private MusicCatalogCard? observed;
    private MusicCatalogController? catalog;
    private MusicLibraryController? library;
    private MusicPlaybackController? playback;
    private FavoriteButton? favoriteButton;
    private long favoriteRequest;
    private MenuFlyout? menu;
    private MusicCatalogCardLayout? appliedPresentation;

    private MusicCatalogView? HostView
    {
        get
        {
            for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicCatalogView view) return view;
            return null;
        }
    }

    public MusicCatalogCardView()
    {
        InitializeComponent();
        _ = new MusicCatalogSlot(this);
        DataContextChanged += (_, _) => { menu?.Hide(); Observe(); };
        Loaded += (_, _) => { ObserveOwners(); Observe(); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            menu?.Hide();
            observed = null;
            DetachOwners();
        };
        RightTapped += (_, e) => { if (DataContext is MusicCatalogCard card) { ShowMenu(this, card); e.Handled = true; } };
    }

    internal static IEnumerable<ArtworkItemResource> ArtworkResources(MusicCatalogCard card)
    {
        yield return new("cover", card.Cover, card.Song?.Id ?? "");
        yield return new("decoration", card.Decoration);
        yield return new("first", card.FirstPreviewCover, card.FirstPreviewIdentity);
        yield return new("second", card.SecondPreviewCover, card.SecondPreviewIdentity);
        yield return new("third", card.ThirdPreviewCover, card.ThirdPreviewIdentity);
    }

    private void ObserveOwners()
    {
        var view = HostView;
        var nextCatalog = view?.Catalog;
        var nextLibrary = view?.Library;
        var nextPlayback = view?.Playback;
        if (ReferenceEquals(catalog, nextCatalog) && ReferenceEquals(library, nextLibrary) && ReferenceEquals(playback, nextPlayback)) return;
        DetachOwners();
        catalog = nextCatalog;
        library = nextLibrary;
        playback = nextPlayback;
        if (library is not null)
        {
            library.SongFavoritesChanged += OnFavoritesChanged;
            library.Playlists.CollectionChanged += OnPlaylistsChanged;
        }
        if (playback is not null)
            playback.PropertyChanged += OnPlaybackChanged;
    }

    private void DetachOwners()
    {
        if (library is not null)
        {
            library.SongFavoritesChanged -= OnFavoritesChanged;
            library.Playlists.CollectionChanged -= OnPlaylistsChanged;
        }
        if (playback is not null)
            playback.PropertyChanged -= OnPlaybackChanged;
        catalog = null;
        library = null;
        playback = null;
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
        var currentLibrary = library ?? HostView?.Library;
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

    private void SongRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border row) return;
        var hover = row.FindName("HoverSurface") as Border;
        var shade = row.FindName("PlayShade") as Border;
        var copy = row.FindName("CopyPanel") as StackPanel;
        var actions = row.FindName("ActionsPanel") as StackPanel;
        void Motion()
        {
            double progress = CardMotion.GetProgress(row);
            if (hover is not null) hover.Opacity = progress;
            if (shade is not null) shade.Opacity = progress;
            if (actions is not null)
            {
                actions.Opacity = progress;
                actions.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
                actions.IsHitTestVisible = progress > 0;
            }
            if (copy is not null) copy.Margin = new Thickness(0, 0, progress > 0 ? 72 : 0, 0);
        }
        long token = row.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => Motion());
        void OnUnloaded(object s, RoutedEventArgs args)
        {
            row.Unloaded -= OnUnloaded;
            row.UnregisterPropertyChangedCallback(CardMotion.ProgressProperty, token);
        }
        row.Unloaded += OnUnloaded;
        Motion();
    }

    private void OverlayCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid) return;
        var play = grid.FindName("PlayOverlay") as Button;
        var shade = grid.FindName("CoverShade") as Border;
        void Motion()
        {
            double progress = CardMotion.GetProgress(grid);
            if (play is not null)
            {
                play.Opacity = progress;
                play.IsHitTestVisible = progress > 0;
            }
            if (shade is not null) shade.Opacity = progress;
        }
        long token = grid.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => Motion());
        void OnUnloaded(object s, RoutedEventArgs args)
        {
            grid.Unloaded -= OnUnloaded;
            grid.UnregisterPropertyChangedCallback(CardMotion.ProgressProperty, token);
        }
        grid.Unloaded += OnUnloaded;
        Motion();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((catalog ?? HostView?.Catalog) is { } owner && DataContext is MusicCatalogCard card)
            await owner.OpenCatalogCardAsync(card);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((catalog ?? HostView?.Catalog) is { } owner && DataContext is MusicCatalogCard card)
            await owner.PlayCatalogCardAsync(card);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if ((catalog ?? HostView?.Catalog) is { } owner && DataContext is MusicCatalogCard card)
            await owner.ToggleCatalogCardFavoriteAsync(card);
    }

    private async void Heartbeat_Click(object sender, RoutedEventArgs e)
    {
        if ((catalog ?? HostView?.Catalog) is { } owner && DataContext is MusicCatalogCard card)
            await owner.HeartbeatFavoritesCatalogCardAsync(card);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor && DataContext is MusicCatalogCard card)
            ShowMenu(anchor, card);
    }

    internal MenuFlyout? ShowMenu(FrameworkElement host, MusicCatalogCard card)
    {
        var currentLibrary = library ?? HostView?.Library;
        if (currentLibrary is null || card.Kind != "playlist" && card.Song is null) return null;
        menu?.Hide();
        var next = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        bool Current() => IsLoaded && ReferenceEquals(DataContext, card) && ReferenceEquals(library ?? HostView?.Library, currentLibrary);
        if (card.Song is not null) currentLibrary.PopulateSongMenu(next, card.Song, card, Current);
        else currentLibrary.PopulatePlaylistMenu(next, card, Current);
        MusicLibraryController.ApplyContentMenuStyles(host, next);
        next.Closed += (_, _) => { if (ReferenceEquals(menu, next)) menu = null; };
        menu = next;
        next.ShowAt(host);
        return next;
    }
}
