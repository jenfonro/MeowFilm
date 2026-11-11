using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MeowFilm.Desktop;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed class NovelCapability : IDesktopModule, IDesktopNavigationSource, IDesktopNavigationRetention, IDesktopImmersiveSource
{
    public const string HomeRoute = "novel-home", ShelfRoute = "novel-shelf", BookRoute = "novel-book", ReaderRoute = "novel-reader";
    private readonly NovelWork work = new();
    private CancellationTokenSource presentation = new();
    private readonly Dictionary<string, NovelBookSession> sessions = [];
    private readonly NovelView view;
    private readonly NovelSettingsView settings;
    internal FrameworkElement? SearchWebOwner { get; set; }
    internal CancellationToken PresentationToken => presentation.Token;
    private bool stopping, disposed;
    private Task? preparation, disposal;
    private string route = HomeRoute;
    private CancellationTokenSource? discovery;
    private long discoveryGeneration;
    private int explorePage;
    public NovelCapability(IRuntimeClient runtime, IDocumentStore store) : this(new NovelSourceService(runtime),
        new NovelLibrary(store), new NovelRuleProgram(runtime)) { }
    public NovelCapability(NovelSourceService sources, NovelLibrary library, NovelRuleProgram rules)
    {
        Sources = sources; Library = library; Rules = rules; Search = new(sources, rules);
        Navigation = [new(HomeRoute, "小说", "\uE8F1"), new(ShelfRoute, "书架", "\uE82D"),
            new(BookRoute, "书籍详情", "\uE8F1", ShowInSidebar: false), new(ReaderRoute, "阅读", "\uE8F1", ShowInSidebar: false)];
        view = new(this); settings = new(this);
        Library.Changed += LibraryChanged; Sources.Changed += SourcesChanged;
        Sources.InteractAsync = (request, token) =>
        {
            view.PauseReading();
            FrameworkElement owner = settings.IsVisible ? settings : view.IsVisible ? view :
                SearchWebOwner is { IsVisible: true } searchOwner ? searchOwner : throw new OperationCanceledException(token);
            return NovelWebInteraction.ShowAsync(owner, Rules, request, token);
        };
    }
    public string Id => "novel";
    public string Title => "小说";
    public string Version => "0.1.0";
    public ObservableCollection<NavigationItem> Navigation { get; }
    public FrameworkElement View => view;
    public FrameworkElement SettingsView => settings;
    public FrameworkElement? PlayerView => null;
    public bool IsImmersive => route == ReaderRoute;
    public string Route => route;
    public string Notice { get; private set; } = "";
    public NovelSourceService Sources { get; }
    public NovelLibrary Library { get; }
    public NovelRuleProgram Rules { get; }
    public NovelSearch Search { get; }
    public NovelBookSession? Session { get; private set; }
    public NovelSource? ExploreSource { get; private set; }
    public NovelKind? ExploreKind { get; private set; }
    public IReadOnlyList<NovelKind> Kinds { get; private set; } = [];
    public ObservableCollection<NovelBook> DiscoverBooks { get; } = [];
    public bool MoreDiscovery { get; private set; }
    public bool LoadingDiscovery { get; private set; }
    public string DiscoveryError { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    private void LibraryChanged(object? sender, EventArgs e) => Publish();
    private void SourcesChanged(object? sender, EventArgs e)
    {
        Search.SourcesChanged();
        if (e is not NovelSourceProjectionChange { Initial: true } && Session is { } book &&
            !Sources.Sources.Any(s => s.InstallationId == book.Book.InstallationId && s.Enabled && s.Revision == book.Book.Revision))
        { book.Cancel(); view.PauseReading(); }
        if (ExploreSource is { } source && !Sources.IsCurrent(source))
        { CancelDiscovery(); ExploreSource = null; ExploreKind = null; Kinds = []; DiscoverBooks.Clear(); MoreDiscovery = false; }
        settings.Refresh(); Publish();
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        await Library.InitializeAsync(token);
    }
    public Task NavigateAsync(string next, CancellationToken token) => NavigateAsync(next, Session, token);
    private async Task NavigateAsync(string next, NovelBookSession? session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping) return;
        if (!Navigation.Any(n => n.Route == next)) throw new ArgumentException("Unknown novel route.", nameof(next));
        bool changed = route != next || !ReferenceEquals(Session, session);
        if (route == ReaderRoute && (changed || session?.NeedsReprocessing == true)) await view.SaveAndPauseAsync();
        if (next == ReaderRoute && session is not null) await session.ReprocessAsync(token);
        token.ThrowIfCancellationRequested();
        if (changed) CancelPresentation();
        if (!ReferenceEquals(Session, session)) { Session?.Cancel(); Session = session; }
        if (next != HomeRoute) CancelDiscovery();
        route = next; Publish();
        if (route == HomeRoute && ExploreSource is null) await LoadDiscoveryAsync(null, null, true, token);
        if (route == BookRoute && Session is { Chapters.Count: 0 }) await Session.LoadMetadataAsync(token);
        Publish();
    }
    public Task SearchAsync(string query, CancellationToken token) => Search.StartAsync(query, token);
    public async Task ReceiveBookAsync(NovelBook book, CancellationToken token)
    {
        await Library.InitializeAsync(token);
        string key = await Rules.IdentityAsync(book, token);
        token.ThrowIfCancellationRequested();
        var entry = Library.Find(book, key) ?? new(Guid.NewGuid().ToString("N"), key, book);
        if (!sessions.TryGetValue(entry.BookId, out var session))
        {
            session = new(Sources, Library, Rules, entry); sessions.Add(entry.BookId, session);
            session.Changed += SessionChanged; session.SelectChapterAsync = view.SelectChapterAsync;
        }
        if (!ReferenceEquals(Session, session)) { Session?.Cancel(); Session = session; }
    }
    public async Task OpenBookAsync(NovelBook book, CancellationToken token = default)
    {
        await ReceiveBookAsync(book, token);
        Request(BookRoute);
    }
    public async Task StartReadingAsync(CancellationToken token = default)
    {
        if (Session is not { } session) return;
        await session.ContinueAsync(token);
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(Session, session) && session.HasContent) Request(ReaderRoute);
    }
    internal async Task ReprocessCurrentAsync(CancellationToken token)
    {
        if (Session is not { NeedsReprocessing: true } session) return;
        await view.SaveAndPauseAsync();
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(Session, session)) await session.ReprocessAsync(token);
    }
    private void SessionChanged(object? sender, EventArgs e) { if (ReferenceEquals(sender, Session)) Publish(); }
    internal void Request(string next)
    {
        if (NavigationRequested is { } handler) handler(this, new(next));
        else RunUi(ct => NavigateAsync(next, ct));
    }
    public string CaptureNavigationLocation() => (route is BookRoute or ReaderRoute) && Session is not null ? route + ":" + Session.Entry.BookId : route;
    public async Task RestoreNavigationAsync(string location, CancellationToken token)
    {
        int separator = location.IndexOf(':');
        if (separator >= 0)
        {
            if (!sessions.TryGetValue(location[(separator + 1)..], out var session)) throw new ArgumentException("Retired book location.");
            await NavigateAsync(location[..separator], session, token);
        }
        else await NavigateAsync(location, token);
    }
    public void RetainNavigationLocations(IReadOnlySet<string> locations)
    {
        foreach (var pair in sessions.Where(p => !ReferenceEquals(p.Value, Session) &&
            !locations.Contains(BookRoute + ":" + p.Key) && !locations.Contains(ReaderRoute + ":" + p.Key)).ToArray())
        { pair.Value.Cancel(); pair.Value.Changed -= SessionChanged; sessions.Remove(pair.Key); }
    }
    public Task LoadDiscoveryAsync(NovelSource? source, NovelKind? kind, bool reset, CancellationToken token = default)
    {
        CancelDiscovery(); discovery?.Dispose(); discovery = CancellationTokenSource.CreateLinkedTokenSource(token);
        long id = discoveryGeneration; var ct = discovery.Token;
        return work.Run(async cancellation =>
        {
            LoadingDiscovery = true; DiscoveryError = ""; Publish();
            try
            {
                if (Sources.Sources.Count == 0) await Sources.RefreshAsync(cancellation);
                source ??= Sources.Sources.FirstOrDefault(s => s.Enabled && s.EnabledExplore && s.Actions.Contains("explore"));
                if (source is null) { ExploreSource = null; ExploreKind = null; Kinds = []; DiscoverBooks.Clear(); MoreDiscovery = false; return true; }
                if (reset || ExploreSource?.InstallationId != source.InstallationId)
                {
                    var result = await Sources.InvokeAsync(source, "exploreKinds", new { }, cancellation);
                    cancellation.ThrowIfCancellationRequested(); if (id != discoveryGeneration) return false;
                    Kinds = NovelJson.Decode<NovelKind[]>(result.GetProperty("kinds"));
                    ExploreSource = source; ExploreKind = kind ?? Kinds.FirstOrDefault(k => k.Url.Length > 0);
                    explorePage = 0; DiscoverBooks.Clear();
                }
                else if (kind is not null && kind != ExploreKind) { ExploreKind = kind; explorePage = 0; DiscoverBooks.Clear(); }
                if (ExploreKind is null || ExploreKind.Url.Length == 0) { MoreDiscovery = false; return true; }
                var page = await Sources.InvokeAsync(source, "explore", new { url = ExploreKind.Url, page = explorePage + 1 }, cancellation);
                cancellation.ThrowIfCancellationRequested(); if (id != discoveryGeneration) return false;
                int before = DiscoverBooks.Count;
                foreach (var item in page.GetProperty("books").EnumerateArray())
                {
                    var book = NovelBook.From(item, source);
                    if (!DiscoverBooks.Any(b => b.SourceIdentity == book.SourceIdentity)) DiscoverBooks.Add(book);
                }
                explorePage++; MoreDiscovery = DiscoverBooks.Count > before; return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { if (id == discoveryGeneration) DiscoveryError = "发现加载失败"; throw; }
            finally { if (id == discoveryGeneration) { LoadingDiscovery = false; Publish(); } }
        }, ct);
    }
    public void CancelDiscovery() { ++discoveryGeneration; discovery?.Cancel(); LoadingDiscovery = false; Publish(); }
    internal void CancelPresentation()
    {
        if (disposed) return;
        var old = presentation; presentation = new(); old.Cancel(); old.Dispose();
        view.PauseReading();
    }
    internal Task ImportSourcesAsync(FrameworkElement owner, CancellationToken token) => settings.ImportFromAsync(owner, token);
    public void RunUi(Func<Task> action) => RunUi(_ => action());
    public void RunUi(Func<CancellationToken, Task> action) => _ = RunVisibleAsync(action);
    private async Task RunVisibleAsync(Func<CancellationToken, Task> action)
    {
        if (stopping || disposed) return;
        try { await work.Run(async ct => { await action(ct); return true; }, presentation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Notify(error is DocumentStoreException ? "小说数据保存失败，原记录已保留" : "小说操作失败，请重试"); }
    }
    public void Notify(string text) { Notice = text; Publish(); }
    private void Publish()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); view.Present();
    }
    public string OperationErrorText(Exception error) => "小说内容暂时无法加载";
    public Task PrepareShutdownAsync() => preparation ??= PrepareCoreAsync();
    private async Task PrepareCoreAsync()
    {
        stopping = true; CancelPresentation(); CancelDiscovery(); settings.Cancel();
        Exception? failure = null;
        try { await view.SaveAndPauseAsync(); } catch (Exception error) { failure = error; }
        foreach (var session in sessions.Values) session.Cancel();
        try
        {
            await Task.WhenAll(work.PauseAsync(), Search.PrepareShutdownAsync(), Rules.PrepareShutdownAsync(),
                Task.WhenAll(sessions.Values.Select(s => s.PrepareShutdownAsync())), settings.DrainAsync(), view.DrainSavesAsync());
            try { await view.DrainImagesAsync(); } catch (Exception error) { failure ??= error; }
            await Sources.PrepareShutdownAsync();
        }
        catch (Exception error) { failure ??= error; }
        finally { await Library.PrepareShutdownAsync(); }
        if (failure is not null) throw failure;
    }
    public async Task CancelShutdownPreparationAsync()
    {
        if (preparation is null) return;
        try { await preparation; } catch { }
        preparation = null; stopping = false; work.Resume(); Search.Resume(); Sources.Resume(); Rules.Resume(); Library.Resume();
        foreach (var session in sessions.Values) session.Resume(); settings.Resume(); view.Resume(); Publish();
    }
    public Task AbortAsync() => DisposeAsync().AsTask();
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        try { await PrepareShutdownAsync(); }
        finally
        {
            disposed = true; presentation.Cancel(); presentation.Dispose(); discovery?.Dispose(); view.Dispose(); settings.Dispose();
            Sources.Changed -= SourcesChanged; Library.Changed -= LibraryChanged;
            await Rules.DisposeAsync();
        }
    }
}
public sealed record NovelKind(string Title, string Url)
{
    public override string ToString() => Title;
}
