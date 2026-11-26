using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using MeowFilm.Desktop;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public enum NovelDiscoveryState { Idle, Loading, Ready, Empty, Failed, Cancelled, Unavailable }

internal readonly record struct NovelKind(string Title, string Url);

internal sealed record NovelKindOption(string Id, string Title, string Url);

internal sealed record NovelSectionDefinition(string Key, string Title, IReadOnlyList<NovelKindOption> Options);

internal sealed record NovelCardItem(NovelSourceService Sources, NovelBook Book) : ICatalogCardDisplay
{
    public string Id => Book.SourceIdentity;
    public string Title => Book.Name;
    public string Poster => "";
    public string Score => "";
    public bool HasScore => false;
    public string TextBadge => "";
    public bool HasTextBadge => false;
    public string Subtitle => "";
    public bool HasSubtitle => false;
    public bool ShowPlayMark => false;
    public NovelBook? NovelBook => Book;
    public NovelSourceService? NovelSources => Sources;

    public bool HasSameDisplay(ICatalogCardDisplay other) =>
        other is NovelCardItem card &&
        ReferenceEquals(Sources, card.Sources) &&
        Book.ContentEquals(card.Book);
}

public sealed class NovelHomeSection : CatalogHomeSection
{
    internal NovelHomeSection(NovelSectionDefinition definition)
        : base(definition.Key, definition.Title, defaultFailedText: "栏目加载失败", emptyText: "暂无书籍")
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    internal NovelSectionDefinition Definition { get; }
    internal NovelKindOption DefaultOption => Definition.Options[0];

    internal void Complete(NovelSourceService sources, IReadOnlyList<NovelBook> books)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(books);
        var cards = new List<ICatalogCardDisplay>(books.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            if (seen.Add(book.SourceIdentity))
                cards.Add(new NovelCardItem(sources, book));
        }
        CompleteItems(cards);
    }
}

public sealed class NovelDataSection : CatalogDataSection
{
    internal NovelDataSection(NovelSectionDefinition definition)
        : base(definition.Key, definition.Title, CreateFilterRows(definition), defaultFailedText: "书籍加载失败", emptyText: "暂无书籍")
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    internal NovelSectionDefinition Definition { get; }
    internal int Page { get; private set; }

    internal NovelKindOption SelectedOption
    {
        get
        {
            if (FilterRows.Count == 0) return Definition.Options[0];
            string selectedId = FilterRows[0].SelectedValue;
            return Definition.Options.FirstOrDefault(o => o.Id == selectedId) ?? Definition.Options[0];
        }
    }

    internal bool CanSeedFromHome =>
        Page == 0 && Items.Count == 0 && SelectedOption.Id == Definition.Options[0].Id;

    internal bool SelectFilter(CatalogFilterOption option)
    {
        if (!SelectFilterOption(option)) return false;
        Page = 0;
        return true;
    }

    internal void SeedFromHome(IEnumerable<ICatalogCardDisplay> items, bool hasAny)
    {
        SeedUniqueItems(items, markReady: true, more: hasAny);
        Page = 1;
    }

    internal void CompletePage(NovelSourceService sources, IReadOnlyList<NovelBook> books, int page, bool replace)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(books);
        var cards = new List<ICatalogCardDisplay>(books.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            if (seen.Add(book.SourceIdentity))
                cards.Add(new NovelCardItem(sources, book));
        }
        if (replace)
        {
            ReplaceAllItems(cards, more: cards.Count > 0);
        }
        else
        {
            AppendUniqueItems(cards, added => added > 0);
        }
        Page = page;
    }

    private static IReadOnlyList<CatalogFilterRow> CreateFilterRows(NovelSectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Options.Count <= 1) return [];
        return
        [
            new CatalogFilterRow(new CatalogFilterRowDefinition(
                "category",
                "分类",
                definition.Options[0].Id,
                definition.Options.Select(o => new CatalogFilterOptionDefinition(o.Title, o.Id)).ToArray()))
        ];
    }
}

