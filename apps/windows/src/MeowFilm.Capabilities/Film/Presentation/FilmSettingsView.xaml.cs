using Windows.System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView : UserControl, IDesktopHeaderSource, INotifyPropertyChanged
{
    private readonly FilmRuleService rules;
    private readonly FilmSourceService sources;
    private readonly FilmMetadataService? metadata;
    private readonly PanAccounts panAccounts;
    private readonly FilmPlaybackSettingsView? playbackSettings;
    private string section = "rules";
    internal string CurrentDebugSection => section;
    private bool initialized, observingSources;
    private long activation;
    private Func<Task>? popupAction;
    private Control? popupAnchor;

    public FilmSettingsView(FilmRuleService rules, FilmSourceService sources, FilmMetadataService? metadata = null)
        : this(rules, sources, sources.Storage, null, () => null, metadata) { }

    internal FilmSettingsView(FilmRuleService rules, FilmSourceService sources, IStorageDatabase store,
        FilmPlaybackSettings? business, Func<FilmPlaybackTarget?> target,
        FilmMetadataService? metadata = null)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.metadata = metadata;
        panAccounts = new(sources.Runtime, store);
        InitializeComponent();
        PanPage.DataContext = panAccounts;
        if (business is not null)
        {
            playbackSettings = new(business, target, "clean");
            SiteCleanHost.Content = playbackSettings;
            BusinessNavigation.Visibility = Visibility.Visible;
        }
        DataContext = this;
        foreach (var editor in RuleEditors)
        {
            editor.ActionRequested += Rule_ActionRequested;
            editor.DraftChanged += (_, _) => InvalidateRuleResults();
        }
        ConfigureSourceLists();
        initialized = true;
        _ = new FilmPresentationVisibility(this, _ => View_VisibilityChanged());
    }

    public DesktopHeaderContribution Header => new("设置", SearchVisible: false, SearchEnabled: false);
    public event EventHandler? HeaderChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Active => initialized && IsLoaded && MeowFilm.Desktop.DesktopVisual.IsVisible(this);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Notify(string message) => Notices.Message = message;

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (!observingSources) { sources.Changed += Sources_Changed; observingSources = true; }
        await ActivateCurrentAsync();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        Deactivate();
        if (observingSources) { sources.Changed -= Sources_Changed; observingSources = false; }
    }

    private async void View_VisibilityChanged()
    {
        if (!initialized) return;
        if (!MeowFilm.Desktop.DesktopVisual.IsVisible(this)) Deactivate();
        else if (IsLoaded) await ActivateCurrentAsync();
    }

    private void Deactivate()
    {
        activation++;
        ruleOperation?.Cancel();
        sourceOperation?.Cancel();
        metadataOperation?.Cancel();
        playbackSettings?.Deactivate();
        ClosePopup(restoreFocus: false);
    }
    internal Task PreparePlaybackSettingsShutdownAsync() => Task.WhenAll(
        playbackSettings?.PrepareShutdownAsync() ?? Task.CompletedTask, panAccounts.PrepareShutdownAsync());
    internal void ResumePlaybackSettings()
    {
        playbackSettings?.Resume();
        panAccounts.Resume();
    }

    private async Task ActivateCurrentAsync()
    {
        if (!Active) return;
        if (section == "pan") await panAccounts.ActivateAsync();
        else if (section == "pan-routing") await LoadPanInterceptionAsync();
        else if (section == "rules")
        {
            if ((!RulesLoaded || rulesReloadPending) && !RulesBusy && !RulesFailed) await LoadRulesAsync();
            if (Active && section == "rules") await PresentPlaybackSettingsAsync("clean");
        }
        else if (section == "metadata")
        {
            if (!MetadataLoaded && !MetadataBusy && !MetadataFailed) await LoadMetadataAsync();
        }
        else if (section is not ("cats" or "videos"))
        {
            await PresentPlaybackSettingsAsync("preferences");
        }
        else if (!SourcesBusy && !SourcesFailed)
        {
            if (!SourcesLoaded)
                await LoadSourcesAsync();
            else SynchronizeSources();
        }
    }
    private async Task PresentPlaybackSettingsAsync(string area)
    {
        if (playbackSettings is null) return;
        bool clean = area == "clean";
        if (clean && SiteCleanHost.Content is null || !clean && BusinessPage.Children.Count == 0)
        {
            SiteCleanHost.Content = null;
            BusinessPage.Children.Clear();
            playbackSettings.SelectSection(area);
            if (clean) SiteCleanHost.Content = playbackSettings;
            else BusinessPage.Children.Add(playbackSettings);
        }
        await playbackSettings.ShowAsync(area);
    }

    private async void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || sender is not RadioButton { Tag: string key } anchor || key == section) return;
        Deactivate();
        section = key;
        RulesPage.Visibility = key == "rules" ? Visibility.Visible : Visibility.Collapsed;
        CatsPage.Visibility = key == "cats" ? Visibility.Visible : Visibility.Collapsed;
        VideosPage.Visibility = key == "videos" ? Visibility.Visible : Visibility.Collapsed;
        MetadataPage.Visibility = key == "metadata" ? Visibility.Visible : Visibility.Collapsed;
        PanPage.Visibility = key == "pan" ? Visibility.Visible : Visibility.Collapsed;
        PanInterceptionPage.Visibility = key == "pan-routing" ? Visibility.Visible : Visibility.Collapsed;
        BusinessPage.Visibility = key is "rules" or "cats" or "videos" or "metadata" or "pan" or "pan-routing" ? Visibility.Collapsed : Visibility.Visible;
        anchor.Focus(FocusState.Programmatic); anchor.StartBringIntoView();
        await ActivateCurrentAsync();
    }

    internal async Task<bool> SelectDebugSectionAsync(string key)
    {
        var normalized = key.Trim().ToLowerInvariant() switch
        {
            "rules" or "search-rules" or "matching" => "rules",
            "cats" => "cats",
            "videos" => "videos",
            "metadata" or "datasources" => "metadata",
            "pan" => "pan",
            "pan-routing" or "paninterception" => "pan-routing",
            "preferences" or "player" => "preferences",
            _ => ""
        };
        if (normalized.Length == 0) return false;
        var radio = SettingsNavigation.Children.OfType<RadioButton>()
            .Concat(BusinessNavigation.Children.OfType<RadioButton>())
            .FirstOrDefault(r => string.Equals(r.Tag as string, normalized, StringComparison.OrdinalIgnoreCase));
        if (radio is null) return false;
        Deactivate();
        section = normalized;
        radio.IsChecked = true;
        RulesPage.Visibility = normalized == "rules" ? Visibility.Visible : Visibility.Collapsed;
        CatsPage.Visibility = normalized == "cats" ? Visibility.Visible : Visibility.Collapsed;
        VideosPage.Visibility = normalized == "videos" ? Visibility.Visible : Visibility.Collapsed;
        MetadataPage.Visibility = normalized == "metadata" ? Visibility.Visible : Visibility.Collapsed;
        PanPage.Visibility = normalized == "pan" ? Visibility.Visible : Visibility.Collapsed;
        PanInterceptionPage.Visibility = normalized == "pan-routing" ? Visibility.Visible : Visibility.Collapsed;
        BusinessPage.Visibility = normalized is "rules" or "cats" or "videos" or "metadata" or "pan" or "pan-routing"
            ? Visibility.Collapsed : Visibility.Visible;
        await ActivateCurrentAsync();
        return true;
    }

    internal async Task<bool> TryDebugSetPanInterceptionAsync(string provider, bool enabled, CancellationToken cancellationToken)
    {
        var p = provider.Trim().ToLowerInvariant();
        if (p is not ("baidu" or "quark" or "uc" or "tianyi" or "yidong")) return false;
        var saved = await sources.SavePanInterceptionAsync(p, enabled, cancellationToken);
        ReplacePanInterception(saved);
        return true;
    }

    internal Task RefreshDebugSourcesAsync(CancellationToken cancellationToken) =>
        sources.InitializeAsync(cancellationToken);

    internal object[] DescribeDebugTabs() => SettingsNavigation.Children.OfType<RadioButton>()
        .Concat(BusinessNavigation.Children.OfType<RadioButton>())
        .Where(r => r.Visibility == Visibility.Visible)
        .Select((r, idx) => new
        {
            group = "settings-section",
            index = idx,
            id = r.Tag as string ?? "",
            title = r.Content?.ToString() ?? "",
            selected = string.Equals(r.Tag as string, section, StringComparison.OrdinalIgnoreCase),
            enabled = r.IsEnabled
        }).ToArray();

    internal object BuildDebugSnapshot() => new
    {
        domain = "film",
        section,
        tabs = DescribeDebugTabs(),
        rulesLoaded = RulesLoaded,
        rulesBusy = RulesBusy,
        rulesFailed = RulesFailed,
        sourcesBusy = SourcesBusy,
        sourcesFailed = SourcesFailed,
        hasMetadataSettings = HasMetadataSettings,
        metadataLoaded = MetadataLoaded,
        panInterception = new
        {
            baidu = BaiduInterception.IsChecked == true,
            quark = QuarkInterception.IsChecked == true,
            uc = UcInterception.IsChecked == true,
            tianyi = TianyiInterception.IsChecked == true,
            yidong = YidongInterception.IsChecked == true
        }
    };

    private void Section_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is RadioButton current && NavigateSection(current, e.Key)) e.Handled = true;
    }
    internal bool NavigateSection(RadioButton current, VirtualKey key)
    {
        if (key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return false;
        var anchors = SettingsNavigation.Children.OfType<RadioButton>()
            .Concat(BusinessNavigation.Children.OfType<RadioButton>())
            .Where(anchor => MeowFilm.Desktop.DesktopVisual.IsVisible(anchor)).ToArray();
        int index = Array.IndexOf(anchors, current);
        if (index < 0) return false;
        int next = key switch { VirtualKey.Home => 0, VirtualKey.End => anchors.Length - 1,
            VirtualKey.Left => (index + anchors.Length - 1) % anchors.Length, _ => (index + 1) % anchors.Length };
        anchors[next].IsChecked = true; anchors[next].Focus(FocusState.Programmatic); anchors[next].StartBringIntoView();
        return true;
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!initialized) return;
        ContentFrame.Margin = e.NewSize.Width < 700 || e.NewSize.Height < 450
            ? new(16, 0, 16, 8) : new(32, 0, 32, 12);
        if (compactSources != (e.NewSize.Width < 850)) UpdateSourceColumns(e.NewSize.Width < 850);
        if (ActionPopup.IsOpen) UpdatePopupSize();
    }

    private void OpenPopup(FrameworkElement anchor, string title, string message, string accept,
        Func<Task> execute, bool edit = false, FilmCatSource? cat = null, string? importType = null)
    {
        if (!Active) return;
        ClosePopup();
        long ticket = activation;
        popupAnchor = anchor as Control;
        popupAction = () => Active && ticket == activation ? execute() : Task.CompletedTask;
        PopupTitle.Text = title;
        PopupMessage.Text = message;
        PopupMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PopupFields.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        CatNameBox.Text = cat?.Name ?? "";
        CatUrlBox.Text = cat?.SourceUrl ?? "";
        catFilePath = null;
        originalCatType = cat?.SourceType;
        CatFileButton.Content = cat?.LocalPath is { Length: > 0 } path ? System.IO.Path.GetFileName(path) : "选择文件";
        CatTypeField.Visibility = edit && cat is not null ? Visibility.Visible : Visibility.Collapsed;
        CatTypeBox.SelectedValue = importType ?? cat?.SourceType ?? "network";
        UpdateCatInput();
        PopupCancel.Visibility = Visibility.Visible;
        PopupAccept.Content = accept;
        AutomationProperties.SetName(PopupAccept, title + "：" + accept);
        AutomationProperties.SetName(PopupCancel, title + "：取消");
        AutomationProperties.SetName(PopupSurface, title);
        UpdatePopupSize();
        ActionPopup.XamlRoot = XamlRoot;
        ActionPopup.IsOpen = true;
    }

    private void UpdatePopupSize()
    {
        PopupSurface.Width = Math.Min(400, Math.Max(240, ActualWidth - 32));
        PopupScroller.MaxHeight = Math.Max(100, ActualHeight - 64);
        if (XamlRoot is { } root)
        {
            ActionPopup.HorizontalOffset = Math.Max(0, (root.Size.Width - PopupSurface.Width) / 2);
            PopupSurface.Measure(new Size(PopupSurface.Width, PopupScroller.MaxHeight));
            ActionPopup.VerticalOffset = Math.Max(0, (root.Size.Height - PopupSurface.DesiredSize.Height) / 2);
        }
    }

    private string? catFilePath, originalCatType;
    private string CatInputType => CatTypeBox.SelectedValue as string ?? "network";
    private void CatType_Changed(object sender, SelectionChangedEventArgs e) => UpdateCatInput();
    private void UpdateCatInput()
    {
        if (CatUrlField is null || CatFileField is null) return;
        bool local = CatInputType == "local";
        CatUrlField.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        CatFileField.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CatFile_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || !ActionPopup.IsOpen) return;
        ActionPopup.IsLightDismissEnabled = false;
        try
        {
            string? path = (await PickLocalScriptsAsync("选择猫源文件")).FirstOrDefault();
            if (path is not null && Active && ActionPopup.IsOpen) SelectCatFile(path);
        }
        finally { ActionPopup.IsLightDismissEnabled = true; }
    }
    internal void SelectCatFile(string path)
    {
        catFilePath = path;
        CatFileButton.Content = System.IO.Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(CatNameBox.Text)) CatNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private void ClosePopup(bool restoreFocus = true)
    {
        if (!initialized) return;
        if (!restoreFocus) popupAnchor = null;
        ActionPopup.IsOpen = false;
        popupAction = null;
    }

    private void ActionPopup_Opened(object? sender, object e)
    {
        if (PopupFields.Visibility == Visibility.Visible) { CatNameBox.Focus(FocusState.Programmatic); CatNameBox.SelectAll(); }
        else if (PopupCancel.Visibility == Visibility.Visible) PopupCancel.Focus(FocusState.Programmatic);
        else PopupAccept.Focus(FocusState.Programmatic);
    }

    private void ActionPopup_Closed(object? sender, object e)
    {
        popupAction = null;
        var anchor = popupAnchor;
        popupAnchor = null;
        if (Active && anchor is { Visibility: Visibility.Visible, IsEnabled: true }) anchor.Focus(FocusState.Programmatic);
    }

    private void ActionPopup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { ClosePopup(); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter && PopupFields.Visibility == Visibility.Visible)
        {

            PopupAccept_Click(PopupAccept, new());
        }
    }

    private void PopupCancel_Click(object sender, RoutedEventArgs e) => ClosePopup();
    private async void PopupAccept_Click(object sender, RoutedEventArgs e)
    {
        var execute = popupAction;
        if (!ActionPopup.IsOpen || execute is null || !Active) return;
        if (PopupFields.Visibility == Visibility.Visible)
        {
            if (string.IsNullOrWhiteSpace(CatNameBox.Text))
            {
                Notify("请输入猫源名称"); CatNameBox.Focus(FocusState.Programmatic); return;
            }
            if (CatInputType == "local" && catFilePath is null && originalCatType != "local")
            {
                Notify("请选择猫源文件"); CatFileButton.Focus(FocusState.Programmatic); return;
            }
            if (CatUrlField.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(CatUrlBox.Text))
            {
                Notify("请输入网络 URL"); CatUrlBox.Focus(FocusState.Programmatic); return;
            }
        }
        ClosePopup();
        await execute();
    }
}
