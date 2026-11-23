using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace MeowFilm.Capabilities;

internal static class NovelPresentation
{
    internal static Geometry Geometry(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);

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

    internal sealed class VisibilityWatch : IDisposable
    {
        private readonly IDisposable observation;
        internal VisibilityWatch(FrameworkElement element, Action<bool> changed)
        {
            bool suppressInitial = !element.IsLoaded;
            observation = DesktopVisual.ObserveVisibility(element, visible =>
            {
                // Novel views start their work on first load; constructing an
                // unmounted view must not invoke its unload cleanup prematurely.
                bool publish = !suppressInitial || visible;
                suppressInitial = false;
                if (publish) changed(visible);
            });
        }
        public void Dispose() => observation.Dispose();
    }
}
