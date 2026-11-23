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
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow : DesktopWindow
{
    private readonly ShellState _state = new();
    private readonly ShellNavigationHistory _history = new();
    private readonly ShellModuleCoordinator _modules;
    private readonly ShellSidebarCoordinator _sidebar;
    private readonly ShellInputCoordinator _input;
    private readonly ShellSearchCoordinator _search;
    private readonly ShellCloseCoordinator _closeCoordinator;
    private readonly ShellOwnedNoticeBridge _ownedNotices;
    private readonly ShellSurfaceMotion _motion = new();
    private ShellDebugAdapter? _debugAdapter;
    private NativeCaption? _nativeCaption;
    private bool _loaded, _isClosing, _isClosed;

    public ShellWindow(IReadOnlyList<IDesktopModule> modules, IReadOnlyList<IDesktopPage> pages,
        Func<Task> shutdown,
        IDesktopAppearance? appearance = null, IDesktopClosePreferences? closePreferences = null,
        IDesktopMediaControlSettings? mediaControls = null, Func<Task>? resetUserData = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (modules.Count == 0) throw new ArgumentException("At least one module is required.", nameof(modules));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sessions = new List<ModuleSession>(modules.Count);
        foreach (var module in modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Id) || !ids.Add(module.Id))
                throw new ArgumentException("Modules must have distinct, non-empty identities.", nameof(modules));
            sessions.Add(new(module));
        }
        if (pages.Any(page => page is null || string.IsNullOrWhiteSpace(page.Id) || !ids.Add(page.Id)))
            throw new ArgumentException("Pages must have distinct, non-empty identities.", nameof(pages));
        _pages = pages.Select(page => new ShellPageSession(page)).ToArray();
        _resetUserData = resetUserData;
        MediaControls = mediaControls;
        InitializeComponent();
        Bindings?.Initialize();
        AttachRoot();
        _modules = new ShellModuleCoordinator(this, sessions);
        _sidebar = new ShellSidebarCoordinator(this);
        _input = new ShellInputCoordinator(this);
        _search = new ShellSearchCoordinator(SearchBox, SearchTargetPicker, SearchInput, SearchDivider, () => _currentPage, () => _modules.ActiveRequest);
        _closeCoordinator = new ShellCloseCoordinator(this, shutdown, closePreferences);
        _ownedNotices = new ShellOwnedNoticeBridge(Notices, QueueUi);
        SessionEnding += (_, _) => PrepareForSessionEnding();
        Root.Loaded += OnLoaded;
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => _input.OnPreviewKeyDown(e)), true);
        Root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) => _input.OnPreviewKeyUp(e)), true);
        Root.PointerPressed += (_, e) => _input.OnChannelHeaderPointerPressed(e);
        Root.PointerReleased += (_, e) => _input.OnChannelHeaderPointerReleased(e);
        Root.GotFocus += (_, _) => _input.OnGotKeyboardFocus();
        PlayerSurfaceInput.AddDismissRequestedHandler(Root, OnQueueDismissClick);
        ShellWindowStartupSize.ApplyToPrimaryScreen(this, Handle);
        _nativeCaption = new(
            this, CaptionDragRegion, WindowCaptionButtons, () => _playerDetailOpen,
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
        _modules.SubscribeModules();
        _sidebar.Initialize(pages);
        InitializeHeaderPresentation();
        InitializeSettingsNavigation();
        UpdateCaption();
    }

    public Task ShutdownCompletion => _closeCoordinator.ShutdownCompletion;
    public IDesktopMediaControlSettings? MediaControls { get; }
    public IDebugShellHost DebugHost => _debugAdapter ??= new ShellDebugAdapter(this);
    public bool ShutdownSucceeded => _closeCoordinator.ShutdownSucceeded;
    public bool CleanupCompleted => _closeCoordinator.CleanupCompleted;
    public bool IsHiddenToTray => _closeCoordinator.IsHiddenToTray;

    internal ShellState State => _state;
    internal ShellNavigationHistory History => _history;
    internal ShellModuleCoordinator Modules => _modules;
    internal ShellSidebarCoordinator Sidebar => _sidebar;
    internal ShellSearchCoordinator SearchCoordinator => _search;
    internal ShellCloseCoordinator CloseCoordinator => _closeCoordinator;
    internal ShellOwnedNoticeBridge OwnedNotices => _ownedNotices;
    internal IReadOnlyList<ModuleSession> Sessions => _modules.Sessions;
    internal IReadOnlyList<ShellPageSession> Pages => _pages;
    internal ModuleSession? CurrentSession => _modules.CurrentSession;
    internal ShellPageSession? CurrentPage => _currentPage;
    internal ModuleSession? PlayerDetailSession => _playerDetailSession;
    internal ModuleSession? QueueSession => _queueSession;
    internal NativeCaption? NativeCaption => _nativeCaption;
    internal ContentControl GeneralSettingsHost => _generalSettingsHost;
    internal bool IsLoaded => _loaded;
    internal bool IsClosing => _isClosing;
    internal bool IsClosed => _isClosed;
    internal bool IsRequestBusy => _state.IsBusy || _modules.ActiveRequest is not null || _pageRequest is not null;
    internal bool IsGeneralSettingsOpen => _generalSettingsOpen;
    internal bool IsPlayerDetailOpen => _playerDetailOpen;
    internal bool IsQueueOpen => _queueOpen;
    internal Grid RootLayoutElement => RootLayout;
    internal Grid ContentDeckElement => ContentDeck;
    internal Grid HeaderChromeElement => HeaderChrome;
    internal Grid SearchChromeElement => SearchChrome;
    internal Grid SearchInputElement => SearchInput;
    internal TextBox SearchBoxElement => SearchBox;
    internal ShellSearchScope SearchTargetPickerElement => SearchTargetPicker;
    internal StackPanel WindowCaptionButtonsElement => WindowCaptionButtons;
    internal Border SidebarBackgroundElement => SidebarBackground;
    internal Border PlayerFrameElement => PlayerFrame;
    internal ContentControl PlayerHostElement => PlayerHost;
    internal DesktopPageHeader PageHeaderHostElement => PageHeaderHost;
    internal Button SettingsButtonElement => SettingsButton;
    internal Button PlayerDetailBackButtonElement => PlayerDetailBackButton;
    internal ListView ModuleNavigationListElement => ModuleNavigationList;
    internal ListView NavigationListElement => NavigationList;
    internal ListView PublicNavigationListElement => PublicNavigationList;
    internal ListView CollectionNavigationListElement => CollectionNavigationList;
    internal ContentControl SidebarContributionHostElement => SidebarContributionHost;
    internal DesktopModalHost ChannelDialogHostElement => ChannelDialogHost;
    internal CloseWindowDialog CloseDialogElement => CloseDialog;
    internal UserDataResetDialog ResetDialogElement => ResetDialog;
    internal NoticeStack NoticesElement => Notices;

    public void RestoreFromTray() => _closeCoordinator.RestoreFromTray();
    public void PrepareForSessionEnding() => _closeCoordinator.PrepareForSessionEnding();
    public Task CloseAfterReleaseAsync() => _closeCoordinator.CloseAfterReleaseAsync();
    public CancellationToken GetPageNavigationToken(IDesktopPage page, string moduleId, string route) => _modules.GetPageNavigationToken(page, moduleId, route);
    public Task NavigateFromPageAsync(IDesktopPage page, string moduleId, string route, CancellationToken cancellationToken = default) => _modules.NavigateFromPageAsync(page, moduleId, route, cancellationToken);

    internal void PreparePresentation(ModuleSession session) => _modules.PreparePresentation(session);
    internal Task NavigateFromUiAsync(ModuleSession session, string route) => _modules.NavigateFromUiAsync(session, route);
    internal Task RunRequestAsync(RequestPlan plan, CancellationToken cancellationToken = default, bool reportOutcome = false) => _modules.RunRequestAsync(plan, cancellationToken, reportOutcome);
    internal Task GoBackAsync() => _modules.GoBackAsync();
    internal Task GoForwardAsync() => _modules.GoForwardAsync();
    internal void CancelActive() => _modules.CancelActive();
    internal void CloseGeneralSettingsFlag() => _generalSettingsOpen = false;
    internal void SetModalGatesEnabled(bool enabled) => RootInteractionGate.IsEnabled = NotificationInteractionGate.IsEnabled = enabled;
    internal void OpenSettingsFromUi() => OnSettingsClick(this, new RoutedEventArgs());
    internal void CancelFromUi() => OnCancelClick(this, new RoutedEventArgs());

    internal void MarkClosingForRelease()
    {
        _isClosing = true; _state.IsInteractive = false;
        RetireHeaderPresentation(); CancelActive(); CancelPageActivation();
    }

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
        RunUiAction(() => _modules.ActivateAsync(CurrentSession ?? Sessions[0]));
    }

    private void OnPresentationSizeChanged(object sender, SizeChangedEventArgs args) =>
        PresentationSurface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };

    internal void ApplyPresentation()
    {
        if (CurrentSession is not { } current) return;
        if (current.Mounted && _currentPage is null) _modules.RefreshSuppliedViews(current);
        foreach (var session in Sessions.Where(session => session.Mounted))
        {
            var selected = ReferenceEquals(session, current);
            session.Surface.Visibility = selected && _currentPage is null ? Visibility.Visible : Visibility.Collapsed;
            session.MainHost.Visibility = session.IsSettingsOpen ? Visibility.Collapsed : Visibility.Visible;
            session.SettingsHost.Visibility = session.IsSettingsOpen && !_generalSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        _generalSettingsHost.Visibility = _currentPage is null && current.IsSettingsOpen && _generalSettingsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        _modules.ApplyPlayerPresentation(current);
        _sidebar.ApplyOwner(current);
        _sidebar.ApplySidebarContribution(current);
        _modules.ApplyModuleAccent(current);
        ApplyHeaderPresentation(current);
        ApplyGlobalSurfacePresentation(current);
        bool immersive = _currentPage is null && !current.IsSettingsOpen &&
            current.Module is IDesktopImmersiveSource { IsImmersive: true };
        RootLayout.ColumnDefinitions[0].Width = new(immersive ? 0 : 216);
        PageHeaderHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarBackground.Visibility = Brand.Visibility = SidebarScroll.Visibility =
            immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarEdge.Visibility = immersive || SystemAppearance.IsHighContrast ? Visibility.Collapsed : Visibility.Visible;
        _state.CanGoBack = _queueOpen || _playerDetailOpen || _currentPage is not null || current.IsSettingsOpen || _history.CanGoBack;
        _state.CanGoForward = !_queueOpen && !_playerDetailOpen && !current.IsSettingsOpen && _history.CanGoForward;
        _search.ApplyPresentation();
        foreach (var page in _pages) page.Host.Visibility = ReferenceEquals(page, _currentPage) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SettingsButton, "设置");
        AutomationProperties.SetName(NavigationList, "内容导航");
        _sidebar.RestoreSelection();
        _modules.UpdateNavigationRetention();
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

    internal void RunUiAction(Func<Task> action) { if (!_isClosing) _ = RunUiActionCoreAsync(action); }

    private async Task RunUiActionCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_isClosing) SetStatus("已取消"); }
        catch (Exception) { if (!_isClosing) SetStatus("暂时无法完成操作", error: true); }
    }

    internal void QueueUi(Action action)
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

    private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e) => _sidebar.OnModuleSelectionChanged(sender);
    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e) => _sidebar.OnNavigationSelectionChanged(sender);
    private void OnNavigationContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) => _sidebar.OnContainerContentChanging(args);
    private void OnBackClick(object sender, RoutedEventArgs e) => RunUiAction(GoBackAsync);
    private void OnForwardClick(object sender, RoutedEventArgs e) => RunUiAction(GoForwardAsync);
    private void OnSearchCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _search.OnCompositionStarted();
    private void OnSearchCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _search.OnCompositionEnded();
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _search.OnTextChanged();
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e) => _input.OnSearchKeyDown(e);

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        CancelPageActivation(); CancelActive();
        if (_currentPage is not null) _modules.ResetPageNavigation();
        _modules.RetryPlan = null;
        _search.ApplyPresentation(force: true);
        SetStatus("已取消");
        _sidebar.RestoreSelection();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_retryPage is { } page) { RunUiAction(() => ActivatePageAsync(page)); return; }
        if (_modules.RetryPlan is { } plan) RunUiAction(() => RunRequestAsync(plan));
    }

    private void OnDismissStatusClick(object sender, RoutedEventArgs e) { if (!_isClosing) { _modules.RetryPlan = null; SetStatus(); } }
    private void OnSystemAppearanceChanged(object? sender, EventArgs args) => QueueUi(() => { ApplyAccessibilityPresentation(); ApplyPresentation(); });

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeCoordinator.AllowClose || _closeCoordinator.IsSessionEnding || e.Cancel) return;
        e.Cancel = true;
        if (_isClosing || ResetDialog.IsOpen) return;
        if (_closeCoordinator.HasPreferences) { _closeCoordinator.QueueCloseChoice(); return; }
        BeginShutdown();
    }

    internal void BeginShutdown()
    {
        if (_isClosed || (_isClosing && !(_userDataResetStarted && _closeCoordinator.IsSessionEnding)) ||
            (ResetDialog.IsOpen && !_closeCoordinator.IsSessionEnding)) return;
        if (_closeCoordinator.IsSessionEnding) ResetDialog.Visibility = Visibility.Collapsed;
        _closeCoordinator.DismissCloseChoice(restoreFocus: false);
        _isClosing = true; _state.IsInteractive = false; _modules.RetryPlan = null;
        RetireHeaderPresentation(); CancelActive(); CancelPageActivation();
        _modules.CancelAllRequests();
        _ = _closeCoordinator.EnsureShutdownAsync();
        _ = _closeCoordinator.RequestFinalCloseAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _motion.Dispose();
        _isClosed = true; _isClosing = true;
        _closeCoordinator.Dispose();
        CancelActive(); CancelPageActivation(); _modules.CancelAllRequests();
        SystemAppearance.Changed -= OnSystemAppearanceChanged;
        _modules.DisposeSubscriptions();
        _sidebar.Dispose();
        _ownedNotices.Dispose();
        RetireHeaderPresentation(); DisposeGlobalSurfaces();
        _nativeCaption?.Dispose(); _nativeCaption = null;
        if (!_userDataResetStarted || _closeCoordinator.IsSessionEnding) _ = _closeCoordinator.EnsureShutdownAsync();
        else _closeCoordinator.CompleteShutdownWithoutWork();
        base.OnClosed(e);
    }
}
