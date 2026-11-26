using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

internal sealed class HistoryDomainController : ICatalogBrowseController
{
    internal const string FavoritesKey = "favorites";
    internal const string HistoryKey = "history";
    private const int FilmHistoryPageSize = 100;

    private readonly string domain;
    private readonly FilmCapability film;
    private readonly NovelCapability novel;
    private readonly Func<FilmCardItem, Task> openFilmAsync;
    private readonly Func<NovelEntry, Task> openNovelAsync;
    private readonly Action<string> reportError;
    private readonly Action onModeChanged;
    private JsonElement? filmHistoryCursor;
    private bool isData;
    private bool loading;

    internal HistoryDomainController(
        string domain,
        FilmCapability film,
        NovelCapability novel,
        Func<FilmCardItem, Task> openFilmAsync,
        Func<NovelEntry, Task> openNovelAsync,
        Action<string> reportError,
        Action onModeChanged)
    {
        this.domain = domain;
        this.film = film;
        this.novel = novel;
        this.openFilmAsync = openFilmAsync;
        this.openNovelAsync = openNovelAsync;
        this.reportError = reportError;
        this.onModeChanged = onModeChanged;

        bool isFilm = domain == "film";
        string favEmpty = isFilm ? "暂无收藏" : "暂无收藏";
        string histEmpty = isFilm ? "暂无历史记录" : "暂无历史记录";

        FavoritesSection = new HistoryHomeSection(FavoritesKey, "我的收藏", favEmpty);
        HistorySection = new HistoryHomeSection(HistoryKey, "历史记录", histEmpty, actionText: "清空历史");
        Sections = [FavoritesSection, HistorySection];

        FavoritesDataSection = new HistoryDataSection(FavoritesKey, "我的收藏", favEmpty);
        HistoryDataSection = new HistoryDataSection(HistoryKey, "历史记录", histEmpty, actionText: "清空历史");
        DataSections = [FavoritesDataSection, HistoryDataSection];
        ActiveData = FavoritesDataSection;
    }

    internal string Domain => domain;
    internal IReadOnlyList<HistoryHomeSection> Sections { get; }
    internal IReadOnlyList<HistoryDataSection> DataSections { get; }
    internal HistoryHomeSection FavoritesSection { get; }
    internal HistoryHomeSection HistorySection { get; }
    internal HistoryDataSection FavoritesDataSection { get; }
    internal HistoryDataSection HistoryDataSection { get; }
    internal HistoryDataSection ActiveData { get; private set; }
    internal bool IsLoading => loading;

    public bool IsHome => !isData;
    public bool IsData => isData;
    public IEnumerable BrowseSections => Sections;
    public bool HasSections => true;
    public bool ShowHomeLoading => false;
    public bool ShowHomeStatus => false;
    public string HomeStatusText => "";
    public bool CanRetryHome => false;
    public bool CanImportHome => false;
    public CatalogDataSection BrowseActiveData => ActiveData;
    public string DataRefreshAutomationName => "刷新";
    public string DataActionAutomationName => ActiveData.ActionText;
    public string DataListAutomationName => ActiveData.Title;
    public string DataRetryAutomationName => "重试";
    public string DataMoreAutomationName => "加载更多";

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool TryExitData()
    {
        if (!isData) return false;
        isData = false;
        Changed(nameof(IsHome));
        Changed(nameof(IsData));
        onModeChanged();
        return true;
    }

