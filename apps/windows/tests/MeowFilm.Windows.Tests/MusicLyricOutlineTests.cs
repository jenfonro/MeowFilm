using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicLyricOutlineTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("status original translation and word retain native fill with a surrounding narrow stroke", async () =>
        {
            foreach (double size in new[] { 30d, 32d, 17d })
            {
                using var plain = new Fixture(new MusicDesktopLyricTextView { Text = "未在播放", FontSize = size });
                await plain.Ready();
                VerifyFillAndStroke(plain.Capture(), plain.ReferenceFrame(), plain.FillFrame());
                using var word = new Fixture(new MusicLyricWordView
                {
                    Word = new("未在播放", 1, 2), FontSize = size,
                    Stroke = new SolidColorBrush(Color.FromArgb(230, 25, 25, 25)), StrokeThickness = 1.1
                });
                await word.Ready();
                VerifyFillAndStroke(word.Capture(), word.ReferenceFrame(), word.FillFrame());
                Check(plain.Capture().SequenceEqual(word.Capture()), "ordinary and unhighlighted words paint the same pixels");
            }
        });
        yield return Case("scaled and fractional layouts keep the stroke on the current glyph pixels", async () =>
        {
            using var f = new Fixture(new MusicDesktopLyricTextView { Text = "歌词 Hgj", FontSize = 32 });
            await f.Ready();
            foreach (var (scale, phase) in new[] { (.67, .35), (1.5, .25), (2d, .45), (1d, 0d) })
            {
                f.Control.RenderTransform = new ScaleTransform(scale, scale);
                Canvas.SetLeft(f.Control, 20 + phase);
                Canvas.SetTop(f.Control, 12 + phase);
                await f.Ready();
                VerifyFillAndStroke(f.Capture(), f.ReferenceFrame(), f.FillFrame(), scale);
            }
            var box = new Viewbox { Width = 105, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            f.PlaceIn(box);
            await f.Ready();
            VerifyFillAndStroke(f.Capture(), f.ReferenceFrame(), f.FillFrame(), 105 / f.Control.DesiredSize.Width);
            box.Width = 150;
            await f.Ready();
            VerifyFillAndStroke(f.Capture(), f.ReferenceFrame(), f.FillFrame(), 150 / f.Control.DesiredSize.Width);
        });
        yield return Case("text font direction and formatting changes refresh the outline without moving native fill", async () =>
        {
            var control = new MusicDesktopLyricTextView { Text = "原文 e\u0301", FontSize = 32 };
            using var f = new Fixture(control);
            await f.Ready();
            foreach (Action change in new Action[]
            {
                () => control.Text = "译文 Hgj",
                () => control.FontFamily = new FontFamily("Arial"),
                () => control.FontSize = 24,
                () => control.FontStyle = FontStyles.Italic,
                () => control.FontWeight = FontWeights.Bold,
                () => control.FontStretch = FontStretches.Condensed,
                () => control.Language = XmlLanguage.GetLanguage("zh-CN"),
                () => TextOptions.SetTextFormattingMode(control, TextFormattingMode.Ideal),
                () => { control.Text = "العربية"; control.FlowDirection = FlowDirection.RightToLeft; }
            })
            {
                var before = Masks(control).Single();
                change();
                await f.Ready();
                Check(!ReferenceEquals(before, Masks(control).Single()), "changed text inputs refresh the coverage");
                VerifyFillAndStroke(f.Capture(), f.ReferenceFrame(), f.FillFrame());
            }
            control.Text = "   ";
            await f.Ready();
            Check(f.Capture().Where((_, index) => index % 4 == 3).All(alpha => alpha == 0), "whitespace has no outline rectangle");
            control.Text = "";
            await f.Ready();
            Check(f.Capture().All(value => value == 0), "empty text is transparent");
        });
        yield return Case("colors high contrast and word progress reuse glyphs and outline while preserving clipping", async () =>
        {
            var word = new MusicLyricWordView
            {
                Word = new("实际逐字", 0, 2), FontSize = 32, Highlight = Brushes.Gold,
                Stroke = new SolidColorBrush(Color.FromArgb(230, 25, 25, 25)), StrokeThickness = 1.1
            };
            using var f = new Fixture(word);
            await f.Ready();
            var mask = Masks(word).Single();
            var glyphs = Glyphs(word).Select(glyph => glyph.GlyphRun).ToArray();
            var normal = f.Capture();
            word.Position = 1;
            await f.Ready();
            Check(word.WordProgress == .5, "half the real word interval is highlighted");
            var half = f.Capture();
            word.Position = 2;
            await f.Ready();
            var full = f.Capture();
            Check(word.WordProgress == 1 && !normal.SequenceEqual(half) && !half.SequenceEqual(full),
                "zero half and full progress paint different native foregrounds");
            double clipX = 20 + word.DesiredSize.Width / 2;
            int checkedLeft = 0, checkedRight = 0;
            for (int y = 0; y < Fixture.Height; y++)
                for (int x = 0; x < Fixture.Width; x++)
                {
                    int p = (y * Fixture.Width + x) * 4;
                    if (normal[p] != 255 || normal[p + 1] != 255 || normal[p + 2] != 255 || normal[p + 3] != 255) continue;
                    if (x < clipX - 1) { Check(half[p] == full[p], "highlight stays inside the leading half"); checkedLeft++; }
                    if (x > clipX + 1) { Check(half[p] == normal[p], "the trailing half retains ordinary ink"); checkedRight++; }
                }
            Check(checkedLeft > 0 && checkedRight > 0, "both clip halves contain real glyph interiors");
            word.Foreground = Brushes.LightPink;
            word.Highlight = Brushes.LightBlue;
            word.Stroke = Brushes.Navy;
            await f.Ready();
            Canvas.SetLeft(word, 21);
            word.InvalidateVisual();
            await f.Ready();
            Check(ReferenceEquals(mask, Masks(word).Single()), "integer device movement reuses the coverage");
            DesktopTheme.SetIsHighContrast(word, true);
            await f.Ready();
            Check(Glyphs(word).Select(glyph => glyph.ForegroundBrush).OfType<SolidColorBrush>()
                .Any(brush => brush.Color == SystemColors.WindowTextColor), "high contrast retains system foreground ink");
            Check(ReferenceEquals(mask, Masks(word).Single()) &&
                glyphs.SequenceEqual(Glyphs(word).Select(glyph => glyph.GlyphRun)), "color and progress reuse both caches");
            word.StrokeThickness = 2;
            await f.Ready();
            Check(!ReferenceEquals(mask, Masks(word).Single()), "stroke width refreshes only the coverage");
            Check(glyphs.SequenceEqual(Glyphs(word).Select(glyph => glyph.GlyphRun)), "stroke width does not reshape text");
        });
        yield return Case("hidden unloaded and unoutlined words release outline drawing and resume current layout", async () =>
        {
            var word = new MusicLyricWordView { Word = new("详情文字", 1, 2), FontSize = 32 };
            using var f = new Fixture(word);
            await f.Ready();
            Check(!Masks(word).Any(), "detail text without a stroke has no outline surface");
            Check(f.Capture().SequenceEqual(f.ReferenceFrame()), "detail text stays native glyph-only");
            word.Stroke = new SolidColorBrush(Color.FromArgb(230, 25, 25, 25));
            word.StrokeThickness = 1.1;
            await f.Ready();
            var original = Masks(word).Single();
            word.Visibility = Visibility.Hidden;
            await f.Ready();
            word.RenderTransform = new ScaleTransform(.7, .7);
            Canvas.SetLeft(word, 20.35);
            word.Visibility = Visibility.Visible;
            await f.Ready();
            Check(!ReferenceEquals(original, Masks(word).Single()), "show uses the new device transform");
            VerifyFillAndStroke(f.Capture(), f.ReferenceFrame(), f.FillFrame(), .7);
            f.Surface.Children.Remove(word);
            await f.Ready();
            f.Surface.Children.Add(word);
            await f.Ready();
            VerifyFillAndStroke(f.Capture(), f.ReferenceFrame(), f.FillFrame(), .7);
            word.Stroke = null;
            await f.Ready();
            Check(!Masks(word).Any(), "removing stroke removes the outline surface");
            Check(f.Capture().SequenceEqual(f.ReferenceFrame()), "removing stroke restores native-only rendering");
            word.Stroke = Brushes.Black;
            word.StrokeThickness = 0;
            await f.Ready();
            Check(!Masks(word).Any(), "zero stroke width also releases the outline surface");
        });
        yield return Case("framework word animation advances without rebuilding glyph or stroke pixels", async () =>
        {
            var word = new MusicLyricWordView
            {
                Word = new("连续高亮", 0, 8), FontSize = 32, Stroke = Brushes.Black, StrokeThickness = 1.1
            };
            var list = new MusicLyricList
            {
                Position = 1, PlaybackRate = 1, IsPlaying = true, PresentationKey = "outline-clock",
                Template = new ControlTemplate(typeof(MusicLyricList))
                { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) }
            };
            list.Items.Add(word);
            var window = new DesktopWindow
            {
                Content = list, Width = 500, Height = 120, Left = -32000, Top = -32000,
                AllowsTransparency = true, WindowStyle = WindowStyle.None, Background = Brushes.Transparent,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var mask = Masks(word).Single();
                var glyphs = Glyphs(word).Select(glyph => glyph.GlyphRun).ToArray();
                double before = word.WordProgress;
                await Task.Delay(240);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                    Check(word.WordProgress > before + .015, "the framework clip advances between media samples");
                Check(ReferenceEquals(mask, Masks(word).Single()) &&
                    glyphs.SequenceEqual(Glyphs(word).Select(glyph => glyph.GlyphRun)), "animation reuses both caches");
                window.Content = null;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                double stopped = word.WordProgress;
                await Task.Delay(120);
                Check(Math.Abs(stopped - word.WordProgress) < .001, "unloading stops the retained clip clock");
            }
            finally { window.Close(); }
        });
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("lyric outline: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void VerifyFillAndStroke(byte[] result, byte[] native, byte[] fill, double scale = 1)
    {
        // Compare native coverage directly; inverting 8-bit source-over accumulates rounding error.
        Check(fill.SequenceEqual(native), "the retained foreground exactly matches independent native glyph pixels");
        int ink = 0, outside = 0;
        var nativeBounds = InkBounds(native);
        var strokeBounds = InkBounds(result);
        int reach = (int)Math.Ceiling(.55 * scale) + 1;
        Check(strokeBounds.Left < nativeBounds.Left && strokeBounds.Right > nativeBounds.Right &&
            strokeBounds.Top < nativeBounds.Top && strokeBounds.Bottom > nativeBounds.Bottom,
            $"stroke surrounds all four sides of the native ink: native={nativeBounds}, stroke={strokeBounds}");
        Check(nativeBounds.Left - strokeBounds.Left <= reach && strokeBounds.Right - nativeBounds.Right <= reach &&
            nativeBounds.Top - strokeBounds.Top <= reach && strokeBounds.Bottom - nativeBounds.Bottom <= reach,
            "the outline remains narrow rather than creating a displaced shadow");
        for (int p = 0; p < result.Length; p += 4)
        {
            Check(result[p + 3] >= native[p + 3], "the outline never removes native ink");
            if (native[p + 3] == 255)
                Check(result[p] == 255 && result[p + 1] == 255 && result[p + 2] == 255 && result[p + 3] == 255,
                    "the outline does not darken fully covered glyph interiors");
            if (native[p + 3] > 0) ink++;
            else if (result[p + 3] > 0) outside++;
        }
        Check(ink > 0 && outside > 0, "the fixture contains both native ink and outside stroke");
        Check(result[3] == 0 && result[^1] == 0, "the surrounding surface remains transparent");
    }

    private static Rect InkBounds(byte[] pixels)
    {
        int left = Fixture.Width, top = Fixture.Height, right = -1, bottom = -1;
        for (int y = 0; y < Fixture.Height; y++)
            for (int x = 0; x < Fixture.Width; x++)
                if (pixels[(y * Fixture.Width + x) * 4 + 3] > 0)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        Check(right >= left && bottom >= top, "expected real text ink");
        return new(left, top, right - left + 1, bottom - top + 1);
    }

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in Drawings(child)) yield return item;
    }
    private static IEnumerable<BitmapSource> Masks(Visual view) => Drawings(VisualTreeHelper.GetDrawing(view)!)
        .OfType<DrawingGroup>().Select(group => group.OpacityMask).OfType<ImageBrush>()
        .Select(brush => (BitmapSource)brush.ImageSource);
    private static IEnumerable<GlyphRunDrawing> Glyphs(Visual view) =>
        Drawings(VisualTreeHelper.GetDrawing(view)!).OfType<GlyphRunDrawing>();

    private sealed class Fixture : IDisposable
    {
        internal const int Width = 900, Height = 160;
        internal Control Control { get; }
        internal Canvas Surface { get; } = new() { Width = Width, Height = Height };
        private readonly Canvas referenceCanvas = new() { Width = Width, Height = Height };
        private readonly ReferenceText reference;
        private readonly Window window;
        private Viewbox? box, referenceBox;

        internal Fixture(Control control)
        {
            Control = control;
            Control.Foreground = Brushes.White;
            Control.FontFamily = SystemFonts.MessageFontFamily;
            Control.FontWeight = FontWeights.Normal;
            reference = new(control);
            Canvas.SetLeft(control, 20); Canvas.SetTop(control, 12);
            Surface.Children.Add(control);
            referenceCanvas.Children.Add(reference);
            window = new DesktopWindow
            {
                Content = new Grid { Children = { Surface, referenceCanvas } }, Width = Width, Height = Height,
                Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false,
                UseLayoutRounding = false, SnapsToDevicePixels = false
            };
            window.Show();
        }
        internal void PlaceIn(Viewbox viewbox)
        {
            Surface.Children.Remove(Control);
            box = viewbox;
            box.Child = Control;
            Canvas.SetLeft(box, 20); Canvas.SetTop(box, 12);
            Surface.Children.Add(box);
            referenceCanvas.Children.Remove(reference);
            referenceBox = new() { Child = reference, Stretch = viewbox.Stretch, StretchDirection = viewbox.StretchDirection };
            Canvas.SetLeft(referenceBox, 20); Canvas.SetTop(referenceBox, 12);
            referenceCanvas.Children.Add(referenceBox);
        }
        internal async Task Ready()
        {
            reference.FlowDirection = Control.FlowDirection;
            reference.RenderTransform = Control.RenderTransform.CloneCurrentValue();
            reference.InvalidateMeasure();
            reference.InvalidateVisual();
            if (box is null)
            {
                Canvas.SetLeft(reference, Canvas.GetLeft(Control));
                Canvas.SetTop(reference, Canvas.GetTop(Control));
            }
            else referenceBox!.Width = box.Width;
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        internal byte[] Capture() => Capture(Surface);
        internal byte[] ReferenceFrame() => Capture(referenceCanvas);
        internal byte[] FillFrame()
        {
            var fill = VisualTreeHelper.GetDrawing(Control)!.CloneCurrentValue();
            RemoveStroke(fill);
            var transform = Control.TransformToAncestor(Surface);
            Point origin = transform.Transform(new());
            Vector x = transform.Transform(new(1, 0)) - origin;
            Vector y = transform.Transform(new(0, 1)) - origin;
            var visual = new DrawingVisual();
            TextOptions.SetTextFormattingMode(visual, TextOptions.GetTextFormattingMode(Control));
            TextOptions.SetTextRenderingMode(visual, TextOptions.GetTextRenderingMode(Control));
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new MatrixTransform(x.X, x.Y, y.X, y.Y, origin.X, origin.Y));
                drawing.DrawDrawing(fill);
                drawing.Pop();
            }
            return Capture(visual);
            static void RemoveStroke(DrawingGroup group)
            {
                for (int i = group.Children.Count - 1; i >= 0; i--)
                    if (group.Children[i] is DrawingGroup child)
                    {
                        if (child.OpacityMask is not null) group.Children.RemoveAt(i);
                        else RemoveStroke(child);
                    }
            }
        }
        private static byte[] Capture(Visual visual)
        {
            var image = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            var pixels = new byte[Width * Height * 4];
            image.CopyPixels(pixels, Width * 4, 0);
            return pixels;
        }
        public void Dispose() => window.Close();
    }

    private sealed class ReferenceText(Control target) : FrameworkElement
    {
        private FormattedText Text() => new(target switch
        {
            MusicDesktopLyricTextView plain => plain.Text,
            MusicLyricWordView word => word.Word?.Text ?? "",
            _ => throw new InvalidOperationException()
        }, target.Language.GetSpecificCulture(), target.FlowDirection,
            new Typeface(target.FontFamily, target.FontStyle, target.FontWeight, target.FontStretch),
            target.FontSize, Brushes.White, null, TextOptions.GetTextFormattingMode(target),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        protected override Size MeasureOverride(Size constraint)
        {
            var text = Text();
            return new(text.WidthIncludingTrailingWhitespace, text.Height);
        }
        protected override void OnRender(DrawingContext drawing) => drawing.DrawText(Text(), new());
    }
}
