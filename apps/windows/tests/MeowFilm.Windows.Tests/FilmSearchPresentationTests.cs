using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

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
            title = "流浪地球", poster = "", textBadge = "2019", siteLabel = "电影", tmdbType = "movie", tmdbId = 8,
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
        using var source = SearchRenderSource(f.Shell, out var root);
        var columnCounts = new HashSet<int>();
        var posterWidths = new HashSet<double>();
        int requests = f.Runtime.SearchOpens;
        foreach (int width in new[] { 1110, 1450, 860 })
        {
            SearchLayout(root, width, 800); await Drain(); SearchLayout(root, width, 800);
            var cards = Visuals(f.Page.View).OfType<FilmCard>().ToArray();
            Equal(6, cards.Length);
            var panel = Visuals(f.Page.View).OfType<AdaptiveCardPanel>().Single();
            columnCounts.Add(cards.Count(card => card.TranslatePoint(new Point(), panel).Y < 1));
            foreach (var card in cards)
            {
                var item = (FilmCardItem)card.DataContext;
                var poster = (Grid)card.FindName("PosterFrame");
                var label = (Border)card.FindName("SiteLabel");
                var subtitle = (TextBlock)card.FindName("Subtitle");
                True(poster.ActualWidth >= 143.5 && poster.ActualWidth <= 176.5 &&
                    Math.Abs(poster.ActualHeight - poster.ActualWidth * 1.5) <= 1,
                    "search posters shrink within readable bounds and retain the maximum reference ratio");
                posterWidths.Add(poster.ActualWidth);
                True(Math.Abs(card.ActualHeight - poster.ActualHeight - (item.HasSubtitle ? 54 : 30)) <= 1,
                    "caption and source-chip rows keep their native height while the poster changes size");
                Equal(item.Subtitle, subtitle.Text);
                Equal(item.HasSubtitle ? Visibility.Visible : Visibility.Collapsed, label.Visibility);
                if (item.HasSubtitle)
                {
                    True(label.ActualWidth <= 176 && label.TranslatePoint(new Point(), card).Y >= poster.ActualHeight &&
                        label.TranslatePoint(new Point(0, label.ActualHeight), card).Y <= card.ActualHeight + .5 &&
                        Math.Abs(label.TranslatePoint(new Point(label.ActualWidth / 2, 0), card).X - card.ActualWidth / 2) < 1,
                        $"bounded source/type chip stays centered below the poster without overlap or clipping: {label.RenderSize} at {label.TranslatePoint(new Point(), card)}, card {card.RenderSize}");
                    Equal(new Thickness(1), label.BorderThickness); Equal(new CornerRadius(8), label.CornerRadius);
                    Equal(Color.FromRgb(0xE2, 0xE8, 0xF0), ((SolidColorBrush)label.BorderBrush).Color);
                    Equal(Color.FromRgb(0xF8, 0xFA, 0xFC), ((SolidColorBrush)label.Background).Color);
                    Equal(Color.FromRgb(0x6B, 0x72, 0x80), ((SolidColorBrush)subtitle.Foreground).Color);
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
                    var badgePosition = badge.TranslatePoint(new Point(), poster);
                    True(Math.Abs(badgePosition.Y - 8) < .5 &&
                        Math.Abs(badgePosition.X + badge.ActualWidth - (poster.ActualWidth - 8)) < .5 &&
                        badge.ActualWidth <= poster.ActualWidth * .7 + .5, "text badge sits at the server top-right inset with a seventy-percent width cap");
                    Equal(Color.FromRgb(0xF9, 0x73, 0x16), ((SolidColorBrush)badge.Background).Color);
                    Equal(Colors.White, ((SolidColorBrush)text.Foreground).Color);
                    Equal(new CornerRadius(8), badge.CornerRadius); Equal(new Thickness(8, 2, 8, 2), badge.Padding);
                    Equal(10d, text.FontSize); Equal(FontWeights.Normal, text.FontWeight);
                    Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                }
                var title = Visuals(card).OfType<TextBlock>().Single(text => text.Text == item.Title);
                True(title.TextTrimming == TextTrimming.CharacterEllipsis && title.ActualWidth <= 176,
                    "long titles trim within the bounded native card width");
                var transform = title.TransformToAncestor(root);
                Point origin = transform.Transform(new Point()), unit = transform.Transform(new Point(1, 1));
                True(title.FontSize == 15 && title.FontWeight == FontWeights.Normal &&
                    title.FontFamily.Equals(SystemFonts.MessageFontFamily) &&
                    TextOptions.GetTextFormattingMode(title) == TextFormattingMode.Display &&
                    TextOptions.GetTextRenderingMode(title) == TextRenderingMode.Auto &&
                    Math.Abs(unit.X - origin.X - 1) < .001 && Math.Abs(unit.Y - origin.Y - 1) < .001,
                    "search captions retain system typography at native scale across widths");
                var bounds = new Rect(card.TranslatePoint(new Point(), f.Page.View), card.RenderSize);
                True(bounds.Left >= 0 && bounds.Right <= f.Page.View.ActualWidth + .5, "narrow layout wraps real cards inside its viewport");
            }
            SaveSearchEvidence(root, width, 800, $"cards-{width}.png");
        }
        True(columnCounts.Count > 1 && posterWidths.Count > 1 && f.Runtime.SearchOpens == requests,
            "resizing changes complete column count and poster size without another search");
        DesktopTheme.SetIsHighContrast(root, true); await Drain();
        foreach (var card in Visuals(f.Page.View).OfType<FilmCard>())
        {
            var badge = (Border)card.FindName("TextBadge"); var label = (Border)card.FindName("SiteLabel");
            Equal(SystemColors.HighlightBrush.Color, ((SolidColorBrush)badge.Background).Color);
            Equal(SystemColors.HighlightTextBrush.Color, ((SolidColorBrush)((TextBlock)badge.Child).Foreground).Color);
            True(badge.Effect is null, "high contrast removes the decorative badge shadow");
            Equal(SystemColors.WindowBrush.Color, ((SolidColorBrush)label.Background).Color);
            Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)label.BorderBrush).Color);
            Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)((TextBlock)label.Child).Foreground).Color);
        }
        source.RootVisual = null;

        var homeItem = new FilmCardItem("home", "原首页影片", "", "9.4") { TextBadge = "2023" };
        var home = new FilmCard { DataContext = homeItem };
        FilmCardItem? activated = null; home.Activated += (_, e) => activated = e.Item;
        SearchLayout(home, 176, 294);
        Equal(294d, home.ActualHeight);
        Equal(Visibility.Visible, ((Border)home.FindName("ScoreBadge")).Visibility);
        Equal(Visibility.Collapsed, ((Border)home.FindName("TextBadge")).Visibility);
        Equal(Visibility.Collapsed, ((Border)home.FindName("SiteLabel")).Visibility);
        ((Button)home.FindName("HitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(ReferenceEquals(homeItem, activated), "existing home card activation and score remain intact");
    }
    private static JsonElement CardWithLabels(JsonElement card, string label, string badge)
    {
        var value = JsonNode.Parse(card.GetRawText())!.AsObject();
        value["siteLabel"] = label; value["textBadge"] = badge;
        return JsonSerializer.SerializeToElement(value);
    }
    private static void SaveSearchEvidence(FrameworkElement root, int width, int height, string name)
    {
        string path = TestWorkspace.DataPath("film-search", name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
        True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 240) > width * height / 2,
            "offscreen evidence contains nonempty product pixels");
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(path)) png.Save(output);
        Console.WriteLine("EVIDENCE film-search " + path);
    }
    private static async Task UnifiedTabs()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("仙逆", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        using var source = SearchRenderSource(f.Shell, out var root);
        SearchLayout(root, 860, 800); await Drain();
        var tabs = Visuals((ScrollViewer)((SearchResultsView)f.Page.View).FindName("TabScroll")).OfType<Button>().ToArray();
        Equal(5, tabs.Length);
        void VerifySelection()
        {
            foreach (var button in tabs)
            {
                bool selected = (string)button.Tag == f.Page.SelectedTab;
                var indicator = (Border)button.Template.FindName("SelectionIndicator", button);
                Equal(selected ? Visibility.Visible : Visibility.Collapsed, indicator.Visibility);
                True(button.Focusable && button.IsTabStop && button.FocusVisualStyle is not null &&
                    AutomationProperties.GetName(button) == button.Content + "搜索结果", "category retains focus and accessible native button behavior");
                Equal(FontWeights.Normal, button.FontWeight);
                if (!selected) continue;
                Equal(new Size(20, 3), indicator.RenderSize);
                True(Math.Abs(indicator.TranslatePoint(new Point(10, 0), button).X - button.ActualWidth / 2) < .5 &&
                    Math.Abs(indicator.TranslatePoint(new Point(0, 3), button).Y - button.ActualHeight) < .5,
                    "selection is a short straight centered mark below the label");
            }
        }
        VerifySelection();
        var songs = tabs.Single(button => (string)button.Tag == "songs");
        songs.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SearchFixture.Until(() => f.Page.SelectedTab == "songs"); await Drain(); SearchLayout(root, 860, 800);
        VerifySelection();
        True(tabs.Single(button => (string)button.Tag == "film").FontWeight == FontWeights.Normal, "previous tab loses its active emphasis");
        songs.IsEnabled = false; await Drain();
        Equal(((SolidColorBrush)songs.FindResource("MeowFilm.Brush.Disabled")).Color, ((SolidColorBrush)songs.Foreground).Color);
        songs.IsEnabled = true;
        DesktopTheme.SetIsHighContrast(root, true); await Drain();
        var activeIndicator = (Border)songs.Template.FindName("SelectionIndicator", songs);
        Equal(((SolidColorBrush)songs.Foreground).Color, ((SolidColorBrush)activeIndicator.Background).Color);
        Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)songs.Foreground).Color);
        songs.IsEnabled = false; await Drain();
        Equal(SystemColors.GrayTextBrush.Color, ((SolidColorBrush)songs.Foreground).Color);
        source.RootVisual = null;
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
        using var source = SearchRenderSource(f.Shell, out var root);
        SearchLayout(root, 860, 800); await Drain();
        var view = (SearchResultsView)f.Page.View;
        var tools = (FrameworkElement)view.FindName("FilmTools");
        var tabs = (ScrollViewer)view.FindName("TabScroll");
        var toggle = (CheckBox)view.FindName("RawListToggle");
        var toolsRect = new Rect(tools.TranslatePoint(new Point(), view), tools.RenderSize);
        var tabsRect = new Rect(tabs.TranslatePoint(new Point(), view), tabs.RenderSize);
        True(toolsRect.Left >= tabsRect.Right - 1 && toolsRect.Right <= view.ActualWidth &&
            Math.Abs(toolsRect.Top - tabsRect.Top) < 1 && toolsRect.Bottom > tabsRect.Top,
            "TMDB status and the raw switch share the tabs row at its right edge without overlapping tabs");
        True(Visuals(tools).OfType<TextBlock>().First().Text == f.Page.MetadataStatus,
            "metadata status is immediately before the original-list control");
        var scroll = (ScrollViewer)view.FindName("FilmScroll");
        scroll.ScrollToVerticalOffset(400); await Drain();
        double offset = scroll.VerticalOffset;
        True(offset > 0, "real search scroll has a restorable position");
        var first = f.Page.Cards.Select(card => card.SearchContext!.DisplayKey).ToArray();
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); await Drain(); SearchLayout(root, 860, 800);
        True(f.Page.RawListMode, "the actual switch changes the existing presentation");
        var rawKeys = f.Page.Cards.Select(card => card.SearchContext!.DisplayKey).ToArray();
        True(rawKeys.All(key => key.StartsWith("site:", StringComparison.Ordinal)), "raw mode retains exact source identities");
        await f.Page.LoadMoreAsync(); await Drain(); SearchLayout(root, 860, 800);
        True(rawKeys.SequenceEqual(f.Page.Cards.Take(rawKeys.Length).Select(card => card.SearchContext!.DisplayKey)), "raw older round remains in place");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); await Drain();
        True(first.SequenceEqual(f.Page.Cards.Take(first.Length).Select(card => card.SearchContext!.DisplayKey)) &&
            Math.Abs(scroll.VerticalOffset - offset) < 1, "aggregate order and independent scroll return unchanged");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); await Drain();
        Equal(1, f.Runtime.SearchOpens); Equal(1, f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "more")); Equal(48, f.Page.Cards.Count);
        source.RootVisual = null;
    }
    private static HwndSource SearchRenderSource(ShellWindow shell, out FrameworkElement root)
    {
        root = (FrameworkElement)shell.Content;
        // Preserve the real window's inherited context when its content moves to a hidden HWND.
        root.Resources.MergedDictionaries.Add(shell.Resources);
        root.DataContext = shell.DataContext;
        ((Border)root).Background = shell.Background;
        root.SetValue(TextElement.FontFamilyProperty, shell.FontFamily);
        root.SetValue(TextElement.FontSizeProperty, shell.FontSize);
        root.SetValue(TextElement.FontWeightProperty, shell.FontWeight);
        root.SetValue(TextElement.ForegroundProperty, shell.Foreground);
        TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(shell));
        TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(shell));
        root.UseLayoutRounding = shell.UseLayoutRounding;
        shell.Content = null;
        return new(new HwndSourceParameters("film-search-offscreen")
        { Width = 1110, Height = 800, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000000 }) { RootVisual = root };
    }
    private static void SearchLayout(FrameworkElement root, int width, int height)
    {
        root.Width = width; root.Height = height;
        root.Measure(new(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
}
