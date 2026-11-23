using MeowFilm.Desktop;
using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public partial class MusicSettingsView : UserControl, IDesktopHeaderSource
{
    private readonly RadioButton[] anchors;
    private readonly Border[] sections;
    private string searchAdvancedKind = "songs";
    private bool updatingNavigation;
    private long exclusionRequest, advancedRequest;
    private bool active, exclusionShowing, advancedShowing;
    private Task advancedPresentation = Task.CompletedTask;
    private double scrollOffset;
    private string currentSectionKey = "accounts";
    internal string CurrentSectionKey => currentSectionKey;
    public MusicSettingsController Settings { get; }
    public MusicAccountsController Accounts { get; }

    public MusicSettingsView(MusicSettingsController settings, MusicAccountsController accounts)
    {
        Settings = settings;
        Accounts = accounts;
        InitializeComponent();
        ModalStash.Children.Clear();
        AccountsPanel.Bind(accounts, settings);
        SourcesPage.Bind(settings);
        anchors = SettingsNavigation.Children.OfType<RadioButton>().ToArray();
        sections = [AccountsSection, SearchSection, QualitySection, MetadataSection];
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
        ExclusionAddButton.Unloaded += (_, _) => { if (!ExclusionAddButton.IsLoaded) RetireExclusionPopup(); };
        _ = DesktopVisual.ObserveVisibility(this, visible =>
        {
            if (active == visible) return;
            RunUiAction(VisibilityChanged);
        });
    }

    public DesktopHeaderContribution Header => new("设置", SearchVisible: false, SearchEnabled: false);
    public event EventHandler? HeaderChanged { add { } remove { } }

    private async void RunUiAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMetrics();
        RunUiAction(UpdateActivation);
    }
    private async void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        active = false;
        RetireExclusionPopup();
        AccountTransferButton.Flyout.Hide();
        AccountsPanel.CloseTransientPopups();
        advancedRequest++;
        DesktopModalHost.CloseFor(this, SearchAdvancedSurface);
        try { await Settings.DeactivateSettingsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _ = Settings.Notify(MusicRuntimeContext.ErrorText(error)); }
    }
    private Task UpdateActivation()
    {
        bool next = IsLoaded && DesktopVisual.IsVisible(this);
        if (active == next) return Task.CompletedTask;
        active = next;
        return active ? Settings.ActivateSettingsAsync() : Settings.DeactivateSettingsAsync();
    }
    private async Task VisibilityChanged()
    {
        if (!DesktopVisual.IsVisible(this))
        {
            AccountTransferButton.Flyout.Hide(); AccountsPanel.CloseTransientPopups();
            advancedRequest++;
            DesktopModalHost.CloseFor(this, SearchAdvancedSurface); RetireExclusionPopup();
        }
        await UpdateActivation();
    }
    private void AccountImport_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Accounts.ImportAccountsFileAsync());
    private void AccountExport_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Accounts.ExportAccountsFileAsync());
    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMetrics();
    }
    private void UpdateLayoutMetrics()
    {
        double width = Math.Min(1248, Math.Max(0, SettingsScroller.ViewportWidth - 12));
        if (width > 0 && (double.IsNaN(SectionsStack.Width) || Math.Abs(SectionsStack.Width - width) > .5)) SectionsStack.Width = width;
        double space = Math.Max(0, SettingsScroller.ViewportHeight - MetadataSection.ActualHeight);
        if (Math.Abs(EndSpace.Height - space) > .5) EndSpace.Height = space;
    }
    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (updatingNavigation || sender is not RadioButton { Tag: string key } anchor) return;
        currentSectionKey = NormalizeSectionName(key);
        AccountsPanel.CloseTransientPopups();
        bool sources = key == "sources";
        SettingsScroller.Visibility = sources ? Visibility.Collapsed : Visibility.Visible;
        SourcesPage.Visibility = sources ? Visibility.Visible : Visibility.Collapsed;
        anchor.StartBringIntoView();
        if (sources)
        {
            AccountTransferButton.Flyout.Hide();
            RunUiAction(Accounts.RetireAccountTransferAsync);
            RetireExclusionPopup();
            advancedRequest++;
            DesktopModalHost.CloseFor(this, SearchAdvancedSurface);
            return;
        }
        SettingsScroller.UpdateLayout(); UpdateLayoutMetrics();
        Border? section = key switch
        {
            "AccountsSection" => AccountsSection,
            "SearchSection" => SearchSection,
            "QualitySection" => QualitySection,
            "MetadataSection" => MetadataSection,
            _ => null
        };
        if (section is not null) SettingsScroller.ChangeView(null, section.TransformToVisual(SectionsStack).TransformPoint(new Point()).Y, null, true);
    }

    internal bool SelectDebugSection(string section)
    {
        string normalized = section.Trim().ToLowerInvariant();
        if (normalized == "exclusions")
        {
            currentSectionKey = "exclusions";
            AccountsPanel.CloseTransientPopups();
            SettingsScroller.Visibility = Visibility.Visible;
            SourcesPage.Visibility = Visibility.Collapsed;
            var qualityAnchor = anchors.FirstOrDefault(a => string.Equals(a.Tag as string, "QualitySection", StringComparison.OrdinalIgnoreCase));
            if (qualityAnchor is not null)
            {
                updatingNavigation = true;
                try { qualityAnchor.IsChecked = true; }
                finally { updatingNavigation = false; }
                qualityAnchor.StartBringIntoView();
            }
            SettingsScroller.UpdateLayout();
            UpdateLayoutMetrics();
            SettingsScroller.ChangeView(null, ExclusionSection.TransformToVisual(SectionsStack).TransformPoint(new Point()).Y, null, true);
            return true;
        }
        string tag = normalized switch
        {
            "accounts" or "aggregation" => "AccountsSection",
            "search" => "SearchSection",
            "quality" or "playback" => "QualitySection",
            "metadata" => "MetadataSection",
            "sources" => "sources",
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

    internal object[] DescribeDebugTabs() => anchors.Select((a, idx) => new
    {
        group = "settings-section",
        index = idx,
        id = NormalizeSectionName(a.Tag as string ?? ""),
        title = a.Content?.ToString() ?? "",
        selected = a.IsChecked == true,
        enabled = a.IsEnabled
    }).ToArray();

    internal async Task<object> DebugAccountLoginAsync(string? platform, bool close, string? mode, CancellationToken cancellationToken)
    {
        if (currentSectionKey != "accounts") SelectDebugSection("accounts");
        await UpdateActivation();
        return await AccountsPanel.DebugAccountLoginAsync(platform, close, mode, cancellationToken);
    }

    internal async Task<object> DebugAccountLogoutAsync(string? platform, bool close, bool confirm, CancellationToken cancellationToken)
    {
        if (currentSectionKey != "accounts") SelectDebugSection("accounts");
        await UpdateActivation();
        return await AccountsPanel.DebugAccountLogoutAsync(platform, close, confirm, cancellationToken);
    }

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
        updatingNavigation = true;
        try { anchors[next].IsChecked = true; }
        finally { updatingNavigation = false; }
        Section_Checked(anchors[next], new()); anchors[next].Focus(FocusState.Keyboard); return true;
    }
    private void Anchor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is RadioButton current) e.Handled = NavigateAnchor(current, e.Key);
    }
    private void Settings_ViewportChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMetrics();
    private void Settings_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (SettingsScroller.Visibility != Visibility.Visible) return;
        if (scrollOffset != SettingsScroller.VerticalOffset) AccountsPanel.CloseTransientPopups();
        scrollOffset = SettingsScroller.VerticalOffset;
        int selected = 0;
        for (int index = 0; index < sections.Length; index++)
            if (sections[index].TransformToVisual(SectionsStack).TransformPoint(new Point()).Y <= SettingsScroller.VerticalOffset + 1) selected = index;
        string nextKey = selected == 2 &&
            ExclusionSection.TransformToVisual(SectionsStack).TransformPoint(new Point()).Y <= SettingsScroller.VerticalOffset + 1
            ? "exclusions"
            : NormalizeSectionName(anchors[selected].Tag as string ?? "");
        if (anchors[selected].IsChecked == true && currentSectionKey == nextKey) return;
        currentSectionKey = nextKey;
        if (anchors[selected].IsChecked == true) return;
        updatingNavigation = true;
        try { anchors[selected].IsChecked = true; anchors[selected].StartBringIntoView(); }
        finally { updatingNavigation = false; }
    }
    private void ExclusionAdd_Click(object sender, RoutedEventArgs e)
    {
        long request = ++exclusionRequest;
        ExclusionSong.SelectedItem = ExclusionProvider.SelectedItem = ExclusionPlatform.SelectedItem = null;
        RunUiAction(async () =>
        {
            if (!await Settings.PrepareSourceExclusionFormAsync()) return;
            if (request == exclusionRequest && ExclusionAddButton.IsLoaded && DesktopVisual.IsVisible(ExclusionAddButton))
            {
                if (exclusionShowing) return;
                exclusionShowing = true;
                try { await DesktopModalHost.ShowSurfaceAsync(this, ExclusionPopupSurface, onOpened: () => ExclusionSong.Focus(FocusState.Programmatic)); }
                finally { exclusionShowing = false; }
            }
        });
    }
    private void RetireExclusionPopup()
    {
        exclusionRequest++;
        DesktopModalHost.CloseFor(this, ExclusionPopupSurface);
    }
    private void ExclusionCancel_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, ExclusionPopupSurface);
    private void ExclusionConfirm_Click(object sender, RoutedEventArgs e)
    {
        var song = ExclusionSong.SelectedItem as MusicExclusionOption;
        var provider = ExclusionProvider.SelectedItem as MusicExclusionOption;
        var platform = ExclusionPlatform.SelectedItem as MusicExclusionOption;
        if (song is null || provider is null || platform is null) return;
        DesktopModalHost.CloseFor(this, ExclusionPopupSurface);
        RunUiAction(() => Settings.AddSourceExclusionAsync(song, provider, platform));
    }
    private void ExclusionRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicSourceExclusionRow row }) RunUiAction(() => Settings.RestoreSourceExclusionAsync(row));
    }
    private void SearchHistoryClear_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Settings.ClearSearchHistoryAsync());
    private MusicSearchKindSettings SearchAdvancedSettings => searchAdvancedKind == "songs" ? Settings.SongSearch : Settings.PlaylistSearch;
    private void SearchAdvanced_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind }) return;
        long request = ++advancedRequest;
        DesktopModalHost.CloseFor(this, SearchAdvancedSurface);
        RunUiAction(async () =>
        {
            try { await advancedPresentation; }
            catch (OperationCanceledException) { }
            if (request != advancedRequest || !IsLoaded || !active || !DesktopVisual.IsVisible(this) || advancedShowing) return;
            searchAdvancedKind = kind;
            ShowSearchAdvanced();
            advancedShowing = true;
            var presentation = ShowSearchAdvancedSurfaceAsync();
            advancedPresentation = presentation;
            await presentation;
        });
    }
    private async Task ShowSearchAdvancedSurfaceAsync()
    {
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(
                this,
                SearchAdvancedSurface,
                onOpened: () => { SearchPagesBox.Focus(FocusState.Programmatic); SearchPagesBox.SelectAll(); });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
        finally
        {
            advancedShowing = false;
            if (IsLoaded && active)
                await CommitSearchAdvancedSafeAsync();
        }
    }
    private async Task CommitSearchAdvancedSafeAsync()
    {
        try { await CommitSearchAdvancedAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
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
    private void SearchAdvancedBox_LostFocus(object sender, RoutedEventArgs e) => RunUiAction(CommitSearchAdvancedAsync);
    private void SearchAdvancedPopup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            // Discard unsaved edits.
            ShowSearchAdvanced();
            DesktopModalHost.CloseFor(this, SearchAdvancedSurface); e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            RunUiAction(async () =>
            {
                await CommitSearchAdvancedAsync();
                if (IsLoaded) ShowSearchAdvanced();
            });
        }
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
        await Settings.SetSearchAdvancedAsync(searchAdvancedKind, pages, concurrency, cacheMinutes);
        if (IsLoaded) ShowSearchAdvanced(keepEditing: true);
    }
    private void PlaybackQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { SelectedItem: string value } || value == Settings.PlaybackQualityRequest) return;
        RunUiAction(() => Settings.SetPlaybackQualityRequestAsync(value));
    }
    private void Priority_Move(object sender, SettingsPriorityMoveEventArgs e)
    {
        if (sender is not SettingsPriorityList { Tag: string kind }) return;
        RunUiAction(() => (e.Source, e.Target) switch
        {
            (MusicPlatformPriorityRow source, MusicPlatformPriorityRow target) when kind == "metadata" => Settings.MoveMetadataPlatformAsync(source, target),
            (MusicPlatformPriorityRow source, MusicPlatformPriorityRow target) => Settings.MoveSearchPlatformAsync(kind, source, target),
            (MusicQualityRow source, MusicQualityRow target) => Settings.MoveQualityAsync(source, target, kind == "spatial"),
            _ => Task.CompletedTask
        });
    }
    private void Priority_Toggle(object sender, SettingsPriorityToggleEventArgs e)
    {
        if (sender is not SettingsPriorityList { Tag: string kind }) return;
        RunUiAction(() => e.Item switch
        {
            MusicPlatformPriorityRow row when kind == "metadata" => Settings.SetMetadataEnabledAsync(row, e.Enabled),
            MusicPlatformPriorityRow row => Settings.SetSearchPlatformEnabledAsync(kind, row, e.Enabled),
            MusicQualityRow row => Settings.SetQualityEnabledAsync(row, e.Enabled, kind == "spatial"),
            _ => Task.CompletedTask
        });
    }
}