public sealed partial class NovelCapability
{
    private const int InitialAdmittedSections = 4;
    private readonly SemaphoreSlim sectionConcurrency = new(3, 3);
    private readonly Dictionary<NovelHomeSection, CancellationTokenSource> sectionRequests = [];
    private readonly HashSet<Task> discoveryOperations = [];
    private CancellationTokenSource? homeRequest;
    private CancellationTokenSource? dataRequest;
    private NovelDataSection? dataRequestSection;
    private long discoveryGeneration;
    private (string InstallationId, long Revision)? loadedKindsSource;
    private bool discoveryInvalidated = true;

    public NovelSource? ExploreSource { get; private set; }
    public ObservableCollection<NovelHomeSection> Sections { get; } = [];
    public IReadOnlyList<NovelDataSection> DataSections { get; private set; } = [];
    public NovelDataSection ActiveNovelData { get; private set; } = new(new("", "", [new("", "", "")]));
    public NovelDiscoveryState HomeState { get; private set; } = NovelDiscoveryState.Idle;
    public string DiscoveryError { get; private set; } = "";

    internal bool IsDiscoveryBusy =>
        homeRequest is not null || sectionRequests.Count > 0 || dataRequest is not null;

    public bool IsHome => route == HomeRoute;
    public bool IsData => route == DataRoute;
    IEnumerable ICatalogBrowseController.BrowseSections => Sections;
    public bool HasSections => Sections.Count > 0;
    public bool ShowHomeLoading => !HasSections && HomeState is NovelDiscoveryState.Idle or NovelDiscoveryState.Loading;
    public bool ShowHomeStatus => !HasSections && !ShowHomeLoading;
    public string HomeStatusText => HomeState switch
    {
        NovelDiscoveryState.Failed => DiscoveryError.Length > 0 ? DiscoveryError : "发现加载失败",
        NovelDiscoveryState.Cancelled => "加载已取消",
        NovelDiscoveryState.Unavailable => ExploreSource is null ? "无可用发现书源" : "书源不可用",
        NovelDiscoveryState.Empty => "暂无可用分类",
        _ => ""
    };
    public bool CanRetryHome => !HasSections && HomeState is NovelDiscoveryState.Failed or NovelDiscoveryState.Cancelled;
    public bool CanImportHome => !HasSections && ExploreSource is null && HomeState == NovelDiscoveryState.Unavailable;
    CatalogDataSection ICatalogBrowseController.BrowseActiveData =>
        DataSections.Contains(ActiveNovelData) ? ActiveNovelData : CatalogDataSection.Empty;
    string ICatalogBrowseController.DataRefreshAutomationName => "刷新分类书籍";
    string ICatalogBrowseController.DataListAutomationName => "分类书籍列表";
    string ICatalogBrowseController.DataRetryAutomationName => "重试加载分类书籍";
    string ICatalogBrowseController.DataMoreAutomationName => "加载更多书籍";

    internal static bool CanExplore(NovelSource source) =>
        source.Enabled && source.EnabledExplore &&
        source.Actions.Contains("exploreKinds") && source.Actions.Contains("explore");

    private bool HasLoadedKindsFor(NovelSource? source) =>
        loadedKindsSource is { } loaded && source is not null &&
        loaded.InstallationId == source.InstallationId && loaded.Revision == source.Revision &&
        !discoveryInvalidated;

    private bool NeedsHomeKindsLoad =>
        discoveryInvalidated ||
        (!HasLoadedKindsFor(ExploreSource) && HomeState is not (NovelDiscoveryState.Unavailable or NovelDiscoveryState.Failed));

    private void InvalidateDiscoverySource()
    {
        if (ExploreSource is not { } selected)
        {
            var first = Sources.Sources.FirstOrDefault(CanExplore);
            if (first is null)
            {
                discoveryInvalidated = true;
                PublishBrowseProperties();
                return;
            }
            ExploreSource = first;
            discoveryInvalidated = true;
            DiscoveryError = "";
            HomeState = NovelDiscoveryState.Idle;
            PublishBrowseProperties();
            if (DesktopVisual.IsVisible(view) && route == HomeRoute)
                RunUi(ct => EnsureHomeLoadedAsync(ct));
            return;
        }
        var current = Sources.Sources.FirstOrDefault(s => s.InstallationId == selected.InstallationId);
        if (current is not null && current.Revision == selected.Revision &&
            (CanExplore(current) || !CanExplore(selected)))
        {
            ExploreSource = current;
            return;
        }
        discoveryGeneration++;
        RetireDiscoveryRequests();
        // Keep the selected identity, including a removed source, until the user chooses another.
        ExploreSource = current ?? selected;
        ClearDiscoverySections();
        discoveryInvalidated = true;
        DiscoveryError = "";
        HomeState = current is not null && CanExplore(current) ? NovelDiscoveryState.Idle : NovelDiscoveryState.Unavailable;
        PublishBrowseProperties();
        if (DesktopVisual.IsVisible(view) && route == HomeRoute && HomeState == NovelDiscoveryState.Idle)
            RunUi(ct => EnsureHomeLoadedAsync(ct));
    }

