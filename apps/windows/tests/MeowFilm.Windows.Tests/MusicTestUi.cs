using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Windows.UI;

namespace MeowFilm.Windows.Tests;

// Native UI test mechanisms only; domain operations remain on each fixture's real owners.
internal static class MusicTestUi
{
    internal static DesktopWindow Window(UIElement content, int width, int height)
    {
        var window = new DesktopWindow { Content = content };
        window.AttachRoot(); window.Resize(width, height); return window;
    }
    internal static void OpenAccountLogin(MusicAccountsView view) =>
        typeof(MusicAccountsView).GetMethod("ShowLogin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, null);
    internal static bool IsDialogOpen(ContentDialog dialog) => dialog.XamlRoot is { } root &&
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .Any(popup => popup.Child is DependencyObject child && DesktopTestUi.Visuals(child)
                .Any(element => ReferenceEquals(element, dialog) || ReferenceEquals(element, dialog.Content)));
    internal static byte[] Pixels(WriteableBitmap bitmap)
    {
        using var stream = bitmap.PixelBuffer.AsStream();
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        stream.ReadExactly(pixels); return pixels;
    }
    internal static void Invoke(MenuFlyoutItemBase item)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item) ?? (item switch
        {
            ToggleMenuFlyoutItem toggle => new ToggleMenuFlyoutItemAutomationPeer(toggle),
            MenuFlyoutItem action => new MenuFlyoutItemAutomationPeer(action),
            _ => throw new InvalidOperationException("Menu item has no action automation peer")
        });
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else throw new InvalidOperationException("Menu item exposes no native action pattern");
    }
    internal static MenuFlyoutPresenter MenuPresenter(MenuFlyout menu) =>
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(menu.XamlRoot)
            .SelectMany(popup => DesktopTestUi.Visuals(popup.Child)).OfType<MenuFlyoutPresenter>()
            .Single(presenter => DesktopTestUi.Visuals(presenter).Contains(menu.Items[0]));
    internal static async Task Drain()
    {
        for (int i = 0; i < 3; i++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => completion.TrySetResult()))
                throw new InvalidOperationException("TEST_UI_DISPATCHER_CLOSED");
            await completion.Task;
        }
    }
    internal static WriteableBitmap Bitmap(int width, int height, Func<int, int, Color> pixel)
    {
        var image = Pixels(width, height, pixel);
        return (WriteableBitmap)image.GetSource(DispatcherQueue.GetForCurrentThread());
    }
    internal static ArtworkPixels Pixels(int width, int height, Func<int, int, Color> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var color = pixel(x, y); int offset = (y * width + x) * 4;
            bytes[offset] = (byte)((color.B * color.A + 127) / 255);
            bytes[offset + 1] = (byte)((color.G * color.A + 127) / 255);
            bytes[offset + 2] = (byte)((color.R * color.A + 127) / 255); bytes[offset + 3] = color.A;
        }
        return new(width, height, bytes);
    }
    internal static DataTemplate Template(string content) => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='using:MeowFilm.Capabilities'>" + content + "</DataTemplate>");
}
