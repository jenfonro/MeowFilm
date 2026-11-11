using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public partial class GeneralSettingsView : UserControl
{
    private readonly IDesktopAppearance appearance;
    private readonly IDesktopClosePreferences? closePreferences;
    private readonly Action? resetUserData;
    public static IValueConverter StatusVisibility { get; } = new StatusConverter();
    public GeneralSettingsView(IDesktopAppearance appearance, IEnumerable<IDesktopGeneralSettingsSource> sources,
        IDesktopClosePreferences? closePreferences = null, IDesktopMediaControlSettings? mediaControls = null,
        Action? resetUserData = null)
    {
        this.appearance = appearance;
        this.closePreferences = closePreferences;
        this.resetUserData = resetUserData;
        InitializeComponent();
        UserDataSection.Visibility = resetUserData is null ? Visibility.Collapsed : Visibility.Visible;
        ThemeSelection.Presets = [new(AppearanceColors.DefaultColor, "浅蓝", AppearanceColors.DefaultColor)];
        WindowBehaviorPage.DataContext = closePreferences;
        if (closePreferences is null)
        {
            Areas.Children.Remove(WindowArea);
            ((Panel)WindowBehaviorPage.Parent).Children.Remove(WindowBehaviorPage);
        }
        else WindowArea.Visibility = Visibility.Visible;
        DataContext = appearance;
        if (mediaControls is not null)
        {
            ShortcutsPage.Content = new MediaControlSettingsView(mediaControls);
            ShortcutsArea.Visibility = Visibility.Visible;
        }
        else Areas.Children.Remove(ShortcutsArea);
        foreach (var source in sources)
        {
            AppearanceContributions.Children.Add(source.AppearanceSettingsView);
            HomeSourceContributions.Children.Add(source.HomeSourceSettingsView);
            CacheContributions.Children.Add(source.CacheSettingsView);
        }
    }
    private void Area_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not RadioButton { Tag: string area }) return;
        AppearancePage.Visibility = area == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        HomeSourceContributions.Visibility = area == "sources" ? Visibility.Visible : Visibility.Collapsed;
        CachePage.Visibility = area == "cache" ? Visibility.Visible : Visibility.Collapsed;
        WindowBehaviorPage.Visibility = area == "window" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = area == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroller.ScrollToTop();
    }
    private void Area_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not RadioButton current || e.Key is not (Key.Home or Key.End or Key.Left or Key.Right)) return;
        var tabs = Areas.Children.OfType<RadioButton>().Where(tab => tab.Visibility == Visibility.Visible).ToArray();
        int index = Array.IndexOf(tabs, current);
        int next = e.Key switch { Key.Home => 0, Key.End => tabs.Length - 1,
            Key.Left => (index + tabs.Length - 1) % tabs.Length, _ => (index + 1) % tabs.Length };
        tabs[next].IsChecked = true; tabs[next].Focus(); e.Handled = true;
    }
    private async void View_Loaded(object sender, RoutedEventArgs e) =>
        await Task.WhenAll(appearance.LoadAsync(), closePreferences?.LoadAsync() ?? Task.CompletedTask);
    private async void Reload_Click(object sender, RoutedEventArgs e) => await appearance.LoadAsync();
    private async void Theme_Committed(object? sender, ColorSelectionEventArgs e)
    {
        await appearance.SaveAsync(e.Value);
        ThemeSelection.RestoreSelection();
    }
    private async void CloseReload_Click(object sender, RoutedEventArgs e)
    {
        if (closePreferences is not null) await closePreferences.LoadAsync();
    }
    private void ClearUserData_Click(object sender, RoutedEventArgs e) => resetUserData?.Invoke();
    private async void CloseBehavior_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (closePreferences is null || CloseBehaviorSelection.SelectedValue is not WindowCloseBehavior behavior ||
            behavior == closePreferences.Behavior) return;
        await closePreferences.SaveAsync(behavior);
        CloseBehaviorSelection.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget();
    }
    private sealed class StatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
