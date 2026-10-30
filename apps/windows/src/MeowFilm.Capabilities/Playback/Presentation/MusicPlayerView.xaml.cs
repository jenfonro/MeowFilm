using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView : UserControl
{
    private readonly MusicCapability module;
    private string? scrubKey;
    public MusicPlayerView(MusicCapability module, bool showArtwork = true)
    {
        InitializeComponent();
        if (!showArtwork) { MetadataPanel.Children.Remove(ArtworkButton); ArtworkButton.Content = null; }
        this.module = module; DataContext = module;
        Progress.ScrubStarted += (_, _) => BeginProgress();
        Progress.ScrubCancelled += (_, _) => RestoreProgress();
        Progress.ScrubCommitted += async (_, _) =>
        {
            var key = scrubKey; var position = Progress.Value;
            RestoreProgress();
            if (key is not null && key == module.PlaybackPresentationKey) await module.SeekAsync(position);
        };
        Loaded += (_, _) => { module.PropertyChanged -= OnPlaybackChanged; module.PropertyChanged += OnPlaybackChanged; };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnPlaybackChanged;
            Deactivate();
        };
        SizeChanged += (_, _) => MetadataText.MaxWidth = Math.Min(200, Math.Max(30, ActualWidth / 2 - 260));
    }
    internal void Deactivate()
    {
        QualityPopup.IsOpen = false;
        PlaylistPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        RatePopup.IsOpen = false;
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
        QualityPopup.IsOpen = !QualityPopup.IsOpen;
        if (!QualityPopup.IsOpen) return;
        await module.ActivateQualitySettingsAsync();
        if (QualityPopup.IsOpen) await module.RefreshCurrentRecordingsAsync();
    }
    private async void PlaybackQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { SelectedItem: string value } ||
            value == module.PlaybackQualityRequest) return;
        await module.SetPlaybackQualityRequestAsync(value);
    }
    private async void Quality_Closed(object? sender, EventArgs e)
    { module.CloseCurrentRecordings(); await module.DeactivateQualityAsync(); }
    private async void Recording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicRecordingRow row })
            await module.SelectCurrentRecordingAsync(row);
    }
    private async void Recordings_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (QualityPopup.IsOpen && e.VerticalChange > 0 &&
            e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 24)
            await module.LoadCurrentRecordingsAsync();
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await module.TogglePlaybackAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await module.StepAsync(false);
    private async void Next_Click(object sender, RoutedEventArgs e) => await module.StepAsync(true);
    private async void Mode_Click(object sender, RoutedEventArgs e) => await module.ChangeModeAsync((module.Mode + 1) % 4);
    private async void Queue_Click(object sender, RoutedEventArgs e) => await module.OpenPlayQueueAsync();
    private void Rate_Click(object sender, RoutedEventArgs e) => RatePopup.IsOpen = !RatePopup.IsOpen;
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
