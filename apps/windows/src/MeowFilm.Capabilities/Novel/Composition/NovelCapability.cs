using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using MeowFilm.Desktop;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class NovelCapability : IDesktopModule, IDesktopNavigationSource, IDesktopNavigationRetention, IDesktopImmersiveSource, IDesktopSidebarSource, IDebugModuleAdapterSource
{
    public const string HomeRoute = "novel-home", CategoriesRoute = "novel-categories", ShelfRoute = "novel-shelf", HistoryRoute = "novel-history", BookRoute = "novel-book", ReaderRoute = "novel-reader";
    private readonly NovelWork work = new();
    private CancellationTokenSource presentation = new();
    private readonly Dictionary<string, NovelBookSession> sessions = [];
    private readonly HashSet<Task> retiringSessions = [];
    private readonly NovelView view;
    private readonly NovelSettingsView settings;
    private readonly NovelDebugAdapter debugAdapter;
    internal NovelView DebugView => view;
    internal NovelSettingsView DebugSettings => settings;
    public IDebugModuleAdapter DebugAdapter => debugAdapter;
    internal FrameworkElement? SearchWebOwner { get; set; }
    internal CancellationToken PresentationToken => presentation.Token;
    private bool stopping, disposed, applyingReaderSettings, publishedImmersive;
    private Task? preparation, disposal;
    private string route = HomeRoute, publishedRoute = HomeRoute;
    public NovelCapability(IRuntimeClient runtime, IStorageDatabase store)
    {
        Sources = new NovelSourceService(runtime, store);
        Library = new NovelLibrary(store);
        Rules = new NovelRuleProgram(runtime);
        Search = new(Sources, Rules);
        Navigation = [new(HomeRoute, "小说", "\uE8F1", ShowInSidebar: false)
            { IconData = "M12,5 C9,3 6,3 3,4 L3,19 C6,18 9,18 12,20 C15,18 18,18 21,19 L21,4 C18,3 15,3 12,5 L12,20" },
            new(CategoriesRoute, "分类", "\uE8A9"),
            new(ShelfRoute, "书架", "\uE82D"),
            new(HistoryRoute, "阅读历史", "\uE81C"),
            new(BookRoute, "书籍详情", "\uE8F1", ShowInSidebar: false), new(ReaderRoute, "阅读", "\uE8F1", ShowInSidebar: false)];
        view = new(this); settings = new(this); debugAdapter = new(this);
        Library.Changed += LibraryChanged; Sources.Changed += SourcesChanged; Sources.ApplyFailed += SourceApplyFailed;
        Sources.InteractAsync = (request, token) =>
        {
            view.PauseReading();
            FrameworkElement owner = DesktopVisual.IsVisible(settings) ? settings : DesktopVisual.IsVisible(view) ? view :
                SearchWebOwner is { } searchOwner && DesktopVisual.IsVisible(searchOwner) ? searchOwner : throw new OperationCanceledException(token);
            return NovelWebInteraction.ShowAsync(owner, Rules, request, token);
        };
    }
    public string Id => "novel";
    public string Title => "小说";
    public string Version => "0.1.0";
    public ObservableCollection<NavigationItem> Navigation { get; }
    public FrameworkElement View => view;
    public FrameworkElement SidebarView => view.SidebarView;
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
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    public event EventHandler? SettingsRequested;
    internal void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void LibraryChanged(object? sender, EventArgs e)
    {
        if (e is not NovelLibraryChangeEventArgs change || change.Rules)
            settings.RefreshRulesIfVisible();
        Publish();
        RefreshReaderSettings();
    }
    internal void RefreshReaderSettings()
    {
        if (!stopping && !disposed && !applyingReaderSettings && DesktopVisual.IsVisible(view) && route == ReaderRoute &&
            Session is { NeedsReprocessing: true, Loading: false })
            _ = ApplyReaderSettingsAsync();
    }
    private async Task ApplyReaderSettingsAsync()
    {
        applyingReaderSettings = true;
        try
        {
            await work.Run(async ct =>
            {
                while (DesktopVisual.IsVisible(view) && route == ReaderRoute && Session is { NeedsReprocessing: true, Loading: false })
                    await ReprocessCurrentAsync(ct);
                return true;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!stopping && !disposed) Notify("小说设置已保存，当前正文暂未生效"); }
        finally { applyingReaderSettings = false; }
    }
    private void SourceApplyFailed(object? sender, string text)
    {
        if (stopping || disposed) return;
        if (view.DispatcherQueue.HasThreadAccess) Notify(text);
        else _ = view.DispatcherQueue.TryEnqueue(() => { if (!stopping && !disposed) Notify(text); });
    }
    private void SourcesChanged(object? sender, EventArgs e)
    {
        Search.SourcesChanged();
        if (e is NovelSourceProjectionChange change && Session is { } book && change.Retired.Contains(book.Book.InstallationId))
        { book.Cancel(); view.PauseReading(); }
        InvalidateDiscoverySource();
        settings.RefreshSourcesIfVisible(); Publish();
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        await Library.InitializeAsync(token);
        await Sources.EnsureReadyAsync(token);
    }
    public async Task StartSourcesAsync(CancellationToken token = default)
    {
        if (stopping || disposed) return;
        try { await Sources.StartAsync(token); }
        catch (OperationCanceledException) { }
        catch { /* Source owner reports runtime failures via ApplyFailed. */ }
    }
    public Task NavigateAsync(string next, CancellationToken token) => NavigateAsync(next, Session, token);
    private async Task NavigateAsync(string next, NovelBookSession? session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping) return;
        if (!Navigation.Any(n => n.Route == next)) throw new ArgumentException("Unknown novel route.", nameof(next));
        bool changed = route != next || !ReferenceEquals(Session, session);
        if (route == ReaderRoute && (changed || session?.NeedsReprocessing == true)) await view.SaveAndPauseAsync();
        if (next == ReaderRoute && session is not null)
        {
            try { await session.ReprocessAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (ReferenceEquals(session, Session) && route == ReaderRoute)
            { Notify("小说设置已保存，当前正文暂未生效"); }
        }
        token.ThrowIfCancellationRequested();
        if (changed) CancelPresentation();
        if (!ReferenceEquals(Session, session)) { Session?.Cancel(); Session = session; }
        if (next is not (HomeRoute or CategoriesRoute)) CancelDiscovery();
        route = next; Publish();
        if (route is HomeRoute or CategoriesRoute && discoveryNeedsReload)
            await LoadDiscoveryAsync(ExploreSource, ExploreKind, true, token);
        if (route == BookRoute && Session is { Chapters.Count: 0 }) await Session.LoadMetadataAsync(token);
    }
    public Task SearchAsync(string query, CancellationToken token) => Search.StartAsync(query, token);
    public async Task ReceiveBookAsync(NovelBook book, CancellationToken token)
    {
        await Library.InitializeAsync(token);
        string key = await Rules.IdentityAsync(book, token);
        token.ThrowIfCancellationRequested();
        var entry = Library.Find(book, key) ?? new(Guid.NewGuid().ToString("N"), key, book);
        ReceiveEntry(entry);
    }
    private void ReceiveEntry(NovelEntry entry)
    {
        if (!sessions.TryGetValue(entry.BookId, out var session))
        {
            session = new(Sources, Library, Rules, entry); sessions.Add(entry.BookId, session);
            session.Changed += SessionChanged; session.SelectChapterAsync = view.SelectChapterAsync;
        }
        if (!ReferenceEquals(Session, session)) { Session?.Cancel(); Session = session; }
    }
    internal Task OpenEntryAsync(NovelEntry entry, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ReceiveEntry(entry);
        Request(BookRoute);
        return Task.CompletedTask;
    }
    internal Task OpenEntryDirectAsync(NovelEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReceiveEntry(entry);
        return NavigateAsync(BookRoute, Session, token);
    }
    internal async Task OpenBookDirectAsync(NovelBook book, CancellationToken token)
    {
        await ReceiveBookAsync(book, token);
        await NavigateAsync(BookRoute, Session, token);
    }
    internal async Task StartReadingDirectAsync(CancellationToken token)
    {
        if (Session is not { } session) return;
        await session.ContinueAsync(token);
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(Session, session) && session.HasContent)
            await NavigateAsync(ReaderRoute, session, token);
    }
    internal Task OpenReaderDirectAsync(CancellationToken token) => NavigateAsync(ReaderRoute, Session, token);
    internal Task ContinueReadingAsync(NovelEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReceiveEntry(entry);
        return StartReadingAsync(token);
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
        else _ = NavigateStandaloneAsync(next);
    }
    private async Task NavigateStandaloneAsync(string next)
    {
        if (stopping || disposed) return;
        try { await work.Run(async ct => { await NavigateAsync(next, ct); return true; }); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Notify(error is StorageException ? "小说数据保存失败，原记录已保留" : "小说操作失败，请重试"); }
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
        {
            pair.Value.Changed -= SessionChanged;
            sessions.Remove(pair.Key);
            var retiring = pair.Value.RetireAsync();
            retiringSessions.Add(retiring);
            _ = ObserveRetire(retiring);
        }
        async Task ObserveRetire(Task task)
        {
            try { await task; }
            catch { }
            finally { retiringSessions.Remove(task); }
        }
    }
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
        catch (Exception error) { Notify(error is StorageException ? "小说数据保存失败，原记录已保留" : "小说操作失败，请重试"); }
    }
    public void Notify(string text)
    {
        Notice = text;
        if (DesktopVisual.IsVisible(settings)) settings.PostNotice(text); else view.PostNotice(text);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notice)));
    }
    private void Publish()
    {
        bool routeChanged = publishedRoute != route;
        bool immersiveChanged = publishedImmersive != IsImmersive;
        publishedRoute = route;
        publishedImmersive = IsImmersive;
        if (routeChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Route)));
        if (immersiveChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImmersive)));
        view.Present();
    }
    public string OperationErrorText(Exception error) => "小说内容暂时无法加载";
    public Task PrepareShutdownAsync() => preparation ??= PrepareCoreAsync();
    private async Task PrepareCoreAsync()
    {
        stopping = true; CancelPresentation(); CancelDiscovery(); settings.Cancel();
        Exception? failure = null;
        try { await view.SaveAndPauseAsync(); }
        catch (Exception error) { failure ??= error; }
        foreach (var session in sessions.Values) session.Cancel();
        try
        {
            await Task.WhenAll(work.PauseAsync(), Search.PrepareShutdownAsync(), Rules.PrepareShutdownAsync(),
                Task.WhenAll(sessions.Values.Select(s => s.PrepareShutdownAsync()).Concat(retiringSessions.ToArray())),
                settings.DrainAsync(), view.DrainSavesAsync(), view.DrainImagesAsync(), Sources.PrepareShutdownAsync());
        }
        catch (Exception error) { failure ??= error; }
        finally { await Library.PrepareShutdownAsync(); }
        if (failure is not null) throw failure;
    }

    public Task AbortAsync() => DisposeAsync().AsTask();
    public ValueTask DisposeAsync()
    {
        if (disposal is null || disposal.IsFaulted || disposal.IsCanceled) disposal = DisposeCoreAsync();
        return new(disposal);
    }
    private async Task DisposeCoreAsync()
    {
        try { await PrepareShutdownAsync(); }
        catch { /* Operation failures were reported by their owners; release still proceeds. */ }
        // A save error is not a release error. Conversely, a failed image lease
        // release must not authorize deleting a still-owned data root.
        await Task.WhenAll(view.DrainImagesAsync(), Sources.PrepareShutdownAsync(), Rules.DisposeAsync().AsTask());
        disposed = true; presentation.Cancel(); presentation.Dispose(); view.Dispose(); settings.Dispose();
        Sources.Changed -= SourcesChanged; Sources.ApplyFailed -= SourceApplyFailed; Library.Changed -= LibraryChanged;
    }
}
public sealed record NovelKind(string Title, string Url)
{
    public override string ToString() => Title;
}
