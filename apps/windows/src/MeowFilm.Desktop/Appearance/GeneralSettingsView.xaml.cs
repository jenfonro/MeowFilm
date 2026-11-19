using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.System;

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
        if (closePreferences is not null)
        {
            closePreferences.PropertyChanged += ClosePreferenceChanged;
            Unloaded += (_, _) => closePreferences.PropertyChanged -= ClosePreferenceChanged;
            Loaded += (_, _) => { closePreferences.PropertyChanged -= ClosePreferenceChanged; closePreferences.PropertyChanged += ClosePreferenceChanged; ClosePreferenceChanged(null, new(null)); };
        }
        if (closePreferences is not null) WindowArea.Visibility = Visibility.Visible;
        DataContext = appearance;
        if (mediaControls is not null)
        {
            ShortcutsPage.Content = new MediaControlSettingsView(mediaControls);
            ShortcutsArea.Visibility = Visibility.Visible;
        }
        else ShortcutsArea.Visibility = Visibility.Collapsed;
        foreach (var source in sources)
        {
            if (source.AppearanceSettingsView is { } appearanceView) AppearanceContributions.Children.Add(appearanceView);
            if (source.HomeSourceSettingsView is { } homeView) HomeSourceContributions.Children.Add(homeView);
            if (source.CacheSettingsView is { } cacheView) CacheContributions.Children.Add(cacheView);
        }
    }
    private void ClosePreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (closePreferences is not null) CloseBehaviorSelection.SelectedValue = closePreferences.Behavior.ToString();
    }
    private void Area_Checked(object sender, RoutedEventArgs e)
    {
        if (AppearancePage is null || sender is not RadioButton { Tag: string area }) return;
        AppearancePage.Visibility = area == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        HomeSourceContributions.Visibility = area == "sources" ? Visibility.Visible : Visibility.Collapsed;
        CachePage.Visibility = area == "cache" ? Visibility.Visible : Visibility.Collapsed;
        WindowBehaviorPage.Visibility = area == "window" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = area == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroller.ChangeView(null, 0, null, true);
    }
    private void Area_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not RadioButton current || e.Key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return;
        var tabs = Areas.Children.OfType<RadioButton>().Where(tab => tab.Visibility == Visibility.Visible).ToArray();
        int index = Array.IndexOf(tabs, current);
        int next = e.Key switch { VirtualKey.Home => 0, VirtualKey.End => tabs.Length - 1,
            VirtualKey.Left => (index + tabs.Length - 1) % tabs.Length, _ => (index + 1) % tabs.Length };
        tabs[next].IsChecked = true; tabs[next].Focus(FocusState.Programmatic); e.Handled = true;
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
        if (closePreferences is null || CloseBehaviorSelection.SelectedValue is not string choice || !Enum.TryParse<WindowCloseBehavior>(choice, out var behavior) ||
            behavior == closePreferences.Behavior) return;
        await closePreferences.SaveAsync(behavior);
        CloseBehaviorSelection.SelectedValue = closePreferences.Behavior.ToString();
    }
    private sealed class StatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
}
