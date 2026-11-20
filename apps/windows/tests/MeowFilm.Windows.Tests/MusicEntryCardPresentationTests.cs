using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Threading;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class MusicEntryCardPresentationTests
{
    private const string CoverAddress = "https://art.example.test/entry-cover.png";
    private const string DecorationAddress = "https://art.example.test/entry-decoration.png";
    private static readonly Color BrightCover = Color.FromArgb(255, 250, 248, 245);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("resting covers blend into the same footer color with fixed gradient stops", BrightCoverContrast);
        yield return Case("entry captions share two lines with single line text on the second baseline", SubtitleWrapping);
        yield return Case("official decoration drives the title palette and failed decoration falls back to the cover", DecorationPalette);
        yield return Case("decorative fades preserve open and explicit play hit regions", HitAndPlayIntent);
        yield return Case("daily calendar days render valid single and double digit dates", CalendarDays);
        yield return Case("daily captions and covers stay stable with only three embedded songs", DailyPreview);
        yield return Case("entry presentations do not add artwork reads or implicit business requests", ReadOnlyPresentation);
        yield return Case("expanded content stays bottom aligned through resize reversal and rebinding", ContentGeometry);
        yield return Case("entry surfaces follow artwork colors and use system colors in high contrast", ColorSurfaces);
        yield return Case("entries without song lists reuse cover-only playlist shading through initialization and rebinding", CoverOnlyHover);
        yield return Case("titles remain above every hover shade with slightly stronger bounded top protection", TitleLayering);
        yield return Case("resting title protection follows local artwork independently of the footer palette", TitlePaletteIndependence);
        yield return Case("title protection persists through hover focus leave and rebinding without expanding the footer", TitleProtectionStates);
        yield return Case("title protection never lifts dark artwork into a lighter band", DarkTitleProtection);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-entry-card: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task BrightCoverContrast()
    {
        foreach (var model in Cards())
        foreach (double width in new[] { 152d, 168d })
        {
            using var f = new Fixture(model, width);
            await f.Load();
            Check(!CardMotion.IsPointerActive(f.Entry) && !DesktopTestUi.HasFocus(f.Entry) && CardMotion.GetProgress(f.Surface) == 0,
                "contrast is present without pointer hover or keyboard focus");
            var bottom = (Border)f.Entry.FindName("BottomShade");
            var stops = ((LinearGradientBrush)bottom.Background).GradientStops;
            var offsets = stops.Select(stop => stop.Offset).ToArray();
            // WinUI stores gradient stop offsets at single precision.
            Check(offsets.SequenceEqual(new[] { (double).62f, (double).82f, 1d }),
                $"resting gradient stop offsets: {string.Join(", ", offsets)}");
            Check(stops.Last().Color == ((SolidColorBrush)f.Surface.Background).Color && bottom.Opacity == 1 &&
                stops.Select(stop => stop.Color.A).SequenceEqual(new byte[] { 0, 112, 255 }),
                "resting color and alpha geometry are independent of hover");
            var title = f.Text(model.Title);
            var titleBounds = f.Bounds(title);
            var cover = f.Bounds(f.Cover);
            double sampleX = f.ContrastProbeX;
            foreach (double scale in new[] { 1d, 1.5d, 2d })
            {
                var frame = await Capture(f.Entry, scale);
                var middle = BrightestCoverSample(frame, cover, sampleX);
                Check(Luminance(middle) > Luminance(BrightCover) * .8,
                    "the center of the cover is preserved instead of applying a flat dark fill");
                Check(PaintContrast(frame, titleBounds, sampleX) > 1.15,
                    $"{model.Presentation} at {scale}x: white title glyphs remain visible with the requested light protection");
                var header = frame.At(new Point(sampleX, 12));
                Check(Luminance(header) > Luminance(BrightCover) * .33 &&
                    Luminance(header) < Luminance(BrightCover) * .38,
                    "the title region has slightly deeper protection while retaining the bright artwork");

                var lastCover = frame.At(new Point(sampleX, cover.Bottom - 2));
                var firstFooter = frame.At(new Point(sampleX, cover.Bottom + 2));
                Check(Contrast(lastCover, firstFooter) < 1.12,
                    "the resting gradient reaches the same RGB as the footer without a hard boundary");
            }
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "rendering all DPI variants uses one acquired cover and no business intent");
        }
    }

    private static async Task SubtitleWrapping()
    {
        var oneLine = DailyCard() with
        {
            Subtitle = "符合你口味的好歌"
        };
        var twoLines = oneLine with
        {
            Subtitle = "根据最近常听与收藏，为你推荐今天值得听的歌曲"
        };
        foreach (double width in new[] { 152d, 168d, 184d })
        {
            using var f = new Fixture(oneLine, width);
            await f.Load();
            double height = f.Entry.ActualHeight;
            var single = f.Bounds(f.CaptionText);
            Check(single.Height <= 18.5 && Math.Abs(single.Bottom - (height - 6)) < .5,
                "a single line occupies the second baseline, six DIP above the card bottom");
            await f.Bind(twoLines);
            var wrapped = f.Bounds(f.CaptionText);
            Check(wrapped.Height > 18.5 && wrapped.Height <= 36.5 && f.Entry.ActualHeight == height &&
                Math.Abs(single.Bottom - wrapped.Bottom) < .5 && Math.Abs(single.Top - wrapped.Top - 18) < .5,
                "one and two line captions share the same card height and second baseline");
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
                await CheckWholeGlyph(f, f.CaptionText, await Capture(f.Entry, scale));
            await f.Expand(true);
            Check(f.CaptionText.ActualHeight > 18.5 && f.CaptionText.ActualHeight <= 36.5 && f.Entry.ActualHeight == height,
                "expansion retains the two line slot and external card height");
            foreach (var model in Cards().Append(oneLine with { Subtitle = "" }))
            {
                await f.Bind(model);
                Check(f.Entry.ActualHeight == height, "all entry kinds and empty captions retain the same height");
            }
            Check(f.Opens == 0 && f.Plays == 0 && f.Loader.Addresses.Count == 1,
                "caption measurement does not trigger a business or artwork reread");
        }
    }

    private static async Task DecorationPalette()
    {
        var model = DailyCard() with { Decoration = DecorationAddress };
        var dark = Color.FromArgb(255, 28, 22, 20);
        using (var decorated = new Fixture(model, coverColor: dark))
        {
            await decorated.Load();
            Check(decorated.Decoration.HasTopPalette &&
                decorated.Loader.Addresses.OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(new[] { CoverAddress, DecorationAddress }.OrderBy(value => value, StringComparer.Ordinal)),
                "the declared cover and official decoration are each acquired once");
            var foreground = (SolidColorBrush)decorated.Text(model.Title).Foreground;
            Check(foreground.Color == Colors.White, "all entries retain white titles, including bright artwork");
            var shade = (LinearGradientBrush)decorated.TitleShade.Background;
            Check(shade.GradientStops[0].Color.A is >= 138 and <= 144 &&
                shade.GradientStops[^1].Color.A == 0 && decorated.TitleShade.ActualHeight <= 64,
                "the bright decoration receives only a light, short title gradient");
            var frame = await Capture(decorated.Entry, 1);
            var cover = decorated.Bounds(decorated.Cover);
            Check(Luminance(frame.At(new Point(cover.Left + cover.Width / 2, cover.Top + cover.Height * .7))) < .08,
                "an opaque decoration is clipped to the title region instead of replacing the real cover");
            Check(decorated.Opens == 0 && decorated.Plays == 0,
                "decoration presentation emits no business intent");
        }
        using (var fallback = new Fixture(model, coverColor: dark, failDecoration: true))
        {
            await fallback.Load();
            Check(!fallback.Decoration.HasTopPalette &&
                ((Button)fallback.Decoration.Children[1]).Visibility == Visibility.Collapsed,
                "failed decoration remains non-interactive and exposes no duplicate retry");
            Check(((SolidColorBrush)fallback.Text(model.Title).Foreground).Color == Colors.White,
                "failed decoration falls back to the dark cover palette");
            var shade = (LinearGradientBrush)fallback.TitleShade.Background;
            Check(shade.GradientStops[0].Color.R > shade.GradientStops[0].Color.B &&
                shade.GradientStops[0].Color.A is >= 90 and <= 102,
                "the cover fallback keeps the warm image hue with light title protection");
        }
    }

    private static async Task HitAndPlayIntent()
    {
        foreach (var model in Cards())
        {
            using var f = new Fixture(model);
            await f.Load();
            Check(AutomationProperties.GetName(f.Open) == model.Title, "the title remains the accessible open name");
            var cover = f.Bounds(f.Cover);
            var title = f.Bounds(f.Text(model.Title));
            var points = new[]
            {
                new Point(title.Left + title.Width / 2, title.Top + title.Height / 2),
                new Point(cover.Left + 5, cover.Top + cover.Height / 2),
                new Point(cover.Left + 5, cover.Bottom - 3)
            };
            foreach (var point in points)
                Check(ReferenceEquals(f.HitButton(point), f.Open), "decorative title and footer fades do not own input");
            var restingPlay = f.Bounds(f.Play);
            if (!restingPlay.IsEmpty && restingPlay.Width > 0 && restingPlay.Height > 0)
                Check(ReferenceEquals(f.HitButton(Center(restingPlay)), f.Open),
                    "an invisible play affordance does not intercept open input");
            Check(f.Opens == 0 && f.Plays == 0, "hit testing alone never starts an action");
            Click(f.HitButton(points[0]));
            Check(f.Opens == 1 && f.Plays == 0 && f.LastIntent == model,
                "clicking the shaded title emits exactly one open intent for the bound card");

            await f.Expand(true);
            foreach (var point in points)
                Check(ReferenceEquals(f.HitButton(point), f.Open), "expanded decorative pixels retain open ownership");
            if (model.Presentation == MusicCatalogEntryPresentation.Static)
                Check(f.Play.Visibility == Visibility.Collapsed, "static entries do not acquire a play affordance");
            else
            {
                Check(f.Play.IsEnabled && DesktopVisual.IsVisible(f.Play) && f.Play.Opacity > 0 && f.Play.IsHitTestVisible,
                    "a playable expanded entry exposes its explicit play control");
                var play = f.HitButton(Center(f.Bounds(f.Play)));
                Check(ReferenceEquals(play, f.Play), "the expanded play control owns its hit region above the fades");
                Check(PaintContrast(await Capture(f.Entry), f.Bounds(f.Play), f.ContrastProbeX) > 1.5,
                    "the white play glyph paints visible pixels on the muted content surface");
                Click(play);
                Check(f.Plays == 1 && f.Opens == 1 && f.LastIntent == model,
                    "play is one separate intent and never also opens the card");
            }
            Check(f.Opens == 1 && f.Plays == (model.Presentation == MusicCatalogEntryPresentation.Static ? 0 : 1) && f.Loader.Addresses.Count == 1,
                "native card actions emit only their separate owner intent without extra preview/artwork requests");
        }
        using var unavailable = new Fixture(DailyCard() with { Available = false, ErrorCode = "AUTH_REQUIRED" });
        await unavailable.Load();
        await unavailable.Expand(true);
        Check(!unavailable.Open.IsEnabled && !unavailable.Play.IsEnabled &&
            unavailable.Play.Visibility == Visibility.Collapsed,
            "visual fades do not make unavailable entry actions playable");
    }

    private static async Task CalendarDays()
    {
        using var f = new Fixture(ReadDaily(1));
        await f.Load();
        foreach (int day in new[] { 1, 9, 10, 29, 31 })
        {
            var model = ReadDaily(day);
            Check(model.HasCalendarDay && model.DayOfMonth == day, "the daily entry retains its declared valid date");
            await f.Bind(model);
            var date = f.Text(day.ToString(CultureInfo.InvariantCulture));
            Check(IsPainted(date, f.Entry), "the calendar date is effectively visible, not merely bound in a hidden parent");
            var bounds = f.Bounds(date);
            Check(bounds.Right <= f.Bounds(f.Text(model.Title)).Left, "the calendar date and daily title do not overlap");
            Check(PaintContrast(await Capture(f.Entry), bounds, f.ContrastProbeX) > 1.15,
                "the calendar date paints contrasting glyph pixels on a bright cover");
        }
        foreach (var model in new[] { ReadDaily(null), ReadDaily(null, includeDay: false) })
        {
            Check(!model.HasCalendarDay && model.DayOfMonth is null, "absent and null calendar dates remain absent");
            await f.Bind(model);
            Check(!PaintedTexts(f.Entry).Any(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)),
                "an absent calendar date does not display a fabricated numeric day");
        }
        foreach (object invalid in new object[] { -1, 0, 32, 1.5, "29", true })
        {
            var card = ReadDaily(invalid);
            Check(!card.HasCalendarDay && card.Title == "每日推荐", "an unusable calendar value only omits its own glyph");
        }
        Check(f.Loader.Addresses.Count == 1, "date rebinding does not reacquire the cover or request songs");
    }

    private static async Task DailyPreview()
    {
        var model = DailyCard();
        using var f = new Fixture(model, 152);
        await f.Load();
        Check(f.Surface.ActualWidth == 152 && Math.Abs(f.Entry.ActualHeight - 200) < 1,
            "the card reserves the same two-line footer for a short or wrapped caption");
        string[] expected = ["1 第一首", "2 第二首", "3 Waking Up"];
        Check(((MusicCatalogCard)f.Entry.DataContext).PreviewTitles.SequenceEqual(expected), "the bound preview provides exactly the first three songs");
        Check(PaintedTexts(f.Entry).Contains(model.Subtitle),
            "the full business caption is visible at rest");
        Check(!PaintedTexts(f.Entry).Any(expected.Contains), "embedded song titles are not painted on a collapsed card");

        Hover(f.Entry, true);
        await f.Expand(true);
        Check(PaintedTexts(f.Entry).Contains(model.Subtitle) && f.Cover.Address == model.Cover,
            "hover preserves the same business caption and artwork");
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            var expanded = await Capture(f.Entry, scale);
            foreach (string text in expected)
            {
                var row = f.Text(text);
                Check(IsPainted(row, f.Entry) &&
                    PaintContrast(expanded, f.Bounds(row), f.ContrastProbeX) > 1.5,
                    "each of the first three preview songs paints visible glyph pixels");
                await CheckWholeGlyph(f, row, expanded);
            }
        }
        Check(!Visuals(f.Entry).OfType<TextBlock>().Any(text => text.Text.Contains("第四首", StringComparison.Ordinal)),
            "the fourth embedded song is not presented");
        Hover(f.Entry, false);
        await f.Expand(false);
        Check(PaintedTexts(f.Entry).Contains(model.Subtitle) && !PaintedTexts(f.Entry).Any(expected.Contains),
            "collapse restores the ordinary caption and hides song rows");

        var replacement = model with { Preview = [new("替换歌曲", "歌手", "", 1, 0)] };
        await f.Bind(replacement);
        await f.Expand(true);
        Check(((MusicCatalogCard)f.Entry.DataContext).PreviewTitles.SequenceEqual(new[] { "1 替换歌曲" }) &&
            !PaintedTexts(f.Entry).Any(expected.Contains), "rebinding replaces rather than appends embedded preview rows");
        await f.Bind(model with { Preview = null });
        Check(((MusicCatalogCard)f.Entry.DataContext).PreviewTitles.Count == 0 && !PaintedTexts(f.Entry).Contains("1 替换歌曲"),
            "rebinding without a preview clears the previous card's song titles");
        Check(f.Opens == 0 && f.Plays == 0 && f.Loader.Addresses.Count == 1,
            "embedded preview expansion, fallback and rebinding make no extra requests or playback intent");
    }

    private static async Task ContentGeometry()
    {
        foreach (double width in new[] { 152d, 168d, 184d })
        foreach (string subtitle in new[] { "今天值得听", "每日推荐 | 从「Fell In Love」听起，发现今天的新鲜好歌" })
        {
            using var f = new Fixture(DailyCard() with { Subtitle = subtitle }, width);
            await f.Load();
            var rest = f.Bounds(f.CaptionText);
            var title = f.Bounds(f.Text("每日推荐"));
            var source = f.Cover.Source;
            double height = f.Entry.ActualHeight;
            await f.Expand(true);
            await AssertExpanded();
            var windowSize = DesktopTestUi.WindowSize(f.Window);
            f.Entry.Width = width + 16;
            DesktopTestUi.ResizeWindow(f.Window, windowSize.Width + 16, windowSize.Height + 16);
            await Until(() => f.Entry.ActualWidth == width + 16 && f.Entry.ActualHeight == height + 16,
                "the native client resize arranges the new card width and height");
            await AssertExpanded();
            CardMotion.SetPointerActive(f.Surface, false);
            await Task.Delay(40); await Drain();
            double partial = CardMotion.GetProgress(f.Surface);
            Check(partial is > 0 and < 1, "collapse uses the native presentation animation");
            CardMotion.SetPointerActive(f.Surface, true);
            await Until(() => CardMotion.GetProgress(f.Surface) is > 0 and < .999,
                "reversal starts from the intermediate presentation state");
            await Until(() => CardMotion.GetProgress(f.Surface) >= .999, "reversed animation settles at the expanded endpoint");
            await AssertExpanded();
            await f.Expand(false);
            Check(Math.Abs(f.Bounds(f.CaptionText).Bottom - (f.Entry.ActualHeight - 6)) < .5 &&
                ReferenceEquals(source, f.Cover.Source), "collapse restores the second baseline without replacing the artwork");
            await f.Bind(DailyCard() with { Subtitle = subtitle, Preview = [new("唯一歌曲", "", "", 1, null)] });
            await f.Expand(true);
            var one = f.Bounds(f.Text("1 唯一歌曲"));
            Check(Math.Abs(f.Entry.ActualHeight - one.Bottom - 6) < .5 &&
                f.Bounds(f.CaptionText).Bottom <= f.Bounds(f.Play).Top,
                "a shorter preview stays at the bottom and its play control does not cover the caption");
            await f.Bind(DailyCard() with { Subtitle = subtitle, Preview = null });
            await f.Expand(true);
            Check(Math.Abs(f.Bounds(f.CaptionText).Bottom - (f.Entry.ActualHeight - 6)) < .5,
                "rebinding without songs removes the lift instead of retaining old content height");
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "layout, reverse animation and rebinding have no data or playback side effects");

            async Task AssertExpanded()
            {
                var caption = f.Bounds(f.CaptionText);
                var first = f.Bounds(f.Text("1 第一首"));
                var third = f.Bounds(f.Text("3 Waking Up"));
                Check(caption.Top < rest.Top && caption.Top >= title.Bottom + 6 &&
                    first.Top >= caption.Bottom + 7 && third.Bottom <= f.Entry.ActualHeight,
                    "the same caption lifts above its three rows without covering the title; " +
                    $"progress {CardMotion.GetProgress(f.Surface):F6}, caption {caption}, rest {rest}, " +
                    $"title {title}, first {first}, third {third}, card height {f.Entry.ActualHeight}");
                Check(Math.Abs(f.Entry.ActualHeight - third.Bottom - 6) < .5,
                    $"third row ends at the normal six DIP inset, not an extra blank line; third={third}, " +
                    $"card={new Size(f.Entry.ActualWidth, f.Entry.ActualHeight)}, content={f.Bounds((FrameworkElement)f.Entry.FindName("ContentBlock"))}");
                var header = f.Bounds(f.Text("每日推荐"));
                Check(f.Entry.ActualHeight == height + f.Entry.Width - width &&
                    header.X == title.X && header.Y == title.Y && header.Height == title.Height,
                    $"hover does not change outer height or translate the header; height={height}->{f.Entry.ActualHeight}, " +
                    $"width={width}->{f.Entry.Width}, header={title}->{header}");
                foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    var frame = await Capture(f.Entry, scale);
                    await CheckWholeGlyph(f, f.CaptionText, frame);
                    foreach (string name in new[] { "1 第一首", "2 第二首", "3 Waking Up" })
                        await CheckWholeGlyph(f, f.Text(name), frame);
                }
            }
        }
    }

    private static async Task ColorSurfaces()
    {
        var images = new[]
        {
            MusicEntryPaletteTests.CoolArtwork(),
            MusicEntryPaletteTests.Image((x, y) => Color.FromArgb(255, 206, 145, 99)),
            MusicEntryPaletteTests.Image((x, y) => Color.FromArgb(255, 48, 33, 28)),
            MusicEntryPaletteTests.Image((x, y) => Color.FromArgb(255, 138, 55, 218)),
            MusicEntryPaletteTests.Image((x, y) => Color.FromArgb(255, 245, 245, 245))
        };
        var snapshots = new List<(DesktopTestUi.RenderFrame Resting, DesktopTestUi.RenderFrame Expanded)>();
        foreach (var image in images)
        {
            using var f = new Fixture(DailyCard() with { Subtitle = "每日推荐 | 从「Fell In Love」听起" },
                152, coverImage: image);
            await f.Load();
            var source = f.Cover.Source;
            var surface = (SolidColorBrush)f.Surface.Background;
            var shade = (Border)f.Entry.FindName("ExpansionShade");
            var rest = await Capture(f.Entry);
            var footer = new Point(f.Entry.ActualWidth / 2, f.Entry.ActualHeight - 3);
            Check(rest.At(footer) == surface.Color,
                $"the real footer paints the selected image tone: actual={rest.At(footer)}; expected={surface.Color}");
            Check(shade.Background is LinearGradientBrush expansion && expansion.GradientStops[^1].Color == surface.Color,
                "hover and footer use one color source");
            await f.Expand(true);
            var expanded = await Capture(f.Entry);
            Check(expanded.At(footer) == surface.Color && ReferenceEquals(surface, f.Surface.Background) &&
                ReferenceEquals(source, f.Cover.Source), "hover reuses the same palette and image");
            Check(PaintContrast(expanded, f.Bounds(f.Text("3 Waking Up")), f.ContrastProbeX) > 1.5,
                "the third song is actually painted over the muted surface");
            snapshots.Add((rest.Image, expanded.Image));
            DesktopTheme.SetIsHighContrast(f.Entry, true); await Drain(); DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
            Check(((SolidColorBrush)f.Surface.Background).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window) &&
                ((SolidColorBrush)f.Text("每日推荐").Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText) &&
                ((SolidColorBrush)f.CaptionText.Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText),
                "system high contrast replaces the light image palette and foreground together");
            Check((await Capture(f.Entry)).At(new Point(3, 80)) == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window),
                "high contrast paints an opaque system backdrop, not photo pixels");
        }
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1") await SaveVisuals(snapshots);
    }

    private static async Task CoverOnlyHover()
    {
        var cover = Color.FromArgb(255, 112, 168, 210);
        var snapshots = new List<(DesktopTestUi.RenderFrame Resting, DesktopTestUi.RenderFrame Expanded)>();
        foreach (var model in Cards().Where(card => !card.HasPreview && card.CanPlay)
                     .Append(DailyCard() with { Preview = null }))
        {
            using var f = new Fixture(model, 152, coverColor: cover);
            await f.Load();
            var rest = await Capture(f.Entry);
            var caption = f.Bounds(f.CaptionText);
            var source = f.Cover.Source;
            var palette = f.Surface.Background;
            var coverShade = (Border)f.Entry.FindName("CoverShade");
            var expansion = (Border)f.Entry.FindName("ExpansionShade");
            await f.Expand(true);
            var expanded = await Capture(f.Entry);
            Check(DesktopVisual.IsVisible(coverShade) && coverShade.Opacity == 1 && !DesktopVisual.IsVisible(expansion),
                "a playable entry without songs uses only the cover shade, not the colored expansion");
            Check(f.Bounds(coverShade) == f.Bounds(f.Cover) && f.Bounds(f.CaptionText) == caption &&
                ReferenceEquals(source, f.Cover.Source) && ReferenceEquals(palette, f.Surface.Background),
                "cover shading neither reaches the footer nor moves text or replaces artwork");
            var sample = new Point(5, 100);
            var shaded = expanded.At(sample);
            var resting = rest.At(sample);
            var expected = Color.FromArgb(255, (byte)Math.Round(resting.R * 199 / 255d),
                (byte)Math.Round(resting.G * 199 / 255d), (byte)Math.Round(resting.B * 199 / 255d));
            Check(Math.Abs(shaded.R - expected.R) <= 1 && Math.Abs(shaded.G - expected.G) <= 1 &&
                Math.Abs(shaded.B - expected.B) <= 1, "the resting artwork gradient receives the existing playlist's neutral 0x38 shade");
            Check(expanded.At(new Point(5, 190)) == rest.At(new Point(5, 190)),
                "the footer pixels stay unchanged while the cover dims");
            Check(ReferenceEquals(f.HitButton(new Point(5, 100)), f.Open) &&
                ReferenceEquals(f.HitButton(Center(f.Bounds(f.Play))), f.Play),
                "reusing the playlist shade does not import its full-cover play hit target");
            snapshots.Add((rest.Image, expanded.Image));
            DesktopTheme.SetIsHighContrast(f.Entry, true); await Drain(); DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
            Check((await Capture(f.Entry)).At(sample) == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window) &&
                ((SolidColorBrush)f.Text(model.Title).Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText),
                "no-list entries retain opaque system backgrounds and foregrounds in high contrast");
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "hover shading and high contrast add no artwork reads or business intents");
        }
        // Compare to the actual ordinary playlist view, not just a resource value.
        var ordinary = new MusicCatalogCardView { Width = 152, VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, Presentation = MusicCatalogCardLayout.Playlist, DataContext = DailyCard() };
        ArtworkScope.SetLoader(ordinary, new ArtworkLoader(cover, false, null));
        var ordinaryRoot = new Grid(); ordinaryRoot.Children.Add(ordinary);
        var window = DesktopTestUi.CreateWindow(ordinaryRoot, 480, 640);
        NativeDesktopCapture.DisableWindowTransitions(window);
        try
        {
            window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout();
            var artwork = Visuals(ordinary).OfType<ArtworkImage>().Single();
            await Until(() => artwork.Source is not null, "the ordinary playlist cover is acquired");
            var motion = Visuals(ordinary).OfType<FrameworkElement>().Single(CardMotion.GetEnabled);
            CardMotion.SetPointerActive(motion, true);
            await Until(() => !CardMotion.HasActiveAnimation(motion) && CardMotion.GetProgress(motion) >= .999,
                "the ordinary playlist's native hover animation reaches its endpoint");
            DesktopTestUi.WindowRoot(window).UpdateLayout();
            var image = await Capture(ordinary);
            var actual = image.At(artwork.TransformToVisual(ordinary).TransformPoint(new Point(5, 100)));
            Check(Math.Abs(actual.R - Math.Round(cover.R * 199 / 255d)) <= 1 &&
                Math.Abs(actual.G - Math.Round(cover.G * 199 / 255d)) <= 1 &&
                Math.Abs(actual.B - Math.Round(cover.B * 199 / 255d)) <= 1,
                "the shared brush preserves the ordinary playlist's existing pixels");
        }
        finally { window.Close(); }

        var pending = DailyCard() with { Preview = null };
        using (var f = new Fixture(pending, 152, coverColor: cover))
        {
            await f.Load(); await f.Expand(true);
            var caption = f.Bounds(f.CaptionText);
            pending.InitializePreview([new("后来到达", "", "", 1, null)]);
            await Drain(); DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
            Check(DesktopVisual.IsVisible(((Border)f.Entry.FindName("ExpansionShade"))) &&
                !DesktopVisual.IsVisible(((Border)f.Entry.FindName("CoverShade"))) &&
                f.Bounds(f.CaptionText).Top < caption.Top && PaintedTexts(f.Entry).Contains("1 后来到达"),
                "initialized songs switch to list expansion even when exactly one song exists");
            await f.Bind(Cards().Single(card => card.Kind == "fm")); await f.Expand(true);
            Check(!DesktopVisual.IsVisible(((Border)f.Entry.FindName("ExpansionShade"))) &&
                DesktopVisual.IsVisible(((Border)f.Entry.FindName("CoverShade"))) &&
                !PaintedTexts(f.Entry).Contains("1 后来到达"),
                "rebinding to FM clears the list expansion without retaining the old shade");
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "initialization and rebind do not introduce reads or playback intent");
        }
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1")
            await SaveVisuals(snapshots, "entry-cover-hover.png");
    }

    private static async Task TitleLayering()
    {
        foreach (var model in new[] { DailyCard(), Cards().Single(card => card.Kind == "fm"),
                     Cards().First(card => card.Kind == "similar") })
        {
            using var f = new Fixture(model, 152, coverColor: Color.FromArgb(255, 95, 150, 198));
            await f.Load();
            var title = f.Text(model.Title);
            var position = f.Bounds(title);
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            {
                await f.Expand(false);
                var resting = await Capture(f.Entry, scale);
                await f.Expand(true);
                var expanded = await Capture(f.Entry, scale);
                int solidInk = 0;
                for (int y = (int)Math.Ceiling(position.Top * scale); y < (int)Math.Floor(position.Bottom * scale); y++)
                for (int x = (int)Math.Ceiling(position.Left * scale); x < (int)Math.Floor(position.Right * scale); x++)
                {
                    var before = resting.Pixel(x, y);
                    if (Math.Min(before.R, Math.Min(before.G, before.B)) < 254) continue;
                    var after = expanded.Pixel(x, y);
                    Check(Math.Abs(after.R - before.R) <= 1 && Math.Abs(after.G - before.G) <= 1 &&
                        Math.Abs(after.B - before.B) <= 1,
                        $"hover masks cannot tint the actual white title ink; model={model.Kind}, scale={scale}, " +
                        $"pixel={x},{y}, before={before}, after={after}");
                    solidInk++;
                }
                Check(solidInk >= 15 && f.Bounds(title) == position,
                    "real topmost white title glyphs remain in the same position at every DPI");
            }
        }
        using var bright = new Fixture(DailyCard(), 152);
        await bright.Load();
        var shade = (LinearGradientBrush)bright.TitleShade.Background;
        Check(shade.GradientStops[0].Color.A is >= 138 and <= 144 &&
            shade.GradientStops[^1].Color.A == 0 && bright.TitleShade.ActualHeight == 64,
            "bright covers receive slightly more top protection without growing its extent");
    }

    private static BitmapSource SplitArtwork(Color bottom) => MusicEntryPaletteTests.Image((x, y) =>
        y >= 16 ? bottom : x >= 24 ? BrightCover : Color.FromArgb(255, 24, 44, 76));

    private static async Task TitlePaletteIndependence()
    {
        var snapshots = new List<(DesktopTestUi.RenderFrame Resting, DesktopTestUi.RenderFrame Expanded)>();
        using var warm = new Fixture(DailyCard(), 160, coverImage: SplitArtwork(Color.FromArgb(255, 210, 145, 95)));
        using var cool = new Fixture(DailyCard(), 160, coverImage: SplitArtwork(Color.FromArgb(255, 95, 145, 210)));
        await warm.Load(); await cool.Load();
        Check(((SolidColorBrush)warm.Surface.Background).Color != ((SolidColorBrush)cool.Surface.Background).Color,
            "the two covers have genuinely different footer palettes despite sharing their title region");
        foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            var a = await Capture(warm.Entry, scale); var b = await Capture(cool.Entry, scale);
            foreach (double y in new[] { 12d, 24d, 28d, 40d, 56d, 63d })
            {
                Check(a.At(new Point(5, y)) == b.At(new Point(5, y)) &&
                    a.At(new Point(145, y)) == b.At(new Point(145, y)),
                    $"identical title artwork has identical rendered protection regardless of the lower image colors; " +
                    $"scale={scale}, y={y}, left={a.At(new Point(5, y))}/{b.At(new Point(5, y))}, " +
                    $"right={a.At(new Point(145, y))}/{b.At(new Point(145, y))}, " +
                    $"shade={((LinearGradientBrush)warm.TitleShade.Background).GradientStops[0].Color}/{((LinearGradientBrush)cool.TitleShade.Background).GradientStops[0].Color}");
            }
            var brightPatch = a.At(new Point(145, 28));
            Check(Luminance(brightPatch) < Luminance(BrightCover) * .65 &&
                Luminance(brightPatch) > Luminance(BrightCover) * .4,
                "a local bright patch behind the lower title glyphs is visibly softened, not blacked out");
            Check(a.At(new Point(145, 72)) == BrightCover && b.At(new Point(145, 72)) == BrightCover,
                "resting title protection fades out before the image body, above the lower gradient");
        }
        foreach (var f in new[] { warm, cool })
        {
            var rest = await Capture(f.Entry);
            await f.Expand(true);
            snapshots.Add((rest.Image, (await Capture(f.Entry)).Image));
        }
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1")
            await SaveVisuals(snapshots, "entry-title-shade.png");
    }

    private static async Task TitleProtectionStates()
    {
        var bottom = Color.FromArgb(255, 95, 145, 210);
        foreach (var model in Cards())
        {
            using var f = new Fixture(model, 160, coverImage: SplitArtwork(bottom));
            await f.Load();
            var shade = f.TitleShade.Background;
            var resting = await Capture(f.Entry);
            var headerPoint = new Point(145, 28);
            var lowerPoint = new Point(5, 92);
            Check(resting.At(lowerPoint) == bottom && Luminance(resting.At(headerPoint)) < Luminance(BrightCover) * .75,
                $"the initial card protects its title while leaving the lower image untouched; model={model.Kind}, " +
                $"lower={resting.At(lowerPoint)}/{bottom}, header={resting.At(headerPoint)}, " +
                $"shade={((LinearGradientBrush)f.TitleShade.Background).GradientStops[0].Color}");
            foreach (bool active in new[] { true, false })
            {
                await f.Expand(active);
                Check(DesktopVisual.IsVisible(f.TitleShade) && f.TitleShade.Opacity == 1 &&
                    ReferenceEquals(shade, f.TitleShade.Background),
                    "hover changes neither the independent title shade nor its visibility");
                var frame = await Capture(f.Entry);
                Check(active && (model.CanPlay || model.HasPreview) ?
                    frame.At(lowerPoint) != bottom : frame.At(lowerPoint) == bottom,
                    "only interactive lower content follows hover; a static entry retains its cover");
                if (!active)
                    Check(frame.At(headerPoint) == resting.At(headerPoint),
                        "leaving hover restores the same protected title, not an unshaded cover");
            }
            Check(f.Open.Focus(FocusState.Programmatic), "title-protected entries remain keyboard reachable");
            await Until(() => CardMotion.GetProgress(f.Surface) >= .999, "focus activates the existing lower shade");
            Check(DesktopVisual.IsVisible(f.TitleShade) && f.TitleShade.Opacity == 1 && ReferenceEquals(shade, f.TitleShade.Background),
                "keyboard focus does not own the title protection");
            Check(f.Root.Focus(FocusState.Programmatic), "focus can leave the protected entry");
            await Until(() => CardMotion.GetProgress(f.Surface) <= .001, "leaving focus retires only hover presentation");
            Check((await Capture(f.Entry)).At(headerPoint) == resting.At(headerPoint) && (await Capture(f.Entry)).At(lowerPoint) == bottom,
                "keyboard focus leaving retains the initial title and lower image pixels");
            await f.Bind(model.HasPreview ? Cards().Single(card => card.Kind == "fm") : DailyCard());
            Check(DesktopVisual.IsVisible(f.TitleShade) && f.TitleShade.Opacity == 1 && ReferenceEquals(shade, f.TitleShade.Background) &&
                (await Capture(f.Entry)).At(headerPoint) == resting.At(headerPoint) && (await Capture(f.Entry)).At(lowerPoint) == bottom,
                "rebinding between list and cover-only entries cannot retire title protection or expand the lower shade");
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "title protection and interaction transitions reuse artwork without business requests");
        }
    }

    private static async Task DarkTitleProtection()
    {
        foreach (var color in new[] { Colors.Black, Color.FromArgb(255, 24, 44, 76), Color.FromArgb(255, 28, 22, 20) })
        {
            using var f = new Fixture(DailyCard(), 160, coverColor: color);
            await f.Load();
            var frame = await Capture(f.Entry);
            Check(Luminance(frame.At(new Point(145, 24))) <= Luminance(color) + .0001,
                "a protective title shade must not brighten a dark backdrop");
            Check(frame.At(new Point(145, 80)) == color,
                "dark artwork outside the short title shade remains intact");
        }
    }

    private static async Task ReadOnlyPresentation()
    {
        foreach (var model in Cards())
        {
            using var f = new Fixture(model);
            await f.Load();
            int reads = f.Loader.Addresses.Count;
            for (int i = 0; i < 3; i++)
            {
                Hover(f.Entry, true);
                await f.Expand(true);
                await Capture(f.Entry);
                Hover(f.Entry, false);
                await f.Expand(false);
                await Capture(f.Entry);
            }
            Check(f.Open.Focus(FocusState.Programmatic), "the card open control remains keyboard reachable");
            await Until(() => CardMotion.GetProgress(f.Surface) >= .999, "keyboard focus expands the card");
            Check(f.Root.Focus(FocusState.Programmatic), "keyboard focus can leave the card");
            await Until(() => CardMotion.GetProgress(f.Surface) <= .001, "leaving keyboard focus collapses the card");
            Check(f.Loader.Addresses.Count == reads && reads == 1 &&
                f.Opens == 0 && f.Plays == 0,
                $"{model.Presentation}: repeated render, hover and focus use only the acquired cover and embedded data");
            var artwork = f.Cover;
            var image = Visuals(artwork).OfType<Image>().Single();
            var pixels = image.Source;
            var palette = artwork.RawPaletteBrush;
            var entryPalette = f.Surface.Background;
            Check(pixels is not null, "the retention check starts with acquired pixels");
            f.Entry.Visibility = Visibility.Collapsed;
            await Drain();
            Check(f.Entry.IsLoaded && f.Loader.Leases.All(lease => !lease.Disposed) &&
                ReferenceEquals(pixels, image.Source) && ReferenceEquals(palette, artwork.RawPaletteBrush) &&
                ReferenceEquals(entryPalette, f.Surface.Background) && ReferenceEquals(pixels, artwork.Source),
                "hidden entries retain their acquired cover and palette");
            Check(!CardMotion.HasActiveAnimation(f.Surface), "hidden entries stop their hover clock");
            f.Entry.Visibility = Visibility.Visible;
            DesktopTestUi.WindowRoot(f.Window).UpdateLayout(); await Drain();
            Check(f.Loader.Addresses.Count == reads && ReferenceEquals(pixels, image.Source) &&
                ReferenceEquals(palette, artwork.RawPaletteBrush), "returning reuses the cover without a new request");
            f.Root.Children.Remove(f.Entry); await Drain();
            Check(f.Loader.Leases.All(lease => lease.Disposed) && image.Source is null && artwork.Source is null,
                "actual unmount releases the acquired cover");
        }

        using var lazy = new Fixture(DailyCard() with { Preview = null });
        await lazy.Load();
        Hover(lazy.Entry, true);
        await lazy.Expand(true);
        var initialized = (MusicCatalogCard)lazy.Entry.DataContext;
        Check(!initialized.HasPreview && lazy.Opens == 0 && lazy.Plays == 0,
            "hover presents existing data and cannot emit a read or playback action");
        initialized.InitializePreview([new("初始化曲目", "艺人", "", 1, null)]);
        await Drain(); DesktopTestUi.WindowRoot(lazy.Window).UpdateLayout();
        Check(PaintedTexts(lazy.Entry).Contains("1 初始化曲目"), "page-owned data publishes through binding");
        Hover(lazy.Entry, false);
        Check(initialized.PreviewTitles.SequenceEqual(["1 初始化曲目"]), "leaving does not retire initialized data");

        var viewport = new Canvas { Width = 200, Height = 220,
            Clip = new RectangleGeometry { Rect = new(0, 0, 200, 220) } };
        var entry = new EntryCard { Width = 152, DataContext = DailyCard() with { Decoration = DecorationAddress } };
        var loader = new ArtworkLoader(BrightCover, false, null); ArtworkScope.SetLoader(viewport, loader);
        viewport.Children.Add(entry); Canvas.SetTop(entry, 260);
        using var host = new DesktopTestUi.Host(viewport, 280, 340);
        await TestUiHost.LoadedAsync(entry); await Drain(); viewport.UpdateLayout();
        var decoration = (ArtworkImage)entry.FindName("DecorationLayer");
        var fade = Visuals(entry).OfType<MusicArtworkFade>().Single();
        string PresentationState() =>
            $"reads={string.Join(',', loader.Addresses)}, liveLeases={loader.Leases.Count(lease => !lease.Disposed)}, " +
            $"decoration={decoration.Source is not null}, mask={fade.Mask is not null}, " +
            $"entry={DesktopTestUi.Bounds(entry, viewport)}, fade={DesktopTestUi.Bounds(fade, viewport)}, " +
            $"viewport={viewport.ActualWidth}x{viewport.ActualHeight}, root={viewport.XamlRoot.Size}, " +
            $"loaded={entry.IsLoaded}/{decoration.IsLoaded}/{fade.IsLoaded}, visible={DesktopVisual.IsVisible(fade)}";
        Check(loader.Addresses.Count == 0 && decoration.Source is null && fade.Mask is null,
            "a realized decoration outside the actual viewport owns no read or presentation surface");
        Canvas.SetTop(entry, 210); viewport.InvalidateArrange(); viewport.UpdateLayout();
        await Until(() => loader.Addresses.Count == 2 && decoration.Source is not null && fade.Mask is not null,
            "partial visibility prepares the same clipped decoration source", PresentationState);
        var retainedDecoration = decoration.Source;
        var retainedMask = fade.Mask;
        Canvas.SetTop(entry, 260); viewport.InvalidateArrange(); viewport.UpdateLayout();
        await Drain();
        Check(ReferenceEquals(retainedDecoration, decoration.Source) && ReferenceEquals(retainedMask, fade.Mask) &&
            loader.Leases.All(lease => !lease.Disposed), "scrolling keeps the already presented decoration and its pixels");
        Check(entry.IsLoaded, "scrolling preserves the mounted entry");
        Canvas.SetTop(entry, 0); viewport.InvalidateArrange(); viewport.UpdateLayout();
        await Drain();
        Check(loader.Addresses.Count == 2 && ReferenceEquals(retainedDecoration, decoration.Source) &&
            ReferenceEquals(retainedMask, fade.Mask), "reentry reuses the original decoration without acquiring again");
        viewport.Visibility = Visibility.Collapsed;
        await Drain();
        Check(ReferenceEquals(retainedDecoration, decoration.Source) && ReferenceEquals(retainedMask, fade.Mask),
            "hiding retains the mounted decoration");
        viewport.Visibility = Visibility.Visible; viewport.UpdateLayout();
        await Drain();
        Check(loader.Addresses.Count == 2 && ReferenceEquals(retainedDecoration, decoration.Source) &&
            ReferenceEquals(retainedMask, fade.Mask), "redisplay reuses both cover and decoration");
        viewport.Children.Remove(entry);
        await Until(() => fade.Mask is null && loader.Leases.All(lease => lease.Disposed),
            "actual unmount releases every decoration presentation and artwork lease");
    }

    private static MusicCatalogCard DailyCard() =>
        new("daily", "每日推荐", "符合你口味的新鲜好歌", CoverAddress, "daily", "wy", ActionId: "daily",
            Preview: new[] { "第一首", "第二首", "Waking Up", "第四首" }
                .Select((title, i) => new MusicRankPreview(title, "歌手", "", i + 1, 0)).ToArray(),
            Presentation: MusicCatalogEntryPresentation.Preview,
            DayOfMonth: 29, Label: "每日推荐");

    private static IEnumerable<MusicCatalogCard> Cards()
    {
        yield return DailyCard();
        yield return DailyCard() with { Id = "daily-style", Title = "电音日推", Label = "电音日推", DayOfMonth = null };
        yield return new("similar", "心动模式", "你的红心歌曲和相似推荐", CoverAddress, "similar", "wy",
            ActionId: "similar", Presentation: MusicCatalogEntryPresentation.Overlay, Label: "心动模式");
        yield return new("similar-song", "相似歌曲", "从你喜欢的歌听起", CoverAddress, "similar", "wy",
            ActionId: "similar", Presentation: MusicCatalogEntryPresentation.Overlay, Label: "相似歌曲");
        yield return new("fm", "私人雷达", "你爱的歌值得反复聆听", CoverAddress, "fm", "wy",
            ActionId: "fm", Presentation: MusicCatalogEntryPresentation.Animated, Label: "私人雷达");
        yield return new("china", "国风频道", "国风歌曲", CoverAddress, "channel", "wy",
            ActionId: "china", PageCode: "MUSIC_CHINA", Presentation: MusicCatalogEntryPresentation.Static, Label: "国风频道");
    }

    private static MusicCatalogCard ReadDaily(object? day, bool includeDay = true)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = "daily", ["kind"] = "daily", ["title"] = "每日推荐", ["label"] = "每日推荐", ["subtitle"] = "符合你口味的新鲜好歌",
            ["cover"] = CoverAddress, ["presentation"] = "preview", ["action"] = new { type = "daily" }
        };
        if (includeDay) entry["dayOfMonth"] = day;
        var reply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] { new { layout = "feature-rail", items = new[] { entry } } } });
        return MusicCatalogDto.Read(reply, "wy", "home", "").Single().Items.Single();
    }

    private static void Hover(EntryCard entry, bool active) =>
        CardMotion.SetPointerActive((FrameworkElement)entry.FindName("Surface"), active);

    private static void Click(Button button)
    {
        Check(button.IsEnabled, "only an enabled hit target can be invoked");
        DesktopTestUi.Invoke(button);
    }

    private static Point Center(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static bool IsPainted(FrameworkElement element, FrameworkElement root)
    {
        if (!DesktopVisual.IsVisible(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && (!DesktopVisual.IsVisible(visual) || visual.Opacity <= .001)) return false;
            if (ReferenceEquals(current, root)) break;
        }
        var bounds = element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var visible = bounds; visible.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        return !visible.IsEmpty && visible.Width > 0 && visible.Height > 0;
    }

    private static string[] PaintedTexts(FrameworkElement root) =>
        Visuals(root).OfType<TextBlock>().Where(text => IsPainted(text, root)).Select(text => text.Text).ToArray();

    private static async Task<Frame> Capture(FrameworkElement element, double scale = 1)
    {
        element.UpdateLayout();
        Check(element.IsLoaded && element.ActualWidth > 0 && element.ActualHeight > 0,
            "the actual native card has a mounted render surface");
        var root = (FrameworkElement)element.XamlRoot!.Content;
        var transform = element.RenderTransform;
        var canvas = (Panel)root;
        var background = canvas.Background;
        double rasterScale = element.XamlRoot.RasterizationScale;
        try
        {
            // Render the real Composition tree at each requested scale; do not resize
            // a captured bitmap or rely on RenderTargetBitmap's child-visual support.
            element.RenderTransform = new ScaleTransform { ScaleX = scale / rasterScale, ScaleY = scale / rasterScale };
            Rect Bounds() => element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            await Until(() => Math.Abs(Bounds().Width * rasterScale - element.ActualWidth * scale) < .01 &&
                Math.Abs(Bounds().Height * rasterScale - element.ActualHeight * scale) < .01,
                "the compositor reports the requested card scale before pixel sampling");
            var bounds = Bounds();
            Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight,
                "the fixed native client contains the complete scaled card");
            canvas.Background = new SolidColorBrush(Colors.Black);
            var image = await NativeDesktopCapture.CaptureAsync(element);
            canvas.Background = new SolidColorBrush(Colors.White);
            var white = await NativeDesktopCapture.CaptureAsync(element);
            Check(image.Width == (int)Math.Ceiling(element.ActualWidth * scale) &&
                image.Height == (int)Math.Ceiling(element.ActualHeight * scale) &&
                white.Width == image.Width && white.Height == image.Height,
                $"the desktop capture uses the requested {scale}x render scale, got {image.Width}x{image.Height}");
            // Screen captures are opaque. Two real backings recover coverage so
            // the existing opaque-card assertion still tests the card itself.
            for (int at = 0; at < image.Pixels.Length; at += 4)
            {
                int difference = 0;
                for (int channel = 0; channel < 3; channel++)
                    difference = Math.Max(difference, Math.Abs(white.Pixels[at + channel] - image.Pixels[at + channel]));
                image.Pixels[at + 3] = (byte)(255 - difference);
            }
            return new Frame(new(image.Width, image.Height, scale, image.Pixels),
                $"card={element.ActualWidth}x{element.ActualHeight}, inRoot={bounds}, root={root.ActualWidth}x{root.ActualHeight}, " +
                $"xamlRoot={element.XamlRoot.Size}, rasterScale={rasterScale}, method=screen-BitBlt-target-bounds");
        }
        finally
        {
            canvas.Background = background;
            element.RenderTransform = transform;
            root.UpdateLayout(); await Drain();
        }
    }

    private sealed class Frame(DesktopTestUi.RenderFrame image, string geometry)
    {
        internal readonly DesktopTestUi.RenderFrame Image = image;
        internal double Scale => Image.Scale;
        internal Color At(Point point) => Pixel((int)(point.X * Scale), (int)(point.Y * Scale));
        internal Color Pixel(int x, int y)
        {
            Check(x >= 0 && x < Image.Width && y >= 0 && y < Image.Height, "pixel probe stays within the card");
            int index = (y * Image.Width + x) * 4;
            Check(Image.Pixels[index + 3] >= 250,
                $"contrast probes read painted opaque card pixels: ({x},{y}) in {Image.Width}x{Image.Height} at {Scale:F3}x; " +
                $"BGRA={Image.Pixels[index]},{Image.Pixels[index + 1]},{Image.Pixels[index + 2]},{Image.Pixels[index + 3]}; {geometry}");
            return Color.FromArgb(255, Image.Pixels[index + 2], Image.Pixels[index + 1], Image.Pixels[index]);
        }
    }

    private static double PaintContrast(Frame frame, Rect bounds, double backgroundX)
    {
        var region = bounds;
        region.Intersect(new Rect(0, 0, frame.Image.Width / frame.Scale, frame.Image.Height / frame.Scale));
        Check(!region.IsEmpty && region.Width > 0 && region.Height > 0, "the text has an on-card pixel region");
        double maximum = 1;
        int background = (int)(backgroundX * frame.Scale);
        for (int y = (int)Math.Ceiling(region.Top * frame.Scale); y < (int)Math.Floor(region.Bottom * frame.Scale); y++)
        {
            var backdrop = frame.Pixel(background, y);
            for (int x = (int)Math.Ceiling(region.Left * frame.Scale); x < (int)Math.Floor(region.Right * frame.Scale); x++)
                maximum = Math.Max(maximum, Contrast(frame.Pixel(x, y), backdrop));
        }
        return maximum;
    }

    private static Color BrightestCoverSample(Frame frame, Rect cover, double x)
    {
        var brightest = frame.At(new Point(x, cover.Top + cover.Height * .35));
        for (int percent = 36; percent <= 75; percent++)
        {
            var candidate = frame.At(new Point(x, cover.Top + cover.Height * percent / 100));
            if (Luminance(candidate) > Luminance(brightest)) brightest = candidate;
        }
        return brightest;
    }

    private static async Task CheckWholeGlyph(Fixture f, TextBlock row, Frame frame)
    {
        // A 25-DIP row may extend beyond the card while its shorter glyphs still
        // fit. Measure actual raster ink without the card's ancestor clipping.
        var reference = new TextBlock
        {
            Text = row.Text, FontFamily = row.FontFamily, FontSize = row.FontSize,
            FontStyle = row.FontStyle, FontWeight = row.FontWeight, FontStretch = row.FontStretch,
            Foreground = (new SolidColorBrush(Microsoft.UI.Colors.White)), FlowDirection = row.FlowDirection, Language = row.Language,
            Padding = row.Padding, TextAlignment = row.TextAlignment, TextWrapping = row.TextWrapping,
            TextTrimming = row.TextTrimming, LineHeight = row.LineHeight,
            LineStackingStrategy = row.LineStackingStrategy, UseLayoutRounding = row.UseLayoutRounding
        };



        var rowSize = new Size(row.ActualWidth, row.ActualHeight);
        reference.Measure(rowSize); reference.Arrange(new Rect(new Point(), rowSize)); reference.UpdateLayout();
        double scale = frame.Scale;
        reference.Width = row.ActualWidth; reference.Height = row.ActualHeight;
        reference.HorizontalAlignment = HorizontalAlignment.Left; reference.VerticalAlignment = VerticalAlignment.Top;
        reference.Margin = new Thickness(0, f.Entry.ActualHeight + 32, 0, 0);
        f.Root.Children.Add(reference);
        DesktopTestUi.RenderFrame image;
        try
        {
            await TestUiHost.LoadedAsync(reference); await Drain();
            image = await DesktopTestUi.Render(reference, scale);
        }
        finally { f.Root.Children.Remove(reference); }
        var pixels = image.Pixels;
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            if (pixels[(y * image.Width + x) * 4 + 3] >= 16)
            {
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        Check(bottom >= top && right >= left, "the unclipped reference contains actual text glyphs");
        var ink = row.TransformToVisual(f.Entry).TransformBounds(
            new Rect(left / scale, top / scale, (right - left + 1) / scale, (bottom - top + 1) / scale));
        var viewport = f.Bounds(f.Surface); viewport.Intersect(new Rect(0, 0, f.Entry.ActualWidth, f.Entry.ActualHeight));
        double tolerance = .5 / scale;
        Check(ink.Left >= viewport.Left - tolerance && ink.Top >= viewport.Top - tolerance &&
            ink.Right <= viewport.Right + tolerance && ink.Bottom <= viewport.Bottom + tolerance,
            $"{row.Text} at {scale}x: layout bottom {f.Bounds(row).Bottom:F2}, " +
            $"actual glyph bottom {ink.Bottom:F2}, visible card bottom {viewport.Bottom:F2}; complete glyphs must fit");
        var lastInk = new Rect(ink.Left, Math.Max(ink.Top, ink.Bottom - 2 / scale), ink.Width,
            Math.Min(ink.Height, 2 / scale));
        Check(PaintContrast(frame, lastInk, f.ContrastProbeX) > 1.1,
            $"{row.Text} at {scale}x: the bottom raster ink is actually painted, not clipped by an ancestor");
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1")
            Console.WriteLine($"ENTRY-INK {row.Text}; DPI {scale}x; layout-bottom {f.Bounds(row).Bottom:F2}; " +
                $"glyph-bottom {ink.Bottom:F2}; viewport-bottom {viewport.Bottom:F2}");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static async Task SaveVisuals(IReadOnlyList<(DesktopTestUi.RenderFrame Resting, DesktopTestUi.RenderFrame Expanded)> snapshots,
        string fileName = "entry-cards.png")
    {
        Check(snapshots.Count > 0, "the explicitly requested visual sheet contains real rendered cards");
        int width = snapshots.Max(pair => pair.Resting.Width), height = snapshots.Max(pair => pair.Resting.Height);
        const int gap = 16;
        int sheetWidth = (width + gap) * snapshots.Count + gap, sheetHeight = height * 2 + gap * 3;
        var pixels = Enumerable.Repeat((byte)255, sheetWidth * sheetHeight * 4).ToArray();
        for (int column = 0; column < snapshots.Count; column++)
        for (int row = 0; row < 2; row++)
        {
            var image = row == 0 ? snapshots[column].Resting : snapshots[column].Expanded;
            for (int y = 0; y < image.Height; y++)
                Array.Copy(image.Pixels, y * image.Width * 4, pixels,
                    ((gap + row * (height + gap) + y) * sheetWidth + gap + column * (width + gap)) * 4,
                    image.Width * 4);
        }
        string path = TestWorkspace.ValidatePath(TestWorkspace.ResultsRoot,
            Path.Combine(TestWorkspace.ResultsRoot, "windows-native", fileName));
        await DesktopTestUi.Save(new(sheetWidth, sheetHeight, 1, pixels), path);
        Console.WriteLine("VISUAL " + path);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await DesktopTestUi.DrainAsync();
    }

    private static async Task Until(Func<bool> done, string message, Func<string>? state = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message + (state is null ? "" : "; " + state()));
            await DesktopTestUi.DrainAsync();
            if (!done()) await Task.Delay(1);
        }
        await Drain();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Grid Root = new() { Background = (new SolidColorBrush(Microsoft.UI.Colors.Transparent)), IsTabStop = true };
        internal readonly EntryCard Entry;
        internal readonly Window Window;
        internal readonly ArtworkLoader Loader;
        internal int Opens, Plays;
        internal MusicCatalogCard? LastIntent;
        internal Grid Surface => (Grid)Entry.FindName("Surface");
        internal Button Open => (Button)Entry.FindName("OpenButton");
        internal Button Play => (Button)Entry.FindName("PlayButton");
        internal ArtworkImage Cover => (ArtworkImage)Entry.FindName("Cover");
        internal ArtworkImage Decoration => (ArtworkImage)Entry.FindName("DecorationLayer");
        internal Border TitleShade => (Border)Entry.FindName("TitleShade");
        internal TextBlock CaptionText => (TextBlock)Entry.FindName("Caption");
        internal double ContrastProbeX => Entry.ActualWidth - Surface.CornerRadius.TopRight - 1;

        internal Fixture(MusicCatalogCard model, double width = 168, Color? coverColor = null,
            bool failDecoration = false, BitmapSource? coverImage = null)
        {
            Root.Resources.MergedDictionaries.Add(new()
                { Source = new("ms-appx:///MeowFilm.Capabilities/Presentation/CapabilityTheme.xaml", UriKind.Absolute) });
            Loader = new ArtworkLoader(coverColor ?? BrightCover, failDecoration, coverImage);
            ArtworkScope.SetLoader(Root, Loader);
            Entry = new EntryCard { Width = width, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, DataContext = model };
            Entry.OpenRequested += (sender, _) => { Check(ReferenceEquals(sender, Entry), "open intent belongs to the card"); Opens++; LastIntent = (MusicCatalogCard)Entry.DataContext; };
            Entry.PlayRequested += (sender, _) => { Check(ReferenceEquals(sender, Entry), "play intent belongs to the card"); Plays++; LastIntent = (MusicCatalogCard)Entry.DataContext; };
            Root.Children.Add(Entry);
            Window = DesktopTestUi.CreateWindow(Root, 480, 640);
            NativeDesktopCapture.DisableWindowTransitions(Window);
        }

        internal async Task Load()
        {
            Window.Show(); DesktopTestUi.WindowRoot(Window).UpdateLayout(); Root.Focus(FocusState.Programmatic);
            var expectedReads = ((MusicCatalogCard)Entry.DataContext).Decoration.Length > 0 ? 2 : 1;
            await Until(() => Visuals(Cover).OfType<Image>().Any(image => image.Source is not null) &&
                Loader.Addresses.Count == expectedReads && CardMotion.GetProgress(Surface) <= .001,
                "the real native card loads its declared artwork at rest");
            DesktopTestUi.WindowRoot(Window).UpdateLayout();
        }

        internal TextBlock Text(string text) => Visuals(Entry).OfType<TextBlock>().Single(block => block.Text == text);
        internal Rect Bounds(FrameworkElement element) =>
            element.TransformToVisual(Entry).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        internal Button HitButton(Point point)
        {
            for (DependencyObject? hit = DesktopTestUi.Hit(Entry, point); hit is not null;
                hit = VisualTreeHelper.GetParent(hit))
                if (hit is Button button) return button;
            throw new InvalidOperationException("No entry action owns the actual native hit point " + point);
        }
        internal async Task Expand(bool active)
        {
            CardMotion.SetPointerActive(Surface, active);
            await Until(() => !CardMotion.HasActiveAnimation(Surface) &&
                Math.Abs(CardMotion.GetProgress(Surface) - (active ? 1 : 0)) < .001,
                "the native hover animation reaches its requested endpoint");
            DesktopTestUi.WindowRoot(Window).UpdateLayout();
        }
        internal async Task Bind(MusicCatalogCard model)
        {
            Entry.DataContext = model;
            await Drain(); DesktopTestUi.WindowRoot(Window).UpdateLayout();
        }
        public void Dispose() => Window.Close();
    }

    private sealed class ArtworkLoader : IArtworkLoader
    {
        internal readonly List<string> Addresses = [];
        internal readonly List<ArtworkLease> Leases = [];
        private readonly ArtworkPixels cover;
        private readonly ArtworkPixels decoration;
        private readonly bool failDecoration;
        internal ArtworkLoader(Color coverColor, bool failDecoration, BitmapSource? coverImage)
        {
            this.failDecoration = failDecoration;
            if (coverImage is null) cover = MusicTestUi.Pixels(1, 1, (_, _) => coverColor);
            else
            {
                var source = (WriteableBitmap)coverImage;
                cover = new ArtworkPixels(source.PixelWidth, source.PixelHeight, MusicTestUi.Pixels(source));
            }
            decoration = MusicTestUi.Pixels(1, 8, (_, _) => Colors.White);
        }
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Addresses.Add(address);
            if (address == DecorationAddress && failDecoration)
                return Task.FromException<IArtworkLease>(new ArtworkException("ARTWORK_NETWORK"));
            var image = address switch
            {
                CoverAddress => cover,
                DecorationAddress => decoration,
                _ => throw new InvalidOperationException("presentation acquired an undeclared artwork address")
            };
            var lease = new ArtworkLease(image);
            Leases.Add(lease);
            return Task.FromResult<IArtworkLease>(lease);
        }
    }

    private sealed class ArtworkLease(ArtworkPixels image) : IArtworkLease
    {
        internal bool Disposed;
        public ArtworkPixels Image => !Disposed ? image : throw new ObjectDisposedException(nameof(ArtworkLease));
        public void Dispose() => Disposed = true;
    }
}
