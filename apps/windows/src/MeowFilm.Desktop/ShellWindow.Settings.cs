using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    static ShellWindow() => EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
        new RoutedEventHandler(StyleAutomaticEditorMenu));

    private static void StyleAutomaticEditorMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement editor } menu ||
            editor is not (TextBoxBase or PasswordBox) || editor.ContextMenu is not null) return;
        var owner = Window.GetWindow(editor);
        while (owner is not null && owner is not ShellWindow) owner = owner.Owner;
        if (owner is not ShellWindow shell) return;
        menu.SetCurrentValue(StyleProperty, shell.FindResource("MeowFilm.ContextMenu"));
        // WPF's EditorMenuItem/SpellingMenuItem are private derived types, so the
        // MenuItem implicit key is insufficient. Keep every generated action and target.
        ApplyItems(menu);
        void ApplyItems(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<MenuItem>())
            {
                item.SetCurrentValue(StyleProperty, shell.FindResource("MeowFilm.MenuItem"));
                ApplyItems(item);
            }
        }
    }
    private readonly ContentControl _generalSettingsHost = new()
    {
        Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private bool _generalSettingsOpen;
    private void InitializeGeneralSettings(IDesktopAppearance? appearance)
    {
        if (appearance is null) return;
        _generalSettingsHost.Content = new GeneralSettingsView(appearance,
            _sessions.Select(session => session.Module).OfType<IDesktopGeneralSettingsSource>(), MediaControls);
        ContentDeck.Children.Add(_generalSettingsHost);
        string? appliedColor = null;
        void Apply()
        {
            if (appliedColor == appearance.ThemeColor) return;
            Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background(appearance.ThemeColor);
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
        Margin = new(0, 4, 0, 0)
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
                Style = (Style)FindResource("MeowFilm.SettingsArea")
            };
            AutomationProperties.SetName(general, "通用设置");
            general.Checked += (_, _) =>
            {
                if (_updatingSettingsTabs || _isClosing || _current is not { IsSettingsOpen: true }) return;
                CancelActive();
                _generalSettingsOpen = true;
                ApplyPresentation();
                _generalSettingsHost.MoveFocus(new(FocusNavigationDirection.First));
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
                Style = (Style)FindResource("MeowFilm.SettingsArea")
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
            target.SettingsHost.MoveFocus(new(FocusNavigationDirection.First));
    }
}
