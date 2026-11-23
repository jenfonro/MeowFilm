using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class NovelSwitchCandidateItem : INotifyPropertyChanged
{
    private NovelBook book;
    private string sourceName;
    private bool isCurrent;

    public NovelBook Book => book;
    public string SourceName => sourceName;
    public Visibility CurrentBadgeVisibility => isCurrent ? Visibility.Visible : Visibility.Collapsed;
    public string BookSummary => string.Join(" · ", new[] { book.Name, book.Author, book.LatestChapterTitle }.Where(s => s.Length > 0));

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelSwitchCandidateItem(NovelBook book, string sourceName, bool isCurrent)
    {
        this.book = book;
        this.sourceName = sourceName;
        this.isCurrent = isCurrent;
    }

    internal void Update(NovelBook nextBook, string nextSourceName, bool nextIsCurrent)
    {
        bool bookChanged = book != nextBook;
        bool sourceChanged = sourceName != nextSourceName;
        bool currentChanged = isCurrent != nextIsCurrent;
        if (!bookChanged && !sourceChanged && !currentChanged) return;
        book = nextBook;
        sourceName = nextSourceName;
        isCurrent = nextIsCurrent;
        if (sourceChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceName)));
        if (currentChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentBadgeVisibility)));
        if (bookChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BookSummary)));
    }
}

public sealed partial class NovelSwitchSourceDialogView : UserControl
{
    private readonly NovelCapability module;
    private readonly NovelBookSession session;
    private readonly NovelSearch search;
    private readonly CancellationToken token;
    private readonly ObservableCollection<NovelSwitchCandidateItem> candidates = [];
    private readonly Dictionary<string, NovelSwitchCandidateItem> candidateLookup = new(StringComparer.Ordinal);
    private IReadOnlyList<JsonElement>? displayedGroups;
    private string? displayedFilterGroup;
    private Task? activeSwitch;
    private bool switching, switched;

    private NovelSwitchSourceDialogView(
        NovelCapability module,
        NovelBookSession session,
        NovelSearch search,
        CancellationToken token)
    {
        this.module = module;
        this.session = session;
        this.search = search;
        this.token = token;
        InitializeComponent();

        GroupBox.ItemsSource = module.Sources.Sources
            .SelectMany(s => NovelGroups.Split(s.Group))
            .Distinct(StringComparer.CurrentCulture)
            .Order(StringComparer.CurrentCulture)
            .Prepend("全部分组")
            .ToArray();
        GroupBox.SelectedIndex = 0;
        CandidateList.ItemsSource = candidates;
        search.Changed += Search_Changed;
        Unloaded += (_, _) => { if (!IsLoaded) search.Changed -= Search_Changed; };
    }

