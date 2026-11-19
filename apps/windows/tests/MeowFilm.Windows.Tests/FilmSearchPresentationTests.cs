using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using FontWeights = Microsoft.UI.Text.FontWeights;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchPresentationTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.FilmSearchCardCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> FilmSearchCardCases()
    {
        yield return Case("film search existing cards render ordinary aggregate TMDB and long titles at narrow widths", UnifiedCards);
        yield return Case("film search raw aggregate round trips preserve order scroll and request counts", UnifiedModes);
        yield return Case("search categories show one centered straight indicator and retain native button feedback", UnifiedTabs);
    }
    private static async Task UnifiedCards()
    {
        string longTitle = string.Concat(Enumerable.Repeat("很长的原始影片标题", 12));
        var sourceConfig = SearchFixture.Source("来源甲");
        await using var f = await UnifiedScene.Create(new(sourceConfig));
        var ordinary = SearchFixture.Member(sourceConfig, "ordinary", "仙逆");
        var members = new[] { SearchFixture.Member(sourceConfig, "s1", "聚合片 第一季"), SearchFixture.Member(sourceConfig, "s2", "聚合片 第二季") };
        var ordinaryCard = CardWithLabels(ordinary, "来源甲", "更新至第166集");
        var aggregate = CardWithLabels(SearchFixture.Card("group:aggregate", "site", "聚合片", members), "聚合", "更新至第2季第12集");
        var tv = CardWithLabels(SearchFixture.Card("tmdb:tv:7", "tmdb", "森林大帝", []), "剧集", "共2季78集");
        var movie = FilmJson.Encode(new { id = "tmdb:movie:8", displayKey = "tmdb:movie:8", sourceKind = "tmdb",
            businessId = "tmdb:movie:8", playbackTarget = new FilmPlaybackTarget("tmdb:movie:8", "movie", "流浪地球", "tmdb", "8").Wire(),
            title = "流浪地球", poster = FilmPlaybackFixture.Assets + FilmPlaybackFixture.Posters[0],
            textBadge = "2019", siteLabel = "电影", tmdbType = "movie", tmdbId = 8,
            aggregateItems = Array.Empty<object>() });
        var longCard = CardWithLabels(SearchFixture.Member(sourceConfig, "long", longTitle),
            string.Concat(Enumerable.Repeat("很长的站点名称", 12)), string.Concat(Enumerable.Repeat("更新至第100季第100集", 8)));
        var empty = CardWithLabels(SearchFixture.Member(sourceConfig, "empty", "暂无备注"), "", "");
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"),
            [ordinaryCard, aggregate, tv, movie, longCard, empty],
            [ordinary, .. members]);
        await f.Submit("原查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        True(f.Page.Cards.Count == 6 && f.Page.Cards.Select(card => card.Subtitle).Take(4).SequenceEqual(["来源甲", "聚合", "剧集", "电影"]),
            "the actual search projection retains site, aggregate, television and movie label semantics");
        var root = f.Shell.Root;
        ArtworkScope.SetLoader(root, new FilmPlaybackFixture.BundledArtworkLoader());
        var columnCounts = new HashSet<int>();
        var posterWidths = new HashSet<double>();
        int requests = f.Runtime.SearchOpens;
        foreach (int width in new[] { 1110, 1450, 860 })
        {
            await SearchLayout(f, width, 800);
            await SearchFixture.Until(() => Visuals(f.Page.View).OfType<FilmCard>().Count(card => card.ActualWidth > 0) == 6);
            var cards = Visuals(f.Page.View).OfType<FilmCard>().ToArray();
            Equal(6, cards.Length);
            var panel = Visuals(f.Page.View).OfType<ItemsWrapGrid>().Single();
            var items = (GridView)f.Page.View.FindName("FilmItems");
            double viewport = ((SearchResultsView)f.Page.View).FilmScroll!.ViewportWidth;
            double contentWidth = items.ActualWidth - 36;
            int columns = Math.Max(1, (int)Math.Floor((contentWidth + 36) / 216));
            double expectedWidth = (contentWidth - (columns - 1) * 36) / columns;
            columnCounts.Add(cards.Count(card => card.TransformToVisual(panel).TransformPoint(new Point()).Y < 1));
            await SearchFixture.Until(() => Visuals(cards[0]).OfType<Image>().Any(image => image.Source is not null));
            True(Visuals(cards[0]).OfType<Image>().Any(image => image.Source is not null),
                "search visual evidence includes decoded bundled artwork beneath the text badge");
            foreach (var card in cards)
            {
                var item = (FilmSearchCardItem)card.DataContext;
                var poster = (Grid)card.FindName("PosterFrame");
                var label = (Border)card.FindName("SiteLabel");
                var subtitle = (TextBlock)card.FindName("Subtitle");
                True(Math.Abs(poster.ActualWidth - expectedWidth) <= 1 &&
                    Math.Abs(poster.ActualHeight - poster.ActualWidth * 1.5) <= 1,
                    $"search follows the frontend's 180 DIP minimum, equal columns, 36 DIP gap and two-to-three posters: " +
                    $"window={width}, viewport={viewport}, expected={expectedWidth}, card={new Size(card.ActualWidth, card.ActualHeight)}, poster={new Size(poster.ActualWidth, poster.ActualHeight)}");
                posterWidths.Add(poster.ActualWidth);
                True(Math.Abs(card.ActualHeight - poster.ActualHeight - (item.HasSubtitle ? 54 : 30)) <= 1,
                    "caption and source-chip rows keep their native height while the poster changes size");
                Equal(item.Subtitle, subtitle.Text);
                Equal(item.HasSubtitle ? Visibility.Visible : Visibility.Collapsed, label.Visibility);
                if (item.HasSubtitle)
                {
                    True(label.ActualWidth <= card.ActualWidth && label.TransformToVisual(card).TransformPoint(new Point()).Y >= poster.ActualHeight &&
                        label.TransformToVisual(card).TransformPoint(new Point(0, label.ActualHeight)).Y <= card.ActualHeight + .5 &&
                        Math.Abs(label.TransformToVisual(card).TransformPoint(new Point(label.ActualWidth / 2, 0)).X - card.ActualWidth / 2) < 1,
                        $"bounded source/type chip stays centered below the poster without overlap or clipping: {new Size(label.ActualWidth, label.ActualHeight)} at {label.TransformToVisual(card).TransformPoint(new Point())}, card {new Size(card.ActualWidth, card.ActualHeight)}");
                    Equal(new Thickness(1), label.BorderThickness); Equal(new CornerRadius(8), label.CornerRadius);
                    Equal(Color.FromArgb(255, 0xE2, 0xE8, 0xF0), ((SolidColorBrush)label.BorderBrush).Color);
                    Equal(Color.FromArgb(255, 0xF8, 0xFA, 0xFC), ((SolidColorBrush)label.Background).Color);
                    Equal(Color.FromArgb(255, 0x6B, 0x72, 0x80), ((SolidColorBrush)subtitle.Foreground).Color);
                    Equal(new Thickness(8, 2, 8, 2), label.Padding); Equal(10d, subtitle.FontSize); Equal(FontWeights.Normal, subtitle.FontWeight);
                    Equal(TextTrimming.CharacterEllipsis, subtitle.TextTrimming);
                    if (item.Subtitle.Length < 10) True(label.ActualWidth < 100, "short labels hug their content instead of stretching");
                }
                Equal(Visibility.Collapsed, ((Border)card.FindName("ScoreBadge")).Visibility);
                var badge = (Border)card.FindName("TextBadge");
                Equal(item.HasTextBadge ? Visibility.Visible : Visibility.Collapsed, badge.Visibility);
                if (item.HasTextBadge)
                {
                    var text = (TextBlock)badge.Child;
                    Equal(item.TextBadge, text.Text);
                    var badgePosition = badge.TransformToVisual(poster).TransformPoint(new Point());
                    True(Math.Abs(badgePosition.Y - 8) < .5 &&
                        Math.Abs(badgePosition.X + badge.ActualWidth - (poster.ActualWidth - 8)) < .5 &&
                        badge.ActualWidth <= poster.ActualWidth * .7 + .5, "text badge sits at the server top-right inset with a seventy-percent width cap");
                    Equal(Color.FromArgb(255, 0xF9, 0x73, 0x16), ((SolidColorBrush)badge.Background).Color);
                    Equal(Colors.White, ((SolidColorBrush)text.Foreground).Color);
                    Equal(new CornerRadius(8), badge.CornerRadius); Equal(new Thickness(8, 2, 8, 2), badge.Padding);
                    Equal(12d, text.FontSize); Equal(FontWeights.Normal, text.FontWeight);
                    Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                    var hit = (Button)card.FindName("HitButton");
                    CardMotion.SetPointerActive(hit, true);
                    await SearchFixture.Until(() => !CardMotion.HasActiveAnimation(hit)); await Drain();
                    var badgeTransform = text.TransformToVisual(root);
                    Point badgeOrigin = badgeTransform.TransformPoint(new Point()), badgeUnit = badgeTransform.TransformPoint(new Point(1, 1));
                    True(
                        Math.Abs(badgeUnit.X - badgeOrigin.X - 1) < .001 &&
                        Math.Abs(badgeUnit.Y - badgeOrigin.Y - 1) < .001,
                        "badge glyphs stay at native scale without shadow composition while the poster is hovered");
                    CardMotion.SetPointerActive(hit, false);
                    await SearchFixture.Until(() => !CardMotion.HasActiveAnimation(hit));
                }
                var title = Visuals(card).OfType<TextBlock>().Single(text => text.Text == item.Title);
                True(title.TextTrimming == TextTrimming.CharacterEllipsis && title.ActualWidth <= card.ActualWidth,
                    "long titles trim within the bounded native card width");
                var transform = title.TransformToVisual(root);
                Point origin = transform.TransformPoint(new Point()), unit = transform.TransformPoint(new Point(1, 1));
                True(title.FontSize == 15 && title.FontWeight == FontWeights.Normal &&
                    title.FontFamily.Source == new TextBlock().FontFamily.Source &&
                    Math.Abs(unit.X - origin.X - 1) < .001 && Math.Abs(unit.Y - origin.Y - 1) < .001,
                    "search captions retain system typography at native scale across widths");
                var bounds = new Rect(card.TransformToVisual(f.Page.View).TransformPoint(new Point()), new Size(card.ActualWidth, card.ActualHeight));
                True(bounds.Left >= 0 && bounds.Right <= f.Page.View.ActualWidth + .5, "narrow layout wraps real cards inside its viewport");
            }
            await NativeDesktopCapture.SaveAsync(root, $"film-search-cards-{width}");
        }
        True(columnCounts.Count > 1 && posterWidths.Count > 1 && f.Runtime.SearchOpens == requests,
            "resizing changes complete column count and poster size without another search");
        var settings = new global::Windows.UI.ViewManagement.UISettings();
        DesktopTheme.SetIsHighContrast(root, true); using var contrast = new FilmUiTest.HighContrastResources(root); await Drain();
        foreach (var card in Visuals(f.Page.View).OfType<FilmCard>())
        {
            var badge = (Border)card.FindName("TextBadge"); var label = (Border)card.FindName("SiteLabel");
            Equal(settings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent), ((SolidColorBrush)badge.Background).Color);
            Equal(settings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Background), ((SolidColorBrush)((TextBlock)badge.Child).Foreground).Color);
            Equal(settings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Background), ((SolidColorBrush)label.Background).Color);
            Equal(settings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground), ((SolidColorBrush)label.BorderBrush).Color);
            Equal(settings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground), ((SolidColorBrush)((TextBlock)label.Child).Foreground).Color);
        }

        var homeItem = new FilmCardItem("home", "原首页影片", "", "9.4") { TextBadge = "2023" };
        var home = new FilmCard { DataContext = homeItem };
        FilmCardItem? activated = null; home.Activated += (_, e) => activated = e.Item;
        home.Width = 176;
        var homeRoot = new Grid { Children = { home } };
        homeRoot.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MeowFilm.Desktop/Theme.xaml") });
        var homeWindow = FilmUiTest.Window(homeRoot, 240, 380);
        try
        {
            homeWindow.Activate(); await TestUiHost.LoadedAsync(home);
            await SearchFixture.Until(() => home.ActualHeight == 294);
            Equal(Visibility.Visible, ((Border)home.FindName("ScoreBadge")).Visibility);
            Equal(Visibility.Collapsed, ((Border)home.FindName("TextBadge")).Visibility);
            Equal(Visibility.Collapsed, ((Border)home.FindName("SiteLabel")).Visibility);
            Click((Button)home.FindName("HitButton"));
            True(ReferenceEquals(homeItem, activated), "existing home card activation and score remain intact");
        }
        finally { homeWindow.Close(); await Drain(); }
    }
    private static JsonElement CardWithLabels(JsonElement card, string label, string badge)
    {
        var value = JsonNode.Parse(card.GetRawText())!.AsObject();
        value["siteLabel"] = label; value["textBadge"] = badge;
        value["poster"] = FilmPlaybackFixture.Assets + FilmPlaybackFixture.Posters[6];
        return JsonSerializer.SerializeToElement(value);
    }
    private static async Task UnifiedTabs()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("仙逆", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var root = f.Shell.Root;
        await SearchLayout(f, 860, 800);
        var tabs = Visuals((ScrollViewer)((SearchResultsView)f.Page.View).FindName("TabScroll")).OfType<Button>().ToArray();
        Equal(5, tabs.Length);
        void VerifySelection()
        {
            foreach (var button in tabs)
            {
                bool selected = (string)button.Tag == f.Page.SelectedTab;
                var indicator = (Border)Visuals(button).OfType<Border>().Single(item => item.Name == "SelectionIndicator");
                Equal(selected ? Visibility.Visible : Visibility.Collapsed, indicator.Visibility);
                True(button.IsTabStop && button.UseSystemFocusVisuals &&
                    AutomationProperties.GetName(button) == button.Content + "搜索结果", "category retains focus and accessible native button behavior");
                Equal(FontWeights.Normal, button.FontWeight);
                if (!selected) continue;
                Equal(new Size(20, 3), new Size(indicator.ActualWidth, indicator.ActualHeight));
                True(Math.Abs(indicator.TransformToVisual(button).TransformPoint(new Point(10, 0)).X - button.ActualWidth / 2) < .5 &&
                    Math.Abs(indicator.TransformToVisual(button).TransformPoint(new Point(0, 3)).Y - button.ActualHeight) < .5,
                    "selection is a short straight centered mark below the label");
            }
        }
        VerifySelection();
        var songs = tabs.Single(button => (string)button.Tag == "songs");
        Click(songs);
        await SearchFixture.Until(() => f.Page.SelectedTab == "songs"); await SearchLayout(f, 860, 800);
        VerifySelection();
        True(tabs.Single(button => (string)button.Tag == "film").FontWeight == FontWeights.Normal, "previous tab loses its active emphasis");
        songs.IsEnabled = false; await Drain();
        Equal(((SolidColorBrush)DesktopVisual.Resource(songs, "MeowFilm.Brush.Disabled")).Color, ((SolidColorBrush)songs.Foreground).Color);
        songs.IsEnabled = true;
        var settings = new global::Windows.UI.ViewManagement.UISettings();
        DesktopTheme.SetIsHighContrast(root, true); using var contrast = new FilmUiTest.HighContrastResources(root); await Drain();
        var activeIndicator = (Border)Visuals(songs).OfType<Border>().Single(item => item.Name == "SelectionIndicator");
        Equal(((SolidColorBrush)songs.Foreground).Color, ((SolidColorBrush)activeIndicator.Background).Color);
        Equal(settings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground), ((SolidColorBrush)songs.Foreground).Color);
        songs.IsEnabled = false; await Drain();
        Equal(settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.GrayText), ((SolidColorBrush)songs.Foreground).Color);
    }
    private static async Task UnifiedModes()
    {
        await using var f = await UnifiedScene.Create();
        var sourceConfig = f.Sources.Sources[0];
        var raw = Enumerable.Range(0, 48).Select(index => SearchFixture.Member(sourceConfig, index.ToString(), "影片" + index + "独立")).ToArray();
        var aggregate = raw.Select((member, index) => SearchFixture.Card("group:" + index, "site", "影片" + index, [member])).ToArray();
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), aggregate[..24], raw[..24], hasMore: true);
        f.Runtime.SearchCommand = (input, previous) => FilmJson.Text(input, "action") == "more"
            ? SearchFixture.Snapshot(Guid.Parse(FilmJson.Text(previous, "sessionId")), FilmJson.Text(previous, "query"), aggregate, raw, revision: 2, round: 2)
            : previous;
        await f.Submit("影片", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var root = f.Shell.Root;
        await SearchLayout(f, 860, 800);
        var view = (SearchResultsView)f.Page.View;
        var tools = (FrameworkElement)view.FindName("FilmTools");
        var tabs = (ScrollViewer)view.FindName("TabScroll");
        var toggle = (CheckBox)view.FindName("RawListToggle");
        var toolsRect = new Rect(tools.TransformToVisual(view).TransformPoint(new Point()), new Size(tools.ActualWidth, tools.ActualHeight));
        var tabsRect = new Rect(tabs.TransformToVisual(view).TransformPoint(new Point()), new Size(tabs.ActualWidth, tabs.ActualHeight));
        True(toolsRect.Left >= tabsRect.Right - 1 && toolsRect.Right <= view.ActualWidth &&
            Math.Abs(toolsRect.Top - tabsRect.Top) < 1 && toolsRect.Bottom > tabsRect.Top,
            "TMDB status and the raw switch share the tabs row at its right edge without overlapping tabs");
        True(Visuals(tools).OfType<TextBlock>().First().Text == f.Page.MetadataStatus,
            "metadata status is immediately before the original-list control");
        var scroll = view.FilmScroll!;
        scroll.ChangeView(null, 400, null, true);
        await SearchFixture.Until(() => Math.Abs(scroll.VerticalOffset - 400) < 1);
        double offset = scroll.VerticalOffset;
        True(offset > 0, "real search scroll has a restorable position");
        var first = f.Page.Cards.Select(card => card.SearchContext!.DisplayKey).ToArray();
        FilmUiTest.Click(toggle); await SearchLayout(f, 860, 800);
        True(f.Page.RawListMode, "the actual switch changes the existing presentation");
        var rawKeys = f.Page.Cards.Select(card => card.SearchContext!.DisplayKey).ToArray();
        True(rawKeys.All(key => key.StartsWith("site:", StringComparison.Ordinal)), "raw mode retains exact source identities");
        await f.Page.LoadMoreAsync(); await SearchLayout(f, 860, 800);
        True(rawKeys.SequenceEqual(f.Page.Cards.Take(rawKeys.Length).Select(card => card.SearchContext!.DisplayKey)), "raw older round remains in place");
        FilmUiTest.Click(toggle); await SearchLayout(f, 860, 800);
        await SearchFixture.Until(() => Math.Abs(scroll.VerticalOffset - offset) < 1);
        True(first.SequenceEqual(f.Page.Cards.Take(first.Length).Select(card => card.SearchContext!.DisplayKey)) &&
            Math.Abs(scroll.VerticalOffset - offset) < 1, "aggregate order and independent scroll return unchanged");
        FilmUiTest.Click(toggle); await Drain();
        Equal(1, f.Runtime.SearchOpens); Equal(1, f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "more")); Equal(48, f.Page.Cards.Count);
    }
    private static async Task SearchLayout(UnifiedScene scene, int width, int height)
    {
        var root = scene.Shell.Root;
        double scale = root.XamlRoot.RasterizationScale;
        scene.Shell.Resize(width, height);
        await SearchFixture.Until(() => scene.Shell.AppWindow.Size.Width == (int)Math.Round(width * scale) &&
            scene.Shell.AppWindow.Size.Height == (int)Math.Round(height * scale) &&
            scene.Page.View.IsLoaded && DesktopVisual.IsVisible(scene.Page.View));
        for (int frame = 0; frame < 2; frame++)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> handler = (_, _) => rendered.TrySetResult();
            CompositionTarget.Rendering += handler;
            try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { CompositionTarget.Rendering -= handler; }
            root.UpdateLayout(); await Drain();
        }
    }
}
