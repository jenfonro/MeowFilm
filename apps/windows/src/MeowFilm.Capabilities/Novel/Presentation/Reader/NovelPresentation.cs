using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using MeowFilm.Desktop;
using Windows.ApplicationModel.DataTransfer;

namespace MeowFilm.Capabilities;

internal static class NovelPresentation
{
    internal static void SyncKeyed<TSource, TItem>(
        ObservableCollection<TItem> items,
        Dictionary<string, TItem> lookup,
        IReadOnlyList<TSource> desired,
        Func<TSource, string> sourceKey,
        Func<TItem, string> itemKey,
        Func<TSource, TItem> create,
        Action<TItem, TSource> update)
    {
        var keys = new HashSet<string>(desired.Count, StringComparer.Ordinal);
        for (int i = 0; i < desired.Count; i++) keys.Add(sourceKey(desired[i]));
        for (int i = items.Count - 1; i >= 0; i--)
        {
            string key = itemKey(items[i]);
            if (!keys.Contains(key))
            {
                lookup.Remove(key);
                items.RemoveAt(i);
            }
        }
        for (int index = 0; index < desired.Count; index++)
        {
            var src = desired[index];
            string key = sourceKey(src);
            if (lookup.TryGetValue(key, out var existing))
            {
                update(existing, src);
                int currentIdx = items.IndexOf(existing);
                if (currentIdx != index && currentIdx >= 0) items.Move(currentIdx, index);
            }
            else
            {
                var item = create(src);
                lookup.Add(key, item);
                items.Insert(index, item);
            }
        }
    }

    internal static Dictionary<string, string> IndexSourceNames(IReadOnlyList<NovelSource> sources)
    {
        var map = new Dictionary<string, string>(sources.Count, StringComparer.Ordinal);
        foreach (var source in sources) map[source.InstallationId] = source.Name;
        return map;
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
    internal static Task<string?> OpenFileAsync(FrameworkElement owner, params string[] extensions) =>
        CapabilityFilePicker.OpenAsync(owner, "打开", extensions);
    internal static Task<string?> SaveJsonAsync(FrameworkElement owner, string name) =>
        CapabilityFilePicker.SaveAsync(owner, "保存", name, ".json");

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
