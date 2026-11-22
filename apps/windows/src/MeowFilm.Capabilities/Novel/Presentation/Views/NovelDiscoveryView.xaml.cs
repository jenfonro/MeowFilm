using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Capabilities;

public sealed class NovelFilterChoiceItem : INotifyPropertyChanged
{
    private bool isSelected;
    public NovelKind Kind { get; }
    public string Title => Kind.Title;
    public string AutomationId => "novel.discovery.kind:" + Kind.Url;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelFilterChoiceItem(NovelKind kind, bool isSelected)
    {
        Kind = kind;
        this.isSelected = isSelected;
    }
}

public sealed class NovelDiscoveryBookItem : INotifyPropertyChanged
{
    private NovelBook book;
    private string sourceName;
    private bool onShelf;

    public NovelSourceService Sources { get; }
    public NovelBook Book => book;
    public string Title => book.Name;
    public string Author => book.Author;
    public string AuthorAndKind => string.Join(" · ", new[] { book.Author, NovelJson.Text(book.Data, "kind") }.Where(s => s.Length > 0));
    public string Metadata => string.Join(" · ", new[] { NovelJson.Text(book.Data, "kind"), NovelJson.Text(book.Data, "wordCount") }.Where(s => s.Length > 0));
    public string Intro => book.Intro;
    public string SourceName => sourceName;
    public bool OnShelf => onShelf;
    public string ShelfLabel => onShelf ? "移出书架" : "加入书架";
    public Visibility AddIconVisibility => onShelf ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckedIconVisibility => onShelf ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelDiscoveryBookItem(NovelSourceService sources, NovelBook book, string sourceName, bool onShelf)
    {
        Sources = sources;
        this.book = book;
        this.sourceName = sourceName;
        this.onShelf = onShelf;
    }

