using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public partial class MusicSettingsView : UserControl, MeowFilm.Desktop.IDesktopHeaderSource
{
    private readonly MusicCapability module;
    private readonly RadioButton[] anchors;
    private readonly Border[] sections;
    private string searchAdvancedKind = "songs";
    private bool updatingNavigation;
    private long exclusionRequest;

    public MusicSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        anchors = SettingsNavigation.Children.OfType<RadioButton>().ToArray();
        sections = [AccountsSection, SearchSection, QualitySection, MetadataSection];
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
        AccountTransferButton.IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is false)
            {
                AccountTransferButton.ContextMenu.IsOpen = false;
                await module.RetireAccountTransferAsync();
            }
        };
        ExclusionAddButton.IsVisibleChanged += (_, e) => { if (e.NewValue is false) RetireExclusionPopup(); };
        ExclusionAddButton.Unloaded += (_, _) => RetireExclusionPopup();
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { AccountTransferButton.ContextMenu.IsOpen = false; AccountsPanel.CloseTransientPopups(); SearchAdvancedPopup.IsOpen = false; }
            if (IsLoaded) await UpdateActivation();
        };
    }

    public MeowFilm.Desktop.DesktopHeaderContribution Header => new("设置", SearchVisible: false, SearchEnabled: false);
    public event EventHandler? HeaderChanged { add { } remove { } }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMetrics();
        await UpdateActivation();
    }
    private async void View_Unloaded(object sender, RoutedEventArgs e)
    {
        AccountTransferButton.ContextMenu.IsOpen = false;
        AccountsPanel.CloseTransientPopups();
        SearchAdvancedPopup.IsOpen = false;
        await module.DeactivateSettingsAsync();
    }
    private Task UpdateActivation() => IsLoaded && IsVisible
        ? module.ActivateSettingsAsync() : module.DeactivateSettingsAsync();
    private void AccountTransfer_Click(object sender, RoutedEventArgs e)
    {
        AccountTransferButton.ContextMenu.PlacementTarget = AccountTransferButton;
        AccountTransferButton.ContextMenu.IsOpen = true;
    }
    private async void AccountImport_Click(object sender, RoutedEventArgs e) => await module.ImportAccountsFileAsync();
    private async void AccountExport_Click(object sender, RoutedEventArgs e) => await module.ExportAccountsFileAsync();
    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
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
        updatingNavigation = true;
        try { anchors[selected].IsChecked = true; anchors[selected].BringIntoView(); }
        finally { updatingNavigation = false; }
    }
    private void Settings_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || !SettingsScroller.IsVisible) return;
        for (DependencyObject? current = e.OriginalSource as DependencyObject; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ComboBox { IsDropDownOpen: true } || ReferenceEquals(current, ExclusionList)) return;
        double distance = SystemParameters.WheelScrollLines < 0 ? SettingsScroller.ViewportHeight :
            SystemParameters.WheelScrollLines * 16d;
        SettingsScroller.ScrollToVerticalOffset(SettingsScroller.VerticalOffset - e.Delta / 120d * distance);
        e.Handled = true;
    }
    private async void ExclusionAdd_Click(object sender, RoutedEventArgs e)
    {
        long request = ++exclusionRequest;
        ExclusionSong.SelectedItem = ExclusionProvider.SelectedItem = ExclusionPlatform.SelectedItem = null;
        await module.PrepareSourceExclusionFormAsync();
        if (request == exclusionRequest && ExclusionAddButton.IsLoaded && ExclusionAddButton.IsVisible)
        { ExclusionPopup.IsOpen = true; ExclusionSong.Focus(); }
    }
    private void RetireExclusionPopup()
    {
        exclusionRequest++;
        ExclusionPopup.IsOpen = false;
    }
    private void ExclusionCancel_Click(object sender, RoutedEventArgs e) => ExclusionPopup.IsOpen = false;
    private async void ExclusionConfirm_Click(object sender, RoutedEventArgs e)
    {
        var song = ExclusionSong.SelectedItem as MusicExclusionOption;
        var provider = ExclusionProvider.SelectedItem as MusicExclusionOption;
        var platform = ExclusionPlatform.SelectedItem as MusicExclusionOption;
        if (song is null || provider is null || platform is null) return;
        ExclusionPopup.IsOpen = false;
        await module.AddSourceExclusionAsync(song, provider, platform);
    }
    private async void ExclusionRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicSourceExclusionRow row }) await module.RestoreSourceExclusionAsync(row);
    }
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
