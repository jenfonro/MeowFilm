using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.Foundation;
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
    [ThreadStatic] private static SolidColorBrush? tabSelectedFg, tabMutedFg, tabHoverBg, tabSelectedBg, transparentBrush;
    private static SolidColorBrush TabSelectedFg => tabSelectedFg ??= DesktopVisual.Brush("#F3F1F4");
    private static SolidColorBrush TabMutedFg => tabMutedFg ??= DesktopVisual.Brush("#9B969B");
    private static SolidColorBrush TabHoverBg => tabHoverBg ??= DesktopVisual.Brush("#10FFFFFF");
    private static SolidColorBrush TabSelectedBg => tabSelectedBg ??= DesktopVisual.Brush("#18FFFFFF");
    private static SolidColorBrush TransparentBrush => transparentBrush ??= DesktopVisual.Brush("#00000000");

    public MusicPlaybackController Playback { get; }
    public MusicLyricsController Lyrics { get; }
    private readonly Action<bool>? onDetailVisibilityChanged;
    private readonly MusicPlayerView footer;
    private readonly DispatcherTimer lyricResumeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int centeredLyric = -1;
    private int centeringLyric = -1;
    private long lyricMotionEpoch;
    private bool centerRequest;
    private long centerEpoch;
    private Storyboard? needleMotion;
    private ScrollViewer? lyricScroll;
    private bool IsVisible => DesktopVisual.IsVisible(this);
    private IDisposable? visibility;
    private readonly List<IDisposable> themeObservers = [];
    private bool lyricsTabPointer, similarTabPointer;
    private FrameworkElement? centeredRow;
    private bool? needlePlaying;
    private double lyricViewportHeight;
    private double lastFirstLyricHeight = 47, lastLastLyricHeight = 47;
    private bool snappingLyric;
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
        WireDetailTab(LyricsTab, isLyrics: true);
        WireDetailTab(SimilarTab, isLyrics: false);
        DetailRecord.PaletteUpdated += (_, _) => Playback.RefreshPlayerAppearance(DetailRecord.PaletteBrush, animate: IsVisible);
        lyricResumeTimer.Tick += (_, _) =>
        {
            lyricResumeTimer.Stop();
            if (!IsVisible) return;
            Lyrics.CancelLyricPreview();
            CenterLyric();
        };
        KeyDown += Detail_KeyDown; KeyUp += Detail_KeyUp;
        LostFocus += (_, _) => playbackSpacePressed = false;
        LyricViewport.PointerWheelChanged += (_, e) =>
        {
            e.Handled = BrowseLyricWheel(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
        };
        LyricViewport.KeyDown += (_, e) =>
        {
            if (BrowseLyric(e.Key)) e.Handled = true;
        };
        LyricList.ContainerContentChanging += (_, _) => UpdateLyricViewport();
        LyricList.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width != e.PreviousSize.Width) InvalidateLyricCenter();
            UpdateLyricViewport();
        };
        Loaded += (_, _) =>
        {
            Lyrics.PropertyChanged -= OnLyricsChanged; Lyrics.PropertyChanged += OnLyricsChanged;
            Playback.PropertyChanged -= OnPlaybackChanged; Playback.PropertyChanged += OnPlaybackChanged;
            Lyrics.Lyrics.CollectionChanged -= OnLyricRowsChanged; Lyrics.Lyrics.CollectionChanged += OnLyricRowsChanged;
            if (lyricScroll is not null) { lyricScroll.ViewChanged -= OnLyricViewChanged; lyricScroll = null; }
            if (DesktopVisual.Descendant<ScrollViewer>(LyricList) is { } scroll)
            { lyricScroll = scroll; scroll.ViewChanged -= OnLyricViewChanged; scroll.ViewChanged += OnLyricViewChanged; }
            DisposeThemeObservers();
            themeObservers.Add(DesktopTheme.Observe(this, RefreshDetailPalette));
            themeObservers.Add(DesktopTheme.Observe(LyricsTab, () => RefreshDetailTab(LyricsTab, lyricsTabPointer)));
            themeObservers.Add(DesktopTheme.Observe(SimilarTab, () => RefreshDetailTab(SimilarTab, similarTabPointer)));
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                SyncBackgroundVisuals(visible);
                if (visible) { QueueLyricAlignment(); SyncNeedle(); }
                else
                {
                    StopLyricMotion();
                    playbackSpacePressed = false;
                    needleMotion?.Stop(); needlePlaying = null;
                    artworkRequest?.Cancel();
                    artworkPresentation?.Dispose(); artworkPresentation = null;
                }
            });
            SyncBackgroundVisuals(IsVisible);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            if (lyricScroll is not null) { lyricScroll.ViewChanged -= OnLyricViewChanged; lyricScroll = null; }
            Lyrics.PropertyChanged -= OnLyricsChanged;
            Playback.PropertyChanged -= OnPlaybackChanged;
            Lyrics.Lyrics.CollectionChanged -= OnLyricRowsChanged;
            DisposeThemeObservers();
            lyricsTabPointer = similarTabPointer = false;
            visibility?.Dispose(); visibility = null;
            SyncBackgroundVisuals(detailVisible: false);
            playbackSpacePressed = false;
            StopLyricMotion();
            needleMotion?.Stop(); needleMotion = null;
            needlePlaying = null;
            artworkRequest?.Cancel();
            artworkPresentation?.Dispose(); artworkPresentation = null;
        };
        RefreshTranslation();
    }

    private void WireDetailTab(RadioButton tab, bool isLyrics)
    {
        void SetPointer(bool value)
        {
            if (isLyrics) lyricsTabPointer = value; else similarTabPointer = value;
            RefreshDetailTab(tab, value);
        }
        tab.PointerEntered += (_, _) => SetPointer(true);
        tab.PointerExited += (_, _) => SetPointer(false);
        tab.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) =>
            RefreshDetailTab(tab, isLyrics ? lyricsTabPointer : similarTabPointer));
    }

    private void DisposeThemeObservers()
    {
        for (int i = 0; i < themeObservers.Count; i++) themeObservers[i].Dispose();
        themeObservers.Clear();
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
        ((SolidColorBrush)Resources["Player.DetailText"]).Color = contrast ? SystemAppearance.GetElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText) :
            global::Windows.UI.Color.FromArgb(255, 235, 234, 237);
        Foreground = (Brush)Resources["Player.DetailText"];
        DetailMetadata.Opacity = contrast ? 1 : .68;
        PageBackdrop.Background = contrast
            ? SystemAppearance.GetElementBrush(global::Windows.UI.ViewManagement.UIElementType.Window)
            : Playback.PlayerBackground;
    }

    private static void RefreshDetailTab(RadioButton tab, bool pointer)
    {
        bool selected = tab.IsChecked == true;
        if (DesktopTheme.GetIsHighContrast(tab))
        {
            tab.Foreground = SystemAppearance.GetElementBrush(selected || pointer ? global::Windows.UI.ViewManagement.UIElementType.HighlightText : global::Windows.UI.ViewManagement.UIElementType.WindowText);
            tab.Background = SystemAppearance.GetElementBrush(selected || pointer ? global::Windows.UI.ViewManagement.UIElementType.Highlight : global::Windows.UI.ViewManagement.UIElementType.Window);
        }
        else
        {
            tab.Foreground = selected ? TabSelectedFg : TabMutedFg;
            tab.Background = pointer ? TabHoverBg : selected ? TabSelectedBg : TransparentBrush;
        }
    }

    private ScrollViewer? GetLyricScroll()
    {
        if (lyricScroll is not null) return lyricScroll;
        if (DesktopVisual.Descendant<ScrollViewer>(LyricList) is not { } scroll) return null;
        lyricScroll = scroll;
        scroll.ViewChanged -= OnLyricViewChanged;
        scroll.ViewChanged += OnLyricViewChanged;
        return scroll;
    }

    internal bool BrowseLyric(VirtualKey key)
    {
        if (!IsVisible || GetLyricScroll() is not { } scroll) return false;
        if (key is VirtualKey.Home or VirtualKey.End)
        {
            if (Lyrics.Lyrics.Count == 0) return false;
            BrowseLyrics(scroll, 0, key == VirtualKey.Home ? 0 : Lyrics.Lyrics.Count - 1);
            return true;
        }
        double? distance = key switch
        {
            VirtualKey.Up => -48, VirtualKey.Down => 48,
            VirtualKey.PageUp => -scroll.ViewportHeight, VirtualKey.PageDown => scroll.ViewportHeight,
            _ => null
        };
        if (distance is null) return false;
        BrowseLyrics(scroll, distance.Value); return true;
    }

    internal bool BrowseLyricWheel(int delta)
    {
        if (!IsVisible || GetLyricScroll() is not { } scroll) return false;
        int lines = DesktopSystemSettings.WheelScrollLines;
        double distance = lines < 0 ? scroll.ViewportHeight : lines * 24d;
        BrowseLyrics(scroll, -delta / 120d * distance); return true;
    }

    private void RefreshTranslation()
    {
        string label = Lyrics.ShowLyricTranslation ? "隐藏译文歌词" : "显示译文歌词";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LyricTranslationButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(LyricTranslationButton, Lyrics.ShowLyricTranslation ? "已显示" : "已隐藏");
        LyricTranslationButton.Background = Lyrics.ShowLyricTranslation ? TabSelectedBg : TransparentBrush;
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
        // The routed handler consumes the key before ButtonBase can arm its release.
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

    private void OnLyricViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    { if (lyricResumeTimer.IsEnabled && sender is ScrollViewer scroll) UpdateLyricPreview(scroll); }

    internal Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        artworkRequest?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        artworkRequest = request;
        // Demand exists even while the shell's transform is outside the viewport.
        // Both owners start now; neither is an entrance/close gate.
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
        if (e.PropertyName == nameof(MusicLyricsController.LyricDisplayIndex) && IsVisible &&
            !Lyrics.HasLyricPreview && !lyricResumeTimer.IsEnabled &&
            Lyrics.LyricDisplayIndex != centeredLyric && Lyrics.LyricDisplayIndex != centeringLyric)
            QueueLyricAlignment();
        if (e.PropertyName == nameof(MusicLyricsController.HasLyricPreview) && !lyricResumeTimer.IsEnabled)
        {
            if (Lyrics.HasLyricPreview) InvalidateLyricCenter();
            else QueueLyricAlignment();
        }
        if (e.PropertyName == nameof(MusicLyricsController.ShowLyricTranslation))
        {
            RefreshTranslation();
            InvalidateLyricCenter();
            QueueLyricAlignment();
        }
    }

    private void OnLyricRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lastFirstLyricHeight = lastLastLyricHeight = 47;
        InvalidateLyricCenter();
        QueueLyricAlignment();
    }

    private void InvalidateLyricCenter()
    {
        UnwatchCenteredRow();
        lyricMotionEpoch++;
        snappingLyric = false;
        centeredLyric = centeringLyric = -1;
        if (GetLyricScroll() is { } scroll) MusicLyricScroll.Stop(scroll);
    }

    private void UnwatchCenteredRow()
    {
        if (centeredRow is not null) centeredRow.SizeChanged -= OnCenteredRowSizeChanged;
        centeredRow = null;
    }

    private void OnCenteredRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateLyricCenter();
        QueueLyricAlignment();
    }

    private void UpdateLyricViewport()
    {
        if (GetLyricScroll() is not { ViewportHeight: > 0 } scroll) return;
        bool changed = false;
        if (lyricViewportHeight != scroll.ViewportHeight)
        {
            InvalidateLyricCenter();
            lyricViewportHeight = scroll.ViewportHeight;
            changed = true;
        }
        if (LyricList.ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 } firstRow)
            lastFirstLyricHeight = firstRow.ActualHeight;
        if (Lyrics.Lyrics.Count > 0 && LyricList.ContainerFromIndex(Lyrics.Lyrics.Count - 1) is FrameworkElement { ActualHeight: > 0 } lastRow)
            lastLastLyricHeight = lastRow.ActualHeight;
        var leading = Math.Max(0, scroll.ViewportHeight / 2 - lastFirstLyricHeight / 2);
        var trailing = Math.Max(0, scroll.ViewportHeight / 2 - lastLastLyricHeight / 2);
        if (Math.Abs(LyricLeadingSpace.Height - leading) >= .5) { LyricLeadingSpace.Height = leading; changed = true; }
        if (Math.Abs(LyricTrailingSpace.Height - trailing) >= .5) { LyricTrailingSpace.Height = trailing; changed = true; }
        if (changed && (Lyrics.HasLyricPreview || !lyricResumeTimer.IsEnabled)) QueueLyricAlignment();
    }

    private void SyncNeedle()
    {
        if (needlePlaying == Playback.IsPlaying) return;
        needlePlaying = Playback.IsPlaying; double from = NeedleRotation.Angle;
        needleMotion?.Stop(); NeedleRotation.Angle = Playback.IsPlaying ? 25 : 0;
        if (IsVisible && DesktopVisual.CanAnimate(this))
            needleMotion = DesktopVisual.AnimateDouble(NeedleRotation, "Angle", from, NeedleRotation.Angle, TimeSpan.FromMilliseconds(450));
    }

    private void BrowseLyrics(ScrollViewer scroll, double distance, int? index = null)
    {
        // Accumulate wheel input against the unfinished manual destination.
        var target = MusicLyricScroll.ManualTarget(scroll) + distance;
        UnwatchCenteredRow();
        lyricMotionEpoch++;
        snappingLyric = false;
        centeredLyric = centeringLyric = -1;
        lyricResumeTimer.Stop();
        lyricResumeTimer.Start();
        if (index is { } rowIndex)
        {
            MusicLyricScroll.Stop(scroll);
            snappingLyric = true;
            Lyrics.PreviewLyric(Lyrics.Lyrics[rowIndex]);
            SnapLyricPreview(scroll, rowIndex);
            return;
        }
        var epoch = lyricMotionEpoch;
        MusicLyricScroll.Move(scroll, target, () =>
        {
            if (epoch == lyricMotionEpoch && IsVisible && lyricResumeTimer.IsEnabled)
                SnapLyricPreview(scroll);
        });
        UpdateLyricPreview(scroll);
    }

    private void UpdateLyricPreview(ScrollViewer scroll)
    {
        if (LyricList.ItemsPanelRoot is not { } panel) return;
        MusicLyricRow? nearest = null;
        var distance = double.PositiveInfinity;
        var middle = scroll.ViewportHeight / 2;
        foreach (FrameworkElement item in panel.Children)
        {
            int index = LyricList.IndexFromContainer(item);
            if (index < 0 || LyricList.Items[index] is not MusicLyricRow row) continue;
            var bounds = item.TransformToVisual(scroll).TransformPoint(new Point());
            var gap = Math.Abs(bounds.Y + item.ActualHeight / 2 - middle);
            if (gap >= distance) continue;
            distance = gap;
            nearest = row;
        }
        if (!snappingLyric && nearest is not null) Lyrics.PreviewLyric(nearest);
        UpdatePreviewFrame();
    }

    private void UpdatePreviewFrame()
    {
        if (!Lyrics.HasLyricPreview ||
            LyricList.ContainerFromIndex(Lyrics.LyricDisplayIndex) is not FrameworkElement row)
            return;
        LyricPreviewFrame.Height = row.ActualHeight;
    }

    private void QueueLyricAlignment()
    {
        if (centerRequest) return;
        centerRequest = true; var epoch = centerEpoch;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != centerEpoch) return;
            centerRequest = false;
            if (!IsLoaded || !IsVisible) return;
            if (Lyrics.HasLyricPreview)
            {
                if (GetLyricScroll() is { } scroll && !MusicLyricScroll.IsMoving(scroll))
                    SnapLyricPreview(scroll);
            }
            else if (!lyricResumeTimer.IsEnabled)
                CenterLyric();
        }))
            centerRequest = false;
    }

    private void SnapLyricPreview(ScrollViewer scroll, int? requestedIndex = null, int realizeAttempts = 0)
    {
        if (!Lyrics.HasLyricPreview) return;
        if (requestedIndex is null) UpdateLyricPreview(scroll);
        var index = requestedIndex ?? Lyrics.LyricDisplayIndex;
        if (index < 0 || index >= Lyrics.Lyrics.Count) { snappingLyric = false; return; }
        snappingLyric = true;
        if (LyricList.ContainerFromIndex(index) is not FrameworkElement row || row.ActualHeight <= 0)
        {
            if (LyricList.ContainerFromIndex(index) is null)
                LyricList.ScrollIntoView(Lyrics.Lyrics[index]);
            if (realizeAttempts < 3)
            {
                var retryEpoch = lyricMotionEpoch;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (retryEpoch == lyricMotionEpoch && IsLoaded && IsVisible && Lyrics.HasLyricPreview &&
                        GetLyricScroll() is { } currentScroll)
                        SnapLyricPreview(currentScroll, index, realizeAttempts + 1);
                });
                return;
            }
            snappingLyric = false;
            return;
        }
        UpdatePreviewFrame();
        var correction = row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y -
            scroll.ViewportHeight * .5;
        var epoch = ++lyricMotionEpoch;
        MusicLyricScroll.Move(scroll, scroll.VerticalOffset + correction, () =>
        {
            if (epoch != lyricMotionEpoch || !Lyrics.HasLyricPreview) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (epoch != lyricMotionEpoch || !IsLoaded || !IsVisible || !Lyrics.HasLyricPreview) return;
                if (LyricList.ContainerFromIndex(index) is FrameworkElement settledRow && settledRow.ActualHeight > 0)
                {
                    var gap = settledRow.TransformToVisual(scroll).TransformPoint(new Point(0, settledRow.ActualHeight / 2)).Y -
                        scroll.ViewportHeight * .5;
                    if (Math.Abs(gap) >= .5)
                        MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + gap);
                }
                snappingLyric = false;
                UpdatePreviewFrame();
                UnwatchCenteredRow();
                centeredRow = LyricList.ContainerFromIndex(index) as FrameworkElement;
                if (centeredRow is not null) centeredRow.SizeChanged += OnCenteredRowSizeChanged;
            });
        });
    }

    private void StopLyricMotion()
    {
        lyricResumeTimer.Stop();
        snappingLyric = false;
        Lyrics.CancelLyricPreview();
        centerEpoch++; centerRequest = false;
        InvalidateLyricCenter();
    }

    private void CenterLyric(int realizeAttempts = 0)
    {
        var index = Math.Max(0, Lyrics.LyricDisplayIndex);
        if (index < 0 || index >= Lyrics.Lyrics.Count ||
            index == centeringLyric || index == centeredLyric && centeringLyric < 0) return;
        if (GetLyricScroll() is not { } scroll) return;
        UnwatchCenteredRow();
        MusicLyricScroll.Stop(scroll);
        var row = LyricList.ContainerFromIndex(index) as FrameworkElement;
        if (row is null || row.ActualHeight <= 0)
        {
            if (row is null)
                LyricList.ScrollIntoView(Lyrics.Lyrics[index]);
            if (realizeAttempts < 3)
            {
                var retryEpoch = lyricMotionEpoch;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (retryEpoch == lyricMotionEpoch && IsLoaded && IsVisible &&
                        !lyricResumeTimer.IsEnabled && !Lyrics.HasLyricPreview)
                        CenterLyric(realizeAttempts + 1);
                });
            }
            return;
        }
        var absoluteCenter = scroll.VerticalOffset +
            row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y;
        var epoch = ++lyricMotionEpoch;
        var anchor = .5;
        centeringLyric = index;
        var target = absoluteCenter - scroll.ViewportHeight * anchor;
        if (centeredLyric < 0 && Lyrics.ActiveLyricIndex < 0)
        {
            MusicLyricScroll.Settle(scroll, target);
            SettleCenteredLyric(index, scroll, epoch, anchor);
        }
        else
            MusicLyricScroll.Move(scroll, target,
                settled: () => SettleCenteredLyric(index, scroll, epoch, anchor));
    }

    private void SettleCenteredLyric(int index, ScrollViewer scroll, long epoch, double anchor)
    {
        if (epoch != lyricMotionEpoch || !IsVisible || Lyrics.HasLyricPreview ||
            lyricResumeTimer.IsEnabled || Math.Max(0, Lyrics.LyricDisplayIndex) != index) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != lyricMotionEpoch || !IsLoaded || !IsVisible || Lyrics.HasLyricPreview ||
                lyricResumeTimer.IsEnabled || Math.Max(0, Lyrics.LyricDisplayIndex) != index) return;
            var row = LyricList.ContainerFromIndex(index) as FrameworkElement;
            if (row is null || row.ActualHeight <= 0)
            {
                if (centeringLyric == index) centeringLyric = -1;
                return;
            }
            var rowCenter = row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y;
            var correction = rowCenter - scroll.ViewportHeight * anchor;
            if (Math.Abs(correction) >= .5)
                MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + correction);
            centeredLyric = index;
            centeringLyric = -1;
            UnwatchCenteredRow();
            centeredRow = row;
            centeredRow.SizeChanged += OnCenteredRowSizeChanged;
        });
    }

    private async void PlayLyric_Click(object sender, RoutedEventArgs e)
    {
        if (!Lyrics.CanConfirmLyricPreview) return;
        if (GetLyricScroll() is { } scroll) MusicLyricScroll.Stop(scroll);
        lyricResumeTimer.Stop();
        await Lyrics.ConfirmLyricPreviewAsync();
        QueueLyricAlignment();
    }

    private async void DetailRecord_SwipeCommitted(object? sender, MusicRecordSwipeEventArgs e)
    {
        try { await Playback.StepFromPresentationAsync(e.Direction > 0, e.Identity); }
        finally { DetailRecord.ResolveSwipe(e.Identity); }
    }
}
