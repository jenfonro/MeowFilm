using FontWeights = Microsoft.UI.Text.FontWeights;
using Windows.UI;
using Windows.System;
using Windows.UI.ViewManagement;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI;
using Windows.UI.Text;
using MeowFilm.Desktop;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class FilmPresentationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-presentation: first load uses card skeletons and results or errors end them",
            () => TestUiHost.InvokeAsync(SkeletonPresentation));
        yield return new("film-presentation: transparent four-rail home matches card and paging interactions",
            () => TestUiHost.InvokeAsync(HomePresentation));
        yield return new("film-presentation: section title opens data while all cards open the playback page",
            () => TestUiHost.InvokeAsync(DataNavigation));
        yield return new("film-presentation: classified list error uses the floating notice and real source selection recovers it",
            () => TestUiHost.InvokeAsync(PlaybackListFailure));
        yield return new("film-presentation: playback shares the application canvas across poster and theme changes with system contrast",
            () => TestUiHost.InvokeAsync(PlaybackCanvas));
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
            window.Root.UpdateLayout();
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
            var skeletons = Visuals(view).OfType<LoadingSkeleton>().Where(item => FilmUiTest.Visible(item)).ToArray();
            Check(skeletons.Length > 0 && !Visuals(view).OfType<FilmCard>().Any(),
                "first pending load presents image-free poster and title placeholders");
            foreach (var row in Visuals(view).OfType<FilmLoadingCards>().Where(row => FilmUiTest.Visible(row) && !row.IsGrid))
            {
                var cards = row.Children.Cast<FrameworkElement>().Select(card =>
                    card.TransformToVisual(row).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight))).ToArray();
                Check(cards.Length > 0 && cards.All(card => card.Left >= -.5 && card.Right <= row.ActualWidth + .5) &&
                    Math.Abs(cards[0].Left) < .5 && Math.Abs(cards[^1].Right - row.ActualWidth) < .5,
                    $"loading placeholders fill one complete row ({row.ActualWidth:0.##} wide): " +
                    string.Join("; ", cards.Select(card => card.ToString())));
            }
            source.Completion.SetResult();
            await navigation;
            window.Root.UpdateLayout();
            await Drain();
            Check(skeletons.All(item => !FilmUiTest.Visible(item) && !item.IsSweeping),
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
        MeowFilm.Desktop.Artwork.ArtworkScope.SetLoader(view, new FilmPlaybackFixture.BundledArtworkLoader());
        var window = WindowFor(view, 1160, 760);
        try
        {
            window.Show();
            window.Root.UpdateLayout();
            await Drain();

            Check(view.Background is SolidColorBrush { Color.A: 0 }, "page background is transparent");
            Check(view.FontWeight == FontWeights.Normal &&
                view.FontFamily.Source == "XamlAutoFontFamily" && view.UseLayoutRounding,
                "film pages inherit system typography and use native WinUI text antialiasing");
            Check(module.Sections.Select(section => section.Title)
                .SequenceEqual(["热门电影", "热门剧集", "新番放送", "热门综艺"]),
                "fixed section order");
            var shelves = Visuals(view).OfType<FilmShelf>().ToArray();
            Check(shelves.Length == 4 && shelves.All(shelf =>
                ((ListView)((Shelf)shelf.FindName("Cards")).FindName("ItemsHost")).Items.Count == 12), "four twelve-card rails");
            var sectionEntries = Visuals(view).OfType<Button>()
                .Where(button => button.Tag is FilmHomeSection).ToArray();
            var headingSize = (double)DesktopVisual.Resource(view, "MeowFilm.Font.Size.SectionHeading");
            var sectionTitles = Visuals(view).OfType<TextBlock>()
                .Where(text => module.Sections.Any(section => section.Title == text.Text))
                .ToArray();
            Check(sectionEntries.Length == 4 && sectionEntries.All(button =>
                AutomationProperties.GetName(button).StartsWith("查看更多 ", StringComparison.Ordinal)),
                "each section has one accessible title action");
            Check(!Visuals(view).OfType<Button>().Any(button => button.Content as string == "查看更多"),
                "no separate more button remains");
            Check(sectionTitles.Length == 4 &&
                sectionTitles.All(title => title.FontSize == headingSize && title.FontWeight == FontWeights.Normal &&
                    title.FontFamily.Source == "Segoe UI"),
                "film section typography reuses the shared music heading role");
            Check(sectionTitles.All(title =>
                title.FontWeight == FontWeights.Normal),
                "actual film headings use regular typography");
            foreach (var button in sectionEntries)
            {
                button.ApplyTemplate();
                var title = ((FilmHomeSection)button.Tag).Title;
                var labels = Visuals(button).OfType<TextBlock>().Select(text => text.Text).ToArray();
                Check(button.BorderThickness == new Thickness(0) && labels.Contains(title) &&
                    Visuals(button).OfType<FontIcon>().Any(icon => icon.Glyph == "\uE76C") &&
                    ReferenceEquals(button.Style.BasedOn, DesktopVisual.Resource(view, "MeowFilm.QuietButton")) && button.UseSystemFocusVisuals,
                    "title and native chevron share one borderless button with keyboard focus feedback");
            }
            var refreshButtons = Visuals(view).OfType<Button>()
                .Where(button => button.DataContext is FilmHomeSection &&
                    AutomationProperties.GetName(button).StartsWith("刷新 ", StringComparison.Ordinal)).ToArray();
            Check(refreshButtons.Length == 4 && refreshButtons.All(button =>
                button.Content is FontIcon { Glyph: "\uE72C" } && button.BorderThickness == new Thickness(0) &&
                button.IsEnabled == ((FilmHomeSection)button.DataContext).CanRefresh),
                "each section retains its accessible refresh action as a borderless circular arrow");

            var card = Visuals(shelves[0]).OfType<FilmCard>().First();
            var cardText = Visuals(card).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Check(cardText.Length > 0 && cardText.All(text =>
                text.FontWeight == FontWeights.Normal &&
                text.FontFamily.Source == "XamlAutoFontFamily") && card.UseLayoutRounding,
                "film card captions and scores retain system fonts and Auto through artwork composition");
            Check(card.ActualWidth >= 143.5 && card.ActualWidth <= 176.5 &&
                Math.Abs(card.ActualHeight - (card.ActualWidth * 1.5 + 30)) <= 1,
                "desktop cards use bounded native artwork with a fixed caption role");
            var poster = (Grid)card.FindName("PosterFrame");
            var score = (Border)card.FindName("ScoreBadge");
            var hitButton = (Button)card.FindName("HitButton");
            var shade = (Border)card.FindName("HoverShade");
            Check(Math.Abs(poster.ActualWidth - card.ActualWidth) < .5 &&
                Math.Abs(poster.ActualHeight - poster.ActualWidth * 1.5) <= 1,
                "poster keeps the two-to-three ratio");
            Check(Math.Abs(score.ActualWidth - 32) < .5 && Math.Abs(score.ActualHeight - 32) < .5,
                "score badge is circular and fixed");
            Check(CardMotion.GetEnabled(hitButton), "card motion owns hover and focus");

            CardMotion.SetProgress(hitButton, 1);
            await Drain();
            var posterScale = (ScaleTransform)poster.RenderTransform;
            var artwork = (FilmPlaybackPoster)card.FindName("Poster");
            Check(Visuals(artwork).OfType<Image>().Any(image => image.Source is not null),
                "home visual evidence includes decoded bundled posters");
            var imageScale = (ScaleTransform)artwork.RenderTransform;
            var scoreScale = (ScaleTransform)score.RenderTransform;
            Check(Math.Abs(posterScale.ScaleX - 1.03) < .001 &&
                Math.Abs(imageScale.ScaleX - 1.05) < .001 &&
                Math.Abs(scoreScale.ScaleX - 1.1) < .001 &&
                Math.Abs(shade.Opacity - 1) < .001, "hover scales and scrim match the server card");
            CardMotion.SetProgress(hitButton, 0);

            var shelf = shelves[0];
            var rail = shelf.ScrollHost ?? throw new InvalidOperationException("film rail missing");
            var sharedShelf = (Shelf)shelf.FindName("Cards");
            var previous = (Button)sharedShelf.FindName("Previous");
            var next = (Button)sharedShelf.FindName("Next");
            Check(rail.ScrollableWidth > 0 && previous.Visibility == Visibility.Collapsed &&
                next.Visibility == Visibility.Visible, "initial rail exposes only forward paging");
            Check(next.Opacity == 0 && !next.IsHitTestVisible, "resting arrow does not intercept cards");
            Check(next.Focus(FocusState.Programmatic), "hidden arrow remains keyboard reachable");
            await Drain();
            Check(next.Opacity == 1 && next.IsHitTestVisible, "keyboard focus reveals the round arrow");
            FilmUiTest.Click(next);
            double pageWidth = Math.Round((rail.ViewportWidth + 16) / sharedShelf.CardWidth) * sharedShelf.CardWidth;
            await Until(() => Math.Abs(rail.HorizontalOffset - pageWidth) < .5 && shelf.IsSettled, "rail advances by complete visible cards");
            Check(previous.Visibility == Visibility.Visible, "backward arrow appears after paging");

            FilmUiTest.Click(previous);
            await Until(() => rail.HorizontalOffset < .5 && shelf.IsSettled,
                "backward paging restores the first boundary before End");
            Check(sharedShelf.Navigate(VirtualKey.End), "the native shelf admits End at the initial boundary");
            await Until(() => Math.Abs(rail.HorizontalOffset - sharedShelf.MaximumOffset) < .5 &&
                shelf.IsSettled, "End reaches the final boundary");
            Check(next.Visibility == Visibility.Collapsed, "forward arrow disappears at the boundary");
            var retainedCard = Visuals(shelf).OfType<FilmCard>().First();
            double retainedOffset = rail.HorizontalOffset;
            view.Visibility = Visibility.Collapsed;
            await Drain();
            Check(shelf.IsSettled && ReferenceEquals(shelf.ScrollHost, rail),
                "hidden native rail stops motion while retaining its scroll host");
            view.Visibility = Visibility.Visible;
            await module.RestoreNavigationAsync(FilmCapability.HomeRoute, default);
            await Drain();
            Check(ReferenceEquals(rail, shelf.ScrollHost) &&
                Visuals(shelf).OfType<FilmCard>().Contains(retainedCard) &&
                Math.Abs(rail.HorizontalOffset - retainedOffset) < .5,
                "returning to film preserves the rail, realized card and horizontal offset");
            sharedShelf.ScrollToIndex(0);
            await Until(() => rail.HorizontalOffset < .5 && shelf.IsSettled, "Home restores the first boundary");
            FilmUiTest.Click(next);
            view.Visibility = Visibility.Collapsed;
            double pausedOffset = rail.HorizontalOffset;
            await Drain();
            Check(shelf.IsSettled && Math.Abs(rail.HorizontalOffset - pausedOffset) < .5 &&
                Math.Abs(module.Sections[0].HorizontalOffset - pageWidth) < .5,
                "hiding during paging stops the offset clock immediately");
            if (Environment.GetEnvironmentVariable("MEOWFILM_FILM_RAIL_PREVIEW_ROOT") is not null)
            {
                view.Visibility = Visibility.Visible;
                sharedShelf.HorizontalOffset = 0;
                window.AppWindow.Move(new global::Windows.Graphics.PointInt32(24, 24));
                if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
                window.Root.UpdateLayout();
                await Until(() => rail.HorizontalOffset < .5 && shelf.IsSettled,
                    "the first group is restored before desktop capture");
                await NativeDesktopCapture.SaveAsync(view, "home-sections");
            }
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
            window.Root.UpdateLayout();
            await Drain();
            var movie = module.Sections.Single(section => section.Key == "movie");
            var sectionEntry = Visuals(view).OfType<Button>()
                .Single(button => ReferenceEquals(button.Tag, movie));
            FilmUiTest.Click(sectionEntry);
            var movieShelf = Visuals(view).OfType<FilmShelf>()
                .Single(shelf => ReferenceEquals(shelf.DataContext, movie));
            var homeCard = Visuals(movieShelf).OfType<FilmCard>().First();
            var homeItem = (FilmCardItem)homeCard.DataContext;
            var cardButton = (Button)homeCard.FindName("HitButton");
            FilmUiTest.Click(cardButton);
            await Drain();
            Check(routes.SequenceEqual([FilmCapability.DataRoute, FilmCapability.PlaybackRoute]),
                "section title and cards request their distinct internal destinations");
            Check(module.ActiveData.Key == "movie" &&
                module.Playback?.Item.Id == homeItem.Id && module.Playback.Request.Target.Id == homeItem.PlaybackContext!.Target.Id,
                "both entry points retain their data and playback contexts");

            await module.NavigateAsync(FilmCapability.DataRoute, default);
            window.Root.UpdateLayout();
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
            var sharedHeading = (Style)DesktopVisual.Resource(view, "MeowFilm.SectionHeading");
            Check(dataTitle.Text == "热门电影" &&
                ReferenceEquals(dataTitle.Style, sharedHeading) &&
                dataTitle.FontSize == 18 && dataTitle.FontWeight == FontWeights.Normal,
                "data page uses the shared compact section heading");
            var filters = (ItemsControl)view.FindName("DataFilters");
            var filterButtons = Visuals(filters).OfType<ToggleButton>()
                .Where(button => button.Tag is FilmFilterOption).ToArray();
            Check(filters.Items.Count == 2 && filterButtons.Length == 9 &&
                filterButtons.Count(button => ((FilmFilterOption)button.Tag).IsSelected) == 2,
                "movie data page renders both accessible server-compatible filter rows");
            var latest = filterButtons.Single(button => button.Content as string == "最新电影");
            FilmUiTest.Click(latest);
            await Until(() => module.ActiveData.FilterRows[0].Options
                .Single(option => option.Label == "最新电影").IsSelected &&
                source.Calls.Last().Section.Category == "最新",
                "filter interaction refreshes the bound data page");
            var filterGroups = Visuals(filters).OfType<Border>()
                .Where(border => border.Child is ItemsControl).ToArray();
            var selectedFilters = filterButtons.Where(button => ((FilmFilterOption)button.Tag).IsSelected).ToArray();
            var floatingSurface = new SolidColorBrush(Color.FromArgb(255, 245, 250, 248));
            var accentSoft = new SolidColorBrush(Color.FromArgb(40, 25, 160, 100));
            var accentText = new SolidColorBrush(Color.FromArgb(255, 20, 110, 70));
            view.Resources["MeowFilm.Brush.FloatingSurface"] = floatingSurface;
            view.Resources["MeowFilm.Brush.AccentSoft"] = accentSoft;
            view.Resources["MeowFilm.Brush.AccentText"] = accentText;
            await Drain();
            Check(filterGroups.Length == 2 &&
                filterGroups.All(group => ReferenceEquals(group.Background, floatingSurface)) &&
                selectedFilters.Length == 2 && selectedFilters.All(button =>
                    ReferenceEquals(FilterContent(button).Background, accentSoft) && ReferenceEquals(FilterContent(button).Foreground, accentText)),
                "real film filter capsules and selected chips follow the shared surface and accent resources");
            using (var contrast = new FilmUiTest.HighContrastResources(view))
            {
                await Drain();
                var ground = FilmUiTest.ResourceColor(view, "MeowFilm.Brush.FloatingSurface");
                var border = FilmUiTest.ResourceColor(view, "Film.FilterBorder");
                var selected = FilmUiTest.ResourceColor(view, "MeowFilm.Brush.AccentSoft");
                var selectedText = FilmUiTest.ResourceColor(view, "MeowFilm.Brush.AccentText");
                Check(filterGroups.All(group => group.Background is SolidColorBrush fill && fill.Color == ground &&
                        group.BorderBrush is SolidColorBrush stroke && stroke.Color == border && group.BorderThickness == new Thickness(1)) &&
                    filterButtons.All(button => button.IsChecked == ((FilmFilterOption)button.Tag).IsSelected) &&
                    selectedFilters.All(button => FilterContent(button).Background is SolidColorBrush fill && fill.Color == selected &&
                        FilterContent(button).Foreground is SolidColorBrush ink && ink.Color == selectedText),
                    "shipped high-contrast resources retain native selection and distinct filter surfaces and borders");
            }
            await Drain();
            Check(filterGroups.All(group => ReferenceEquals(group.Background, floatingSurface) &&
                    group.BorderThickness == new Thickness(0)) &&
                selectedFilters.All(button =>
                    ReferenceEquals(FilterContent(button).Background, accentSoft) && ReferenceEquals(FilterContent(button).Foreground, accentText)),
                "leaving the contrast resource scope restores the current filter surface and soft accent");
            view.Resources.Remove("MeowFilm.Brush.FloatingSurface");
            view.Resources.Remove("MeowFilm.Brush.AccentSoft");
            view.Resources.Remove("MeowFilm.Brush.AccentText");
            await Drain();
            var grid = Visuals(items).OfType<AdaptiveCardPanel>().Single();
            var firstRow = Visuals(items).OfType<FilmCard>().Where(card =>
                card.TransformToVisual(grid).TransformPoint(new Point()).Y < 1).ToArray();
            Check(firstRow.Length > 5 && firstRow.All(card => card.ActualWidth >= 143.5 && card.ActualWidth <= 176.5),
                "the data grid uses readable smaller posters to fit another complete column");

            var pageAction = (Button)view.FindName("DataPageAction");
            pageAction.ApplyTemplate();
            Check(pageAction.Content as string == "加载更多" &&
                pageAction.ActualHeight == 44 &&
                pageAction.CornerRadius == new CornerRadius(22) &&
                ReferenceEquals(pageAction.Style.BasedOn, DesktopVisual.Resource(view, "MeowFilm.Button")) &&
                AutomationProperties.GetName(pageAction) == "加载更多影片",
                "load more derives shared button typography and keeps its accessible capsule");

            var routeCount = routes.Count;
            var dataCard = Visuals(items).OfType<FilmCard>().First();
            var dataItem = (FilmCardItem)dataCard.DataContext;
            FilmUiTest.Click(((Button)dataCard.FindName("HitButton")));
            await Drain();
            Check(routes.Count == routeCount + 1 && routes[^1] == FilmCapability.PlaybackRoute &&
                module.Playback?.Item.Id == dataItem.Id && module.Playback.Request.Target.Id == dataItem.PlaybackContext!.Target.Id,
                "data cards use the same internal playback destination and retain the selected id");

            FilmUiTest.Click(pageAction);
            await Until(() => module.ActiveData.Items.Count == 48, "load more appends another page");
            window.Root.UpdateLayout();
            Check(items.Items.Count == 48 &&
                source.Calls.Last() is { Key: "movie", Start: 24, Limit: 24 },
                "the rendered list and source both advance by 24");
            data.ChangeView(null, 220, null, true);
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
            window.Root.UpdateLayout();
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
            window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background("#FF0000");
            Check(playerPage.Background is SolidColorBrush { Color.A: 0 },
                "film playback exposes the shared application canvas when the program theme changes");
            var playbackText = Visuals(playback).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Check(playbackText.Length > 0 && playbackText.All(text =>
                text.FontWeight == FontWeights.Normal),
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
        static JsonNode FileRow(string id, string name)
        {
            var file = JsonNode.Parse(FilmPlaybackFixture.Candidate(id).GetRawText())!;
            file["candidateId"] = id;
            file["name"] = name;
            return file;
        }
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        rig.Runtime.Change(root =>
        {
            root["sources"] = JsonNode.Parse(FilmJson.Encode(new[]
            {
                new { id = "failed-source", title = "失败影片", source = new { name = "失败站点" } },
                new { id = "valid-source", title = "有效影片", source = new { name = "有效站点" } }
            }).GetRawText());
            root["browsing"] = JsonNode.Parse(FilmJson.Encode(new
            {
                sourceId = "failed-source", season = 1, loading = false, error = (string?)null, lines = Array.Empty<object>(),
                episodes = new[] { FileRow("old", "旧集数") },
                originalEpisodes = new[] { FileRow("old", "旧集数") }
            }).GetRawText());
            root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { FilmPlaybackFixture.Candidate("old") }).GetRawText());
        });
        await Until(() => rig.Page.SourceId == "failed-source" && rig.Page.Episodes.Count == 1,
            "the initial source and its episode reach the playback page");
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var window = WindowFor(view, 1150, 850);
        var notices = new List<string>();
        NoticeOutlet.SetReceiver(window.Root, args => { if (args.Message.Length > 0) notices.Add(args.Message); });
        rig.Session.OperationFailed += (_, code) => ((NoticeOutlet)view.FindName("Notices")).Message =
            FilmPlaybackError.Message(code, "播放操作失败");
        try
        {
            window.Show(); window.Root.UpdateLayout(); await Drain();
            var source = (ComboBox)view.FindName("SourcePicker");
            rig.Runtime.Change(root => root["browsing"]!["error"] = "UPSTREAM_FORMAT");
            await Until(() => rig.Page.ListStatus == "上游数据格式错误" && notices.Contains("上游数据格式错误"),
                "the upstream format failure reaches the classified floating notice");
            window.Root.UpdateLayout(); await Drain();
            Check(rig.Page.ShowListMessage &&
                Visuals(view).OfType<TextBlock>().Count(text => FilmUiTest.Visible(text) && text.Text == "上游数据格式错误") == 1 &&
                rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 && !rig.Page.CanBrowseRaw,
                "the classified directory error occupies the list row without stale playable content");
            Check(source.IsEnabled && source.Items.Count == 3 &&
                rig.Page.Sources.Any(choice => choice is { Id: "valid-source", Label: "有效站点" }),
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
                    root["browsing"]!["episodes"] = new JsonArray(FileRow("recovered", "恢复集数"));
                    root["browsing"]!["originalEpisodes"] = new JsonArray(FileRow("recovered", "恢复集数"));
                    root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { FilmPlaybackFixture.Candidate("recovered") }).GetRawText());
                });
                return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
            };
            source.SelectedItem = rig.Page.Sources.Single(choice => choice.Id == "valid-source");
            await Until(() => rig.Page.SourceId == "valid-source" && rig.Page.Episodes.Count == 1 && rig.Page.ListError.Length == 0,
                "the explicitly selected source restores its episode and clears the failed directory");
            Check(rig.Page.ListError == "" && !rig.Page.ShowListStatus && rig.Page.Candidates.Single().Id == "recovered" &&
                rig.Page.CanBrowseRaw && source.IsEnabled &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == "browse") == before + 1 &&
                FilmJson.Text(rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "browse").Parameters, "sourceId") == "valid-source",
                "only explicit source selection requests its exact identity and restores real episodes and raw-directory access");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
    }

    private static async Task PlaybackCanvas()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var canvas = new Border { Child = view };
        canvas.UseResource(Border.BackgroundProperty, "MeowFilm.Brush.AppBackground");
        var window = WindowFor(canvas, 1150, 850);
        window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.DefaultBackground;
        try
        {
            window.Show(); window.Root.UpdateLayout(); await Drain();
            var metadata = (Grid)view.FindName("MetadataPanel");
            var episodeItems = (ItemsControl)FilmUiTest.Named<Shelf>(view, "EpisodeShelf").FindName("ItemsHost");
            Point[] samples = [new(20, 200),
                metadata.TransformToVisual(canvas).TransformPoint(new Point(metadata.ActualWidth / 2, 4))];
            Color[]? defaultCanvas = null;
            foreach (string color in new[] { AppearanceColors.DefaultColor, "#FF0000", "#000000" })
            {
                window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background(color);
                view.Visibility = Visibility.Collapsed;
                await Drain();
                var expected = (await CanvasSamples(canvas, samples));
                if (defaultCanvas is null) defaultCanvas = expected;
                else Check(!expected.SequenceEqual(defaultCanvas), "the application owner applies the selected global hue");
                view.Visibility = Visibility.Visible;
                await Drain();
                foreach (string poster in new[] { FilmPlaybackFixture.Assets + FilmPlaybackFixture.Posters[0],
                    FilmPlaybackFixture.Assets + FilmPlaybackFixture.Posters[1], "" })
                {
                    rig.Runtime.Change(root => root["workInfo"]!["metadata"]!["poster_path"] = poster);
                    await Until(() => rig.Page.Poster == poster, "the selected poster reaches the playback page");
                    Check((await CanvasSamples(canvas, samples)).SequenceEqual(expected),
                        "the page margin and metadata region show the same application canvas for every poster");
                }
                foreach (var label in new[] { (TextBlock)view.FindName("FilmTitle"),
                    Visuals(episodeItems).OfType<TextBlock>()
                        .First(text => text.Text == rig.Page.Episodes[0].Overview) })
                {
                    var ink = ((SolidColorBrush)label.Foreground).Color;
                    Check(((LinearGradientBrush)canvas.Background).GradientStops
                        .All(stop => Contrast(ink, stop.Color) >= 4.5),
                        "playback text and secondary text remain readable on the default and custom application canvas");
                }
            }
            foreach (var items in new[] { episodeItems, (ItemsControl)view.FindName("RelatedItems") })
            {
                var cards = Visuals(items).OfType<FilmPlaybackPoster>().ToArray();
                Check(cards.Length > 0 && cards.All(card => FilmUiTest.Visible(card) &&
                    card.Children.OfType<Image>().Any(image => image.Source is BitmapSource { PixelWidth: > 0 } &&
                        image.Stretch == Stretch.UniformToFill)),
                    "episode and recommendation poster cards retain their normal decoded images and crop");
            }
            Check(((Grid)view.FindName("VideoPicture")).Background is SolidColorBrush video && video.Color == Colors.Black,
                "the video pixel host retains its black canvas");

            window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.DefaultBackground;
            await Drain();
            using (var contrast = new FilmUiTest.HighContrastResources(canvas))
            {
                await Drain();
                var ground = FilmUiTest.ResourceColor(canvas, "MeowFilm.Brush.AppBackground");
                var ink = FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Text");
                Check((await CanvasSamples(canvas, samples)).All(sample => sample == ground) &&
                    ((TextBlock)view.FindName("FilmTitle")).Foreground is SolidColorBrush text && text.Color == ink &&
                    Visuals(episodeItems).OfType<TextBlock>()
                        .First(label => label.Text == rig.Page.Episodes[0].Overview).Foreground is SolidColorBrush overview &&
                    overview.Color == ink &&
                    ((ToggleButton)view.FindName("FavoriteButton")).Foreground is SolidColorBrush favorite && favorite.Color == ink,
                    "high-contrast dictionaries pair the rendered system canvas with system text and action colors");
            }
            await Drain();
            Check((await CanvasSamples(canvas, samples)).SequenceEqual(defaultCanvas!),
                "leaving high contrast restores the application canvas");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
    }

    private static ContentPresenter FilterContent(ToggleButton button) =>
        Visuals(button).OfType<ContentPresenter>().Single(item => item.Name == "FilmFilterContent");

    private static async Task<Color[]> CanvasSamples(FrameworkElement element, IEnumerable<Point> samples)
    {
        var pixels = await FilmUiTest.Pixels(element);
        int width = (int)element.ActualWidth;
        return samples.Select(point =>
        {
            int offset = ((int)point.Y * width + (int)point.X) * 4;
            Check(pixels[offset + 3] == 255, "canvas samples contain the opaque application owner");
            return Color.FromArgb(255, pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }).ToArray();
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            double channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        static double Luminance(Color value) =>
            .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static Window WindowFor(FrameworkElement content, double width, double height)
    {
        content.Width = width;
        return FilmUiTest.Window(content, width, height);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++)
            await FilmUiTest.Pump();
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await FilmUiTest.Pump();
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
                    $"{section.Title} {index}", FilmPlaybackFixture.Assets +
                        FilmPlaybackFixture.Posters[(index - 1) % FilmPlaybackFixture.Posters.Length]) with
                    { Id = $"{section.Key}:{index}", Score = index == 1 ? "8.6" : "" })
                .ToArray();
            return Task.FromResult(items);
        }

        public void Dispose()
        {
        }
    }
}
