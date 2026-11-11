using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        yield return new("film-presentation: classified list error stays in the third row and real source selection recovers it",
            () => MusicQualityConsumerTests.Sta(PlaybackListFailure));
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
            Check(TextOptions.GetTextFormattingMode(view) == TextFormattingMode.Display &&
                TextOptions.GetTextRenderingMode(view) == TextRenderingMode.Auto &&
                view.FontFamily.Equals(SystemFonts.MessageFontFamily) && view.UseLayoutRounding && view.SnapsToDevicePixels,
                "film pages inherit system typography and let WPF choose text antialiasing");
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
            Check(sectionTitles.All(title =>
                TextOptions.GetTextFormattingMode(title) == TextFormattingMode.Display &&
                TextOptions.GetTextRenderingMode(title) == TextRenderingMode.Auto),
                "actual film headings use Display and Auto");
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
            var cardText = Visuals(card).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Check(cardText.Length > 0 && cardText.All(text =>
                TextOptions.GetTextFormattingMode(text) == TextFormattingMode.Display &&
                TextOptions.GetTextRenderingMode(text) == TextRenderingMode.Auto &&
                text.FontFamily.Equals(SystemFonts.MessageFontFamily)) && card.UseLayoutRounding && card.SnapsToDevicePixels,
                "film card captions and scores retain system fonts and Auto through artwork composition");
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
        await using var runtime = new FilmRuntimeProbe();
        await using var module = new FilmCapability(source, new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe()),
            new FilmLibrary(new FilmMemoryStore()));
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
                module.Playback?.Item.Id == homeItem.Id && module.Playback.Request.Target.Id == homeItem.PlaybackContext!.Target.Id,
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
                module.Playback?.Item.Id == dataItem.Id && module.Playback.Request.Target.Id == dataItem.PlaybackContext!.Target.Id,
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
            Check(source.Calls.Count == retainedCalls + 6 &&
                ReferenceEquals(items, view.FindName("DataItems")) &&
                Visuals(items).OfType<FilmCard>().Contains(retainedDataCard) &&
                Math.Abs(data.VerticalOffset - dataOffset) < .5 &&
                module.ActiveData.FilterRows[0].Options.Single(option => option.Label == "最新电影").IsSelected,
                "restoration revalidates the four home ranges and two loaded data ranges while preserving controls, filter and scroll");
            Check(((IDesktopHeaderSource)view).Header is
                { Title: "影片数据", SearchVisible: true, SearchEnabled: true, SearchTargetId: "film" },
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
            var playerPage = Visuals(playback).OfType<FilmPlaybackView>().Single();
            var playerGround = playerPage.Background;
            window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background("#FF0000");
            Check(playerGround is SolidColorBrush { Color.A: 255 } &&
                ReferenceEquals(playerPage.Background, playerGround),
                "film playback keeps its own opaque ground when the program theme changes");
            var playbackText = Visuals(playback).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Check(playbackText.Length > 0 && playbackText.All(text =>
                TextOptions.GetTextFormattingMode(text) == TextFormattingMode.Display &&
                TextOptions.GetTextRenderingMode(text) == TextRenderingMode.Auto),
                "playback page text uses Display and Auto instead of page-wide grayscale");
            Check(((IDesktopHeaderSource)view).Header is
                { Title: "播放页", SearchVisible: true, SearchEnabled: true, SearchTargetId: "film" },
                "shell header contribution follows the playback route");
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static async Task PlaybackListFailure()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        rig.Runtime.Change(root =>
        {
            root["sources"] = JsonNode.Parse(FilmJson.Encode(new[]
            {
                new { id = "failed-source", title = "失败来源" }, new { id = "valid-source", title = "有效来源" }
            }).GetRawText());
            root["browsing"] = JsonNode.Parse(FilmJson.Encode(new
            {
                sourceId = "failed-source", season = 1, loading = false, error = (string?)null, lines = Array.Empty<object>(),
                episodes = new[] { new { id = "old", candidateId = "old", name = "旧集数", season = 1, episode = 1 } },
                originalEpisodes = new[] { new { id = "old", candidateId = "old", name = "旧集数", path = "/old.mp4" } }
            }).GetRawText());
            root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { FilmPlaybackFixture.Candidate("old") }).GetRawText());
        });
        await Until(() => rig.Page.SourceId == "failed-source" && rig.Page.Episodes.Count == 1,
            "the initial source and its episode reach the playback page");
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var window = WindowFor(view, 1150, 850);
        try
        {
            window.Show(); window.UpdateLayout(); await Drain();
            var source = (ComboBox)view.FindName("SourcePicker");
            var status = Visuals(view).OfType<TextBlock>().Single(text =>
                BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "ListStatus");
            var listRegion = (Grid)status.Parent;
            var controlRows = (StackPanel)((DockPanel)source.Parent).Parent;
            var body = (Panel)listRegion.Parent;
            rig.Runtime.Change(root => root["browsing"]!["error"] = "UPSTREAM_FORMAT");
            await Until(() => rig.Page.ListStatus == "上游数据格式错误",
                "the upstream format failure reaches the classified list status");
            window.UpdateLayout(); await Drain();
            Check(listRegion.IsVisible && status.Text == "上游数据格式错误" &&
                status.HorizontalAlignment == HorizontalAlignment.Center && status.VerticalAlignment == VerticalAlignment.Center &&
                status.TextAlignment == TextAlignment.Center &&
                ReferenceEquals(controlRows.Parent, body) && controlRows.Children.Count == 2 &&
                body.Children.IndexOf(listRegion) == body.Children.IndexOf(controlRows) + 1 &&
                rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 && !rig.Page.CanBrowseRaw,
                "the classified error is centered after the source and season rows, not beside them or over the whole page");
            Check(source.IsEnabled && source.Items.Count == 3 && rig.Page.Sources.Any(choice => choice.Id == "valid-source"),
                "the failed third row leaves exact source choices enabled");
            int before = rig.Runtime.Calls.Count(call => call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == "browse");
            rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method != "film.session.command" || FilmJson.Text(input, "action") != "browse" ||
                    FilmJson.Text(input, "sourceId") != "valid-source")
                    return Task.FromResult(rig.Runtime.Handle(method, input));
                rig.Runtime.Change(root =>
                {
                    root["browsing"]!["sourceId"] = "valid-source"; root["browsing"]!["error"] = null;
                    root["browsing"]!["episodes"] = JsonNode.Parse(FilmJson.Encode(new[]
                    { new { id = "recovered", candidateId = "recovered", name = "恢复集数", season = 1, episode = 1 } }).GetRawText());
                    root["browsing"]!["originalEpisodes"] = JsonNode.Parse(FilmJson.Encode(new[]
                    { new { id = "recovered", candidateId = "recovered", name = "恢复集数", path = "/recovered.mp4" } }).GetRawText());
                    root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { FilmPlaybackFixture.Candidate("recovered") }).GetRawText());
                });
                return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
            };
            source.SelectedItem = rig.Page.Sources.Single(choice => choice.Id == "valid-source");
            await Until(() => rig.Page.SourceId == "valid-source" && rig.Page.Episodes.Count == 1 && !listRegion.IsVisible,
                "the explicitly selected source restores its episode and hides the list status");
            Check(rig.Page.ListError == "" && !rig.Page.ShowListStatus && rig.Page.Candidates.Single().Id == "recovered" &&
                rig.Page.CanBrowseRaw && source.IsEnabled &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == "browse") == before + 1 &&
                FilmJson.Text(rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "browse").Parameters, "sourceId") == "valid-source",
                "only explicit source selection requests its exact identity and restores real episodes and raw-directory access");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
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
                .Select(index => FilmPlaybackFixture.Card(section.Kind == "movie" ? "movie" : "tv", 100 + index,
                    $"{section.Title} {index}", "") with
                    { Id = $"{section.Key}:{index}", Score = index == 1 ? "8.6" : "" })
                .ToArray();
            return Task.FromResult(items);
        }

        public void Dispose()
        {
        }
    }
}
