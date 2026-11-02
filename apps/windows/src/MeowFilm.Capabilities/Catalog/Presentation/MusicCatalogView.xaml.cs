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
    private MusicCatalogCard? lastDefault;
    private bool defaultScheduled;
    private bool loadingMore;
    private int channelMoreRevealGeneration;
    public MusicCatalogView()
    {
        scrollIntent = new(Dispatcher);
        InitializeComponent();
        Loaded += (_, _) => Observe();
        DataContextChanged += (_, _) => { if (IsLoaded) Observe(); };
        Unloaded += (_, _) =>
        {
            ChannelMorePopup.IsOpen = false;
            ResetChannelMoreReveal();
            scrollIntent.Clear();
            if (observed is not null) observed.PropertyChanged -= OnOwnerChanged;
            observed = null;
        };
    }
    private void Observe()
    {
        if (observed is not null) observed.PropertyChanged -= OnOwnerChanged;
        observed = Owner;
        if (observed is not null) observed.PropertyChanged += OnOwnerChanged;
        ScheduleDefaultChannel();
    }
    private void OnOwnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MusicCapability.HasChannelTabs) or nameof(MusicCapability.CanCancelCatalog)) ScheduleDefaultChannel(); }
    private void ScheduleDefaultChannel()
    {
        if (!IsVisible || Owner is not { } owner) return;
        if (!owner.IsChannelDirectory) return; // Keep the consumed snapshot on Back; do not bounce forward again.
        if (owner.CanCancelCatalog || defaultScheduled) return;
        var card = owner.ChannelTabs.FirstOrDefault(c => c.CanOpen);
        if (card is null || ReferenceEquals(card, lastDefault)) return;
        defaultScheduled = true;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            defaultScheduled = false;
            if (!IsVisible || !ReferenceEquals(Owner, owner) || !owner.IsChannelDirectory || owner.CanCancelCatalog || !owner.ChannelTabs.Contains(card)) return;
            lastDefault = card; // One attempt per directory snapshot; errors never trigger a retry loop.
            try { await owner.OpenCatalogCardAsync(card); } catch { }
        }, System.Windows.Threading.DispatcherPriority.Background);
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
        if (ReferenceEquals(e.OriginalSource, CatalogScroll) && e.VerticalChange > 0 &&
            scrollIntent.Accepts(CatalogScroll))
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
            (!owner.IsCatalogLanding || !owner.CanCatalogNext) && !owner.CanLoadMoreSearchPlaylists ||
            CatalogScroll.ScrollableHeight - CatalogScroll.VerticalOffset > 280) return;
        loadingMore = true;
        scrollIntent.Clear();
        try
        {
            if (owner.CanLoadMoreSearchPlaylists) await owner.MoreSearchAsync();
            else await owner.CatalogPageAsync(1);
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
    { if (IsLoaded && Owner is { } owner) await owner.CatalogOptionsChangedAsync(); }
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
        if (IsLoaded && Owner is { } owner)
            try { await owner.ChangeCatalogPlatformAsync(); } catch { }
    }
    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && Owner is { IsSearchPage: false } owner)
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
        if (!token.IsCancellationRequested && entry.IsLoaded && ReferenceEquals(entry.DataContext, card)) entry.PreviewTitles = titles;
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