    internal void SelectExploreSource(NovelSource source, bool resetScroll = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ExploreSource?.InstallationId == source.InstallationId && ExploreSource?.Revision == source.Revision)
            return;
        discoveryGeneration++;
        RetireDiscoveryRequests();
        ExploreSource = source;
        ClearDiscoverySections();
        discoveryInvalidated = true;
        DiscoveryError = "";
        HomeState = CanExplore(source) ? NovelDiscoveryState.Idle : NovelDiscoveryState.Unavailable;
        if (resetScroll) view.ResetBrowseScroll();
        PublishBrowseProperties();
    }

    internal async Task SwitchExploreSourceAsync(NovelSource source, CancellationToken token)
    {
        SelectExploreSource(source);
        if (route == HomeRoute)
            await EnsureHomeLoadedAsync(token, forceReloadKinds: true);
    }

    internal async Task EnsureHomeLoadedAsync(CancellationToken token, bool forceReloadKinds = false)
    {
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;

        if (forceReloadKinds || NeedsHomeKindsLoad)
        {
            await LoadHomeKindsAsync(token);
            token.ThrowIfCancellationRequested();
        }

        if (!stopping && !disposed && IsHome && Sections.Count > 0 && ExploreSource is { } active && CanExplore(active))
        {
            for (int i = 0; i < Math.Min(InitialAdmittedSections, Sections.Count); i++)
                Sections[i].MarkAdmitted();

            var targets = Sections
                .Where(s => s.Admitted && s.State != CatalogSectionState.Ready && !sectionRequests.ContainsKey(s))
                .ToArray();
            if (targets.Length > 0)
            {
                long generation = discoveryGeneration;
                await Task.WhenAll(targets.Select(s => StartNovelSectionLoadAsync(s, generation, token)));
                token.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task LoadHomeKindsAsync(CancellationToken token)
    {
        long generation = ++discoveryGeneration;
        RetireDiscoveryRequests();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, presentation.Token);
        homeRequest = request;
        DiscoveryError = "";
        HomeState = NovelDiscoveryState.Loading;
        PublishBrowseProperties();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discoveryOperations.Add(completion.Task);
        try
        {
            await Sources.EnsureExecutionReadyAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (generation != discoveryGeneration) return;

            var target = ExploreSource ?? Sources.Sources.FirstOrDefault(CanExplore);
            var current = target is null
                ? null
                : Sources.Sources.FirstOrDefault(s => s.InstallationId == target.InstallationId);
            bool firstSelection = ExploreSource is null && current is not null;
            ExploreSource = current ?? target;
            if (firstSelection) view.Present();

            if (current is null || !CanExplore(current))
            {
                ClearDiscoverySections();
                discoveryInvalidated = false;
                HomeState = NovelDiscoveryState.Unavailable;
                PublishBrowseProperties();
                return;
            }

            var result = await Sources.InvokeAsync(current, "exploreKinds", new { }, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (generation != discoveryGeneration) return;

            var rawKinds = ReadDiscoveryKinds(result.GetProperty("kinds"));
            var definitions = BuildSectionDefinitions(rawKinds);
            ClearDiscoverySections();
            foreach (var def in definitions)
                Sections.Add(new NovelHomeSection(def));
            DataSections = definitions.Select(def => new NovelDataSection(def)).ToArray();
            if (DataSections.Count > 0)
                ActiveNovelData = DataSections[0];

            loadedKindsSource = (current.InstallationId, current.Revision);
            discoveryInvalidated = false;
            HomeState = Sections.Count == 0 ? NovelDiscoveryState.Empty : NovelDiscoveryState.Ready;
            PublishBrowseProperties();
        }
        catch (OperationCanceledException)
        {
            if (generation == discoveryGeneration && ReferenceEquals(homeRequest, request))
            {
                HomeState = NovelDiscoveryState.Cancelled;
                PublishBrowseProperties();
            }
            throw;
        }
        catch (Exception error)
        {
            if (generation == discoveryGeneration && ReferenceEquals(homeRequest, request))
            {
                DiscoveryError = DiscoveryErrorText(error, "发现加载失败");
                HomeState = NovelDiscoveryState.Failed;
                PublishBrowseProperties();
                Notify(DiscoveryError == "书源需要网页验证" ? "书源需要网页验证" : "发现加载失败，请重试");
            }
        }
        finally
        {
            if (ReferenceEquals(homeRequest, request))
                homeRequest = null;
            discoveryOperations.Remove(completion.Task);
            completion.TrySetResult();
        }
    }

    private Task StartNovelSectionLoadAsync(
        NovelHomeSection section,
        long generation,
        CancellationToken cancellationToken)
    {
        if (stopping || disposed || !IsHome || ExploreSource is not { } source || !CanExplore(source))
            return Task.CompletedTask;
        if (sectionRequests.ContainsKey(section))
            return Task.CompletedTask;

        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, presentation.Token);
        sectionRequests.Add(section, request);
        section.Begin();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        discoveryOperations.Add(operation);
        _ = RunAsync();
        return operation;

        async Task RunAsync()
        {
            bool acquired = false;
            try
            {
                await Task.Yield();
                await sectionConcurrency.WaitAsync(request.Token);
                acquired = true;
                request.Token.ThrowIfCancellationRequested();
                var page = await Sources.InvokeAsync(source, "explore",
                    new { url = section.DefaultOption.Url, page = 1 }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                var books = NovelBook.ReadList(page.GetProperty("books"), source, "INVALID_DISCOVERY_BOOKS");
                request.Token.ThrowIfCancellationRequested();
                if (CanPublishSection(section, source, request, generation))
                    section.Complete(Sources, books);
            }
            catch (OperationCanceledException)
            {
                if (CanPublishSection(section, source, request, generation))
                    section.Cancel();
            }
            catch (Exception error)
            {
                if (CanPublishSection(section, source, request, generation))
                {
                    string text = DiscoveryErrorText(error, "栏目加载失败");
                    section.Fail(text);
                    Notify(text);
                }
            }
            finally
            {
                if (acquired) sectionConcurrency.Release();
                if (sectionRequests.TryGetValue(section, out var current) && ReferenceEquals(current, request))
                    sectionRequests.Remove(section);
                discoveryOperations.Remove(operation);
                request.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private bool CanPublishSection(
        NovelHomeSection section,
        NovelSource source,
        CancellationTokenSource request,
        long generation) =>
        !stopping && !disposed && IsHome && generation == discoveryGeneration &&
        ExploreSource?.InstallationId == source.InstallationId && ExploreSource?.Revision == source.Revision &&
        sectionRequests.TryGetValue(section, out var current) && ReferenceEquals(current, request);

    internal async Task EnsureDataLoadedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;

        if (NeedsHomeKindsLoad)
        {
            await LoadHomeKindsAsync(token);
            token.ThrowIfCancellationRequested();
        }
        if (!DataSections.Contains(ActiveNovelData))
        {
            if (DataSections.Count == 0) return;
            ActiveNovelData = DataSections[0];
            PublishBrowseProperties();
        }

        if (ActiveNovelData.CanSeedFromHome)
        {
            var home = Sections.FirstOrDefault(s => s.Key == ActiveNovelData.Key);
            if (home is { State: CatalogSectionState.Ready })
            {
                ActiveNovelData.SeedFromHome(home.Items, home.HasItems);
                return;
            }
        }

        if (ActiveNovelData.State is CatalogSectionState.Pending or CatalogSectionState.Cancelled && dataRequest is null)
        {
            await StartNovelDataLoadAsync(
                ActiveNovelData,
                discoveryGeneration,
                token,
                replace: ActiveNovelData.Items.Count == 0 || ActiveNovelData.ReloadPending);
            token.ThrowIfCancellationRequested();
        }
    }

    private Task StartNovelDataLoadAsync(
        NovelDataSection section,
        long generation,
        CancellationToken cancellationToken,
        bool replace)
    {
        if (stopping || disposed || !IsData || ExploreSource is not { } source || !CanExplore(source))
        {
            if (IsData) section.Fail(ExploreSource is null ? "无可用发现书源" : "书源不可用");
            return Task.CompletedTask;
        }

        int nextPage = replace || section.Page == 0 ? 1 : section.Page + 1;
        string url = section.SelectedOption.Url;
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, presentation.Token);
        dataRequest = request;
        dataRequestSection = section;
        section.Begin(replace);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        discoveryOperations.Add(operation);
        _ = RunAsync();
        return operation;

        async Task RunAsync()
        {
            try
            {
                await Task.Yield();
                request.Token.ThrowIfCancellationRequested();
                await Sources.EnsureExecutionReadyAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                var page = await Sources.InvokeAsync(source, "explore", new { url, page = nextPage }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                var books = NovelBook.ReadList(page.GetProperty("books"), source, "INVALID_DISCOVERY_BOOKS");
                request.Token.ThrowIfCancellationRequested();
                if (CanPublishData(section, source, request, generation))
                    section.CompletePage(Sources, books, nextPage, replace);
            }
            catch (OperationCanceledException)
            {
                if (CanPublishData(section, source, request, generation))
                    section.Cancel();
            }
            catch (Exception error)
            {
                if (CanPublishData(section, source, request, generation))
                {
                    string text = DiscoveryErrorText(error, "书籍加载失败");
                    section.Fail(text);
                    Notify(text);
                }
            }
            finally
            {
                if (ReferenceEquals(dataRequest, request))
                {
                    dataRequest = null;
                    dataRequestSection = null;
                }
                discoveryOperations.Remove(operation);
                request.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private bool CanPublishData(
        NovelDataSection section,
        NovelSource source,
        CancellationTokenSource request,
        long generation) =>
        !stopping && !disposed && IsData && ReferenceEquals(ActiveNovelData, section) &&
        generation == discoveryGeneration &&
        ExploreSource?.InstallationId == source.InstallationId && ExploreSource?.Revision == source.Revision &&
        ReferenceEquals(dataRequest, request) && ReferenceEquals(dataRequestSection, section);

    internal async Task SwitchToDataSectionAsync(NovelDataSection selected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!ReferenceEquals(ActiveNovelData, selected))
        {
            RetireDataRequest();
            ActiveNovelData = selected;
            view.ClearDataScrollMemory();
            PublishBrowseProperties();
        }
        await NavigateAsync(DataRoute, cancellationToken);
    }

    internal Task OpenNovelDataAsync(NovelHomeSection? section = null)
    {
        if (stopping || disposed || !IsHome || Sections.Count == 0) return Task.CompletedTask;
        section ??= Sections[0];
        var selected = DataSections.FirstOrDefault(d => d.Key == section.Key);
        if (selected is null) return Task.CompletedTask;
        if (!ReferenceEquals(ActiveNovelData, selected))
        {
            RetireDataRequest();
            ActiveNovelData = selected;
            view.ClearDataScrollMemory();
            PublishBrowseProperties();
        }
        Request(DataRoute);
        return Task.CompletedTask;
    }

    internal Task RefreshNovelSectionAsync(NovelHomeSection section) =>
        !stopping && !disposed && IsHome && Sections.Contains(section) && !sectionRequests.ContainsKey(section)
            ? StartNovelSectionLoadAsync(section, discoveryGeneration, presentation.Token)
            : Task.CompletedTask;

    internal async Task RetryNovelSectionAsync(NovelHomeSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (stopping || disposed || !IsHome || !Sections.Contains(section) ||
            sectionRequests.ContainsKey(section) || !section.CanRetry)
            return;
        await StartNovelSectionLoadAsync(section, discoveryGeneration, presentation.Token);
    }

    internal Task RefreshNovelHomeAsync(CancellationToken token) =>
        EnsureHomeLoadedAsync(token, forceReloadKinds: true);

    internal Task RefreshNovelDataAsync() =>
        !stopping && !disposed && IsData && dataRequest is null && DataSections.Contains(ActiveNovelData)
            ? StartNovelDataLoadAsync(ActiveNovelData, discoveryGeneration, presentation.Token, replace: true)
            : Task.CompletedTask;

    internal async Task RetryNovelDataAsync()
    {
        var section = ActiveNovelData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanRetry || !DataSections.Contains(section))
            return;
        await StartNovelDataLoadAsync(section, discoveryGeneration, presentation.Token, replace: section.Items.Count == 0 || section.ReloadPending);
    }

    internal async Task LoadMoreNovelDataAsync()
    {
        var section = ActiveNovelData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanPage || !DataSections.Contains(section))
            return;
        await StartNovelDataLoadAsync(section, discoveryGeneration, presentation.Token, replace: section.ReloadPending);
    }

    internal async Task SelectNovelDataFilterAsync(CatalogFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var section = ActiveNovelData;
        if (stopping || disposed || !IsData || !DataSections.Contains(section)) return;
        if (!section.Contains(option))
            throw new ArgumentException("Unknown novel filter option.", nameof(option));
        if (option.IsSelected) return;

        long generation = ++discoveryGeneration;
        RetireDataRequest();
        if (!section.SelectFilter(option)) return;
        await StartNovelDataLoadAsync(section, generation, presentation.Token, replace: true);
    }

    void ICatalogBrowseController.OnSectionAdmitted(CatalogHomeSection section)
    {
        if (stopping || disposed || !IsHome || section is not NovelHomeSection novelSection) return;
        if (!Sections.Contains(novelSection)) return;
        novelSection.MarkAdmitted();
        if (novelSection.State == CatalogSectionState.Pending && !sectionRequests.ContainsKey(novelSection))
            _ = StartNovelSectionLoadAsync(novelSection, discoveryGeneration, presentation.Token);
    }

    Task ICatalogBrowseController.OpenSectionAsync(CatalogHomeSection section) =>
        OpenNovelDataAsync(section as NovelHomeSection);

    Task ICatalogBrowseController.RefreshSectionAsync(CatalogHomeSection section) =>
        section is NovelHomeSection s ? RefreshNovelSectionAsync(s) : Task.CompletedTask;

    Task ICatalogBrowseController.RetrySectionAsync(CatalogHomeSection section) =>
        section is NovelHomeSection s ? RetryNovelSectionAsync(s) : Task.CompletedTask;

    Task ICatalogBrowseController.RetryHomeAsync() =>
        !stopping && !disposed && IsHome ? EnsureHomeLoadedAsync(presentation.Token, forceReloadKinds: true) : Task.CompletedTask;

    Task ICatalogBrowseController.ImportHomeAsync(FrameworkElement owner) =>
        !stopping && !disposed ? ImportSourcesAsync(owner, presentation.Token) : Task.CompletedTask;

    Task ICatalogBrowseController.ActivateCardAsync(ICatalogCardDisplay card) =>
        card is NovelCardItem item ? OpenBookAsync(item.Book, presentation.Token) : Task.CompletedTask;

    Task ICatalogBrowseController.RefreshDataAsync() => RefreshNovelDataAsync();
    Task ICatalogBrowseController.RetryDataAsync() => RetryNovelDataAsync();
    Task ICatalogBrowseController.LoadMoreDataAsync() => LoadMoreNovelDataAsync();
    Task ICatalogBrowseController.SelectDataFilterAsync(CatalogFilterOption option) => SelectNovelDataFilterAsync(option);
    void ICatalogBrowseController.ReportBrowseError(string message) => Notify(message);

    public void CancelDiscovery()
    {
        discoveryGeneration++;
        RetireDiscoveryRequests();
        PublishBrowseProperties();
    }

    internal void ResumeDiscoveryIfVisible()
    {
        if (stopping || disposed || !DesktopVisual.IsVisible(view)) return;
        if (IsHome)
        {
            bool needsHome = NeedsHomeKindsLoad ||
                Sections.Any(s => s.Admitted && s.State is CatalogSectionState.Pending or CatalogSectionState.Cancelled);
            if (needsHome) RunUi(ct => EnsureHomeLoadedAsync(ct));
        }
        else if (IsData && DataSections.Contains(ActiveNovelData) &&
                 ActiveNovelData.State is CatalogSectionState.Pending or CatalogSectionState.Cancelled &&
                 dataRequest is null)
        {
            RunUi(EnsureDataLoadedAsync);
        }
    }

    private void ClearDiscoverySections()
    {
        loadedKindsSource = null;
        Sections.Clear();
        DataSections = [];
    }

    private void RetireDiscoveryRequests()
    {
        var retiredHome = homeRequest;
        homeRequest = null;
        if (retiredHome is not null)
        {
            if (HomeState == NovelDiscoveryState.Loading)
                HomeState = NovelDiscoveryState.Cancelled;
            try { retiredHome.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        RetireSectionRequests();
        RetireDataRequest();
    }

    private void RetireSectionRequests()
    {
        var retiredSections = sectionRequests.Values.ToArray();
        foreach (var sec in sectionRequests.Keys) sec.Cancel();
        sectionRequests.Clear();
        foreach (var req in retiredSections)
        {
            try { req.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.Novel, "discovery", "section.cancel.failed", error: error);
            }
        }
    }

    private void RetireDataRequest()
    {
        var retiredData = dataRequest;
        if (retiredData is not null) dataRequestSection?.Cancel();
        dataRequest = null;
        dataRequestSection = null;
        if (retiredData is not null)
        {
            try { retiredData.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.Novel, "discovery", "data.cancel.failed", error: error);
            }
        }
    }

    private Task DrainDiscoveryAsync() => Task.WhenAll(discoveryOperations.ToArray());

    private void PublishBrowseProperties()
    {
        RaisePropertyChanged(nameof(IsHome));
        RaisePropertyChanged(nameof(IsData));
        RaisePropertyChanged(nameof(ICatalogBrowseController.BrowseSections));
        RaisePropertyChanged(nameof(HasSections));
        RaisePropertyChanged(nameof(ShowHomeLoading));
        RaisePropertyChanged(nameof(ShowHomeStatus));
        RaisePropertyChanged(nameof(HomeStatusText));
        RaisePropertyChanged(nameof(CanRetryHome));
        RaisePropertyChanged(nameof(CanImportHome));
        RaisePropertyChanged(nameof(ICatalogBrowseController.BrowseActiveData));
    }

    private static string DiscoveryErrorText(Exception error, string fallback) =>
        error is BridgeException { Code: "WEB_INTERACTION_REQUIRED" } ? "书源需要网页验证" : fallback;

    private static NovelKind[] ReadDiscoveryKinds(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array) throw new JsonException("INVALID_DISCOVERY_KINDS");
        var result = new List<NovelKind>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) continue;
            string title = NovelJson.Text(value, "title").Trim();
            string url = NovelJson.Text(value, "url").Trim();
            if (title.Length == 0 && url.Length == 0) continue;
            result.Add(new(title.Length == 0 ? "未命名" : title, url));
        }
        if (values.GetArrayLength() > 0 && result.Count == 0) throw new JsonException("INVALID_DISCOVERY_KINDS");
        return result.ToArray();
    }

    internal static IReadOnlyList<NovelSectionDefinition> BuildSectionDefinitions(IReadOnlyList<NovelKind> kinds)
    {
        var sections = new List<(string Key, string Title, List<NovelKindOption> Options)>();
        (string Key, string Title)? heading = null;
        for (int index = 0; index < kinds.Count; index++)
        {
            var kind = kinds[index];
            string title = kind.Title.Trim();
            string url = kind.Url.Trim();
            if (url.Length == 0)
            {
                if (title.Length > 0)
                    heading = ($"group-{index}", title);
                continue;
            }
            var option = new NovelKindOption($"item-{index}", title.Length == 0 ? "未命名" : title, url);
            if (heading is { } group)
            {
                if (sections.Count == 0 || sections[^1].Key != group.Key)
                    sections.Add((group.Key, group.Title, []));
                sections[^1].Options.Add(option);
            }
            else
            {
                sections.Add((option.Id, option.Title, [option]));
            }
        }
        return sections.Select(s => new NovelSectionDefinition(s.Key, s.Title, s.Options)).ToArray();
    }
}
