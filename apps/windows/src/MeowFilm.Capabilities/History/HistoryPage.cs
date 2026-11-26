using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

public sealed class HistoryPage : IDesktopPage, IDesktopHeaderSource, IDebugModuleAdapterSource, INotifyPropertyChanged, IDisposable
{
    public const string PageId = "history";
    private const string HistoryOutlinePath = "M12,4 A8,8 0 1 1 4,12 M4,8 L4,12 L8,12 M12,8 L12,12 L15,14";

    private readonly FilmCapability film;
    private readonly NovelCapability novel;
    private readonly HistoryPageView view;
    private readonly HistoryDebugAdapter debugAdapter;
    private string selectedTab = "film";
    private bool active;
    private bool subscribed;
    private bool disposed;

    public HistoryPage(FilmCapability film, NovelCapability novel)
    {
        this.film = film ?? throw new ArgumentNullException(nameof(film));
        this.novel = novel ?? throw new ArgumentNullException(nameof(novel));
        Tabs = Array.AsReadOnly(new[]
        {
            new HistoryTab("film", "影视", selected: true),
            new HistoryTab("novel", "小说", selected: false)
        });
        FilmController = new HistoryDomainController(
            "film",
            film,
            novel,
            OpenFilmCardAsync,
            OpenNovelEntryAsync,
            ReportError,
            () => HeaderChanged?.Invoke(this, EventArgs.Empty));
        NovelController = new HistoryDomainController(
            "novel",
            film,
            novel,
            OpenFilmCardAsync,
            OpenNovelEntryAsync,
            ReportError,
            () => HeaderChanged?.Invoke(this, EventArgs.Empty));
        view = new HistoryPageView(this);
        debugAdapter = new HistoryDebugAdapter(this);
    }

    public string Id => PageId;
    public string Title => "历史";
    public string Glyph => "\uE81C";
    public string? IconData => HistoryOutlinePath;
    public bool ShowInSidebar => true;
    public FrameworkElement View => view;
    internal FrameworkElement DialogOwner => view;
    public IDebugModuleAdapter DebugAdapter => debugAdapter;

    public IReadOnlyList<HistoryTab> Tabs { get; }
    public string SelectedTab => selectedTab;
    public bool IsFilmSelected => selectedTab == "film";
    public bool IsNovelSelected => selectedTab == "novel";

    internal HistoryDomainController FilmController { get; }
    internal HistoryDomainController NovelController { get; }
    internal HistoryDomainController ActiveController => selectedTab == "novel" ? NovelController : FilmController;

    public Func<FilmCardItem, CancellationToken, Task>? NavigateToPlaybackAsync { get; set; }
    public Func<CancellationToken>? FilmNavigationToken { get; set; }
    public Func<NovelEntry, CancellationToken, Task>? NavigateToNovelAsync { get; set; }
    public Func<CancellationToken>? NovelNavigationToken { get; set; }

    public DesktopHeaderContribution Header => new(
        ActiveController.IsData ? ActiveController.ActiveData.Title : "历史",
        SearchVisible: true,
        SearchEnabled: true)
    {
        SearchTargetId = selectedTab,
        SearchContextId = selectedTab
    };

    public event EventHandler? HeaderChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private CancellationTokenSource? activeScope;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        CancelScope();
        var scope = new CancellationTokenSource();
        activeScope = scope;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Token);
        active = true;
        Subscribe();
        await Task.WhenAll(
            FilmController.ReloadAsync(linked.Token),
            NovelController.ReloadAsync(linked.Token));
    }

    public Task<bool> TryGoBackAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ActiveController.TryExitData());

    public void Deactivate()
    {
        active = false;
        Unsubscribe();
        CancelScope();
    }

    public async Task SelectTabAsync(string tabId, CancellationToken cancellationToken = default)
    {
        if (disposed || tabId is not ("film" or "novel")) return;
        if (selectedTab == tabId)
        {
            ActiveController.TryExitData();
            return;
        }
        selectedTab = tabId;
        foreach (var tab in Tabs) tab.Select(tab.Id == tabId);
        Changed(nameof(SelectedTab));
        Changed(nameof(IsFilmSelected));
        Changed(nameof(IsNovelSelected));
        HeaderChanged?.Invoke(this, EventArgs.Empty);
        if (active && activeScope is { } scope)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Token);
            await ActiveController.ReloadAsync(linked.Token);
        }
    }

    private async Task OpenFilmCardAsync(FilmCardItem card)
    {
        if (!active || NavigateToPlaybackAsync is null) return;
        var token = FilmNavigationToken?.Invoke() ?? default;
        try
        {
            await NavigateToPlaybackAsync(card, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "history.open", "film.open.failed",
                new { title = card.Title }, error: ex);
            if (active) ReportError("无法打开影片");
        }
    }

    private async Task OpenNovelEntryAsync(NovelEntry entry)
    {
        if (!active || NavigateToNovelAsync is null) return;
        var token = NovelNavigationToken?.Invoke() ?? default;
        try
        {
            await NavigateToNovelAsync(entry, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLog.Warn(DebugCategories.Novel, "history.open", "novel.open.failed",
                new { bookId = entry.BookId, name = entry.Book.Name }, error: ex);
            if (active) ReportError("无法打开书籍");
        }
    }

    private void Subscribe()
    {
        if (subscribed) return;
        film.FavoritesLibrary.Changed += OnFilmFavoritesChanged;
        film.HistoryInvalidated += OnFilmHistoryInvalidated;
        novel.Library.Changed += OnNovelChanged;
        novel.Sources.Changed += OnNovelChanged;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;
        film.FavoritesLibrary.Changed -= OnFilmFavoritesChanged;
        film.HistoryInvalidated -= OnFilmHistoryInvalidated;
        novel.Library.Changed -= OnNovelChanged;
        novel.Sources.Changed -= OnNovelChanged;
        subscribed = false;
    }

    private void OnFilmFavoritesChanged(object? sender, EventArgs e)
    {
        if (disposed || !active) return;
        _ = view.DispatcherQueue.TryEnqueue(async () =>
        {
            if (disposed || !active || activeScope is not { } scope) return;
            try { await FilmController.RefreshFavoritesOnlyAsync(scope.Token); }
            catch { }
        });
    }

    private void OnFilmHistoryInvalidated(object? sender, EventArgs e)
    {
        if (disposed || !active) return;
        _ = view.DispatcherQueue.TryEnqueue(async () =>
        {
            if (disposed || !active || activeScope is not { } scope) return;
            try { await FilmController.RefreshHistoryOnlyAsync(scope.Token); }
            catch { }
        });
    }

    private void OnNovelChanged(object? sender, EventArgs e)
    {
        if (disposed || !active) return;
        _ = view.DispatcherQueue.TryEnqueue(async () =>
        {
            if (disposed || !active || activeScope is not { } scope) return;
            try { await NovelController.ReloadAsync(scope.Token); }
            catch { }
        });
    }

    private void CancelScope()
    {
        var previous = activeScope;
        activeScope = null;
        if (previous is null) return;
        try { previous.Cancel(); } catch { }
        previous.Dispose();
    }

    private void ReportError(string message) => view.PostNotice(message);

    public void Dispose()
    {
        if (disposed) return;
        Deactivate();
        disposed = true;
        NavigateToPlaybackAsync = null;
        FilmNavigationToken = null;
        NavigateToNovelAsync = null;
        NovelNavigationToken = null;
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
