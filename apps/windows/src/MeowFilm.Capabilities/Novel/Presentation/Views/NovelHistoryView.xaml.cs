using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class NovelHistoryItem : INotifyPropertyChanged
{
    private NovelEntry entry;
    private string sourceName;

    public NovelSourceService Sources { get; }
    public NovelEntry Entry => entry;
    public NovelBook Book => entry.Book;
    public string BookId => entry.BookId;
    public string Title => entry.Book.Name;
    public string Subtitle => string.Join(" · ", new[] { entry.Book.Author, sourceName, FormatReadAt(entry.ReadAt) }.Where(s => s.Length > 0));
    public string Progress => entry.Progress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelHistoryItem(NovelSourceService sources, NovelEntry entry, string sourceName)
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

    private static string FormatReadAt(DateTimeOffset? value)
    {
        if (value is not { } readAt) return "";
        var local = readAt.LocalDateTime;
        int days = (DateTimeOffset.Now.Date - local.Date).Days;
        return days == 0 ? "今天 " + local.ToString("HH:mm")
            : days == 1 ? "昨天 " + local.ToString("HH:mm")
            : local.ToString("yyyy-MM-dd HH:mm");
    }
}

public sealed partial class NovelHistoryView : UserControl
{
    private readonly NovelCapability module;
    private readonly ObservableCollection<NovelHistoryItem> items = [];
    private readonly Dictionary<string, NovelHistoryItem> lookup = new(StringComparer.Ordinal);
    private IReadOnlyList<NovelEntry>? displayedEntries;
    private IReadOnlyList<NovelSource>? displayedSources;

    internal NovelHistoryView(NovelCapability module)
    {
        this.module = module;
        InitializeComponent();
        HistoryList.ItemsSource = items;
    }

    internal void Present()
    {
        if (ReferenceEquals(displayedEntries, module.Library.Entries) &&
            ReferenceEquals(displayedSources, module.Sources.Sources))
            return;

        displayedEntries = module.Library.Entries;
        displayedSources = module.Sources.Sources;

        var desired = module.Library.OrderedHistoryEntries().ToArray();
        var sourceNames = NovelPresentation.IndexSourceNames(displayedSources);
        NovelPresentation.SyncKeyed(
            items,
            lookup,
            desired,
            entry => entry.BookId,
            item => item.BookId,
            entry => new NovelHistoryItem(module.Sources, entry, sourceNames.GetValueOrDefault(entry.Book.InstallationId, "")),
            (item, entry) => item.Update(entry, sourceNames.GetValueOrDefault(entry.Book.InstallationId, "")));

        CountText.Text = desired.Length > 0 ? $"共 {desired.Length} 条" : "";
        bool empty = desired.Length == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearAllButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelHistoryItem item })
            module.RunUi(ct => module.OpenEntryAsync(item.Entry, ct));
    }

    private void ContinueReading_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelHistoryItem item })
            module.RunUi(ct => module.ContinueReadingAsync(item.Entry, ct));
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelHistoryItem item })
            module.RunUi(ct => module.Library.DeleteHistoryAsync(item.BookId, ct));
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e) => module.RunUi(ClearAllAsync);

    private async Task ClearAllAsync(CancellationToken token)
    {
        var request = new DesktopDialogRequest
        {
            Title = "清空阅读历史",
            Message = "确认清空全部小说阅读历史？书架书籍将保留。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        if (await DesktopModalHost.ShowDialogAsync(this, request, token) != DesktopDialogResult.Primary) return;
        await module.Library.ClearHistoryAsync(token);
        module.Notify("阅读历史已清空");
    }
}
