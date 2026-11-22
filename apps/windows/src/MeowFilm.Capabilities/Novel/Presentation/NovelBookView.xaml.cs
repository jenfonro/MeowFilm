using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelBookView : UserControl
{
    private readonly NovelCapability module;
    private readonly Func<CancellationToken, Task> switchSourceAsync;
    private readonly Func<CancellationToken, Task> catalogAsync;
    private readonly Dictionary<(string Url, int Index), Button> chapterButtons = [];
    private NovelBookSession? session;
    private NovelBook? displayedBook;
    private NovelEntry? displayedEntry;
    private (string Source, long Revision, string Url, string Cover)? coverKey;
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
    }

    internal ScrollViewer ScrollHost => Scroller;

    internal void Present(NovelBookSession? current)
    {
        if (!ReferenceEquals(session, current))
        {
            session = current;
            displayedBook = null;
            displayedEntry = null;
            coverKey = null;
            displayedEntries = null;
            displayedChapters = null;
            chapterButtons.Clear();
            ChaptersPanel.Children.Clear();
            CoverHost.Content = null;
        }
        if (session is null) return;

        if (!Equals(displayedBook, session.Book) || !Equals(displayedEntry, session.Entry))
        {
            var book = session.Book;
            var nextCover = NovelView.CoverIdentity(book);
            if (coverKey != nextCover)
            {
                coverKey = nextCover;
                CoverHost.Content = new NovelImage(module.Sources, book) { Width = 132, Height = 190 };
            }
            NameText.Text = book.Name;
            AuthorText.Text = book.Author;
            SourceText.Text = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == book.InstallationId)?.Name ?? "书源不可用";
            LatestText.Text = book.LatestChapterTitle;
            IntroText.Text = book.Intro;
            displayedBook = book;
            displayedEntry = session.Entry;
        }

        ReadButton.IsEnabled = !session.Loading;
        CancelButton.IsEnabled = session.Loading;
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
            var desired = session.Chapters.Take(8).ToArray();
            var identities = desired.Select(c => (c.ChapterUrl, c.Index)).ToHashSet();
            foreach (var key in chapterButtons.Keys.Where(key => !identities.Contains(key)).ToArray())
            {
                ChaptersPanel.Children.Remove(chapterButtons[key]);
                chapterButtons.Remove(key);
            }
            var capturedSession = session;
            for (int index = 0; index < desired.Length; index++)
            {
                var chapter = desired[index];
                var key = (chapter.ChapterUrl, chapter.Index);
                if (!chapterButtons.TryGetValue(key, out var button))
                {
                    int chapterIndex = chapter.Index;
                    button = NovelUi.Button(chapter.Name, () => module.RunUi(async ct =>
                    {
                        await capturedSession.ReadAsync(chapterIndex, token: ct);
                        module.Request(NovelCapability.ReaderRoute);
                    }));
                    chapterButtons.Add(key, button);
                }
                button.Content = chapter.Name;
                AutomationProperties.SetName(button, chapter.Name);
                NovelView.PlaceChild(ChaptersPanel, button, index);
            }
        }
    }

    private void Read_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.StartReadingAsync(ct));
    private void Shelf_Click(object sender, RoutedEventArgs e) { if (session is { } current) module.RunUi(ct => current.ShelfAsync(ct)); }
    private void SwitchSource_Click(object sender, RoutedEventArgs e) => module.RunUi(switchSourceAsync);
    private void Catalog_Click(object sender, RoutedEventArgs e) => module.RunUi(catalogAsync);
    private void RefreshCatalog_Click(object sender, RoutedEventArgs e) { if (session is { } current) module.RunUi(ct => current.LoadMetadataAsync(ct)); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => session?.Cancel();
}
