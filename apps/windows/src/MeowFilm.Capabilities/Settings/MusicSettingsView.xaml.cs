using MeowFilm.Desktop;
using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private bool active, exclusionShowing, advancedShowing;
    private double scrollOffset;
    private string currentSectionKey = "accounts";
    internal string CurrentSectionKey => currentSectionKey;

    public MusicSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        ModalStash.Children.Clear();
        DataContext = module.SettingsState;
        AccountsPanel.DataContext = module.AccountsState;
        anchors = SettingsNavigation.Children.OfType<RadioButton>().ToArray();
        sections = [AccountsSection, SearchSection, QualitySection, MetadataSection];
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
        AccountTransferButton.RegisterPropertyChangedCallback(VisibilityProperty, async (_, _) =>
        {
            if (!DesktopVisual.IsVisible(AccountTransferButton))
            {
                AccountTransferButton.Flyout.Hide();
                await module.RetireAccountTransferAsync();
            }
        });
        ExclusionAddButton.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        { if (!DesktopVisual.IsVisible(ExclusionAddButton)) RetireExclusionPopup(); });
        ExclusionAddButton.Unloaded += (_, _) => RetireExclusionPopup();
        _ = DesktopVisual.ObserveVisibility(this, async visible =>
        { if (active != visible) await VisibilityChanged(); });
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
        active = false;
        RetireExclusionPopup();
        AccountTransferButton.Flyout.Hide();
        AccountsPanel.CloseTransientPopups();
        DesktopModalHost.CloseFor(this, SearchAdvancedSurface);
        await module.DeactivateSettingsAsync();
    }
    private Task UpdateActivation()
    {
        bool next = IsLoaded && DesktopVisual.IsVisible(this);
        if (active == next) return Task.CompletedTask;
        active = next;
        return active ? module.ActivateSettingsAsync() : module.DeactivateSettingsAsync();
    }
    private async Task VisibilityChanged()
    {
        if (!DesktopVisual.IsVisible(this))
        {
            AccountTransferButton.Flyout.Hide(); AccountsPanel.CloseTransientPopups();
            DesktopModalHost.CloseFor(this, SearchAdvancedSurface); RetireExclusionPopup();
        }
        await UpdateActivation();
    }
    private void AccountTransfer_Click(object sender, RoutedEventArgs e)
    {
        AccountTransferButton.Flyout.ShowAt(AccountTransferButton);
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
        if (sections is null || updatingNavigation || sender is not RadioButton { Tag: string key } anchor) return;
        currentSectionKey = NormalizeSectionName(key);
        AccountsPanel.CloseTransientPopups();
        bool sources = key == "sources";
        SettingsScroller.Visibility = sources ? Visibility.Collapsed : Visibility.Visible;
        SourcesPage.Visibility = sources ? Visibility.Visible : Visibility.Collapsed;
        anchor.Focus(FocusState.Programmatic); anchor.StartBringIntoView();
        if (sources) { RetireExclusionPopup(); DesktopModalHost.CloseFor(this, SearchAdvancedSurface); return; }
        SettingsScroller.UpdateLayout(); UpdateLayoutMetrics();
        if (FindName(key) is Border section) SettingsScroller.ChangeView(null, section.TransformToVisual(SectionsStack).TransformPoint(new Point()).Y, null, true);
    }

    internal bool SelectDebugSection(string section)
    {
        if (anchors is null) return false;
        string tag = section.Trim().ToLowerInvariant() switch
        {
            "accounts" or "aggregation" => "AccountsSection",
            "search" => "SearchSection",
            "quality" or "playback" => "QualitySection",
            "metadata" => "MetadataSection",
            "sources" or "exclusions" => "sources",
            _ => ""
        };
        if (tag.Length == 0) return false;
        var anchor = anchors.FirstOrDefault(a => string.Equals(a.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (anchor is null) return false;
        updatingNavigation = true;
        try { anchor.IsChecked = true; }
        finally { updatingNavigation = false; }
        Section_Checked(anchor, new RoutedEventArgs());
        return true;
    }

    internal object[] DescribeDebugTabs() => (anchors ?? []).Select((a, idx) => new
    {
        group = "settings-section",
        index = idx,
        id = NormalizeSectionName(a.Tag as string ?? ""),
        title = a.Content?.ToString() ?? "",
        selected = a.IsChecked == true,
        enabled = a.IsEnabled
    }).ToArray();

    private static string NormalizeSectionName(string tag) => tag switch
    {
        "AccountsSection" => "accounts",
        "SearchSection" => "search",
        "QualitySection" => "quality",
        "MetadataSection" => "metadata",
        "sources" => "sources",
        _ => tag.ToLowerInvariant()
    };
    internal bool NavigateAnchor(RadioButton current, VirtualKey key)
    {
        int index = Array.IndexOf(anchors, current);
        if (index < 0 || !IsLoaded || !DesktopVisual.IsVisible(this) || key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return false;
        int next = key switch { VirtualKey.Home => 0, VirtualKey.End => anchors.Length - 1,
            VirtualKey.Left => (index + anchors.Length - 1) % anchors.Length, _ => (index + 1) % anchors.Length };
        anchors[next].IsChecked = true; Section_Checked(anchors[next], new()); return true;
    }
    private void Anchor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is RadioButton current) e.Handled = NavigateAnchor(current, e.Key);
    }
    private void Settings_ViewportChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMetrics();
    private void Settings_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sections is null || SettingsScroller.Visibility != Visibility.Visible) return;
        if (scrollOffset != SettingsScroller.VerticalOffset) AccountsPanel.CloseTransientPopups();
        scrollOffset = SettingsScroller.VerticalOffset;
        int selected = 0;
        for (int index = 0; index < sections.Length; index++)
            if (sections[index].TransformToVisual(SectionsStack).TransformPoint(new Point()).Y <= SettingsScroller.VerticalOffset + 1) selected = index;
        updatingNavigation = true;
        try { anchors[selected].IsChecked = true; anchors[selected].StartBringIntoView(); }
        finally { updatingNavigation = false; }
    }
    private async void ExclusionAdd_Click(object sender, RoutedEventArgs e)
    {
        long request = ++exclusionRequest;
        ExclusionSong.SelectedItem = ExclusionProvider.SelectedItem = ExclusionPlatform.SelectedItem = null;
        await module.PrepareSourceExclusionFormAsync();
        if (request == exclusionRequest && ExclusionAddButton.IsLoaded && DesktopVisual.IsVisible(ExclusionAddButton))
        {
            if (exclusionShowing) return;
            exclusionShowing = true;
            try { await DesktopModalHost.ShowSurfaceAsync(this, ExclusionPopupSurface, onOpened: () => ExclusionSong.Focus(FocusState.Programmatic)); }
            finally { exclusionShowing = false; }
        }
    }
    private void RetireExclusionPopup()
    {
        exclusionRequest++;
        DesktopModalHost.CloseFor(this, ExclusionPopupSurface);
    }
    private void ExclusionCancel_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, ExclusionPopupSurface);
    private async void ExclusionConfirm_Click(object sender, RoutedEventArgs e)
    {
        var song = ExclusionSong.SelectedItem as MusicExclusionOption;
        var provider = ExclusionProvider.SelectedItem as MusicExclusionOption;
        var platform = ExclusionPlatform.SelectedItem as MusicExclusionOption;
        if (song is null || provider is null || platform is null) return;
        DesktopModalHost.CloseFor(this, ExclusionPopupSurface);
        await module.AddSourceExclusionAsync(song, provider, platform);
    }
    private async void ExclusionRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicSourceExclusionRow row }) await module.RestoreSourceExclusionAsync(row);
    }
    private async void SearchHistoryClear_Click(object sender, RoutedEventArgs e) => await module.ClearSearchHistoryAsync();
    private MusicSearchKindSettings SearchAdvancedSettings => searchAdvancedKind == "songs" ? module.SongSearch : module.PlaylistSearch;
    private async void SearchAdvanced_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } button) return;
        DesktopModalHost.CloseFor(this, SearchAdvancedSurface);
        searchAdvancedKind = kind;
        ShowSearchAdvanced();
        if (advancedShowing) return;
        advancedShowing = true;
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(
                this,
                SearchAdvancedSurface,
                onOpened: () => { SearchPagesBox.Focus(FocusState.Programmatic); SearchPagesBox.SelectAll(); });
        }
        finally
        {
            advancedShowing = false;
            await CommitSearchAdvancedAsync();
        }
    }
    private void ShowSearchAdvanced(bool keepEditing = false)
    {
        var settings = SearchAdvancedSettings;
        foreach (var (box, value) in new[] { (SearchPagesBox, settings.Pages),
            (SearchConcurrencyBox, settings.Concurrency), (SearchCacheBox, settings.CacheMinutes) })
            // Do not overwrite the field the user has moved on to edit.
            if (!keepEditing || !DesktopVisual.ContainsFocus(box)) box.Text = value.ToString(CultureInfo.InvariantCulture);
    }
    private void SearchAdvancedClose_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, SearchAdvancedSurface);
    private async void SearchAdvancedBox_LostFocus(object sender, RoutedEventArgs e) => await CommitSearchAdvancedAsync();
    private async void SearchAdvancedPopup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            // Discard unsaved edits.
            ShowSearchAdvanced();
            DesktopModalHost.CloseFor(this, SearchAdvancedSurface); e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter) { e.Handled = true; await CommitSearchAdvancedAsync(); ShowSearchAdvanced(); }
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
