using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class MusicEntryCardPresentationTests
{
    private const string CoverAddress = "https://art.example.test/entry-cover.png";
    private const string DecorationAddress = "https://art.example.test/entry-decoration.png";
    private static readonly Color BrightCover = Color.FromRgb(250, 248, 245);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("resting covers keep a light title shade and a separate muted footer", BrightCoverContrast);
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
            Check(!f.Entry.IsMouseOver && !f.Entry.IsKeyboardFocusWithin && CardMotion.GetProgress(f.Surface) == 0,
                "contrast is present without pointer hover or keyboard focus");
            var title = f.Text(model.Title);
            var titleBounds = f.Bounds(title);
            var cover = f.Bounds(f.Cover);
            double sampleX = f.ContrastProbeX;
            foreach (double scale in new[] { 1d, 1.5d, 2d })
            {
                var frame = Capture(f.Entry, scale);
                var middle = BrightestCoverSample(frame, cover, sampleX);
                Check(Luminance(middle) > Luminance(BrightCover) * .8,
                    "the center of the cover is preserved instead of applying a flat dark fill");
                Check(PaintContrast(frame, titleBounds, sampleX) > 1.15,
                    $"{model.Presentation} at {scale}x: white title glyphs remain visible with the requested light protection");
                var header = frame.At(new Point(sampleX, 12));
                Check(Luminance(header) > Luminance(BrightCover) * .5,
                    "the title region is lightly protected, not replaced by an opaque dark band");

                var lastCover = frame.At(new Point(sampleX, cover.Bottom - 2));
                var firstFooter = frame.At(new Point(sampleX, cover.Bottom + 2));
                Check(Math.Abs(Luminance(lastCover) - Luminance(middle)) < .04,
                    "the resting cover remains intact through its bottom edge");
                Check(Contrast(lastCover, firstFooter) > 1.2,
                    "the cover and footer retain a clear boundary instead of a permanent fade");
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
                CheckWholeGlyph(f, f.CaptionText, Capture(f.Entry, scale));
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
        var dark = Color.FromRgb(28, 22, 20);
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
            Check(shade.GradientStops[0].Color.A is >= 96 and <= 102 &&
                shade.GradientStops[^1].Color.A == 0 && decorated.TitleShade.ActualHeight <= 64,
                "the bright decoration receives only a light, short title gradient");
            var frame = Capture(decorated.Entry, 1);
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
                shade.GradientStops[0].Color.A is >= 48 and <= 60,
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
                Check(f.Play.IsEnabled && f.Play.IsVisible && f.Play.Opacity > 0 && f.Play.IsHitTestVisible,
                    "a playable expanded entry exposes its explicit play control");
                var play = f.HitButton(Center(f.Bounds(f.Play)));
                Check(ReferenceEquals(play, f.Play), "the expanded play control owns its hit region above the fades");
                Check(PaintContrast(Capture(f.Entry), f.Bounds(f.Play), f.ContrastProbeX) > 1.5,
                    "the white play glyph paints visible pixels on the muted content surface");
                Click(play);
                Check(f.Plays == 1 && f.Opens == 1 && f.LastIntent == model,
                    "play is one separate intent and never also opens the card");
            }
            Check(f.UnhandledClicks == 0 && f.Loader.Addresses.Count == 1,
                "card clicks are handled without parent click leakage or extra preview/artwork requests");
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
            Check(PaintContrast(Capture(f.Entry), bounds, f.ContrastProbeX) > 1.15,
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
            bool rejected = false;
            try { ReadDaily(invalid); }
            catch (MusicOperationException error) when (error.Code == "INVALID_RESPONSE") { rejected = true; }
            Check(rejected, "malformed or out-of-range provider dates are rejected before presentation");
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
            var expanded = Capture(f.Entry, scale);
            foreach (string text in expected)
            {
                var row = f.Text(text);
                Check(IsPainted(row, f.Entry) &&
                    PaintContrast(expanded, f.Bounds(row), f.ContrastProbeX) > 1.5,
                    "each of the first three preview songs paints visible glyph pixels");
                CheckWholeGlyph(f, row, expanded);
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
            AssertExpanded();
            f.Entry.Width = width + 16; f.Window.Width += 16; f.Window.Height += 16;
            f.Window.UpdateLayout(); await Drain(); f.Window.UpdateLayout();
            AssertExpanded();
            CardMotion.Transition(f.Surface, false, true);
            await Task.Delay(40); await Drain();
            double partial = CardMotion.GetProgress(f.Surface);
            Check(partial is > 0 and < 1, "collapse uses the native presentation animation");
            CardMotion.Transition(f.Surface, true, true);
            // WPF can expose the target base value before the replacement clock's
            // first tick. Observe that clock before accepting its final value.
            await Until(() => CardMotion.GetProgress(f.Surface) is > 0 and < .999,
                "reversal starts from the intermediate presentation state");
            await Until(() => CardMotion.GetProgress(f.Surface) >= .999, "reversed animation settles at the expanded endpoint");
            AssertExpanded();
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

            void AssertExpanded()
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
                    "third row ends at the normal six DIP inset, not an extra blank line");
                var header = f.Bounds(f.Text("每日推荐"));
                Check(f.Entry.ActualHeight == height + f.Entry.Width - width &&
                    header.Location == title.Location && header.Height == title.Height,
                    "hover does not change outer height or translate the header");
                foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    var frame = Capture(f.Entry, scale);
                    CheckWholeGlyph(f, f.CaptionText, frame);
                    foreach (string name in new[] { "1 第一首", "2 第二首", "3 Waking Up" })
                        CheckWholeGlyph(f, f.Text(name), frame);
                }
            }
        }
    }

    private static async Task ColorSurfaces()
    {
        var images = new[]
        {
            MusicEntryPaletteTests.CoolArtwork(),
            MusicEntryPaletteTests.Image((x, y) => Color.FromRgb(206, 145, 99)),
            MusicEntryPaletteTests.Image((x, y) => Color.FromRgb(48, 33, 28)),
            MusicEntryPaletteTests.Image((x, y) => Color.FromRgb(138, 55, 218)),
            MusicEntryPaletteTests.Image((x, y) => Color.FromRgb(245, 245, 245))
        };
        var snapshots = new List<(BitmapSource Resting, BitmapSource Expanded)>();
        foreach (var image in images)
        {
            using var f = new Fixture(DailyCard() with { Subtitle = "每日推荐 | 从「Fell In Love」听起" },
                152, coverImage: image);
            await f.Load();
            var surface = (SolidColorBrush)f.Surface.Background;
            var shade = (Border)f.Entry.FindName("ExpansionShade");
            var rest = Capture(f.Entry);
            var footer = new Point(f.Entry.ActualWidth / 2, f.Entry.ActualHeight - 3);
            Check(rest.At(footer) == surface.Color, "the real footer paints the selected image tone");
            Check(ReferenceEquals(surface, shade.Background), "hover and footer use one color source");
            await f.Expand(true);
            var expanded = Capture(f.Entry);
            Check(expanded.At(footer) == surface.Color && ReferenceEquals(surface, f.Surface.Background) &&
                ReferenceEquals(image, f.Cover.Source), "hover reuses the same palette and image");
            Check(PaintContrast(expanded, f.Bounds(f.Text("3 Waking Up")), f.ContrastProbeX) > 1.5,
                "the third song is actually painted over the muted surface");
            snapshots.Add((rest.Image, expanded.Image));
            DesktopTheme.SetIsHighContrast(f.Entry, true); await Drain(); f.Window.UpdateLayout();
            Check(((SolidColorBrush)f.Surface.Background).Color == SystemColors.WindowColor &&
                ((SolidColorBrush)f.Text("每日推荐").Foreground).Color == SystemColors.WindowTextColor &&
                ((SolidColorBrush)f.CaptionText.Foreground).Color == SystemColors.WindowTextColor,
                "system high contrast replaces the light image palette and foreground together");
            Check(Capture(f.Entry).At(new Point(3, 80)) == SystemColors.WindowColor,
                "high contrast paints an opaque system backdrop, not photo pixels");
        }
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1") SaveVisuals(snapshots);
    }

    private static async Task CoverOnlyHover()
    {
        var cover = Color.FromRgb(112, 168, 210);
        var snapshots = new List<(BitmapSource Resting, BitmapSource Expanded)>();
        foreach (var model in Cards().Where(card => !card.HasPreview && card.CanPlay)
                     .Append(DailyCard() with { Preview = null }))
        {
            using var f = new Fixture(model, 152, coverColor: cover);
            await f.Load();
            var rest = Capture(f.Entry);
            var caption = f.Bounds(f.CaptionText);
            var source = f.Cover.Source;
            var palette = f.Surface.Background;
            var coverShade = (Border)f.Entry.FindName("CoverShade");
            var expansion = (Border)f.Entry.FindName("ExpansionShade");
            await f.Expand(true);
            var expanded = Capture(f.Entry);
            Check(coverShade.IsVisible && coverShade.Opacity == 1 && !expansion.IsVisible,
                "a playable entry without songs uses only the cover shade, not the colored expansion");
            Check(f.Bounds(coverShade) == f.Bounds(f.Cover) && f.Bounds(f.CaptionText) == caption &&
                ReferenceEquals(source, f.Cover.Source) && ReferenceEquals(palette, f.Surface.Background),
                "cover shading neither reaches the footer nor moves text or replaces artwork");
            var sample = new Point(5, 100);
            var shaded = expanded.At(sample);
            var expected = Color.FromRgb((byte)Math.Round(cover.R * 199 / 255d),
                (byte)Math.Round(cover.G * 199 / 255d), (byte)Math.Round(cover.B * 199 / 255d));
            Check(Math.Abs(shaded.R - expected.R) <= 1 && Math.Abs(shaded.G - expected.G) <= 1 &&
                Math.Abs(shaded.B - expected.B) <= 1, "the actual cover receives the existing playlist's neutral 0x38 shade");
            Check(expanded.At(new Point(5, 190)) == rest.At(new Point(5, 190)),
                "the footer pixels stay unchanged while the cover dims");
            Check(ReferenceEquals(f.HitButton(new Point(5, 100)), f.Open) &&
                ReferenceEquals(f.HitButton(Center(f.Bounds(f.Play))), f.Play),
                "reusing the playlist shade does not import its full-cover play hit target");
            snapshots.Add((rest.Image, expanded.Image));
            DesktopTheme.SetIsHighContrast(f.Entry, true); await Drain(); f.Window.UpdateLayout();
            Check(Capture(f.Entry).At(sample) == SystemColors.WindowColor &&
                ((SolidColorBrush)f.Text(model.Title).Foreground).Color == SystemColors.WindowTextColor,
                "no-list entries retain opaque system backgrounds and foregrounds in high contrast");
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "hover shading and high contrast add no artwork reads or business intents");
        }
        // Compare to the actual ordinary playlist template, not just a resource value.
        var view = new MusicCatalogView();
        var ordinary = (Button)((DataTemplate)view.FindResource("Catalog.PlaylistCard")).LoadContent();
        ordinary.DataContext = DailyCard();
        ArtworkScope.SetLoader(ordinary, new ArtworkLoader(cover, false, null));
        var window = new Window { Content = ordinary, Width = 184, Height = 260, WindowStyle = WindowStyle.None,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var artwork = Visuals(ordinary).OfType<ArtworkImage>().Single();
            await Until(() => artwork.Source is not null, "the ordinary playlist cover is acquired");
            CardMotion.Transition(ordinary, true, false); await Drain(); window.UpdateLayout();
            var image = Capture(ordinary);
            var actual = image.At(artwork.TranslatePoint(new Point(5, 100), ordinary));
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
            await Drain(); f.Window.UpdateLayout();
            Check(((Border)f.Entry.FindName("ExpansionShade")).IsVisible &&
                !((Border)f.Entry.FindName("CoverShade")).IsVisible &&
                f.Bounds(f.CaptionText).Top < caption.Top && PaintedTexts(f.Entry).Contains("1 后来到达"),
                "initialized songs switch to list expansion even when exactly one song exists");
            await f.Bind(Cards().Single(card => card.Kind == "fm")); await f.Expand(true);
            Check(!((Border)f.Entry.FindName("ExpansionShade")).IsVisible &&
                ((Border)f.Entry.FindName("CoverShade")).IsVisible &&
                !PaintedTexts(f.Entry).Contains("1 后来到达"),
                "rebinding to FM clears the list expansion without retaining the old shade");
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "initialization and rebind do not introduce reads or playback intent");
        }
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1")
            SaveVisuals(snapshots, "entry-cover-hover.png");
    }

    private static async Task TitleLayering()
    {
        foreach (var model in new[] { DailyCard(), Cards().Single(card => card.Kind == "fm"),
                     Cards().First(card => card.Kind == "similar") })
        {
            using var f = new Fixture(model, 152, coverColor: Color.FromRgb(95, 150, 198));
            await f.Load();
            var title = f.Text(model.Title);
            var position = f.Bounds(title);
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            {
                await f.Expand(false);
                var resting = Capture(f.Entry, scale);
                await f.Expand(true);
                var expanded = Capture(f.Entry, scale);
                int solidInk = 0;
                for (int y = (int)Math.Ceiling(position.Top * scale); y < (int)Math.Floor(position.Bottom * scale); y++)
                for (int x = (int)Math.Ceiling(position.Left * scale); x < (int)Math.Floor(position.Right * scale); x++)
                {
                    var before = resting.Pixel(x, y);
                    if (Math.Min(before.R, Math.Min(before.G, before.B)) < 254) continue;
                    var after = expanded.Pixel(x, y);
                    Check(Math.Abs(after.R - before.R) <= 1 && Math.Abs(after.G - before.G) <= 1 &&
                        Math.Abs(after.B - before.B) <= 1, "hover masks cannot tint the actual white title ink");
                    solidInk++;
                }
                Check(solidInk >= 15 && f.Bounds(title) == position,
                    "real topmost white title glyphs remain in the same position at every DPI");
            }
        }
        using var bright = new Fixture(DailyCard(), 152);
        await bright.Load();
        var shade = (LinearGradientBrush)bright.TitleShade.Background;
        Check(shade.GradientStops[0].Color.A is >= 96 and <= 102 &&
            shade.GradientStops[^1].Color.A == 0 && bright.TitleShade.ActualHeight == 64,
            "bright covers receive slightly more top protection without growing its extent");
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
                Capture(f.Entry);
                Hover(f.Entry, false);
                await f.Expand(false);
                Capture(f.Entry);
            }
            Check(f.Open.Focus(), "the card open control remains keyboard reachable");
            await Until(() => CardMotion.GetProgress(f.Surface) >= .999, "keyboard focus expands the card");
            Check(f.Root.Focus(), "keyboard focus can leave the card");
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
            Check(!f.Surface.HasAnimatedProperties, "hidden entries stop their hover clock");
            f.Entry.Visibility = Visibility.Visible;
            f.Window.UpdateLayout(); await Drain();
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
        await Drain(); lazy.Window.UpdateLayout();
        Check(PaintedTexts(lazy.Entry).Contains("1 初始化曲目"), "page-owned data publishes through binding");
        Hover(lazy.Entry, false);
        Check(initialized.PreviewTitles.SequenceEqual(["1 初始化曲目"]), "leaving does not retire initialized data");
    }

    private static MusicCatalogCard DailyCard() =>
        new("daily", "每日推荐", "符合你口味的新鲜好歌", CoverAddress, "daily", "wy", ActionId: "daily",
            Preview: new[] { "第一首", "第二首", "Waking Up", "第四首" }
                .Select((title, i) => new MusicRankPreview(title, "歌手", "", i + 1, 0)).ToArray(),
            Presentation: MusicCatalogEntryPresentation.Preview,
            DayOfMonth: 29);

    private static IEnumerable<MusicCatalogCard> Cards()
    {
        yield return DailyCard();
        yield return DailyCard() with { Id = "daily-style", Title = "电音日推", DayOfMonth = null };
        yield return new("similar", "心动模式", "你的红心歌曲和相似推荐", CoverAddress, "similar", "wy",
            ActionId: "similar", Presentation: MusicCatalogEntryPresentation.Overlay);
        yield return new("similar-song", "相似歌曲", "从你喜欢的歌听起", CoverAddress, "similar", "wy",
            ActionId: "similar", Presentation: MusicCatalogEntryPresentation.Overlay);
        yield return new("fm", "私人雷达", "你爱的歌值得反复聆听", CoverAddress, "fm", "wy",
            ActionId: "fm", Presentation: MusicCatalogEntryPresentation.Animated);
        yield return new("china", "国风频道", "国风歌曲", CoverAddress, "channel", "wy",
            ActionId: "china", PageCode: "MUSIC_CHINA", Presentation: MusicCatalogEntryPresentation.Static);
    }

    private static MusicCatalogCard ReadDaily(object? day, bool includeDay = true)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = "daily", ["name"] = "每日推荐", ["subtitle"] = "符合你口味的新鲜好歌",
            ["cover"] = CoverAddress, ["presentation"] = "preview", ["action"] = new { type = "daily" }
        };
        if (includeDay) entry["dayOfMonth"] = day;
        var reply = JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] { entry } });
        return MusicCatalogDto.Read(reply, "wy", "home", "").Single().Items.Single();
    }

    private static void Hover(EntryCard entry, bool active) =>
        entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = active ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });

    private static void Click(Button button)
    {
        Check(button.IsEnabled, "only an enabled hit target can be invoked");
        var click = new RoutedEventArgs(Button.ClickEvent);
        button.RaiseEvent(click);
        Check(click.Handled, "entry button clicks are consumed at their intent boundary");
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
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && (!visual.IsVisible || visual.Opacity <= .001)) return false;
            if (ReferenceEquals(current, root)) break;
        }
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var visible = Rect.Intersect(bounds, new Rect(root.RenderSize));
        return !visible.IsEmpty && visible.Width > 0 && visible.Height > 0;
    }

    private static string[] PaintedTexts(FrameworkElement root) =>
        Visuals(root).OfType<TextBlock>().Where(text => IsPainted(text, root)).Select(text => text.Text).ToArray();

    private static Frame Capture(FrameworkElement element, double scale = 1)
    {
        element.UpdateLayout();
        Check(element.ActualWidth > 0 && element.ActualHeight > 0, "the actual WPF card has a measured render surface");
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element);
        image.Freeze();
        return new Frame(image, scale);
    }

    private sealed class Frame
    {
        internal readonly BitmapSource Image;
        internal readonly double Scale;
        private readonly byte[] pixels;
        internal Frame(BitmapSource image, double scale)
        {
            Image = image; Scale = scale;
            pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        }
        internal Color At(Point point) => Pixel((int)(point.X * Scale), (int)(point.Y * Scale));
        internal Color Pixel(int x, int y)
        {
            Check(x >= 0 && x < Image.PixelWidth && y >= 0 && y < Image.PixelHeight, "pixel probe stays within the card");
            int index = (y * Image.PixelWidth + x) * 4;
            Check(pixels[index + 3] >= 250, "contrast probes read painted opaque card pixels");
            return Color.FromRgb(pixels[index + 2], pixels[index + 1], pixels[index]);
        }
    }

    private static double PaintContrast(Frame frame, Rect bounds, double backgroundX)
    {
        var region = Rect.Intersect(bounds,
            new Rect(0, 0, frame.Image.PixelWidth / frame.Scale, frame.Image.PixelHeight / frame.Scale));
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

    private static void CheckWholeGlyph(Fixture f, TextBlock row, Frame frame)
    {
        // A 25-DIP row may extend beyond the card while its shorter glyphs still
        // fit. Measure actual raster ink without the card's ancestor clipping.
        var reference = new TextBlock
        {
            Text = row.Text, FontFamily = row.FontFamily, FontSize = row.FontSize,
            FontStyle = row.FontStyle, FontWeight = row.FontWeight, FontStretch = row.FontStretch,
            Foreground = Brushes.White, FlowDirection = row.FlowDirection, Language = row.Language,
            Padding = row.Padding, TextAlignment = row.TextAlignment, TextWrapping = row.TextWrapping,
            TextTrimming = row.TextTrimming, LineHeight = row.LineHeight,
            LineStackingStrategy = row.LineStackingStrategy, UseLayoutRounding = row.UseLayoutRounding,
            SnapsToDevicePixels = row.SnapsToDevicePixels
        };
        TextOptions.SetTextFormattingMode(reference, TextOptions.GetTextFormattingMode(row));
        TextOptions.SetTextRenderingMode(reference, TextOptions.GetTextRenderingMode(row));
        TextOptions.SetTextHintingMode(reference, TextOptions.GetTextHintingMode(row));
        reference.Measure(row.RenderSize); reference.Arrange(new Rect(row.RenderSize)); reference.UpdateLayout();
        double scale = frame.Scale;
        var image = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth * scale),
            (int)Math.Ceiling(row.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(reference);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int left = image.PixelWidth, top = image.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < image.PixelHeight; y++)
        for (int x = 0; x < image.PixelWidth; x++)
            if (pixels[(y * image.PixelWidth + x) * 4 + 3] >= 16)
            {
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        Check(bottom >= top && right >= left, "the unclipped reference contains actual text glyphs");
        var ink = row.TransformToAncestor(f.Entry).TransformBounds(
            new Rect(left / scale, top / scale, (right - left + 1) / scale, (bottom - top + 1) / scale));
        var viewport = Rect.Intersect(f.Bounds(f.Surface), new Rect(f.Entry.RenderSize));
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

    private static void SaveVisuals(IReadOnlyList<(BitmapSource Resting, BitmapSource Expanded)> snapshots,
        string fileName = "entry-cards.png")
    {
        Check(snapshots.Count > 0, "the explicitly requested visual sheet contains real rendered cards");
        double width = snapshots.Max(pair => pair.Resting.Width), height = snapshots.Max(pair => pair.Resting.Height);
        const double gap = 16;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, (width + gap) * snapshots.Count + gap, height * 2 + gap * 3));
            for (int i = 0; i < snapshots.Count; i++)
            {
                drawing.DrawImage(snapshots[i].Resting, new Rect(gap + (width + gap) * i, gap, width, height));
                drawing.DrawImage(snapshots[i].Expanded, new Rect(gap + (width + gap) * i, height + gap * 2, width, height));
            }
        }
        var sheet = new RenderTargetBitmap((int)Math.Ceiling((width + gap) * snapshots.Count + gap),
            (int)Math.Ceiling(height * 2 + gap * 3), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        string path = TestWorkspace.ValidatePath(TestWorkspace.ResultsRoot,
            Path.Combine(TestWorkspace.ResultsRoot, "windows-native", fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        encoder.Save(stream);
        Console.WriteLine("VISUAL " + path);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> done, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!done()) await Task.Delay(1);
        }
        await Drain();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Grid Root = new() { Background = Brushes.Transparent, Focusable = true };
        internal readonly EntryCard Entry;
        internal readonly Window Window;
        internal readonly ArtworkLoader Loader;
        internal int Opens, Plays, UnhandledClicks;
        internal MusicCatalogCard? LastIntent;
        internal Grid Surface => (Grid)Entry.FindName("Surface");
        internal Button Open => (Button)Entry.FindName("OpenButton");
        internal Button Play => (Button)Entry.FindName("PlayButton");
        internal ArtworkImage Cover => (ArtworkImage)Entry.FindName("Cover");
        internal ArtworkImage Decoration => (ArtworkImage)Entry.FindName("DecorationLayer");
        internal Border TitleShade => (Border)Entry.FindName("TitleShade");
        internal TextBlock CaptionText => (TextBlock)Entry.FindName("Caption");
        internal double ContrastProbeX => Entry.ActualWidth - ((RectangleGeometry)Surface.Clip).RadiusX - 1;

        internal Fixture(MusicCatalogCard model, double width = 168, Color? coverColor = null,
            bool failDecoration = false, BitmapSource? coverImage = null)
        {
            Root.Resources.MergedDictionaries.Add(new()
                { Source = new("/MeowFilm.Capabilities;component/Presentation/CapabilityTheme.xaml", UriKind.Relative) });
            Loader = new ArtworkLoader(coverColor ?? BrightCover, failDecoration, coverImage);
            ArtworkScope.SetLoader(Root, Loader);
            Entry = new EntryCard { Width = width, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, DataContext = model };
            Entry.OpenRequested += (_, args) => { Check(ReferenceEquals(args.OriginalSource, Entry), "open intent belongs to the card"); Opens++; LastIntent = (MusicCatalogCard)Entry.DataContext; };
            Entry.PlayRequested += (_, args) => { Check(ReferenceEquals(args.OriginalSource, Entry), "play intent belongs to the card"); Plays++; LastIntent = (MusicCatalogCard)Entry.DataContext; };
            Root.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) => UnhandledClicks++));
            Root.Children.Add(Entry);
            Window = new Window
            {
                Content = Root, Width = width + 24, Height = width + 96, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
        }

        internal async Task Load()
        {
            Window.Show(); Window.UpdateLayout(); Root.Focus();
            var expectedReads = ((MusicCatalogCard)Entry.DataContext).Decoration.Length > 0 ? 2 : 1;
            await Until(() => Visuals(Cover).OfType<Image>().Any(image => image.Source is not null) &&
                Loader.Addresses.Count == expectedReads && CardMotion.GetProgress(Surface) <= .001,
                "the real WPF card loads its declared artwork at rest");
            Window.UpdateLayout();
        }

        internal TextBlock Text(string text) => Visuals(Entry).OfType<TextBlock>().Single(block => block.Text == text);
        internal Rect Bounds(FrameworkElement element) =>
            element.TransformToAncestor(Entry).TransformBounds(new Rect(element.RenderSize));
        internal Button HitButton(Point point)
        {
            for (DependencyObject? hit = Entry.InputHitTest(point) as DependencyObject; hit is not null;
                hit = VisualTreeHelper.GetParent(hit))
                if (hit is Button button) return button;
            throw new InvalidOperationException("No entry action owns the actual WPF hit point " + point);
        }
        internal async Task Expand(bool active)
        {
            CardMotion.Transition(Surface, active, false);
            await Drain(); Window.UpdateLayout();
        }
        internal async Task Bind(MusicCatalogCard model)
        {
            Entry.DataContext = model;
            await Drain(); Window.UpdateLayout();
        }
        public void Dispose() => Window.Close();
    }

    private sealed class ArtworkLoader : IArtworkLoader
    {
        internal readonly List<string> Addresses = [];
        internal readonly List<ArtworkLease> Leases = [];
        private readonly BitmapSource cover;
        private readonly BitmapSource decoration;
        private readonly bool failDecoration;
        internal ArtworkLoader(Color coverColor, bool failDecoration, BitmapSource? coverImage)
        {
            this.failDecoration = failDecoration;
            cover = coverImage ?? BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { coverColor.B, coverColor.G, coverColor.R, 255 }, 4);
            cover.Freeze();
            var decorationPixels = Enumerable.Range(0, 8)
                .SelectMany(_ => new byte[] { 255, 255, 255, 255 }).ToArray();
            decoration = BitmapSource.Create(1, 8, 96, 96, PixelFormats.Bgra32, null,
                decorationPixels, 4);
            decoration.Freeze();
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

    private sealed class ArtworkLease(BitmapSource image) : IArtworkLease
    {
        internal bool Disposed;
        public BitmapSource Image => !Disposed ? image : throw new ObjectDisposedException(nameof(ArtworkLease));
        public void Dispose() => Disposed = true;
    }
}
