using System.ComponentModel;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Desktop;

internal sealed class ShellModuleCoordinator
{
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] ModuleAccentKeys =
        ["MeowFilm.Brush.Accent", "MeowFilm.Brush.AccentHover", "MeowFilm.Brush.OnAccent", "MeowFilm.Brush.AccentSoft"];

    private readonly ShellWindow _shell;
    private readonly List<ModuleSession> _sessions;
    private readonly HashSet<ShellRequest> _requests = [];
    private readonly List<(IDesktopNavigationSource Source, EventHandler<DesktopNavigationRequestEventArgs> Handler)> _navigationSubscriptions = [];
    private readonly List<(IDesktopSidebarSource Source, EventHandler Handler)> _sidebarSubscriptions = [];
    private readonly object _moduleChangesGate = new();
    private readonly Dictionary<IDesktopModule, (bool Navigation, bool Presentation, bool Player)> _moduleChanges = new(ReferenceEqualityComparer.Instance);
    private bool _moduleChangesQueued;
    private ModuleSession? _current;
    private ModuleSession? _activeContentSession;
    private ModuleSession? _playerSession;
    private ModuleSession? _settingsReturn;
    private ShellRequest? _activeRequest;
    private RequestPlan? _retryPlan;
    private CancellationTokenSource? _pageNavigation;
    private long _presentationGeneration;

    public ShellModuleCoordinator(ShellWindow shell, List<ModuleSession> sessions)
    {
        _shell = shell;
        _sessions = sessions;
    }

    public IReadOnlyList<ModuleSession> Sessions => _sessions;
    public ModuleSession? CurrentSession => _current;
    public ModuleSession? PlayerSession => _playerSession;
    public ModuleSession? SettingsReturn { get => _settingsReturn; set => _settingsReturn = value; }
    public ShellRequest? ActiveRequest => _activeRequest;
    public RequestPlan? RetryPlan { get => _retryPlan; set => _retryPlan = value; }
    public long PresentationGeneration => Interlocked.Read(ref _presentationGeneration);
    public long IncrementGeneration() => Interlocked.Increment(ref _presentationGeneration);

    public void SubscribeModules()
    {
        foreach (var session in _sessions)
        {
            session.Module.PropertyChanged += OnModulePropertyChanged;
            _shell.OwnedNotices.Subscribe(session.Module);
            _shell.SubscribeGlobalSurfaces(session);
            if (session.Module is IDesktopNavigationSource source)
            {
                EventHandler<DesktopNavigationRequestEventArgs> handler = (_, args) => OnNavigationRequested(session, args);
                source.NavigationRequested += handler;
                _navigationSubscriptions.Add((source, handler));
            }
            if (session.Module is IDesktopSidebarSource sidebar)
            {
                EventHandler handler = (_, _) => _shell.QueueUi(() =>
                {
                    if (!_shell.IsClosing && ReferenceEquals(_current, session) && _shell.CurrentPage is null)
                        _shell.OpenSettingsFromUi();
                });
                sidebar.SettingsRequested += handler;
                _sidebarSubscriptions.Add((sidebar, handler));
            }
        }
    }

    public void DisposeSubscriptions()
    {
        foreach (var session in _sessions) session.Module.PropertyChanged -= OnModulePropertyChanged;
        foreach (var (source, handler) in _navigationSubscriptions) source.NavigationRequested -= handler;
        _navigationSubscriptions.Clear();
        foreach (var (source, handler) in _sidebarSubscriptions) source.SettingsRequested -= handler;
        _sidebarSubscriptions.Clear();
    }

    public async Task ActivateAsync(ModuleSession session)
    {
        if (_shell.IsClosing) return;
        PreparePresentation(session, applyPresentation: false);
        await RunRequestAsync(new(session));
    }

    public void LeaveActiveContentSession()
    {
        var leaving = _activeContentSession;
        _activeContentSession = null;
        if (leaving is not null)
            _ = ShellRequest.ObserveAsync(leaving.Module.LeaveNavigationAsync());
    }

    public void NotifyContentEntered(ModuleSession session)
    {
        if (_activeContentSession is { } leaving && !ReferenceEquals(leaving, session))
            LeaveActiveContentSession();
        _activeContentSession = session;
    }

    public void PreparePresentation(ModuleSession session, bool enteringContent = true, bool applyPresentation = true)
    {
        _shell.LeavePublicPage();
        if (enteringContent && !session.IsSettingsOpen)
            NotifyContentEntered(session);
        if (!ReferenceEquals(_current, session))
        {
            CancelActive();
            _shell.RetireHeaderPresentation();
            Volatile.Write(ref _current, session);
            Interlocked.Increment(ref _presentationGeneration);
            _retryPlan = null;
            _shell.SetStatus();
        }
        Mount(session);
        if (applyPresentation) _shell.ApplyPresentation();
    }

    public void Mount(ModuleSession session)
    {
        if (session.Mounted) return;
        RefreshSuppliedViews(session);
        session.Surface.Children.Add(session.MainHost);
        session.Surface.Children.Add(session.SettingsHost);
        _shell.ContentDeckElement.Children.Add(session.Surface);
        session.Mounted = true;
    }

    public void RefreshSuppliedViews(ModuleSession session)
    {
        var main = session.Module.View ?? throw new InvalidOperationException();
        var settings = session.Module.SettingsView ?? throw new InvalidOperationException();
        var player = session.Module.PlayerView;
        if (ReferenceEquals(main, settings) || ReferenceEquals(main, player) || ReferenceEquals(settings, player))
            throw new InvalidOperationException();
        ValidateView(main, session.MainHost);
        ValidateView(settings, session.SettingsHost);
        if (session.IsSettingsOpen && !_shell.IsGeneralSettingsOpen)
        {
            if (!ReferenceEquals(session.SettingsHost.Content, settings)) session.SettingsHost.Content = settings;
        }
        else if (!session.IsSettingsOpen)
        {
            if (!ReferenceEquals(session.MainHost.Content, main)) session.MainHost.Content = main;
        }
    }

    public static void ValidateView(FrameworkElement view, ContentControl host)
    {
        if (!view.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException();
        if (!ReferenceEquals(host.Content, view) && VisualTreeHelper.GetParent(view) is not null)
            throw new InvalidOperationException();
    }

    public void ApplyPlayerPresentation(ModuleSession current)
    {
        var playerHost = _shell.PlayerHostElement;
        var currentPlayer = current.Module.PlayerView;
        if (currentPlayer is not null)
        {
            if (!ReferenceEquals(playerHost.Content, currentPlayer))
            {
                playerHost.Content = null;
                ValidateView(currentPlayer, playerHost);
                playerHost.Content = currentPlayer;
            }
            Volatile.Write(ref _playerSession, current);
        }
        else if (Volatile.Read(ref _playerSession) is { } owner)
        {
            var retainedPlayer = owner.Module.PlayerView;
            if (retainedPlayer is null)
            {
                playerHost.Content = null;
                Volatile.Write(ref _playerSession, null);
            }
            else if (!ReferenceEquals(playerHost.Content, retainedPlayer))
            {
                playerHost.Content = null;
                ValidateView(retainedPlayer, playerHost);
                playerHost.Content = retainedPlayer;
            }
        }
        else if (playerHost.Content is not null)
            playerHost.Content = null;

        _shell.PlayerFrameElement.Visibility = playerHost.Content is not null &&
            !(_shell.CurrentPage is null && !current.IsSettingsOpen && current.Module is IDesktopImmersiveSource { IsImmersive: true })
            ? Visibility.Visible
            : Visibility.Collapsed;
        _shell.UpdateCaption();
    }

    public void ApplyModuleAccent(ModuleSession current)
    {
        var resources = (current.MainHost.Content as FrameworkElement ?? current.Module.View)?.Resources;
        var rootResources = _shell.RootLayoutElement.Resources;
        foreach (var key in ModuleAccentKeys)
        {
            if (resources?.ContainsKey(key) == true && resources[key] is Brush brush)
            {
                if (!rootResources.ContainsKey(key) || !ReferenceEquals(rootResources[key], brush))
                    rootResources[key] = brush;
            }
            else rootResources.Remove(key);
        }
    }

    public void UpdateNavigationRetention()
    {
        foreach (var session in _sessions)
        {
            if (session.Module is not IDesktopNavigationRetention owner) continue;
            var locations = new HashSet<string>(StringComparer.Ordinal);
            _shell.History.CollectRetainedLocations(session, locations);
            if (_shell.CurrentPage is null && ReferenceEquals(_current, session)) Retain(session.Current);
            if (ReferenceEquals(_settingsReturn, session)) Retain(session.Current);
            foreach (var request in _requests)
                if ((request.Plan.IsBack || request.Plan.IsForward) && ReferenceEquals(request.Plan.Session, session)) Retain(request.Plan.Destination);
            if (_retryPlan is { } retry && (retry.IsBack || retry.IsForward) && ReferenceEquals(retry.Session, session)) Retain(retry.Destination);
            owner.RetainNavigationLocations(locations);

            void Retain(Destination? destination)
            {
                if (destination?.Location is { } location) locations.Add(location);
            }
        }
    }

    public Task NavigateFromUiAsync(ModuleSession session, string route)
    {
        if (_shell.IsClosing) return Task.CompletedTask;
        if (DebugLog.IsEnabled)
            DebugLog.Info(DebugCategories.UiNav, "shell", "navigate",
                new { moduleId = session.Module.Id, route });
        _settingsReturn = null;
        if (_current is { } previous) previous.IsSettingsOpen = false;
        session.IsSettingsOpen = false;
        bool alreadyPending = ReferenceEquals(_current, session) &&
            _activeRequest is { } active && ReferenceEquals(active.Plan.Session, session) &&
            active.Plan.Destination is { IsSearch: false } pending && string.Equals(pending.Value, route, StringComparison.Ordinal);
        PreparePresentation(session, applyPresentation: alreadyPending);
        if (alreadyPending) return Task.CompletedTask;
        return RunRequestAsync(new(session, new(route)));
    }

    public async Task RunRequestAsync(RequestPlan plan, CancellationToken cancellationToken = default, bool reportOutcome = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shell.IsClosing || _shell.CurrentPage is not null || !ReferenceEquals(_current, plan.Session))
        {
            if (reportOutcome) throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
            return;
        }
        CancelActive();
        _shell.RetireHeaderPresentation();
        if (plan.Destination is { } target) _shell.SearchCoordinator.PrepareDestination(plan.Session, target);
        var request = new ShellRequest(plan);
        var token = request.GetToken();
        _requests.Add(request);
        _activeRequest = request;
        using var cancellation = cancellationToken.Register(request.Cancel);
        _retryPlan = null;
        _shell.ApplyPresentation();
        _shell.SetStatus(plan.Destination?.IsSearch == true ? "正在搜索…" : plan.Session.Initialized ? "正在打开…" : "正在加载…", busy: true);
        var work = ExecuteModuleRequestAsync(plan, token);
        _ = ReapRequestAsync(request, work);
        try
        {
            var destination = await work.WaitAsync(OperationTimeout, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(request))
            {
                if (reportOutcome) throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
                return;
            }
            if (destination is not null)
            {
                _shell.History.Commit(plan, destination);
                _shell.SearchCoordinator.CommitSearchDestination(plan.Session, destination);
            }
            RefreshSuppliedViews(plan.Session);
            _shell.SetStatus();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(request)) _shell.SetStatus("已取消");
            if (reportOutcome) throw;
        }
        catch (TimeoutException)
        {
            request.Cancel();
            if (IsCurrent(request))
            {
                _retryPlan = plan;
                _shell.SetStatus("操作超时", error: true, retry: true);
            }
            if (reportOutcome) throw;
        }
        catch (Exception error)
        {
            if (IsCurrent(request))
            {
                _retryPlan = plan;
                _shell.SetStatus(plan.Session.Module.OperationErrorText(error), error: true, retry: true);
            }
            if (reportOutcome) throw new InvalidOperationException("SHELL_NAVIGATION_FAILED");
        }
        finally
        {
            if (IsCurrent(request))
            {
                _activeRequest = null;
                _shell.ApplyPresentation();
            }
        }
    }

    public void CancelActive()
    {
        var request = _activeRequest;
        _activeRequest = null;
        request?.Cancel();
    }

    public void CancelAllRequests()
    {
        foreach (var request in _requests.ToArray()) request.Cancel();
    }

    public async Task GoForwardAsync()
    {
        if (!_shell.State.CanGoForward) return;
        var next = _shell.History.PeekForward();
        await RestoreHistoryAsync(new(next.Session, next.Destination, IsForward: true));
    }

    public async Task GoBackAsync()
    {
        if (_shell.IsQueueOpen) { await _shell.CloseQueueAsync(); return; }
        if (_shell.IsPlayerDetailOpen) { await _shell.ClosePlayerDetailAsync(); return; }
        if (_current is not { } current) return;
        if (_shell.CurrentPage is { } currentPage)
        {
            if (!_shell.History.CanGoBack ||
                !string.Equals(_shell.History.LastPageId, currentPage.Page.Id, StringComparison.Ordinal))
            {
                _shell.LeavePublicPage(); NotifyContentEntered(current); _shell.SetStatus(); _shell.ApplyPresentation(); return;
            }
            var previous = _shell.History.PeekBack();
            await RestoreHistoryAsync(new(previous.Session, previous.Destination, IsBack: true));
            return;
        }
        if (current.IsSettingsOpen)
        {
            CancelActive(); _shell.RetireHeaderPresentation();
            current.IsSettingsOpen = false; _shell.CloseGeneralSettingsFlag();
            var restore = _settingsReturn ?? current;
            _settingsReturn = null;
            var lastPageId = _shell.History.LastPageId;
            PreparePresentation(restore, enteringContent: lastPageId is null, applyPresentation: false);
            _retryPlan = null; _shell.SetStatus();
            if (lastPageId is { } pageId)
            {
                await _shell.ActivatePageAsync(_shell.Pages.Single(page => page.Page.Id == pageId), recordNavigation: false);
                return;
            }
            _shell.ApplyPresentation();
            _shell.QueueUi(() =>
            {
                if (!ReferenceEquals(_current, restore) || restore.IsSettingsOpen) return;
                if (restore.ContentFocus is Control { Visibility: Visibility.Visible, IsEnabled: true, IsTabStop: true } focus)
                    focus.Focus(FocusState.Programmatic);
                else DesktopVisual.FocusFirst(restore.MainHost);
            });
            if (!restore.Initialized) await RunRequestAsync(new(restore));
            return;
        }
        if (_shell.History.CanGoBack)
        {
            var previous = _shell.History.PeekBack();
            await RestoreHistoryAsync(new(previous.Session, previous.Destination, IsBack: true));
        }
    }

    private async Task RestoreHistoryAsync(RequestPlan plan)
    {
        if (plan.Destination?.PageId is { } pageId)
        {
            PreparePresentation(plan.Session, enteringContent: false, applyPresentation: false);
            await _shell.ActivatePageAsync(_shell.Pages.Single(item => item.Page.Id == pageId), navigation: plan);
            return;
        }
        plan.Session.IsSettingsOpen = false;
        PreparePresentation(plan.Session, applyPresentation: false);
        await RunRequestAsync(plan);
    }

    public void ResetPageNavigation() => _pageNavigation = new CancellationTokenSource();

    public void CancelPageNavigation()
    {
        var previous = _pageNavigation;
        _pageNavigation = null; // Retire admission before arbitrary cancellation callbacks.
        if (previous is not null)
        {
            Interlocked.Increment(ref _presentationGeneration);
            _ = RetireAsync(previous);
        }

        static async Task RetireAsync(CancellationTokenSource source)
        {
            try { await ShellRequest.ObserveAsync(source.CancelAsync()); }
            finally { source.Dispose(); }
        }
    }

    public CancellationToken GetPageNavigationToken(IDesktopPage page, string moduleId, string route)
    {
        _shell.Dispatcher.VerifyAccess();
        ValidatePageNavigation(page, moduleId, route);
        return (_pageNavigation ?? throw new InvalidOperationException("SHELL_NAVIGATION_STALE")).Token;
    }

    public async Task NavigateFromPageAsync(IDesktopPage page, string moduleId, string route,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Interlocked.Read(ref _presentationGeneration);
        if (_shell.Dispatcher.HasThreadAccess) await NavigateAsync();
        else await _shell.Dispatcher.EnqueueAsync(NavigateAsync, DispatcherQueuePriority.Normal, cancellationToken);

        async Task NavigateAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _presentationGeneration))
                throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
            var session = ValidatePageNavigation(page, moduleId, route);
            session.IsSettingsOpen = false;
            PreparePresentation(session, applyPresentation: false);
            await RunRequestAsync(new(session, new(route)), cancellationToken, reportOutcome: true);
        }
    }

    private ModuleSession ValidatePageNavigation(IDesktopPage page, string moduleId, string route)
    {
        if (_shell.IsClosing || _shell.IsClosed) throw new InvalidOperationException("SHELL_NAVIGATION_CLOSED");
        if (page is null || !ReferenceEquals(_shell.CurrentPage?.Page, page) || _pageNavigation is null)
            throw new InvalidOperationException("SHELL_NAVIGATION_STALE");
        var session = _sessions.SingleOrDefault(item => string.Equals(item.Module.Id, moduleId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("SHELL_MODULE_UNAVAILABLE");
        if (string.IsNullOrWhiteSpace(route) ||
            !session.Module.Navigation.Any(item => string.Equals(item.Route, route, StringComparison.Ordinal)))
            throw new InvalidOperationException("SHELL_ROUTE_UNAVAILABLE");
        return session;
    }

    private void OnNavigationRequested(ModuleSession session, DesktopNavigationRequestEventArgs args)
    {
        if (args is null || string.IsNullOrWhiteSpace(args.Route)) return;
        var generation = Interlocked.Read(ref _presentationGeneration);
        var route = args.Route;
        var fromPlayer = args.FromPlayer;
        _shell.QueueUi(() =>
        {
            if (generation != Interlocked.Read(ref _presentationGeneration) || !MayNavigate(session, fromPlayer)) return;
            if (fromPlayer && !session.Module.Navigation.Any(item => item.Route == route)) return;
            if (_activeRequest is { } inFlight && ReferenceEquals(inFlight.Plan.Session, session) &&
                inFlight.Plan.Destination is { IsSearch: false } destination &&
                string.Equals(destination.Value, route, StringComparison.Ordinal))
                return;
            _shell.RunUiAction(() => NavigateFromUiAsync(session, route));
        });
    }

    private bool MayNavigate(ModuleSession session, bool fromPlayer)
    {
        var current = _current;
        return fromPlayer
            ? current is { IsSettingsOpen: false } && ReferenceEquals(_playerSession, session)
            : _shell.CurrentPage is { Page: IDesktopSearchPage search }
                ? ReferenceEquals(search.ContentOwner, session.Module)
                : _shell.CurrentPage is null && ReferenceEquals(current, session);
    }

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IDesktopModule module || _shell.IsClosing || _shell.Dispatcher.IsClosing) return;
        var all = string.IsNullOrEmpty(e.PropertyName);
        bool navigation = all || e.PropertyName == nameof(IDesktopModule.Navigation);
        bool player = all || e.PropertyName == nameof(IDesktopModule.PlayerView);
        bool presentation = all || e.PropertyName is nameof(IDesktopModule.View) or nameof(IDesktopModule.SettingsView) or nameof(IDesktopModule.Title) ||
            module is IDesktopSidebarSource && e.PropertyName == nameof(IDesktopSidebarSource.SidebarView) ||
            module is IDesktopImmersiveSource && e.PropertyName == nameof(IDesktopImmersiveSource.IsImmersive);
        if (!navigation && !player && !presentation) return;
        lock (_moduleChangesGate)
        {
            _moduleChanges.TryGetValue(module, out var previous);
            _moduleChanges[module] = (
                previous.Navigation || navigation,
                previous.Presentation || presentation,
                previous.Player || player);
            if (_moduleChangesQueued) return;
            _moduleChangesQueued = true;
        }
        _ = ShellRequest.ObserveAsync(_shell.Dispatcher.EnqueueAsync(() =>
        {
            try { ApplyModuleChanges(); }
            catch (Exception) { if (!_shell.IsClosing) _shell.SetStatus("暂时无法完成操作", error: true); }
        }, DispatcherQueuePriority.Normal));
    }

    private void ApplyModuleChanges()
    {
        KeyValuePair<IDesktopModule, (bool Navigation, bool Presentation, bool Player)>[] changes;
        lock (_moduleChangesGate)
        {
            changes = _moduleChanges.ToArray();
            _moduleChanges.Clear();
            _moduleChangesQueued = false;
        }
        if (_shell.IsClosing) return;
        var refreshPresentation = false;
        var refreshPlayer = false;
        foreach (var change in changes)
        {
            var session = _sessions.FirstOrDefault(item => ReferenceEquals(item.Module, change.Key));
            if (session is null) continue;
            if (change.Value.Navigation) _shell.Sidebar.BindNavigation(session);
            refreshPresentation |= ReferenceEquals(session, _current) && change.Value.Presentation;
            refreshPlayer |= change.Value.Player && (ReferenceEquals(session, _current) || ReferenceEquals(session, _playerSession));
        }
        if (refreshPresentation) _shell.ApplyPresentation();
        else if (refreshPlayer && _current is { } current)
        { ApplyPlayerPresentation(current); _shell.ApplyGlobalSurfacePresentation(applyPresentation: true); }
    }

    private static async Task<Destination?> ExecuteModuleRequestAsync(RequestPlan plan, CancellationToken token)
    {
        var session = plan.Session;
        await session.Gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!session.Initialized)
            {
                await session.Module.InitializeAsync(token);
                session.Initialized = true;
            }
            token.ThrowIfCancellationRequested();
            var destination = plan.Destination;
            if (destination is null && session.Current is null && !session.IsSettingsOpen && session.Module.Navigation.Count > 0)
                destination = new(session.Module.Navigation[0].Route);
            if (destination is null) return null;
            if (plan.IsBack || plan.IsForward)
            {
                if (string.IsNullOrEmpty(destination.Location)) throw new InvalidOperationException("SHELL_NAVIGATION_LOCATION_MISSING");
                await session.Module.RestoreNavigationAsync(destination.Location, token);
            }
            else if (destination.IsSearch) await session.Module.SearchAsync(destination.Value, token);
            else await session.Module.NavigateAsync(destination.Value, token);
            token.ThrowIfCancellationRequested();
            if (!plan.IsBack && !plan.IsForward)
            {
                var location = session.Module.CaptureNavigationLocation();
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(location)) throw new InvalidOperationException("SHELL_NAVIGATION_LOCATION_MISSING");
                destination = destination with { Location = location };
            }
            return destination;
        }
        finally { session.Gate.Release(); }
    }

    private async Task ReapRequestAsync(ShellRequest request, Task work)
    {
        await ShellRequest.ObserveAsync(work);
        await ShellRequest.ObserveAsync(request.Cancellation);
        _requests.Remove(request);
        request.Dispose();
        if (!_shell.IsClosing) UpdateNavigationRetention();
    }

    private bool IsCurrent(ShellRequest request) => !_shell.IsClosing && _shell.CurrentPage is null &&
        ReferenceEquals(_activeRequest, request) && ReferenceEquals(_current, request.Plan.Session);
}
