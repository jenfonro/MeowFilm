using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace MeowFilm.Windows.Tests;

// All callers run on TestUiHost's one application UI thread.
internal static class DesktopTestUi
{
    internal static Rect WorkArea
    {
        get
        {
            var display = Microsoft.UI.Windowing.DisplayArea.Primary;
            var area = display.WorkArea;
            var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
            Marshal.ThrowExceptionForHR(GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY));
            return new Rect(area.X * 96d / dpiX, area.Y * 96d / dpiY,
                area.Width * 96d / dpiX, area.Height * 96d / dpiY);
        }
    }

    internal static async Task DrainAsync()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        for (int i = 0; i < 3; i++)
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => drained.TrySetResult()))
                throw new InvalidOperationException("Test UI dispatcher stopped.");
            await drained.Task;
        }
    }
    internal static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static void Invoke(ButtonBase button)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? (button switch
        {
            CheckBox check => new CheckBoxAutomationPeer(check),
            RadioButton radio => new RadioButtonAutomationPeer(radio),
            ToggleButton toggle => new ToggleButtonAutomationPeer(toggle),
            Button action => new ButtonAutomationPeer(action),
            _ => throw new InvalidOperationException("Control has no native action automation peer")
        });
        if (!button.IsEnabled)
        {
            if (peer.IsEnabled()) throw new InvalidOperationException("Disabled action remains accessible as enabled.");
            return;
        }
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider select) select.Select();
        else throw new InvalidOperationException("Control exposes no native action pattern");
    }
    internal static DesktopWindow CreateWindow(UIElement content, double width = 1440, double height = 800)
    {
        var window = new DesktopWindow { Content = content };
        window.AttachRoot(); window.Resize(width, height); return window;
    }
    internal static FrameworkElement WindowRoot(Microsoft.UI.Xaml.Window window) =>
        (FrameworkElement)window.Content;
    internal static Size WindowSize(Microsoft.UI.Xaml.Window window)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96d;
        return new(window.AppWindow.Size.Width / scale, window.AppWindow.Size.Height / scale);
    }
    internal static Point WindowPosition(Microsoft.UI.Xaml.Window window)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96d;
        return new(window.AppWindow.Position.X / scale, window.AppWindow.Position.Y / scale);
    }
    internal static void ResizeWindow(Microsoft.UI.Xaml.Window window, double width, double height)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96d;
        window.AppWindow.Resize(new((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }
    internal static void MoveWindow(Microsoft.UI.Xaml.Window window, double x, double y)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96d;
        window.AppWindow.Move(new((int)Math.Round(x * scale), (int)Math.Round(y * scale)));
    }
    internal static Size WindowMinimumSize(Microsoft.UI.Xaml.Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var bounds = new MinMaxInfo();
        SendMessage(handle, 0x24, 0, ref bounds);
        double scale = GetDpiForWindow(handle) / 96d;
        return new(bounds.MinTrack.X / scale, bounds.MinTrack.Y / scale);
    }
    internal static bool WindowIsVisible(Microsoft.UI.Xaml.Window window) =>
        IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(window));
    internal static bool WindowIsEnabled(Microsoft.UI.Xaml.Window window) =>
        IsWindowEnabled(WinRT.Interop.WindowNative.GetWindowHandle(window));
    internal static bool WindowIsActive(Microsoft.UI.Xaml.Window window) =>
        GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window);
    internal static bool WindowIsTopmost(Microsoft.UI.Xaml.Window window) =>
        (GetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(window), -20).ToInt64() & 8) != 0;
    internal static nint WindowOwner(Microsoft.UI.Xaml.Window window) =>
        GetWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), 4);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    { public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint wParam, ref MinMaxInfo bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    internal static void Unmount(FrameworkElement element)
    {
        if (DesktopWindow.GetWindow(element) is { } window && ReferenceEquals(window.Content, element)) window.Content = null;
        else if (element.Parent is Panel panel) panel.Children.Remove(element);
        else if (element.Parent is Border border && ReferenceEquals(border.Child, element)) border.Child = null;
        else if (element.Parent is ContentControl content && ReferenceEquals(content.Content, element)) content.Content = null;
        else throw new InvalidOperationException("Test target has no directly owned mount to detach.");
    }
    internal static FrameworkElement Part(FrameworkElement owner, string name) =>
        Visuals(owner).OfType<FrameworkElement>().First(element => element.Name == name);
    internal static void Select(ListView list, object item)
    {
        list.ScrollIntoView(item); list.UpdateLayout();
        if (list.ContainerFromItem(item) is not ListViewItem)
            throw new InvalidOperationException("Selection container not realized");
        var owner = (ListViewAutomationPeer)(FrameworkElementAutomationPeer.CreatePeerForElement(list) ?? new ListViewAutomationPeer(list));
        var peer = new ListViewItemDataAutomationPeer(item, owner);
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
    }
    internal static IExpandCollapseProvider Expansion(ComboBox picker) =>
        (IExpandCollapseProvider)(FrameworkElementAutomationPeer.CreatePeerForElement(picker) ?? new ComboBoxAutomationPeer(picker))
            .GetPattern(PatternInterface.ExpandCollapse);
    internal static async Task SelectAsync(ComboBox picker, object item)
    {
        var expansion = Expansion(picker);
        expansion.Expand();
        try
        {
            await DrainAsync(); picker.UpdateLayout();
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(picker) ?? new ComboBoxAutomationPeer(picker);
            var row = peer.GetChildren().OfType<ComboBoxItemDataAutomationPeer>().Single(row => Equals(row.Item, item));
            var selection = row.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider ??
                throw new InvalidOperationException("The mounted category item has no selection automation pattern.");
            selection.Select();
            if (!selection.IsSelected || !Equals(picker.SelectedItem, item))
                throw new InvalidOperationException("The native category selection did not select its exact item.");
        }
        finally { expansion.Collapse(); }
    }
    internal static Rect Bounds(FrameworkElement element, UIElement root) =>
        new(element.TransformToVisual(root).TransformPoint(new Point()), new Size(element.ActualWidth, element.ActualHeight));
    internal static bool InViewport(FrameworkElement element)
    {
        if (!DesktopVisual.IsVisible(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement ancestor && (ancestor.Clip is not null || ancestor is ScrollContentPresenter or ScrollViewer))
            {
                var bounds = element.TransformToVisual(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                bounds.Intersect(ancestor.Clip?.Rect ?? new Rect(0, 0, ancestor.ActualWidth, ancestor.ActualHeight));
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
            }
        return true;
    }
    internal static DependencyObject? Hit(FrameworkElement root, Point point)
    {
        var host = root.XamlRoot?.Content as UIElement ?? throw new InvalidOperationException("Hit-test root is not mounted");
        return VisualTreeHelper.FindElementsInHostCoordinates(root.TransformToVisual(host).TransformPoint(point), root).FirstOrDefault();
    }
    internal static bool HasFocus(FrameworkElement element) => element.XamlRoot is { } root &&
        FocusManager.GetFocusedElement(root) is DependencyObject focused && DesktopVisual.Contains(element, focused);
    internal static async Task<byte[]> Pixels(UIElement element, int width = 0, int height = 0)
    {
        var bitmap = new RenderTargetBitmap();
        if (width > 0 && height > 0) await bitmap.RenderAsync(element, width, height);
        else await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes);
        if (bytes.Length == 0) throw new InvalidOperationException("Empty WinUI render");
        return bytes;
    }
    internal sealed record RenderFrame(int Width, int Height, double Scale, byte[] Pixels)
    {
        internal global::Windows.UI.Color Pixel(double x, double y)
        {
            int index = ((int)(y * Scale) * Width + (int)(x * Scale)) * 4;
            return global::Windows.UI.Color.FromArgb(Pixels[index + 3], Pixels[index + 2], Pixels[index + 1], Pixels[index]);
        }
    }
    internal static async Task<RenderFrame> Render(FrameworkElement element, double scale = 1)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element, (int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale));
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes);
        if (bytes.Length == 0) throw new InvalidOperationException("Empty native XAML raster");
        return new(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth / element.ActualWidth, bytes);
    }
    internal static RenderFrame Crop(RenderFrame frame, Rect bounds)
    {
        int left = (int)Math.Round(bounds.X * frame.Scale), top = (int)Math.Round(bounds.Y * frame.Scale);
        int width = (int)Math.Round(bounds.Width * frame.Scale), height = (int)Math.Round(bounds.Height * frame.Scale);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Array.Copy(frame.Pixels, ((top + y) * frame.Width + left) * 4, pixels, y * width * 4, width * 4);
        return new(width, height, frame.Scale, pixels);
    }
    internal static async Task Save(RenderFrame frame, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (File.Create(path)) { }
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var output = await file.OpenAsync(global::Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)frame.Width,
            (uint)frame.Height, 96 * frame.Scale, 96 * frame.Scale, frame.Pixels);
        await encoder.FlushAsync();
    }
    internal sealed class Host : IDisposable
    {
        internal DesktopWindow Window { get; }
        internal Host(FrameworkElement root, double width = 1440, double height = 800)
        {
            Window = CreateWindow(root, width, height);
            Window.Activate();
        }
        internal void Mount(FrameworkElement root) { Window.Content = root; Window.AttachRoot(); }
        public void Dispose() { Window.Content = null; Window.Close(); }
    }
}
