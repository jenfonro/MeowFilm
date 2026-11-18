using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchViewportTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.FilmSearchViewportCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> FilmSearchViewportCases()
    {
        yield return ViewportCase("full results keep only nearby recycled containers and an accessible footer", SearchViewportRange);
        yield return ViewportCase("resize keeps bounded card geometry row anchors and the true end", SearchViewportResize);
        yield return ViewportCase("insert move replace remove and reset keep the generator and click identity exact", SearchViewportChanges);
        yield return ViewportCase("raw and aggregate restore independent pixel offsets without reopening search", SearchViewportModes);
        yield return ViewportCase("scroll recycling retires old artwork leases and unchanged layout retains live leases", SearchViewportArtwork);
    }

    private static TestCase ViewportCase(string name, Func<Task> run) =>
        new("film-search-viewport: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task SearchViewportRange()
    {
        await using var f = await ViewportScene.Create(1200);
        True(f.Items.Items.Count == 1200, "virtualization does not truncate the observable cards");
        True(f.Scroll.CanContentScroll && ReferenceEquals(f.Panel.ScrollOwner, f.Scroll) &&
            Math.Abs(f.Panel.ViewportHeight - f.Scroll.ViewportHeight) < 1,
            "the actual template delegates a finite viewport to the grid IScrollInfo");
        CheckViewportMapping(f);
        CheckViewportRange(f);
        var firstContainers = ViewportContainers(f).ToArray();
        var firstCards = firstContainers.SelectMany(container => Visuals(container).OfType<FilmCard>()).ToArray();
        True(firstContainers.Length > 0 && firstContainers.Length < 1200, "only nearby rows are realized initially");
        f.Scroll.ScrollToVerticalOffset(18000);
        await f.Layout();
        True(f.Scroll.VerticalOffset > 17000 && f.Items.ItemContainerGenerator.ContainerFromIndex(0) is null,
            "a distant restore does not retain the first row or create all intervening rows");
        CheckViewportMapping(f);
        CheckViewportRange(f);
        var distantContainers = ViewportContainers(f).ToArray();
        True(distantContainers.Any(firstContainers.Contains) &&
            distantContainers.SelectMany(container => Visuals(container).OfType<FilmCard>()).Any(firstCards.Contains),
            "the actual fixed card template and its containers are recycled, not recreated for every row");
        var cached = distantContainers.First(container =>
            container.TransformToAncestor(f.Panel).TransformBounds(new Rect(container.RenderSize)).Bottom < 0);
        f.Panel.MakeVisible(cached, new Rect(cached.RenderSize));
        await f.Layout();
        True(cached.TransformToAncestor(f.Panel).TransformBounds(new Rect(cached.RenderSize)).Top >= -.5,
            "native bring-into-view can expose a clipped cached card without losing its identity");

        f.Scroll.ScrollToEnd();
        await f.Layout();
        True(Math.Abs(f.Scroll.VerticalOffset - f.Scroll.ScrollableHeight) < 1,
            "the full result extent reaches its actual final boundary");
        var more = ViewportMore(f.Panel);
        var bounds = more.TransformToAncestor(f.Panel).TransformBounds(new Rect(more.RenderSize));
        True(more.IsVisible && more.IsEnabled && bounds.Top >= -.5 && bounds.Bottom <= f.Panel.ViewportHeight + .5,
            "the real more button is fully reachable in the same scroll viewport");
        True(f.Items.Items.Count == 1200 && f.Items.ItemContainerGenerator.ContainerFromItem(more) is null,
            "status and more never become fake cards or participate in the card generator");
        CheckViewportRange(f);
    }

    private static async Task SearchViewportResize()
    {
        await using var f = await ViewportScene.Create(900);
        f.Scroll.ScrollToVerticalOffset(7200);
        await f.Layout();
        int anchor = ViewportTopIndex(f);
        foreach (int width in new[] { 1180, 820, 620 })
        {
            f.Width = width;
            await f.Layout();
            int columns = ViewportColumns(f);
            True(ViewportTopIndex(f) <= anchor && anchor < ViewportTopIndex(f) + columns,
                "a width change keeps the previous first film in the first visible row");
            anchor = ViewportTopIndex(f);
            double expectedWidth = (f.Panel.ViewportWidth + 36) / columns - 36;
            foreach (var container in ViewportContainers(f))
                True(Math.Abs(container.RenderSize.Width - expectedWidth) < 1,
                    "search fills the frontend grid with a 180 DIP minimum and 36 DIP gaps");
            var positions = ViewportContainers(f).Select(container =>
                (Index: f.Items.ItemContainerGenerator.IndexFromContainer(container),
                 Bounds: container.TransformToAncestor(f.Panel).TransformBounds(new Rect(container.RenderSize)))).ToArray();
            foreach (var item in positions)
            {
                var nextColumn = positions.Where(other => other.Index == item.Index + 1 && other.Index / columns == item.Index / columns).ToArray();
                if (nextColumn.Length == 1)
                {
                    double pitch = (f.Panel.ViewportWidth + 36) / columns;
                    True(Math.Abs(nextColumn[0].Bounds.Left - item.Bounds.Left - pitch) < 1,
                        "column pitch retains the existing trailing-gap layout");
                }
                var nextRow = positions.Where(other => other.Index == item.Index + columns).ToArray();
                if (nextRow.Length == 1)
                    True(Math.Abs(nextRow[0].Bounds.Top - item.Bounds.Bottom - 36) < 1,
                        "row gap follows the frontend's 36 DIP spacing");
            }
            CheckViewportMapping(f);
            CheckViewportRange(f);
        }
        f.Scroll.ScrollToEnd();
        await f.Layout();
        f.Width = 980;
        await f.Layout();
        True(Math.Abs(f.Scroll.VerticalOffset - f.Scroll.ScrollableHeight) < 1,
            "resizing at the true end retains the reachable footer rather than a stale pixel extent");
        f.Items.Visibility = Visibility.Collapsed;
        f.Width = 720;
        await f.Layout();
        f.Items.Visibility = Visibility.Visible;
        await f.Layout();
        True(Math.Abs(f.Scroll.VerticalOffset - f.Scroll.ScrollableHeight) < 1,
            "returning after a hidden resize reapplies the actual viewport and end anchor");
        CheckViewportRange(f);
    }

    private static async Task SearchViewportChanges()
    {
        await using var f = await ViewportScene.Create(180);
        var first = f.Scene.Page.Cards[0];
        var firstContainer = f.Items.ItemContainerGenerator.ContainerFromIndex(0);
        var inserted = f.Scene.Page.Cards[f.Scene.Page.Cards.Count - 1];
        f.Scene.Page.Cards.RemoveAt(f.Scene.Page.Cards.Count - 1);
        f.Scene.Page.Cards.Insert(0, inserted);
        await f.Layout();
        True(ReferenceEquals(f.Items.ItemContainerGenerator.ContainerFromIndex(1), firstContainer) &&
            ReferenceEquals(f.Scene.Page.Cards[1], first), "front insertion retains the original film's container identity");
        f.Scene.Page.Cards.Move(1, 9);
        f.Scene.Page.Cards.RemoveAt(2);
        var replacement = f.Scene.Page.Cards[f.Scene.Page.Cards.Count - 1];
        f.Scene.Page.Cards.RemoveAt(f.Scene.Page.Cards.Count - 1);
        f.Scene.Page.Cards[3] = replacement;
        await f.Layout();
        CheckViewportMapping(f);
        f.Scroll.ScrollToVerticalOffset(6000);
        await f.Layout();
        var appended = f.Scene.Page.Cards[0];
        f.Scene.Page.Cards.RemoveAt(0);
        f.Scene.Page.Cards.Add(appended);
        await f.Layout();
        CheckViewportMapping(f);
        CheckViewportRange(f);
        FilmCardItem? activated = null;
        f.Scene.Page.NavigateToPlaybackAsync = (item, _) => { activated = item; return Task.CompletedTask; };
        var currentContainer = ViewportContainers(f).First(container =>
        {
            var bounds = container.TransformToAncestor(f.Panel).TransformBounds(new Rect(container.RenderSize));
            return bounds.Bottom > 0 && bounds.Top < f.Panel.ViewportHeight;
        });
        f.Panel.MakeVisible(currentContainer, new Rect(currentContainer.RenderSize));
        await f.Layout();
        var currentBounds = currentContainer.TransformToAncestor(f.Panel).TransformBounds(new Rect(currentContainer.RenderSize));
        True(currentBounds.Top >= -.5 && currentBounds.Bottom <= f.Panel.ViewportHeight + .5,
            "the current card is fully visible before activation regardless of row height");
        var current = f.Scene.Page.Cards[f.Items.ItemContainerGenerator.IndexFromContainer(currentContainer)];
        Visuals(currentContainer).OfType<Button>().Single(button => button.Name == "HitButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Drain();
        True(activated is not null && ReferenceEquals(activated.SearchContext, current.SearchContext),
            "a recycled card invokes only its current exact selection, not its former identity");

        var restart = f.Scene.Page.Cards.Take(20).ToArray();
        f.Scene.Page.Cards.Clear();
        await f.Layout();
        True(ViewportContainers(f).Count() == 0 && f.Scroll.VerticalOffset == 0 &&
            f.Panel.ExtentHeight > 0 && ViewportMore(f.Panel).IsVisible,
            "reset removes old containers and leaves the empty-state footer accessible");
        foreach (var card in restart) f.Scene.Page.Cards.Add(card);
        await f.Layout();
        CheckViewportMapping(f);
        CheckViewportRange(f);
    }

    private static async Task SearchViewportModes()
    {
        await using var scene = await UnifiedScene.Create();
        var source = scene.Sources.Sources[0];
        var raw = Enumerable.Range(0, 600).Select(index =>
            SearchFixture.Member(source, index.ToString(), "影片" + index)).ToArray();
        var aggregate = raw.Take(300).Select((member, index) =>
            SearchFixture.Card("group:" + index, "site", "影片" + index, [member])).ToArray();
        scene.Runtime.SearchOpen = (id, input) =>
            SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), aggregate, raw);
        await scene.Submit("独立滚动", "film");
        await SearchFixture.Settle(scene.Film.Search.Current!);
        await SearchFixture.Until(() => scene.Page.Cards.Count == 300);
        using var hwnd = SearchRenderSource(scene.Shell, out var root);
        SearchLayout(root, 860, 760);
        await Drain();
        var view = (SearchResultsView)scene.Page.View;
        var scroll = (ScrollViewer)view.FindName("FilmScroll");
        var toggle = (CheckBox)view.FindName("RawListToggle");
        scroll.ScrollToVerticalOffset(4100);
        await Drain(); SearchLayout(root, 860, 760);
        double aggregateOffset = scroll.VerticalOffset;
        True(aggregateOffset > 4000, "aggregate has its own nonzero pixel position");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        await SearchFixture.Until(() => scene.Page.Cards.Count == 600);
        await Drain(); SearchLayout(root, 860, 760);
        True(scroll.VerticalOffset < 1, "the first raw visit starts at its own saved position");
        scroll.ScrollToVerticalOffset(8500);
        await Drain(); SearchLayout(root, 860, 760);
        double rawOffset = scroll.VerticalOffset;
        True(rawOffset > 8400, "raw has a distinct nonzero pixel position");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        await SearchFixture.Until(() => scene.Page.Cards.Count == 300);
        await Drain(); SearchLayout(root, 860, 760);
        True(Math.Abs(scroll.VerticalOffset - aggregateOffset) < 1, "aggregate restores its exact independent position");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        await SearchFixture.Until(() => scene.Page.Cards.Count == 600);
        await Drain(); SearchLayout(root, 860, 760);
        True(Math.Abs(scroll.VerticalOffset - rawOffset) < 1 && scene.Runtime.SearchOpens == 1,
            "raw restores its exact position without a new search or partial card list");
        hwnd.RootVisual = null;
    }

    private static async Task SearchViewportArtwork()
    {
        var loader = new ViewportArtworkLoader();
        await using var f = await ViewportScene.Create(900, loader);
        var initial = loader.Leases.Where(lease => lease.Disposals == 0).ToArray();
        True(initial.Length > 0, "real loaded FilmCard artwork acquires visible leases");
        int calls = loader.Leases.Count;
        var more = ViewportMore(f.Panel);
        more.IsEnabled = false;
        f.Panel.InvalidateMeasure();
        await f.Layout();
        True(loader.Leases.Count == calls && initial.All(lease => lease.Disposals == 0),
            "unrelated footer state and repeated grid layout do not reacquire unchanged artwork");
        f.Scroll.ScrollToVerticalOffset(14000);
        await f.Layout();
        True(initial.All(lease => lease.Disposals == 1), "all old visible artwork leases retire exactly once after distant recycling");
        True(loader.Leases.Skip(calls).Any(lease => lease.Disposals == 0),
            "recycled cards acquire the new visible posters rather than retain old pixels");
        CheckViewportMapping(f);
        f.Hwnd.RootVisual = null;
        await Drain();
        True(loader.Leases.All(lease => lease.Disposals == 1),
            "unloading the search viewport releases remaining visible and cached artwork leases");
    }

    private static IEnumerable<ContentPresenter> ViewportContainers(ViewportScene f) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(f.Panel))
            .Select(index => VisualTreeHelper.GetChild(f.Panel, index))
            .OfType<ContentPresenter>()
            .Where(container => f.Items.ItemContainerGenerator.IndexFromContainer(container) >= 0);

    private static int ViewportColumns(ViewportScene f) =>
        Math.Max(1, (int)Math.Floor((f.Panel.ViewportWidth + 36) / (180 + 36)));

    private static int ViewportTopIndex(ViewportScene f)
    {
        double height = ViewportContainers(f).First().RenderSize.Height;
        return (int)Math.Floor(f.Scroll.VerticalOffset / (height + 36)) * ViewportColumns(f);
    }

    private static void CheckViewportRange(ViewportScene f)
    {
        int columns = ViewportColumns(f);
        var containers = ViewportContainers(f).ToArray();
        double height = containers.First().RenderSize.Height;
        int bound = columns * ((int)Math.Ceiling(f.Panel.ViewportHeight / (height + 36)) + 3);
        True(containers.Length <= bound, "realized card count is bounded by the viewport plus adjacent cache rows");
        foreach (var container in containers)
        {
            var bounds = container.TransformToAncestor(f.Panel).TransformBounds(new Rect(container.RenderSize));
            True(bounds.Bottom >= -height - 72 && bounds.Top <= f.Panel.ViewportHeight + height + 72,
                "realized containers stay near the viewport rather than accumulating at old positions");
        }
    }

    private static void CheckViewportMapping(ViewportScene f)
    {
        foreach (var container in ViewportContainers(f))
        {
            int index = f.Items.ItemContainerGenerator.IndexFromContainer(container);
            True(ReferenceEquals(container.DataContext, f.Items.Items[index]) &&
                ReferenceEquals(Visuals(container).OfType<FilmCard>().Single().DataContext, f.Items.Items[index]),
                "container, card and generator agree on the current exact item after layout");
        }
    }

    private static Button ViewportMore(FilmSearchVirtualizingPanel panel) =>
        Visuals(panel).OfType<Button>().Single(button => button.Content as string == "加载更多");

    private sealed class ViewportScene : IAsyncDisposable
    {
        internal readonly UnifiedScene Scene;
        internal readonly HwndSource Hwnd;
        internal readonly FrameworkElement Root;
        internal readonly ItemsControl Items;
        internal readonly ScrollViewer Scroll;
        internal FilmSearchVirtualizingPanel Panel => Visuals(Items).OfType<FilmSearchVirtualizingPanel>().Single();
        internal int Width = 860;

        private ViewportScene(UnifiedScene scene, IArtworkLoader? loader)
        {
            Scene = scene;
            Hwnd = SearchRenderSource(scene.Shell, out Root, Width, 760);
            ArtworkScope.SetLoader(Root, loader);
            var view = (SearchResultsView)scene.Page.View;
            Items = (ItemsControl)view.FindName("FilmItems");
            Scroll = (ScrollViewer)view.FindName("FilmScroll");
        }

        internal static async Task<ViewportScene> Create(int count, IArtworkLoader? loader = null)
        {
            var scene = await UnifiedScene.Create();
            ViewportScene? viewport = null;
            try
            {
                var source = scene.Sources.Sources[0];
                var cards = Enumerable.Range(0, count).Select(index =>
                {
                    var member = SearchFixture.Member(source, index.ToString(), "影片" + index);
                    if (loader is null) return member;
                    var node = JsonNode.Parse(member.GetRawText())!.AsObject();
                    node["poster"] = "https://fixture.invalid/poster-" + index + ".jpg";
                    return FilmJson.Encode(node);
                }).ToArray();
                scene.Runtime.SearchOpen = (id, input) =>
                    SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), cards, cards, hasMore: true);
                await scene.Submit("网格生命周期", "film");
                await SearchFixture.Settle(scene.Film.Search.Current!);
                await SearchFixture.Until(() => scene.Page.Cards.Count == count);
                await Drain();
                viewport = new ViewportScene(scene, loader);
                await viewport.Layout();
                return viewport;
            }
            catch
            {
                if (viewport is not null) await viewport.DisposeAsync();
                else await scene.DisposeAsync();
                throw;
            }
        }

        internal async Task Layout()
        {
            SearchLayout(Root, Width, 760);
            await Drain();
            SearchLayout(Root, Width, 760);
            await Drain();
        }

        public async ValueTask DisposeAsync()
        {
            Hwnd.RootVisual = null;
            Hwnd.Dispose();
            await Scene.DisposeAsync();
        }
    }

    private sealed class ViewportArtworkLoader : IArtworkLoader
    {
        internal readonly List<ViewportArtworkLease> Leases = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new ViewportArtworkLease();
            Leases.Add(lease);
            return Task.FromResult<IArtworkLease>(lease);
        }
    }

    private sealed class ViewportArtworkLease : IArtworkLease
    {
        internal int Disposals;
        public BitmapSource Image { get; } = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 100, 200, 250, 255 }, 4);
        public void Dispose() => Disposals++;
    }
}
