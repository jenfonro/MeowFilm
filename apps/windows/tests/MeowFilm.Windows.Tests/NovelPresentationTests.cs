using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeowFilm.Windows.Tests;

internal static class NovelTestUi
{
    internal static Task RasterOwnership()
    {
        var raster = new NovelPageRaster();
        var first = raster.Render(new Size(7, 5), 1, Red);
        var snapshot = raster.Snapshot() ?? throw new InvalidOperationException("Missing page snapshot.");
        var original = Pixels(snapshot);
        var second = raster.Render(new Size(7, 5), 1, Blue);
        if (!ReferenceEquals(first, second) || ReferenceEquals(first, snapshot) ||
            !Pixels(snapshot).SequenceEqual(original) || Pixels(second).SequenceEqual(original))
            throw new InvalidOperationException("Page buffer reuse mutated the previous animation frame.");
        var scaled = raster.Render(new Size(7, 5), 1.5, Red);
        if (scaled.PixelWidth != 11 || scaled.PixelHeight != 8 || ReferenceEquals(scaled, second))
            throw new InvalidOperationException("Page raster ignored XAML scale.");
        raster.Clear();
        if (raster.Snapshot() is not null) throw new InvalidOperationException("Retired page retained its raster.");
        var resumed = raster.Render(new Size(7, 5), 1.5, Blue);
        if (ReferenceEquals(resumed, scaled) || !Pixels(snapshot).SequenceEqual(original))
            throw new InvalidOperationException("Page retirement reused a retired source or mutated its snapshot.");
        raster.Clear();
        return Task.CompletedTask;

        static byte[] Pixels(WriteableBitmap bitmap)
        {
            using var reader = DataReader.FromBuffer(bitmap.PixelBuffer);
            var bytes = new byte[bitmap.PixelBuffer.Length]; reader.ReadBytes(bytes);
            if (bytes.Length == 0 || bytes[((bitmap.PixelHeight / 2) * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4 + 3] != 255)
                throw new InvalidOperationException("Empty or unpainted page raster.");
            return bytes;
        }
        static void Red(System.Windows.Media.DrawingContext context) =>
            context.DrawRectangle(System.Windows.Media.Brushes.Red, null, new System.Windows.Rect(0, 0, 7, 5));
        static void Blue(System.Windows.Media.DrawingContext context) =>
            context.DrawRectangle(System.Windows.Media.Brushes.Blue, null, new System.Windows.Rect(0, 0, 7, 5));
    }
    internal static void ReadingFrame(NovelReadingSurface surface)
    {
        var frame = surface.Children.OfType<Image>().Last();
        double scale = surface.XamlRoot?.RasterizationScale ?? 1;
        if (frame.Source is not WriteableBitmap pixels ||
            pixels.PixelWidth != (int)Math.Ceiling(surface.ActualWidth * scale) ||
            pixels.PixelHeight != (int)Math.Ceiling(surface.ActualHeight * scale) ||
            !surface.IsTabStop ||
            FrameworkElementAutomationPeer.CreatePeerForElement(surface)?.GetName() != "小说正文")
            throw new InvalidOperationException("Reader did not present an accessible in-tree XAML page raster.");
    }

    internal static DesktopWindow Window(FrameworkElement content, double width = 1000, double height = 760)
    {
        var root = content as Grid ?? new Grid();
        if (!ReferenceEquals(root, content)) root.Children.Add(content);
        NovelUi.Theme(root);
        var dialogs = root.Children.OfType<PlayerDialogHost>().FirstOrDefault();
        if (dialogs is null) { dialogs = new(); root.Children.Add(dialogs); }
        var window = new DesktopWindow { Content = root };
        window.AttachRoot(); dialogs.Attach(window); window.Resize(width, height);
        return window;
    }
    internal static void Invoke(FrameworkElement control)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control)
            ?? throw new InvalidOperationException("Missing control automation peer.");
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider select) select.Select();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else throw new InvalidOperationException("Missing control automation action.");
    }
    internal static void Check(CheckBox control, bool value)
    {
        if (control.IsChecked != value) Invoke(control);
    }
    internal static Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    internal static object Resource(FrameworkElement element, string key) => DesktopVisual.Resource(element, key);
    internal static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        NovelTests.Find<T>(root).Single(item => item.Name == name);
    internal static void CheckContrastResources(FrameworkElement view)
    {
        var resources = new List<ResourceDictionary>();
        for (DependencyObject? node = view; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element) Collect(element.Resources);
        Collect(Application.Current.Resources);
        foreach (var (key, expected) in new[]
        {
            ("MeowFilm.Brush.Surface", System.Windows.SystemColors.WindowColor),
            ("MeowFilm.Brush.Text", System.Windows.SystemColors.WindowTextColor),
            ("MeowFilm.Brush.Border", System.Windows.SystemColors.WindowTextColor),
            ("MeowFilm.Brush.AccentSoft", System.Windows.SystemColors.HighlightColor),
            ("MeowFilm.Brush.OnAccent", System.Windows.SystemColors.HighlightTextColor)
        })
        {
            var color = global::Windows.UI.Color.FromArgb(expected.A, expected.R, expected.G, expected.B);
            if (!resources.Any(dictionary => dictionary.TryGetValue(key, out var value) &&
                value is SolidColorBrush brush && brush.Color == color))
                throw new InvalidOperationException("Missing system high-contrast mapping: " + key);
        }
        void Collect(ResourceDictionary dictionary)
        {
            if (dictionary.ThemeDictionaries.TryGetValue("HighContrast", out var contrast) && contrast is ResourceDictionary high)
                resources.Add(high);
            foreach (var merged in dictionary.MergedDictionaries) Collect(merged);
        }
    }
    internal static async Task Preview(FrameworkElement view, string name, int width, int height)
    {
        string? root = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        if (root is null) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        System.IO.Directory.CreateDirectory(root);
        view.UpdateLayout(); await DesktopTestUi.DrainAsync();
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(view, width * dpi / 96, height * dpi / 96);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes);
            if (bytes.Length == 0) throw new InvalidOperationException("Empty novel preview.");
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, bytes);
            await encoder.FlushAsync();
            output.Seek(0);
            using var encoded = new DataReader(output.GetInputStreamAt(0));
            await encoded.LoadAsync((uint)output.Size);
            var png = new byte[checked((int)output.Size)]; encoded.ReadBytes(png);
            await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(root, $"{name}-{dpi}.png"), png);
        }
    }
}
