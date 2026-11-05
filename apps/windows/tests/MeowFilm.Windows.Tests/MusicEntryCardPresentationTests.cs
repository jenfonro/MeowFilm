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
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class MusicEntryCardPresentationTests
{
    private const string CoverAddress = "https://art.example.test/entry-cover.png";
    private const string DecorationAddress = "https://art.example.test/entry-decoration.png";
    private static readonly Color BrightCover = Color.FromRgb(250, 248, 245);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("resting covers keep readable titles without blending into the footer", BrightCoverContrast);
        yield return Case("entry subtitles use one natural line and at most two wrapped lines", SubtitleWrapping);
        yield return Case("official decoration drives the title palette and failed decoration falls back to the cover", DecorationPalette);
        yield return Case("decorative fades preserve open and explicit play hit regions", HitAndPlayIntent);
        yield return Case("daily calendar days render valid single and double digit dates", CalendarDays);
        yield return Case("daily captions and covers stay stable with only three embedded songs", DailyPreview);
        yield return Case("entry presentations do not add artwork reads or implicit business requests", ReadOnlyPresentation);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-entry-card: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task BrightCoverContrast()
    {
        bool saveVisuals = Environment.GetEnvironmentVariable("MEOWFILM_TEST_ENTRY_CARD_VISUALS") == "1";
        var snapshots = new List<(BitmapSource Resting, BitmapSource Expanded)>();
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
                Check(PaintContrast(frame, titleBounds, sampleX) > 3,
                    $"{model.Presentation} at {scale}x: actual title glyph pixels contrast with their backdrop");

                var lastCover = frame.At(new Point(sampleX, cover.Bottom - 2));
                var firstFooter = frame.At(new Point(sampleX, cover.Bottom + 2));
                Check(Math.Abs(Luminance(lastCover) - Luminance(middle)) < .04,
                    "the resting cover remains intact through its bottom edge");
                Check(Contrast(lastCover, firstFooter) > 1.2,
                    "the cover and footer retain a clear boundary instead of a permanent fade");
            }
            Check(f.Loader.Addresses.Count == 1 && f.Opens == 0 && f.Plays == 0,
                "rendering all DPI variants uses one acquired cover and no business intent");
            if (saveVisuals && width == 152)
            {
                var resting = Capture(f.Entry).Image;
                await f.Expand(true);
                snapshots.Add((resting, Capture(f.Entry).Image));
            }
        }
        if (saveVisuals) SaveVisuals(snapshots);
    }

    private static async Task SubtitleWrapping()
    {
        var oneLine = DailyCard() with
        {
            Subtitle = "符合你口味的新鲜好歌"
        };
        using var f = new Fixture(oneLine, 168);
        await f.Load();
        var caption = f.CaptionText;
        Check(caption.ActualHeight <= 18.5 && caption.DesiredSize.Height - caption.Margin.Top - caption.Margin.Bottom <= 18.5,
            "a short caption does not reserve an empty second line");

        var twoLines = oneLine with
        {
            Subtitle = "根据最近常听与收藏，为你推荐今天值得听的歌曲"
        };
        await f.Bind(twoLines);
        caption = f.CaptionText;
        Check(caption.ActualHeight > 18.5 && caption.ActualHeight <= 36.5,
            "a longer resting caption wraps naturally and remains bounded to two lines");
        await f.Expand(true);
        Check(caption.ActualHeight > 18.5 && caption.ActualHeight <= 36.5,
            "the expanded caption follows the same two-line bound");
        Check(f.Opens == 0 && f.Plays == 0 && f.Loader.Addresses.Count == 1,
            "caption measurement does not trigger a business or artwork reread");
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
            Check(shade.GradientStops[0].Color.R < 100 && shade.GradientStops[0].Color.A >= 200,
                "the bright decoration receives a permanent tinted contrast guard");
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
            Check(shade.GradientStops[0].Color.R == dark.R && shade.GradientStops[0].Color.A >= 200,
                "the cover fallback receives the ordinary dark-title protection");
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
                new Point(cover.Right - 5, cover.Bottom - 3)
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
                Check(PaintContrast(Capture(f.Entry), f.Bounds(f.Play), f.ContrastProbeX) > 3,
                    "the white play glyph remains legible on bright artwork for every playable entry kind");
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
            Check(PaintContrast(Capture(f.Entry), bounds, f.ContrastProbeX) > 2,
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
        Check(f.Surface.ActualWidth == 152 && Math.Abs(f.Entry.ActualHeight - (152 + f.CaptionText.ActualHeight + 16)) < 1,
            "the card reserves only the square artwork and measured caption, without a fixed empty footer");
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
            Check(pixels is not null, "the retention check starts with acquired pixels");
            f.Entry.Visibility = Visibility.Collapsed;
            await Drain();
            Check(f.Entry.IsLoaded && f.Loader.Leases.All(lease => !lease.Disposed) &&
                ReferenceEquals(pixels, image.Source) && ReferenceEquals(palette, artwork.RawPaletteBrush),
                "hidden entries retain their acquired cover and palette");
            Check(!f.Surface.HasAnimatedProperties, "hidden entries stop their hover clock");
            f.Entry.Visibility = Visibility.Visible;
            f.Window.UpdateLayout(); await Drain();
            Check(f.Loader.Addresses.Count == reads && ReferenceEquals(pixels, image.Source) &&
                ReferenceEquals(palette, artwork.RawPaletteBrush), "returning reuses the cover without a new request");
            f.Root.Children.Remove(f.Entry); await Drain();
            Check(f.Loader.Leases.All(lease => lease.Disposed) && image.Source is null,
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
        yield return new("similar", "心动模式", "你的红心歌曲和相似推荐", CoverAddress, "similar", "wy",
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

    private static void SaveVisuals(IReadOnlyList<(BitmapSource Resting, BitmapSource Expanded)> snapshots)
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
            Path.Combine(TestWorkspace.ResultsRoot, "windows-native", "entry-cards.png"));
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
            bool failDecoration = false)
        {
            Loader = new ArtworkLoader(coverColor ?? BrightCover, failDecoration);
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
        internal ArtworkLoader(Color coverColor, bool failDecoration)
        {
            this.failDecoration = failDecoration;
            cover = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
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
