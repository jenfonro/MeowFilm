using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
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
        True(f.Scroll.ViewportHeight > 0 && f.Panel.ItemHeight > 0 && f.Panel.ItemWidth > 0,
            "the native GridView owns a finite scroll viewport and measured ItemsWrapGrid cells");
        CheckViewportMapping(f); CheckViewportRange(f);
        var firstContainers = ViewportContainers(f).ToArray();
        var firstCards = firstContainers.SelectMany(container => FilmUiTest.Visuals(container).OfType<FilmCard>()).ToArray();
        True(firstContainers.Length > 0 && firstContainers.Length < 1200, "only nearby rows are realized initially");
        f.Scroll.ChangeView(null, 18000, null, true); await f.Layout();
        True(f.Scroll.VerticalOffset > 17000 && f.Items.ContainerFromIndex(0) is null,
            "a distant restore does not retain the first row or create all intervening rows");
        CheckViewportMapping(f); CheckViewportRange(f);
        var distantContainers = ViewportContainers(f).ToArray();
        True(distantContainers.Any(firstContainers.Contains) &&
            distantContainers.SelectMany(container => FilmUiTest.Visuals(container).OfType<FilmCard>()).Any(firstCards.Contains),
            "the fixed native card template and containers are recycled rather than recreated for every row");
        var cached = distantContainers.First(container => FilmUiTest.Bounds(container, f.Scroll).Bottom < 0);
        var cachedItem = f.Items.Items[f.Items.IndexFromContainer(cached)];
        f.Items.ScrollIntoView(cachedItem, ScrollIntoViewAlignment.Leading); await f.Layout();
        var brought = (GridViewItem)f.Items.ContainerFromItem(cachedItem);
        True(FilmUiTest.Bounds(brought, f.Scroll).Top >= -.5 && ReferenceEquals(brought.Content, cachedItem),
            "native bring-into-view exposes a cached card without losing its item identity");

        f.Scroll.ChangeView(null, f.Scroll.ScrollableHeight, null, true); await f.Layout();
        True(Math.Abs(f.Scroll.VerticalOffset - f.Scroll.ScrollableHeight) < 1,
            "the full result extent reaches its actual final boundary");
        var more = ViewportMore(f); var bounds = FilmUiTest.Bounds(more, f.Scroll);
        True(DesktopVisual.IsVisible(more) && more.IsEnabled && bounds.Top >= -.5 && bounds.Bottom <= f.Scroll.ViewportHeight + .5,
            "the real more button is fully reachable in the same native scroll viewport");
        True(f.Items.Items.Count == 1200 && !f.Items.Items.Contains(more),
            "status and more belong to GridView.Footer and never become fake cards");
        CheckViewportRange(f);
    }

    private static async Task SearchViewportResize()
    {
        await using var f = await ViewportScene.Create(900);
        f.Scroll.ChangeView(null, 7200, null, true); await f.Layout();
        int anchor = f.Panel.FirstVisibleIndex;
        foreach (int width in new[] { 1180, 820, 620 })
        {
            f.Width = width; await f.Layout();
            int columns = ViewportColumns(f), first = f.Panel.FirstVisibleIndex;
            True(first <= anchor && anchor < first + columns,
                "a width change keeps the previous first film in the first visible row");
            anchor = first;
            var (_, pitch) = Shelf.MeasureCards(f.Items.ActualWidth, 180, 36);
            double expectedWidth = pitch - 36;
            var positions = ViewportContainers(f).Select(container =>
            {
                var card = FilmUiTest.Visuals(container).OfType<FilmCard>().Single();
                True(Math.Abs(card.ActualWidth - expectedWidth) < 1,
                    "search retains the responsive 144-176 DIP card-width calculation");
                return (Index: f.Items.IndexFromContainer(container), Bounds: FilmUiTest.Bounds(card, f.Scroll));
            }).ToArray();
            foreach (var item in positions)
            {
                var nextColumn = positions.Where(other => other.Index == item.Index + 1 && other.Index / columns == item.Index / columns).ToArray();
                if (nextColumn.Length == 1)
                    True(Math.Abs(nextColumn[0].Bounds.Left - item.Bounds.Right - 36) < 1,
                        "native cells retain the 36 DIP trailing card gap");
                var nextRow = positions.Where(other => other.Index == item.Index + columns).ToArray();
                if (nextRow.Length == 1)
                    True(Math.Abs(nextRow[0].Bounds.Top - item.Bounds.Bottom - 36) < 1, "row gap remains 36 DIP");
            }
            CheckViewportMapping(f); CheckViewportRange(f);
        }
        f.Scroll.ChangeView(null, f.Scroll.ScrollableHeight, null, true); await f.Layout();
        f.Width = 980; await f.Layout();
        True(Math.Abs(f.Scroll.VerticalOffset - f.Scroll.ScrollableHeight) < 1,
            "resizing at the true end retains the reachable footer instead of a stale extent");
        f.Items.Visibility = Visibility.Collapsed; f.Width = 720; await f.Layout();
        f.Items.Visibility = Visibility.Visible; await f.Layout();
        True(Math.Abs(f.Scroll.VerticalOffset - f.Scroll.ScrollableHeight) < 1,
            "returning after a hidden resize reapplies the actual viewport and end anchor");
        CheckViewportRange(f);
    }

    private static async Task SearchViewportChanges()
    {
        await using var f = await ViewportScene.Create(180);
        var first = f.Scene.Page.Cards[0]; var firstContainer = f.Items.ContainerFromIndex(0);
        var inserted = f.Scene.Page.Cards[^1]; f.Scene.Page.Cards.RemoveAt(f.Scene.Page.Cards.Count - 1);
        f.Scene.Page.Cards.Insert(0, inserted); await f.Layout();
        True(ReferenceEquals(f.Items.ContainerFromIndex(1), firstContainer) && ReferenceEquals(f.Scene.Page.Cards[1], first),
            "front insertion retains the original film's container identity");
        f.Scene.Page.Cards.Move(1, 9); f.Scene.Page.Cards.RemoveAt(2);
        var replacement = f.Scene.Page.Cards[^1]; f.Scene.Page.Cards.RemoveAt(f.Scene.Page.Cards.Count - 1);
        f.Scene.Page.Cards[3] = replacement; await f.Layout(); CheckViewportMapping(f);
        f.Scroll.ChangeView(null, 6000, null, true); await f.Layout();
        var appended = f.Scene.Page.Cards[0]; f.Scene.Page.Cards.RemoveAt(0); f.Scene.Page.Cards.Add(appended);
        await f.Layout(); CheckViewportMapping(f); CheckViewportRange(f);
        FilmCardItem? activated = null;
        f.Scene.Page.NavigateToPlaybackAsync = (item, _) => { activated = item; return Task.CompletedTask; };
        var currentContainer = ViewportContainers(f).First(container =>
        {
            var bounds = FilmUiTest.Bounds(container, f.Scroll);
            return bounds.Bottom > 0 && bounds.Top < f.Scroll.ViewportHeight;
        });
        var current = f.Scene.Page.Cards[f.Items.IndexFromContainer(currentContainer)];
        f.Items.ScrollIntoView(current); await f.Layout();
        currentContainer = (GridViewItem)f.Items.ContainerFromItem(current);
        var currentBounds = FilmUiTest.Bounds(currentContainer, f.Scroll);
        True(currentBounds.Top >= -.5 && currentBounds.Bottom <= f.Scroll.ViewportHeight + .5,
            "the current recycled card is completely visible before its semantic activation");
        DesktopTestUi.Invoke(FilmUiTest.Visuals(currentContainer).OfType<Button>().Single(button => button.Name == "HitButton"));
        await Drain();
        True(activated is not null && ReferenceEquals(activated.SearchContext, current.SearchContext),
            "a recycled card invokes its current exact selection, never its former identity");
        var restart = f.Scene.Page.Cards.Take(20).ToArray(); f.Scene.Page.Cards.Clear(); await f.Layout();
        True(!ViewportContainers(f).Any() && f.Scroll.VerticalOffset == 0 && DesktopVisual.IsVisible(ViewportMore(f)),
            "reset removes old containers and leaves the empty-state footer accessible");
        foreach (var card in restart) f.Scene.Page.Cards.Add(card);
        await f.Layout(); CheckViewportMapping(f); CheckViewportRange(f);
    }

    private static async Task SearchViewportModes()
    {
        await using var scene = await UnifiedScene.Create();
        var source = scene.Sources.Sources[0];
        var raw = Enumerable.Range(0, 600).Select(index => SearchFixture.Member(source, index.ToString(), "影片" + index)).ToArray();
        var aggregate = raw.Take(300).Select((member, index) => SearchFixture.Card("group:" + index, "site", "影片" + index, [member])).ToArray();
        scene.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), aggregate, raw);
        await scene.Submit("独立滚动", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        await SearchFixture.Until(() => scene.Page.Cards.Count == 300);
        scene.Shell.Resize(860, 760); await Drain(); scene.Shell.Root.UpdateLayout();
        var view = (SearchResultsView)scene.Page.View; var scroll = view.FilmScroll!;
        var toggle = (CheckBox)view.FindName("RawListToggle");
        scroll.ChangeView(null, 4100, null, true); await Drain(); view.UpdateLayout();
        double aggregateOffset = scroll.VerticalOffset;
        True(aggregateOffset > 4000, "aggregate has its own nonzero pixel position");
        ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)new Microsoft.UI.Xaml.Automation.Peers.CheckBoxAutomationPeer(toggle)
            .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)).Toggle();
        await SearchFixture.Until(() => scene.Page.Cards.Count == 600); await Drain(); view.UpdateLayout();
        True(scroll.VerticalOffset < 1, "the first raw visit starts at its own saved position");
        scroll.ChangeView(null, 8500, null, true); await Drain(); view.UpdateLayout();
        double rawOffset = scroll.VerticalOffset;
        True(rawOffset > 8400, "raw has a distinct nonzero pixel position");
        FilmUiTest.Click(toggle); await SearchFixture.Until(() => scene.Page.Cards.Count == 300); await Drain(); view.UpdateLayout();
        True(Math.Abs(scroll.VerticalOffset - aggregateOffset) < 1, "aggregate restores its exact independent position");
        FilmUiTest.Click(toggle); await SearchFixture.Until(() => scene.Page.Cards.Count == 600); await Drain(); view.UpdateLayout();
        True(Math.Abs(scroll.VerticalOffset - rawOffset) < 1 && scene.Runtime.SearchOpens == 1,
            "raw restores its exact position without a new search or a partial card list");
    }

    private static async Task SearchViewportArtwork()
    {
        var loader = new ViewportArtworkLoader(); await using var f = await ViewportScene.Create(900, loader);
        var initial = loader.Leases.Where(lease => lease.Disposals == 0).ToArray();
        True(initial.Length > 0, "real loaded FilmCard artwork acquires visible leases");
        int calls = loader.Leases.Count; ViewportMore(f).IsEnabled = false; f.Panel.InvalidateMeasure(); await f.Layout();
        True(loader.Leases.Count == calls && initial.All(lease => lease.Disposals == 0),
            "unrelated footer state and repeated grid layout do not reacquire unchanged artwork");
        f.Scroll.ChangeView(null, 14000, null, true); await f.Layout();
        True(initial.All(lease => lease.Disposals == 1), "old visible artwork retires exactly once after distant recycling");
        True(loader.Leases.Skip(calls).Any(lease => lease.Disposals == 0), "recycled cards acquire their current posters");
        CheckViewportMapping(f);
        f.Scene.Shell.Content = new Grid(); f.Scene.Shell.AttachRoot(); await Drain();
        True(loader.Leases.All(lease => lease.Disposals == 1), "actual viewport unloading releases visible and cached leases");
    }

    private static IEnumerable<GridViewItem> ViewportContainers(ViewportScene f) =>
        f.Panel.Children.OfType<GridViewItem>().Where(container => f.Items.IndexFromContainer(container) >= 0);
    private static int ViewportColumns(ViewportScene f) => Math.Max(1, (int)Math.Floor(f.Items.ActualWidth / f.Panel.ItemWidth));
    private static void CheckViewportRange(ViewportScene f)
    {
        int columns = ViewportColumns(f); double height = f.Panel.ItemHeight;
        var containers = ViewportContainers(f).ToArray();
        // WinUI's native cache is measured in viewport lengths, not the removed WPF panel's row count.
        double cache = f.Panel.CacheLength * f.Scroll.ViewportHeight;
        int bound = columns * ((int)Math.Ceiling((f.Scroll.ViewportHeight + 2 * cache) / height) + 2);
        True(containers.Length <= bound && containers.Length < f.Items.Items.Count,
            "native realization stays bounded by the viewport and its configured nearby cache");
        foreach (var container in containers)
        {
            var bounds = FilmUiTest.Bounds(container, f.Scroll);
            True(bounds.Bottom >= -cache - height && bounds.Top <= f.Scroll.ViewportHeight + cache + height,
                "realized containers remain near the viewport instead of accumulating at old positions");
        }
    }
    private static void CheckViewportMapping(ViewportScene f)
    {
        foreach (var container in ViewportContainers(f))
        {
            int index = f.Items.IndexFromContainer(container);
            True(ReferenceEquals(container.Content, f.Items.Items[index]) &&
                ReferenceEquals(FilmUiTest.Visuals(container).OfType<FilmCard>().Single().DataContext, f.Items.Items[index]),
                "container, card and native index agree on the current exact item");
        }
    }
    private static Button ViewportMore(ViewportScene f) =>
        FilmUiTest.Visuals((DependencyObject)f.Items.Footer).OfType<Button>().Single(button => button.Content as string == "加载更多");

    private sealed class ViewportScene : IAsyncDisposable
    {
        internal readonly UnifiedScene Scene;
        internal readonly GridView Items;
        internal ScrollViewer Scroll => ((SearchResultsView)Scene.Page.View).FilmScroll!;
        internal ItemsWrapGrid Panel => (ItemsWrapGrid)Items.ItemsPanelRoot;
        internal int Width = 860;
        private ViewportScene(UnifiedScene scene, IArtworkLoader? loader)
        {
            Scene = scene; ArtworkScope.SetLoader(Scene.Shell.Root, loader);
            Items = (GridView)scene.Page.View.FindName("FilmItems");
        }
        internal static async Task<ViewportScene> Create(int count, IArtworkLoader? loader = null)
        {
            var scene = await UnifiedScene.Create();
            try
            {
                var source = scene.Sources.Sources[0];
                var cards = Enumerable.Range(0, count).Select(index =>
                {
                    var member = SearchFixture.Member(source, index.ToString(), "影片" + index);
                    if (loader is null) return member;
                    var node = JsonNode.Parse(member.GetRawText())!.AsObject(); node["poster"] = "https://fixture.invalid/poster-" + index + ".jpg";
                    return FilmJson.Encode(node);
                }).ToArray();
                scene.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), cards, cards, hasMore: true);
                await scene.Submit("网格生命周期", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
                await SearchFixture.Until(() => scene.Page.Cards.Count == count);
                var viewport = new ViewportScene(scene, loader); await viewport.Layout(); return viewport;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal async Task Layout()
        {
            Scene.Shell.Resize(Width, 760); await Drain(); Scene.Shell.Root.UpdateLayout(); await Drain();
        }
        public ValueTask DisposeAsync() => Scene.DisposeAsync();
    }
    private sealed class ViewportArtworkLoader : IArtworkLoader
    {
        internal readonly List<ViewportArtworkLease> Leases = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var lease = new ViewportArtworkLease(); Leases.Add(lease);
            return Task.FromResult<IArtworkLease>(lease);
        }
    }
    private sealed class ViewportArtworkLease : IArtworkLease
    {
        internal int Disposals;
        public ArtworkPixels Image { get; } = new(1, 1, new byte[] { 100, 200, 250, 255 });
        public void Dispose() => Disposals++;
    }
}
