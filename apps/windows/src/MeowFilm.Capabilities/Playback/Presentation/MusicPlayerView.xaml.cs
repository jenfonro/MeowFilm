using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;
using System.Windows.Automation;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView : UserControl
{
    private readonly MusicCapability module;
    private string? scrubKey;
    private int qualityRequest;
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
        Loaded += (_, _) => { module.PropertyChanged -= OnPlaybackChanged; module.PropertyChanged += OnPlaybackChanged; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Deactivate(); };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged;
            Deactivate();
        };
        SizeChanged += (_, _) => MetadataText.MaxWidth = Math.Min(200, Math.Max(30, ActualWidth / 2 - 260));
    }
    internal void Deactivate()
    {
        qualityRequest++;
        QualityMenu.IsOpen = false;
        SourcePopup.IsOpen = false;
        PlaylistPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        RatePopup.IsOpen = false;
        if (MoreButton.ContextMenu is { } menu) menu.IsOpen = false;
        Progress.CancelScrub(); RestoreProgress();
    }
    private async void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && !ReferenceEquals(node, sender))
        {
            if (node is ButtonBase or Slider or Thumb or TextBoxBase or ScrollBar) return;
            node = node is FrameworkContentElement text ? text.Parent : VisualTreeHelper.GetParent(node);
        }
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
            Style = (Style)FindResource("Player.QualityMenuItem")
        };
        AutomationProperties.SetName(submenu, MusicQualityLabels.Name("spatial"));
        foreach (var choice in spatial) submenu.Items.Add(QualityItem(choice));
        QualityMenu.Items.Add(submenu);
    }
    private MenuItem QualityItem(MusicQualityChoice choice)
    {
        var item = new MenuItem { Header = choice.Label, Tag = choice.Key, IsChecked = choice.Current,
            Style = (Style)FindResource("Player.QualityMenuItem") };
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
        qualityRequest++;
        await module.DeactivateQualityAsync();
    }
    /// <summary>The "more" menu: playback rate (its own panel) and "wrong source".</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Top };
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
