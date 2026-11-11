using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;
using System.Windows.Automation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView : UserControl
{
    private readonly MusicCapability module;
    private string? scrubKey;
    private int qualityRequest;
    private PlayerSurfaceInput? surfaceInput;
    private string fmActionKey = "";
    private long fmPopupGeneration;
    private string playbackSessionActionKey = "";
    private long playbackSessionMenuGeneration;
    private bool returnPlaybackSessionFocus;
    private string modeActionKey = "";
    private long modeMenuGeneration;
    public MusicPlayerView(MusicCapability module, bool showArtwork = true)
    {
        InitializeComponent();
        if (showArtwork)
        {
            var record = new MusicRecordView { Width = 60, Height = 60 };
            record.SetBinding(MusicRecordView.AddressProperty, new Binding(nameof(MusicCapability.CurrentArtwork)));
            record.SetBinding(MusicRecordView.TrackIdentityProperty, new Binding(nameof(MusicCapability.CurrentTrackIdentity)));
            record.SetBinding(MusicRecordView.PlayingProperty, new Binding(nameof(MusicCapability.IsPlaying)));
            record.ArtworkLoadCompleted += (_, e) => module.ArtworkLoadCompleted(e.Address, e.Succeeded);
            ArtworkButton.Content = record;
        }
        else MetadataPanel.Children.Remove(ArtworkButton);
        this.module = module; DataContext = module;
        QualityMenu.DataContext = module;
        ModeMenu.DataContext = module;
        PlaybackSessionMenu.DataContext = module;
        Progress.ScrubStarted += (_, _) => BeginProgress();
        Progress.ScrubCancelled += (_, _) => RestoreProgress();
        Progress.ScrubCommitted += async (_, _) =>
        {
            var key = scrubKey; var position = Progress.Value;
            RestoreProgress();
            if (key is not null && key == module.PlaybackPresentationKey) await module.SeekAsync(position);
        };
        Loaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged; module.PropertyChanged += OnPlaybackChanged;
            surfaceInput?.Dispose(); surfaceInput = null;
            if (Window.GetWindow(this) is { } window) surfaceInput = new(window, this, LayersOpen);
        };
        foreach (var popup in new[] { SourcePopup, PlaylistPopup, VolumePopup, RatePopup, FmPopup })
        {
            popup.Opened += (_, _) => { if (popup.Child is Visual visual) surfaceInput?.Observe(visual); };
            popup.Closed += (_, _) => { surfaceInput?.LayerClosed(); if (popup.Child is Visual visual) surfaceInput?.Forget(visual); };
        }
        QualityMenu.Opened += (_, _) => surfaceInput?.Observe(QualityMenu);
        ModeMenu.Opened += (_, _) => surfaceInput?.Observe(ModeMenu);
        PlaybackSessionMenu.Opened += (_, _) => surfaceInput?.Observe(PlaybackSessionMenu);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Deactivate(); };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged;
            Deactivate();
            surfaceInput?.Dispose(); surfaceInput = null;
        };
        SizeChanged += (_, _) => MetadataText.MaxWidth = Math.Min(200, Math.Max(30, ActualWidth / 2 - 284));
    }
    internal void Deactivate()
    {
        returnPlaybackSessionFocus = false;
        surfaceInput?.Cancel();
        qualityRequest++;
        QualityMenu.IsOpen = false;
        ModeMenu.IsOpen = false;
        PlaybackSessionMenu.IsOpen = false;
        playbackSessionActionKey = ""; playbackSessionMenuGeneration++;
        modeActionKey = ""; modeMenuGeneration++;
        SourcePopup.IsOpen = false;
        PlaylistPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        RatePopup.IsOpen = false;
        FmPopup.IsOpen = false;
        fmActionKey = ""; fmPopupGeneration++;
        if (MoreButton.ContextMenu is { } menu) menu.IsOpen = false;
        Progress.CancelScrub(); RestoreProgress();
    }
    private bool LayersOpen() => QualityMenu.IsOpen || ModeMenu.IsOpen || PlaybackSessionMenu.IsOpen || SourcePopup.IsOpen || PlaylistPopup.IsOpen ||
        VolumePopup.IsOpen || RatePopup.IsOpen || FmPopup.IsOpen || MoreButton.ContextMenu?.IsOpen == true;
    private static bool IsBlank(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && !ReferenceEquals(node, sender))
        {
            if (node is ButtonBase or Slider or Thumb or TextBoxBase or ScrollBar) return false;
            node = node is FrameworkContentElement text ? text.Parent : VisualTreeHelper.GetParent(node);
        }
        return node is not null;
    }
    private void Surface_MouseDown(object sender, MouseButtonEventArgs e) => surfaceInput?.Press(IsBlank(sender, e));
    private async void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (surfaceInput?.Release(!e.Handled && IsBlank(sender, e)) != true) return;
        e.Handled = true;
        await module.OpenPlayerDetailAsync();
    }
    private async void Lyrics_Click(object sender, RoutedEventArgs e) => await module.OpenPlayerDetailAsync();
    private async void Favorite_Click(object sender, RoutedEventArgs e) => await module.ToggleCurrentFavoriteAsync();
    private async void DesktopLyrics_Click(object sender, RoutedEventArgs e) =>
        await module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
    private void PlaylistMenu_Click(object sender, RoutedEventArgs e) => PlaylistPopup.IsOpen = !PlaylistPopup.IsOpen;
    private async void AddCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) { PlaylistPopup.IsOpen = false; await module.AddCurrentToPlaylistAsync(id); }
    }
    private void Volume_Click(object sender, RoutedEventArgs e) => VolumePopup.IsOpen = !VolumePopup.IsOpen;
    private async void VolumeCommit_MouseUp(object sender, MouseButtonEventArgs e) => await module.SaveVolumeAsync();
    private async void VolumeCommit_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await module.SaveVolumeAsync();
    }
    private async void Quality_Click(object sender, RoutedEventArgs e)
    {
        if (QualityMenu.IsOpen) { QualityMenu.IsOpen = false; return; }
        await OpenQualityMenuAsync();
    }
    private async void Quality_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (!QualityMenu.IsOpen) await OpenQualityMenuAsync();
    }
    private async Task OpenQualityMenuAsync()
    {
        int request = ++qualityRequest;
        await module.ActivateQualitySettingsAsync();
        if (request != qualityRequest || !IsLoaded || !IsVisible || !module.HasQualityButton) return;
        ShowQualityMenu();
        QualityMenu.PlacementTarget = QualityButton;
        QualityMenu.IsOpen = true;
    }
    private void ShowQualityMenu()
    {
        while (QualityMenu.Items.Count > 2) QualityMenu.Items.RemoveAt(QualityMenu.Items.Count - 1);
        foreach (var choice in module.QualityChoices()) QualityMenu.Items.Add(QualityItem(choice));
        var spatial = module.SpatialChoices();
        if (spatial.Count == 0) return;
        var submenu = new MenuItem
        {
            Header = MusicQualityLabels.Name("spatial"), IsChecked = spatial.Any(choice => choice.Current),
            Style = (Style)FindResource("Player.CurrentQualityItem")
        };
        AutomationProperties.SetName(submenu, MusicQualityLabels.Name("spatial"));
        foreach (var choice in spatial) submenu.Items.Add(QualityItem(choice));
        Popup? observedPopup = null;
        submenu.Loaded += (_, _) =>
        {
            if (submenu.Template.FindName("PART_Popup", submenu) is not Popup popup ||
                ReferenceEquals(popup, observedPopup)) return;
            observedPopup = popup;
            popup.Opened += (_, _) => { if (popup.Child is Visual visual) surfaceInput?.Observe(visual); };
            popup.Closed += (_, _) =>
            {
                surfaceInput?.LayerClosed();
                if (popup.Child is Visual visual) surfaceInput?.Forget(visual);
            };
        };
        QualityMenu.Items.Add(submenu);
    }
    private MenuItem QualityItem(MusicQualityChoice choice)
    {
        var item = new MenuItem { Header = choice.Label, Tag = choice.Key, IsChecked = choice.Current,
            Style = (Style)FindResource("Player.CurrentQualityItem") };
        item.SetBinding(IsEnabledProperty, new Binding(nameof(MusicCapability.CanSwitchSource)) { Source = module });
        AutomationProperties.SetName(item, choice.Label);
        item.Click += QualityChoice_Click;
        return item;
    }
    private async void QualityChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        QualityMenu.IsOpen = false;
        await module.SwitchQualityAsync(key);
    }
    private void SourceList_Click(object sender, RoutedEventArgs e)
    {
        if (!module.CanSwitchSource) return;
        QualityMenu.IsOpen = false;
        module.OpenSourceChoices();
        SourcePopup.IsOpen = true;
        CloseSourcesButton.Focus();
    }
    private async void LoadMoreSources_Click(object sender, RoutedEventArgs e) => await module.LoadMoreSourcesAsync();
    private async void SourceChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicSourceChoice choice }) return;
        SourcePopup.IsOpen = false;
        await module.SelectSourceChoiceAsync(choice);
    }
    private void CloseSources_Click(object sender, RoutedEventArgs e) => SourcePopup.IsOpen = false;
    private void SourcePopup_Closed(object? sender, EventArgs e)
    {
        module.CloseSourceChoices();
        if (IsLoaded && IsVisible) QualityButton.Focus();
    }
    private async void Quality_Closed(object? sender, RoutedEventArgs e)
    {
        surfaceInput?.LayerClosed(); surfaceInput?.Forget(QualityMenu);
        qualityRequest++;
        await module.DeactivateQualityAsync();
    }
    /// <summary>The "more" menu keeps playback tools separate from source actions.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Top,
            Style = (Style)FindResource("MeowFilm.ContextMenu") };
        menu.Opened += (_, _) => surfaceInput?.Observe(menu);
        menu.Closed += (_, _) => { surfaceInput?.LayerClosed(); surfaceInput?.Forget(menu); };
        var rate = new MenuItem { Header = "播放倍速 · " + module.PlaybackRateText };
        rate.Click += (_, _) => { menu.IsOpen = false; RatePopup.IsOpen = true; };
        var wrong = new MenuItem { Header = "音源不对", IsEnabled = module.CanSwitchSource };
        wrong.Click += async (_, _) => { menu.IsOpen = false; await module.ReportWrongSourceAsync(); };
        menu.Items.Add(rate); menu.Items.Add(wrong);
        MoreButton.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await module.TogglePlaybackAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await module.StepAsync(false);
    private async void Next_Click(object sender, RoutedEventArgs e) => await module.StepAsync(true);
    private void Mode_Click(object sender, RoutedEventArgs e) => OpenModeChoices();
    private void Mode_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (!ModeMenu.IsOpen && !FmPopup.IsOpen) OpenModeChoices();
    }
    private void OpenModeChoices()
    {
        if (!module.CanChangePlaybackMode) return;
        if (ModeMenu.IsOpen) { ModeMenu.IsOpen = false; return; }
        modeActionKey = module.PlaybackModeActionKey;
        modeMenuGeneration++;
        ModeMenu.Items.Clear();
        ModeMenu.Items.Add(ModeChoice("列表循环", "repeat", 1, module.Mode == 1));
        ModeMenu.Items.Add(ModeChoice("单曲循环", "single", 3, module.Mode == 3));
        ModeMenu.Items.Add(ModeChoice("随机播放", "shuffle", 2, module.Mode == 2));
        ModeMenu.PlacementTarget = ModeButton;
        ModeMenu.IsOpen = true;
    }
    private void PlaybackSession_Click(object sender, RoutedEventArgs e) => OpenPlaybackSessionMenu();
    private void PlaybackSession_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (!PlaybackSessionMenu.IsOpen && !FmPopup.IsOpen) OpenPlaybackSessionMenu();
    }
    private void OpenPlaybackSessionMenu()
    {
        if (!IsLoaded || !IsVisible) return;
        if (PlaybackSessionMenu.IsOpen) { PlaybackSessionMenu.IsOpen = false; return; }
        FmPopup.IsOpen = false;
        returnPlaybackSessionFocus = true;
        PlaybackSessionMenu.Items.Clear();
        string actionKey = playbackSessionActionKey = module.PlaybackSourceActionKey;
        long generation = ++playbackSessionMenuGeneration;
        string presentationKey = module.PlaybackPresentationKey;
        void Add(string label, bool enabled, Func<Task> action)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled, StaysOpenOnClick = true };
            AutomationProperties.SetName(item, label);
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                if (!PlaybackSessionMenu.IsOpen || !item.IsEnabled || !IsLoaded || !IsVisible ||
                    generation != playbackSessionMenuGeneration || actionKey != module.PlaybackSourceActionKey) return;
                PlaybackSessionMenu.IsOpen = false;
                await action();
            };
            PlaybackSessionMenu.Items.Add(item);
        }
        PlaybackSessionMenu.Items.Add(new MenuItem { Header = module.PlaybackQueueSourceText, IsEnabled = false });
        if (module.RecommendationLoading)
            PlaybackSessionMenu.Items.Add(new MenuItem { Header = "正在加载", IsEnabled = false });
        if (module.HasCurrentRecommendationIdentity && !module.HasActiveRecommendation)
            Add("推荐模式", module.CanRecommendCurrent, () => module.RecommendCurrentAsync(presentationKey));
        if (module.CanReturnRecommendation)
            Add(module.HasPlaybackHeartbeat ? "退出心动" : "退出推荐", true, module.ReturnRecommendationAsync);
        if (module.CanChangeRecommendationContinue)
        {
            bool enabled = !module.RecommendationContinue;
            Add(enabled ? "开启续播" : "关闭续播", true, () => module.ChangeRecommendationContinueAsync(enabled));
        }
        if (module.HasPlaybackFm)
            Add("频道切换", CanOpenFmChoices, () => { OpenFmChoices(); return Task.CompletedTask; });
        if (module.CanHeartbeatCurrent && !module.HasPlaybackHeartbeat)
            Add("从当前歌曲进入心动", true, () => module.HeartbeatCurrentAsync(presentationKey));
        if (module.CanRetryRecommendation)
            Add("重试取歌", true, module.RetryRecommendationAsync);
        if (module.CanReconcileRecommendation)
            Add("核对保存结果", true, module.ReconcileRecommendationAsync);
        MusicCapability.ApplyContentMenuStyles(this, PlaybackSessionMenu);
        PlaybackSessionMenu.PlacementTarget = PlaybackSessionButton;
        PlaybackSessionMenu.IsOpen = true;
    }
    private void PlaybackSessionMenu_Closed(object? sender, RoutedEventArgs e)
    {
        surfaceInput?.LayerClosed(); surfaceInput?.Forget(PlaybackSessionMenu);
        playbackSessionActionKey = ""; playbackSessionMenuGeneration++;
        if (!FmPopup.IsOpen) RestorePlaybackSessionFocus();
    }
    private bool CanOpenFmChoices => module.HasPlaybackFm && module.CanChangePlaybackMode && !module.CanReconcileRecommendation;
    private void OpenFmChoices()
    {
        if (!IsLoaded || !IsVisible || !CanOpenFmChoices) return;
        returnPlaybackSessionFocus = true;
        fmActionKey = module.PlaybackFmActionKey;
        fmPopupGeneration++;
        FmModeChoices.Children.Clear(); FmSceneChoices.Children.Clear();
        foreach (var choice in module.PlaybackFmModes) FmModeChoices.Children.Add(FmChoice(choice, scene: false));
        foreach (var choice in module.CatalogFmScenes) FmSceneChoices.Children.Add(FmChoice(choice, scene: true));
        FmPopup.IsOpen = true;
    }
    private void FmPopup_Opened(object? sender, EventArgs e)
    {
        if (!IsLoaded || !IsVisible) return;
        var choices = FmModeChoices.Children.OfType<Button>().Concat(FmSceneChoices.Children.OfType<Button>());
        var selected = choices.FirstOrDefault(button => AutomationProperties.GetItemStatus(button) == "已选中");
        if ((selected ?? choices.FirstOrDefault()) is not { } initial) return;
        FocusManager.SetFocusedElement(FmPanel, initial);
        if (Window.GetWindow(this) is null or { IsActive: true }) initial.Focus();
        initial.BringIntoView();
    }
    private MenuItem ModeChoice(string name, string iconKey, int mode, bool selected)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new MusicPlaybackIcon { Key = iconKey, Width = 18, Height = 18, Margin = new(0, 0, 10, 0) };
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(label);
        // Native MenuItem closes on PreviewClick, before Click; keep it open until the guarded handler closes it.
        var item = new MenuItem { Header = content, StaysOpenOnClick = true, Tag = mode,
            IsChecked = selected, Style = (Style)FindResource("Player.CurrentQualityItem") };
        icon.SetBinding(Control.ForegroundProperty, new Binding(nameof(Foreground)) { Source = item });
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = item });
        item.SetBinding(IsEnabledProperty, new Binding(nameof(MusicCapability.CanChangePlaybackMode)) { Source = module });
        AutomationProperties.SetName(item, name);
        string actionKey = modeActionKey;
        long generation = modeMenuGeneration;
        item.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!ModeMenu.IsOpen || !item.IsEnabled || !IsLoaded || !IsVisible ||
                generation != modeMenuGeneration || actionKey != module.PlaybackModeActionKey) return;
            ModeMenu.IsOpen = false;
            await module.ChangeModeAsync(mode);
        };
        return item;
    }
    private void ModeMenu_Closed(object? sender, RoutedEventArgs e)
    {
        surfaceInput?.LayerClosed(); surfaceInput?.Forget(ModeMenu);
        modeActionKey = ""; modeMenuGeneration++;
        if (IsLoaded && IsVisible) ModeButton.Focus();
    }
    private Button FmChoice(MusicCatalogChoice choice, bool scene)
    {
        bool selected = module.PlaybackFmRuleId.Length == 0 && module.PlaybackFmTabTagId.Length == 0 &&
            (scene ? module.PlaybackFmMode == "scene" && module.PlaybackFmScene == choice.Key :
                module.PlaybackFmMode == choice.Key);
        string actionKey = fmActionKey;
        long generation = fmPopupGeneration;
        var button = new Button
        {
            Tag = choice,
            Style = (Style)FindResource(scene ? "Player.FmScene" : "Player.FmChoice"),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var content = new StackPanel();
        if (scene)
        {
            var icon = new MusicPlaybackIcon { Key = choice.Key, Width = 25, Height = 25,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 10) };
            icon.SetBinding(Control.ForegroundProperty, new Binding(nameof(Foreground)) { Source = button });
            content.Children.Add(icon);
        }
        var label = new TextBlock { Text = choice.Name, TextAlignment = TextAlignment.Center,
            FontWeight = scene ? FontWeights.Normal : FontWeights.Medium };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = button });
        content.Children.Add(label);
        button.Content = content;
        AutomationProperties.SetName(button, choice.Name);
        AutomationProperties.SetItemStatus(button, selected ? "已选中" : "");
        button.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!FmPopup.IsOpen || !button.IsEnabled || !IsLoaded || !IsVisible || !CanOpenFmChoices ||
                generation != fmPopupGeneration || actionKey != module.PlaybackFmActionKey) return;
            FmPopup.IsOpen = false;
            await module.ChangePlaybackFmAsync(scene ? "scene" : choice.Key, scene ? choice.Key : "", actionKey);
        };
        return button;
    }
    private void FmPopup_Closed(object? sender, EventArgs e)
    {
        fmActionKey = ""; fmPopupGeneration++;
        FocusManager.SetFocusedElement(FmPanel, null);
        FmModeChoices.Children.Clear(); FmSceneChoices.Children.Clear();
        RestorePlaybackSessionFocus();
    }
    private void RestorePlaybackSessionFocus()
    {
        if (!returnPlaybackSessionFocus || !IsLoaded || !IsVisible || Window.GetWindow(this) is { IsVisible: false }) return;
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(PlaybackSessionButton), PlaybackSessionButton);
        if (Window.GetWindow(this) is null or { IsActive: true }) PlaybackSessionButton.Focus();
    }
    private void CloseFm_Click(object sender, RoutedEventArgs e) => FmPopup.IsOpen = false;
    private void FmPopup_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { FmPopup.IsOpen = false; e.Handled = true; } }
    private async void Queue_Click(object sender, RoutedEventArgs e) => await module.OpenPlayQueueAsync();
    private async void Rate_MouseUp(object sender, MouseButtonEventArgs e) => await module.ChangePlaybackRateAsync(Math.Round(RateSlider.Value, 1));
    private async void Rate_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await module.ChangePlaybackRateAsync(Math.Round(RateSlider.Value, 1));
    }
    private async void RateDown_Click(object sender, RoutedEventArgs e) => await module.ChangePlaybackRateAsync(Math.Max(0.5, Math.Round(module.PlaybackRate - 0.1, 1)));
    private async void RateUp_Click(object sender, RoutedEventArgs e) => await module.ChangePlaybackRateAsync(Math.Min(3, Math.Round(module.PlaybackRate + 0.1, 1)));
    private async void RateReset_Click(object sender, RoutedEventArgs e) => await module.ChangePlaybackRateAsync(1);
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (PlaybackSessionMenu.IsOpen && (playbackSessionActionKey != module.PlaybackSourceActionKey ||
            !module.CanPlay && !module.HasActiveRecommendation && !module.CanReconcileRecommendation))
            PlaybackSessionMenu.IsOpen = false;
        if (SourcePopup.IsOpen && !module.HasSourceChoices) SourcePopup.IsOpen = false;
        if (!module.HasQualityButton) QualityMenu.IsOpen = false;
        if (ModeMenu.IsOpen && (modeActionKey != module.PlaybackModeActionKey || !module.CanChangePlaybackMode))
            ModeMenu.IsOpen = false;
        if (FmPopup.IsOpen && (fmActionKey != module.PlaybackFmActionKey || !CanOpenFmChoices)) FmPopup.IsOpen = false;
        if (scrubKey is not null && (scrubKey != module.PlaybackPresentationKey || !module.CanSeek))
        { Progress.CancelScrub(); RestoreProgress(); }
    }
    private void BeginProgress()
    {
        if (!module.CanSeek) return;
        scrubKey = module.PlaybackPresentationKey;
        var position = Progress.Value;
        BindingOperations.ClearBinding(Progress, Slider.ValueProperty);
        Progress.Value = position;
    }
    private void RestoreProgress()
    {
        if (scrubKey is null) return;
        scrubKey = null;
        Progress.SetBinding(Slider.ValueProperty, new Binding(nameof(MusicCapability.PositionSeconds)) { Mode = BindingMode.OneWay });
    }
}
