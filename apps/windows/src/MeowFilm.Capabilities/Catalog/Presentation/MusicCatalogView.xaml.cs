using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class MusicCatalogView : UserControl
{
    private readonly UserScrollIntent scrollIntent;
    private MusicCapability? observed;
    private bool loadingMore;
    private int channelMoreRevealGeneration;
    private IReadOnlyList<MusicCatalogCard> searchPlaylistCards = [];
    private readonly ObservableCollection<MusicCatalogPlaylistRow> searchPlaylistRows = [];
    private double searchPlaylistOffset;
    private long searchPlaylistHostGeneration;
    private bool restoringSearchPlaylistOffset;
    internal ScrollViewer ActiveScroll =>
        Owner is { IsSearchPlaylists: true } ? SearchPlaylistScroll ?? CatalogScroll :
        Owner is { IsCatalogHome: true } ? HomeScroll :
        Owner is { IsSearchOverview: true } ? SearchOverviewScroll :
        Owner is { IsChannelLanding: true } ? CatalogScroll : UtilityScroll;
    private ScrollViewer? SearchPlaylistScroll
    {
        get
        {
            SearchPlaylistList.ApplyTemplate();
            return SearchPlaylistList.Template.FindName("PART_ScrollViewer", SearchPlaylistList) as ScrollViewer ??
                Descendant<ScrollViewer>(SearchPlaylistList);
        }
    }
    public MusicCatalogView()
    {
        scrollIntent = new(Dispatcher);
        InitializeComponent();
        AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, args) =>
        {
            if (IsVisible && ActiveScroll is { } scroll) scrollIntent.ObserveScroll(scroll, args);
        }), true);
        Loaded += (_, _) =>
        {
            Observe();
            RefreshSearchPlaylistHost(restoreOffset: true);
            RefreshSearchPlaylistRows();
        };
        DataContextChanged += (_, _) =>
        {
            SearchPlaylistList.ItemsSource = null;
            ResetSearchPlaylistProjection();
            if (IsLoaded) { Observe(); RefreshSearchPlaylistHost(); }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                ChannelMorePopup.IsOpen = false;
                ResetChannelMoreReveal();
                scrollIntent.Clear();
            }
            else { RefreshSearchPlaylistHost(); RefreshSearchPlaylistRows(); }
        };
        SearchPlaylistList.SizeChanged += (_, _) => RefreshSearchPlaylistRows();
        Unloaded += (_, _) =>
        {
            ChannelMorePopup.IsOpen = false;
            ResetChannelMoreReveal();
            scrollIntent.Clear();
            if (observed is not null) observed.PropertyChanged -= OnOwnerChanged;
            observed = null;
            SuspendSearchPlaylistList();
            if (Owner is not { HasSearchSession: true }) ResetSearchPlaylistProjection();
        };
    }
    private void Observe()
    {
        if (observed is not null) observed.PropertyChanged -= OnOwnerChanged;
        observed = Owner;
        if (observed is not null) observed.PropertyChanged += OnOwnerChanged;
    }
    private void OnOwnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MusicCapability.IsSearchPlaylists) or
            nameof(MusicCapability.HasSearchSession) or nameof(MusicCapability.CatalogSections))
            RefreshSearchPlaylistHost();
    }
    private void RefreshSearchPlaylistHost(bool restoreOffset = false)
    {
        bool searchPlaylists = Owner is { IsSearchPlaylists: true };
        if (!searchPlaylists)
        {
            SearchPlaylistList.Visibility = Visibility.Collapsed;
            if (Owner is not { HasSearchSession: true })
                ResetSearchPlaylistProjection();
            return;
        }
        if (Owner is not { HasSearchSession: true })
            ResetSearchPlaylistProjection();
        restoreOffset |= SearchPlaylistList.Visibility != Visibility.Visible;
        SearchPlaylistList.Visibility = Visibility.Visible;
        if (!ReferenceEquals(SearchPlaylistList.ItemsSource, searchPlaylistRows))
        {
            SearchPlaylistList.ItemsSource = searchPlaylistRows;
            restoreOffset = true;
        }
        if (restoreOffset) RestoreSearchPlaylistOffset();
        IReadOnlyList<MusicCatalogCard> cards = Owner!.CatalogSections.Count == 1
            ? Owner.CatalogSections[0].Items : [];
        if (ReferenceEquals(searchPlaylistCards, cards)) return;
        searchPlaylistCards = cards;
        RefreshSearchPlaylistRows();
    }
    private void RefreshSearchPlaylistRows()
    {
        if (Owner is not { IsSearchPlaylists: true } || !IsLoaded || !IsVisible) return;
        double viewportWidth = SearchPlaylistList.ActualWidth;
        if (viewportWidth <= 0) return;
        int columns = Math.Max(1, (int)Math.Floor(viewportWidth / 168));
        UpdateSearchPlaylistRows(columns);
    }
    private void UpdateSearchPlaylistRows(int columns)
    {
        int count = searchPlaylistCards.Count / columns +
            (searchPlaylistCards.Count % columns == 0 ? 0 : 1);
        int shared = Math.Min(searchPlaylistRows.Count, count);
        for (int index = 0; index < shared; index++)
        {
            var row = searchPlaylistRows[index];
            int start = index * columns, length = Math.Min(columns, searchPlaylistCards.Count - start);
            bool same = row.Items.Count == length;
            for (int item = 0; same && item < length; item++)
                same = ReferenceEquals(row.Items[item], searchPlaylistCards[start + item]);
            if (same) continue;
            searchPlaylistRows[index] = new(SearchPlaylistRowCards(start, length));
        }
        while (searchPlaylistRows.Count > count) searchPlaylistRows.RemoveAt(searchPlaylistRows.Count - 1);
        for (int index = searchPlaylistRows.Count; index < count; index++)
        {
            int start = index * columns, length = Math.Min(columns, searchPlaylistCards.Count - start);
            searchPlaylistRows.Add(new(SearchPlaylistRowCards(start, length)));
        }
    }
    private MusicCatalogCard[] SearchPlaylistRowCards(int start, int length)
    {
        var cards = new MusicCatalogCard[length];
        for (int index = 0; index < length; index++) cards[index] = searchPlaylistCards[start + index];
        return cards;
    }
    internal void ResetSearchPlaylistProjection()
    {
        ++searchPlaylistHostGeneration;
        restoringSearchPlaylistOffset = false;
        searchPlaylistCards = [];
        searchPlaylistOffset = 0;
        if (searchPlaylistRows.Count > 0) searchPlaylistRows.Clear();
    }
    private void SuspendSearchPlaylistList()
    {
        // A detached body can return with the same session and generated containers.
        if (SearchPlaylistList.ItemsSource is not null && !restoringSearchPlaylistOffset)
            searchPlaylistOffset = SearchPlaylistScroll?.VerticalOffset ?? searchPlaylistOffset;
        ++searchPlaylistHostGeneration;
        restoringSearchPlaylistOffset = false;
    }
    private void RestoreSearchPlaylistOffset()
    {
        long generation = ++searchPlaylistHostGeneration;
        restoringSearchPlaylistOffset = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (generation != searchPlaylistHostGeneration)
                return;
            if (!IsLoaded || !IsVisible || Owner is not { IsSearchPlaylists: true })
            {
                restoringSearchPlaylistOffset = false;
                return;
            }
            UpdateLayout();
            var scroll = SearchPlaylistScroll;
            scroll?.ScrollToVerticalOffset(searchPlaylistOffset);
            if (scroll is not null) searchPlaylistOffset = scroll.VerticalOffset;
            restoringSearchPlaylistOffset = false;
        }, DispatcherPriority.Loaded);
    }
    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) return value;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void SearchPlaylistList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsVisible || !SearchPlaylistList.IsVisible) return;
        if (e.OriginalSource is not ScrollViewer scroll || !ReferenceEquals(scroll, SearchPlaylistScroll)) return;
        if (!restoringSearchPlaylistOffset) searchPlaylistOffset = scroll.VerticalOffset;
        if (e.VerticalChange > 0 && scrollIntent.Accepts(SearchPlaylistList))
            LoadMoreSearchPlaylistsNearEnd();
    }
    private void SearchPlaylistList_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsVisible || !SearchPlaylistList.IsVisible) return;
        scrollIntent.Set(e.Delta < 0);
        if (scrollIntent.Active) LoadMoreSearchPlaylistsNearEnd();
    }
    private void SearchPlaylistList_Key(object sender, KeyEventArgs e)
    {
        if (!IsVisible || !SearchPlaylistList.IsVisible) return;
        var scroll = SearchPlaylistScroll;
        if (scroll is null) return;
        scrollIntent.Set(e.Key is Key.Down or Key.PageDown or Key.End);
        switch (e.Key)
        {
            case Key.Down: scroll.LineDown(); break;
            case Key.PageDown: scroll.PageDown(); break;
            case Key.End: scroll.ScrollToEnd(); break;
            case Key.Up: scroll.LineUp(); break;
            case Key.PageUp: scroll.PageUp(); break;
            case Key.Home: scroll.ScrollToHome(); break;
            default: return;
        }
        e.Handled = true;
        if (scrollIntent.Active) LoadMoreSearchPlaylistsNearEnd();
    }
    private async void LoadMoreSearchPlaylistsNearEnd()
    {
        if (loadingMore || !IsVisible || !SearchPlaylistList.IsVisible ||
            Owner is not { CanLoadMoreSearchPlaylists: true } owner) return;
        var scroll = SearchPlaylistScroll;
        if (scroll is null || scroll.ScrollableHeight - scroll.VerticalOffset > 280) return;
        loadingMore = true;
        scrollIntent.Clear();
        try { await owner.MoreSearchAsync(); }
        catch { }
        finally { loadingMore = false; scrollIntent.Clear(); }
    }
    private void ChannelMore_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelMorePopup.IsOpen) ChannelMorePopup.IsOpen = false;
        else
        {
            ChannelMoreClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            var width = ChannelMorePanel.ActualWidth > 0 ? ChannelMorePanel.ActualWidth : 572;
            var originWidth = Math.Min(width, Math.Max(56, ChannelMoreButton.ActualWidth));
            ChannelMoreClip.Rect = new Rect((width - originWidth) / 2, 0, originWidth, 0);
            ChannelMorePopup.IsOpen = true;
        }
    }
    private CustomPopupPlacement[] PlaceChannelMorePopup(Size popupSize, Size targetSize, Point offset) =>
        [new(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + 4), PopupPrimaryAxis.Horizontal)];
    private void ChannelMorePopup_Opened(object? sender, EventArgs e)
    {
        ChannelMoreChevron.Angle = 180;
        var generation = ++channelMoreRevealGeneration;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (generation != channelMoreRevealGeneration || !ChannelMorePopup.IsOpen) return;
            var width = ChannelMorePanel.ActualWidth;
            var height = ChannelMorePanel.ActualHeight;
            if (width <= 0 || height <= 0) return;
            var full = new Rect(0, 0, width, height);
            ChannelMoreClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            ChannelMoreClip.Rect = full;
            if (!SystemParameters.ClientAreaAnimation) return;
            var originWidth = Math.Min(width, Math.Max(56, ChannelMoreButton.ActualWidth));
            var start = new Rect((width - originWidth) / 2, 0, originWidth, 0);
            var animation = new RectAnimation(start, full, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) =>
            {
                if (generation != channelMoreRevealGeneration) return;
                ChannelMoreClip.BeginAnimation(RectangleGeometry.RectProperty, null);
                ChannelMoreClip.Rect = full;
            };
            ChannelMoreClip.BeginAnimation(RectangleGeometry.RectProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }, DispatcherPriority.Render);
    }
    private void ChannelMorePopup_Closed(object? sender, EventArgs e)
    {
        ChannelMoreChevron.Angle = 0;
        ResetChannelMoreReveal();
    }
    private void ResetChannelMoreReveal()
    {
        channelMoreRevealGeneration++;
        ChannelMoreClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        if (ChannelMorePanel.ActualWidth > 0 && ChannelMorePanel.ActualHeight > 0)
            ChannelMoreClip.Rect = new Rect(0, 0, ChannelMorePanel.ActualWidth, ChannelMorePanel.ActualHeight);
    }
    private async void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is Button { Tag: MusicCatalogTag tag })
            await owner.CatalogTagAsync(tag);
    }
    private void CatalogScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ActiveScroll) && e.VerticalChange > 0 &&
            scrollIntent.Accepts(ActiveScroll))
            LoadMoreNearEnd();
    }
    private void CatalogScroll_Wheel(object sender, MouseWheelEventArgs e)
    { scrollIntent.Set(e.Delta < 0); if (scrollIntent.Active) LoadMoreNearEnd(); }
    private void CatalogScroll_Key(object sender, KeyEventArgs e)
    {
        scrollIntent.Set(e.Key is Key.Down or Key.PageDown or Key.End);
        if (scrollIntent.Active) LoadMoreNearEnd();
    }
    private async void LoadMoreNearEnd()
    {
        if (loadingMore || !IsVisible || Owner is not { } owner ||
            !owner.IsCatalogLanding || !owner.CanCatalogNext ||
            ActiveScroll.ScrollableHeight - ActiveScroll.VerticalOffset > 280) return;
        loadingMore = true;
        scrollIntent.Clear();
        try
        {
            await owner.CatalogPageAsync(1);
        }
        catch { /* The owner publishes bounded business errors; never retry in a scroll loop. */ }
        finally { loadingMore = false; scrollIntent.Clear(); }
    }
    private void SongOptions_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner || sender is not Button { Tag: MusicCatalogCard card } button) return;
        var menu = new ContextMenu();
        void Add(string title, bool enabled, Func<Task> action)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += async (_, _) => { try { await action(); } catch { } }; menu.Items.Add(item);
        }
        Add("下一首播放", card.CanFavorite, () => owner.QueueCatalogCardAsync(card));
        Add("相似歌曲", card.CanSimilar, () => owner.SimilarCatalogCardAsync(card));
        Add("专辑", card.CanAlbum, () => owner.AlbumCatalogCardAsync(card));
        Add("心动推荐", card.CanSimilar && owner.IsCatalogPlaylist, () => owner.HeartbeatCatalogCardAsync(card));
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private MusicCapability? Owner => DataContext as MusicCapability;
    private async void SearchBack_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) await owner.ReturnToSearchAsync(); }
    private async void SearchSectionMore_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is Button { Tag: string title })
            await owner.OpenSearchTabAsync(title == "歌单" ? "playlists" : "songs");
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Owner?.CancelCatalog();
    private async void PlayAll_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) await owner.PlayCatalogPlaylistAsync(); }
    private async void Save_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) await owner.SaveCatalogPlaylistAsync(); }
    private async void Account_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) await owner.ReadCatalogAccountAsync(); }
    private async void Options_Changed(object sender, SelectionChangedEventArgs e)
    { if (IsLoaded && IsVisible && Owner is { } owner) await owner.CatalogOptionsChangedAsync(); }
    private async void BatchRefresh_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) await owner.RefreshCatalogAsync(); }
    private async void VipSign_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) try { await owner.SignVipPageAsync(); } catch { } }
    private async void VipBanner_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) try { await owner.OpenVipBannerAsync(); } catch { } }
    private async void QueueRecommendations_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner) await owner.CatalogQueueRecommendationsAsync(); }
    private async void Platform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && IsVisible && Owner is { } owner)
            try { await owner.ChangeCatalogPlatformAsync(); } catch { }
    }
    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && IsVisible && Owner is { IsSearchPage: false } owner)
            try { await owner.CatalogSearchKindAsync(); } catch { }
    }
    private async void Page_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is Button { Tag: string direction })
            try { await owner.CatalogPageAsync(int.Parse(direction, CultureInfo.InvariantCulture)); } catch { }
    }
    private async void Card_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card })
            try { ChannelMorePopup.IsOpen = false; await owner.OpenCatalogCardAsync(card); } catch { }
    }
    private async void CardPlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Owner is { } owner && sender is FrameworkElement { DataContext: MusicCatalogCard card })
            try { await owner.PlayCatalogCardAsync(card); } catch { }
    }
    private async void Entry_Open(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Owner is { } owner && sender is EntryCard { DataContext: MusicCatalogCard card })
            try { await owner.OpenCatalogCardAsync(card); } catch { }
    }
    private async void Entry_Preview(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Owner is not { } owner || sender is not EntryCard { DataContext: MusicCatalogCard card } entry) return;
        var token = entry.PreviewCancellation;
        var titles = await owner.ReadCatalogCardPreviewAsync(card, token);
        if (!token.IsCancellationRequested && entry.IsLoaded && entry.IsVisible && ReferenceEquals(entry.DataContext, card)) entry.PreviewTitles = titles;
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card })
            try { await owner.FavoriteCatalogCardAsync(card); } catch { }
    }
    private async void Queue_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card }) await owner.QueueCatalogCardAsync(card); }
    private async void Similar_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card }) await owner.SimilarCatalogCardAsync(card); }
    private async void Album_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card }) await owner.AlbumCatalogCardAsync(card); }
    private async void Heartbeat_Click(object sender, RoutedEventArgs e)
    { if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card }) await owner.HeartbeatCatalogCardAsync(card); }
}

public sealed class MusicChannelSelectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is MusicCatalogCard card && values[1] is string key && key == card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
