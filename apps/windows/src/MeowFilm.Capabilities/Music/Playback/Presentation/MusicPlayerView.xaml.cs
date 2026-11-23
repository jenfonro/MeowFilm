using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Windows.System;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView : UserControl
{
    public MusicPlaybackController Playback { get; }
    public MusicPlaybackClock Clock => Playback.Clock;
    public MusicQueueController Queue { get; }
    public MusicLibraryController Library { get; }
    public MusicLyricsController Lyrics { get; }
    private readonly MusicSettingsController settings;
    private readonly MusicCatalogController catalog;

    private MusicRecordView? recordView;
    private bool backgroundVisualsPaused;
    private string? scrubKey;
    private int qualityRequest;
    private long modeGeneration, fmGeneration;
    private string modeActionKey = "", fmActionKey = "";
    private FlyoutBase? activeFlyout;
    private bool openingFm, openingSources;
    private PlayerSurfaceInput? surfaceInput;
    private DesktopWindow? window;
    private string rateKey = "";
    private Task qualityRetired = Task.CompletedTask;
    private Task flyoutRetired = Task.CompletedTask;
    private DesktopModalHost? fmHost;
    private DesktopModalHost? sourceHost;
    private MusicSourcePickerDialogView? sourcePicker;
    internal MusicFmPanelView FmPanel { get; }
    private PlayerMenuInput? menuInput;
    private readonly List<IDisposable> menuRegistrations = [];
    private readonly Geometry playGeometry = DesktopVisual.Geometry("M15.5,11 L29,20 L15.5,29 Z");
    private readonly Geometry pauseGeometry = DesktopVisual.Geometry("M13,11 L17,11 17,29 13,29 Z M23,11 L27,11 27,29 23,29 Z");

    public MusicPlayerView(
        MusicPlaybackController playback,
        MusicQueueController queue,
        MusicLibraryController library,
        MusicLyricsController lyrics,
        MusicSettingsController settings,
        MusicCatalogController catalog,
        bool showArtwork = true)
    {
        Playback = playback;
        Queue = queue;
        Library = library;
        Lyrics = lyrics;
        this.settings = settings;
        this.catalog = catalog;
        InitializeComponent();
        DesktopTheme.Observe(this, RefreshFooterPalette);
        foreach (var button in new Button[] { FavoriteButton, ModeButton, PreviousButton, NextButton, QueueButton,
            QualityButton, PlaylistButton, DesktopLyricsButton, VolumeButton, MoreButton, PlayButton })
            button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => RefreshFooterPalette());
        if (showArtwork)
        {
            ArtworkScope.SetLoader(RecordView, playback.ArtworkLoader);
            recordView = RecordView;
        }
        else MetadataPanel.Children.Remove(ArtworkButton);
        foreach (var (flyout, anchor) in new (FlyoutBase, Button)[]
        {
            (PlaylistPopup, PlaylistButton), (VolumePopup, VolumeButton), (RatePopup, MoreButton),
            (MoreMenu, MoreButton), (QualityMenu, QualityButton), (ModeMenu, ModeButton)
        })
        {
            flyout.Closing += (_, _) => FlyoutClosing(flyout, anchor);
            flyout.Closed += (_, _) => surfaceInput?.LayerClosed();
        }
        FmPanel = new MusicFmPanelView(queue, playback, () => fmHost?.RequestClose(), OnFmChoiceSelectedAsync);
        Progress.ScrubStarted += (_, _) => BeginProgress();
        Progress.ScrubCancelled += (_, _) => RestoreProgress();
        Progress.ScrubCommitted += async (_, _) =>
        {
            var key = scrubKey; var position = Progress.Value; RestoreProgress();
            if (key is not null && key == Playback.PlaybackPresentationKey) await Playback.SeekAsync(position);
        };
        Loaded += (_, _) =>
        {
            Playback.PropertyChanged -= OnPlaybackChanged; Playback.PropertyChanged += OnPlaybackChanged;
            Queue.PropertyChanged -= OnQueueChanged; Queue.PropertyChanged += OnQueueChanged;
            Clock.PropertyChanged -= OnClockChanged; Clock.PropertyChanged += OnClockChanged;
            Progress.Value = Clock.PositionSeconds;
            DetachWindowInput();
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
                menuRegistrations.Add(menuInput.Register(QualityButton, () => QualityMenu.IsOpen || sourceHost?.IsOpen == true,
                    () => _ = OpenQualityAsync(), CloseQuality));
                menuRegistrations.Add(menuInput.Register(QueueButton, () => PlayerSurfaceInput.GetQueueOpen(this),
                    () => _ = Playback.OpenPlayQueueAsync(), () => { }, queue: true));
                surfaceInput = new PlayerSurfaceInput(window, this, () => activeFlyout is { IsOpen: true } || fmHost?.IsOpen == true || sourceHost?.IsOpen == true);
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            Playback.PropertyChanged -= OnPlaybackChanged;
            Queue.PropertyChanged -= OnQueueChanged;
            Clock.PropertyChanged -= OnClockChanged;
            Deactivate();
            DetachWindowInput();
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
        CloseFlyout(); CloseFm(); CloseSourcePicker();
        Progress.CancelScrub(); RestoreProgress();
    }

    private void DetachWindowInput()
    {
        if (window is not null) window.VisibilityChanged -= WindowVisibilityChanged;
        foreach (var registration in menuRegistrations) registration.Dispose();
        menuRegistrations.Clear(); menuInput = null;
        window = null; surfaceInput?.Dispose(); surfaceInput = null;
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
        if (ReferenceEquals(flyout, RatePopup)) rateKey = Playback.PlaybackPresentationKey;
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
        if (openingSources) { CloseFlyout(); return; }
        qualityRequest++; qualityRetired = settings.DeactivateQualityAsync();
        CloseSourcePicker();
        Playback.CloseSourceChoices();
        CloseFlyout();
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
        if (!(ReferenceEquals(anchor, QualityButton) && openingSources))
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
        return (open, open ? Playback.OpenPlayerDetailAsync() : Task.CompletedTask);
    }

    private async void Lyrics_Click(object sender, RoutedEventArgs e) => await Playback.OpenPlayerDetailAsync();
    private async void Favorite_Click(object sender, RoutedEventArgs e) => await Playback.ToggleCurrentFavoriteAsync();
    private async void DesktopLyrics_Click(object sender, RoutedEventArgs e) => await Playback.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
    private void PlaylistMenu_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(PlaylistButton);
    private async void AddCurrent_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id }) { PlaylistPopup.Hide(); await Playback.AddCurrentToPlaylistAsync(id); } }
    private void Volume_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(VolumeButton);
    private async void VolumeCommit_PointerReleased(object sender, PointerRoutedEventArgs e) => await settings.SaveVolumeAsync();
    private async void VolumeCommit_KeyUp(object sender, KeyRoutedEventArgs e)
    { if (IsRangeKey(e.Key)) await settings.SaveVolumeAsync(); }
    private void Quality_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(QualityButton);
    internal void OpenQualityMenu() => menuInput?.Show(QualityButton);

    private async Task OpenQualityAsync()
    {
        int request = ++qualityRequest; await qualityRetired;
        await flyoutRetired;
        if (!CanOpenQuality(request)) return;
        await settings.ActivateQualitySettingsAsync();
        if (!CanOpenQuality(request)) return;
        QualityMenu.Items.Clear();
        var sources = new MenuFlyoutItem { Text = "查看更多", IsEnabled = Playback.CanSwitchSource };
        sources.Click += SourceList_Click; QualityMenu.Items.Add(sources); QualityMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var choice in Playback.QualityChoices()) QualityMenu.Items.Add(QualityItem(choice));
        var spatial = Playback.SpatialChoices();
        if (spatial.Count > 0)
        {
            var submenu = new MenuFlyoutSubItem { Text = MusicQualityLabels.Name("spatial") };
            foreach (var choice in spatial) submenu.Items.Add(QualityItem(choice));
            QualityMenu.Items.Add(submenu);
        }
        await Show(QualityMenu, QualityButton);
    }

    private bool CanOpenQuality(int request) => request == qualityRequest && menuInput?.IsCurrent(QualityButton) == true &&
        DesktopVisual.IsVisible(this) && Playback.HasQualityButton;

    private ToggleMenuFlyoutItem QualityItem(MusicQualityChoice choice)
    {
        var item = new MusicPlayerMenuChoice { Style = (Style)Resources["Player.MenuChoice"], Text = choice.Label, Tag = choice.Key, IsChecked = choice.Current, IsEnabled = Playback.CanSwitchSource };
        item.Click += async (_, _) => { QualityMenu.Hide(); await Playback.SwitchQualityAsync(choice.Key); };
        return item;
    }

    private async void SourceList_Click(object sender, RoutedEventArgs e)
    {
        if (!Playback.CanSwitchSource || !IsLoaded) return;
        openingSources = true;
        try
        {
            CloseFlyout();
            await flyoutRetired;
            if (!Playback.CanSwitchSource || !DesktopVisual.IsVisible(this)) return;
            var host = DesktopModalHost.Find(this) ?? throw new InvalidOperationException("MODAL_HOST_UNAVAILABLE");
            CloseSourcePicker();
            Playback.OpenSourceChoices();
            sourcePicker = new MusicSourcePickerDialogView(Playback, () => sourceHost?.RequestClose());
            sourceHost = host;
            host.Show(this, sourcePicker, CloseSourcePicker);
            Refresh();
        }
        finally { openingSources = false; }
    }

    private void CloseSourcePicker()
    {
        var host = sourceHost;
        sourceHost = null;
        if (host?.IsOpen == true && sourcePicker is not null && ReferenceEquals(host.ActiveContent, sourcePicker))
        {
            host.Close(sourcePicker);
            return;
        }
        if (sourcePicker is null) return;
        sourcePicker = null;
        qualityRequest++;
        qualityRetired = settings.DeactivateQualityAsync();
        Playback.CloseSourceChoices();
        menuInput?.Closed(QualityButton);
        surfaceInput?.LayerClosed();
        if (DesktopVisual.IsVisible(this)) QualityButton.Focus(FocusState.Programmatic);
    }

    private void More_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(MoreButton);

    private void OpenMore()
    {
        MoreMenu.Items.Clear();
        var rate = new MenuFlyoutItem { Text = "播放倍速 · " + Playback.PlaybackRateText };
        rate.Click += (_, _) => _ = Show(RatePopup, MoreButton);
        var wrong = new MenuFlyoutItem { Text = "音源不对", IsEnabled = Playback.CanSwitchSource };
        wrong.Click += async (_, _) => await Playback.ReportWrongSourceAsync();
        MoreMenu.Items.Add(rate); MoreMenu.Items.Add(wrong); _ = Show(MoreMenu, MoreButton);
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await Playback.TogglePlaybackAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Playback.StepAsync(false);
    private async void Next_Click(object sender, RoutedEventArgs e) => await Playback.StepAsync(true);
    private void Mode_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(ModeButton);

    private async Task OpenModeAsync()
    {
        if (menuInput is null) return;
        long request = menuInput.Generation;
        await flyoutRetired;
        if (!Queue.CanChangePlaybackMode || !menuInput.IsCurrent(ModeButton, request) || !DesktopVisual.IsVisible(this)) return;
        modeActionKey = Queue.PlaybackModeActionKey; modeGeneration++;
        ModeMenu.Items.Clear(); string key = Playback.PlaybackPresentationKey;
        if (Queue.CanReturnRecommendation && (Queue.HasPlaybackHeartbeat || Queue.PlaybackSpecialModeIcon == "sparkles"))
            ModeMenu.Items.Add(ModeAction(Queue.HasPlaybackHeartbeat ? "退出心动模式" : "退出推荐模式", true, false, Queue.ReturnRecommendationAsync));
        if (Queue.HasActiveRecommendation)
            ModeMenu.Items.Add(ModeAction(Queue.PlaybackSpecialModeText, Queue.CanChangeRecommendationContinue, Queue.RecommendationContinue, () => Queue.ChangeRecommendationContinueAsync(true)));
        else
        {
            if (Queue.HasCurrentRecommendationIdentity) ModeMenu.Items.Add(ModeAction("推荐模式", Queue.CanRecommendCurrent, false, () => Queue.RecommendCurrentAsync(key)));
            if (Queue.CanHeartbeatCurrent) ModeMenu.Items.Add(ModeAction("心动模式", true, false, () => Queue.HeartbeatCurrentAsync(key)));
        }
        foreach (var (label, mode) in new[] { ("列表循环", 1), ("单曲循环", 3), ("随机播放", 2) })
            ModeMenu.Items.Add(ModeAction(label, Queue.CanChangePlaybackMode, !(Queue.HasActiveRecommendation && Queue.RecommendationContinue) && Playback.Mode == mode, () => Playback.ChangeModeAsync(mode)));
        if (Queue.HasPlaybackFm) ModeMenu.Items.Add(ModeAction("切换频道", CanOpenFmChoices, false, OpenFmChoicesAsync));
        if (Queue.CanReconcileRecommendation) ModeMenu.Items.Add(ModeAction("核对保存结果", true, false, Queue.ReconcileRecommendationAsync));
        await Show(ModeMenu, ModeButton);
    }

    private ToggleMenuFlyoutItem ModeAction(string name, bool enabled, bool selected, Func<Task> action)
    {
        string icon = name switch { "列表循环" => "repeat", "单曲循环" => "single", "随机播放" => "shuffle", "心动模式" => "heartbeat", "切换频道" => "fm", "退出推荐模式" or "退出心动模式" => "return", "推荐模式" => "sparkles", _ => Queue.PlaybackSpecialModeIcon };
        var item = new MusicPlayerMenuChoice { Style = (Style)Resources["Player.MenuChoice"], Text = name, IconKey = icon, IsEnabled = enabled, IsChecked = selected };
        string key = modeActionKey; long generation = modeGeneration;
        item.Click += async (_, _) =>
        {
            if (!item.IsEnabled || !DesktopVisual.IsVisible(this) || generation != modeGeneration || key != Queue.PlaybackModeActionKey) return;
            openingFm = name == "切换频道"; menuInput?.CloseCurrent();
            try { await action(); }
            finally { openingFm = false; }
        };
        return item;
    }

    private bool CanOpenFmChoices => Queue.HasPlaybackFm && Queue.CanChangePlaybackMode && !Queue.CanReconcileRecommendation;

    private async Task OpenFmChoicesAsync()
    {
        if (!CanOpenFmChoices || !IsLoaded) return;
        long menuGeneration = menuInput!.Generation;
        string actionKey = Queue.PlaybackFmActionKey;
        await flyoutRetired;
        if (menuInput is null || menuInput.Generation != menuGeneration || actionKey != Queue.PlaybackFmActionKey ||
            !CanOpenFmChoices || !DesktopVisual.IsVisible(this)) return;
        var host = DesktopModalHost.Find(this) ?? throw new InvalidOperationException("MODAL_HOST_UNAVAILABLE");
        CloseFm();
        fmActionKey = Queue.PlaybackFmActionKey;
        fmGeneration++;
        FmPanel.Populate(Queue.PlaybackFmModes, catalog.CatalogFmScenes);
        fmHost = host;
        host.Show(this, FmPanel, CloseFm);
        FmPanel.UpdateLayout();
        FmPanel.RevealSelection();
    }

    private void CloseFm()
    {
        var host = fmHost; fmHost = null;
        if (host?.IsOpen == true && ReferenceEquals(host.ActiveContent, FmPanel)) { host.Close(FmPanel); return; }
        fmGeneration++; fmActionKey = "";
        FmPanel.Clear();
        surfaceInput?.LayerClosed();
        if (DesktopVisual.IsVisible(this)) ModeButton.Focus(FocusState.Programmatic);
    }

    private async Task OnFmChoiceSelectedAsync(MusicFmChoiceItem item)
    {
        long generation = fmGeneration;
        string key = fmActionKey;
        if (fmHost?.IsOpen != true || !DesktopVisual.IsVisible(this) || !CanOpenFmChoices || generation != fmGeneration || key != Queue.PlaybackFmActionKey) return;
        fmHost.Close(FmPanel);
        await Queue.ChangePlaybackFmAsync(item.IsScene ? "scene" : item.Choice.Key, item.IsScene ? item.Choice.Key : "", key);
    }

    private void Queue_Click(object sender, RoutedEventArgs e) => menuInput?.Toggle(QueueButton);
    private async void Rate_PointerReleased(object sender, PointerRoutedEventArgs e) => await CommitRateAsync();
    private async void Rate_KeyUp(object sender, KeyRoutedEventArgs e) { if (IsRangeKey(e.Key)) await CommitRateAsync(); }
    private Task CommitRateAsync() => rateKey == Playback.PlaybackPresentationKey && Playback.CanChangePlaybackRate
        ? Playback.ChangePlaybackRateAsync(Math.Round(RateSlider.Value, 1)) : Task.CompletedTask;
    private static bool IsRangeKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown;
    private async void RateDown_Click(object sender, RoutedEventArgs e) => await Playback.ChangePlaybackRateAsync(Math.Max(.5, Math.Round(Playback.PlaybackRate - .1, 1)));
    private async void RateUp_Click(object sender, RoutedEventArgs e) => await Playback.ChangePlaybackRateAsync(Math.Min(3, Math.Round(Playback.PlaybackRate + .1, 1)));
    private async void RateReset_Click(object sender, RoutedEventArgs e) => await Playback.ChangePlaybackRateAsync(1);
    private double ToVolumeDouble(int volume) => volume;
    private void FromVolumeDouble(double value) => Playback.Volume = (int)Math.Round(value);

    internal void SetBackgroundVisualsPaused(bool paused)
    {
        if (backgroundVisualsPaused == paused) return;
        backgroundVisualsPaused = paused;
        Refresh();
    }

    private void OnClockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (scrubKey is null && (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MusicPlaybackClock.PositionSeconds)))
            Progress.Value = Clock.PositionSeconds;
    }

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((modeActionKey != Queue.PlaybackModeActionKey || !Queue.CanChangePlaybackMode) && ReferenceEquals(activeFlyout, ModeMenu)) ModeMenu.Hide();
        if (fmActionKey != Queue.PlaybackFmActionKey || !CanOpenFmChoices) fmHost?.Close(FmPanel);
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlaybackController.PlayerBackground))
            FmPanel.RefreshPalette();
        if (!Playback.HasSourceChoices && sourceHost?.IsOpen == true && sourcePicker is not null) sourceHost.Close(sourcePicker);
        if (!Playback.HasQualityButton && ReferenceEquals(activeFlyout, QualityMenu)) QualityMenu.Hide();
        if ((modeActionKey != Queue.PlaybackModeActionKey || !Queue.CanChangePlaybackMode) && ReferenceEquals(activeFlyout, ModeMenu)) ModeMenu.Hide();
        if (fmActionKey != Queue.PlaybackFmActionKey || !CanOpenFmChoices) fmHost?.Close(FmPanel);
        if ((rateKey != Playback.PlaybackPresentationKey || !Playback.CanChangePlaybackRate) && ReferenceEquals(activeFlyout, RatePopup)) RatePopup.Hide();
        if (scrubKey is not null && (scrubKey != Playback.PlaybackPresentationKey || !Playback.CanSeek)) { Progress.CancelScrub(); RestoreProgress(); }
        Refresh();
    }

    private void Refresh()
    {
        bool visualPlaying = !backgroundVisualsPaused && Playback.IsPlaying;
        if (recordView is not null) recordView.Playing = visualPlaying;
        Progress.IsPlaying = visualPlaying;
        PlayGlyph.Data = Playback.IsPlaying ? pauseGeometry : playGeometry;
        FavoriteButton.IsFavorited = Playback.IsCurrentFavorite;
    }

    private void RefreshFooterPalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        static Brush SystemBrush(global::Windows.UI.ViewManagement.UIElementType type) => SystemAppearance.GetElementBrush(type);
        Brush Resource(string name) => (Brush)DesktopVisual.Resource(this, "Capability.Player." + name);
        foreach (var button in new Button[] { FavoriteButton, ModeButton, PreviousButton, NextButton, QueueButton,
            QualityButton, PlaylistButton, DesktopLyricsButton, VolumeButton, MoreButton })
            button.Foreground = contrast ? SystemBrush(button.IsEnabled ? global::Windows.UI.ViewManagement.UIElementType.WindowText :
                global::Windows.UI.ViewManagement.UIElementType.GrayText) : Resource("Foreground");
        ((TextBlock)MetadataText.Children[0]).Foreground = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("Text");
        ((TextBlock)MetadataText.Children[1]).Foreground = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("Muted");
        PlayButton.Background = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.Highlight) : (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent");
        PlayButton.Foreground = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.HighlightText) : DesktopVisual.Brush("#FFFFFF");
        Progress.RailBrush = contrast ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("Rail");
        Refresh();
    }

    private void BeginProgress()
    {
        if (!Playback.CanSeek) return;
        scrubKey = Playback.PlaybackPresentationKey;
    }

    private void RestoreProgress()
    {
        if (scrubKey is null) return;
        scrubKey = null;
        Progress.Value = Clock.PositionSeconds;
    }
}
