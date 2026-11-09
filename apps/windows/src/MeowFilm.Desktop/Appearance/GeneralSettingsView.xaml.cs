using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public partial class GeneralSettingsView : UserControl
{
    private readonly IDesktopAppearance appearance;
    public static IValueConverter StatusVisibility { get; } = new StatusConverter();
    public GeneralSettingsView(IDesktopAppearance appearance, IEnumerable<IDesktopGeneralSettingsSource> sources,
        IDesktopMediaControlSettings? mediaControls = null)
    {
        this.appearance = appearance;
        InitializeComponent();
        ThemeSelection.Presets = [new(AppearanceColors.DefaultColor, "浅蓝", AppearanceColors.DefaultColor)];
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
        CacheContributions.Visibility = area == "cache" ? Visibility.Visible : Visibility.Collapsed;
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
    private async void View_Loaded(object sender, RoutedEventArgs e) => await appearance.LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await appearance.LoadAsync();
    private async void Theme_Committed(object? sender, ColorSelectionEventArgs e)
    {
        await appearance.SaveAsync(e.Value);
        ThemeSelection.RestoreSelection();
    }
    private sealed class StatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
