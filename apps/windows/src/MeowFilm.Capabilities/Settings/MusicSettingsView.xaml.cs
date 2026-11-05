using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public partial class MusicSettingsView : UserControl, MeowFilm.Desktop.IDesktopHeaderSource
{
    private readonly MusicCapability module;
    private readonly RadioButton[] anchors;
    private readonly Border[] sections;
    private readonly StackPanel areaTabs;
    private string area = "music";
    private string searchAdvancedKind = "songs";
    private bool updatingNavigation;

    public MusicSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        // Right-aligned below its button; the arrow sits over the button.
        SearchAdvancedPopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Horizontal)];
        anchors = SettingsNavigation.Children.OfType<RadioButton>().ToArray();
        sections = [AccountsSection, SearchSection, QualitySection, CacheSection, AppearanceSection, MetadataSection];
        // The anchor underline reserves 4 DIP below its text; offset it to centre the labels on the heading.
        areaTabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 4, 0, 0) };
        // The tabs live in the shell header, outside this view's resource scope.
        areaTabs.Resources.MergedDictionaries.Add(Resources.MergedDictionaries[0]);
        AutomationProperties.SetName(areaTabs, "设置类别");
        var anchor = (Style)FindResource("Settings.Anchor");
        foreach (var (key, name) in new[] { ("music", "音乐"), ("video", "影视"), ("books", "小说") })
        {
            var tab = new RadioButton { Content = name, Tag = key, GroupName = "SettingsArea", Style = anchor, IsChecked = key == area };
            tab.Checked += Area_Checked;
            areaTabs.Children.Add(tab);
        }
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { AccountsPanel.CloseTransientPopups(); SearchAdvancedPopup.IsOpen = false; }
            if (IsLoaded) await UpdateActivation();
        };
    }

    public MeowFilm.Desktop.DesktopHeaderContribution Header => new("设置", SearchVisible: false, SearchEnabled: false, Slot: areaTabs);
    public event EventHandler? HeaderChanged { add { } remove { } }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMetrics();
        await UpdateActivation();
    }
    private async void View_Unloaded(object sender, RoutedEventArgs e)
    {
        AccountsPanel.CloseTransientPopups();
        await module.DeactivateSettingsAsync();
    }
    private Task UpdateActivation() => IsLoaded && IsVisible && area == "music"
        ? module.ActivateSettingsAsync() : module.DeactivateSettingsAsync();
    private async void Area_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key } || key == area) return;
        area = key;
        AccountsPanel.CloseTransientPopups();
        MusicContent.Visibility = area == "music" ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) await UpdateActivation();
    }
    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AccountsPanel?.CloseTransientPopups();
        UpdateLayoutMetrics();
    }
    private void UpdateLayoutMetrics()
    {
        if (SettingsScroller is null || SectionsStack is null || MetadataSection is null) return;
        double width = Math.Min(1248, Math.Max(0, SettingsScroller.ViewportWidth - 12));
        if (width > 0 && (double.IsNaN(SectionsStack.Width) || Math.Abs(SectionsStack.Width - width) > .5)) SectionsStack.Width = width;
        double space = Math.Max(0, SettingsScroller.ViewportHeight - MetadataSection.ActualHeight);
        if (Math.Abs(EndSpace.Height - space) > .5) EndSpace.Height = space;
    }
    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || updatingNavigation || sender is not RadioButton { Tag: string key } anchor) return;
        AccountsPanel.CloseTransientPopups();
        bool sources = key == "sources";
        SettingsScroller.Visibility = sources ? Visibility.Collapsed : Visibility.Visible;
        SourcesPage.Visibility = sources ? Visibility.Visible : Visibility.Collapsed;
        anchor.Focus(); anchor.BringIntoView();
        if (sources) return;
        SettingsScroller.UpdateLayout(); UpdateLayoutMetrics();
        if (FindName(key) is Border section) SettingsScroller.ScrollToVerticalOffset(section.TranslatePoint(new Point(), SectionsStack).Y);
    }
    private void Anchor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not RadioButton current || e.Key is not (Key.Home or Key.End or Key.Left or Key.Right)) return;
        int index = Array.IndexOf(anchors, current);
        int next = e.Key switch { Key.Home => 0, Key.End => anchors.Length - 1, Key.Left => (index + anchors.Length - 1) % anchors.Length, _ => (index + 1) % anchors.Length };
        anchors[next].IsChecked = true; Section_Checked(anchors[next], new()); e.Handled = true;
    }
    private void Settings_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SettingsScroller) || sections is null || SettingsScroller.Visibility != Visibility.Visible) return;
        if (e.VerticalChange != 0) AccountsPanel.CloseTransientPopups();
        if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0) UpdateLayoutMetrics();
        int selected = 0;
        for (int index = 0; index < sections.Length; index++)
            if (sections[index].TranslatePoint(new Point(), SectionsStack).Y <= SettingsScroller.VerticalOffset + 1) selected = index;
        int anchorIndex = selected switch { 0 => 0, 1 => 1, 2 or 3 => 2, 4 => 3, _ => 4 };
        updatingNavigation = true;
        try { anchors[anchorIndex].IsChecked = true; anchors[anchorIndex].BringIntoView(); }
        finally { updatingNavigation = false; }
    }
    private void Settings_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || !SettingsScroller.IsVisible) return;
        for (DependencyObject? current = e.OriginalSource as DependencyObject; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ComboBox { IsDropDownOpen: true }) return;
        double distance = SystemParameters.WheelScrollLines < 0 ? SettingsScroller.ViewportHeight :
            SystemParameters.WheelScrollLines * 16d;
        SettingsScroller.ScrollToVerticalOffset(SettingsScroller.VerticalOffset - e.Delta / 120d * distance);
        e.Handled = true;
    }
    private async void ClearPlaybackCache_Click(object sender, RoutedEventArgs e) => await module.ClearPlaybackCacheAsync();
    private async void SearchHistoryClear_Click(object sender, RoutedEventArgs e) => await module.ClearSearchHistoryAsync();
    private MusicSearchKindSettings SearchAdvancedSettings => searchAdvancedKind == "songs" ? module.SongSearch : module.PlaylistSearch;
    private void SearchAdvanced_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } button) return;
        SearchAdvancedPopup.IsOpen = false;
        searchAdvancedKind = kind;
        ShowSearchAdvanced();
        SearchAdvancedPopup.PlacementTarget = button;
        SearchAdvancedPopup.IsOpen = true;
    }
    private void ShowSearchAdvanced(bool keepEditing = false)
    {
        var settings = SearchAdvancedSettings;
        foreach (var (box, value) in new[] { (SearchPagesBox, settings.Pages),
            (SearchConcurrencyBox, settings.Concurrency), (SearchCacheBox, settings.CacheMinutes) })
            // Do not overwrite the field the user has moved on to edit.
            if (!keepEditing || !box.IsKeyboardFocusWithin) box.Text = value.ToString(CultureInfo.InvariantCulture);
    }
    private void SearchAdvancedPopup_Opened(object? sender, EventArgs e) { SearchPagesBox.Focus(); SearchPagesBox.SelectAll(); }
    private async void SearchAdvancedPopup_Closed(object? sender, EventArgs e)
    {
        SearchAdvancedPopup.PlacementTarget = null;
        await CommitSearchAdvancedAsync();
    }
    private async void SearchAdvancedBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => await CommitSearchAdvancedAsync();
    private async void SearchAdvancedPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Discard unsaved edits.
            ShowSearchAdvanced();
            SearchAdvancedPopup.IsOpen = false; e.Handled = true;
        }
        else if (e.Key == Key.Enter) { e.Handled = true; await CommitSearchAdvancedAsync(); ShowSearchAdvanced(); }
    }
    /// <summary>Saves valid input immediately; invalid input reverts to the saved value.</summary>
    private async Task CommitSearchAdvancedAsync()
    {
        static int? Read(TextBox box, Func<int, bool> valid) =>
            int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            valid(value) ? value : null;
        var settings = SearchAdvancedSettings;
        int pages = Read(SearchPagesBox, MusicSearchSettings.ValidPages) ?? settings.Pages;
        int concurrency = Read(SearchConcurrencyBox, MusicSearchSettings.ValidConcurrency) ?? settings.Concurrency;
        int cacheMinutes = Read(SearchCacheBox, MusicSearchSettings.ValidCacheMinutes) ?? settings.CacheMinutes;
        await module.SetSearchAdvancedAsync(searchAdvancedKind, pages, concurrency, cacheMinutes);
        ShowSearchAdvanced(keepEditing: true);
    }
    private async void PlaybackQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { SelectedItem: string value } || value == module.PlaybackQualityRequest) return;
        await module.SetPlaybackQualityRequestAsync(value);
    }
    private async void CatalogSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !IsVisible || area != "music" ||
            sender is not ComboBox { SelectedValue: string platform } picker ||
            platform == module.CatalogSourcePlatform) return;
        await module.SetCatalogSourceAsync(platform);
        picker.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
    }
    private async void CatalogSourceRetry_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && IsVisible && area == "music") await module.ReadCatalogSourceSettingsAsync();
    }
    private async void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { Tag: string setting, SelectedValue: string value }) return;
        string current = setting switch { "playlistView" => module.AppearancePlaylistView, "colorMode" => module.AppearanceColorMode, "colorPalette" => module.AppearanceColorPalette, _ => value };
        if (current != value) await module.SetAppearanceAsync(setting, value);
    }
    private async void Priority_Move(object sender, SettingsPriorityMoveEventArgs e)
    {
        if (sender is not SettingsPriorityList { Tag: string kind }) return;
        await ((e.Source, e.Target) switch
        {
            (MusicSearchPlatformRow source, MusicSearchPlatformRow target) => module.MoveSearchPlatformAsync(kind, source, target),
            (MusicQualityRow source, MusicQualityRow target) => module.MoveQualityAsync(source, target, kind == "spatial"),
            (MusicMetadataPlatformRow source, MusicMetadataPlatformRow target) => module.MoveMetadataPlatformAsync(source, target),
            _ => Task.CompletedTask
        });
    }
    private async void Priority_Toggle(object sender, SettingsPriorityToggleEventArgs e)
    {
        if (sender is not SettingsPriorityList { Tag: string kind }) return;
        await (e.Item switch
        {
            MusicSearchPlatformRow row => module.SetSearchPlatformEnabledAsync(kind, row, e.Enabled),
            MusicQualityRow row => module.SetQualityEnabledAsync(row, e.Enabled, kind == "spatial"),
            MusicMetadataPlatformRow row => module.SetMetadataEnabledAsync(row, e.Enabled),
            _ => Task.CompletedTask
        });
    }
}
