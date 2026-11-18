using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Desktop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace MeowFilm.Capabilities;

internal static class NovelPresentation
{
    internal static Geometry Geometry(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    internal static WriteableBitmap Bitmap(System.Windows.Media.Imaging.BitmapSource source)
    {
        if (source.Format != System.Windows.Media.PixelFormats.Pbgra32)
            source = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        var result = new WriteableBitmap(source.PixelWidth, source.PixelHeight);
        using (var stream = result.PixelBuffer.AsStream()) stream.Write(pixels);
        result.Invalidate();
        return result;
    }

    internal static async Task<string> ClipboardTextAsync()
    {
        var content = Clipboard.GetContent();
        return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : "";
    }
    internal static void CopyText(string text)
    {
        var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content);
    }
    internal static async Task<string?> OpenFileAsync(FrameworkElement owner, params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            DesktopWindow.GetWindow(owner)?.Handle ?? throw new InvalidOperationException("The file picker requires a window."));
        foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }
    internal static async Task<string?> SaveJsonAsync(FrameworkElement owner, string name)
    {
        var picker = new FileSavePicker { SuggestedFileName = name, DefaultFileExtension = ".json" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            DesktopWindow.GetWindow(owner)?.Handle ?? throw new InvalidOperationException("The file picker requires a window."));
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // Visibility is inherited through the visual tree, not through a WinUI property.
    // Observe only the current ancestors, and release their subscriptions on unload.
    internal sealed class VisibilityWatch : IDisposable
    {
        private readonly FrameworkElement element;
        private readonly Action<bool> changed;
        private readonly List<(UIElement Element, long Token)> ancestors = [];
        private XamlRoot? root;
        private bool? visible;
        internal VisibilityWatch(FrameworkElement element, Action<bool> changed)
        {
            this.element = element; this.changed = changed;
            element.Loaded += Loaded; element.Unloaded += Unloaded;
            if (element.IsLoaded) Attach();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) { Detach(); Publish(false); }
        private void Attach()
        {
            Detach();
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement ancestor)
                    ancestors.Add((ancestor, ancestor.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Refresh())));
            root = element.XamlRoot;
            if (root is not null) root.Changed += RootChanged;
            Refresh();
        }
        private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();
        private void Refresh() => Publish(element.IsLoaded && DesktopVisual.IsVisible(element));
        private void Publish(bool next)
        {
            if (next == visible) return;
            visible = next; changed(next);
        }
        private void Detach()
        {
            foreach (var (ancestor, token) in ancestors) ancestor.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
            ancestors.Clear();
            if (root is not null) root.Changed -= RootChanged;
            root = null;
        }
        public void Dispose() { Detach(); element.Loaded -= Loaded; element.Unloaded -= Unloaded; }
    }
}
