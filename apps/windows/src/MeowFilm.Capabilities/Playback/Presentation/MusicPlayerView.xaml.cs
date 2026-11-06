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
        SpatialPopup.IsOpen = false;
        QualityPopup.IsOpen = false;
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
        if (QualityPopup.IsOpen) { QualityPopup.IsOpen = false; return; }
        await module.ActivateQualitySettingsAsync();
        ShowQualityMenu();
        QualityPopup.IsOpen = true;
    }
    /// <summary>Quality choices of the current playback; spatial levels form the right-hand submenu.</summary>
    private void ShowQualityMenu()
    {
        SourcePanel.Visibility = Visibility.Collapsed;
        QualityMenu.Visibility = Visibility.Visible;
        Fill(QualityItems, module.QualityChoices());
        var spatial = module.SpatialChoices();
        Fill(SpatialItems, spatial);
        SpatialButton.Visibility = spatial.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpatialButton.Tag = spatial.Any(choice => choice.Current) ? "current" : null;
    }
    private void Fill(Panel panel, IReadOnlyList<MusicQualityChoice> choices)
    {
        panel.Children.Clear();
        foreach (var choice in choices)
        {
            var item = new Button
            {
                Content = choice.Current ? choice.Label + "  ✓" : choice.Label, Tag = choice.Key,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(10, 7, 10, 7), FontSize = 13,
                FontFamily = SystemFonts.MessageFontFamily, IsEnabled = module.CanSwitchSource
            };
            AutomationProperties.SetName(item, choice.Label);
            item.Click += QualityChoice_Click;
            panel.Children.Add(item);
        }
    }
    private async void QualityChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        SpatialPopup.IsOpen = false;
        QualityPopup.IsOpen = false;
        await module.SwitchQualityAsync(key);
    }
    private void Spatial_Click(object sender, RoutedEventArgs e) => OpenSpatial(focus: true);
    private void Spatial_MouseEnter(object sender, MouseEventArgs e) => OpenSpatial(focus: false);
    private void Spatial_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Right) return;
        e.Handled = true;
        OpenSpatial(focus: true);
    }
    private void OpenSpatial(bool focus)
    {
        if (SpatialItems.Children.Count == 0) return;
        SpatialPopup.IsOpen = true;
        if (focus && SpatialItems.Children[0] is Button first)
            Dispatcher.BeginInvoke(() => first.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
    private void SpatialItems_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Escape)) return;
        e.Handled = true;
        SpatialPopup.IsOpen = false;
        SpatialButton.Focus();
    }
    private void SpatialPopup_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!SpatialButton.IsMouseOver) SpatialPopup.IsOpen = false;
    }
    private void SourceList_Click(object sender, RoutedEventArgs e)
    {
        SpatialPopup.IsOpen = false;
        module.OpenSourceChoices();
        QualityMenu.Visibility = Visibility.Collapsed;
        SourcePanel.Visibility = Visibility.Visible;
    }
    private void SourceBack_Click(object sender, RoutedEventArgs e) => ShowQualityMenu();
    private async void LoadMoreSources_Click(object sender, RoutedEventArgs e) => await module.LoadMoreSourcesAsync();
    private async void SourceChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicSourceChoice choice }) return;
        QualityPopup.IsOpen = false;
        await module.SelectSourceChoiceAsync(choice);
    }
    private async void Quality_Closed(object? sender, EventArgs e)
    {
        SpatialPopup.IsOpen = false;
        module.CloseSourceChoices();
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