    internal void Update(NovelBook nextBook, string nextSourceName, bool nextOnShelf)
    {
        bool bookChanged = book != nextBook;
        bool sourceChanged = sourceName != nextSourceName;
        bool shelfChanged = onShelf != nextOnShelf;
        if (!bookChanged && !sourceChanged && !shelfChanged) return;
        book = nextBook;
        sourceName = nextSourceName;
        onShelf = nextOnShelf;
        if (bookChanged)
        {
            Notify(nameof(Book));
            Notify(nameof(Title));
            Notify(nameof(Author));
            Notify(nameof(AuthorAndKind));
            Notify(nameof(Metadata));
            Notify(nameof(Intro));
        }
        if (sourceChanged) Notify(nameof(SourceName));
        if (shelfChanged)
        {
            Notify(nameof(OnShelf));
            Notify(nameof(ShelfLabel));
            Notify(nameof(AddIconVisibility));
            Notify(nameof(CheckedIconVisibility));
        }
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class NovelDiscoveryView : UserControl
{
    private readonly NovelCapability module;
    private readonly bool categories;
    private readonly ObservableCollection<NovelFilterChoiceItem> filterItems = [];
    private readonly ObservableCollection<NovelDiscoveryBookItem> bookItems = [];
    private readonly Dictionary<string, NovelDiscoveryBookItem> bookLookup = new(StringComparer.Ordinal);

    private IReadOnlyList<NovelSource>? displayedSources;
    private NovelSource? displayedSource;
    private NovelKind[]? displayedKinds;
    private IReadOnlyList<NovelKind>? displayedKindsInput;
    private (string? Id, long? Revision)? channelSource;
    private IReadOnlyList<NovelDiscoveryGroup> groups = [];
    private string? displayedFilter;
    private string? displayedKind;
    private bool showBooks;
    private bool? displayedBooks;
    private bool booksDirty = true;
    private bool presenting;
    private bool changingShelf;
    private bool? stackedFeature;
    private bool? loadingDisplayed;
    private IReadOnlyList<NovelEntry>? displayedEntries;
    private HashSet<string> displayedShelf = [];
    private NovelBook? featuredBook;
    private NovelEntry? continuationEntry;
    private long shownFailure;
    private bool showingFailure;

    internal ScrollViewer ScrollHost => Scroller;

    internal NovelDiscoveryView(NovelCapability module, bool categories)
    {
        this.module = module;
        this.categories = categories;
        showBooks = categories;
        InitializeComponent();

        AutomationProperties.SetName(RootPanel, categories ? "小说分类" : "小说首页");
        FilterChoices.ItemsSource = filterItems;
        if (categories)
        {
            CategoryBooksList.ItemsSource = bookItems;
            CategoryBooksList.Visibility = Visibility.Visible;
        }
        else
        {
            HomeBooksList.ItemsSource = bookItems;
            HomeBooksList.Visibility = Visibility.Visible;
            AllCategoriesButton.Visibility = Visibility.Visible;
            FeaturedCover.Sources = module.Sources;
            ContinuationCover.Sources = module.Sources;
        }

        ChannelBar.ChannelSelected += ChannelBar_ChannelSelected;
        module.DiscoverBooks.CollectionChanged += DiscoverBooks_CollectionChanged;
    }

    internal void Detach()
    {
        ChannelBar.Close();
        module.DiscoverBooks.CollectionChanged -= DiscoverBooks_CollectionChanged;
    }

    internal void ClosePopups() => ChannelBar.Close();

    private void DiscoverBooks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => booksDirty = true;

    internal void Present()
    {
        presenting = true;
        try
        {
            bool sourceChanged = !ReferenceEquals(displayedSources, module.Sources.Sources) || displayedSource != module.ExploreSource;
            if (sourceChanged)
            {
                displayedSources = module.Sources.Sources;
                displayedSource = module.ExploreSource;
                var choices = module.Sources.Sources.Where(NovelCapability.CanExplore).ToList();
                if (module.ExploreSource is { } selected && !choices.Any(s => s.InstallationId == selected.InstallationId))
                    choices.Insert(0, selected);
                SourceBox.ItemsSource = choices;
                SourceBox.SelectedItem = module.ExploreSource;
            }

            bool kindsChanged = displayedKinds is null ||
                !ReferenceEquals(displayedKindsInput, module.Kinds) && !displayedKinds.SequenceEqual(module.Kinds) ||
                channelSource != (module.ExploreSource?.InstallationId, module.ExploreSource?.Revision);
            displayedKindsInput = module.Kinds;
            if (kindsChanged)
            {
                channelSource = (module.ExploreSource?.InstallationId, module.ExploreSource?.Revision);
                displayedKinds = module.Kinds.ToArray();
                groups = NovelDiscoveryKinds.Groups(module.Kinds, rankingsOnly: !categories);
                ChannelBar.SetChannels(BuildChannelItems(groups));
                displayedFilter = null;
            }

            if (kindsChanged || displayedKind != module.ExploreKind?.Url)
            {
                displayedKind = module.ExploreKind?.Url;
                var current = groups.FirstOrDefault(g => g.Kinds.Any(k => k.Url == displayedKind));
                string filter = categories && current is not null && !string.IsNullOrWhiteSpace(current.Title) ? current.Kinds[0].Url : "";
                if (displayedFilter != filter)
                {
                    displayedFilter = filter;
                    UpdateFilters(filter.Length > 0 ? current : null);
                }
                else
                {
                    foreach (var choice in filterItems)
                        choice.IsSelected = choice.Kind.Url == displayedKind;
                }
                ChannelBar.UpdateSelection(displayedKind);
                showBooks = categories || groups.Any(g => g.Kinds.Any(k => k.Url == displayedKind));
            }

            if (booksDirty || displayedBooks != showBooks || sourceChanged)
            {
                booksDirty = false;
                displayedBooks = showBooks;
                SyncBooks(showBooks ? module.DiscoverBooks.ToArray() : []);
            }

            if (!ReferenceEquals(displayedEntries, module.Library.Entries))
            {
                displayedEntries = module.Library.Entries;
                var shelf = module.Library.Entries.Where(e => e.OnShelf).Select(e => e.Book.SourceIdentity).ToHashSet(StringComparer.Ordinal);
                var changed = new HashSet<string>(displayedShelf, StringComparer.Ordinal);
                changed.SymmetricExceptWith(shelf);
                foreach (var identity in changed)
                {
                    if (bookLookup.TryGetValue(identity, out var item))
                        item.Update(item.Book, item.SourceName, shelf.Contains(identity));
                }
                displayedShelf = shelf;
                if (!categories)
                {
                    if (featuredBook is not null && changed.Contains(featuredBook.SourceIdentity))
                        UpdateFeaturedShelf();
                    UpdateContinuation();
                }
            }

            UpdateLoadingVisuals();
            MoreButton.IsEnabled = !module.LoadingDiscovery;
            MoreButton.Visibility = showBooks && module.MoreDiscovery ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = BuildStatusText();
            StatusText.Visibility = StatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = module.DiscoveryState == NovelDiscoveryState.Failed ? Visibility.Visible : Visibility.Collapsed;
            ImportButton.Visibility = module.ExploreSource is null && !module.LoadingDiscovery ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            presenting = false;
        }
    }

    internal void PresentFailureIfNeeded()
    {
        if (module.DiscoveryState != NovelDiscoveryState.Failed || module.LoadingDiscovery || !IsLoaded || !DesktopVisual.IsVisible(this))
            return;
        if (showingFailure || shownFailure == module.DiscoveryGeneration) return;
        module.RunUi(ShowRetryDialogAsync);
    }

    private List<NovelChannelItem> BuildChannelItems(IReadOnlyList<NovelDiscoveryGroup> channelGroups)
    {
        var items = new List<NovelChannelItem>();
        if (!categories)
        {
            foreach (var kind in channelGroups.SelectMany(g => g.Kinds))
                items.Add(new(kind.Title, "novel.discovery.kind:" + kind.Url, kind, [kind], module.ExploreKind?.Url == kind.Url));
            return items;
        }
        foreach (var group in channelGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Title))
            {
                foreach (var kind in group.Kinds)
                    items.Add(new(kind.Title, "novel.discovery.kind:" + kind.Url, kind, [kind], module.ExploreKind?.Url == kind.Url));
                continue;
            }
            var entry = group.Kinds.FirstOrDefault(k => k.Url == module.ExploreKind?.Url) ?? group.Kinds[0];
            bool selected = group.Kinds.Any(k => k.Url == module.ExploreKind?.Url);
            items.Add(new(group.Title, "novel.discovery.group:" + group.Kinds[0].Url, entry, group.Kinds, selected));
        }
        return items;
    }