    internal static async Task<bool> ShowAsync(
        FrameworkElement owner,
        NovelCapability module,
        NovelBookSession session,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var search = new NovelSearch(module.Sources, module.Rules);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var dialog = new NovelSwitchSourceDialogView(module, session, search, cancel.Token);
        var loading = search.StartAsync(session.Book.Name, cancel.Token);
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(
                owner,
                dialog,
                dismissOnOutside: true,
                onOpened: () => dialog.CandidateList.Focus(FocusState.Programmatic),
                cancellationToken: cancel.Token,
                onCloseRequested: () => { if (!dialog.switching) DesktopModalHost.CloseFor(owner, dialog); });
            return dialog.switched && !token.IsCancellationRequested;
        }
        finally
        {
            cancel.Cancel();
            search.Cancel();
            if (dialog.activeSwitch is { } running)
            {
                try { await running; } catch { }
            }
            try { await loading; } catch { }
            await search.PrepareShutdownAsync();
        }
    }

    private void Search_Changed(object? sender, EventArgs e) => RefreshCandidates();

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCandidates();

    private void RefreshCandidates()
    {
        string? filterGroup = GroupBox.SelectedIndex > 0 ? GroupBox.SelectedItem as string : null;
        if (ReferenceEquals(displayedGroups, search.Groups) && displayedFilterGroup == filterGroup)
        {
            UpdateActions();
            return;
        }
        displayedGroups = search.Groups;
        displayedFilterGroup = filterGroup;

        var selectedIdentity = (CandidateList.SelectedItem as NovelSwitchCandidateItem)?.Book.SourceIdentity;
        var sourceById = module.Sources.Sources.ToDictionary(s => s.InstallationId, StringComparer.Ordinal);
        var list = new List<(NovelSource Source, NovelBook Book)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in search.Groups)
        {
            foreach (var g in group.GetProperty("candidates").EnumerateArray())
            {
                string id = NovelJson.Text(g, "installationId");
                if (!sourceById.TryGetValue(id, out var source)) continue;
                if (filterGroup is not null && !NovelGroups.Split(source.Group).Contains(filterGroup)) continue;
                var book = NovelBook.From(g, source);
                if (seen.Add(book.SourceIdentity)) list.Add((source, book));
            }
        }

        string currentSourceId = session.Book.InstallationId;
        NovelPresentation.SyncKeyed(
            candidates,
            candidateLookup,
            list,
            entry => entry.Book.SourceIdentity,
            item => item.Book.SourceIdentity,
            entry => new NovelSwitchCandidateItem(entry.Book, entry.Source.Name, entry.Book.InstallationId == currentSourceId),
            (item, entry) => item.Update(entry.Book, entry.Source.Name, entry.Book.InstallationId == currentSourceId));

        if (selectedIdentity is not null && candidateLookup.TryGetValue(selectedIdentity, out var restore) &&
            !ReferenceEquals(CandidateList.SelectedItem, restore))
            CandidateList.SelectedItem = restore;
        UpdateActions();
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private void UpdateActions()
    {
        SwitchButton.IsEnabled = !switching && CandidateList.SelectedItem is NovelSwitchCandidateItem;
        CancelButton.IsEnabled = !switching;
        CloseButton.IsEnabled = !switching;
        RefreshButton.IsEnabled = !switching;
        LoadMoreButton.IsEnabled = !switching;
        GroupBox.IsEnabled = !switching;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        module.RunUi(_ => search.StartAsync(session.Book.Name, token, restart: true));

    private void LoadMore_Click(object sender, RoutedEventArgs e) =>
        module.RunUi(() => search.LoadMoreAsync(token));

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (switching || CandidateList.SelectedItem is not NovelSwitchCandidateItem item) return;
        var task = SwitchCoreAsync(item);
        activeSwitch = task;
        await task;
    }

    private async Task SwitchCoreAsync(NovelSwitchCandidateItem item)
    {
        switching = true;
        UpdateActions();
        try
        {
            var target = await module.Rules.InvokeAsync(new { operation = "identity", book = session.Book.Json() }, token);
            var resolved = await session.ResolveCandidateAsync(item.Book, token);
            var book = resolved.Book;
            var candidate = await module.Rules.InvokeAsync(new { operation = "identity", book = book.Json() }, token);
            string author = NovelJson.Text(target, "author"), nextAuthor = NovelJson.Text(candidate, "author");
            if (NovelJson.Text(target, "name") != NovelJson.Text(candidate, "name") ||
                author.Length > 0 && nextAuthor.Length > 0 && author != nextAuthor)
                throw new InvalidDataException("所选书源与当前书籍不匹配");
            if (author.Length == 0 || nextAuthor.Length == 0)
            {
                var confirm = new DesktopDialogRequest
                {
                    Title = "确认换源",
                    Message = $"切换至《{book.Name}》 · {book.Author}？",
                    PrimaryButtonText = "切换",
                    CloseButtonText = "取消",
                    DefaultButton = DesktopDialogDefaultButton.Primary
                };
                if (await DesktopModalHost.ShowDialogAsync(this, confirm, token, stack: true) != DesktopDialogResult.Primary)
                    return;
            }
            await session.SwitchSourceAsync(resolved, token);
            switched = true;
            DesktopModalHost.CloseFor(this, this);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Notices.Show(this, error is InvalidDataException ? error.Message : "切换书源失败，请重试");
        }
        finally
        {
            switching = false;
            UpdateActions();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!switching) DesktopModalHost.CloseFor(this, this);
    }
}
