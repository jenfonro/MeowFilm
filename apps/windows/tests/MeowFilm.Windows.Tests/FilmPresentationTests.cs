using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class FilmPresentationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-presentation: first load uses card skeletons and results or errors end them",
            () => MusicQualityConsumerTests.Sta(SkeletonPresentation));
        yield return new("film-presentation: transparent four-rail home matches card and paging interactions",
            () => MusicQualityConsumerTests.Sta(HomePresentation));
        yield return new("film-presentation: more opens data while all cards open the playback page",
            () => MusicQualityConsumerTests.Sta(DataNavigation));
    }

    private static async Task SkeletonPresentation()
    {
        var source = new GatedFilmSource();
        await using var module = new FilmCapability(source);
        var view = (FilmView)module.View;
        var window = WindowFor(view, 1160, 760);
        try
        {
            await module.InitializeAsync(default);
            window.Show();
            window.UpdateLayout();
            await Drain();
            Check(source.Calls == 0, "mounting the initial page does not read business data");
            var abandoned = module.NavigateAsync(FilmCapability.HomeRoute, default);
            view.Visibility = Visibility.Collapsed;
            await abandoned;
            Check(source.Calls == 0 && module.Sections.All(section => !section.ShowSkeleton),
                "hiding before queued source reads cancels them and finishes loading state");
            view.Visibility = Visibility.Visible;
            await Drain();
            Check(source.Calls == 0, "visibility alone does not start business reads");
            var navigation = module.RestoreNavigationAsync(FilmCapability.HomeRoute, default);
            await Drain();
            var skeletons = Visuals(view).OfType<LoadingSkeleton>().Where(item => item.IsVisible).ToArray();
            Check(skeletons.Length > 0 && !Visuals(view).OfType<FilmCard>().Any(),
                "first pending load presents image-free poster and title placeholders");
            source.Completion.SetResult();
            await navigation;
            window.UpdateLayout();
            await Drain();
            Check(skeletons.All(item => !item.IsVisible && !item.IsSweeping),
                "success and failure both remove active skeletons");
            Check(module.Sections.Sum(section => section.Items.Count) == 3 &&
                module.Sections.Single(section => section.Key == "anime").CanRetry,
                "real result counts replace placeholders and errors retain retry");
        }
        finally { source.Completion.TrySetResult(); window.Content = null; window.Close(); }
    }

    private sealed class GatedFilmSource : IFilmHomeSource
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async Task<IReadOnlyList<FilmCardItem>> LoadAsync(
            FilmSectionDefinition section, int start, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            await Completion.Task.WaitAsync(cancellationToken);
            if (section.Key == "anime") throw new InvalidOperationException("fixture failure");
            return [new FilmCardItem(section.Key, section.Title, "", "")];
        }
        public void Dispose() { }
    }

    private static async Task HomePresentation()
    {
        await using var module = new FilmCapability(new ImmediateFilmSource());
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var view = (FilmView)module.View;
        var window = WindowFor(view, 1160, 760);
        try
        {
            window.Show();
            window.UpdateLayout();
            await Drain();

            Check(view.Background is SolidColorBrush { Color.A: 0 }, "page background is transparent");
            Check(module.Sections.Select(section => section.Title)
                .SequenceEqual(["热门电影", "热门剧集", "新番放送", "热门综艺"]),
                "fixed section order");
            var shelves = Visuals(view).OfType<FilmShelf>().ToArray();
            Check(shelves.Length == 4 && shelves.All(shelf =>
                ((ListBox)shelf.FindName("ItemsHost")).Items.Count == 12), "four twelve-card rails");
            var more = Visuals(view).OfType<Button>()
                .Where(button => button.Content as string == "查看更多").ToArray();
            var sharedHeading = (Style)view.FindResource("MeowFilm.SectionHeading");
            var sectionTitles = Visuals(view).OfType<TextBlock>()
                .Where(text => module.Sections.Any(section => section.Title == text.Text))
                .ToArray();
            Check(more.Length == 4 && more.All(button =>
                AutomationProperties.GetName(button).StartsWith("查看更多 ", StringComparison.Ordinal)),
                "each section has one accessible more action");
            Check(sectionTitles.Length == 4 &&
                sectionTitles.All(title => ReferenceEquals(title.Style, sharedHeading)),
                "film section typography reuses the shared music heading role");
            foreach (var button in more)
            {
                button.ApplyTemplate();
                var frame = Visuals(button).OfType<Border>().First();
                Check(button.BorderThickness == new Thickness(1) && button.MinHeight == 34 &&
                    frame.CornerRadius == new CornerRadius(17) &&
                    Visuals(button).OfType<System.Windows.Shapes.Path>().Any() &&
                    ReferenceEquals(button.Style.BasedOn, view.FindResource("MeowFilm.Button")),
                    "more action derives shared button typography and keeps its direction affordance");
            }

            var card = Visuals(shelves[0]).OfType<FilmCard>().First();
            Check(Math.Abs(card.ActualWidth - 176) < .5 && Math.Abs(card.ActualHeight - 294) < .5,
                "desktop card dimensions");
            var poster = (Grid)card.FindName("PosterFrame");
            var score = (Border)card.FindName("ScoreBadge");
            var hitButton = (Button)card.FindName("HitButton");
            var shade = (Border)card.FindName("HoverShade");
            Check(Math.Abs(poster.ActualWidth - 176) < .5 && Math.Abs(poster.ActualHeight - 264) < .5,
                "poster keeps the two-to-three ratio");
            Check(Math.Abs(score.ActualWidth - 32) < .5 && Math.Abs(score.ActualHeight - 32) < .5,
                "score badge is circular and fixed");
            Check(CardMotion.GetEnabled(hitButton), "card motion owns hover and focus");

            CardMotion.Transition(hitButton, true, animate: false);
            await Drain();
            var posterScale = (ScaleTransform)poster.RenderTransform;
            var artwork = Visuals(card).OfType<MeowFilm.Desktop.Artwork.ArtworkImage>().Single();
            var imageScale = (ScaleTransform)artwork.RenderTransform;
            var scoreScale = (ScaleTransform)score.RenderTransform;
            Check(Math.Abs(posterScale.ScaleX - 1.03) < .001 &&
                Math.Abs(imageScale.ScaleX - 1.05) < .001 &&
                Math.Abs(scoreScale.ScaleX - 1.1) < .001 &&
                Math.Abs(shade.Opacity - 1) < .001, "hover scales and scrim match the server card");
            CardMotion.Transition(hitButton, false, animate: false);

            var shelf = shelves[0];
            var rail = shelf.ScrollHost ?? throw new InvalidOperationException("film rail missing");
            var previous = (Button)shelf.FindName("Previous");
            var next = (Button)shelf.FindName("Next");
            Check(rail.ScrollableWidth > 0 && previous.Visibility == Visibility.Collapsed &&
                next.Visibility == Visibility.Visible, "initial rail exposes only forward paging");
            Check(next.Opacity == 0 && !next.IsHitTestVisible, "resting arrow does not intercept cards");
            Check(next.Focus(), "hidden arrow remains keyboard reachable");
            await Drain();
            Check(next.Opacity == 1 && next.IsHitTestVisible, "keyboard focus reveals the round arrow");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => rail.HorizontalOffset > 900 && shelf.IsSettled, "rail advances by about 1000 px");
            Check(previous.Visibility == Visibility.Visible, "backward arrow appears after paging");

            RaiseKey(shelf, Key.End);
            await Until(() => Math.Abs(rail.HorizontalOffset - rail.ScrollableWidth) < .5 &&
                shelf.IsSettled, "End reaches the final boundary");
            Check(next.Visibility == Visibility.Collapsed, "forward arrow disappears at the boundary");
            var retainedCard = Visuals(shelf).OfType<FilmCard>().First();
            double retainedOffset = rail.HorizontalOffset;
            view.Visibility = Visibility.Collapsed;
            await Drain();
            Check(shelf.IsSettled && ReferenceEquals(rail, shelf.ScrollHost),
                "hidden rail stops motion while retaining its scroll host");
            view.Visibility = Visibility.Visible;
            await module.RestoreNavigationAsync(FilmCapability.HomeRoute, default);
            await Drain();
            Check(ReferenceEquals(rail, shelf.ScrollHost) &&
                Visuals(shelf).OfType<FilmCard>().Contains(retainedCard) &&
                Math.Abs(rail.HorizontalOffset - retainedOffset) < .5,
                "returning to film preserves the rail, realized card and horizontal offset");
            RaiseKey(shelf, Key.Home);
            await Until(() => rail.HorizontalOffset < .5 && shelf.IsSettled, "Home restores the first boundary");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.Visibility = Visibility.Collapsed;
            double pausedOffset = rail.HorizontalOffset;
            await Drain();
            Check(shelf.IsSettled && Math.Abs(rail.HorizontalOffset - pausedOffset) < .5,
                "hiding during paging stops the offset clock immediately");
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static async Task DataNavigation()
    {
        var source = new ImmediateFilmSource();
        await using var module = new FilmCapability(source);
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var routes = new List<string>();
        module.NavigationRequested += (_, args) => routes.Add(args.Route);
        var view = (FilmView)module.View;
        var window = WindowFor(view, 1160, 760);
        try
        {
            window.Show();
            window.UpdateLayout();
            await Drain();
            var movie = module.Sections.Single(section => section.Key == "movie");
            var more = Visuals(view).OfType<Button>()
                .Single(button => ReferenceEquals(button.Tag, movie));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var movieShelf = Visuals(view).OfType<FilmShelf>()
                .Single(shelf => ReferenceEquals(shelf.DataContext, movie));
            var homeCard = Visuals(movieShelf).OfType<FilmCard>().First();
            var homeItem = (FilmCardItem)homeCard.DataContext;
            var cardButton = (Button)homeCard.FindName("HitButton");
            cardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Drain();
            Check(routes.SequenceEqual([FilmCapability.DataRoute, FilmCapability.PlaybackRoute]),
                "more and cards request their distinct internal destinations");
            Check(module.ActiveData.Key == "movie" &&
                ReferenceEquals(module.Playback?.Item, homeItem),
                "both entry points retain their data and playback contexts");

            await module.NavigateAsync(FilmCapability.DataRoute, default);
            window.UpdateLayout();
            await Drain();
            var data = (ScrollViewer)view.FindName("FilmDataPage");
            var home = (ScrollViewer)view.FindName("HomeScroll");
            var items = (ItemsControl)view.FindName("DataItems");
            Check(module.Heading == "影片数据" && module.IsData && !module.IsHome,
                "data route publishes the shell title");
            Check(data.Visibility == Visibility.Visible &&
                data.Background is SolidColorBrush { Color.A: 0 }, "data page is visible and transparent");
            Check(home.Visibility == Visibility.Collapsed, "home rails retire on the data route");
            Check(module.ActiveData.Title == "热门电影" && items.Items.Count == 24,
                "selected section title and first 24 cards are presented");
            var dataTitle = (TextBlock)view.FindName("DataTitle");
            var sharedHeading = (Style)view.FindResource("MeowFilm.SectionHeading");
            Check(dataTitle.Text == "热门电影" &&
                ReferenceEquals(dataTitle.Style, sharedHeading) &&
                dataTitle.FontSize == 18 && dataTitle.FontWeight == FontWeights.SemiBold,
                "data page uses the shared compact section heading");
            var filters = (ItemsControl)view.FindName("DataFilters");
            var filterButtons = Visuals(filters).OfType<Button>()
                .Where(button => button.Tag is FilmFilterOption).ToArray();
            Check(filters.Items.Count == 2 && filterButtons.Length == 9 &&
                filterButtons.Count(button => ((FilmFilterOption)button.Tag).IsSelected) == 2,
                "movie data page renders both accessible server-compatible filter rows");
            var latest = filterButtons.Single(button => button.Content as string == "最新电影");
            latest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => module.ActiveData.FilterRows[0].Options
                .Single(option => option.Label == "最新电影").IsSelected &&
                source.Calls.Last().Section.Category == "最新",
                "filter interaction refreshes the bound data page");
            var grid = Visuals(items).OfType<UniformGrid>().Single();
            Check(grid.Columns == 5, "desktop data cards use the responsive five-column grid");
            var converter = new FilmGridColumnsConverter();
            Check((int)converter.Convert(610d, typeof(int), null!, CultureInfo.InvariantCulture) == 3 &&
                (int)converter.Convert(150d, typeof(int), null!, CultureInfo.InvariantCulture) == 1,
                "responsive grid retains usable card widths");

            var pageAction = (Button)view.FindName("DataPageAction");
            pageAction.ApplyTemplate();
            var actionFrame = Visuals(pageAction).OfType<Border>().First();
            Check(pageAction.Content as string == "加载更多" &&
                pageAction.ActualHeight == 44 &&
                actionFrame.CornerRadius == new CornerRadius(22) &&
                ReferenceEquals(pageAction.Style.BasedOn, view.FindResource("MeowFilm.Button")) &&
                AutomationProperties.GetName(pageAction) == "加载更多影片",
                "load more derives shared button typography and keeps its accessible capsule");

            var routeCount = routes.Count;
            var dataCard = Visuals(items).OfType<FilmCard>().First();
            var dataItem = (FilmCardItem)dataCard.DataContext;
            ((Button)dataCard.FindName("HitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Drain();
            Check(routes.Count == routeCount + 1 && routes[^1] == FilmCapability.PlaybackRoute &&
                ReferenceEquals(module.Playback?.Item, dataItem),
                "data cards use the same internal playback destination and retain the selected id");

            pageAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => module.ActiveData.Items.Count == 48, "load more appends another page");
            window.UpdateLayout();
            Check(items.Items.Count == 48 &&
                source.Calls.Last() is { Key: "movie", Start: 24, Limit: 24 },
                "the rendered list and source both advance by 24");
            data.ScrollToVerticalOffset(220);
            await Drain();
            double dataOffset = data.VerticalOffset;
            var retainedDataCard = Visuals(items).OfType<FilmCard>().First();
            int retainedCalls = source.Calls.Count;
            string dataLocation = module.CaptureNavigationLocation();
            Check(dataLocation == FilmCapability.DataRoute, "capture identifies the retained data page");
            await module.RestoreNavigationAsync(FilmCapability.HomeRoute, default);
            await module.RestoreNavigationAsync(dataLocation, default);
            await Drain();
            Check(source.Calls.Count == retainedCalls &&
                ReferenceEquals(items, view.FindName("DataItems")) &&
                Visuals(items).OfType<FilmCard>().Contains(retainedDataCard) &&
                Math.Abs(data.VerticalOffset - dataOffset) < .5 &&
                module.ActiveData.FilterRows[0].Options.Single(option => option.Label == "最新电影").IsSelected,
                "restoration preserves data controls, filter, scroll and request count");
            Check(((IDesktopHeaderSource)view).Header is
                { Title: "影片数据", SearchVisible: false, SearchEnabled: false },
                "shell header contribution follows the data route");

            await module.NavigateAsync(FilmCapability.PlaybackRoute, default);
            window.UpdateLayout();
            await Drain();
            var playback = (Grid)view.FindName("PlaybackPage");
            Check(module.Heading == "播放页" && module.IsPlayback &&
                home.Visibility == Visibility.Collapsed &&
                data.Visibility == Visibility.Collapsed &&
                playback.Visibility == Visibility.Visible &&
                playback.Background is SolidColorBrush { Color.A: 0 } &&
                Visuals(playback).OfType<FilmPlaybackView>().Single().DataContext == module.Playback,
                "playback route presents its selected film through the native page");
            Check(((IDesktopHeaderSource)view).Header is
                { Title: "播放页", SearchVisible: false, SearchEnabled: false },
                "shell header contribution follows the playback route");
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static Window WindowFor(FrameworkElement content, double width, double height)
    {
        // Windows can constrain the offscreen window to the session's display width.
        // Keep the tested content width independent of that native window constraint.
        content.Width = width;
        return new()
        {
            Content = content,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }

    private static void RaiseKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ??
            throw new InvalidOperationException("film shelf presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
        Check(args.Handled, "shelf handles " + key);
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++)
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!condition()) await Task.Delay(1);
        }
        await Drain();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed record FilmCall(FilmSectionDefinition Section, int Start, int Limit)
    {
        internal string Key => Section.Key;
    }

    private sealed class ImmediateFilmSource : IFilmHomeSource
    {
        internal List<FilmCall> Calls { get; } = [];

        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(
            FilmSectionDefinition section,
            int start,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(section, start, limit));
            IReadOnlyList<FilmCardItem> items = Enumerable.Range(start + 1, limit)
                .Select(index => new FilmCardItem(
                    $"{section.Key}:{index}",
                    $"{section.Title} {index}",
                    "",
                    index == 1 ? "8.6" : ""))
                .ToArray();
            return Task.FromResult(items);
        }

        public void Dispose()
        {
        }
    }
}
