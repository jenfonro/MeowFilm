using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;

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
                await plain.Ready(); await VerifyFillAndStroke(plain);
                using var word = new Fixture(new MusicLyricWordView
                {
                    Word = new("未在播放", 1, 2), FontSize = size,
                    Stroke = new SolidColorBrush(Color.FromArgb(230, 25, 25, 25)), StrokeThickness = 1.1
                });
                await word.Ready(); await VerifyFillAndStroke(word);
                var plainPixels = await plain.Capture(); var wordPixels = await word.Capture();
                Check(plainPixels.SequenceEqual(wordPixels),
                    "ordinary and unhighlighted words paint the same native pixels");
            }
        });
        yield return Case("scaled and fractional layouts keep the stroke on the current glyph pixels", async () =>
        {
            using var f = new Fixture(new MusicDesktopLyricTextView { Text = "歌词 Hgj", FontSize = 32 });
            foreach (var (scale, phase) in new[] { (.67, .35), (1.5, .25), (2d, .45), (1d, 0d) })
            {
                f.Control.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
                Canvas.SetLeft(f.Control, 20 + phase); Canvas.SetTop(f.Control, 12 + phase);
                await f.Ready(); await VerifyFillAndStroke(f, scale);
            }
            var box = new Viewbox { Width = 105, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            f.PlaceIn(box); await f.Ready(); await VerifyFillAndStroke(f, 105 / f.Control.DesiredSize.Width);
            box.Width = 150; await f.Ready(); await VerifyFillAndStroke(f, 150 / f.Control.DesiredSize.Width);
        });
        yield return Case("text font direction and formatting changes refresh the outline without moving native fill", async () =>
        {
            var control = new MusicDesktopLyricTextView { Text = "原文 e\u0301", FontSize = 32 };
            using var f = new Fixture(control); await f.Ready();
            foreach (Action change in new Action[]
            {
                () => control.Text = "译文 Hgj", () => control.FontFamily = new FontFamily("Arial"),
                () => control.FontSize = 24, () => control.Language = "zh-CN",
                () => { control.Text = "العربية"; control.FlowDirection = FlowDirection.RightToLeft; }
            })
            {
                var before = f.Outline.Coverage; change(); await f.Ready();
                Check(!ReferenceEquals(before, f.Outline.Coverage), "changed native text inputs refresh coverage");
                Check(DesktopTestUi.Visuals(control).OfType<TextBlock>().All(text => text.FontWeight.Weight == 400),
                    "ordinary glyphs remain system regular text");
                await VerifyFillAndStroke(f);
            }
            control.Text = "   "; await f.Ready();
            Check((await f.Capture()).Where((_, index) => index % 4 == 3).All(alpha => alpha == 0),
                "whitespace has no outline rectangle");
            control.Text = ""; await f.Ready();
            Check((await f.Capture()).All(value => value == 0), "empty text is transparent");
        });
        yield return Case("colors high contrast and word progress reuse glyphs and outline while preserving clipping", async () =>
        {
            var word = new MusicLyricWordView
            {
                Word = new("实际逐字", 0, 2), FontSize = 32, Highlight = new SolidColorBrush(Colors.Gold),
                Stroke = new SolidColorBrush(Color.FromArgb(230, 25, 25, 25)), StrokeThickness = 1.1
            };
            using var f = new Fixture(word); await f.Ready();
            var mask = f.Outline.Coverage; var texts = DesktopTestUi.Visuals(word).OfType<TextBlock>().ToArray();
            var normal = await f.Capture(); word.Position = 1; await f.Ready(); var half = await f.Capture();
            Check(word.WordProgress == .5, "half the real word interval is highlighted");
            word.Position = 2; await f.Ready(); var full = await f.Capture();
            Check(word.WordProgress == 1 && !normal.SequenceEqual(half) && !half.SequenceEqual(full),
                "zero half and full progress paint different native foregrounds");
            double clipX = 20 + word.ActualWidth / 2; int left = 0, right = 0;
            for (int y = 0; y < Fixture.Height; y++)
            for (int x = 0; x < Fixture.Width; x++)
            {
                int p = (y * Fixture.Width + x) * 4;
                if (normal[p] != 255 || normal[p + 1] != 255 || normal[p + 2] != 255 || normal[p + 3] != 255) continue;
                if (x < clipX - 1) { Check(half[p] == full[p], "highlight stays inside the leading half"); left++; }
                if (x > clipX + 1) { Check(half[p] == normal[p], "the trailing half retains ordinary ink"); right++; }
            }
            Check(left > 0 && right > 0, "both clip halves contain real glyph interiors");
            word.Foreground = new SolidColorBrush(Colors.LightPink); word.Highlight = new SolidColorBrush(Colors.LightBlue);
            word.Stroke = new SolidColorBrush(Colors.Navy); Canvas.SetLeft(word, 21); await f.Ready();
            Check(ReferenceEquals(mask, f.Outline.Coverage), "color changes and integer movement reuse coverage");
            DesktopTheme.SetIsHighContrast(word, true); await f.Ready();
            var foreground = new UISettings().GetColorValue(UIColorType.Foreground);
            Check(texts.Any(text => text.Foreground is SolidColorBrush brush && brush.Color == foreground),
                "high contrast retains system foreground ink");
            Check(ReferenceEquals(mask, f.Outline.Coverage) && texts.SequenceEqual(DesktopTestUi.Visuals(word).OfType<TextBlock>()),
                "color and progress preserve coverage and the actual native text objects");
            word.StrokeThickness = 2; await f.Ready();
            Check(!ReferenceEquals(mask, f.Outline.Coverage), "stroke width refreshes only coverage");
            Check(texts.SequenceEqual(DesktopTestUi.Visuals(word).OfType<TextBlock>()), "stroke width does not rebuild text");
        });
        yield return Case("hidden unloaded and unoutlined words release outline drawing and resume current layout", async () =>
        {
            var word = new MusicLyricWordView { Word = new("详情文字", 1, 2), FontSize = 32 };
            using var f = new Fixture(word); await f.Ready();
            Check(f.Outline.Coverage is null, "detail text without a stroke has no outline surface");
            var nativePixels = await f.Capture(); var referencePixels = await f.ReferenceFrame();
            Check(nativePixels.SequenceEqual(referencePixels), "detail text stays native text-only");
            word.Stroke = new SolidColorBrush(Color.FromArgb(230, 25, 25, 25)); word.StrokeThickness = 1.1;
            await f.Ready(); var original = f.Outline.Coverage;
            word.Visibility = Visibility.Collapsed; word.RenderTransform = new ScaleTransform { ScaleX = .7, ScaleY = .7 };
            Canvas.SetLeft(word, 20.35); word.Visibility = Visibility.Visible; await f.Ready();
            Check(!ReferenceEquals(original, f.Outline.Coverage), "show uses the new device transform");
            await VerifyFillAndStroke(f, .7);
            f.Surface.Children.Remove(word); await f.Ready();
            Check(f.Outline.Coverage is null, "actual unloading releases the raster");
            f.Surface.Children.Add(word); await f.Ready(); await VerifyFillAndStroke(f, .7);
            word.Stroke = null; await f.Ready();
            nativePixels = await f.Capture(); referencePixels = await f.ReferenceFrame();
            Check(f.Outline.Coverage is null && nativePixels.SequenceEqual(referencePixels),
                "removing stroke releases its raster and restores native-only rendering");
            word.Stroke = new SolidColorBrush(Colors.Black); word.StrokeThickness = 0; await f.Ready();
            Check(f.Outline.Coverage is null, "zero stroke width also releases the raster");
        });
        yield return Case("framework word animation advances without rebuilding glyph or stroke pixels", async () =>
        {
            var word = new MusicLyricWordView
            {
                Word = new("连续高亮", 0, 8), FontSize = 32,
                Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1.1
            };
            using var f = new Fixture(word); await f.Ready();
            bool animating = word.Present(1, 1, true); var mask = f.Outline.Coverage;
            var texts = DesktopTestUi.Visuals(word).OfType<TextBlock>().ToArray(); double before = word.WordProgress;
            var picture = await NativeDesktopCapture.CaptureAsync(f.Surface);
            await Task.Delay(240); await DesktopTestUi.DrainAsync();
            if (new UISettings().AnimationsEnabled && !new AccessibilitySettings().HighContrast)
            {
                Check(animating && word.WordProgress > before + .015, "the native compositor clip advances between media samples");
                var advanced = await NativeDesktopCapture.CaptureAsync(f.Surface);
                Check(!picture.Pixels.SequenceEqual(advanced.Pixels),
                    "the advancing clip changes actual desktop pixels");
            }
            Check(ReferenceEquals(mask, f.Outline.Coverage) && texts.SequenceEqual(DesktopTestUi.Visuals(word).OfType<TextBlock>()),
                "animation reuses both retained resources");
            f.Surface.Children.Remove(word); await f.Ready(); double stopped = word.WordProgress;
            await Task.Delay(120);
            Check(Math.Abs(stopped - word.WordProgress) < .001 && f.Outline.Coverage is null,
                "unloading stops the retained clip clock and releases coverage");
        });
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("lyric outline: " + name, () => TestUiHost.InvokeAsync(run));
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task VerifyFillAndStroke(Fixture f, double scale = 1)
    {
        var result = await f.Capture(); var native = await f.ReferenceFrame(); var fill = await f.FillFrame();
        Check(fill.SequenceEqual(native), "the retained foreground exactly matches independent native text pixels");
        var n = InkBounds(native); var s = InkBounds(result); int reach = (int)Math.Ceiling(.55 * scale) + 1;
        Check(s.Left < n.Left && s.Right > n.Right && s.Top < n.Top && s.Bottom > n.Bottom,
            $"stroke surrounds all four sides of native ink: native={n}, stroke={s}");
        Check(n.Left - s.Left <= reach && s.Right - n.Right <= reach && n.Top - s.Top <= reach && s.Bottom - n.Bottom <= reach,
            "the outline remains narrow rather than creating a displaced shadow");
        int ink = 0, outside = 0;
        for (int p = 0; p < result.Length; p += 4)
        {
            Check(result[p + 3] >= native[p + 3], "the outline never removes native ink");
            if (native[p + 3] == 255)
                Check(result[p] == 255 && result[p + 1] == 255 && result[p + 2] == 255 && result[p + 3] == 255,
                    "the outline does not darken fully covered glyph interiors");
            if (native[p + 3] > 0) ink++; else if (result[p + 3] > 0) outside++;
        }
        Check(ink > 0 && outside > 0 && result[3] == 0 && result[^1] == 0,
            "native ink and outside stroke retain a transparent surrounding surface");
    }
    private static Rect InkBounds(byte[] pixels)
    {
        int left = Fixture.Width, top = Fixture.Height, right = -1, bottom = -1;
        for (int y = 0; y < Fixture.Height; y++)
        for (int x = 0; x < Fixture.Width; x++)
            if (pixels[(y * Fixture.Width + x) * 4 + 3] > 0)
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        Check(right >= left && bottom >= top, "expected real native text ink");
        return new(left, top, right - left + 1, bottom - top + 1);
    }
    private sealed class Fixture : IDisposable
    {
        internal const int Width = 900, Height = 160;
        internal Control Control { get; }
        internal Canvas Surface { get; } = new() { Width = Width, Height = Height };
        internal MusicLyricOutline Outline => Control is MusicDesktopLyricTextView plain ? plain.Outline : ((MusicLyricWordView)Control).Outline;
        private readonly Canvas referenceCanvas = new() { Width = Width, Height = Height };
        private readonly TextBlock reference = new();
        private readonly Grid root;
        private readonly DesktopWindow window;
        private Viewbox? box, referenceBox;
        internal Fixture(Control control)
        {
            Control = control; control.Foreground = new SolidColorBrush(Colors.White); control.FontFamily = new FontFamily("Segoe UI");
            Canvas.SetLeft(control, 20); Canvas.SetTop(control, 12); Surface.Children.Add(control); referenceCanvas.Children.Add(reference);
            root = new Grid { Width = Width, Height = Height, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16), Children = { referenceCanvas, Surface } }; referenceCanvas.Opacity = 0;
            window = new DesktopWindow { Content = new Grid { Children = { root } } };
            window.AttachRoot();
            NativeDesktopCapture.DisableWindowTransitions(window);
            ((Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter).SetBorderAndTitleBar(false, false);
            window.AppWindow.ResizeClient(new(Width + 32, Height + 32));
            window.Activate();
        }
        internal void PlaceIn(Viewbox viewbox)
        {
            Surface.Children.Remove(Control); box = viewbox; box.Child = Control;
            Canvas.SetLeft(box, 20); Canvas.SetTop(box, 12); Surface.Children.Add(box);
            referenceCanvas.Children.Remove(reference);
            referenceBox = new() { Child = reference, Stretch = viewbox.Stretch, StretchDirection = viewbox.StretchDirection };
            Canvas.SetLeft(referenceBox, 20); Canvas.SetTop(referenceBox, 12); referenceCanvas.Children.Add(referenceBox);
        }
        internal async Task Ready()
        {
            reference.Text = Control is MusicDesktopLyricTextView plain ? plain.Text : ((MusicLyricWordView)Control).Word?.Text ?? "";
            reference.FontFamily = Control.FontFamily; reference.FontSize = Control.FontSize;
            reference.FontWeight = Microsoft.UI.Text.FontWeights.Normal; reference.Foreground = new SolidColorBrush(Colors.White);
            reference.FlowDirection = Control.FlowDirection; reference.Language = Control.Language;
            reference.RenderTransform = Control.RenderTransform is ScaleTransform scale
                ? new ScaleTransform { ScaleX = scale.ScaleX, ScaleY = scale.ScaleY } : new ScaleTransform();
            if (box is null) { Canvas.SetLeft(reference, Canvas.GetLeft(Control)); Canvas.SetTop(reference, Canvas.GetTop(Control)); }
            else referenceBox!.Width = box.Width;
            DesktopTestUi.WindowRoot(window).UpdateLayout(); await TestUiHost.LoadedAsync(DesktopTestUi.WindowRoot(window));
            await DesktopTestUi.DrainAsync(); await Outline.RenderWork; await DesktopTestUi.DrainAsync();
            DesktopTestUi.WindowRoot(window).UpdateLayout();
        }
        internal Task<byte[]> Capture() => CaptureCanvas(false);
        internal Task<byte[]> ReferenceFrame() => CaptureCanvas(true);
        private async Task<byte[]> CaptureCanvas(bool referenceOnly)
        {
            var background = root.Background;
            double surfaceOpacity = Surface.Opacity, referenceOpacity = referenceCanvas.Opacity;
            try
            {
                Surface.Opacity = referenceOnly ? 0 : 1; referenceCanvas.Opacity = referenceOnly ? 1 : 0;
                root.Background = new SolidColorBrush(Colors.Black);
                var black = await NativeDesktopCapture.CaptureAsync(root);
                root.Background = new SolidColorBrush(Colors.White);
                var white = await NativeDesktopCapture.CaptureAsync(root);
                Check(black.Width == Width && black.Height == Height && white.Width == Width && white.Height == Height,
                    $"the reference and actual native lyric use the same complete device viewport; " +
                    $"black={black.Width}x{black.Height}, white={white.Width}x{white.Height}, root={root.ActualWidth}x{root.ActualHeight}");
                for (int at = 0; at < black.Pixels.Length; at += 4)
                {
                    int difference = 0;
                    for (int channel = 0; channel < 3; channel++)
                        difference = Math.Max(difference, Math.Abs(white.Pixels[at + channel] - black.Pixels[at + channel]));
                    black.Pixels[at + 3] = (byte)(255 - difference);
                }
                return black.Pixels;
            }
            finally
            {
                root.Background = background; Surface.Opacity = surfaceOpacity; referenceCanvas.Opacity = referenceOpacity;
            }
        }
        internal async Task<byte[]> FillFrame()
        {
            var images = DesktopTestUi.Visuals(Control).OfType<Image>().ToArray();
            foreach (var image in images) image.Visibility = Visibility.Collapsed;
            try { return await Capture(); }
            finally { foreach (var image in images) image.Visibility = Visibility.Visible; }
        }
        public void Dispose() { window.Content = null; window.Close(); }
    }
}
