using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly ContentControl _generalSettingsHost = new()
    {
        Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private bool _generalSettingsOpen;
    private void InitializeGeneralSettings(IDesktopAppearance? appearance, IDesktopClosePreferences? closePreferences)
    {
        if (appearance is null) return;
        _generalSettingsHost.Content = new GeneralSettingsView(appearance,
            _sessions.Select(session => session.Module).OfType<IDesktopGeneralSettingsSource>(),
            closePreferences, mediaControls: MediaControls,
            resetUserData: _resetUserData is null ? null : ConfirmUserDataReset);
        ContentDeck.Children.Add(_generalSettingsHost);
        string? appliedColor = null;
        void Apply()
        {
            if (appliedColor == appearance.ThemeColor) return;
            Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background(appearance.ThemeColor);
            ApplyAccessibilityPresentation();
            AppearanceColors.ApplyFloatingPalette(appearance.ThemeColor);
            appliedColor = appearance.ThemeColor;
        }
        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(IDesktopAppearance.ThemeColor)) Apply();
        };
        Apply();
        appearance.PropertyChanged += handler;
        Closed += (_, _) => appearance.PropertyChanged -= handler;
    }
    private readonly StackPanel _settingsTabs = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new(0)
    };
    private bool _updatingSettingsTabs;

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
                if (_updatingSettingsTabs || _isClosing || _current is not { IsSettingsOpen: true }) return;
                CancelActive();
                _generalSettingsOpen = true;
                ApplyPresentation();
                DesktopVisual.FocusFirst(_generalSettingsHost);
            };
            _settingsTabs.Children.Add(general);
        }
        foreach (var session in _sessions)
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
        if (_isClosing || _current is not { IsSettingsOpen: true } current ||
            (ReferenceEquals(current, target) && !_generalSettingsOpen)) return;
        CancelActive();
        _generalSettingsOpen = false;
        current.IsSettingsOpen = false;
        target.IsSettingsOpen = true;
        // Keep _settingsReturn: Back returns to the original content, not to
        // whichever module's settings happened to be selected last.
        PreparePresentation(target);
        SetStatus();
        ApplyPresentation();
        if (!target.Initialized) await RunRequestAsync(new(target));
        if (ReferenceEquals(_current, target) && target.IsSettingsOpen)
            DesktopVisual.FocusFirst(target.SettingsHost);
    }
}
