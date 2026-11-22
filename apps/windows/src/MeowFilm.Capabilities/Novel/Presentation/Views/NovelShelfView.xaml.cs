using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class NovelShelfItem : INotifyPropertyChanged
{
    private NovelEntry entry;
    private string sourceName;

    public NovelSourceService Sources { get; }
    public NovelEntry Entry => entry;
    public NovelBook Book => entry.Book;
    public string BookId => entry.BookId;
    public string Title => entry.Book.Name;
    public string Subtitle => string.Join(" · ", new[] { entry.Book.Author, sourceName }.Where(s => s.Length > 0));
    public string Progress => entry.Anchor is not null ? entry.Progress : string.IsNullOrEmpty(entry.Book.LatestChapterTitle) ? "未开始" : entry.Book.LatestChapterTitle;
    public bool IsPinned => entry.PinnedAt is not null;
    public Visibility PinnedVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;
    public string PinLabel => IsPinned ? "取消置顶" : "置顶";

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelShelfItem(NovelSourceService sources, NovelEntry entry, string sourceName)
    {
        Sources = sources;
        this.entry = entry;
        this.sourceName = sourceName;
    }

    internal void Update(NovelEntry nextEntry, string nextSourceName)
    {
        if (entry == nextEntry && sourceName == nextSourceName) return;
        entry = nextEntry;
        sourceName = nextSourceName;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed partial class NovelShelfView : UserControl
{
    private readonly NovelCapability module;
    private readonly ObservableCollection<NovelShelfItem> items = [];
    private readonly Dictionary<string, NovelShelfItem> lookup = new(StringComparer.Ordinal);
    private IReadOnlyList<NovelEntry>? displayedEntries;
    private IReadOnlyList<NovelSource>? displayedSources;

    internal NovelShelfView(NovelCapability module)
    {
        this.module = module;
        InitializeComponent();
        ShelfList.ItemsSource = items;
    }

    internal void Present()
    {
        if (ReferenceEquals(displayedEntries, module.Library.Entries) &&
            ReferenceEquals(displayedSources, module.Sources.Sources))
            return;

        displayedEntries = module.Library.Entries;
        displayedSources = module.Sources.Sources;

        var desired = module.Library.Entries
            .Where(e => e.OnShelf)
            .OrderByDescending(e => e.PinnedAt is not null)
            .ThenByDescending(e => e.PinnedAt)
            .ThenByDescending(e => e.ReadAt ?? e.AddedAt)
            .ThenBy(e => e.Book.Name, StringComparer.CurrentCulture)
            .ToArray();

        var ids = desired.Select(e => e.BookId).ToHashSet(StringComparer.Ordinal);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(items[i].BookId))
            {
                lookup.Remove(items[i].BookId);
                items.RemoveAt(i);
            }
        }

        for (int index = 0; index < desired.Length; index++)
        {
            var entry = desired[index];
            string sourceName = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == entry.Book.InstallationId)?.Name ?? "";
            if (lookup.TryGetValue(entry.BookId, out var existing))
            {
                existing.Update(entry, sourceName);
                int currentIdx = items.IndexOf(existing);
                if (currentIdx != index && currentIdx >= 0) items.Move(currentIdx, index);
            }
            else
            {
                var item = new NovelShelfItem(module.Sources, entry, sourceName);
                lookup.Add(entry.BookId, item);
                items.Insert(index, item);
            }
        }

        CountText.Text = desired.Length > 0 ? $"共 {desired.Length} 本" : "";
        bool empty = desired.Length == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelShelfItem item })
            module.RunUi(ct => module.OpenEntryAsync(item.Entry, ct));
    }

    private void ContinueReading_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelShelfItem item })
            module.RunUi(ct => module.ContinueReadingAsync(item.Entry, ct));
    }

    private void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelShelfItem item })
            module.RunUi(ct => module.Library.PinAsync(item.BookId, ct));
    }

    private void RemoveFromShelf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelShelfItem item })
            module.RunUi(async ct =>
            {
                await module.Library.SetShelfAsync(item.BookId, false, ct);
                module.Notify("已移出书架");
            });
    }

    private void GoDiscover_Click(object sender, RoutedEventArgs e) => module.Request(NovelCapability.HomeRoute);
}
