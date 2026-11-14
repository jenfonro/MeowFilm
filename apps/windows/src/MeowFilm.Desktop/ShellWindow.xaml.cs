using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow : DesktopWindow
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private const int HistoryLimit = 64;

    private readonly ShellState _state = new();
    private readonly List<ModuleSession> _sessions;
    private readonly Func<Task> _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<ShellRequest> _requests = [];
    private readonly List<(IDesktopNavigationSource Source, EventHandler<DesktopNavigationRequestEventArgs> Handler)> _navigationSubscriptions = [];
    private TaskCompletionSource _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ModuleSession? _current, _playerSession;
    private ShellRequest? _activeRequest;
    private RequestPlan? _retryPlan;
    private NativeCaption? _nativeCaption;
    private bool _suppressSelection;
    private bool _suppressSearch;
    private ModuleSession? _searchSession;
    private string? _searchPresentationKey;
    private bool _loaded;
    private bool _isClosing;
    private bool _allowClose;
    private bool _isClosed;
    private bool _shutdownWaitInProgress;
    private bool _closeQueued;
    private Task? _shutdownTask;
    private int _shutdownStarted;
    private long _presentationGeneration;

    /// <summary>
    /// The first supplied module is the default. No runtime, account, storage, or player is created here.
    /// shutdown must return promptly with its asynchronous Task; it owns module/runtime disposal.
    /// </summary>
    public ShellWindow(IReadOnlyList<IDesktopModule> modules, Func<Task> shutdown)
        : this(modules, shutdown, TimeProvider.System)
    {
    }

    // Deterministic code tests can advance the existing production deadlines without sleeping.
    // No public timeout override or App configuration is introduced.
    internal ShellWindow(IReadOnlyList<IDesktopModule> modules, Func<Task> shutdown, TimeProvider timeProvider)
        : this(modules, [], shutdown, null, null, timeProvider) { }

    public ShellWindow(IReadOnlyList<IDesktopModule> modules, IReadOnlyList<IDesktopPage> pages,
        Func<Task> shutdown, Func<Task> forceShutdown, Func<Task>? cancelShutdown = null,
        IDesktopAppearance? appearance = null, IDesktopClosePreferences? closePreferences = null,
        IDesktopMediaControlSettings? mediaControls = null, Func<Task>? resetUserData = null)
        : this(modules, pages, shutdown, forceShutdown, cancelShutdown, TimeProvider.System, appearance,
            closePreferences, mediaControls: mediaControls, resetUserData: resetUserData) { }

    internal ShellWindow(IReadOnlyList<IDesktopModule> modules, IReadOnlyList<IDesktopPage> pages,
        Func<Task> shutdown, Func<Task>? forceShutdown, Func<Task>? cancelShutdown, TimeProvider timeProvider,
        IDesktopAppearance? appearance = null, IDesktopClosePreferences? closePreferences = null,
        Func<Window, Action, Action, Action, IDesktopTrayIcon>? trayIconFactory = null,
        IDesktopMediaControlSettings? mediaControls = null, Func<Task>? resetUserData = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (modules.Count == 0) throw new ArgumentException("At least one module is required.", nameof(modules));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        _sessions = new(modules.Count);
        foreach (var module in modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Id) || !ids.Add(module.Id))
                throw new ArgumentException("Modules must have distinct, non-empty identities.", nameof(modules));
            _sessions.Add(new(module));
        }
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Any(page => page is null || string.IsNullOrWhiteSpace(page.Id) || !ids.Add(page.Id)))
            throw new ArgumentException("Pages must have distinct, non-empty identities.", nameof(pages));
        _pages = pages.Select(page => new PageSession(page)).ToArray();
        _forceShutdown = forceShutdown;
        _cancelShutdown = cancelShutdown;
        _shutdown = shutdown;
        _resetUserData = resetUserData;
        MediaControls = mediaControls;
        _closePreferences = closePreferences;
        _trayIconFactory = trayIconFactory;
        InitializeComponent();
        ChannelDialogHost.Attach(this);
        InitializeGeneralSettings(appearance, closePreferences);
        CloseDialog.ChoiceRequested += OnCloseChoiceRequested;
        CloseDialog.CancelRequested += CancelCloseChoice;
        ResetDialog.ConfirmRequested += OnUserDataResetConfirmed;
        ResetDialog.CancelRequested += CancelUserDataReset;
        ResetDialog.ExitRequested += OnUserDataResetExit;
        AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, e) =>
        {
            Notices.Show(e.Outlet, e.Message);
            e.Handled = true;
        }));
        InitializePresentationMotion();
        DataContext = _state;
        DesktopTheme.SetIsHighContrast(this, SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
        foreach (var session in _sessions)
        {
            session.Module.PropertyChanged += OnModulePropertyChanged;
            SubscribeGlobalSurfaces(session);
            if (session.Module is IDesktopNavigationSource source)
            {
                // Capture the subscribed module, not the caller-supplied sender argument.
                EventHandler<DesktopNavigationRequestEventArgs> handler = (_, args) => OnNavigationRequested(session, args);
                source.NavigationRequested += handler;
                _navigationSubscriptions.Add((source, handler));
            }
        }
        _suppressSelection = true;
        InitializeUnifiedNavigation();
        InitializeHeaderPresentation();
        InitializeSettingsNavigation();
        PublicNavigationList.ItemsSource = pages.Where(page => page.ShowInSidebar).ToArray();
        PublicNavigationList.Visibility = !pages.Any(page => page.ShowInSidebar) ? Visibility.Collapsed : Visibility.Visible;
        _suppressSelection = false;
        UpdateCaption();
    }

    private void UpdatePlayerDetailChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        PlayerDetailMaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        var label = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(PlayerDetailMaximizeButton, label);
    }

    /// <summary>
    /// Completes after the initial shutdown wait succeeds, fails, or reaches five seconds.
    /// Completion is not permission to exit: on failure/timeout the shell requires an explicit decision.
    /// </summary>
    public Task ShutdownCompletion => _shutdownCompletion.Task;
    public IDesktopMediaControlSettings? MediaControls { get; }

    /// <summary>True only after an active wait observes actual successful cleanup, never merely a timeout or forced exit.</summary>
    public bool ShutdownSucceeded { get; private set; }
    public bool CleanupCompleted { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Install before WindowChrome's SourceInitialized handler so the custom maximize button
        // can retain the native HTMAXBUTTON/Snap Layout hit target.
        ShellWindowStartupSize.ApplyToPrimaryScreen(this, new WindowInteropHelper(this).Handle);
        _nativeCaption = new(this, MaximizeButton, ToggleMaximize);
        base.OnSourceInitialized(e);
        _nativeCaption.SetImmersiveBorder(false);
        InitializeTrayIcon();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized) _trayRestoreState = WindowState;
        if (MaximizeGlyph is not null) UpdateCaption();
        if (PlayerDetailMaximizeGlyph is not null) UpdatePlayerDetailChrome();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _isClosing) return;
        _loaded = true;
        RunUiAction(() => ActivateAsync(_current ?? _sessions[0]));
    }

    /// <summary>
    /// Synchronously mounts the default/current supplied views for an STA offscreen layout/render test.
    /// Does not show the window, raise Loaded, initialize modules, or call any business operation.
    /// Repeated calls retain the same view instances. Normal Loaded performs async initialization later.
    /// </summary>
    public void PrepareForRender()
    {
        Dispatcher.VerifyAccess();
        if (_isClosing) throw new InvalidOperationException("The shell is closing.");
        PreparePresentation(_current ?? _sessions[0]);
    }

    private async Task ActivateAsync(ModuleSession session)
    {
        if (_isClosing) return;
        PreparePresentation(session);
        await RunRequestAsync(new(session));
    }

    private void PreparePresentation(ModuleSession session)
    {
        LeavePublicPage();
        if (!ReferenceEquals(_current, session))
        {
            CancelActive();
            RetireHeaderPresentation();
            Volatile.Write(ref _current, session);
            Interlocked.Increment(ref _presentationGeneration);
            _retryPlan = null;
            SetStatus();
        }
        // Hide the previous capability before attaching the first-visited surface.
        // Visited surfaces retain their visual tree and owner-managed state.
        ApplyPresentation();
        _suppressSelection = true;
        try { NavigationList.SelectedItem = null; }
        finally { _suppressSelection = false; }
        Mount(session);
        BindNavigation(session);
        ApplyPresentation();
    }

    private void Mount(ModuleSession session)
    {
        if (session.Mounted) return;
        RefreshSuppliedViews(session);
        session.Surface.Children.Add(session.MainHost);
        session.Surface.Children.Add(session.SettingsHost);
        ContentDeck.Children.Add(session.Surface);
        session.Mounted = true;
    }

    private void RefreshSuppliedViews(ModuleSession session)
    {
        var main = session.Module.View ?? throw new InvalidOperationException();
        var settings = session.Module.SettingsView ?? throw new InvalidOperationException();
        var player = session.Module.PlayerView;
        if (ReferenceEquals(main, settings) || ReferenceEquals(main, player) || ReferenceEquals(settings, player))
            throw new InvalidOperationException();
        ValidateView(main, session.MainHost);
        ValidateView(settings, session.SettingsHost);
        if (session.IsSettingsOpen && !_generalSettingsOpen)
        {
            if (!ReferenceEquals(session.SettingsHost.Content, settings)) session.SettingsHost.Content = settings;
        }
        else if (!session.IsSettingsOpen)
        {
            if (!ReferenceEquals(session.MainHost.Content, main)) session.MainHost.Content = main;
        }
    }

    private static void ValidateView(FrameworkElement view, ContentControl host)
    {
        if (!view.Dispatcher.CheckAccess()) throw new InvalidOperationException();
        if (!ReferenceEquals(host.Content, view) &&
            (LogicalTreeHelper.GetParent(view) is not null || System.Windows.Media.VisualTreeHelper.GetParent(view) is not null))
            throw new InvalidOperationException();
    }

    private void BindNavigation(ModuleSession session)
    {
        var navigation = session.Module.Navigation ?? throw new InvalidOperationException();
        if (!ReferenceEquals(session.Navigation, navigation))
        {
            if (session.Navigation is not null) session.Navigation.CollectionChanged -= OnNavigationChanged;
            session.Navigation = navigation;
            navigation.CollectionChanged += OnNavigationChanged;
        }
        RebuildSidebar();
    }

    private void ApplyPresentation()
    {
        if (_current is not { } current) return;
        if (current.Mounted && _currentPage is null) RefreshSuppliedViews(current);
        foreach (var session in _sessions.Where(session => session.Mounted))
        {
            var selected = ReferenceEquals(session, current);
            session.Surface.Visibility = selected && _currentPage is null ? Visibility.Visible : Visibility.Collapsed;
            session.MainHost.Visibility = session.IsSettingsOpen ? Visibility.Collapsed : Visibility.Visible;
            session.SettingsHost.Visibility = session.IsSettingsOpen && !_generalSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        _generalSettingsHost.Visibility = _currentPage is null && current.IsSettingsOpen && _generalSettingsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyPlayerPresentation(current);
        ApplyModuleAccent(current);
        ApplyHeaderPresentation(current);
        ApplyGlobalSurfacePresentation(current);
        bool immersive = _currentPage is null && !current.IsSettingsOpen &&
            current.Module is IDesktopImmersiveSource { IsImmersive: true };
        RootLayout.ColumnDefinitions[0].Width = new(immersive ? 0 : 203);
        PageHeaderHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarBackground.Visibility = Brand.Visibility = SidebarScroll.Visibility =
            immersive ? Visibility.Collapsed : Visibility.Visible;
        if (immersive) PlayerFrame.Visibility = Visibility.Collapsed;
        _state.CanGoBack = _queueOpen || _playerDetailOpen || _currentPage is not null ||
            current.IsSettingsOpen || _returnPage is not null || _history.Count > 1;
        ApplySearchPresentation();
        foreach (var page in _pages) page.Host.Visibility = ReferenceEquals(page, _currentPage) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SettingsButton, "设置");
        AutomationProperties.SetName(NavigationList, "内容导航");
        RestoreNavigationSelection();
        UpdateNavigationRetention();
    }

    private void UpdateNavigationRetention()
    {
        foreach (var session in _sessions)
        {
            if (session.Module is not IDesktopNavigationRetention owner) continue;
            var locations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _history)
                if (ReferenceEquals(entry.Session, session)) Retain(entry.Destination);
            if (_currentPage is null && ReferenceEquals(_current, session)) Retain(session.Current);
            if (ReferenceEquals(_settingsReturn, session)) Retain(session.Current);
            RetainOrigin(_currentPage);
            RetainOrigin(_returnPage);
            foreach (var request in _requests)
                if (request.Plan.IsBack && ReferenceEquals(request.Plan.Session, session)) Retain(request.Plan.Destination);
            if (_retryPlan is { IsBack: true } retry && ReferenceEquals(retry.Session, session)) Retain(retry.Destination);
            owner.RetainNavigationLocations(locations);

            void Retain(Destination? destination)
            {
                if (destination?.Location is { } location) locations.Add(location);
            }
            void RetainOrigin(PageSession? page)
            {
                if (page?.Origin is { } origin && ReferenceEquals(origin.Session, session)) Retain(origin.Destination);
            }
        }
    }

    private void ApplyPlayerPresentation(ModuleSession current)
    {
        var currentPlayer = current.Module.PlayerView;
        if (currentPlayer is not null)
        {
            if (!ReferenceEquals(PlayerHost.Content, currentPlayer))
            {
                PlayerHost.Content = null;
                ValidateView(currentPlayer, PlayerHost);
                PlayerHost.Content = currentPlayer;
            }
            Volatile.Write(ref _playerSession, current);
        }
        else if (Volatile.Read(ref _playerSession) is { } owner)
        {
            var retainedPlayer = owner.Module.PlayerView;
            if (retainedPlayer is null)
            {
                PlayerHost.Content = null;
                Volatile.Write(ref _playerSession, null);
            }
            else if (!ReferenceEquals(PlayerHost.Content, retainedPlayer))
            {
                PlayerHost.Content = null;
                ValidateView(retainedPlayer, PlayerHost);
                PlayerHost.Content = retainedPlayer;
            }
        }
        else if (PlayerHost.Content is not null)
            PlayerHost.Content = null;

        PlayerFrame.Visibility = !current.IsSettingsOpen && PlayerHost.Content is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string SearchKey(Destination? destination) => destination switch
    {
        { IsSearch: true } => "search",
        { Value.Length: > 0 } value => "route/" + value.Value,
        _ => "default"
    };

    private void ApplySearchDraft(ModuleSession session, Destination? destination = null, bool force = false)
    {
        var key = SearchKey(destination ?? session.Current);
        if (!force && ReferenceEquals(_searchSession, session) &&
            string.Equals(_searchPresentationKey, key, StringComparison.Ordinal)) return;
        _searchSession = session;
        _searchPresentationKey = key;
        session.ActiveSearchKey = key;
        _suppressSearch = true;
        try { SearchBox.Text = session.SearchDrafts.GetValueOrDefault(key) ?? ""; }
        finally { _suppressSearch = false; }
    }

    private void ApplySearchPresentation(bool force = false)
    {
        if (_currentPage?.Page is IDesktopSearchPage || _searchOwnerSession is not { } owner) return;
        var destination = _activeRequest is { } request && ReferenceEquals(request.Plan.Session, owner)
            ? request.Plan.Destination
            : owner.Current;
        ApplySearchDraft(owner, destination, force);
    }

    private void ApplyModuleAccent(ModuleSession current)
    {
        // Only explicit resources on the supplied main view opt into shell accent overrides.
        // Never identify a capability by Id or copy its business resources into another owner.
        string[] keys = ["MeowFilm.Brush.Accent", "MeowFilm.Brush.AccentHover", "MeowFilm.Brush.OnAccent", "MeowFilm.Brush.AccentSoft"];
        var resources = (current.MainHost.Content as FrameworkElement ?? current.Module.View)?.Resources;
        foreach (var key in keys)
        {
            if (resources?.Contains(key) == true && resources[key] is System.Windows.Media.Brush brush)
            {
                if (!RootLayout.Resources.Contains(key) || !ReferenceEquals(RootLayout.Resources[key], brush))
                    RootLayout.Resources[key] = brush;
            }
            else RootLayout.Resources.Remove(key);
        }
    }
    private void RestoreNavigationSelection()
    {
        if (_current is not { } current) return;
        var route = _currentPage is null && !current.IsSettingsOpen && current.Current is { IsSearch: false } destination ? destination.Value : null;
        _suppressSelection = true;
        try
        {
            var selected = _sidebar.FirstOrDefault(item => ReferenceEquals(item.Owner, current) && string.Equals(item.Route, route, StringComparison.Ordinal));
            NavigationList.SelectedItem = selected?.Group is null ? selected : null;
            CollectionNavigationList.SelectedItem = selected?.Group is not null ? selected : null;
        }
        finally { _suppressSelection = false; }
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _isClosing || (!ReferenceEquals(e.Source, NavigationList) && !ReferenceEquals(e.Source, CollectionNavigationList)) || _current is not { } current) return;
        if (sender is not ListBox { SelectedItem: ShellNavigationEntry item }) return;
        RunUiAction(() => NavigateFromUiAsync(item.Owner, item.Route));
    }

    private void OnNavigationRequested(ModuleSession session, DesktopNavigationRequestEventArgs args)
    {
        if (args is null || string.IsNullOrWhiteSpace(args.Route)) return;
        var generation = Interlocked.Read(ref _presentationGeneration);
        if (!MayNavigate(session, args.FromPlayer)) return;
        var route = args.Route;
        if (args.FromPlayer && !session.Module.Navigation.Any(item => item.Route == route)) return;
        var inFlight = Volatile.Read(ref _activeRequest);
        if (inFlight is not null && ReferenceEquals(inFlight.Plan.Session, session) &&
            inFlight.Plan.Destination is { IsSearch: false } destination && string.Equals(destination.Value, route, StringComparison.Ordinal))
            return;
        QueueUi(() =>
        {
            // A queued request from an old presentation must not navigate after switching away/back.
            if (!MayNavigate(session, args.FromPlayer) || generation != Interlocked.Read(ref _presentationGeneration)) return;
            RunUiAction(() => NavigateFromUiAsync(session, route, fromContent: !args.FromPlayer));
        });
    }

    private bool MayNavigate(ModuleSession session, bool fromPlayer)
    {
        var current = Volatile.Read(ref _current);
        return fromPlayer
            ? current is { IsSettingsOpen: false } &&
                ReferenceEquals(Volatile.Read(ref _playerSession), session)
             : Volatile.Read(ref _currentPage) is { Page: IDesktopSearchPage search }
                ? ReferenceEquals(search.ContentOwner, session.Module)
                : Volatile.Read(ref _currentPage) is null && ReferenceEquals(current, session);
    }

    private Task NavigateFromUiAsync(ModuleSession session, string route, bool fromContent = false)
    {
        if (fromContent && _currentPage?.Page is IDesktopSearchPage) _returnPage = _currentPage;
        else if (!fromContent) _returnPage = null;
        if (_isClosing) return Task.CompletedTask;
        _settingsReturn = null;
        if (_current is { } previous) previous.IsSettingsOpen = false;
        PreparePresentation(session);
        LeavePublicPage();
        // Avoid a same-route feedback loop if an adapter mistakenly re-emits an in-flight request.
        if (_activeRequest is { } active && ReferenceEquals(active.Plan.Session, session) &&
            active.Plan.Destination is { IsSearch: false } pending && string.Equals(pending.Value, route, StringComparison.Ordinal))
            return Task.CompletedTask;
        session.IsSettingsOpen = false;
        return RunRequestAsync(new(session, new(route)));
    }

    private void OnNavigationChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueUi(() =>
    {
        RebuildSidebar();
    });

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueUi(() =>
    {
        var session = _sessions.FirstOrDefault(item => ReferenceEquals(item.Module, sender));
        if (session is null) return;
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(IDesktopModule.Navigation)) BindNavigation(session);
        if (all || e.PropertyName is nameof(IDesktopModule.View) or nameof(IDesktopModule.SettingsView))
            { if (session.Mounted && ReferenceEquals(session, _current) && _currentPage is null) RefreshSuppliedViews(session); }
        if (ReferenceEquals(session, _current) || ReferenceEquals(session, _playerSession))
            ApplyPresentation();
    });

    private void OnSettingsClick(object sender, RoutedEventArgs e) => RunUiAction(async () =>
    {
        if (_currentPage?.Page is IDesktopSearchPage) await GoBackAsync();
        if (_current is not { } current) return;
        if (_playerDetailOpen) await ClosePlayerDetailAsync();
        if (current.IsSettingsOpen && _currentPage is null) { ApplyPresentation(); return; }
        var leftPublicPage = _currentPage is not null;
        LeavePublicPage();
        CancelActive();
        if (!leftPublicPage) RetireHeaderPresentation();
        if (current.MainHost.IsKeyboardFocusWithin) current.ContentFocus = Keyboard.FocusedElement;
        _settingsReturn = current;
        current.IsSettingsOpen = true;
        _generalSettingsOpen = _generalSettingsHost.Content is not null;
        _retryPlan = null;
        SetStatus();
        ApplyPresentation();
        QueueUi(() =>
        {
            if (ReferenceEquals(_current, current) && current.IsSettingsOpen)
                (_generalSettingsOpen ? _generalSettingsHost : current.SettingsHost).MoveFocus(new(FocusNavigationDirection.First));
        });
        if (!_generalSettingsOpen && !current.Initialized) await RunRequestAsync(new(current));
    });

    private void OnBackClick(object sender, RoutedEventArgs e) => RunUiAction(GoBackAsync);

    private async Task GoBackAsync()
    {
        if (_queueOpen) { await CloseQueueAsync(); return; }
        if (_playerDetailOpen) { await ClosePlayerDetailAsync(); return; }
        if (_current is not { } current) return;
        if (_currentPage is { } page)
        {
            LeavePublicPage();
            if (page.Origin is { } origin)
            {
                page.Origin = null;
                PreparePresentation(origin.Session);
                await RunRequestAsync(new(origin.Session, origin.Destination, IsBack: true));
            }
            SetStatus();
            ApplyPresentation();
            return;
        }
        if (current.IsSettingsOpen)
        {
            CancelActive();
            RetireHeaderPresentation();
            current.IsSettingsOpen = false;
            _generalSettingsOpen = false;
            var restore = _settingsReturn ?? current;
            _settingsReturn = null;
            PreparePresentation(restore);
            _retryPlan = null;
            SetStatus();
            ApplyPresentation();
            QueueUi(() =>
            {
                if (!ReferenceEquals(_current, restore) || restore.IsSettingsOpen) return;
                if (restore.ContentFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } focus)
                    Keyboard.Focus(focus);
                else restore.MainHost.MoveFocus(new(FocusNavigationDirection.First));
            });
            // No navigation replay: returning from settings reveals the same view and scroll state.
            if (!restore.Initialized) await RunRequestAsync(new(restore));
            return;
        }
        if (_returnPage is { } returnPage)
        {
            _returnPage = null;
            if (returnPage.Origin is { } origin && _history.Count > origin.HistoryDepth)
                _history.RemoveRange(origin.HistoryDepth, _history.Count - origin.HistoryDepth);
            await ActivatePageAsync(returnPage);
            return;
        }
        if (_history.Count > 1)
        {
            var previous = _history[^2];
            PreparePresentation(previous.Session);
            await RunRequestAsync(new(previous.Session, previous.Destination, IsBack: true));
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_current is { } current && current.MainHost.IsKeyboardFocusWithin)
            current.ContentFocus = e.NewFocus;
    }
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;
        if (_currentPage is { Page: IDesktopSearchPage } page) page.SearchDraft = SearchBox.Text;
        else if (_searchSession is { } owner)
            owner.SearchDrafts[owner.ActiveSearchKey] = SearchBox.Text;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        // Key.ImeProcessed is intentionally not interpreted as Enter while composing text.
        if (_isClosing || e.Key != Key.Enter || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        RunUiAction(SearchAsync);
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => RunUiAction(SearchAsync);

    private Task SearchAsync()
    {
        if (!_isClosing && _current is { IsSettingsOpen: false } && _searchPage is { } searchPage &&
            SearchBox.IsEnabled && !string.IsNullOrWhiteSpace(SearchBox.Text) &&
            SearchTargetPicker.SelectedValue is string target)
        {
            string query = SearchBox.Text.Trim();
            var page = _pages.Single(item => ReferenceEquals(item.Page, searchPage));
            page.SearchDraft = SearchBox.Text;
            if (!ReferenceEquals(_currentPage, page) && _current?.Current is { } destination)
                page.Origin = (_current, destination, _history.Count);
            _returnPage = null;
            return ActivatePageAsync(page, token => searchPage.SubmitAsync(query, target, token));
        }
        if (_current is not { } current || current.IsSettingsOpen ||
            _searchOwnerSession is not { } owner || !SearchBox.IsEnabled ||
            string.IsNullOrWhiteSpace(SearchBox.Text)) return Task.CompletedTask;
        owner.SearchDrafts[owner.ActiveSearchKey] = SearchBox.Text;
        owner.IsSettingsOpen = false;
        _settingsReturn = null;
        if (_currentPage is not null || !ReferenceEquals(current, owner))
            PreparePresentation(owner);
        return RunRequestAsync(new(owner, new(SearchBox.Text.Trim(), IsSearch: true)));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CloseDialog.IsOpen || ResetDialog.IsOpen) return; // The active dialog owns keyboard navigation and Escape.
        if (_isClosing) return;
        if (ChannelDialogHost.IsOpen)
        {
            if (!ChannelDialogHost.HandleEscape(e)) ProtectChannelKeyboard(e);
            if (e.Key == Key.F && e.KeyboardDevice.Modifiers == ModifierKeys.Control ||
                (e.Key == Key.Left || e.SystemKey == Key.Left) && e.KeyboardDevice.Modifiers == ModifierKeys.Alt)
                e.Handled = true;
            return;
        }
        // Let a native range editor cancel its own gesture before Escape closes
        // the full player. The shell must not depend on a music-specific type.
        if (e.Key == Key.Escape && (Mouse.Captured is Slider || Keyboard.FocusedElement is Slider)) return;
        if (e.Key == Key.F && e.KeyboardDevice.Modifiers == ModifierKeys.Control &&
            SearchInput.Visibility == Visibility.Visible && SearchBox.IsEnabled)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if ((e.Key == Key.Left || e.SystemKey == Key.Left) && e.KeyboardDevice.Modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            RunUiAction(GoBackAsync);
        }
        else if (e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.None && _state.CanCancel)
        {
            e.Handled = true;
            OnCancelClick(this, e);
        }
        else if (e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            (_queueOpen || _playerDetailOpen))
        {
            e.Handled = true;
            RunUiAction(GoBackAsync);
        }
    }

    private void ProtectChannelKeyboard(KeyEventArgs e)
    {
        // Only controls within the active channel keep their own keyboard input.
        if (e.OriginalSource is not DependencyObject target ||
            !ChannelDialogHost.IsAncestorOf(target)) e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!CloseDialog.IsOpen && !ResetDialog.IsOpen && ChannelDialogHost.IsOpen)
            ProtectChannelKeyboard(e);
    }

    private void OnChannelHeaderPointer(object sender, MouseButtonEventArgs e)
    {
        if (CloseDialog.IsOpen || ResetDialog.IsOpen || !ChannelDialogHost.IsOpen) return;
        bool In(FrameworkElement control) => e.OriginalSource is DependencyObject target &&
            (ReferenceEquals(target, control) || control.IsAncestorOf(target));
        if (ChannelDialogHost.HasOutsidePress || In(SearchChrome) || In(SettingsButton) || In(PlayerDetailBackButton) ||
            In(PageHeaderHost) && e.OriginalSource is IInputElement node &&
            System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(node))
            ChannelDialogHost.HandleOutsidePointer(e, RootLayout);
    }

    private async Task RunRequestAsync(RequestPlan plan, CancellationToken cancellationToken = default, bool reportOutcome = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isClosing || _currentPage is not null || !ReferenceEquals(_current, plan.Session))
        {
            if (reportOutcome) throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
            return;
        }
        CancelActive();
        RetireHeaderPresentation();
        if (plan.Destination is { } target)
        {
            if (target.IsSearch) plan.Session.SearchDrafts[SearchKey(target)] = target.Value;
            if (ReferenceEquals(plan.Session, _searchOwnerSession))
                ApplySearchDraft(plan.Session, target, force: true);
        }
        var request = new ShellRequest(plan);
        var token = request.GetToken();
        _requests.Add(request);
        _activeRequest = request;
        using var cancellation = cancellationToken.Register(request.Cancel);
        _retryPlan = null;
        ApplyPresentation();
        SetStatus(plan.Destination?.IsSearch == true ? "正在搜索…" : plan.Session.Initialized ? "正在打开…" : "正在加载…", busy: true);
        var work = ExecuteModuleRequestAsync(plan, token);
        _ = ReapRequestAsync(request, work);
        try
        {
            var destination = await work.WaitAsync(OperationTimeout, _timeProvider, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(request))
            {
                if (reportOutcome) throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
                return;
            }
            if (destination is not null)
            {
                CommitDestination(plan, destination);
                if (destination.IsSearch)
                {
                    plan.Session.ActiveSearchKey = SearchKey(destination);
                    plan.Session.SearchDrafts[plan.Session.ActiveSearchKey] = destination.Value;
                    if (ReferenceEquals(plan.Session, _searchOwnerSession))
                    {
                        _suppressSearch = true;
                        try { SearchBox.Text = destination.Value; }
                        finally { _suppressSearch = false; }
                    }
                }
            }
            RefreshSuppliedViews(plan.Session);
            BindNavigation(plan.Session);
            SetStatus();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(request)) SetStatus("已取消");
            if (reportOutcome) throw;
        }
        catch (TimeoutException)
        {
            request.Cancel();
            if (IsCurrent(request))
            {
                _retryPlan = plan;
                SetStatus("操作超时", error: true, retry: true);
            }
            if (reportOutcome) throw;
        }
        catch (Exception error)
        {
            if (IsCurrent(request))
            {
                _retryPlan = plan;
                SetStatus(plan.Session.Module.OperationErrorText(error), error: true, retry: true);
            }
            if (reportOutcome) throw new InvalidOperationException("SHELL_NAVIGATION_FAILED");
        }
        finally
        {
            if (IsCurrent(request))
            {
                _activeRequest = null;
                ApplyPresentation();
            }
        }
    }

    private static async Task<Destination?> ExecuteModuleRequestAsync(RequestPlan plan, CancellationToken token)
    {
        var session = plan.Session;
        // The lease follows the actual module task, not the shell's timeout. A non-cooperative old
        // request can delay this module but can never overlap a replacement operation on it.
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
            if (plan.IsBack)
            {
                if (string.IsNullOrEmpty(destination.Location)) throw new InvalidOperationException("SHELL_NAVIGATION_LOCATION_MISSING");
                await session.Module.RestoreNavigationAsync(destination.Location, token);
            }
            else if (destination.IsSearch) await session.Module.SearchAsync(destination.Value, token);
            else await session.Module.NavigateAsync(destination.Value, token);
            token.ThrowIfCancellationRequested();
            if (!plan.IsBack)
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

    private void CommitDestination(RequestPlan plan, Destination destination)
    {
        var target = (plan.Session, destination);
        if (plan.IsBack && _history.Count > 1 && _history[^2] == target) _history.RemoveAt(_history.Count - 1);
        else if (_history.Count == 0 || _history[^1] != target)
        {
            _history.Add(target);
            if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        }
        plan.Session.Current = destination;
    }

    private async Task ReapRequestAsync(ShellRequest request, Task work)
    {
        await ShellRequest.ObserveAsync(work);
        await ShellRequest.ObserveAsync(request.Cancellation);
        _requests.Remove(request);
        request.Dispose();
        if (!_isClosing) UpdateNavigationRetention();
    }

    private bool IsCurrent(ShellRequest request) => !_isClosing && _currentPage is null && ReferenceEquals(_activeRequest, request) &&
        ReferenceEquals(_current, request.Plan.Session);

    private void CancelActive()
    {
        var request = _activeRequest;
        _activeRequest = null; // Retire UI ownership before invoking arbitrary cancellation registrations.
        request?.Cancel();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        CancelPageActivation();
        CancelActive();
        if (_currentPage is not null) _pageNavigation = new CancellationTokenSource();
        _retryPlan = null;
        ApplySearchPresentation(force: true);
        SetStatus("已取消");
        RestoreNavigationSelection();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_retryPage is { } page) { RunUiAction(() => ActivatePageAsync(page)); return; }
        if (_retryPlan is { } plan) RunUiAction(() => RunRequestAsync(plan));
    }

    private void OnDismissStatusClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        _retryPlan = null;
        SetStatus();
    }

    private void SetStatus(string message = "", bool busy = false, bool error = false, bool retry = false)
    {
        _state.SetStatus(message, busy, error, retry);
        if (StatusText.IsLoaded && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void RunUiAction(Func<Task> action)
    {
        if (!_isClosing) _ = RunUiActionCoreAsync(action);
    }

    private async Task RunUiActionCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_isClosing) SetStatus("已取消"); }
        catch (Exception) { if (!_isClosing) SetStatus("暂时无法完成操作", error: true); }
    }

    private void QueueUi(Action action)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_isClosing) return;
                try { action(); }
                catch (Exception) { SetStatus("暂时无法完成操作", error: true); }
            }, DispatcherPriority.DataBind);
        }
        catch (InvalidOperationException) { /* The dispatcher is already closing. */ }
    }

    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
            QueueUi(() =>
            {
                DesktopTheme.SetIsHighContrast(this, SystemParameters.HighContrast);
                ApplyPresentation();
            });
    }

    private void UpdateCaption()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        var label = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, label);
        UpdatePlayerDetailChrome();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || _sessionEnding || e.Cancel) return;
        e.Cancel = true;
        if (_isClosing || ResetDialog.IsOpen) return;
        if (_closePreferences is not null)
        {
            QueueCloseChoice();
            return;
        }
        BeginShutdown();
    }

    private void BeginShutdown()
    {
        if (_isClosed || (_isClosing && !(_userDataResetStarted && _sessionEnding)) ||
            (ResetDialog.IsOpen && !_sessionEnding)) return;
        // OS session ending retains its existing best-effort shutdown path.
        // Ordinary close/tray exit cannot compete with a confirmed reset.
        if (_sessionEnding) ResetDialog.Visibility = Visibility.Collapsed;
        DismissCloseChoice(restoreFocus: false);
        _isClosing = true;
        _state.IsInteractive = false;
        _retryPlan = null;
        RetireHeaderPresentation();
        SetStatus("正在关闭…");
        CancelActive();
        CancelPageActivation();
        foreach (var request in _requests.ToArray()) request.Cancel();
        _ = EnsureShutdownAsync();
    }

    private async Task RequestFinalCloseAsync()
    {
        if (_closeQueued || _isClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _closeQueued = true;
        _state.SetShutdownConfirmation(_state.IsShutdownConfirmationOpen);
        try
        {
            // Even a synchronously completed callback must not re-enter Close from inside OnClosing.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_isClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            _allowClose = true;
            Close();
            if (!_isClosed)
            {
                // A caller can still veto the final Closing event; never restart disposal.
                _allowClose = false;
                _state.SetShutdownConfirmation(true, canWait: true, canForce: _forceShutdown is not null);
            }
        }
        catch (OperationCanceledException) { /* Dispatcher teardown can cancel the deferred close. */ }
        catch (InvalidOperationException) { /* A forced application shutdown may already have closed the window. */ }
        finally { _closeQueued = false; }
    }

    private Task EnsureShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            try
            {
                // UI-affine disposal begins on this dispatcher. The callback must return promptly;
                // no in-process deadline can safely preempt synchronous dispatcher blocking.
                _shutdownTask = _shutdown() ?? Task.FromException(new InvalidOperationException());
            }
            catch (Exception) { _shutdownTask = Task.FromException(new InvalidOperationException()); }
            _ = ShellRequest.ObserveAsync(_shutdownTask);
            _ = CompleteShutdownAsync(initialAttempt: true);
        }
        return _shutdownCompletion.Task;
    }

    private async Task CompleteShutdownAsync(bool initialAttempt)
    {
        if (_shutdownWaitInProgress || _shutdownTask is null) return;
        _shutdownWaitInProgress = true;
        var succeeded = false;
        var timedOut = false;
        try
        {
            // Keep the bounded completion signal independent of dispatcher teardown on forced Closed.
            await _shutdownTask.WaitAsync(ShutdownTimeout, _timeProvider).ConfigureAwait(false);
            succeeded = true;
        }
        catch (TimeoutException)
        {
            // Distinguish the wait deadline from a callback that itself throws TimeoutException.
            // A successful completion racing the deadline still requires an explicit new wait.
            timedOut = !_shutdownTask.IsFaulted && !_shutdownTask.IsCanceled;
        }
        catch (Exception) { /* A fault/cancellation is not proof of saved data or released resources. */ }
        finally
        {
            if (initialAttempt) _shutdownCompletion.TrySetResult();
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            if (Dispatcher.CheckAccess()) ApplyShutdownOutcome(succeeded, timedOut);
            else await Dispatcher.InvokeAsync(() => ApplyShutdownOutcome(succeeded, timedOut),
                DispatcherPriority.Normal).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Dispatcher teardown can reject the UI-only result. */ }
        catch (InvalidOperationException) { /* An externally forced shutdown already owns exit. */ }
    }

    private void ApplyShutdownOutcome(bool succeeded, bool timedOut)
    {
        _shutdownWaitInProgress = false;
        // Forced exit fences every late result, including a successful callback after the decision.
        if (_isClosed || _allowClose || _closeQueued || _forceStarted || _sessionEnding) return;
        if (succeeded)
        {
            ShutdownSucceeded = true;
            CleanupCompleted = true;
            _ = RequestFinalCloseAsync();
            return;
        }
        SetStatus(timedOut ? "关闭超时" : "关闭失败", error: true);
        _state.SetShutdownConfirmation(true, canWait: timedOut, canForce: _forceShutdown is not null,
            canCancel: !timedOut && _cancelShutdown is not null && !_forceStarted);
        if (timedOut) WaitForShutdownButton.Focus();
        else ShutdownConfirmation.Focus(); // Enter must never implicitly choose destructive exit.
    }

    private void OnWaitForShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!_isClosing || _isClosed || _closeQueued || !_state.CanWaitForShutdown || _shutdownWaitInProgress) return;
        _state.SetShutdownConfirmation(true, canForce: _forceShutdown is not null);
        SetStatus("正在等待关闭…", busy: true);
        // A new bounded wait on the same task, not a second shutdown callback. No automatic retries.
        _ = CompleteShutdownAsync(initialAttempt: false);
    }

    private void OnForceExitClick(object sender, RoutedEventArgs e)
    {
        if (!_isClosing || _isClosed || _closeQueued || !_state.CanForceExit || _forceShutdown is null) return;
        _ = ForceShutdownAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposePresentationMotion();
        _isClosed = true;
        _isClosing = true;
        DismissCloseChoice(restoreFocus: false);
        _trayIcon?.Dispose();
        _trayIcon = null;
        CancelActive();
        CancelPageActivation();
        foreach (var request in _requests.ToArray()) request.Cancel();
        SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
        foreach (var session in _sessions)
        {
            session.Module.PropertyChanged -= OnModulePropertyChanged;
            if (session.Navigation is not null) session.Navigation.CollectionChanged -= OnNavigationChanged;
        }
        foreach (var (source, handler) in _navigationSubscriptions) source.NavigationRequested -= handler;
        _navigationSubscriptions.Clear();
        RetireHeaderPresentation();
        DisposeGlobalSurfaces();
        _nativeCaption?.Dispose();
        _nativeCaption = null;
        // A confirmed reset is already released by the App's abort/replacement path.
        // Only ordinary/OS closure shares the once-only normal shutdown attempt.
        if (!_userDataResetStarted || _sessionEnding) _ = EnsureShutdownAsync();
        else _shutdownCompletion.TrySetResult();
        base.OnClosed(e);
    }
}
