using System.Globalization;
using System.Text.Json;
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
    private string currentArea = "appearance";
    internal string CurrentArea => currentArea;
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
        if (closePreferences is not null) RestoreCloseBehavior();
    }
    private void RestoreCloseBehavior() => CloseBehaviorSelection.SelectedItem = CloseBehaviorSelection.Items
        .OfType<ComboBoxItem>().Single(item => Equals(item.Tag, closePreferences!.Behavior.ToString()));
    private void Area_Checked(object sender, RoutedEventArgs e)
    {
        if (AppearancePage is null || sender is not RadioButton { Tag: string area }) return;
        currentArea = area;
        AppearancePage.Visibility = area == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        HomeSourceContributions.Visibility = area == "sources" ? Visibility.Visible : Visibility.Collapsed;
        CachePage.Visibility = area == "cache" ? Visibility.Visible : Visibility.Collapsed;
        WindowBehaviorPage.Visibility = area == "window" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = area == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroller.ChangeView(null, 0, null, true);
    }

    internal bool SelectArea(string area)
    {
        var normalized = area.Trim().ToLowerInvariant() switch
        {
            "home-source" or "home-sources" => "sources",
            "media-controls" => "shortcuts",
            var other => other
        };
        var target = Areas.Children.OfType<RadioButton>()
            .FirstOrDefault(tab => tab.Visibility == Visibility.Visible &&
                string.Equals(tab.Tag as string, normalized, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;
        if (target.IsChecked != true) target.IsChecked = true;
        else Area_Checked(target, new RoutedEventArgs());
        return true;
    }

    internal string? SelectAreaByIndex(int index)
    {
        var visible = Areas.Children.OfType<RadioButton>().Where(tab => tab.Visibility == Visibility.Visible).ToArray();
        if (index < 0 || index >= visible.Length) return null;
        var target = visible[index];
        if (target.IsChecked != true) target.IsChecked = true;
        else Area_Checked(target, new RoutedEventArgs());
        return target.Tag as string ?? currentArea;
    }

    internal object[] DescribeDebugTabs() => Areas.Children.OfType<RadioButton>()
        .Where(tab => tab.Visibility == Visibility.Visible)
        .Select((tab, idx) => new
        {
            group = "settings-section",
            index = idx,
            id = tab.Tag as string ?? "",
            title = tab.Content?.ToString() ?? "",
            selected = tab.IsChecked == true,
            enabled = tab.IsEnabled
        }).ToArray();

    internal object BuildDebugSnapshot()
    {
        return new
        {
            domain = "general",
            section = currentArea,
            tabs = DescribeDebugTabs(),
            appearance = new
            {
                themeColor = appearance.ThemeColor,
                canEdit = appearance.CanEdit,
                status = appearance.Status
            },
            windowBehavior = closePreferences is null ? null : new
            {
                behavior = closePreferences.Behavior.ToString(),
                canEdit = closePreferences.CanEdit,
                status = closePreferences.Status
            },
            canResetUserData = resetUserData is not null
        };
    }

    internal async Task<bool> TryDebugSetAsync(string key, JsonElement value)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case "themecolor" or "theme_color" or "theme":
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
                    !AppearanceColors.TryParse(value.GetString()!, out var parsed))
                    return false;
                if (!appearance.CanEdit) await appearance.LoadAsync();
                await appearance.SaveAsync(value.GetString()!);
                ThemeSelection.RestoreSelection();
                return string.Equals(appearance.ThemeColor, AppearanceColors.Hex(parsed), StringComparison.OrdinalIgnoreCase) &&
                       string.IsNullOrEmpty(appearance.Status);
            case "closebehavior" or "close_behavior":
                if (closePreferences is null || value.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<WindowCloseBehavior>(value.GetString(), ignoreCase: true, out var behavior))
                    return false;
                if (!closePreferences.CanEdit) await closePreferences.LoadAsync();
                bool saved = await closePreferences.SaveAsync(behavior);
                RestoreCloseBehavior();
                return saved;
            default:
                return false;
        }
    }
    private void Area_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is RadioButton current && NavigateArea(current, e.Key)) e.Handled = true;
    }
    internal bool NavigateArea(RadioButton current, VirtualKey key)
    {
        if (key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return false;
        var tabs = Areas.Children.OfType<RadioButton>().Where(tab => tab.Visibility == Visibility.Visible).ToArray();
        int index = Array.IndexOf(tabs, current);
        if (index < 0) return false;
        int next = key switch { VirtualKey.Home => 0, VirtualKey.End => tabs.Length - 1,
            VirtualKey.Left => (index + tabs.Length - 1) % tabs.Length, _ => (index + 1) % tabs.Length };
        tabs[next].IsChecked = true; tabs[next].Focus(FocusState.Programmatic);
        return true;
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
        if (closePreferences is null || CloseBehaviorSelection.SelectedItem is not ComboBoxItem { Tag: string choice } || !Enum.TryParse<WindowCloseBehavior>(choice, out var behavior) ||
            behavior == closePreferences.Behavior) return;
        await closePreferences.SaveAsync(behavior);
        RestoreCloseBehavior();
    }
    private sealed class StatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
}
