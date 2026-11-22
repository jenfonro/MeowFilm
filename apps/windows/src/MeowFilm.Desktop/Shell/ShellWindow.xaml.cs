using System.Collections.Specialized;
using System.ComponentModel;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow : DesktopWindow
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    private readonly ShellState _state = new();
    private readonly ShellNavigationHistory _history = new();
    private readonly ShellSearchCoordinator _search;
    private readonly ShellCloseCoordinator _closeCoordinator;
    private readonly ShellOwnedNoticeBridge _ownedNotices;
    private readonly ShellSurfaceMotion _motion = new();
    private readonly List<ModuleSession> _sessions;
    private readonly Func<Task> _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<ShellRequest> _requests = [];
    private readonly List<(IDesktopNavigationSource Source, EventHandler<DesktopNavigationRequestEventArgs> Handler)> _navigationSubscriptions = [];
    private readonly List<(IDesktopSidebarSource Source, EventHandler Handler)> _sidebarSubscriptions = [];
    private readonly object _moduleChangesGate = new();
    private readonly Dictionary<IDesktopModule, (bool Navigation, bool Presentation, bool Player)> _moduleChanges = new(ReferenceEqualityComparer.Instance);
    private bool _moduleChangesQueued;
    private TaskCompletionSource _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ShellDebugAdapter? _debugAdapter;
    private ModuleSession? _current, _playerSession;
    private ShellRequest? _activeRequest;
    private RequestPlan? _retryPlan;
    private NativeCaption? _nativeCaption;
    private bool _suppressSelection;
    private bool _loaded;
    private bool _isClosing;
    private bool _allowClose;
    private bool _isClosed;
    private bool _closeQueued;
    private Task? _shutdownTask;
    private int _shutdownStarted;
    private long _presentationGeneration;

    public ShellWindow(IReadOnlyList<IDesktopModule> modules, Func<Task> shutdown)
        : this(modules, shutdown, TimeProvider.System)
    {
    }

    internal ShellWindow(IReadOnlyList<IDesktopModule> modules, Func<Task> shutdown, TimeProvider timeProvider)
        : this(modules, [], shutdown, timeProvider) { }

    public ShellWindow(IReadOnlyList<IDesktopModule> modules, IReadOnlyList<IDesktopPage> pages,
        Func<Task> shutdown,
        IDesktopAppearance? appearance = null, IDesktopClosePreferences? closePreferences = null,
        IDesktopMediaControlSettings? mediaControls = null, Func<Task>? resetUserData = null)
        : this(modules, pages, shutdown, TimeProvider.System, appearance,
            closePreferences, mediaControls: mediaControls, resetUserData: resetUserData) { }

    internal ShellWindow(IReadOnlyList<IDesktopModule> modules, IReadOnlyList<IDesktopPage> pages,
        Func<Task> shutdown, TimeProvider timeProvider,
        IDesktopAppearance? appearance = null, IDesktopClosePreferences? closePreferences = null,
        Func<DesktopWindow, Action, Action, Action, IDesktopTrayIcon>? trayIconFactory = null,
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
        _pages = pages.Select(page => new ShellPageSession(page)).ToArray();
        _shutdown = shutdown;
        _resetUserData = resetUserData;
        MediaControls = mediaControls;
        InitializeComponent();
        Bindings?.Initialize();
        AttachRoot();
        _search = new ShellSearchCoordinator(SearchBox, SearchTargetPicker, SearchInput, SearchDivider, () => _currentPage, () => _activeRequest);
        _closeCoordinator = new ShellCloseCoordinator(this, closePreferences, trayIconFactory);
        _ownedNotices = new ShellOwnedNoticeBridge(Notices, QueueUi);
        SessionEnding += (_, _) => PrepareForSessionEnding();
        Root.Loaded += OnLoaded;
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        Root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnPreviewKeyUp), true);
        Root.PointerPressed += OnChannelHeaderPointerPressed;
        Root.PointerReleased += OnChannelHeaderPointerReleased;
        Root.GotFocus += OnGotKeyboardFocus;
        PlayerSurfaceInput.AddDismissRequestedHandler(Root, OnQueueDismissClick);
        ShellWindowStartupSize.ApplyToPrimaryScreen(this, Handle);
        _nativeCaption = new(
            this,
            CaptionDragRegion,
            WindowCaptionButtons,
            () => _playerDetailOpen,
            [BackButton, ForwardButton, SearchInput, PageHeaderHost, CaptionBar],
            [WindowCaptionButtons, PlayerDetailBackButton, ChannelDialogHost, CloseDialog, ResetDialog, NotificationOverlay]);
        _closeCoordinator.InitializeTrayIcon();
        ChannelDialogHost.Attach(this);
        ChannelDialogHost.OpenStateChanged += (_, _) => _nativeCaption?.Refresh();
        Activated += (_, _) => UpdateCaption();
        Root.ActualThemeChanged += (_, _) => UpdateCaption();
        InitializeGeneralSettings(appearance, closePreferences);
        CloseDialog.ChoiceRequested += _closeCoordinator.OnCloseChoiceRequested;
        CloseDialog.CancelRequested += _closeCoordinator.CancelCloseChoice;
        ResetDialog.ConfirmRequested += OnUserDataResetConfirmed;
        ResetDialog.CancelRequested += CancelUserDataReset;
        ResetDialog.ExitRequested += OnUserDataResetExit;
        NoticeOutlet.SetReceiver(Root, notice => Notices.Show(notice.Identity, notice.Message));
        _state.PropertyChanged += (_, _) => UpdateStatusPresentation();
        SystemAppearance.Changed += OnSystemAppearanceChanged;
        ApplyAccessibilityPresentation();
        foreach (var session in _sessions)
        {
            session.Module.PropertyChanged += OnModulePropertyChanged;
            _ownedNotices.Subscribe(session.Module);
            SubscribeGlobalSurfaces(session);
            if (session.Module is IDesktopNavigationSource source)
            {
                EventHandler<DesktopNavigationRequestEventArgs> handler = (_, args) => OnNavigationRequested(session, args);
                source.NavigationRequested += handler;
                _navigationSubscriptions.Add((source, handler));
            }
            if (session.Module is IDesktopSidebarSource sidebar)
            {
                EventHandler handler = (_, _) => QueueUi(() =>
                {
                    if (!_isClosing && ReferenceEquals(_current, session) && _currentPage is null)
                        OnSettingsClick(this, new RoutedEventArgs());
                });
                sidebar.SettingsRequested += handler;
                _sidebarSubscriptions.Add((sidebar, handler));
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

    public Task ShutdownCompletion => _shutdownCompletion.Task;
    public IDesktopMediaControlSettings? MediaControls { get; }
    public IDebugShellHost DebugHost => _debugAdapter ??= new ShellDebugAdapter(this);
    public bool ShutdownSucceeded { get; private set; }
    public bool CleanupCompleted { get; private set; }
    public bool IsHiddenToTray => _closeCoordinator.IsHiddenToTray;

    internal ShellState State => _state;
    internal ShellNavigationHistory History => _history;
    internal ShellSearchCoordinator SearchCoordinator => _search;
    internal ShellCloseCoordinator CloseCoordinator => _closeCoordinator;
    internal ShellOwnedNoticeBridge OwnedNotices => _ownedNotices;
    internal IReadOnlyList<ModuleSession> Sessions => _sessions;
    internal IReadOnlyList<ShellPageSession> Pages => _pages;
    internal ModuleSession? CurrentSession => _current;
    internal ShellPageSession? CurrentPage => _currentPage;
    internal ModuleSession? PlayerDetailSession => _playerDetailSession;
    internal ModuleSession? QueueSession => _queueSession;
    internal NativeCaption? NativeCaption => _nativeCaption;
    internal ContentControl GeneralSettingsHost => _generalSettingsHost;
    internal bool IsLoaded => _loaded;
    internal bool IsClosing => _isClosing;
    internal bool IsClosed => _isClosed;
    internal bool IsRequestBusy => _state.IsBusy || _activeRequest is not null || _pageRequest is not null;
    internal bool IsGeneralSettingsOpen => _generalSettingsOpen;
    internal bool IsPlayerDetailOpen => _playerDetailOpen;
    internal bool IsQueueOpen => _queueOpen;
    internal Grid HeaderChromeElement => HeaderChrome;
    internal Grid SearchInputElement => SearchInput;
    internal TextBox SearchBoxElement => SearchBox;
    internal ShellSearchScope SearchTargetPickerElement => SearchTargetPicker;
    internal StackPanel WindowCaptionButtonsElement => WindowCaptionButtons;
    internal Border SidebarBackgroundElement => SidebarBackground;
    internal ContentControl PlayerHostElement => PlayerHost;
    internal DesktopModalHost ChannelDialogHostElement => ChannelDialogHost;
    internal CloseWindowDialog CloseDialogElement => CloseDialog;
    internal UserDataResetDialog ResetDialogElement => ResetDialog;
    internal NoticeStack NoticesElement => Notices;

    public void RestoreFromTray() => _closeCoordinator.RestoreFromTray();
    public void PrepareForSessionEnding() => _closeCoordinator.PrepareForSessionEnding();

    internal void SetModalGatesEnabled(bool enabled) =>
        RootInteractionGate.IsEnabled = NotificationInteractionGate.IsEnabled = enabled;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        _closeCoordinator.OnWindowStateChanged(WindowState);
        UpdateCaption();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Bindings?.Update();
        if (_loaded || _isClosing) return;
        _loaded = true;
        RunUiAction(() => ActivateAsync(_current ?? _sessions[0]));
    }

    private void OnPresentationSizeChanged(object sender, SizeChangedEventArgs args) =>
        PresentationSurface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };

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

    internal void PreparePresentation(ModuleSession session)
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
        ApplyPresentation();
        Mount(session);
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
        if (!view.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException();
        if (!ReferenceEquals(host.Content, view) &&
            VisualTreeHelper.GetParent(view) is not null)
            throw new InvalidOperationException();
    }

    private void BindNavigation(ModuleSession session)
    {
        var navigation = session.Module.Navigation ?? throw new InvalidOperationException();
        if (ReferenceEquals(session.Navigation, navigation)) return;
        if (session.Navigation is not null) session.Navigation.CollectionChanged -= OnNavigationChanged;
        session.Navigation = navigation;
        navigation.CollectionChanged += OnNavigationChanged;
        SynchronizeNavigation(session);
    }

    internal void ApplyPresentation()
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
        ApplyNavigationOwner(current);
        var sidebar = (current.Module as IDesktopSidebarSource)?.SidebarView;
        if (!ReferenceEquals(SidebarContributionHost.Content, sidebar))
        {
            SidebarContributionHost.Content = null;
            if (sidebar is not null) ValidateView(sidebar, SidebarContributionHost);
            SidebarContributionHost.Content = sidebar;
        }
        ApplyModuleAccent(current);
        ApplyHeaderPresentation(current);
        ApplyGlobalSurfacePresentation(current);
        bool immersive = _currentPage is null && !current.IsSettingsOpen &&
            current.Module is IDesktopImmersiveSource { IsImmersive: true };
        RootLayout.ColumnDefinitions[0].Width = new(immersive ? 0 : 216);
        PageHeaderHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarBackground.Visibility = Brand.Visibility = SidebarScroll.Visibility =
            immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarEdge.Visibility = immersive || SystemAppearance.IsHighContrast ? Visibility.Collapsed : Visibility.Visible;
        _state.CanGoBack = _queueOpen || _playerDetailOpen || _currentPage is not null ||
            current.IsSettingsOpen || _history.CanGoBack;
        _state.CanGoForward = !_queueOpen && !_playerDetailOpen &&
            !current.IsSettingsOpen && _history.CanGoForward;
        _search.ApplyPresentation();
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
            _history.CollectRetainedLocations(session, locations);
            if (_currentPage is null && ReferenceEquals(_current, session)) Retain(session.Current);
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

        PlayerFrame.Visibility = PlayerHost.Content is not null &&
            !(_currentPage is null && !current.IsSettingsOpen && current.Module is IDesktopImmersiveSource { IsImmersive: true })
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateCaption();
    }

    private void ApplyModuleAccent(ModuleSession current)
    {
        string[] keys = ["MeowFilm.Brush.Accent", "MeowFilm.Brush.AccentHover", "MeowFilm.Brush.OnAccent", "MeowFilm.Brush.AccentSoft"];
        var resources = (current.MainHost.Content as FrameworkElement ?? current.Module.View)?.Resources;
        foreach (var key in keys)
        {
            if (resources?.ContainsKey(key) == true && resources[key] is Brush brush)
            {
                if (!RootLayout.Resources.ContainsKey(key) || !ReferenceEquals(RootLayout.Resources[key], brush))
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
            ModuleNavigationList.SelectedItem = route is not null && route == current.Module.Navigation.FirstOrDefault()?.Route
                ? current : null;
            if (ModuleNavigationList.SelectedItem is not null) selected = null;
            NavigationList.SelectedItem = selected?.Group is null ? selected : null;
            CollectionNavigationList.SelectedItem = selected?.Group is not null ? selected : null;
        }
        finally { _suppressSelection = false; }
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _isClosing || (!ReferenceEquals(sender, NavigationList) && !ReferenceEquals(sender, CollectionNavigationList)) || _current is not { } current) return;
        if (sender is not ListView { SelectedItem: ShellNavigationEntry item }) return;
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
            if (!MayNavigate(session, args.FromPlayer) || generation != Interlocked.Read(ref _presentationGeneration)) return;
            RunUiAction(() => NavigateFromUiAsync(session, route));
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

    internal Task NavigateFromUiAsync(ModuleSession session, string route)
    {
        if (_isClosing) return Task.CompletedTask;
        if (DebugLog.IsEnabled)
            DebugLog.Info(DebugCategories.UiNav, "shell", "navigate",
                new { moduleId = session.Module.Id, route });
        _settingsReturn = null;
        if (_current is { } previous) previous.IsSettingsOpen = false;
        PreparePresentation(session);
        LeavePublicPage();
        if (_activeRequest is { } active && ReferenceEquals(active.Plan.Session, session) &&
            active.Plan.Destination is { IsSearch: false } pending && string.Equals(pending.Value, route, StringComparison.Ordinal))
            return Task.CompletedTask;
        session.IsSettingsOpen = false;
        return RunRequestAsync(new(session, new(route)));
    }

    private void OnNavigationChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueUi(() =>
    {
        foreach (var session in _sessions.Where(session => ReferenceEquals(session.Navigation, sender)))
            SynchronizeNavigation(session);
    });

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IDesktopModule module || _isClosing || Dispatcher.IsClosing) return;
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
        try
        {
            _ = Dispatcher.EnqueueAsync(() =>
            {
                try { ApplyModuleChanges(); }
                catch (Exception) { if (!_isClosing) SetStatus("暂时无法完成操作", error: true); }
            }, DispatcherQueuePriority.Normal);
        }
        catch (InvalidOperationException) { }
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
        if (_isClosing) return;
        var refreshPresentation = false;
        var refreshPlayer = false;
        foreach (var change in changes)
        {
            var session = _sessions.FirstOrDefault(item => ReferenceEquals(item.Module, change.Key));
            if (session is null) continue;
            if (change.Value.Navigation) BindNavigation(session);
            refreshPresentation |= ReferenceEquals(session, _current) && change.Value.Presentation;
            refreshPlayer |= change.Value.Player && (ReferenceEquals(session, _current) || ReferenceEquals(session, _playerSession));
        }
        if (refreshPresentation) ApplyPresentation();
        else if (refreshPlayer && _current is { } current)
        { ApplyPlayerPresentation(current); ApplyGlobalSurfacePresentation(current); }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => RunUiAction(async () =>
    {
        if (_current is not { } current) return;
        if (_playerDetailOpen) await ClosePlayerDetailAsync();
        if (current.IsSettingsOpen && _currentPage is null) { ApplyPresentation(); return; }
        var leftPublicPage = _currentPage is not null;
        LeavePublicPage();
        CancelActive();
        if (!leftPublicPage) RetireHeaderPresentation();
        if (DesktopVisual.ContainsFocus(current.MainHost)) current.ContentFocus = DesktopVisual.Focused(Root);
        _settingsReturn = current;
        current.IsSettingsOpen = true;
        _generalSettingsOpen = _generalSettingsHost.Content is not null;
        _retryPlan = null;
        SetStatus();
        ApplyPresentation();
        QueueUi(() =>
        {
            if (ReferenceEquals(_current, current) && current.IsSettingsOpen)
                DesktopVisual.FocusFirst(_generalSettingsOpen ? _generalSettingsHost : current.SettingsHost);
        });
        if (!_generalSettingsOpen && !current.Initialized) await RunRequestAsync(new(current));
    });

    internal void EnterGeneralSettings()
    {
        var current = _current ?? _sessions[0];
        if (!current.IsSettingsOpen || _currentPage is not null)
        {
            LeavePublicPage();
            CancelActive();
            RetireHeaderPresentation();
            _settingsReturn = current;
            current.IsSettingsOpen = true;
        }
        _generalSettingsOpen = _generalSettingsHost.Content is not null;
        SetStatus();
        ApplyPresentation();
    }

    internal async Task EnterModuleSettingsAsync(ModuleSession targetSession)
    {
        var current = _current ?? _sessions[0];
        if (!current.IsSettingsOpen || _currentPage is not null)
        {
            LeavePublicPage();
            CancelActive();
            RetireHeaderPresentation();
            _settingsReturn = current;
            current.IsSettingsOpen = true;
        }
        _generalSettingsOpen = false;
        if (!ReferenceEquals(_current, targetSession))
        {
            if (_current is { } prev) prev.IsSettingsOpen = false;
            targetSession.IsSettingsOpen = true;
            PreparePresentation(targetSession);
        }
        else
        {
            targetSession.IsSettingsOpen = true;
        }
        SetStatus();
        ApplyPresentation();
        if (!targetSession.Initialized) await RunRequestAsync(new(targetSession));
    }

    internal async Task PrepareModuleTargetAsync(ModuleSession session)
    {
        _generalSettingsOpen = false;
        if (_current is { } prev) prev.IsSettingsOpen = false;
        session.IsSettingsOpen = false;
        LeavePublicPage();
        PreparePresentation(session);
        if (!session.Initialized) await RunRequestAsync(new(session));
    }

    internal void CommitDebugDestination(ModuleSession session)
    {
        var location = session.Module.CaptureNavigationLocation();
        if (string.IsNullOrEmpty(location)) return;
        var destination = new Destination(location, Location: location);
        _history.Commit(new(session, destination), destination);
        ApplyPresentation();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => RunUiAction(GoBackAsync);
    private void OnForwardClick(object sender, RoutedEventArgs e) => RunUiAction(GoForwardAsync);

    internal async Task GoForwardAsync()
    {
        if (!_state.CanGoForward) return;
        var next = _history.PeekForward();
        await RestoreHistoryAsync(new(next.Session, next.Destination, IsForward: true));
    }

    private async Task RestoreHistoryAsync(RequestPlan plan)
    {
        if (plan.Destination?.PageId is { } pageId)
        {
            var page = _pages.Single(item => item.Page.Id == pageId);
            PreparePresentation(plan.Session);
            await ActivatePageAsync(page, navigation: plan);
            return;
        }
        PreparePresentation(plan.Session);
        await RunRequestAsync(plan);
    }

    internal async Task GoBackAsync()
    {
        if (_queueOpen) { await CloseQueueAsync(); return; }
        if (_playerDetailOpen) { await ClosePlayerDetailAsync(); return; }
        if (_current is not { } current) return;
        if (_currentPage is not null && !_history.CanGoBack)
        {
            LeavePublicPage();
            SetStatus();
            ApplyPresentation();
            return;
        }
        if (_currentPage is not null)
        {
            var previous = _history.PeekBack();
            await RestoreHistoryAsync(new(previous.Session, previous.Destination, IsBack: true));
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
            if (_history.LastPageId is { } pageId)
            {
                await ActivatePageAsync(_pages.Single(page => page.Page.Id == pageId), recordNavigation: false);
                return;
            }
            QueueUi(() =>
            {
                if (!ReferenceEquals(_current, restore) || restore.IsSettingsOpen) return;
                if (restore.ContentFocus is Control { Visibility: Visibility.Visible, IsEnabled: true, IsTabStop: true } focus)
                    focus.Focus(FocusState.Programmatic);
                else DesktopVisual.FocusFirst(restore.MainHost);
            });
            if (!restore.Initialized) await RunRequestAsync(new(restore));
            return;
        }
        if (_history.CanGoBack)
        {
            var previous = _history.PeekBack();
            await RestoreHistoryAsync(new(previous.Session, previous.Destination, IsBack: true));
        }
    }

    private void OnGotKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (_current is { } current && DesktopVisual.ContainsFocus(current.MainHost))
            current.ContentFocus = DesktopVisual.Focused(Root);
    }

    private void OnSearchCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _search.OnCompositionStarted();
    private void OnSearchCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _search.OnCompositionEnded();
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _search.OnTextChanged();

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Enter) return;
        e.Handled = TrySubmitSearch(DesktopVisual.Modifiers, _search.IsComposing);
    }

    internal bool TrySubmitSearch(VirtualKeyModifiers modifiers, bool composing)
    {
        if (_isClosing || composing || modifiers != VirtualKeyModifiers.None || !SearchBox.IsEnabled) return false;
        RunUiAction(SearchAsync);
        return true;
    }

    private Task SearchAsync()
    {
        if (!_isClosing && _current is { IsSettingsOpen: false } && _search.SearchPage is { } searchPage &&
            SearchBox.IsEnabled && !string.IsNullOrWhiteSpace(SearchBox.Text) &&
            SearchTargetPicker.SelectedValue is string target)
        {
            string query = SearchBox.Text.Trim();
            var page = _pages.Single(item => ReferenceEquals(item.Page, searchPage));
            page.SearchDraft = SearchBox.Text;
            return ActivatePageAsync(page, token => searchPage.SubmitAsync(query, target, token));
        }
        if (_current is not { } current || current.IsSettingsOpen ||
            _search.OwnerSession is not { } owner || !SearchBox.IsEnabled ||
            string.IsNullOrWhiteSpace(SearchBox.Text)) return Task.CompletedTask;
        owner.SearchDrafts[owner.ActiveSearchKey] = SearchBox.Text;
        owner.IsSettingsOpen = false;
        _settingsReturn = null;
        if (_currentPage is not null || !ReferenceEquals(current, owner))
            PreparePresentation(owner);
        return RunRequestAsync(new(owner, new(SearchBox.Text.Trim(), IsSearch: true)));
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (CloseDialog.IsOpen || ResetDialog.IsOpen) return;
        if (_isClosing) return;
        if (ChannelDialogHost.IsOpen)
        {
            if (!ChannelDialogHost.HandleEscape(e)) ProtectChannelKeyboard(e);
            if (e.Key is VirtualKey.F or VirtualKey.K && DesktopVisual.Modifiers == VirtualKeyModifiers.Control ||
                e.Key is VirtualKey.Left or VirtualKey.Right && DesktopVisual.Modifiers == VirtualKeyModifiers.Menu)
                e.Handled = true;
            return;
        }
        e.Handled = TryExecuteShortcut(e.Key, DesktopVisual.Modifiers);
    }

    internal bool TryExecuteShortcut(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (_isClosing || CloseDialog.IsOpen || ResetDialog.IsOpen || ChannelDialogHost.IsOpen) return false;
        if (key == VirtualKey.Escape && DesktopVisual.Focused(Root) is Slider) return false;
        if (key is VirtualKey.F or VirtualKey.K && modifiers == VirtualKeyModifiers.Control &&
            SearchInput.Visibility == Visibility.Visible && SearchBox.IsEnabled)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
            return true;
        }
        if (key == VirtualKey.Left && modifiers == VirtualKeyModifiers.Menu)
        {
            RunUiAction(GoBackAsync);
            return true;
        }
        if (key == VirtualKey.Right && modifiers == VirtualKeyModifiers.Menu)
        {
            RunUiAction(GoForwardAsync);
            return true;
        }
        if (key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None && _state.CanCancel)
        {
            OnCancelClick(this, new RoutedEventArgs());
            return true;
        }
        if (key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None &&
            (_queueOpen || _playerDetailOpen))
        {
            RunUiAction(GoBackAsync);
            return true;
        }
        return false;
    }

    private void ProtectChannelKeyboard(KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject target ||
            !DesktopVisual.Contains(ChannelDialogHost, target)) e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!CloseDialog.IsOpen && !ResetDialog.IsOpen && ChannelDialogHost.IsOpen)
            ProtectChannelKeyboard(e);
    }

    private void OnChannelHeaderPointerPressed(object sender, PointerRoutedEventArgs e) =>
        OnChannelHeaderPointer(e, pressed: true);

    private void OnChannelHeaderPointerReleased(object sender, PointerRoutedEventArgs e) =>
        OnChannelHeaderPointer(e, pressed: false);

    private void OnChannelHeaderPointer(PointerRoutedEventArgs e, bool pressed)
    {
        if (CloseDialog.IsOpen || ResetDialog.IsOpen || !ChannelDialogHost.IsOpen) return;
        bool In(FrameworkElement control) => e.OriginalSource is DependencyObject target && DesktopVisual.Contains(control, target);
        if (ChannelDialogHost.HasOutsidePress || In(SearchChrome) || In(SearchInput) || In(SettingsButton) ||
            In(PlayerDetailBackButton) || In(PageHeaderHost)) ChannelDialogHost.HandleOutsidePointer(e, RootLayout, pressed);
    }

    internal async Task RunRequestAsync(RequestPlan plan, CancellationToken cancellationToken = default, bool reportOutcome = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isClosing || _currentPage is not null || !ReferenceEquals(_current, plan.Session))
        {
            if (reportOutcome) throw new OperationCanceledException("SHELL_NAVIGATION_STALE");
            return;
        }
        CancelActive();
        RetireHeaderPresentation();
        if (plan.Destination is { } target) _search.PrepareDestination(plan.Session, target);
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
                _history.Commit(plan, destination);
                _search.CommitSearchDestination(plan.Session, destination);
            }
            RefreshSuppliedViews(plan.Session);
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
        if (!_isClosing) UpdateNavigationRetention();
    }

    private bool IsCurrent(ShellRequest request) => !_isClosing && _currentPage is null && ReferenceEquals(_activeRequest, request) &&
        ReferenceEquals(_current, request.Plan.Session);

    internal void CancelActive()
    {
        var request = _activeRequest;
        _activeRequest = null;
        request?.Cancel();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        CancelPageActivation();
        CancelActive();
        if (_currentPage is not null) _pageNavigation = new CancellationTokenSource();
        _retryPlan = null;
        _search.ApplyPresentation(force: true);
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

    internal void SetStatus(string message = "", bool busy = false, bool error = false, bool retry = false)
    {
        if (DebugLog.IsEnabled && (message.Length > 0 || error))
            DebugLog.Write(error ? "warn" : "debug", DebugCategories.UiNav, "shell.status",
                "status.changed", new { message, busy, error, retry });
        _state.SetStatus(message, busy, error, retry);
        if (StatusText.IsLoaded && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            FrameworkElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
        if (_isClosing || Dispatcher.IsClosing) return;
        try
        {
            _ = Dispatcher.EnqueueAsync(() =>
            {
                if (_isClosing) return;
                try { action(); }
                catch (Exception) { SetStatus("暂时无法完成操作", error: true); }
            }, DispatcherQueuePriority.Normal);
        }
        catch (InvalidOperationException) { }
    }

    private void OnSystemAppearanceChanged(object? sender, EventArgs args) => QueueUi(() =>
    {
        ApplyAccessibilityPresentation();
        ApplyPresentation();
    });

    private void ApplyAccessibilityPresentation()
    {
        bool highContrast = SystemAppearance.IsHighContrast;
        DesktopTheme.SetIsHighContrast(Root, highContrast);
        var background = (Brush)DesktopVisual.Resource(Root,
            highContrast ? "MeowFilm.Brush.Window" : "MeowFilm.Brush.AppBackground");
        WindowFrame.Background = ApplicationBackground.Background = background;
        SidebarMaterial.Visibility = SidebarTint.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        SidebarEdge.Visibility = highContrast || SidebarBackground.Visibility == Visibility.Collapsed
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateCaption();
        UpdateStatusPresentation();
    }

    private void OnMinimizeCaptionClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void OnMaximizeCaptionClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter) return;
        if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            presenter.Restore();
        else
            presenter.Maximize();
    }

    private void OnCloseCaptionClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateCaption()
    {
        bool maximized = WindowState == DesktopWindowState.Maximized;
        MaximizeCaptionButton.Content = maximized ? "\uE923" : "\uE922";
        AutomationProperties.SetName(MaximizeCaptionButton, maximized ? "向下还原" : "最大化");
        WindowCaptionButtons.RequestedTheme = _playerDetailOpen ? ElementTheme.Dark : ElementTheme.Light;
        WindowCaptionButtons.Opacity = IsActive ? 1.0 : 0.68;
        _nativeCaption?.Refresh();
    }

    private void UpdateStatusPresentation()
    {
        StatusPanel.Visibility = _state.HasStatus && !_state.IsBusy
            ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Background = (Brush)DesktopVisual.Resource(Root,
            _state.IsError ? "MeowFilm.Brush.ErrorSurface" : "MeowFilm.Brush.FloatingSurface");
        StatusPanel.BorderBrush = (Brush)DesktopVisual.Resource(Root,
            _state.IsError ? "MeowFilm.Brush.Error" : "MeowFilm.Brush.Border");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || _closeCoordinator.IsSessionEnding || e.Cancel) return;
        e.Cancel = true;
        if (_isClosing || ResetDialog.IsOpen) return;
        if (_closeCoordinator.HasPreferences)
        {
            _closeCoordinator.QueueCloseChoice();
            return;
        }
        BeginShutdown();
    }

    internal void BeginShutdown()
    {
        if (_isClosed || (_isClosing && !(_userDataResetStarted && _closeCoordinator.IsSessionEnding)) ||
            (ResetDialog.IsOpen && !_closeCoordinator.IsSessionEnding)) return;
        if (_closeCoordinator.IsSessionEnding) ResetDialog.Visibility = Visibility.Collapsed;
        _closeCoordinator.DismissCloseChoice(restoreFocus: false);
        _isClosing = true;
        _state.IsInteractive = false;
        _retryPlan = null;
        RetireHeaderPresentation();
        CancelActive();
        CancelPageActivation();
        foreach (var request in _requests.ToArray()) request.Cancel();
        _ = EnsureShutdownAsync();
        _ = RequestFinalCloseAsync();
    }

    private async Task RequestFinalCloseAsync()
    {
        if (_closeQueued || _isClosed || Dispatcher.IsClosing) return;
        _closeQueued = true;
        try
        {
            await OwnerDispatcher.YieldAsync(DispatcherQueuePriority.Low);
            if (_isClosed || Dispatcher.IsClosing) return;
            _allowClose = true;
            Close();
            if (!_isClosed) _allowClose = false;
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        finally { _closeQueued = false; }
    }

    private Task EnsureShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            try
            {
                _shutdownTask = _shutdown() ?? Task.FromException(new InvalidOperationException());
            }
            catch (Exception error) { _shutdownTask = Task.FromException(error); }
            _ = ShellRequest.ObserveAsync(_shutdownTask);
            _ = CompleteShutdownAsync();
        }
        return _shutdownCompletion.Task;
    }

    private async Task CompleteShutdownAsync()
    {
        try
        {
            await _shutdownTask!;
            ShutdownSucceeded = CleanupCompleted = true;
        }
        catch { }
        finally { _shutdownCompletion.TrySetResult(); }
    }

    public async Task CloseAfterReleaseAsync()
    {
        _closeCoordinator.DismissCloseChoice(restoreFocus: false);
        _isClosing = true;
        _state.IsInteractive = false;
        RetireHeaderPresentation();
        CancelActive(); CancelPageActivation();
        await EnsureShutdownAsync();
        await _shutdownTask!;
        await RequestFinalCloseAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _motion.Dispose();
        _isClosed = true;
        _isClosing = true;
        _closeCoordinator.Dispose();
        CancelActive();
        CancelPageActivation();
        foreach (var request in _requests.ToArray()) request.Cancel();
        ModuleNavigationList.ItemsSource = null;
        SystemAppearance.Changed -= OnSystemAppearanceChanged;
        foreach (var session in _sessions)
        {
            session.Module.PropertyChanged -= OnModulePropertyChanged;
            if (session.Navigation is not null) session.Navigation.CollectionChanged -= OnNavigationChanged;
        }
        foreach (var (source, handler) in _navigationSubscriptions) source.NavigationRequested -= handler;
        _navigationSubscriptions.Clear();
        _ownedNotices.Dispose();
        foreach (var (source, handler) in _sidebarSubscriptions) source.SettingsRequested -= handler;
        _sidebarSubscriptions.Clear();
        SidebarContributionHost.Content = null;
        RetireHeaderPresentation();
        DisposeGlobalSurfaces();
        _nativeCaption?.Dispose();
        _nativeCaption = null;
        if (!_userDataResetStarted || _closeCoordinator.IsSessionEnding) _ = EnsureShutdownAsync();
        else _shutdownCompletion.TrySetResult();
        base.OnClosed(e);
    }
}
