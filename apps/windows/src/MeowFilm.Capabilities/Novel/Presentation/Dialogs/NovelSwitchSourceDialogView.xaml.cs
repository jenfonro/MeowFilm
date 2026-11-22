using System.Collections.ObjectModel;
using System.IO;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class NovelSwitchCandidateItem(NovelBook book, string sourceName, bool isCurrent)
{
    public NovelBook Book { get; } = book;
    public string SourceName { get; } = sourceName;
    public Visibility CurrentBadgeVisibility => isCurrent ? Visibility.Visible : Visibility.Collapsed;
    public string BookSummary => string.Join(" · ", new[] { Book.Name, Book.Author, Book.LatestChapterTitle }.Where(s => s.Length > 0));
}

public sealed partial class NovelSwitchSourceDialogView : UserControl
{
    private readonly NovelCapability module;
    private readonly NovelBookSession session;
    private readonly NovelSearch search;
    private readonly CancellationToken token;
    private readonly ObservableCollection<NovelSwitchCandidateItem> candidates = [];
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

        GroupBox.ItemsSource = module.Sources.Sources.Select(s => s.Group).Prepend("全部分组").Distinct().ToArray();
        GroupBox.SelectedIndex = 0;
        CandidateList.ItemsSource = candidates;
        search.Changed += Search_Changed;
        Unloaded += (_, _) => search.Changed -= Search_Changed;
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
                cancellationToken: cancel.Token);
            return dialog.switched && !token.IsCancellationRequested;
        }
        finally
        {
            cancel.Cancel();
            search.Cancel();
            try { await loading; } catch (OperationCanceledException) { }
            await search.PrepareShutdownAsync();
        }
    }

    private void Search_Changed(object? sender, EventArgs e) => RefreshCandidates();

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCandidates();

    private void RefreshCandidates()
    {
        var selectedBook = (CandidateList.SelectedItem as NovelSwitchCandidateItem)?.Book;
        string? filterGroup = GroupBox.SelectedIndex > 0 ? GroupBox.SelectedItem as string : null;
        var list = search.Groups
            .SelectMany(g => g.GetProperty("candidates").EnumerateArray())
            .Select(g =>
            {
                string id = NovelJson.Text(g, "installationId");
                var source = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == id);
                return source is null ? ((NovelSource Source, NovelBook Book)?)null : (source, NovelBook.From(g, source));
            })
            .OfType<(NovelSource Source, NovelBook Book)>()
            .Where(item => filterGroup is null || item.Source.Group == filterGroup)
            .ToArray();

        candidates.Clear();
        NovelSwitchCandidateItem? restore = null;
        foreach (var (source, book) in list)
        {
            var entry = new NovelSwitchCandidateItem(book, source.Name, book.InstallationId == session.Book.InstallationId);
            candidates.Add(entry);
            if (selectedBook is not null && book.SourceIdentity == selectedBook.SourceIdentity) restore = entry;
        }
        if (restore is not null) CandidateList.SelectedItem = restore;
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
        switching = true;
        UpdateActions();
        try
        {
            var book = item.Book;
            var target = await module.Rules.InvokeAsync(new { operation = "identity", book = session.Book.Json() }, token);
            var resolved = new NovelBookSession(module.Sources, module.Library, module.Rules, new("candidate", "", book));
            await resolved.LoadMetadataAsync(token);
            book = resolved.Book;
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
            await session.SwitchSourceAsync(book, token);
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
