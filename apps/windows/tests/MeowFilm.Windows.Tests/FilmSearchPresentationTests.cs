using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    }
    private static async Task UnifiedCards()
    {
        string longTitle = string.Concat(Enumerable.Repeat("很长的原始影片标题", 12));
        var sourceConfig = SearchFixture.Source("来源甲");
        await using var f = await UnifiedScene.Create(new(sourceConfig));
        var ordinary = SearchFixture.Member(sourceConfig, "ordinary", longTitle);
        var members = new[] { SearchFixture.Member(sourceConfig, "s1", "聚合片 第一季"), SearchFixture.Member(sourceConfig, "s2", "聚合片 第二季") };
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"),
            [ordinary, SearchFixture.Card("group:aggregate", "site", "聚合片", members), SearchFixture.Card("tmdb:tv:7", "tmdb", "元数据片", [])],
            [ordinary, .. members]);
        await f.Submit("原查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        True(f.Page.Cards.Count == 3 && f.Page.Cards.Select(card => card.Subtitle).ToHashSet().SetEquals(["来源甲", "聚合", "剧集"]),
            "the actual search projection contains exactly the three card semantics");
        using var source = SearchRenderSource(f.Shell, out var root);
        foreach (int width in new[] { 1110, 860 })
        {
            SearchLayout(root, width, 800); await Drain(); SearchLayout(root, width, 800);
            var cards = Visuals(f.Page.View).OfType<FilmCard>().ToArray();
            Equal(3, cards.Length);
            foreach (var card in cards)
            {
                var item = (FilmCardItem)card.DataContext;
                var poster = (Grid)card.FindName("PosterFrame");
                var subtitle = (TextBlock)card.FindName("Subtitle");
                Equal(new Size(176, 264), poster.RenderSize); Equal(318d, card.ActualHeight);
                Equal(item.Subtitle, subtitle.Text);
                True(subtitle.ActualWidth <= 176 && subtitle.TranslatePoint(new Point(), card).Y >= poster.ActualHeight &&
                    subtitle.TranslatePoint(new Point(0, subtitle.ActualHeight), card).Y <= card.ActualHeight + .5,
                    "source/type label remains below the existing poster without overlap or clipping");
                Equal(Visibility.Collapsed, ((Border)card.FindName("ScoreBadge")).Visibility);
                var badge = (Border)card.FindName("TextBadge");
                Equal(item.HasTextBadge ? Visibility.Visible : Visibility.Collapsed, badge.Visibility);
                if (item.HasTextBadge) Equal(item.TextBadge, ((TextBlock)badge.Child).Text);
                var title = Visuals(card).OfType<TextBlock>().Single(text => text.Text == item.Title);
                True(title.TextTrimming == TextTrimming.CharacterEllipsis && title.ActualWidth <= 176,
                    "long titles trim within the unchanged card width");
                var bounds = new Rect(card.TranslatePoint(new Point(), f.Page.View), card.RenderSize);
                True(bounds.Left >= 0 && bounds.Right <= f.Page.View.ActualWidth + .5, "narrow layout wraps real cards inside its viewport");
            }
        }
        string path = TestWorkspace.DataPath("film-search", "cards.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new RenderTargetBitmap(860, 800, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var pixels = new byte[860 * 800 * 4]; image.CopyPixels(pixels, 860 * 4, 0);
        True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 240) > 860 * 800 / 2,
            "offscreen evidence contains nonempty product pixels");
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(path)) png.Save(output);
        Console.WriteLine("EVIDENCE film-search cards " + path);
        source.RootVisual = null;

        var homeItem = new FilmCardItem("home", "原首页影片", "", "9.4");
        var home = new FilmCard { DataContext = homeItem };
        FilmCardItem? activated = null; home.Activated += (_, e) => activated = e.Item;
        SearchLayout(home, 176, 294);
        Equal(294d, home.ActualHeight);
        Equal(Visibility.Visible, ((Border)home.FindName("ScoreBadge")).Visibility);
        Equal(Visibility.Collapsed, ((TextBlock)home.FindName("Subtitle")).Visibility);
        ((Button)home.FindName("HitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(ReferenceEquals(homeItem, activated), "existing home card activation and score remain intact");
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
        var scroll = (ScrollViewer)view.FindName("FilmScroll");
        scroll.ScrollToVerticalOffset(400); await Drain();
        double offset = scroll.VerticalOffset;
        True(offset > 0, "real search scroll has a restorable position");
        var first = f.Page.Cards.Select(card => card.SearchContext!.DisplayKey).ToArray();
        f.Page.RawListMode = true; await Drain(); SearchLayout(root, 860, 800);
        var rawKeys = f.Page.Cards.Select(card => card.SearchContext!.DisplayKey).ToArray();
        True(rawKeys.All(key => key.StartsWith("site:", StringComparison.Ordinal)), "raw mode retains exact source identities");
        await f.Page.LoadMoreAsync(); await Drain(); SearchLayout(root, 860, 800);
        True(rawKeys.SequenceEqual(f.Page.Cards.Take(rawKeys.Length).Select(card => card.SearchContext!.DisplayKey)), "raw older round remains in place");
        f.Page.RawListMode = false; await Drain();
        True(first.SequenceEqual(f.Page.Cards.Take(first.Length).Select(card => card.SearchContext!.DisplayKey)) &&
            Math.Abs(scroll.VerticalOffset - offset) < 1, "aggregate order and independent scroll return unchanged");
        f.Page.RawListMode = true; await Drain();
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
        root.SetValue(TextElement.ForegroundProperty, shell.Foreground);
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
