using System.ComponentModel;
using MeowFilm.Core.Debug;
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
    private readonly RectangleGeometry _presentationClip = new();
    private readonly CloseWindowDialog _closeDialog = new();
    private readonly UserDataResetDialog _resetDialog = new();
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
        PresentationSurface.Clip = _presentationClip;
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) => _input.OnPreviewKeyDown(e)), true);
        Root.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler((_, e) => _input.OnPreviewKeyUp(e)), true);
        Root.PointerPressed += (_, e) => _input.OnChannelHeaderPointerPressed(e);
        Root.PointerReleased += (_, e) => _input.OnChannelHeaderPointerReleased(e);
        Root.GotFocus += (_, _) => _input.OnGotKeyboardFocus();
        PlayerSurfaceInput.AddDismissRequestedHandler(Root, OnQueueDismissClick);
        ShellWindowStartupSize.ApplyToPrimaryScreen(this, Handle);
        _nativeCaption = new(
            this, CaptionDragRegion,
            () => _playerDetailOpen || IsAnyModalOpen,
            [BackButton, ForwardButton, SearchInput, PageHeaderHost, CaptionBar],
            [WindowCaptionButtons, PlayerDetailBackButton, ChannelDialogHost, SystemDialogHost, NotificationOverlay]);
        _closeCoordinator.InitializeTrayIcon();
        ChannelDialogHost.Attach(this);
        ChannelDialogHost.OpenStateChanged += (_, _) => _nativeCaption?.RefreshImmediate();
        SystemDialogHost.AttachSystem(this);
        SystemDialogHost.OpenStateChanged += (_, _) =>
        {
            SetModalGatesEnabled(!SystemDialogHost.IsOpen);
            _nativeCaption?.RefreshImmediate();
        };
        Activated += (_, _) => UpdateCaption();
        Root.ActualThemeChanged += (_, _) => UpdateCaption();
        InitializeGeneralSettings(appearance, closePreferences);
        _closeDialog.ChoiceRequested += _closeCoordinator.OnCloseChoiceRequested;
        _closeDialog.CancelRequested += _closeCoordinator.CancelCloseChoice;
        _closeDialog.ForceDismissRequested += () => _closeCoordinator.DismissCloseChoice(restoreFocus: false);
        _resetDialog.ConfirmRequested += OnUserDataResetConfirmed;
        _resetDialog.CancelRequested += CancelUserDataReset;
        _resetDialog.ExitRequested += OnUserDataResetExit;
        NoticeOutlet.SetReceiver(Root, notice => Notices.Show(notice.Identity, notice.Message));
        SystemAppearance.Changed += OnSystemAppearanceChanged;
        _modules.SubscribeModules();
        _sidebar.Initialize(pages);
        InitializeHeaderPresentation();
        InitializeSettingsNavigation();
        ApplyAccessibilityPresentation();
    }

    public Task ShutdownCompletion => _closeCoordinator.ShutdownCompletion;
    public IDesktopMediaControlSettings? MediaControls { get; }
    public Func<string?>? ActivePlaybackOwnerProvider { get; set; }
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
    internal DesktopModalHost? ActiveModalHost =>
        SystemDialogHost.IsOpen ? SystemDialogHost
        : ChannelDialogHost.IsOpen ? ChannelDialogHost
        : null;
    internal bool IsSystemModalOpen => SystemDialogHost.IsOpen;
    internal bool IsAnyModalOpen => ActiveModalHost is not null;
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
    internal CloseWindowDialog CloseDialogElement => _closeDialog;
    internal UserDataResetDialog ResetDialogElement => _resetDialog;
    internal NoticeStack NoticesElement => Notices;

    internal void ShowSystemModal(FrameworkElement content, int zIndex, Action? onCloseRequested = null)
    {
        Canvas.SetZIndex(SystemDialogHost, zIndex);
        SystemDialogHost.Show(Root, content, dismissOnOutside: false, onCloseRequested: onCloseRequested);
    }

    internal void DismissSystemModal(FrameworkElement content, bool restoreFocus = true) =>
        SystemDialogHost.Close(content, restoreFocus);

    public void RestoreFromTray() => _closeCoordinator.RestoreFromTray();
    public void PrepareForSessionEnding() => _closeCoordinator.PrepareForSessionEnding();
    public Task CloseAfterReleaseAsync() => _closeCoordinator.CloseAfterReleaseAsync();
    public CancellationToken GetPageNavigationToken(IDesktopPage page, string moduleId, string route) => _modules.GetPageNavigationToken(page, moduleId, route);
    public Task NavigateFromPageAsync(IDesktopPage page, string moduleId, string route, CancellationToken cancellationToken = default) => _modules.NavigateFromPageAsync(page, moduleId, route, cancellationToken);

    internal void PreparePresentation(ModuleSession session, bool applyPresentation = true) =>
        _modules.PreparePresentation(session, applyPresentation: applyPresentation);
    internal Task NavigateFromUiAsync(ModuleSession session, string route) => _modules.NavigateFromUiAsync(session, route);
    internal Task RunRequestAsync(RequestPlan plan, CancellationToken cancellationToken = default, bool reportOutcome = false) => _modules.RunRequestAsync(plan, cancellationToken, reportOutcome);
    internal Task GoBackAsync() => _modules.GoBackAsync();
    internal Task GoForwardAsync() => _modules.GoForwardAsync();
    internal void CancelActive() => _modules.CancelActive();
    internal void CloseGeneralSettingsFlag() => _generalSettingsOpen = false;
    internal void SetModalGatesEnabled(bool enabled) => RootInteractionGate.IsEnabled = NotificationInteractionGate.IsEnabled = enabled;
    internal void OpenSettingsFromUi() => OnSettingsClick(this, new RoutedEventArgs());

    internal void CancelFromUi()
    {
        if (_isClosing) return;
        CancelPageActivation(); CancelActive();
        if (_currentPage is not null) _modules.ResetPageNavigation();
        _modules.RetryPlan = null;
        _search.ApplyPresentation(force: true);
        SetStatus("已取消");
        _sidebar.RestoreSelection();
    }

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
        Bindings.Update();
        if (_loaded || _isClosing) return;
        _loaded = true;
        RunUiAction(() => _modules.ActivateAsync(CurrentSession ?? Sessions[0]));
    }

    private void OnPresentationSizeChanged(object sender, SizeChangedEventArgs args) =>
        _presentationClip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);

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
        ApplyGlobalSurfacePresentation(applyPresentation: false);
        bool immersive = _currentPage is null && !current.IsSettingsOpen &&
            current.Module is IDesktopImmersiveSource { IsImmersive: true };
        RootLayout.ColumnDefinitions[0].Width = new(immersive ? 0 : 216);
        PageHeaderHost.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarBackground.Visibility = Brand.Visibility = SidebarScroll.Visibility =
            immersive ? Visibility.Collapsed : Visibility.Visible;
        SidebarEdge.Visibility = immersive || SystemAppearance.IsHighContrast ? Visibility.Collapsed : Visibility.Visible;
        bool underlyingInteractive = !_playerDetailOpen;
        HeaderChrome.IsHitTestVisible = underlyingInteractive;
        SidebarScroll.IsHitTestVisible = underlyingInteractive;
        ContentRegion.IsHitTestVisible = underlyingInteractive;
        PlayerFrame.IsHitTestVisible = underlyingInteractive;
        _state.CanGoBack = _queueOpen || _playerDetailOpen || _currentPage is not null || current.IsSettingsOpen || _history.CanGoBack;
        _state.CanGoForward = !_queueOpen && !_playerDetailOpen && !current.IsSettingsOpen && _history.CanGoForward;
        _search.ApplyPresentation();
        foreach (var page in _pages) page.Host.Visibility = ReferenceEquals(page, _currentPage) ? Visibility.Visible : Visibility.Collapsed;
        _sidebar.RestoreSelection();
        _modules.UpdateNavigationRetention();
        _nativeCaption?.Refresh();
    }

    internal void SetStatus(string message = "", bool busy = false, bool error = false, bool retry = false)
    {
        if (DebugLog.IsEnabled && (message.Length > 0 || error))
            DebugLog.Write(error ? "warn" : "debug", DebugCategories.UiNav, "shell.status",
                "status.changed", new { message, busy, error, retry });
        _state.SetStatus(message, busy, error, retry);
        UpdateStatusPresentation();
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
        _ = ShellRequest.ObserveAsync(Dispatcher.EnqueueAsync(() =>
        {
            if (_isClosing) return;
            try { action(); }
            catch (Exception) { SetStatus("暂时无法完成操作", error: true); }
        }, DispatcherQueuePriority.Normal));
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

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_retryPage is { } retry)
        {
            RunUiAction(() => ActivatePageAsync(retry.Session, retry.Operation, retry.Navigation, retry.RecordNavigation));
            return;
        }
        if (_modules.RetryPlan is { } plan) RunUiAction(() => RunRequestAsync(plan));
    }

    private void OnDismissStatusClick(object sender, RoutedEventArgs e) { if (!_isClosing) { _modules.RetryPlan = null; _retryPage = null; SetStatus(); } }
    private void OnSystemAppearanceChanged(object? sender, EventArgs args) => QueueUi(() => { ApplyAccessibilityPresentation(); ApplyPresentation(); });

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeCoordinator.AllowClose || _closeCoordinator.IsSessionEnding || e.Cancel) return;
        e.Cancel = true;
        if (_isClosing || _resetDialog.IsOpen) return;
        if (_closeCoordinator.HasPreferences) { _closeCoordinator.QueueCloseChoice(); return; }
        BeginShutdown();
    }

    internal void BeginShutdown()
    {
        if (_isClosed || (_isClosing && !(_userDataResetStarted && _closeCoordinator.IsSessionEnding)) ||
            (_resetDialog.IsOpen && !_closeCoordinator.IsSessionEnding)) return;
        if (_closeCoordinator.IsSessionEnding) DismissSystemModal(_resetDialog, restoreFocus: false);
        _closeCoordinator.DismissCloseChoice(restoreFocus: false);
        _isClosing = true; _state.IsInteractive = false; _modules.RetryPlan = null;
        RetireHeaderPresentation(); CancelActive(); CancelPageActivation();
        _modules.CancelAllRequests();
        if (_closeCoordinator.IsSessionEnding)
        {
            _ = _closeCoordinator.EnsureShutdownAsync();
            _ = _closeCoordinator.RequestFinalCloseAsync();
        }
        else
        {
            _ = _closeCoordinator.ShutdownAndCloseAsync();
        }
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
