using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelBookView : UserControl
{
    private readonly NovelCapability module;
    private readonly Func<CancellationToken, Task> switchSourceAsync;
    private readonly Func<CancellationToken, Task> catalogAsync;
    private readonly ObservableCollection<NovelChapter> chapters = [];
    private NovelBookSession? session;
    private NovelBook? displayedBook;
    private NovelEntry? displayedEntry;
    private IReadOnlyList<NovelSource>? displayedSources;
    private IReadOnlyList<NovelEntry>? displayedEntries;
    private IReadOnlyList<NovelChapter>? displayedChapters;

    internal NovelBookView(
        NovelCapability module,
        Func<CancellationToken, Task> switchSourceAsync,
        Func<CancellationToken, Task> catalogAsync)
    {
        this.module = module;
        this.switchSourceAsync = switchSourceAsync;
        this.catalogAsync = catalogAsync;
        InitializeComponent();
        BookCover.Sources = module.Sources;
        ChaptersList.ItemsSource = chapters;
    }

    internal ScrollViewer ScrollHost => Scroller;

    internal void Present(NovelBookSession? current)
    {
        if (!ReferenceEquals(session, current))
        {
            session = current;
            displayedBook = null;
            displayedEntry = null;
            displayedSources = null;
            displayedEntries = null;
            displayedChapters = null;
            chapters.Clear();
            BookCover.Book = null;
        }
        if (session is null) return;

        bool bookChanged = !Equals(displayedBook, session.Book) || !Equals(displayedEntry, session.Entry);
        bool sourcesChanged = !ReferenceEquals(displayedSources, module.Sources.Sources);
        if (bookChanged)
        {
            var book = session.Book;
            BookCover.Book = book;
            NameText.Text = book.Name;
            AuthorText.Text = book.Author;
            LatestText.Text = book.LatestChapterTitle;
            IntroText.Text = book.Intro;
            displayedBook = book;
            displayedEntry = session.Entry;
        }
        if (bookChanged || sourcesChanged)
        {
            displayedSources = module.Sources.Sources;
            SourceText.Text = displayedSources.FirstOrDefault(s => s.InstallationId == session.Book.InstallationId)?.Name ?? "书源不可用";
        }

        ReadButton.IsEnabled = !session.Loading;
        CancelButton.IsEnabled = session.Loading;
        LoadingRing.IsActive = session.Loading;
        LoadingRing.Visibility = session.Loading ? Visibility.Visible : Visibility.Collapsed;

        if (!ReferenceEquals(displayedEntries, module.Library.Entries))
        {
            displayedEntries = module.Library.Entries;
            string label = displayedEntries.Any(e => e.BookId == session.Entry.BookId && e.OnShelf) ? "移出书架" : "加入书架";
            if (!Equals(ShelfButton.Content, label))
            {
                ShelfButton.Content = label;
                AutomationProperties.SetName(ShelfButton, label);
            }
        }

        if (!ReferenceEquals(displayedChapters, session.Chapters))
        {
            displayedChapters = session.Chapters;
            SyncChapters(session.Chapters);
        }
    }

    private void SyncChapters(IReadOnlyList<NovelChapter> desired)
    {
        while (chapters.Count > desired.Count)
            chapters.RemoveAt(chapters.Count - 1);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < chapters.Count)
            {
                if (!Equals(chapters[i], desired[i])) chapters[i] = desired[i];
            }
            else
            {
                chapters.Add(desired[i]);
            }
        }
    }

    private void Chapter_Click(object sender, RoutedEventArgs e)
    {
        if (session is not { } current || sender is not FrameworkElement { DataContext: NovelChapter chapter }) return;
        int chapterIndex = chapter.Index;
        module.RunUi(async ct =>
        {
            await current.ReadAsync(chapterIndex, token: ct);
            module.Request(NovelCapability.ReaderRoute);
        });
    }

    private void Read_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.StartReadingAsync(ct));
    private void Shelf_Click(object sender, RoutedEventArgs e) { if (session is { } current) module.RunUi(ct => current.ShelfAsync(ct)); }
    private void SwitchSource_Click(object sender, RoutedEventArgs e) => module.RunUi(switchSourceAsync);
    private void Catalog_Click(object sender, RoutedEventArgs e) => module.RunUi(catalogAsync);
    private void RefreshCatalog_Click(object sender, RoutedEventArgs e) { if (session is { } current) module.RunUi(ct => current.LoadMetadataAsync(ct)); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => session?.Cancel();
}
