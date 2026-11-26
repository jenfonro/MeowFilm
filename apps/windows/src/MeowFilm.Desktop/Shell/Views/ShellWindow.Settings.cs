using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly ContentControl _generalSettingsHost = new()
    {
        Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private bool _generalSettingsOpen;
    private readonly StackPanel _settingsTabs = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new(0, 6, 0, 0)
    };
    private bool _updatingSettingsTabs;

    private void InitializeGeneralSettings(IDesktopAppearance? appearance, IDesktopClosePreferences? closePreferences)
    {
        if (appearance is null) return;
        _generalSettingsHost.Content = new GeneralSettingsView(appearance,
            Sessions.Select(session => session.Module).OfType<IDesktopGeneralSettingsSource>(),
            closePreferences, mediaControls: MediaControls,
            resetUserData: _resetUserData is null ? null : ConfirmUserDataReset);
        ContentDeck.Children.Add(_generalSettingsHost);
        string? appliedColor = null;
        void Apply(bool refreshAccessibility)
        {
            if (appliedColor == appearance.ThemeColor) return;
            Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background(appearance.ThemeColor);
            if (refreshAccessibility) ApplyAccessibilityPresentation();
            AppearanceColors.ApplyFloatingPalette(appearance.ThemeColor);
            appliedColor = appearance.ThemeColor;
        }
        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(IDesktopAppearance.ThemeColor)) Apply(refreshAccessibility: true);
        };
        Apply(refreshAccessibility: false);
        appearance.PropertyChanged += handler;
        Closed += (_, _) => appearance.PropertyChanged -= handler;
    }

    private void InitializeSettingsNavigation()
    {
        AutomationProperties.SetName(_settingsTabs, "设置类别");
        if (_generalSettingsHost.Content is not null)
        {
            var general = new RadioButton
            {
                Content = "通用", GroupName = "SettingsArea",
                Style = (Style)DesktopVisual.Resource(Root, "MeowFilm.SettingsArea")
            };
            AutomationProperties.SetName(general, "通用设置");
            general.Checked += (_, _) =>
            {
                if (_updatingSettingsTabs || _isClosing || CurrentSession is not { IsSettingsOpen: true }) return;
                CancelActive();
                _generalSettingsOpen = true;
                ApplyPresentation();
                DesktopVisual.FocusFirst(_generalSettingsHost);
            };
            _settingsTabs.Children.Add(general);
        }
        foreach (var session in Sessions)
        {
            var tab = new RadioButton
            {
                Content = session.Module.Title,
                Tag = session,
                GroupName = "SettingsArea",
                Style = (Style)DesktopVisual.Resource(Root, "MeowFilm.SettingsArea")
            };
            AutomationProperties.SetName(tab, session.Module.Title + "设置");
            tab.Checked += (_, _) =>
            {
                if (!_updatingSettingsTabs)
                    RunUiAction(() => SwitchSettingsAsync(session));
            };
            _settingsTabs.Children.Add(tab);
        }
        if (_settingsTabs.Children.LastOrDefault() is FrameworkElement last)
            last.Margin = new(0, 0, 24, 0);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => RunUiAction(async () =>
    {
        if (CurrentSession is not { } current) return;
        if (_playerDetailOpen) await ClosePlayerDetailAsync();
        if (current.IsSettingsOpen && _currentPage is null) { ApplyPresentation(); return; }
        var leftPublicPage = _currentPage is not null;
        LeavePublicPage();
        CancelActive();
        if (!leftPublicPage) RetireHeaderPresentation();
        if (DesktopVisual.ContainsFocus(current.MainHost)) current.ContentFocus = DesktopVisual.Focused(Root);
        _modules.SettingsReturn = current;
        current.IsSettingsOpen = true;
        _generalSettingsOpen = _generalSettingsHost.Content is not null;
        _modules.RetryPlan = null;
        SetStatus();
        ApplyPresentation();
        QueueUi(() =>
        {
            if (ReferenceEquals(CurrentSession, current) && current.IsSettingsOpen)
                DesktopVisual.FocusFirst(_generalSettingsOpen ? _generalSettingsHost : current.SettingsHost);
        });
        if (!_generalSettingsOpen && !current.Initialized) await RunRequestAsync(new(current));
    });

    internal void EnterGeneralSettings()
    {
        var current = CurrentSession ?? Sessions[0];
        if (!current.IsSettingsOpen || _currentPage is not null)
        {
            LeavePublicPage(); CancelActive(); RetireHeaderPresentation();
            _modules.SettingsReturn = current;
            current.IsSettingsOpen = true;
        }
        _generalSettingsOpen = _generalSettingsHost.Content is not null;
        SetStatus(); ApplyPresentation();
    }

    internal async Task EnterModuleSettingsAsync(ModuleSession targetSession)
    {
        var current = CurrentSession ?? Sessions[0];
        if (!current.IsSettingsOpen || _currentPage is not null)
        {
            LeavePublicPage(); CancelActive(); RetireHeaderPresentation();
            _modules.SettingsReturn = current;
        }
        _generalSettingsOpen = false;
        if (!ReferenceEquals(current, targetSession))
            current.IsSettingsOpen = false;
        if (!ReferenceEquals(CurrentSession, targetSession))
        {
            if (CurrentSession is { } prev) prev.IsSettingsOpen = false;
            targetSession.IsSettingsOpen = true;
            PreparePresentation(targetSession, applyPresentation: false);
        }
        else targetSession.IsSettingsOpen = true;
        SetStatus(); ApplyPresentation();
        if (!targetSession.Initialized) await RunRequestAsync(new(targetSession));
    }

    internal async Task PrepareModuleTargetAsync(ModuleSession session)
    {
        _generalSettingsOpen = false;
        if (CurrentSession is { } prev) prev.IsSettingsOpen = false;
        session.IsSettingsOpen = false;
        PreparePresentation(session, applyPresentation: session.Initialized);
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

    private void SelectSettingsTab(ModuleSession current)
    {
        _updatingSettingsTabs = true;
        try
        {
            foreach (var tab in _settingsTabs.Children.OfType<RadioButton>())
                tab.IsChecked = _generalSettingsOpen ? tab.Tag is null : ReferenceEquals(tab.Tag, current);
        }
        finally { _updatingSettingsTabs = false; }
    }

    private async Task SwitchSettingsAsync(ModuleSession target)
    {
        if (_isClosing || CurrentSession is not { IsSettingsOpen: true } current ||
            (ReferenceEquals(current, target) && !_generalSettingsOpen)) return;
        CancelActive();
        _generalSettingsOpen = false;
        current.IsSettingsOpen = false;
        target.IsSettingsOpen = true;
        // Keep SettingsReturn: Back returns to the original content, not to
        // whichever module's settings happened to be selected last.
        PreparePresentation(target, applyPresentation: false);
        SetStatus();
        ApplyPresentation();
        if (!target.Initialized) await RunRequestAsync(new(target));
        if (ReferenceEquals(CurrentSession, target) && target.IsSettingsOpen)
            DesktopVisual.FocusFirst(target.SettingsHost);
    }
}
