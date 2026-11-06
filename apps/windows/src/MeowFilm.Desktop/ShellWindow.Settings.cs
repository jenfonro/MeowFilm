using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
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
                tab.IsChecked = ReferenceEquals(tab.Tag, current);
        }
        finally { _updatingSettingsTabs = false; }
    }

    private async Task SwitchSettingsAsync(ModuleSession target)
    {
        if (_isClosing || _current is not { IsSettingsOpen: true } current ||
            ReferenceEquals(current, target)) return;
        CancelActive();
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
