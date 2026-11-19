using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Windows.System;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView : UserControl
{
    private readonly MusicCapability module;
    private string? scrubKey;
    private int qualityRequest;
    private long modeGeneration, fmGeneration;
    private string modeActionKey = "", fmActionKey = "";
    private FlyoutBase? activeFlyout;
    private bool openingFm;
    private PlayerSurfaceInput? surfaceInput;
    private DesktopWindow? window;
    private string rateKey = "";
    private Task qualityRetired = Task.CompletedTask;
    private Task flyoutRetired = Task.CompletedTask;
    private PlayerDialogHost? fmHost;
    internal Border FmPanel { get; }
    private ScrollViewer FmScroll { get; }
    private StackPanel FmModeChoices { get; }
    private Grid FmSceneChoices { get; }
    private PlayerMenuInput? menuInput;
    private readonly List<IDisposable> menuRegistrations = [];
    private readonly Geometry playGeometry = MusicVisuals.Geometry("M15.5,11 L29,20 L15.5,29 Z");
    private readonly Geometry pauseGeometry = MusicVisuals.Geometry("M13,11 L17,11 17,29 13,29 Z M23,11 L27,11 27,29 23,29 Z");
    private readonly Brush favoriteFill = MusicVisuals.Brush("#FC3D49");
    private readonly Brush emptyFavoriteFill = MusicVisuals.Brush("#00000000");
    public MusicPlayerView(MusicCapability module, bool showArtwork = true)
    {
        this.module = module; InitializeComponent(); DataContext = module;
        DesktopTheme.Observe(SourcePanel, RefreshSourcePalette);
        DesktopTheme.Observe(this, RefreshFooterPalette);
        foreach (var button in new Button[] { FavoriteButton, ModeButton, PreviousButton, NextButton, QueueButton,
            QualityButton, PlaylistButton, DesktopLyricsButton, VolumeButton, MoreButton, PlayButton })
            button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => RefreshFooterPalette());
        FavoriteBadge.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            FavoriteBadge.Visibility = FavoriteBadge.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        if (showArtwork)
        {
            var record = new MusicRecordView { Width = 60, Height = 60 };
            ArtworkScope.SetLoader(record, module.ArtworkLoader);
            MusicVisuals.Bind(record, MusicRecordView.AddressProperty, nameof(MusicCapability.CurrentArtwork));
            MusicVisuals.Bind(record, MusicRecordView.ArtworkIdentityProperty, nameof(MusicCapability.CurrentArtworkIdentity));
            MusicVisuals.Bind(record, MusicRecordView.TrackIdentityProperty, nameof(MusicCapability.CurrentTrackIdentity));
            MusicVisuals.Bind(record, MusicRecordView.PlayingProperty, nameof(MusicCapability.IsPlaying));
            ArtworkButton.Content = record;
        }
        else MetadataPanel.Children.Remove(ArtworkButton);
        foreach (var flyout in new[] { PlaylistPopup, VolumePopup, RatePopup, SourcePopup })
            ((FrameworkElement)flyout.Content).DataContext = module;
        foreach (var (flyout, anchor) in new (FlyoutBase, Button)[]
        {
            (PlaylistPopup, PlaylistButton), (VolumePopup, VolumeButton), (RatePopup, MoreButton),
            (MoreMenu, MoreButton), (QualityMenu, QualityButton), (SourcePopup, QualityButton), (ModeMenu, ModeButton)
        })
        {
            flyout.Closing += (_, _) => FlyoutClosing(flyout, anchor);
            flyout.Closed += (_, _) => surfaceInput?.LayerClosed();
        }
        // The dialog host owns attachment. A live panel in Resources would enter the
        // resource dictionary itself and reenter implicit style lookup while it is locked.
        FmPanel = (Border)((DataTemplate)Resources["FmPanelTemplate"]).LoadContent();
        FmScroll = (ScrollViewer)FmPanel.FindName("FmScroll");
        FmModeChoices = (StackPanel)FmPanel.FindName("FmModeChoices");
        FmSceneChoices = (Grid)FmPanel.FindName("FmSceneChoices");
        FmPanel.DataContext = module;
        DesktopTheme.Observe(FmPanel, RefreshFmPalette);
        ObserveFmButton((Button)FmPanel.FindName("CloseFmButton"), false, false, close: true);
        FmPanel.Loaded += (_, _) => RevealFmSelection();
        Progress.ScrubStarted += (_, _) => BeginProgress();
        Progress.ScrubCancelled += (_, _) => RestoreProgress();
        Progress.ScrubCommitted += async (_, _) =>
        {
            var key = scrubKey; var position = Progress.Value; RestoreProgress();
            if (key is not null && key == module.PlaybackPresentationKey) await module.SeekAsync(position);
        };
        Loaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged; module.PropertyChanged += OnPlaybackChanged;
            window = DesktopWindow.GetWindow(this);
            if (window is not null)
            {
                window.VisibilityChanged += WindowVisibilityChanged;
                menuInput = PlayerMenuInput.For(this);
                menuRegistrations.Add(menuInput.Register(PlaylistButton, () => PlaylistPopup.IsOpen,
                    () => _ = Show(PlaylistPopup, PlaylistButton), CloseFlyout));
                menuRegistrations.Add(menuInput.Register(VolumeButton, () => VolumePopup.IsOpen,
                    () => _ = Show(VolumePopup, VolumeButton), CloseFlyout));
                menuRegistrations.Add(menuInput.Register(MoreButton, () => MoreMenu.IsOpen || RatePopup.IsOpen, OpenMore, CloseFlyout));
                menuRegistrations.Add(menuInput.Register(ModeButton, () => ModeMenu.IsOpen,
                    () => _ = OpenModeAsync(), CloseMode));
                menuRegistrations.Add(menuInput.Register(QualityButton, () => QualityMenu.IsOpen || SourcePopup.IsOpen,
                    () => _ = OpenQualityAsync(), CloseQuality));
                menuRegistrations.Add(menuInput.Register(QueueButton, () => PlayerSurfaceInput.GetQueueOpen(this),
                    () => _ = module.OpenPlayQueueAsync(), () => { }, queue: true));
                surfaceInput = new PlayerSurfaceInput(window, this, () => activeFlyout is { IsOpen: true } || fmHost?.IsOpen == true);
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged;
            if (window is not null) window.VisibilityChanged -= WindowVisibilityChanged;
            Deactivate();
            foreach (var registration in menuRegistrations) registration.Dispose();
            menuRegistrations.Clear(); menuInput = null;
            window = null; surfaceInput?.Dispose(); surfaceInput = null;
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility != Visibility.Visible) Deactivate(); });
        QualityButton.RightTapped += (_, e) => { e.Handled = true; OpenQualityMenu(); };
        QualityButton.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Application || e.Key == VirtualKey.F10 && DesktopVisual.Modifiers == VirtualKeyModifiers.Shift)
            { e.Handled = true; OpenQualityMenu(); }
        };
        SizeChanged += (_, _) => MetadataText.MaxWidth = Math.Min(200, Math.Max(30, ActualWidth / 2 - 262));
    }
    internal void Deactivate()
    {
        surfaceInput?.Cancel(); rateKey = "";
        if (new[] { PlaylistButton, VolumeButton, MoreButton, ModeButton, QualityButton }
            .Any(button => menuInput?.IsCurrent(button) == true)) menuInput!.CloseCurrent();
        CloseFlyout(); CloseFm();
        Progress.CancelScrub(); RestoreProgress();
    }
    private void WindowVisibilityChanged(object? sender, EventArgs e)
    { if (!DesktopVisual.IsVisible(this)) Deactivate(); }
    private async Task Show(FlyoutBase flyout, Button anchor)
    {
        if (menuInput is null) return;
        long generation = menuInput.Generation;
        CloseFlyout();
        await flyoutRetired;
        if (!DesktopVisual.IsVisible(this) || !menuInput.IsCurrent(anchor, generation)) return;
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Closed(object? sender, object args) { flyout.Closed -= Closed; retired.TrySetResult(); }
        flyout.Closed += Closed; flyoutRetired = retired.Task;
        activeFlyout = flyout;
        if (ReferenceEquals(flyout, RatePopup)) rateKey = module.PlaybackPresentationKey;
        flyout.ShowAt(anchor);
    }
    private void CloseFlyout()
    {
        var closing = activeFlyout; activeFlyout = null;
        if (closing is not { IsOpen: true }) return;
        closing.Hide();
    }
    private void CloseQuality()
    {
        qualityRequest++; qualityRetired = module.DeactivateQualityAsync();
        module.CloseSourceChoices(); CloseFlyout();
    }
    private void CloseMode()
    {
        if (!openingFm) { modeActionKey = ""; modeGeneration++; }
        CloseFlyout();
    }
    private void FlyoutClosing(FlyoutBase flyout, Button anchor)
    {
        if (!ReferenceEquals(activeFlyout, flyout)) return;
        activeFlyout = null;
        if (ReferenceEquals(anchor, QualityButton)) CloseQuality();
        if (ReferenceEquals(anchor, ModeButton)) CloseMode();
        if (ReferenceEquals(flyout, RatePopup)) rateKey = "";
        menuInput?.Closed(anchor);
    }
    private static bool IsBlank(object sender, object originalSource)
    {
        for (var node = originalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or Slider or TextBox or ScrollBar) return false;
            if (ReferenceEquals(node, sender)) return true;
        }
        return false;
    }
    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginSurfaceGesture(e.OriginalSource);
    }
    private async void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var command = EndSurfaceGesture(e.OriginalSource, e.Handled);
        e.Handled |= command.Handled;
        await command.Work;
    }
    internal void BeginSurfaceGesture(object originalSource) => surfaceInput?.Press(IsBlank(this, originalSource));
    internal (bool Handled, Task Work) EndSurfaceGesture(object originalSource, bool handled = false)
    {
        bool open = surfaceInput?.Release(!handled && IsBlank(this, originalSource)) == true;
        return (open, open ? module.OpenPlayerDetailAsync() : Task.CompletedTask);
    }
    private async void Lyrics_Click(object sender, RoutedEventArgs e) => await module.OpenPlayerDetailAsync();
    private async void Favorite_Click(object sender, RoutedEventArgs e) => await module.ToggleCurrentFavoriteAsync();
    private async void DesktopLyrics_Click(object sender, RoutedEventArgs e) => await module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
    private void PlaylistMenu_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(PlaylistButton);
    private async void AddCurrent_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id }) { PlaylistPopup.Hide(); await module.AddCurrentToPlaylistAsync(id); } }
    private void Volume_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(VolumeButton);
    private async void VolumeCommit_PointerReleased(object sender, PointerRoutedEventArgs e) => await module.SaveVolumeAsync();
    private async void VolumeCommit_KeyUp(object sender, KeyRoutedEventArgs e)
    { if (IsRangeKey(e.Key)) await module.SaveVolumeAsync(); }
    private void Quality_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(QualityButton);
    internal void OpenQualityMenu() => menuInput?.Show(QualityButton);
    private async Task OpenQualityAsync()
    {
        int request = ++qualityRequest; await qualityRetired;
        await flyoutRetired;
        if (!CanOpenQuality(request)) return;
        await module.ActivateQualitySettingsAsync();
        if (!CanOpenQuality(request)) return;
        QualityMenu.Items.Clear();
        var sources = new MenuFlyoutItem { Text = "查看更多", IsEnabled = module.CanSwitchSource };
        sources.Click += SourceList_Click; QualityMenu.Items.Add(sources); QualityMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var choice in module.QualityChoices()) QualityMenu.Items.Add(QualityItem(choice));
        var spatial = module.SpatialChoices();
        if (spatial.Count > 0)
        {
            var submenu = new MenuFlyoutSubItem { Text = MusicQualityLabels.Name("spatial") };
            foreach (var choice in spatial) submenu.Items.Add(QualityItem(choice));
            QualityMenu.Items.Add(submenu);
        }
        await Show(QualityMenu, QualityButton);
    }
    private bool CanOpenQuality(int request) => request == qualityRequest && menuInput?.IsCurrent(QualityButton) == true &&
        DesktopVisual.IsVisible(this) && module.HasQualityButton;
    private ToggleMenuFlyoutItem QualityItem(MusicQualityChoice choice)
    {
        var item = new MusicPlayerMenuChoice { Style = (Style)Resources["Player.MenuChoice"], Text = choice.Label, Tag = choice.Key, IsChecked = choice.Current, IsEnabled = module.CanSwitchSource };
        item.Click += async (_, _) => { QualityMenu.Hide(); await module.SwitchQualityAsync(choice.Key); };
        return item;
    }
    private void SourceList_Click(object sender, RoutedEventArgs e)
    {
        if (!module.CanSwitchSource) return;
        module.OpenSourceChoices();
        _ = Show(SourcePopup, QualityButton); Refresh();
    }
    private async void LoadMoreSources_Click(object sender, RoutedEventArgs e) => await module.LoadMoreSourcesAsync();
    private async void SourceChoice_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicSourceChoice choice }) { SourcePopup.Hide(); await module.SelectSourceChoiceAsync(choice); } }
    private void SourceChoice_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicSourceChoice choice } button) return;
        button.Loaded -= SourceChoice_Loaded;
        button.Visibility = choice.Key == "auto" ? Visibility.Collapsed : Visibility.Visible;
        DesktopTheme.Observe(button, () => RefreshSourceChoice(button));
        button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => RefreshSourceChoice(button));
        RefreshSourceChoice(button);
        AutomationProperties.SetItemStatus(button, choice.Current ? "当前" : choice.State);
    }
    private void RefreshSourcePalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(SourcePanel);
        var settings = new Windows.UI.ViewManagement.UISettings();
        SourcePanel.Background = contrast ? new SolidColorBrush(settings.UIElementColor(Windows.UI.ViewManagement.UIElementType.Window)) : (Brush)DesktopVisual.Resource(SourcePanel, "MeowFilm.Brush.FloatingSurface");
    }
    private static void RefreshSourceChoice(Button button)
    {
        bool contrast = DesktopTheme.GetIsHighContrast(button);
        bool current = button.Tag is MusicSourceChoice { Current: true };
        var settings = new Windows.UI.ViewManagement.UISettings();
        button.Foreground = contrast ? new SolidColorBrush(settings.UIElementColor(!button.IsEnabled ? Windows.UI.ViewManagement.UIElementType.GrayText : current ? Windows.UI.ViewManagement.UIElementType.HighlightText : Windows.UI.ViewManagement.UIElementType.WindowText)) : (Brush)DesktopVisual.Resource(button, button.IsEnabled ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Disabled");
        button.Background = contrast ? new SolidColorBrush(settings.UIElementColor(button.IsEnabled && current ? Windows.UI.ViewManagement.UIElementType.Highlight : Windows.UI.ViewManagement.UIElementType.Window)) : current ? (Brush)DesktopVisual.Resource(button, "MeowFilm.Brush.ItemSelected") : MusicVisuals.Brush("#00000000");
    }
    private void CloseSources_Click(object sender, RoutedEventArgs e) => SourcePopup.Hide();
    private void More_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(MoreButton);
    private void OpenMore()
    {
        MoreMenu.Items.Clear();
        var rate = new MenuFlyoutItem { Text = "播放倍速 · " + module.PlaybackRateText };
        rate.Click += (_, _) => _ = Show(RatePopup, MoreButton);
        var wrong = new MenuFlyoutItem { Text = "音源不对", IsEnabled = module.CanSwitchSource };
        wrong.Click += async (_, _) => await module.ReportWrongSourceAsync();
        MoreMenu.Items.Add(rate); MoreMenu.Items.Add(wrong); _ = Show(MoreMenu, MoreButton);
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await module.TogglePlaybackAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await module.StepAsync(false);
    private async void Next_Click(object sender, RoutedEventArgs e) => await module.StepAsync(true);
    private void Mode_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(ModeButton);
    private async Task OpenModeAsync()
    {
        if (menuInput is null) return;
        long request = menuInput.Generation;
        await flyoutRetired;
        if (!module.CanChangePlaybackMode || !menuInput.IsCurrent(ModeButton, request) || !DesktopVisual.IsVisible(this)) return;
        modeActionKey = module.PlaybackModeActionKey; modeGeneration++;
        ModeMenu.Items.Clear(); string key = module.PlaybackPresentationKey;
        if (module.CanReturnRecommendation && (module.HasPlaybackHeartbeat || module.PlaybackSpecialModeIcon == "sparkles"))
            ModeMenu.Items.Add(ModeAction(module.HasPlaybackHeartbeat ? "退出心动模式" : "退出推荐模式", true, false, module.ReturnRecommendationAsync));
        if (module.HasActiveRecommendation)
            ModeMenu.Items.Add(ModeAction(module.PlaybackSpecialModeText, module.CanChangeRecommendationContinue, module.RecommendationContinue, () => module.ChangeRecommendationContinueAsync(true)));
        else
        {
            if (module.HasCurrentRecommendationIdentity) ModeMenu.Items.Add(ModeAction("推荐模式", module.CanRecommendCurrent, false, () => module.RecommendCurrentAsync(key)));
            if (module.CanHeartbeatCurrent) ModeMenu.Items.Add(ModeAction("心动模式", true, false, () => module.HeartbeatCurrentAsync(key)));
        }
        foreach (var (label, mode) in new[] { ("列表循环", 1), ("单曲循环", 3), ("随机播放", 2) })
            ModeMenu.Items.Add(ModeAction(label, module.CanChangePlaybackMode, !(module.HasActiveRecommendation && module.RecommendationContinue) && module.Mode == mode, () => module.ChangeModeAsync(mode)));
        if (module.HasPlaybackFm) ModeMenu.Items.Add(ModeAction("切换频道", CanOpenFmChoices, false, OpenFmChoicesAsync));
        if (module.CanReconcileRecommendation) ModeMenu.Items.Add(ModeAction("核对保存结果", true, false, module.ReconcileRecommendationAsync));
        await Show(ModeMenu, ModeButton);
    }
    private ToggleMenuFlyoutItem ModeAction(string name, bool enabled, bool selected, Func<Task> action)
    {
        string icon = name switch { "列表循环" => "repeat", "单曲循环" => "single", "随机播放" => "shuffle", "心动模式" => "heartbeat", "切换频道" => "fm", "退出推荐模式" or "退出心动模式" => "return", "推荐模式" => "sparkles", _ => module.PlaybackSpecialModeIcon };
        var item = new MusicPlayerMenuChoice { Style = (Style)Resources["Player.MenuChoice"], Text = name, IconKey = icon, IsEnabled = enabled, IsChecked = selected };
        string key = modeActionKey; long generation = modeGeneration;
        item.Click += async (_, _) =>
        {
            if (!item.IsEnabled || !DesktopVisual.IsVisible(this) || generation != modeGeneration || key != module.PlaybackModeActionKey) return;
            openingFm = name == "切换频道"; menuInput?.CloseCurrent();
            try { await action(); }
            finally { openingFm = false; }
        };
        return item;
    }
    private bool CanOpenFmChoices => module.HasPlaybackFm && module.CanChangePlaybackMode && !module.CanReconcileRecommendation;
    private async Task OpenFmChoicesAsync()
    {
        if (!CanOpenFmChoices || !IsLoaded) return;
        long menuGeneration = menuInput!.Generation;
        string actionKey = module.PlaybackFmActionKey;
        await flyoutRetired;
        if (menuInput is null || menuInput.Generation != menuGeneration || actionKey != module.PlaybackFmActionKey ||
            !CanOpenFmChoices || !DesktopVisual.IsVisible(this)) return;
        var host = PlayerDialogHost.Find(this) ?? throw new InvalidOperationException("PLAYER_DIALOG_HOST_UNAVAILABLE");
        CloseFm();
        fmActionKey = module.PlaybackFmActionKey; long generation = ++fmGeneration;
        foreach (var choice in module.PlaybackFmModes) FmModeChoices.Children.Add(FmChoice(choice, false, generation));
        int index = 0;
        foreach (var choice in module.CatalogFmScenes)
        {
            if (index % 4 == 0) FmSceneChoices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = FmChoice(choice, true, generation); Grid.SetColumn(button, index % 4); Grid.SetRow(button, index / 4);
            FmSceneChoices.Children.Add(button); index++;
        }
        fmHost = host;
        host.Show(this, FmPanel, CloseFm);
        FmPanel.UpdateLayout(); RevealFmSelection();
    }
    private void CloseFm_Click(object sender, RoutedEventArgs e) => fmHost?.RequestClose();
    private void CloseFm()
    {
        var host = fmHost; fmHost = null;
        if (host?.IsOpen == true) { host.Close(); return; }
        fmGeneration++; fmActionKey = "";
        FmModeChoices.Children.Clear(); FmSceneChoices.Children.Clear(); FmSceneChoices.RowDefinitions.Clear();
        surfaceInput?.LayerClosed();
        if (DesktopVisual.IsVisible(this)) ModeButton.Focus(FocusState.Programmatic);
    }
    private void RevealFmSelection()
    {
        if (fmHost?.IsOpen != true || !FmPanel.IsLoaded) return;
        var choices = FmModeChoices.Children.OfType<Button>().Concat(FmSceneChoices.Children.OfType<Button>());
        var selected = choices.FirstOrDefault(button => AutomationProperties.GetItemStatus(button) == "已选中") ?? choices.FirstOrDefault();
        if (selected is null) return;
        selected.Focus(FocusState.Programmatic);
        var point = selected.TransformToVisual(FmScroll).TransformPoint(new global::Windows.Foundation.Point());
        FmScroll.ChangeView(null, Math.Max(0, FmScroll.VerticalOffset + point.Y -
            (FmScroll.ViewportHeight - selected.ActualHeight) / 2), null, true);
    }
    private Button FmChoice(MusicCatalogChoice choice, bool scene, long generation)
    {
        bool selected = module.PlaybackFmRuleId.Length == 0 && module.PlaybackFmTabTagId.Length == 0 &&
            (scene ? module.PlaybackFmMode == "scene" && module.PlaybackFmScene == choice.Key : module.PlaybackFmMode == choice.Key);
        string key = fmActionKey;
        var button = new Button { Tag = choice, Template = (ControlTemplate)FmPanel.Resources["Player.FmButtonTemplate"],
            MinHeight = scene ? 84 : 64, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = scene ? new Thickness(2) : new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1), Padding = scene ? new Thickness(2, 10, 2, 10) : new Thickness(12),
            FontSize = scene ? 12 : 16, FontWeight = Microsoft.UI.Text.FontWeights.Normal, UseSystemFocusVisuals = true };
        var content = new StackPanel { Spacing = 10 };
        if (scene)
        {
            var icon = new MusicPlaybackIcon { Key = choice.Key, Width = 25, Height = 25, HorizontalAlignment = HorizontalAlignment.Center };
            MusicVisuals.Bind(icon, ForegroundProperty, nameof(Foreground), button); content.Children.Add(icon);
        }
        var text = new TextBlock { Text = choice.Name, FontSize = button.FontSize, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        MusicVisuals.Bind(text, TextBlock.ForegroundProperty, nameof(Foreground), button); content.Children.Add(text);
        button.Content = content;
        AutomationProperties.SetName(button, choice.Name); AutomationProperties.SetItemStatus(button, selected ? "已选中" : "");
        ObserveFmButton(button, selected, scene);
        button.Click += async (_, _) =>
        {
            if (!button.IsEnabled || fmHost?.IsOpen != true || !DesktopVisual.IsVisible(this) || !CanOpenFmChoices || generation != fmGeneration || key != module.PlaybackFmActionKey) return;
            fmHost.Close(); await module.ChangePlaybackFmAsync(scene ? "scene" : choice.Key, scene ? choice.Key : "", key);
        };
        return button;
    }
    private void Queue_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(QueueButton);
    private async void Rate_PointerReleased(object sender, PointerRoutedEventArgs e) => await CommitRateAsync();
    private async void Rate_KeyUp(object sender, KeyRoutedEventArgs e) { if (IsRangeKey(e.Key)) await CommitRateAsync(); }
    private Task CommitRateAsync() => rateKey == module.PlaybackPresentationKey && module.CanChangePlaybackRate
        ? module.ChangePlaybackRateAsync(Math.Round(RateSlider.Value, 1)) : Task.CompletedTask;
    private static bool IsRangeKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown;
    private async void RateDown_Click(object sender, RoutedEventArgs e) => await module.ChangePlaybackRateAsync(Math.Max(.5, Math.Round(module.PlaybackRate - .1, 1)));
    private async void RateUp_Click(object sender, RoutedEventArgs e) => await module.ChangePlaybackRateAsync(Math.Min(3, Math.Round(module.PlaybackRate + .1, 1)));
    private async void RateReset_Click(object sender, RoutedEventArgs e) => await module.ChangePlaybackRateAsync(1);
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!module.HasSourceChoices && ReferenceEquals(activeFlyout, SourcePopup)) SourcePopup.Hide();
        if (!module.HasQualityButton && ReferenceEquals(activeFlyout, QualityMenu)) QualityMenu.Hide();
        if ((modeActionKey != module.PlaybackModeActionKey || !module.CanChangePlaybackMode) && ReferenceEquals(activeFlyout, ModeMenu)) ModeMenu.Hide();
        if (fmActionKey != module.PlaybackFmActionKey || !CanOpenFmChoices) fmHost?.Close();
        if ((rateKey != module.PlaybackPresentationKey || !module.CanChangePlaybackRate) && ReferenceEquals(activeFlyout, RatePopup)) RatePopup.Hide();
        if (scrubKey is not null && (scrubKey != module.PlaybackPresentationKey || !module.CanSeek)) { Progress.CancelScrub(); RestoreProgress(); }
        Refresh();
    }
    private void Refresh()
    {
        PlayGlyph.Data = module.IsPlaying ? pauseGeometry : playGeometry;
        FavoriteGlyph.Fill = module.IsCurrentFavorite ? favoriteFill : emptyFavoriteFill;
        FavoriteGlyph.Stroke = module.IsCurrentFavorite ? FavoriteGlyph.Fill : FavoriteButton.Foreground;
    }
    private void RefreshFooterPalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        var settings = new global::Windows.UI.ViewManagement.UISettings();
        Brush SystemBrush(global::Windows.UI.ViewManagement.UIElementType type) => new SolidColorBrush(settings.UIElementColor(type));
        Brush Resource(string name) => (Brush)DesktopVisual.Resource(this, "Capability.Player." + name);
        foreach (var button in new Button[] { FavoriteButton, ModeButton, PreviousButton, NextButton, QueueButton,
            QualityButton, PlaylistButton, DesktopLyricsButton, VolumeButton, MoreButton })
            button.Foreground = contrast ? SystemBrush(button.IsEnabled ? global::Windows.UI.ViewManagement.UIElementType.WindowText :
                global::Windows.UI.ViewManagement.UIElementType.GrayText) : Resource("Foreground");
        ((TextBlock)MetadataText.Children[0]).Foreground = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("Text");
        ((TextBlock)MetadataText.Children[1]).Foreground = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("Muted");
        PlayButton.Background = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.Highlight) : (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent");
        PlayButton.Foreground = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.HighlightText) : MusicVisuals.Brush("#FFFFFF");
        Progress.RailBrush = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("Rail");
        Refresh();
    }
    private void BeginProgress()
    {
        if (!module.CanSeek) return;
        scrubKey = module.PlaybackPresentationKey; var position = Progress.Value;
        Progress.ClearValue(Slider.ValueProperty); Progress.Value = position;
    }
    private void RestoreProgress()
    {
        if (scrubKey is null) return; scrubKey = null;
        MusicVisuals.Bind(Progress, Slider.ValueProperty, nameof(MusicCapability.PositionSeconds));
    }
}
