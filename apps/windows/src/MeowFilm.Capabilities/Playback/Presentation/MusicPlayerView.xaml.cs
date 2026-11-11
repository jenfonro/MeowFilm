using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView : UserControl
{
    private static readonly DependencyPropertyDescriptor SourceOpenProperty =
        DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup));
    private readonly NativeMenuLifetime modeLifetime, qualityLifetime;
    private readonly MusicCapability module;
    private string? scrubKey;
    private int qualityRequest;
    private PlayerSurfaceInput? surfaceInput;
    private string fmActionKey = "";
    private long fmGeneration;
    private PlayerDialogHost? fmHost;
    private PlayerMenuInput? menuInput;
    private readonly List<IDisposable> menuEntries = [];
    private bool fmOpen;
    private bool returnModeFocus;
    private bool openingFm;
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
        FmPanel.DataContext = module;
        modeLifetime = new(ModeMenu, () =>
        {
            modeActionKey = ""; modeMenuGeneration++;
            menuInput?.Closed(ModeButton);
            return Task.CompletedTask;
        });
        qualityLifetime = new(QualityMenu, () =>
        {
            qualityRequest++;
            if (!SourcePopup.IsOpen) menuInput?.Closed(QualityButton);
            return module.DeactivateQualityAsync();
        });
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
            modeLifetime.Attach(); qualityLifetime.Attach();
            SourceOpenProperty.RemoveValueChanged(SourcePopup, SourceOpenChanged);
            SourceOpenProperty.AddValueChanged(SourcePopup, SourceOpenChanged);
            surfaceInput?.Dispose(); surfaceInput = null;
            foreach (var entry in menuEntries) entry.Dispose(); menuEntries.Clear();
            if (Window.GetWindow(this) is { } window)
            {
                surfaceInput = new(window, this, LayersOpen);
            }
            menuInput = PlayerMenuInput.For(Window.GetWindow(this) ?? (FrameworkElement)this);
            RegisterMenus();
        };
        VolumePopup.Closed += VolumeClosed;
        PlaylistPopup.Closed += PlaylistClosed;
        RatePopup.Closed += (_, _) => menuInput?.Closed(MoreButton);
        foreach (var popup in new[] { SourcePopup, PlaylistPopup, VolumePopup, RatePopup })
        {
            popup.Opened += (_, _) => { if (popup.Child is Visual visual) surfaceInput?.Observe(visual); };
            popup.Closed += (_, _) => { surfaceInput?.LayerClosed(); if (popup.Child is Visual visual) surfaceInput?.Forget(visual); };
        }
        QualityMenu.Opened += (_, _) => surfaceInput?.Observe(QualityMenu);
        ModeMenu.Opened += (_, _) => surfaceInput?.Observe(ModeMenu);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Deactivate(); };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged;
            Deactivate();
            modeLifetime.Detach(); qualityLifetime.Detach();
            SourceOpenProperty.RemoveValueChanged(SourcePopup, SourceOpenChanged);
            surfaceInput?.Dispose(); surfaceInput = null;
            foreach (var entry in menuEntries) entry.Dispose(); menuEntries.Clear(); menuInput = null;
        };
        SizeChanged += (_, _) => MetadataText.MaxWidth = Math.Min(200, Math.Max(30, ActualWidth / 2 - 262));
    }
    internal void Deactivate()
    {
        returnModeFocus = false;
        menuInput?.CancelPress();
        surfaceInput?.Cancel();
        // The other footer shares this module: hiding an inactive view must not cancel its work.
        if (QualityMenu.IsOpen || menuInput?.IsCurrent(QualityButton) == true) qualityLifetime.Close();
        else qualityRequest++;
        modeLifetime.Close();
        SourcePopup.IsOpen = false;
        PlaylistPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        RatePopup.IsOpen = false;
        CloseFm();
        fmActionKey = ""; fmGeneration++;
        if (MoreButton.ContextMenu is { } menu) menu.IsOpen = false;
        foreach (var button in new[] { QualityButton, ModeButton, VolumeButton, PlaylistButton, MoreButton }) menuInput?.Closed(button);
        Progress.CancelScrub(); RestoreProgress();
    }
    private bool LayersOpen() => QualityMenu.IsOpen || ModeMenu.IsOpen || SourcePopup.IsOpen || PlaylistPopup.IsOpen ||
        VolumePopup.IsOpen || RatePopup.IsOpen || fmOpen || MoreButton.ContextMenu?.IsOpen == true;
    private void RegisterMenus()
    {
        void Add(Button button, Func<bool> isOpen, Action open, Action close, bool queue = false) =>
            menuEntries.Add(menuInput!.Register(button, isOpen, open, close, queue));
        Add(ModeButton, () => ModeMenu.IsOpen, async () => await OpenModeChoicesAsync(), modeLifetime.Close);
        Add(QualityButton, () => QualityMenu.IsOpen, async () => await OpenQualityMenuAsync(), () =>
        { qualityLifetime.Close(); SourcePopup.IsOpen = false; });
        Add(VolumeButton, () => VolumePopup.IsOpen, () => VolumePopup.IsOpen = true, () => VolumePopup.IsOpen = false);
        Add(PlaylistButton, () => PlaylistPopup.IsOpen, () => PlaylistPopup.IsOpen = true, () => PlaylistPopup.IsOpen = false);
        Add(MoreButton, () => MoreButton.ContextMenu?.IsOpen == true || RatePopup.IsOpen, OpenMoreMenu, () =>
        { if (MoreButton.ContextMenu is { } more) more.IsOpen = false; RatePopup.IsOpen = false; });
        Add(QueueButton, () => PlayerSurfaceInput.GetQueueOpen(this), async () => await module.OpenPlayQueueAsync(), () => { }, queue: true);
    }
    private void VolumeClosed(object? sender, EventArgs e) => menuInput?.Closed(VolumeButton);
    private void PlaylistClosed(object? sender, EventArgs e) => menuInput?.Closed(PlaylistButton);
    private void ToggleMenu(Button button)
    {
        menuInput?.Toggle(button);
    }
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
    private void PlaylistMenu_Click(object sender, RoutedEventArgs e) => ToggleMenu(PlaylistButton);
    private async void AddCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) { PlaylistPopup.IsOpen = false; await module.AddCurrentToPlaylistAsync(id); }
    }
    private void Volume_Click(object sender, RoutedEventArgs e) => ToggleMenu(VolumeButton);
    private async void VolumeCommit_MouseUp(object sender, MouseButtonEventArgs e) => await module.SaveVolumeAsync();
    private async void VolumeCommit_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await module.SaveVolumeAsync();
    }
    private void Quality_Click(object sender, RoutedEventArgs e) => ToggleMenu(QualityButton);
    private void Quality_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    { e.Handled = true; menuInput?.Show(QualityButton); }
    private async Task OpenQualityMenuAsync()
    {
        int request = ++qualityRequest;
        await qualityLifetime.Ready;
        if (!CanOpenQuality(request)) return;
        await module.ActivateQualitySettingsAsync();
        if (!CanOpenQuality(request)) return;
        ShowQualityMenu();
        QualityMenu.PlacementTarget = QualityButton;
        QualityMenu.IsOpen = true;
    }
    private bool CanOpenQuality(int request) => request == qualityRequest && IsLoaded && IsVisible &&
        module.HasQualityButton && menuInput?.IsCurrent(QualityButton) == true;
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
        module.OpenSourceChoices();
        SourcePopup.IsOpen = true;
        QualityMenu.IsOpen = false;
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
    private void SourceOpenChanged(object? sender, EventArgs e)
    {
        if (SourcePopup.IsOpen) return;
        menuInput?.Closed(QualityButton);
        module.CloseSourceChoices();
    }
    private void SourcePopup_Closed(object? sender, EventArgs e)
    {
        if (menuInput?.HasCurrent != true && IsLoaded && IsVisible &&
            Window.GetWindow(this) is not { IsActive: false }) QualityButton.Focus();
    }
    private void Quality_Closed(object? sender, RoutedEventArgs e)
    {
        surfaceInput?.LayerClosed(); surfaceInput?.Forget(QualityMenu);
    }
    /// <summary>The "more" menu keeps playback tools separate from source actions.</summary>
    private void More_Click(object sender, RoutedEventArgs e) => ToggleMenu(MoreButton);
    private void OpenMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Top,
            Style = (Style)FindResource("MeowFilm.ContextMenu") };
        menu.Opened += (_, _) => surfaceInput?.Observe(menu);
        menu.Closed += (_, _) => { surfaceInput?.LayerClosed(); surfaceInput?.Forget(menu); if (!RatePopup.IsOpen) menuInput?.Closed(MoreButton); };
        var rate = new MenuItem { Header = "播放倍速 · " + module.PlaybackRateText };
        rate.Click += (_, _) => { RatePopup.IsOpen = true; menu.IsOpen = false; };
        var wrong = new MenuItem { Header = "音源不对", IsEnabled = module.CanSwitchSource };
        wrong.Click += async (_, _) => { menu.IsOpen = false; await module.ReportWrongSourceAsync(); };
        menu.Items.Add(rate); menu.Items.Add(wrong);
        MoreButton.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await module.TogglePlaybackAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await module.StepAsync(false);
    private async void Next_Click(object sender, RoutedEventArgs e) => await module.StepAsync(true);
    private void Mode_Click(object sender, RoutedEventArgs e) => ToggleMenu(ModeButton);
    private void Mode_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    { e.Handled = true; menuInput?.Show(ModeButton); }
    private async Task OpenModeChoicesAsync()
    {
        long generation = ++modeMenuGeneration;
        await modeLifetime.Ready;
        if (generation != modeMenuGeneration || menuInput?.IsCurrent(ModeButton) != true) return;
        if (!IsLoaded || !IsVisible || !module.CanChangePlaybackMode) return;
        modeActionKey = module.PlaybackModeActionKey;
        ModeMenu.Items.Clear();
        string presentationKey = module.PlaybackPresentationKey;
        if (module.CanReturnRecommendation && (module.HasPlaybackHeartbeat || module.PlaybackSpecialModeIcon == "sparkles"))
            ModeMenu.Items.Add(ModeAction(module.HasPlaybackHeartbeat ? "退出心动模式" : "退出推荐模式", "return",
                true, false, module.ReturnRecommendationAsync));
        if (module.HasActiveRecommendation)
            ModeMenu.Items.Add(ModeAction(module.PlaybackSpecialModeText, module.PlaybackSpecialModeIcon,
                module.CanChangeRecommendationContinue, module.RecommendationContinue,
                () => module.ChangeRecommendationContinueAsync(true)));
        else
        {
            if (module.HasCurrentRecommendationIdentity)
                ModeMenu.Items.Add(ModeAction("推荐模式", "sparkles", module.CanRecommendCurrent, false,
                    () => module.RecommendCurrentAsync(presentationKey)));
            if (module.CanHeartbeatCurrent)
                ModeMenu.Items.Add(ModeAction("心动模式", "heartbeat", true, false,
                    () => module.HeartbeatCurrentAsync(presentationKey)));
        }
        ModeMenu.Items.Add(ModeChoice("列表循环", "repeat", 1, !(module.HasActiveRecommendation && module.RecommendationContinue) && module.Mode == 1));
        ModeMenu.Items.Add(ModeChoice("单曲循环", "single", 3, !(module.HasActiveRecommendation && module.RecommendationContinue) && module.Mode == 3));
        ModeMenu.Items.Add(ModeChoice("随机播放", "shuffle", 2, !(module.HasActiveRecommendation && module.RecommendationContinue) && module.Mode == 2));
        if (module.HasPlaybackFm)
            ModeMenu.Items.Add(ModeAction("切换频道", "fm", CanOpenFmChoices, false,
                () => { OpenFmChoices(); return Task.CompletedTask; }));
        if (module.CanReconcileRecommendation)
            ModeMenu.Items.Add(ModeAction("核对保存结果", "", true, false, module.ReconcileRecommendationAsync));
        ModeMenu.PlacementTarget = ModeButton;
        ModeMenu.IsOpen = true;
    }
    private bool CanOpenFmChoices => module.HasPlaybackFm && module.CanChangePlaybackMode && !module.CanReconcileRecommendation;
    private void OpenFmChoices()
    {
        if (!IsLoaded || !IsVisible || !CanOpenFmChoices) return;
        returnModeFocus = true;
        fmActionKey = module.PlaybackFmActionKey;
        fmGeneration++;
        FmModeChoices.Children.Clear(); FmSceneChoices.Children.Clear();
        foreach (var choice in module.PlaybackFmModes) FmModeChoices.Children.Add(FmChoice(choice, scene: false));
        foreach (var choice in module.CatalogFmScenes) FmSceneChoices.Children.Add(FmChoice(choice, scene: true));
        fmHost = PlayerDialogHost.Find(this);
        if (fmHost is null) { FmModeChoices.Children.Clear(); FmSceneChoices.Children.Clear(); return; }
        openingFm = true;
        menuInput?.CloseCurrent();
        openingFm = false;
        FmContent.Content = null;
        fmOpen = true;
        fmHost.Show(this, FmPanel, FmClosed);
        fmHost.UpdateLayout();
        FocusFm();
    }
    private void FocusFm()
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
        var item = ModeAction(name, iconKey, module.CanChangePlaybackMode, selected, () => module.ChangeModeAsync(mode));
        item.Tag = mode;
        return item;
    }
    private MenuItem ModeAction(string name, string iconKey, bool enabled, bool selected, Func<Task> action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new MusicPlaybackIcon { Key = iconKey, Width = 18, Height = 18, Margin = new(0, 0, 10, 0) };
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon); content.Children.Add(label);
        var item = new MenuItem { Header = content, StaysOpenOnClick = true, IsEnabled = enabled,
            IsChecked = selected, Style = (Style)FindResource("Player.CurrentQualityItem") };
        icon.SetBinding(Control.ForegroundProperty, new Binding(nameof(Foreground)) { Source = item });
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = item });
        AutomationProperties.SetName(item, name);
        string actionKey = modeActionKey;
        long generation = modeMenuGeneration;
        item.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!ModeMenu.IsOpen || !item.IsEnabled || !IsLoaded || !IsVisible ||
                generation != modeMenuGeneration || actionKey != module.PlaybackModeActionKey) return;
            ModeMenu.IsOpen = false;
            await action();
        };
        return item;
    }
    private void ModeMenu_Closed(object? sender, RoutedEventArgs e)
    {
        surfaceInput?.LayerClosed(); surfaceInput?.Forget(ModeMenu);
        if (!openingFm && !fmOpen && menuInput?.HasCurrent != true && IsLoaded && IsVisible &&
            Window.GetWindow(this) is not { IsActive: false }) ModeButton.Focus();
    }
    // Logical dismissal retires actions/work immediately; native Closed only releases the HWND.
    // Both reusable ContextMenus wait for that same boundary before another open can be admitted.
    private sealed class NativeMenuLifetime
    {
        private static readonly DependencyPropertyDescriptor OpenProperty =
            DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
        private readonly ContextMenu menu;
        private readonly Func<Task> retire;
        private TaskCompletionSource? nativeClosed;
        internal Task Ready { get; private set; } = Task.CompletedTask;
        internal NativeMenuLifetime(ContextMenu menu, Func<Task> retire)
        {
            this.menu = menu; this.retire = retire;
            menu.Closed += (_, _) =>
            {
                var completion = nativeClosed; nativeClosed = null;
                // WPF forbids reopening a Popup inside its Closed callback.
                menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => completion?.TrySetResult()));
            };
        }
        internal void Attach()
        {
            OpenProperty.RemoveValueChanged(menu, OpenChanged);
            OpenProperty.AddValueChanged(menu, OpenChanged);
        }
        internal void Detach() => OpenProperty.RemoveValueChanged(menu, OpenChanged);
        private void OpenChanged(object? sender, EventArgs e)
        {
            if (menu.IsOpen) return;
            nativeClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Ready = Task.WhenAll(nativeClosed.Task, retire());
        }
        internal void Close()
        {
            if (menu.IsOpen) menu.IsOpen = false;
            else Ready = Task.WhenAll(Ready, retire());
        }
    }
    private Button FmChoice(MusicCatalogChoice choice, bool scene)
    {
        bool selected = module.PlaybackFmRuleId.Length == 0 && module.PlaybackFmTabTagId.Length == 0 &&
            (scene ? module.PlaybackFmMode == "scene" && module.PlaybackFmScene == choice.Key :
                module.PlaybackFmMode == choice.Key);
        string actionKey = fmActionKey;
        long generation = fmGeneration;
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
            if (!fmOpen || !button.IsEnabled || !IsLoaded || !IsVisible || !CanOpenFmChoices ||
                generation != fmGeneration || actionKey != module.PlaybackFmActionKey) return;
            CloseFm();
            await module.ChangePlaybackFmAsync(scene ? "scene" : choice.Key, scene ? choice.Key : "", actionKey);
        };
        return button;
    }
    private void FmClosed()
    {
        fmActionKey = ""; fmGeneration++;
        FocusManager.SetFocusedElement(FmPanel, null);
        FmModeChoices.Children.Clear(); FmSceneChoices.Children.Clear();
        fmOpen = false; fmHost = null;
        surfaceInput?.LayerClosed();
        RestoreModeFocus();
    }
    private void RestoreModeFocus()
    {
        if (!returnModeFocus || !IsLoaded || !IsVisible || Window.GetWindow(this) is { IsVisible: false }) return;
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(ModeButton), ModeButton);
        if (Window.GetWindow(this) is null or { IsActive: true }) ModeButton.Focus();
    }
    private void CloseFm() => fmHost?.Close();
    private void CloseFm_Click(object sender, RoutedEventArgs e) => CloseFm();
    private void Queue_Click(object sender, RoutedEventArgs e) => ToggleMenu(QueueButton);
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
        if (SourcePopup.IsOpen && !module.HasSourceChoices) SourcePopup.IsOpen = false;
        if (!module.HasQualityButton) QualityMenu.IsOpen = false;
        if (ModeMenu.IsOpen && (modeActionKey != module.PlaybackModeActionKey || !module.CanChangePlaybackMode))
            ModeMenu.IsOpen = false;
        if (fmOpen && (fmActionKey != module.PlaybackFmActionKey || !CanOpenFmChoices)) CloseFm();
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
