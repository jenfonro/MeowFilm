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
    private static readonly Color BrightCover = Color.FromRgb(250, 248, 245);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("resting bright covers paint readable titles and a continuous footer fade", BrightCoverContrast);
        yield return Case("decorative fades preserve open and explicit play hit regions", HitAndPlayIntent);
        yield return Case("daily calendar days render valid single and double digit dates", CalendarDays);
        yield return Case("daily captions switch between collapsed and expanded text with only three embedded songs", DailyPreview);
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
            var cover = f.Bounds(f.Artwork);
            double sampleX = cover.Right - 5;
            foreach (double scale in new[] { 1d, 1.5d, 2d })
            {
                var frame = Capture(f.Entry, scale);
                var behindTitle = frame.At(new Point(sampleX, titleBounds.Top + titleBounds.Height / 2));
                var middle = BrightestCoverSample(frame, cover, sampleX);
                Check(Contrast(behindTitle, middle) > 1.5,
                    $"{model.Presentation} at {scale}x: the resting title backdrop darkens the bright cover");
                Check(Luminance(middle) > Luminance(BrightCover) * .8,
                    "the center of the cover is preserved instead of applying a flat dark fill");
                Check(PaintContrast(frame, titleBounds, sampleX) > 2,
                    $"{model.Presentation} at {scale}x: actual title glyph pixels contrast with their backdrop");

                var blend = frame.At(new Point(sampleX, cover.Top + cover.Height * .87));
                var lastCover = frame.At(new Point(sampleX, cover.Bottom - 2));
                var firstFooter = frame.At(new Point(sampleX, cover.Bottom + 2));
                double middleLight = Luminance(middle), blendLight = Luminance(blend);
                double footerLight = Luminance(firstFooter);
                Check(middleLight > blendLight && blendLight > footerLight,
                    "cover pixels progressively blend into the footer");
                Check(Math.Abs(Luminance(lastCover) - footerLight) < Math.Abs(middleLight - footerLight) * .3,
                    "the cover/footer seam is smaller than the full cover-to-footer contrast");
            }
            Check(f.Loader.Addresses.Count == 1 && f.Previews == 0 && f.Opens == 0 && f.Plays == 0,
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

    private static async Task HitAndPlayIntent()
    {
        foreach (var model in Cards())
        {
            using var f = new Fixture(model);
            await f.Load();
            Check(AutomationProperties.GetName(f.Open) == model.Title, "the title remains the accessible open name");
            var cover = f.Bounds(f.Artwork);
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
                Click(play);
                Check(f.Plays == 1 && f.Opens == 1 && f.LastIntent == model,
                    "play is one separate intent and never also opens the card");
            }
            Check(f.UnhandledClicks == 0 && f.Previews == 0 && f.Loader.Addresses.Count == 1,
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
            Check(PaintContrast(Capture(f.Entry), bounds, f.Entry.ActualWidth - 5) > 2,
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
        Check(f.Loader.Addresses.Count == 1 && f.Previews == 0, "date rebinding does not reacquire the cover or request songs");
    }

    private static async Task DailyPreview()
    {
        var model = DailyCard();
        using var f = new Fixture(model, 152);
        await f.Load();
        Check(f.Surface.ActualWidth == 152 && f.Entry.ActualHeight == 200,
            "daily preview is exercised in the standard 152 by 200 card viewport");
        string[] expected = ["1 第一首", "2 第二首", "3 Waking Up"];
        Check(f.Entry.PreviewTitles.SequenceEqual(expected), "the bound preview provides exactly the first three songs");
        Check(PaintedTexts(f.Entry).Contains(model.Subtitle) && !PaintedTexts(f.Entry).Contains(model.ExpandedSubtitle),
            "the collapsed card displays Subtitle rather than the expanded caption");
        Check(!PaintedTexts(f.Entry).Any(expected.Contains), "embedded song titles are not painted on a collapsed card");

        Hover(f.Entry, true);
        await f.Expand(true);
        Check(PaintedTexts(f.Entry).Contains(model.ExpandedSubtitle) && !PaintedTexts(f.Entry).Contains(model.Subtitle),
            "the expanded card replaces Subtitle with ExpandedSubtitle");
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            var expanded = Capture(f.Entry, scale);
            foreach (string text in expected)
            {
                var row = f.Text(text);
                Check(IsPainted(row, f.Entry) &&
                    PaintContrast(expanded, f.Bounds(row), f.Entry.ActualWidth - 5) > 1.5,
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

        var fallback = model with { PreviewSubtitle = "" };
        Check(fallback.ExpandedSubtitle == fallback.Subtitle, "missing expanded text falls back to the business subtitle");
        await f.Bind(fallback);
        await f.Expand(true);
        Check(PaintedTexts(f.Entry).Contains(fallback.Subtitle), "expanded fallback captions are not blank");
        var replacement = model with { Preview = [new("替换歌曲", "歌手", "", 1, 0)] };
        await f.Bind(replacement);
        await f.Expand(true);
        Check(f.Entry.PreviewTitles.SequenceEqual(new[] { "1 替换歌曲" }) &&
            !PaintedTexts(f.Entry).Any(expected.Contains), "rebinding replaces rather than appends embedded preview rows");
        await f.Bind(model with { Preview = null });
        Check(f.Entry.PreviewTitles.Count == 0 && !PaintedTexts(f.Entry).Contains("1 替换歌曲"),
            "rebinding without a preview clears the previous card's song titles");
        Check(f.Previews == 0 && f.Opens == 0 && f.Plays == 0 && f.Loader.Addresses.Count == 1,
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
                f.Previews == 0 && f.Opens == 0 && f.Plays == 0,
                $"{model.Presentation}: repeated render, hover and focus use only the acquired cover and embedded data");
            f.Entry.Visibility = Visibility.Collapsed;
            await Drain();
            Check(f.Loader.Leases.All(lease => lease.Disposed), "hidden entries release their acquired cover");
        }

        using var lazy = new Fixture(DailyCard() with { Preview = null });
        await lazy.Load();
        Check(lazy.Previews == 0, "a preview-capable card does not read songs merely because it is rendered");
        Hover(lazy.Entry, true);
        var token = lazy.Entry.PreviewCancellation;
        Check(lazy.Previews == 1 && !token.IsCancellationRequested, "missing embedded data retains the existing on-demand preview intent");
        Hover(lazy.Entry, true);
        await lazy.Expand(true);
        Capture(lazy.Entry);
        Check(lazy.Previews == 1 && lazy.Opens == 0 && lazy.Plays == 0 && lazy.Loader.Addresses.Count == 1,
            "the same active preview is not requested again by shading or animation");
        Hover(lazy.Entry, false);
        Check(token.IsCancellationRequested, "leaving the card cancels the current on-demand preview");
    }

    private static MusicCatalogCard DailyCard() =>
        new("daily", "每日推荐", "符合你口味的新鲜好歌", CoverAddress, "daily", "wy", ActionId: "daily",
            Preview: new[] { "第一首", "第二首", "Waking Up", "第四首" }
                .Select((title, i) => new MusicRankPreview(title, "歌手", "", i + 1, 0)).ToArray(),
            Presentation: MusicCatalogEntryPresentation.Preview,
            PreviewSubtitle: "每日推荐 | 从「第一首」听起", DayOfMonth: 29);

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
        Check(PaintContrast(frame, lastInk, f.Entry.ActualWidth - 5) > 1.1,
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
        internal readonly ArtworkLoader Loader = new();
        internal int Previews, Opens, Plays, UnhandledClicks;
        internal MusicCatalogCard? LastIntent;
        internal Grid Surface => (Grid)Entry.FindName("Surface");
        internal Button Open => (Button)Entry.FindName("OpenButton");
        internal Button Play => (Button)Entry.FindName("PlayButton");
        internal ArtworkImage Artwork => Visuals(Entry).OfType<ArtworkImage>().Single();

        internal Fixture(MusicCatalogCard model, double width = 168)
        {
            ArtworkScope.SetLoader(Root, Loader);
            Entry = new EntryCard { Width = width, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, DataContext = model };
            Entry.PreviewRequested += (_, _) => Previews++;
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
            await Until(() => Visuals(Artwork).OfType<Image>().Any(image => image.Source is not null) &&
                CardMotion.GetProgress(Surface) <= .001, "the real WPF card loads one bright cover at rest");
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
        private readonly BitmapSource image;
        internal ArtworkLoader()
        {
            image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { BrightCover.B, BrightCover.G, BrightCover.R, 255 }, 4);
            image.Freeze();
        }
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Addresses.Add(address);
            Check(address == CoverAddress, "presentation never acquires an undeclared artwork address");
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
