using Windows.System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
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
    private readonly FilmPlaybackSettingsView? siteCleanSettings;
    private readonly FilmPlaybackSettingsView? playbackSettings;
    private string section = "rules";
    internal string CurrentDebugSection => section;
    private bool initialized, observingSources;
    private IDisposable? visibility;
    private long activation, popupGeneration;
    private Func<Task>? popupAction;
    private Control? popupAnchor;
    private bool popupOpen;

    internal FilmSettingsView(FilmRuleService rules, FilmSourceService sources, IStorageDatabase store,
        FilmPlaybackSettings? business, Func<string?> targetId,
        FilmMetadataService? metadata = null)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.metadata = metadata;
        panAccounts = new(sources.Runtime, store);
        InitializeComponent();
        ModalStash.Children.Clear();
        PanPage.DataContext = panAccounts;
        if (business is not null)
        {
            siteCleanSettings = new(business, targetId, "clean");
            SiteCleanHost.Content = siteCleanSettings;
            playbackSettings = new(business, targetId, "preferences");
            BusinessPage.Children.Add(playbackSettings);
            BusinessNavigation.Visibility = Visibility.Visible;
        }
        foreach (var editor in RuleEditors)
        {
            editor.ActionRequested += Rule_ActionRequested;
            editor.DraftChanged += (_, _) => InvalidateRuleResults();
        }
        ConfigureSourceLists();
        initialized = true;
    }

    public DesktopHeaderContribution Header => new("设置", SearchVisible: false, SearchEnabled: false);
    public event EventHandler? HeaderChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Active => initialized && IsLoaded && DesktopVisual.IsVisible(this);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Notify(string message) => NoticeOutlet.Post(this, message);

    private async Task ActivateCurrentGuardedAsync()
    {
        try { await ActivateCurrentAsync(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    internal async Task RefreshAfterExternalImportAsync()
    {
        rulesLoaded = false;
        metadataLoaded = false;
        sourcesLoaded = false;
        panInterception = null;
        if (Active)
        {
            if (section == "pan") await panAccounts.ReloadAsync();
            else await ActivateCurrentGuardedAsync();
        }
    }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        visibility?.Dispose();
        visibility = DesktopVisual.ObserveVisibility(this, _ => View_VisibilityChanged());
        if (!observingSources) { sources.Changed += Sources_Changed; observingSources = true; }
        await ActivateCurrentGuardedAsync();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        visibility?.Dispose();
        visibility = null;
        Deactivate();
        if (observingSources) { sources.Changed -= Sources_Changed; observingSources = false; }
    }

    private async void View_VisibilityChanged()
    {
        if (!initialized) return;
        if (!DesktopVisual.IsVisible(this)) Deactivate();
        else if (IsLoaded) await ActivateCurrentGuardedAsync();
    }

    private void Deactivate()
    {
        activation++;
        ruleOperation?.Cancel();
        sourceOperation?.Cancel();
        metadataOperation?.Cancel();
        siteCleanSettings?.Deactivate();
        playbackSettings?.Deactivate();
        ClosePopup(restoreFocus: false);
    }
    internal Task PreparePlaybackSettingsShutdownAsync() => Task.WhenAll(
        siteCleanSettings?.PrepareShutdownAsync() ?? Task.CompletedTask,
        playbackSettings?.PrepareShutdownAsync() ?? Task.CompletedTask,
        panAccounts.PrepareShutdownAsync());

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
        var view = area == "clean" ? siteCleanSettings : playbackSettings;
        if (view is null) return;
        await view.ShowAsync(area);
    }

    private void ApplySectionVisibility(string key)
    {
        RulesPage.Visibility = key == "rules" ? Visibility.Visible : Visibility.Collapsed;
        CatsPage.Visibility = key == "cats" ? Visibility.Visible : Visibility.Collapsed;
        VideosPage.Visibility = key == "videos" ? Visibility.Visible : Visibility.Collapsed;
        MetadataPage.Visibility = key == "metadata" ? Visibility.Visible : Visibility.Collapsed;
        PanPage.Visibility = key == "pan" ? Visibility.Visible : Visibility.Collapsed;
        PanInterceptionPage.Visibility = key == "pan-routing" ? Visibility.Visible : Visibility.Collapsed;
        BusinessPage.Visibility = key is "rules" or "cats" or "videos" or "metadata" or "pan" or "pan-routing"
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || sender is not RadioButton { Tag: string key } anchor || key == section) return;
        Deactivate();
        section = key;
        ApplySectionVisibility(key);
        anchor.Focus(FocusState.Programmatic); anchor.StartBringIntoView();
        await ActivateCurrentGuardedAsync();
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
        ApplySectionVisibility(normalized);
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

    internal PanAccounts DebugPanAccounts => panAccounts;

    internal void SyncDebugSources()
    {
        sourcesLoaded = true;
        if (Active) SynchronizeSources();
    }

    internal void SyncDebugMetadata(FilmMetadataSettings value)
    {
        if (Active && metadataLoaded) ReplaceMetadata(value);
    }

    internal void SyncDebugDouban(FilmDoubanSettings value)
    {
        if (Active && metadataLoaded) ReplaceDouban(value);
    }

    internal async Task<object> DebugPanAccountLoginAsync(string? platform, bool close, string? mode, CancellationToken cancellationToken)
    {
        if (section != "pan") await SelectDebugSectionAsync("pan");
        await panAccounts.ActivateAsync();
        return await PanPage.DebugAccountLoginAsync(platform, close, mode, cancellationToken);
    }

    internal async Task<object> DebugPanAccountLogoutAsync(string? platform, bool close, bool confirm, CancellationToken cancellationToken)
    {
        if (section != "pan") await SelectDebugSectionAsync("pan");
        await panAccounts.ActivateAsync();
        return await PanPage.DebugAccountLogoutAsync(platform, close, confirm, cancellationToken);
    }

    private RadioButton[] VisibleSectionAnchors() => SettingsNavigation.Children.OfType<RadioButton>()
        .Concat(BusinessNavigation.Visibility == Visibility.Visible
            ? BusinessNavigation.Children.OfType<RadioButton>()
            : Array.Empty<RadioButton>())
        .Where(r => r.Visibility == Visibility.Visible)
        .ToArray();

    internal IReadOnlyList<string> GetDebugTabIds() => VisibleSectionAnchors()
        .Select(r => r.Tag as string ?? "")
        .ToArray();

    internal object[] DescribeDebugTabs() => VisibleSectionAnchors()
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
        catsCount = sources.Cats.Count,
        videosCount = sources.Videos.Count,
        cats = sources.Cats.Select(c => new
        {
            installationId = c.InstallationId,
            name = c.Name,
            sourceType = c.SourceType,
            enabled = c.Enabled,
            state = c.State,
            generation = c.Generation,
            error = c.Error,
            sitesCount = c.Sites.Count
        }).ToArray(),
        hasMetadataSettings = HasMetadataSettings,
        metadataLoaded = MetadataLoaded,
        panLoginOpen = panAccounts.LoginOpen,
        panAccounts = panAccounts.Rows.Select(r => new
        {
            provider = r.Provider,
            name = r.Name,
            status = r.Status,
            statusText = r.StatusText,
            canLogin = r.CanLogin,
            canLogout = r.CanLogout
        }).ToArray(),
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
            .Where(anchor => DesktopVisual.IsVisible(anchor)).ToArray();
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
            ? new(16, 0, 0, 0) : new(32, 0, 0, 0);
        if (popupOpen) UpdatePopupSize();
    }

    private async void OpenPopup(FrameworkElement anchor, string title, string message, string accept,
        Func<Task> execute, bool edit = false, FilmCatSource? cat = null, string? importType = null)
    {
        if (!Active) return;
        ClosePopup();
        long token = ++popupGeneration;
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
        popupOpen = true;
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(this, PopupSurface, onOpened: () =>
            {
                if (popupGeneration != token) return;
                if (PopupFields.Visibility == Visibility.Visible) { CatNameBox.Focus(FocusState.Programmatic); CatNameBox.SelectAll(); }
                else if (PopupCancel.Visibility == Visibility.Visible) PopupCancel.Focus(FocusState.Programmatic);
                else PopupAccept.Focus(FocusState.Programmatic);
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (popupGeneration == token)
            {
                popupOpen = false;
                popupAction = null;
                var restore = popupAnchor;
                popupAnchor = null;
                if (Active && restore is { Visibility: Visibility.Visible, IsEnabled: true }) restore.Focus(FocusState.Programmatic);
            }
        }
    }

    private void UpdatePopupSize()
    {
        PopupSurface.Width = Math.Min(400, Math.Max(240, ActualWidth - 32));
        PopupScroller.MaxHeight = Math.Max(100, ActualHeight - 64);
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
        if (!Active || !popupOpen) return;
        long token = popupGeneration;
        try
        {
            string? path = await CapabilityFilePicker.OpenAsync(this, "选择猫源文件", ".js");
            if (path is not null && Active && popupOpen && token == popupGeneration) SelectCatFile(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Active) Notify("选择文件失败"); }
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
        popupGeneration++;
        if (!restoreFocus) popupAnchor = null;
        popupOpen = false;
        popupAction = null;
        DesktopModalHost.CloseFor(this, PopupSurface);
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
        if (!popupOpen || execute is null || !Active) return;
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
                Notify("请输入网络地址"); CatUrlBox.Focus(FocusState.Programmatic); return;
            }
        }
        ClosePopup();
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { if (Active) Notify("操作失败：" + error.Message); }
    }
}