    private void UpdateFilters(NovelDiscoveryGroup? group)
    {
        filterItems.Clear();
        if (group is null)
        {
            FilterRow.Visibility = Visibility.Collapsed;
            return;
        }
        FilterLabel.Text = group.Title;
        ToolTipService.SetToolTip(FilterLabel, group.Title);
        foreach (var kind in group.Kinds)
            filterItems.Add(new(kind, kind.Url == displayedKind));
        FilterRow.Visibility = Visibility.Visible;
    }

    private void SyncBooks(NovelBook[] desired)
    {
        var identities = desired.Select(b => b.SourceIdentity).ToHashSet(StringComparer.Ordinal);
        for (int i = bookItems.Count - 1; i >= 0; i--)
        {
            if (!identities.Contains(bookItems[i].Book.SourceIdentity))
            {
                bookLookup.Remove(bookItems[i].Book.SourceIdentity);
                bookItems.RemoveAt(i);
            }
        }
        for (int index = 0; index < desired.Length; index++)
        {
            var book = desired[index];
            string sourceName = SourceName(book);
            bool onShelf = IsOnShelf(book);
            if (bookLookup.TryGetValue(book.SourceIdentity, out var existing))
            {
                existing.Update(book, sourceName, onShelf);
                int currentIdx = bookItems.IndexOf(existing);
                if (currentIdx != index && currentIdx >= 0) bookItems.Move(currentIdx, index);
            }
            else
            {
                var item = new NovelDiscoveryBookItem(module.Sources, book, sourceName, onShelf);
                bookLookup.Add(book.SourceIdentity, item);
                bookItems.Insert(index, item);
            }
        }

        if (!categories)
        {
            UpdateFeatured(desired.FirstOrDefault());
            UpdateContinuation();
        }
    }

    private void UpdateFeatured(NovelBook? book)
    {
        featuredBook = book;
        if (book is null)
        {
            FeaturedCover.Book = null;
            FeatureRow.Visibility = Visibility.Collapsed;
            return;
        }
        FeatureRow.Visibility = Visibility.Visible;
        FeaturedCover.Sources = module.Sources;
        FeaturedCover.Book = book;
        FeaturedSourceText.Text = "选读 · " + SourceName(book);
        FeaturedTitleText.Text = book.Name;
        AutomationProperties.SetName(FeaturedTitleButton, book.Name);
        string extra = string.Join(" · ", new[] { NovelJson.Text(book.Data, "kind"), NovelJson.Text(book.Data, "wordCount") }.Where(s => s.Length > 0));
        FeaturedMetaText.Text = string.Join(" · ", new[] { book.Author, extra }.Where(s => s.Length > 0));
        FeaturedIntroText.Text = book.Intro;
        UpdateFeaturedShelf();
    }

    private void UpdateFeaturedShelf()
    {
        if (featuredBook is null) return;
        bool onShelf = IsOnShelf(featuredBook);
        string name = onShelf ? "移出书架" : "加入书架";
        AutomationProperties.SetName(FeaturedShelfButton, name);
        ToolTipService.SetToolTip(FeaturedShelfButton, name);
        FeaturedShelfButton.Content = onShelf ? "✓  已加入书架" : "＋  加入书架";
    }

