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
public sealed class MusicCatalogCardView : UserControl
{
    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(nameof(Presentation),
        typeof(MusicCatalogCardLayout), typeof(MusicCatalogCardView),
        new PropertyMetadata(MusicCatalogCardLayout.Playlist, (owner, _) => ((MusicCatalogCardView)owner).ApplyCardTemplate()));
    public MusicCatalogCardLayout Presentation { get => (MusicCatalogCardLayout)GetValue(PresentationProperty); set => SetValue(PresentationProperty, value); }

    private readonly ContentPresenter cardHost = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly MusicCatalogSlot slot;
    private MusicCatalogCard? observed;
    private MusicCatalogContext? context;
    private FavoriteButton? favoriteButton;
    private FavoriteButton? syncedFavoriteButton;
    private SongRow? syncedFavoriteSong;
    private long favoriteRequest;
    private MenuFlyout? menu;
    private MusicCatalogCardLayout? appliedPresentation;

    public MusicCatalogCardView()
    {
        Content = cardHost;
        slot = new MusicCatalogSlot(this);
        DataContextChanged += (_, _) => { menu?.Hide(); Observe(); };
        Loaded += (_, _) => { SubscribeContext(); Observe(); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            menu?.Hide();
            observed = null;
            favoriteRequest++;
            syncedFavoriteButton = null;
            syncedFavoriteSong = null;
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
        syncedFavoriteButton = null;
        syncedFavoriteSong = null;
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
        if (subscribed || context is null || Presentation != MusicCatalogCardLayout.Song) return;
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
        if (e.PropertyName == nameof(MusicPlaybackController.IsCurrentFavorite)) SyncFavorite(force: true);
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncFavorite(force: true);
    private void OnFavoritesChanged() => SyncFavorite(force: true);

    private async void SyncFavorite(bool force = false)
    {
        var button = favoriteButton;
        var card = observed;
        var currentLibrary = context?.Library;
        if (button is null || card?.Song is not { } song || currentLibrary is null)
        {
            favoriteRequest++; syncedFavoriteButton = null; syncedFavoriteSong = null;
            if (button is not null) { button.IsFavorited = false; AutomationProperties.SetName(button, "收藏"); }
            return;
        }
        bool samePair = ReferenceEquals(syncedFavoriteButton, button) && ReferenceEquals(syncedFavoriteSong, song);
        if (!force && samePair) return;
        if (!samePair) { button.IsFavorited = false; AutomationProperties.SetName(button, "收藏"); }
        syncedFavoriteButton = button; syncedFavoriteSong = song;
        long request = ++favoriteRequest;
        bool favorited;
        try { favorited = await currentLibrary.IsSongFavoritedAsync(song); }
        catch
        {
            if (request == favoriteRequest) { syncedFavoriteButton = null; syncedFavoriteSong = null; }
            return;
        }
        if (request != favoriteRequest || !IsLoaded || !ReferenceEquals(favoriteButton, button) || !ReferenceEquals(observed, card)) return;
        button.IsFavorited = favorited;
        AutomationProperties.SetName(button, favorited ? "取消收藏" : "收藏");
    }

    private void Observe()
    {
        observed = DataContext as MusicCatalogCard;
        ApplyCardTemplate();
        if (!ReferenceEquals(cardHost.Content, observed)) cardHost.Content = observed;
        SyncFavorite();
    }

    private void ApplyCardTemplate()
    {
        if (IsLoaded) { if (Presentation == MusicCatalogCardLayout.Song) SubscribeContext(); else UnsubscribeContext(); }
        if (observed is null)
        {
            if (cardHost.ContentTemplate is not null)
            {
                favoriteButton = syncedFavoriteButton = null; syncedFavoriteSong = null; appliedPresentation = null;
                cardHost.ContentTemplate = null; cardHost.Content = null;
            }
            return;
        }
        if (appliedPresentation == Presentation && cardHost.ContentTemplate is not null) return;
        favoriteButton = syncedFavoriteButton = null; syncedFavoriteSong = null;
        appliedPresentation = Presentation;
        cardHost.ContentTemplate = MusicCatalogCardTemplates.Shared.GetCardTemplate(Presentation);
        if (!ReferenceEquals(cardHost.Content, observed)) cardHost.Content = observed;
    }

    internal void HandleFavoriteButtonLoaded(object sender)
    { if (sender is FavoriteButton button) { favoriteButton = button; SyncFavorite(); } }

    private async void RunUiAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsLoaded) context?.Catalog.PublishCatalogActionError(error); }
    }

    internal void HandleOpenClick() { if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card) RunUiAction(() => owner.OpenCatalogCardAsync(card)); }
    internal void HandlePlayClick() { if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card) RunUiAction(() => owner.PlayCatalogCardAsync(card)); }
    internal void HandleFavoriteClick() { if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card) RunUiAction(() => owner.ToggleCatalogCardFavoriteAsync(card)); }
    internal void HandleHeartbeatClick() { if (context?.Catalog is { } owner && DataContext is MusicCatalogCard card) RunUiAction(() => owner.HeartbeatFavoritesCatalogCardAsync(card)); }
    internal void HandleMoreClick(object sender) { if (sender is FrameworkElement anchor && DataContext is MusicCatalogCard card) ShowMenu(anchor, card); }

    internal MenuFlyout? ShowMenu(FrameworkElement host, MusicCatalogCard card)
    {
        var currentLibrary = context?.Library;
        var currentCatalog = context?.Catalog;
        var currentSearch = context?.Search;
        if (currentLibrary is null || currentCatalog is null || currentSearch is null || card.Kind != "playlist" && card.Song is null) return null;
        menu?.Hide();
        var next = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        bool Current() => IsLoaded && ReferenceEquals(DataContext, card) && ReferenceEquals(context?.Library, currentLibrary);
        if (card.Song is not null) MusicContentMenus.PopulateSongMenu(next, currentLibrary, currentCatalog, currentSearch, card.Song, card, Current);
        else MusicContentMenus.PopulatePlaylistMenu(next, currentLibrary, currentCatalog, card, Current);
        MusicContentMenus.ApplyStyles(host, next);
        next.Closed += (_, _) => { if (ReferenceEquals(menu, next)) menu = null; };
        menu = next;
        next.ShowAt(host);
        return next;
    }
}
