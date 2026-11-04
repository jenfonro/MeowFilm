using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SearchPlaylistPresentationCases()
    {
        yield return Case("search playlist virtualization preserves visible rows containers and offset on append", async () =>
        {
            await using var f = await CreateSearchPlaylistPresentationScene(240);
            for (int round = 2; round <= 6; round++)
            {
                await f.Module.MoreSearchAsync();
                await SearchUntil(() => f.Module.CatalogSections.Single().Items.Count == round * 30 &&
                    f.Module.CanLoadMoreSearchPlaylists);
                Equal(round, f.Http.SearchRequests);
            }

            var window = SearchPlaylistWindow(f.Module.View, 1042, 700);
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var list = SearchPlaylistList(f.Module.View);
                var scroll = SearchPlaylistScrollViewer(list);
                var source = list.ItemsSource;
                True(source is ObservableCollection<MusicCatalogPlaylistRow>,
                    "the playlist row source remains an observable collection");
                True(ScrollViewer.GetCanContentScroll(list), "the search playlist list uses its item scroller");
                Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
                Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
                True(VirtualizingPanel.GetIsVirtualizing(list), "the search playlist list is virtualizing");
                True(scroll.ScrollableHeight > 0, "the realized viewport can actually scroll");
                var containers = SearchPlaylistContainers(list);
                True(containers.Length > 0, "the virtualized list has real generated containers");
                int totalRows = list.Items.Count;
                True(containers.Length < totalRows,
                    $"realized rows {containers.Length} are fewer than total rows {totalRows}");
                AssertSearchPlaylistRows(list, f.Module, 180);

                double requestedOffset = Math.Min(scroll.ScrollableHeight, scroll.ViewportHeight);
                scroll.ScrollToVerticalOffset(requestedOffset); await Drain(); window.UpdateLayout();
                double actualOffset = scroll.VerticalOffset;
                True(actualOffset > 0 && actualOffset <= scroll.ScrollableHeight + .01,
                    "the anchor uses the actual clamped scroll offset");
                containers = SearchPlaylistContainers(list);
                True(containers.Length > 0, "a real container is available before taking the anchor");
                var anchorContainer = VisibleSearchPlaylistContainer(containers, scroll) ??
                    throw new InvalidOperationException("the anchor container intersects the playlist viewport");
                int anchorIndex = list.ItemContainerGenerator.IndexFromContainer(anchorContainer);
                True(anchorIndex >= 0, "the anchor container has a generated item index");
                var anchorRow = (MusicCatalogPlaylistRow)list.Items[anchorIndex]!;
                var firstRow = (MusicCatalogPlaylistRow)list.Items[0]!;

                await f.Module.MoreSearchAsync();
                await SearchUntil(() => f.Module.CatalogSections.Single().Items.Count == 210 &&
                    f.Module.CanLoadMoreSearchPlaylists);
                window.UpdateLayout(); await Drain();
                Equal(7, f.Http.SearchRequests);
                AssertSearchPlaylistRows(list, f.Module, 210);
                True(ReferenceEquals(firstRow, list.Items[0]), "an append preserves the first row object");
                True(ReferenceEquals(anchorRow, list.Items[anchorIndex]), "an append preserves the anchored row object");
                True(ReferenceEquals(anchorContainer, list.ItemContainerGenerator.ContainerFromIndex(anchorIndex)),
                    "an append preserves the visible generated container");
                True(ReferenceEquals(source, list.ItemsSource), "an append does not replace ItemsSource");
                True(Math.Abs(scroll.VerticalOffset - actualOffset) < 1,
                    $"an append preserves the actual offset {actualOffset}, not the requested offset {requestedOffset}");
                True(SearchPlaylistContainers(list).Length < list.Items.Count, "append remains virtualized");
                AssertVisiblePlaylistCardsBelongToCurrentSection(list, f.Module);

                var artwork = Visuals(list).OfType<ArtworkImage>().ToArray();
                await f.Module.OpenSearchTabAsync("overview"); await Drain(); window.UpdateLayout();
                True(ReferenceEquals(source, list.ItemsSource), "a hidden playlist host keeps its row source");
                True(artwork.SequenceEqual(Visuals(list).OfType<ArtworkImage>()), "hidden artwork instances are retained");
                True(artwork.All(image => !image.IsVisible), "retained artwork is inactive while hidden");
                await f.Module.OpenSearchTabAsync("songs"); await Drain(); window.UpdateLayout();
                True(ReferenceEquals(source, list.ItemsSource), "songs tab retains the playlist row source");
                await f.Module.OpenSearchTabAsync("playlists");
                await SearchUntil(() => ReferenceEquals(source, list.ItemsSource) &&
                    Math.Abs(scroll.VerticalOffset - actualOffset) < 1);
                window.UpdateLayout(); await Drain();
                True(ReferenceEquals(source, list.ItemsSource),
                    "returning to playlists reuses the same stable row source");
                True(Math.Abs(scroll.VerticalOffset - actualOffset) < 1,
                    "returning to playlists restores the actual offset");
                True(ReferenceEquals(anchorContainer, list.ItemContainerGenerator.ContainerFromIndex(anchorIndex)),
                    "tab return retains the visible container");
                int reads = f.Http.SearchRequests;
                string searchLocation = f.Module.CaptureNavigationLocation();
                await f.Module.NavigateAsync("catalog-home", default); await Drain();
                await f.Module.RestoreNavigationAsync(searchLocation, default); await Drain(); window.UpdateLayout();
                Equal(reads, f.Http.SearchRequests);
                True(f.Module.IsSearchPlaylists, "history restores the selected search tab");
                True(ReferenceEquals(source, list.ItemsSource), "history retains the list source");
                True(Math.Abs(scroll.VerticalOffset - actualOffset) < 1, "history retains actual scroll");
                True(artwork.SequenceEqual(Visuals(list).OfType<ArtworkImage>()), "history retains image controls");
                f.Module.View.Visibility = Visibility.Collapsed; await Drain();
                RaiseSearchPlaylistKey(list, window, Key.End); await Drain();
                Equal(reads, f.Http.SearchRequests);
                f.Module.View.Visibility = Visibility.Visible; await Drain();
            }
            finally { window.Content = null; window.Close(); }
        });

        yield return Case("search playlist rows reflow by actual width without layout or tab paging", async () =>
        {
            await using var f = await CreateSearchPlaylistPresentationScene(90);
            var window = SearchPlaylistWindow(f.Module.View, 1042, 700);
            ObservableCollection<MusicCatalogPlaylistRow>? rowSource = null;
            int resets = 0;
            void OnRowSourceReset(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    resets++;
            }
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var list = SearchPlaylistList(f.Module.View);
                var scroll = SearchPlaylistScrollViewer(list);
                var source = list.ItemsSource;
                True(source is ObservableCollection<MusicCatalogPlaylistRow>,
                    "the initial row source is an observable collection");
                rowSource = (ObservableCollection<MusicCatalogPlaylistRow>)source;
                rowSource.CollectionChanged += OnRowSourceReset;
                int columns = Math.Max(1, (int)Math.Floor(list.ActualWidth / 168d));
                AssertSearchPlaylistRows(list, f.Module, 30);

                scroll.ScrollToEnd(); await Drain(); window.UpdateLayout();
                await SearchUntil(() => f.Module.CanLoadMoreSearchPlaylists);
                Equal(1, f.Http.SearchRequests);

                double oldWidth = list.ActualWidth;
                window.Width = 802;
                await SearchUntil(() => list.ActualWidth < oldWidth - 1);
                window.UpdateLayout(); await Drain();
                int nextColumns = Math.Max(1, (int)Math.Floor(list.ActualWidth / 168d));
                True(nextColumns < columns, $"the narrower viewport changes columns from {columns} to {nextColumns}");
                AssertSearchPlaylistRows(list, f.Module, 30);
                True(ReferenceEquals(source, list.ItemsSource), "a reflow does not replace ItemsSource");

                await f.Module.OpenSearchTabAsync("overview"); await Drain();
                True(ReferenceEquals(source, list.ItemsSource), "hidden playlist host retains its containers");
                True(Visuals(list).OfType<ArtworkImage>().All(image => !image.IsVisible),
                    "retained artwork is hidden");
                await f.Module.OpenSearchTabAsync("songs"); await Drain();
                True(ReferenceEquals(source, list.ItemsSource), "songs tab retains the playlist row source");
                await f.Module.OpenSearchTabAsync("playlists"); await Drain(); window.UpdateLayout();
                AssertSearchPlaylistRows(list, f.Module, 30);
                True(ReferenceEquals(source, list.ItemsSource),
                    "returning to playlists retains the same row source");
                Equal(1, f.Http.SearchRequests);
                True(Equals(0, resets), "row reflow updates the existing collection without Reset");

                True(scroll.VerticalOffset > 0,
                    "the old keyword has a real non-top playlist offset before replacement");
                await f.Search("新的歌单关键词"); await Drain();
                True(f.Module.IsSearchOverview, "a new keyword starts from the overview");
                True(Equals(0, list.Items.Count), "a new search releases the previous hidden playlist projection");
                True(!list.IsVisible, "a new search keeps the emptied playlist host hidden");
                await f.Module.OpenSearchTabAsync("playlists"); await Drain(); window.UpdateLayout();
                AssertSearchPlaylistRows(list, f.Module, 30);
                True(ReferenceEquals(source, list.ItemsSource),
                    "a new search reuses the stable playlist row source");
                True(Math.Abs(scroll.VerticalOffset) < 1,
                    "a new keyword starts its playlist view at the top, not the old keyword offset");
                Equal(2, f.Http.SearchRequests);
            }
            finally
            {
                if (rowSource is not null) rowSource.CollectionChanged -= OnRowSourceReset;
                window.Content = null; window.Close();
            }
        });

        yield return Case("search playlist paging requires a downward user gesture at the bottom", async () =>
        {
            await using var f = await CreateSearchPlaylistPresentationScene(120);
            var window = SearchPlaylistWindow(f.Module.View, 802, 700);
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var list = SearchPlaylistList(f.Module.View);
                var scroll = SearchPlaylistScrollViewer(list);
                True(SearchPlaylistContainers(list).Length > 0, "the fixture has generated playlist rows");
                AssertSearchPlaylistRows(list, f.Module, 30);

                scroll.ScrollToEnd(); await Drain(); window.UpdateLayout();
                await SearchUntil(() => f.Module.CanLoadMoreSearchPlaylists);
                Equal(1, f.Http.SearchRequests);

                double bottom = scroll.VerticalOffset;
                RaiseSearchPlaylistMouseWheel(list, window, 120); await Drain(); window.UpdateLayout();
                True(scroll.VerticalOffset < bottom, "an upward wheel gesture really scrolls up");
                await SearchUntil(() => f.Module.CanLoadMoreSearchPlaylists);
                Equal(1, f.Http.SearchRequests);

                double afterWheel = scroll.VerticalOffset;
                RaiseSearchPlaylistKey(list, window, Key.Up); await Drain(); window.UpdateLayout();
                True(scroll.VerticalOffset < afterWheel, "the upward key really scrolls up");
                await SearchUntil(() => f.Module.CanLoadMoreSearchPlaylists);
                Equal(1, f.Http.SearchRequests);

                double beforeEnd = scroll.VerticalOffset;
                RaiseSearchPlaylistKey(list, window, Key.End);
                await SearchUntil(() => f.Http.SearchRequests == 2 &&
                    f.Module.CatalogSections.Single().Items.Count == 60 && f.Module.CanLoadMoreSearchPlaylists);
                window.UpdateLayout(); await Drain();
                True(scroll.VerticalOffset > beforeEnd, "the End key really scrolls down");
                AssertSearchPlaylistRows(list, f.Module, 60);
                await Drain();
                Equal(2, f.Http.SearchRequests);

                double wheelOffset = Math.Max(0, scroll.ScrollableHeight - 100);
                scroll.ScrollToVerticalOffset(wheelOffset); await Drain(); window.UpdateLayout();
                await SearchUntil(() => f.Module.CanLoadMoreSearchPlaylists);
                Equal(2, f.Http.SearchRequests);
                RaiseSearchPlaylistMouseWheel(list, window, -120);
                await SearchUntil(() => f.Http.SearchRequests == 3 &&
                    f.Module.CatalogSections.Single().Items.Count == 90 && f.Module.CanLoadMoreSearchPlaylists);
                window.UpdateLayout(); await Drain();
                True(scroll.VerticalOffset > wheelOffset, "the downward wheel gesture really scrolls down");
                AssertSearchPlaylistRows(list, f.Module, 90);
                await Drain();
                Equal(3, f.Http.SearchRequests);
            }
            finally { window.Content = null; window.Close(); }
        });
    }

    private static async Task<SearchScene> CreateSearchPlaylistPresentationScene(int playlistCount)
    {
        var f = await SearchScene.Create();
        try
        {
            await f.Policy([], ["wy"]);
            f.Http.SearchResult = count => new { code = 200, result = new { playlistCount,
                playlists = SearchPlaylistPresentationPage(count, 30).ToArray() } };
            await f.Search("歌单分页");
            await f.Module.OpenSearchTabAsync("playlists");
            await SearchUntil(() => f.Module.CatalogSections.Count == 1 &&
                f.Module.CatalogSections.Single().Items.Count == 30 && f.Module.CanLoadMoreSearchPlaylists);
            Equal(1, f.Http.SearchRequests);
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    private static IEnumerable<object> SearchPlaylistPresentationPage(int requestIndex, int pageSize)
    {
        int start = 500 + (requestIndex - 1) * pageSize;
        for (int index = 0; index < pageSize; index++)
            yield return new
            {
                id = (start + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                name = "歌单" + requestIndex + "-" + index,
                trackCount = 1
            };
    }

    private static Window SearchPlaylistWindow(UIElement content, double width, double height) => new()
    {
        Content = content, Width = width, Height = height, Left = -10000, Top = -10000,
        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
    };

    private static ListBox SearchPlaylistList(UIElement view) =>
        Visuals(view).OfType<MusicCatalogView>().Single().FindName("SearchPlaylistList") as ListBox ??
            throw new InvalidOperationException("search playlist list is unavailable");

    private static ScrollViewer SearchPlaylistScrollViewer(ListBox list) =>
        Visuals(list).OfType<ScrollViewer>().Single(scroll => scroll.ScrollableHeight > 0);

    private static ListBoxItem[] SearchPlaylistContainers(ListBox list) =>
        Visuals(list).OfType<ListBoxItem>().Where(item => item.IsVisible && item.ActualHeight > 0).ToArray();

    private static ListBoxItem? VisibleSearchPlaylistContainer(IEnumerable<ListBoxItem> containers, ScrollViewer scroll)
    {
        var viewport = new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight);
        return containers.FirstOrDefault(container =>
        {
            var bounds = container.TransformToAncestor(scroll).TransformBounds(new Rect(container.RenderSize));
            return bounds.IntersectsWith(viewport);
        });
    }

    private static void AssertSearchPlaylistRows(ListBox list, MusicCapability module, int cardCount)
    {
        int columns = Math.Max(1, (int)Math.Floor(list.ActualWidth / 168d));
        int rowCount = (cardCount + columns - 1) / columns;
        Equal(rowCount, list.Items.Count);
        var sectionCards = module.CatalogSections.Single().Items;
        Equal(cardCount, sectionCards.Count);
        for (int row = 0; row < list.Items.Count; row++)
        {
            var playlistRow = (MusicCatalogPlaylistRow)list.Items[row]!;
            int start = row * columns;
            int length = Math.Min(columns, sectionCards.Count - start);
            Equal(length, playlistRow.Items.Count);
            for (int item = 0; item < length; item++)
                True(ReferenceEquals(sectionCards[start + item], playlistRow.Items[item]),
                    $"row {row} item {item} references the current section card");
        }
    }

    private static void AssertVisiblePlaylistCardsBelongToCurrentSection(ListBox list, MusicCapability module)
    {
        var visibleCards = Visuals(list).OfType<Button>()
            .Select(button => button.DataContext as MusicCatalogCard)
            .Where(card => card is not null).ToArray();
        True(visibleCards.Length > 0, "the virtualized viewport has generated playlist cards");
        var sectionCards = module.CatalogSections.Single().Items;
        True(visibleCards.All(card => sectionCards.Any(sectionCard => ReferenceEquals(sectionCard, card))),
            "every generated card is owned by the current section");
    }

    private static void RaiseSearchPlaylistMouseWheel(ListBox target, Window window, int delta)
    {
        _ = PresentationSource.FromVisual(window) ??
            throw new InvalidOperationException("search playlist wheel presentation source");
        var scroll = SearchPlaylistScrollViewer(target);
        var origin = SearchPlaylistWheelOrigin(target, scroll);
        bool hasScrollerAncestor = false, hasListAncestor = false;
        var current = (DependencyObject)origin;
        while (current is not null)
        {
            hasScrollerAncestor |= ReferenceEquals(current, scroll);
            hasListAncestor |= ReferenceEquals(current, target);
            current = VisualTreeHelper.GetParent(current);
        }
        True(hasScrollerAncestor && hasListAncestor,
            "the search playlist wheel origin routes through the list scroller and list");
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        origin.RaiseEvent(args);
        if (args.Handled) return;
        args.RoutedEvent = Mouse.MouseWheelEvent;
        origin.RaiseEvent(args);
    }

    private static Button SearchPlaylistWheelOrigin(ListBox target, ScrollViewer scroll) =>
        Visuals(target).OfType<Button>()
            .First(button => button.DataContext is MusicCatalogCard && button.IsVisible &&
                button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize))
                    .IntersectsWith(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight)));

    private static void RaiseSearchPlaylistKey(UIElement target, Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window) ??
            throw new InvalidOperationException("search playlist key presentation source");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
}