    private void UpdateContinuation()
    {
        var next = module.Library.Entries.Where(e => e.ReadAt is not null).OrderByDescending(e => e.ReadAt).FirstOrDefault();
        if (next != continuationEntry)
        {
            continuationEntry = next;
            if (next is null)
            {
                ContinuationCover.Book = null;
            }
            else
            {
                ContinuationCover.Sources = module.Sources;
                ContinuationCover.Book = next.Book;
                ContinuationNameText.Text = next.Book.Name;
                ContinuationAuthorText.Text = next.Book.Author;
                ContinuationChapterText.Text = next.Anchor?.ChapterTitle ?? "";
                ContinuationPercentText.Text = next.Anchor is { } anchor ? $"本章 {anchor.Ratio:P0}" : "";
                if (next.ReadAt is { } readAt)
                {
                    int days = (DateTimeOffset.Now.Date - readAt.LocalDateTime.Date).Days;
                    ContinuationDateText.Text = days == 0 ? "今天" : days == 1 ? "昨天" : readAt.LocalDateTime.ToString("MM-dd");
                }
            }
        }
        bool hasHistory = continuationEntry is not null;
        ContinuationEmptyText.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        ContinuationContent.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        ContinuationFooter.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        ContinuationDateText.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLoadingVisuals()
    {
        if (loadingDisplayed == module.LoadingDiscovery) return;
        loadingDisplayed = module.LoadingDiscovery;
        bool loading = loadingDisplayed == true;
        string name = loading ? "取消加载" : "刷新";
        AutomationProperties.SetName(RefreshButton, name);
        ToolTipService.SetToolTip(RefreshButton, name);
        RefreshIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        CancelIcon.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SourceName(NovelBook book) =>
        module.Sources.Sources.FirstOrDefault(s => s.InstallationId == book.InstallationId)?.Name
        ?? NovelJson.Text(book.Data, "originName");

    private bool IsOnShelf(NovelBook book) =>
        module.Library.Entries.Any(e => e.OnShelf && e.Book.SourceIdentity == book.SourceIdentity);

    private string BuildStatusText() => module.DiscoveryState switch
    {
        NovelDiscoveryState.Loading => "加载中…",
        NovelDiscoveryState.Cancelling => "正在取消…",
        NovelDiscoveryState.Cancelled => "已取消",
        NovelDiscoveryState.Unavailable => module.ExploreSource is null ? "无可用发现书源" : "书源不可用",
        NovelDiscoveryState.Empty => module.ExploreKind is null ? categories ? "暂无可用分类" : "暂无可用榜单" : "暂无书籍",
        _ => ""
    };

    private async Task ShelfBookAsync(NovelBook book, bool add, CancellationToken token)
    {
        if (changingShelf) return;
        changingShelf = true;
        try
        {
            string identity = await module.Rules.IdentityAsync(book, token);
            token.ThrowIfCancellationRequested();
            var entry = module.Library.Find(book, identity);
            if (entry is not null)
            {
                if (entry.OnShelf != add) await module.Library.SetShelfAsync(entry.BookId, add, token);
            }
            else if (add)
            {
                await module.Library.ToggleShelfAsync(new(Guid.NewGuid().ToString("N"), identity, book), token);
            }
            module.Notify(add ? "已加入书架" : "已移出书架");
        }
        finally
        {
            changingShelf = false;
        }
    }

    private async Task ReadBookAsync(NovelBook book, CancellationToken token)
    {
        await module.ReceiveBookAsync(book, token);
        token.ThrowIfCancellationRequested();
        await module.StartReadingAsync(token);
    }

    private async Task ShowRetryDialogAsync(CancellationToken token)
    {
        if (showingFailure) return;
        showingFailure = true;
        shownFailure = module.DiscoveryGeneration;
        try
        {
            string sourceLabel = module.ExploreSource?.Name ?? "书源";
            string message = module.DiscoveryError is { Length: > 0 } detail && detail != "发现加载失败"
                ? $"{sourceLabel}\n{detail}"
                : sourceLabel;
            var request = new DesktopDialogRequest
            {
                Title = "发现加载失败",
                Message = message,
                PrimaryButtonText = "重试",
                CloseButtonText = "取消",
                DefaultButton = DesktopDialogDefaultButton.Primary,
                OnPrimaryAsync = async () =>
                {
                    await module.RetryDiscoveryAsync(token);
                    if (module.DiscoveryState == NovelDiscoveryState.Failed)
                    {
                        shownFailure = module.DiscoveryGeneration;
                        module.Notify("发现加载失败，请重试");
                        return false;
                    }
                    return true;
                }
            };
            await DesktopModalHost.ShowDialogAsync(this, request, token);
        }
        finally
        {
            showingFailure = false;
            PresentFailureIfNeeded();
        }
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!presenting && SourceBox.SelectedItem is NovelSource source && source != module.ExploreSource && NovelCapability.CanExplore(source))
            module.RunUi(ct => module.LoadDiscoveryAsync(source, null, true, ct));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (module.LoadingDiscovery) module.CancelDiscovery();
        else module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true, ct));
    }

    private void ChannelBar_ChannelSelected(object? sender, NovelChannelItem item)
    {
        if (presenting) return;
        var kind = item.Kinds.FirstOrDefault(k => k.Url == module.ExploreKind?.Url) ?? item.PrimaryKind;
        if (kind.Url != module.ExploreKind?.Url)
            module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, kind, false, ct));
    }

    private void FilterChoice_Click(object sender, RoutedEventArgs e)
    {
        if (!presenting && sender is FrameworkElement { DataContext: NovelFilterChoiceItem choice } && choice.Kind.Url != module.ExploreKind?.Url)
            module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, choice.Kind, false, ct));
    }

    private void FilterChoice_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } choice) choice.StartBringIntoView();
    }

    private void FilterChoice_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (filterItems.Count == 0 || sender is not FrameworkElement { DataContext: NovelFilterChoiceItem current }) return;
        if (e.Key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return;
        int index = filterItems.IndexOf(current);
        if (index < 0) return;
        int next = e.Key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => filterItems.Count - 1,
            VirtualKey.Left => (index + filterItems.Count - 1) % filterItems.Count,
            _ => (index + 1) % filterItems.Count
        };
        var target = filterItems[next];
        if (FilterChoices.ContainerFromIndex(next) is FrameworkElement container)
        {
            container.Focus(FocusState.Programmatic);
            container.StartBringIntoView();
        }
        if (target.Kind.Url != module.ExploreKind?.Url)
            module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, target.Kind, false, ct));
        e.Handled = true;
    }

    private void FeatureRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool next = FeatureRow.ActualWidth < 760;
        if (next == stackedFeature) return;
        stackedFeature = next;
        FeatureCol0.Width = new GridLength(next ? 1 : 9, GridUnitType.Star);
        FeatureColGap.Width = new GridLength(next ? 0 : 16);
        FeatureCol2.Width = next ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        FeatureRowGap.Height = new GridLength(next ? 16 : 0);
        Grid.SetColumn(ContinuationCard, next ? 0 : 2);
        Grid.SetRow(ContinuationCard, next ? 2 : 0);
    }

    private void FeaturedCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool hideCover = FeaturedCard.ActualWidth < 500;
        FeaturedCoverBorder.Visibility = hideCover ? Visibility.Collapsed : Visibility.Visible;
        FeaturedCoverCol.Width = new GridLength(hideCover ? 0 : 149);
    }

    private void FeaturedOpen_Click(object sender, RoutedEventArgs e)
    {
        if (featuredBook is { } book) module.RunUi(ct => module.OpenBookAsync(book, ct));
    }

    private void FeaturedRead_Click(object sender, RoutedEventArgs e)
    {
        if (featuredBook is { } book) module.RunUi(ct => ReadBookAsync(book, ct));
    }

    private void FeaturedShelf_Click(object sender, RoutedEventArgs e)
    {
        if (featuredBook is { } book) module.RunUi(ct => ShelfBookAsync(book, !IsOnShelf(book), ct));
    }

    private void ContinuationResume_Click(object sender, RoutedEventArgs e)
    {
        if (continuationEntry is { } entry) module.RunUi(ct => module.ContinueReadingAsync(entry, ct));
    }

    private void AllCategories_Click(object sender, RoutedEventArgs e) => module.Request(NovelCapability.CategoriesRoute);

    private void BookOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelDiscoveryBookItem item })
            module.RunUi(ct => module.OpenBookAsync(item.Book, ct));
    }

    private void BookShelf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelDiscoveryBookItem item })
            module.RunUi(ct => ShelfBookAsync(item.Book, !IsOnShelf(item.Book), ct));
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => module.RunUi(ShowRetryDialogAsync);
    private void Import_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.ImportSourcesAsync(this, ct));
    private void More_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false, ct));
}
