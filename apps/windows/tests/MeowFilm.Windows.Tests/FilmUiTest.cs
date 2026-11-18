using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using Windows.Foundation;

namespace MeowFilm.Windows.Tests;

internal static class FilmUiTest
{
    internal static DesktopWindow Window(FrameworkElement content, double width = 1150, double height = 850)
    {
        var window = new DesktopWindow { Content = content };
        window.AttachRoot(); window.Resize(width, height);
        return window;
    }
    internal static void Click(FrameworkElement button)
    {
        if (button is Control { IsEnabled: false }) throw new InvalidOperationException("Disabled test action");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new InvalidOperationException("Missing button peer");
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection) selection.Select();
        else throw new InvalidOperationException("No native button automation action");
    }
    internal static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    internal static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? Visuals(root).OfType<T>().FirstOrDefault(item => item.Name == name)
        ?? throw new InvalidOperationException("Missing Film element " + name);
    internal static bool Visible(DesktopWindow window) => window.IsVisible;
    internal static bool Visible(UIElement element) => DesktopVisual.IsVisible(element);
    internal static Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    internal static Task Pump()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => completion.SetResult());
        return completion.Task;
    }
    internal static async Task<byte[]> Pixels(FrameworkElement element)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(element, (int)element.ActualWidth, (int)element.ActualHeight);
        var buffer = await bitmap.GetPixelsAsync();
        var bytes = new byte[buffer.Length];
        using var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }
    // Invoke presentation operations, not synthetic keyboard/mouse input.
    internal static object? Call(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, args);
    internal static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    internal static IReadOnlyList<ContentDialog> Dialogs(FrameworkElement view) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).SelectMany(popup => Visuals(popup.Child))
            .OfType<ContentDialog>().ToArray();

    // Exercise the shipped HighContrast dictionaries without changing the user's OS setting.
    // This verifies resource consumption, not Windows' HighContrastChanged notification.
    internal sealed class HighContrastResources : IDisposable
    {
        private readonly List<(ResourceDictionary Owner, object Key, bool Existed, object? Value)> saved = [];
        private readonly FrameworkElement[] roots;
        internal HighContrastResources(params FrameworkElement[] roots)
        {
            this.roots = roots;
            var common = new ResourceDictionary { Source = new Uri("ms-appx:///MeowFilm.Desktop/Theme.xaml") };
            foreach (var root in roots)
            {
                var elements = Visuals(root).OfType<FrameworkElement>().Distinct().ToArray();
                foreach (var element in elements)
                {
                    var values = new Dictionary<object, object>();
                    if (ReferenceEquals(element, root)) Collect(common, values);
                    Collect(element.Resources, values);
                    foreach (var pair in values)
                    {
                        bool existed = element.Resources.Keys.Contains(pair.Key);
                        object? old = existed ? element.Resources[pair.Key] : null;
                        saved.Add((element.Resources, pair.Key, existed, old));
                        element.Resources[pair.Key] = pair.Value;
                    }
                }
                RefreshTheme(root);
            }
        }
        private static void Collect(ResourceDictionary dictionary, Dictionary<object, object> values)
        {
            foreach (var merged in dictionary.MergedDictionaries) Collect(merged, values);
            if (dictionary.ThemeDictionaries.TryGetValue("HighContrast", out var value) && value is ResourceDictionary highContrast)
                foreach (var pair in highContrast) values[pair.Key] = pair.Value;
        }
        private static void RefreshTheme(FrameworkElement root)
        {
            var original = root.RequestedTheme;
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = original;
        }
        public void Dispose()
        {
            foreach (var item in saved.AsEnumerable().Reverse())
                if (item.Existed) item.Owner[item.Key] = item.Value!;
                else item.Owner.Remove(item.Key);
            foreach (var root in roots) RefreshTheme(root);
        }
    }
    internal static global::Windows.UI.Color ResourceColor(FrameworkElement root, string key) =>
        ((SolidColorBrush)DesktopVisual.Resource(root, key)).Color;
}
