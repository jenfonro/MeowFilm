using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeowFilm.Windows.Tests;

internal static class NovelTestUi
{
    internal static async Task LoadedAsync(FrameworkElement element)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (!element.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
            element.Loaded += OnLoaded;
            try { await loaded.Task.WaitAsync(deadline.Token); }
            finally { element.Loaded -= OnLoaded; }
        }
        // WinUI can dispatch Loaded before IsLoaded is published for the subtree.
        // Complete only after that native layout phase, not from inside its event.
        do
        {
            await DesktopTestUi.DrainAsync().WaitAsync(deadline.Token);
            element.UpdateLayout();
        }
        while (!element.IsLoaded);
    }

    internal static async Task ResourceOwnership()
    {
        const string key = "Novel.Test.ScopedText";
        var text = new TextBlock { Text = "局部资源" };
        text.UseResource(FrameworkElement.StyleProperty, key);
        try
        {
            _ = DesktopVisual.Resource(text, key);
            throw new InvalidOperationException("Unattached explicit resource lookup hid a missing key.");
        }
        catch (KeyNotFoundException) { }
        var style = new Style { TargetType = typeof(TextBlock) };
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 237d));
        var scope = new Grid(); scope.Resources[key] = style; scope.Children.Add(text);
        bool loadingObserved = false, loadingStyleReference = false;
        double loadingMinWidth = double.NaN;
        text.Loading += (_, _) =>
        {
            loadingObserved = true; loadingStyleReference = ReferenceEquals(text.Style, style);
            loadingMinWidth = text.MinWidth;
        };
        var window = Window(scope);
        try
        {
            window.Show(); await LoadedAsync(text); scope.UpdateLayout();
            if (!loadingObserved) throw new InvalidOperationException("Owner resource Loading observation did not run.");
            if (!loadingStyleReference) throw new InvalidOperationException("Owner style identity was not available during Loading.");
            if (loadingMinWidth != 237) throw new InvalidOperationException($"Owner style setter was not effective during Loading (minWidth={loadingMinWidth}).");
            if (!ReferenceEquals(text.Style, style)) throw new InvalidOperationException($"Loaded child lost the owner style identity (nativeEqual={Equals(text.Style, style)}).");
            if (text.MinWidth != 237) throw new InvalidOperationException($"Loaded owner style setter was not effective (minWidth={text.MinWidth}).");
            var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(text);
            if (text.DesiredSize.Width < 237 || slot.Width < 237)
                throw new InvalidOperationException($"First layout ignored the owner style minimum (desiredWidth={text.DesiredSize.Width}, slotWidth={slot.Width}).");
            try
            {
                text.UseResource(FrameworkElement.StyleProperty, "Novel.Test.Missing");
                throw new InvalidOperationException("A loaded control hid a missing resource.");
            }
            catch (KeyNotFoundException) { }
        }
        finally { window.Close(); }
    }

    internal static async Task VisibilityOwnership()
    {
        var owner = new Border { Width = 120, Height = 80 };
        var scope = new Grid(); scope.Children.Add(owner);
        var window = Window(scope);
        try
        {
            window.Show(); await LoadedAsync(owner);
            var states = new List<bool>();
            using var observation = DesktopVisual.ObserveVisibility(owner, states.Add);
            if (!owner.IsLoaded || !states.SequenceEqual(new[] { true }))
                throw new InvalidOperationException("Observing an already loaded owner fabricated an unload.");
            scope.Visibility = Visibility.Collapsed;
            if (!states.SequenceEqual(new[] { true, false }))
                throw new InvalidOperationException("A collapsed ancestor did not synchronously hide the observed owner.");
            scope.Visibility = Visibility.Visible;
            if (!states.SequenceEqual(new[] { true, false, true }))
                throw new InvalidOperationException("Restoring an ancestor did not restore the observed owner.");
            if (!owner.IsLoaded) throw new InvalidOperationException("Visibility observation invalidated the native loaded state.");
            scope.Visibility = Visibility.Collapsed;
            var novelStates = new List<bool>();
            using (var novel = new NovelPresentation.VisibilityWatch(owner, novelStates.Add))
            {
                if (!owner.IsLoaded || !novelStates.SequenceEqual(new[] { false }))
                    throw new InvalidOperationException("Novel observation hid the initial cancellation of a loaded but collapsed owner.");
                scope.Visibility = Visibility.Visible;
                if (!novelStates.SequenceEqual(new[] { false, true }))
                    throw new InvalidOperationException("Novel observation did not restore its loaded owner after ancestor visibility resumed.");
            }
            var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUnloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
            owner.Unloaded += OnUnloaded;
            scope.Children.Remove(owner);
            try { await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { owner.Unloaded -= OnUnloaded; }
            if (states[^1] || states.Count(state => !state) != 3)
                throw new InvalidOperationException("Actual owner removal did not publish its unload.");
            scope.Children.Add(owner); await LoadedAsync(owner);
            if (!states[^1]) throw new InvalidOperationException("Reattached owner did not resume observation.");
            observation.Dispose(); int count = states.Count;
            scope.Visibility = Visibility.Collapsed; scope.Children.Remove(owner);
            await DesktopTestUi.DrainAsync();
            if (states.Count != count) throw new InvalidOperationException("Disposed visibility observation retained its owner subscriptions.");
        }
        finally { window.Close(); }
    }

    internal static async Task RasterOwnership()
    {
        var raster = new NovelPageRaster();
        var currentView = new Image { Width = 140, Height = 100, Stretch = Stretch.Fill };
        var snapshotView = new Image { Width = 140, Height = 100, Stretch = Stretch.Fill };
        var panel = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(currentView); panel.Children.Add(snapshotView);
        foreach (var color in new[] { Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Blue })
            panel.Children.Add(new Border { Width = 20, Height = 100, Background = new SolidColorBrush(color) });
        var window = Window(panel, 400, 240);
        try
        {
            window.Show(); await DesktopTestUi.DrainAsync();
            var first = raster.Render(new Size(7, 5), 1, Red); currentView.Source = first;
            var snapshot = raster.Snapshot() ?? throw new InvalidOperationException("Missing page snapshot.");
            snapshotView.Source = snapshot;
            await CheckPixels(Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Red);
            var second = raster.Render(new Size(7, 5), 1, Blue);
            if (!ReferenceEquals(first, second) || ReferenceEquals(first, snapshot))
                throw new InvalidOperationException("Page raster lost current-source reuse or independent animation ownership.");
            await CheckPixels(Microsoft.UI.Colors.Blue, Microsoft.UI.Colors.Red);
            foreach (double scale in new[] { 1.5, 2.0 })
            {
                var scaled = raster.Render(new Size(7, 5), scale, Red); currentView.Source = scaled;
                if (scaled.SizeInPixels.Width != (uint)Math.Ceiling(7 * scale) ||
                    scaled.SizeInPixels.Height != (uint)Math.Ceiling(5 * scale) || ReferenceEquals(scaled, second) ||
                    Math.Abs(scaled.Dpi - 96 * scale) > .01)
                    throw new InvalidOperationException("Page raster ignored XAML scale.");
                await CheckPixels(Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Red);
                currentView.Source = null; raster.Clear();
                if (raster.Snapshot() is not null) throw new InvalidOperationException("Retired page retained its raster.");
                var resumed = raster.Render(new Size(7, 5), scale, Blue); currentView.Source = resumed;
                if (ReferenceEquals(resumed, scaled))
                    throw new InvalidOperationException("Page retirement reused a retired source.");
                await CheckPixels(Microsoft.UI.Colors.Blue, Microsoft.UI.Colors.Red);
            }
        }
        finally { currentView.Source = snapshotView.Source = null; raster.Clear(); window.Close(); }
        async Task CheckPixels(global::Windows.UI.Color current, global::Windows.UI.Color saved)
        {
            await DesktopTestUi.DrainAsync();
            var pixels = await NativeDesktopCapture.CaptureAsync(panel);
            // Compare independent page surfaces with native colors in this same
            // desktop frame, after the compositor has transformed both paths.
            var red = Pixel(pixels, 290, 50); var blue = Pixel(pixels, 310, 50);
            var expectedCurrent = current == Microsoft.UI.Colors.Red ? red : blue;
            var expectedSaved = saved == Microsoft.UI.Colors.Red ? red : blue;
            if (red == blue || red.R <= red.B || blue.B <= blue.R ||
                Pixel(pixels, 70, 50) != expectedCurrent || Pixel(pixels, 210, 50) != expectedSaved)
            {
                var path = TestWorkspace.DataPath("desktop-shots", "novel-raster-first-failure.png");
                await DesktopTestUi.Save(new(pixels.Width, pixels.Height, pixels.Scale, pixels.Pixels), path);
                throw new InvalidOperationException($"Current or independent snapshot pixels were blank, stale, or mutated: current={Pixel(pixels, 70, 50)}, expected={expectedCurrent}; snapshot={Pixel(pixels, 210, 50)}, expected={expectedSaved}; nativeRedReference={red}; nativeBlueReference={blue}; active={window.IsActive}; shot={path}");
            }
        }
        static void Red(CanvasDrawingSession context) => context.FillRectangle(0, 0, 7, 5, Microsoft.UI.Colors.Red);
        static void Blue(CanvasDrawingSession context) => context.FillRectangle(0, 0, 7, 5, Microsoft.UI.Colors.Blue);
    }

    internal static global::Windows.UI.Color Pixel(NativeDesktopCapture.Capture frame, double x, double y)
    {
        int at = ((int)(y * frame.Scale) * frame.Width + (int)(x * frame.Scale)) * 4;
        return global::Windows.UI.Color.FromArgb(frame.Pixels[at + 3], frame.Pixels[at + 2], frame.Pixels[at + 1], frame.Pixels[at]);
    }

    internal static void ReadingFrame(NovelReadingSurface surface)
    {
        var frame = surface.Children.OfType<Image>().Last();
        double scale = surface.XamlRoot?.RasterizationScale ?? 1;
        if (frame.Source is not CanvasImageSource pixels ||
            pixels.SizeInPixels.Width != (uint)Math.Ceiling(surface.ActualWidth * scale) ||
            pixels.SizeInPixels.Height != (uint)Math.Ceiling(surface.ActualHeight * scale) ||
            Math.Abs(pixels.Dpi - 96 * scale) > .01 ||
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
            ("MeowFilm.Brush.Surface", new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window)),
            ("MeowFilm.Brush.Text", new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)),
            ("MeowFilm.Brush.Border", new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)),
            ("MeowFilm.Brush.AccentSoft", new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Highlight)),
            ("MeowFilm.Brush.OnAccent", new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText))
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
