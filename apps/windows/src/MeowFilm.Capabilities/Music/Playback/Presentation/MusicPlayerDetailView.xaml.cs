using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerDetailView : UserControl
{
    public MusicPlaybackController Playback { get; }
    public MusicLyricsController Lyrics { get; }
    private readonly Action<bool>? onDetailVisibilityChanged;
    private readonly MusicPlayerView footer;
    private readonly DispatcherTimer lyricResumeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool alignQueued;
    private Storyboard? needleMotion;
    private bool IsVisible => DesktopVisual.IsVisible(this);
    private IDisposable? visibility;
    private IDisposable? themeObserver;
    private bool observingVisible;
    private bool? needlePlaying;
    private string lyricPresentationKey;
    private bool playbackSpacePressed;
    private CancellationTokenSource? artworkRequest;
    private IDisposable? artworkPresentation;

    public MusicPlayerDetailView(
        MusicPlaybackController playback,
        MusicLyricsController lyrics,
        MusicQueueController queue,
        MusicLibraryController library,
        MusicSettingsController settings,
        MusicCatalogController catalog,
        Action<bool>? onDetailVisibilityChanged = null)
    {
        Playback = playback;
        Lyrics = lyrics;
        this.onDetailVisibilityChanged = onDetailVisibilityChanged;
        lyricPresentationKey = playback.PlaybackPresentationKey;
        InitializeComponent();
        ArtworkScope.SetLoader(DetailRecord, playback.ArtworkLoader);
        footer = new MusicPlayerView(playback, queue, library, lyrics, settings, catalog, showArtwork: false);
        foreach (var resource in (ResourceDictionary)Resources["Player.FooterPalette"])
            footer.Resources[resource.Key] = resource.Value;
        footer.SetBackgroundVisualsPaused(true);
        DetailPlayerHost.Content = footer;
        DetailRecord.PaletteUpdated += (_, _) => Playback.RefreshPlayerAppearance(DetailRecord.PaletteBrush, animate: IsVisible);
        lyricResumeTimer.Tick += (_, _) =>
        {
            lyricResumeTimer.Stop();
            if (!IsVisible) return;
            Lyrics.CancelLyricPreview();
            AlignActiveLyric();
        };
        LyricList.BrowseInterrupted += () =>
        {
            lyricResumeTimer.Stop();
            lyricResumeTimer.Start();
        };
        LyricList.BrowsePreviewChanged += (row, _) =>
        {
            if (row is not null) Lyrics.PreviewLyric(row);
            UpdatePreviewFrame();
        };
        LyricList.SizeChanged += (_, _) => QueueLyricAlignment();
        KeyDown += Detail_KeyDown; KeyUp += Detail_KeyUp;
        LostFocus += (_, _) => playbackSpacePressed = false;
        Loaded += (_, _) =>
        {
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, SetVisible);
            SyncBackgroundVisuals(IsVisible);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose(); visibility = null;
            SetVisible(false);
            needleMotion = null;
        };
        RefreshTranslation();
    }

    private void SetVisible(bool visible)
    {
        SyncBackgroundVisuals(visible);
        if (visible)
        {
            if (!observingVisible)
            {
                observingVisible = true;
                Lyrics.PropertyChanged += OnLyricsChanged;
                Playback.PropertyChanged += OnPlaybackChanged;
                Lyrics.Lyrics.CollectionChanged += OnLyricRowsChanged;
                themeObserver?.Dispose();
                themeObserver = DesktopTheme.Observe(this, RefreshDetailPalette);
            }
            if (lyricPresentationKey != Playback.PlaybackPresentationKey)
            {
                lyricPresentationKey = Playback.PlaybackPresentationKey;
                StopLyricMotion();
            }
            RefreshTranslation();
            QueueLyricAlignment();
            SyncNeedle();
            return;
        }
        if (observingVisible)
        {
            observingVisible = false;
            Lyrics.PropertyChanged -= OnLyricsChanged;
            Playback.PropertyChanged -= OnPlaybackChanged;
            Lyrics.Lyrics.CollectionChanged -= OnLyricRowsChanged;
            themeObserver?.Dispose(); themeObserver = null;
        }
        StopLyricMotion();
        playbackSpacePressed = false;
        needleMotion?.Stop(); needlePlaying = null;
        artworkRequest?.Cancel();
        artworkPresentation?.Dispose(); artworkPresentation = null;
    }

    private void SyncBackgroundVisuals(bool detailVisible)
    {
        footer.SetBackgroundVisualsPaused(!detailVisible);
        onDetailVisibilityChanged?.Invoke(detailVisible);
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlaybackController.PlayerBackground) && !DesktopTheme.GetIsHighContrast(this))
            PageBackdrop.Background = Playback.PlayerBackground;
        if (e.PropertyName == nameof(MusicPlaybackController.PlaybackPresentationKey) &&
            lyricPresentationKey != Playback.PlaybackPresentationKey)
        {
            lyricPresentationKey = Playback.PlaybackPresentationKey;
            StopLyricMotion();
            QueueLyricAlignment();
        }
        if (e.PropertyName == nameof(MusicPlaybackController.IsPlaying) && IsVisible)
            SyncNeedle();
    }

    private void RefreshDetailPalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        Foreground = contrast
            ? SystemAppearance.GetElementBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText)
            : (Brush)DesktopVisual.Resource(this, "Player.DetailText");
        DetailMetadata.Opacity = contrast ? 1 : .68;
        PageBackdrop.Background = contrast
            ? SystemAppearance.GetElementBrush(global::Windows.UI.ViewManagement.UIElementType.Window)
            : Playback.PlayerBackground;
    }

    internal bool BrowseLyric(VirtualKey key) => LyricList.BrowseKey(key);
    internal bool BrowseLyricWheel(int delta) => LyricList.BrowseWheel(delta);

    private void RefreshTranslation()
    {
        string label = Lyrics.ShowLyricTranslation ? "隐藏译文歌词" : "显示译文歌词";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LyricTranslationButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(LyricTranslationButton, Lyrics.ShowLyricTranslation ? "已显示" : "已隐藏");
        LyricTranslationButton.Background = Lyrics.ShowLyricTranslation ? (Brush)Resources["Player.TranslationActiveBg"] : DesktopVisual.TransparentBrush;
    }

    private async void Detail_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space) return;
        var command = HandlePlaybackSpace(e.OriginalSource, true, e.KeyStatus.WasKeyDown, DesktopVisual.Modifiers);
        e.Handled |= command.Handled;
        await command.Work;
    }

    private void DelayLyric_Click(object sender, RoutedEventArgs e) => Lyrics.AdjustLyricOffset(.5);
    private void AdvanceLyric_Click(object sender, RoutedEventArgs e) => Lyrics.AdjustLyricOffset(-.5);
    private async void Translation_Click(object sender, RoutedEventArgs e) =>
        await Playback.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);

    private void Detail_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space) e.Handled |= HandlePlaybackSpace(e.OriginalSource, false).Handled;
    }

    internal (bool Handled, Task Work) HandlePlaybackSpace(object originalSource, bool down,
        bool repeat = false, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
    {
        if (!down)
        {
            if (!playbackSpacePressed) return (false, Task.CompletedTask);
            playbackSpacePressed = false;
            return (IsVisible && Lyrics.IsLyrics && IsPlaybackKeySource(originalSource), Task.CompletedTask);
        }
        if (!IsVisible || !Lyrics.IsLyrics || !IsPlaybackKeySource(originalSource) ||
            modifiers != VirtualKeyModifiers.None) return (false, Task.CompletedTask);
        if (playbackSpacePressed) return (true, Task.CompletedTask);
        if (!Playback.CanTogglePlayback) return (false, Task.CompletedTask);
        playbackSpacePressed = true;
        return (true, repeat ? Task.CompletedTask : Playback.TogglePlaybackAsync());
    }

    private bool IsPlaybackKeySource(object originalSource)
    {
        for (var node = originalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or RichEditBox or PasswordBox or ComboBox or MenuFlyoutPresenter or Popup or Slider) return false;
            if (ReferenceEquals(node, this)) return true;
        }
        return false;
    }

    internal Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        artworkRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        artworkRequest = request;
        var artwork = PrepareArtwork();
        var lyrics = Lyrics.RestoreLyricsAsync().WaitAsync(cancellationToken);
        return Task.WhenAll(artwork, lyrics);

        async Task PrepareArtwork()
        {
            try
            {
                var prepared = await DetailRecord.PrepareArtworkAsync(request.Token);
                if (request.IsCancellationRequested || !ReferenceEquals(artworkRequest, request))
                { prepared.Dispose(); return; }
                artworkPresentation?.Dispose(); artworkPresentation = prepared;
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(artworkRequest, request)) artworkRequest = null;
                request.Dispose();
            }
        }
    }

    internal void Deactivate()
    {
        playbackSpacePressed = false;
        artworkRequest?.Cancel();
        if (!IsVisible) { artworkPresentation?.Dispose(); artworkPresentation = null; }
        StopLyricMotion();
        footer.Deactivate();
        SyncBackgroundVisuals(detailVisible: false);
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicLyricsController.LyricDisplayIndex) or nameof(MusicLyricsController.HasLyricPreview) &&
            IsVisible && !lyricResumeTimer.IsEnabled)
        {
            if (Lyrics.HasLyricPreview) LyricList.InvalidateAlignment();
            QueueLyricAlignment();
        }
        if (e.PropertyName == nameof(MusicLyricsController.ShowLyricTranslation))
        {
            RefreshTranslation();
            LyricList.InvalidateAlignment();
            QueueLyricAlignment();
        }
    }

    private void OnLyricRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LyricList.ResetRows();
        QueueLyricAlignment();
    }

    private void SyncNeedle()
    {
        if (needlePlaying == Playback.IsPlaying) return;
        needlePlaying = Playback.IsPlaying; double from = NeedleRotation.Angle;
        needleMotion?.Stop(); NeedleRotation.Angle = Playback.IsPlaying ? 25 : 0;
        if (IsVisible && DesktopVisual.CanAnimate(this))
            needleMotion = DesktopVisual.AnimateDouble(NeedleRotation, "Angle", from, NeedleRotation.Angle, TimeSpan.FromMilliseconds(450));
    }

    private void UpdatePreviewFrame()
    {
        if (!Lyrics.HasLyricPreview) return;
        LyricPreviewFrame.Height = LyricList.RowHeight(Lyrics.LyricDisplayIndex);
    }

    private void QueueLyricAlignment()
    {
        if (alignQueued) return;
        alignQueued = DispatcherQueue.TryEnqueue(() =>
        {
            alignQueued = false;
            if (!IsLoaded || !IsVisible) return;
            if (Lyrics.HasLyricPreview)
            {
                UpdatePreviewFrame();
                if (!lyricResumeTimer.IsEnabled)
                    LyricList.AlignIndex(Lyrics.LyricDisplayIndex, animate: true, preview: true);
            }
            else if (!lyricResumeTimer.IsEnabled)
                AlignActiveLyric();
        });
    }

    private void AlignActiveLyric()
    {
        int index = Math.Max(0, Lyrics.LyricDisplayIndex);
        if (index >= Lyrics.Lyrics.Count) return;
        LyricList.AlignIndex(index, animate: Lyrics.ActiveLyricIndex >= 0);
    }

    private void StopLyricMotion()
    {
        lyricResumeTimer.Stop();
        Lyrics.CancelLyricPreview();
        alignQueued = false;
        LyricList.StopMotion();
    }

    private async void PlayLyric_Click(object sender, RoutedEventArgs e)
    {
        if (!Lyrics.CanConfirmLyricPreview) return;
        lyricResumeTimer.Stop();
        LyricList.StopMotion();
        await Lyrics.ConfirmLyricPreviewAsync();
        QueueLyricAlignment();
    }

    private async void DetailRecord_SwipeCommitted(object? sender, MusicRecordSwipeEventArgs e)
    {
        try { await Playback.StepFromPresentationAsync(e.Direction > 0, e.Identity); }
        finally { DetailRecord.ResolveSwipe(e.Identity); }
    }
}