    internal void OpenDataSection(string key)
    {
        var target = DataSections.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Title, key, StringComparison.OrdinalIgnoreCase)) ?? FavoritesDataSection;
        bool changedData = !ReferenceEquals(ActiveData, target);
        ActiveData = target;
        if (changedData) Changed(nameof(BrowseActiveData));
        if (!isData)
        {
            isData = true;
            Changed(nameof(IsHome));
            Changed(nameof(IsData));
            onModeChanged();
        }
        Changed(nameof(DataActionAutomationName));
        Changed(nameof(DataListAutomationName));
    }

    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        loading = true;
        try
        {
            if (domain == "film")
                await ReloadFilmAsync(cancellationToken);
            else
                await ReloadNovelAsync(cancellationToken);
        }
        finally
        {
            loading = false;
        }
    }

    internal async Task RefreshFavoritesOnlyAsync(CancellationToken cancellationToken)
    {
        if (domain != "film")
        {
            await ReloadNovelAsync(cancellationToken);
            return;
        }
        var favorites = await film.FavoritesLibrary.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        FavoritesSection.PresentItems(favorites);
        FavoritesDataSection.PresentItems(favorites, more: false);
    }

    internal async Task RefreshHistoryOnlyAsync(CancellationToken cancellationToken)
    {
        if (domain != "film")
        {
            await ReloadNovelAsync(cancellationToken);
            return;
        }
        var (historyItems, nextCursor) = await film.ReadHistoryCardsAsync(FilmHistoryPageSize, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        filmHistoryCursor = nextCursor;
        HistorySection.PresentItems(historyItems);
        HistoryDataSection.PresentItems(historyItems, more: nextCursor is not null);
    }

    private async Task ReloadFilmAsync(CancellationToken cancellationToken)
    {
        FavoritesSection.Begin();
        HistorySection.Begin();
        var favoritesTask = film.FavoritesLibrary.ReadAsync(cancellationToken);
        var historyTask = film.ReadHistoryCardsAsync(FilmHistoryPageSize, null, cancellationToken);

        try
        {
            var favorites = await favoritesTask;
            cancellationToken.ThrowIfCancellationRequested();
            FavoritesSection.PresentItems(favorites);
            FavoritesDataSection.PresentItems(favorites, more: false);
        }
        catch (OperationCanceledException)
        {
            FavoritesSection.Cancel();
            FavoritesDataSection.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Warn(DebugCategories.FilmCatalog, "history.favorites", "load.failed", error: ex);
            FavoritesSection.Fail("收藏读取失败");
            FavoritesDataSection.Fail("收藏读取失败");
        }

        try
        {
            var (historyItems, nextCursor) = await historyTask;
            cancellationToken.ThrowIfCancellationRequested();
            filmHistoryCursor = nextCursor;
            HistorySection.PresentItems(historyItems);
            HistoryDataSection.PresentItems(historyItems, more: nextCursor is not null);
        }
        catch (OperationCanceledException)
        {
            HistorySection.Cancel();
            HistoryDataSection.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "history.list", "load.failed", error: ex);
            HistorySection.Fail("历史记录读取失败");
            HistoryDataSection.Fail("历史记录读取失败");
        }
    }

    private async Task ReloadNovelAsync(CancellationToken cancellationToken)
    {
        await novel.Library.InitializeAsync(cancellationToken);
        await novel.Sources.EnsureReadyAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceNames = novel.Sources.Sources.ToDictionary(s => s.InstallationId, s => s.Name, StringComparer.Ordinal);

        var shelfCards = novel.Library.OrderedShelfEntries()
            .Select(entry =>
            {
                string badge = entry.PinnedAt is not null
                    ? "置顶"
                    : (entry.Anchor?.ChapterTitle ?? "");
                string sub = sourceNames.GetValueOrDefault(entry.Book.InstallationId) is { Length: > 0 } sn
                    ? sn
                    : entry.Book.Author;
                return (ICatalogCardDisplay)new HistoryNovelCardItem(novel.Sources, entry, badge, sub);
            })
            .ToArray();

        var historyCards = novel.Library.OrderedHistoryEntries()
            .Select(entry =>
            {
                string badge = entry.Anchor?.ChapterTitle ?? "";
                string sub = sourceNames.GetValueOrDefault(entry.Book.InstallationId) is { Length: > 0 } sn
                    ? sn
                    : entry.Book.Author;
                return (ICatalogCardDisplay)new HistoryNovelCardItem(novel.Sources, entry, badge, sub);
            })
            .ToArray();

        FavoritesSection.PresentItems(shelfCards);
        FavoritesDataSection.PresentItems(shelfCards, more: false);
        HistorySection.PresentItems(historyCards);
        HistoryDataSection.PresentItems(historyCards, more: false);
    }

    public void OnSectionAdmitted(CatalogHomeSection section) => section.MarkAdmitted();

    public Task OpenSectionAsync(CatalogHomeSection section)
    {
        OpenDataSection(section.Key);
        return Task.CompletedTask;
    }

    public Task RefreshSectionAsync(CatalogHomeSection section) => ReloadAsync(CancellationToken.None);

    public async Task InvokeSectionActionAsync(CatalogHomeSection section, FrameworkElement owner)
    {
        if (section.Key == HistoryKey)
            await ConfirmAndClearHistoryAsync(owner);
    }

    public Task RetrySectionAsync(CatalogHomeSection section) => ReloadAsync(CancellationToken.None);
    public Task RetryHomeAsync() => ReloadAsync(CancellationToken.None);
    public Task ImportHomeAsync(FrameworkElement owner) => Task.CompletedTask;

    public Task ActivateCardAsync(ICatalogCardDisplay card) => card switch
    {
        IFilmCardDisplay filmCard => openFilmAsync(filmCard.ToCard()),
        HistoryNovelCardItem novelCard => openNovelAsync(novelCard.Entry),
        _ => Task.CompletedTask
    };

    public Task RefreshDataAsync() => ReloadAsync(CancellationToken.None);

    public async Task InvokeDataActionAsync(FrameworkElement owner)
    {
        if (ActiveData.Key == HistoryKey)
            await ConfirmAndClearHistoryAsync(owner);
    }

    public Task RetryDataAsync() => ReloadAsync(CancellationToken.None);

    public async Task LoadMoreDataAsync()
    {
        if (domain != "film" || !ReferenceEquals(ActiveData, HistoryDataSection) || filmHistoryCursor is not { } cursor)
            return;
        HistoryDataSection.Begin(replace: false);
        try
        {
            var (items, nextCursor) = await film.ReadHistoryCardsAsync(FilmHistoryPageSize, cursor, CancellationToken.None);
            filmHistoryCursor = nextCursor;
            HistoryDataSection.AppendPageItems(items, more: nextCursor is not null);
        }
        catch (OperationCanceledException)
        {
            HistoryDataSection.Cancel();
        }
        catch (Exception ex)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "history.more", "load.failed", error: ex);
            HistoryDataSection.Fail("历史记录读取失败");
            reportError("历史记录加载失败");
        }
    }

    public Task SelectDataFilterAsync(CatalogFilterOption option) => Task.CompletedTask;

    public string OperationErrorText(Exception error) =>
        domain == "film" ? "无法执行影视记录操作" : "无法执行小说记录操作";

    public void ReportBrowseError(string message) => reportError(message);

    internal async Task ClearHistoryDirectAsync(CancellationToken cancellationToken)
    {
        if (domain == "film")
        {
            await film.ClearHistoryAsync(cancellationToken);
            filmHistoryCursor = null;
            HistorySection.PresentItems([]);
            HistoryDataSection.PresentItems([], more: false);
        }
        else
        {
            await novel.Library.ClearHistoryAsync(cancellationToken);
            await ReloadNovelAsync(cancellationToken);
        }
    }

    private async Task ConfirmAndClearHistoryAsync(FrameworkElement owner)
    {
        if (!HistorySection.HasItems && !HistoryDataSection.HasItems) return;
        var request = new DesktopDialogRequest
        {
            Title = "清空历史记录",
            Message = domain == "film" ? "确认清空全部影视观看历史记录？" : "确认清空全部小说阅读历史记录？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        if (await DesktopModalHost.ShowDialogAsync(owner, request, CancellationToken.None) == DesktopDialogResult.Primary)
            await ClearHistoryDirectAsync(CancellationToken.None);
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
