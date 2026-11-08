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
    public MusicPlayerView(MusicCapability module, bool showArtwork = true)
    {
        InitializeComponent();
        if (!showArtwork) { MetadataPanel.Children.Remove(ArtworkButton); ArtworkButton.Content = null; }
        this.module = module; DataContext = module;
        QualityMenu.DataContext = module;
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
        foreach (var popup in new[] { SourcePopup, PlaylistPopup, VolumePopup, RatePopup })
        {
            popup.Opened += (_, _) => { if (popup.Child is Visual visual) surfaceInput?.Observe(visual); };
            popup.Closed += (_, _) => { surfaceInput?.LayerClosed(); if (popup.Child is Visual visual) surfaceInput?.Forget(visual); };
        }
        QualityMenu.Opened += (_, _) => surfaceInput?.Observe(QualityMenu);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Deactivate(); };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged;
            Deactivate();
            surfaceInput?.Dispose(); surfaceInput = null;
        };
        SizeChanged += (_, _) => MetadataText.MaxWidth = Math.Min(200, Math.Max(30, ActualWidth / 2 - 260));
    }
    internal void Deactivate()
    {
        surfaceInput?.Cancel();
        qualityRequest++;
        QualityMenu.IsOpen = false;
        SourcePopup.IsOpen = false;
        PlaylistPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        RatePopup.IsOpen = false;
        if (MoreButton.ContextMenu is { } menu) menu.IsOpen = false;
        Progress.CancelScrub(); RestoreProgress();
    }
    private bool LayersOpen() => QualityMenu.IsOpen || SourcePopup.IsOpen || PlaylistPopup.IsOpen ||
        VolumePopup.IsOpen || RatePopup.IsOpen || MoreButton.ContextMenu?.IsOpen == true;
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
    /// <summary>The "more" menu: playback rate (its own panel) and "wrong source".</summary>
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
    private async void Mode_Click(object sender, RoutedEventArgs e) => await module.ChangeModeAsync((module.Mode + 1) % 4);
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
        if (SourcePopup.IsOpen && !module.HasSourceChoices) SourcePopup.IsOpen = false;
        if (!module.HasQualityButton) QualityMenu.IsOpen = false;
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
