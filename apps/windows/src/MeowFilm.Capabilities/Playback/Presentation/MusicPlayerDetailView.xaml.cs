using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerDetailView : UserControl
{
    private readonly MusicCapability module;
    private readonly DispatcherTimer lyricResumeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int centeredLyric = -1;
    private int centeringLyric = -1;
    private long lyricMotionEpoch;
    private DispatcherOperation? centerRequest;
    private FrameworkElement? centeredRow;
    private bool realizingLyric;
    private bool? needlePlaying;
    private double lyricViewportHeight;
    private bool snappingLyric;
    private string lyricPresentationKey;
    private bool playbackSpacePressed;
    private CancellationTokenSource? artworkRequest;
    private IDisposable? artworkPresentation;

    public MusicPlayerDetailView(MusicCapability module)
    {
        this.module = module;
        lyricPresentationKey = module.PlaybackPresentationKey;
        InitializeComponent();
        DataContext = module;
        module.AttachPlaybackArtwork(DetailRecord);
        var footer = new MusicPlayerView(module, showArtwork: false);
        // The footer's own theme supplies the ordinary-page defaults; this page's backdrop palette wins.
        footer.Resources.MergedDictionaries.Add((ResourceDictionary)Resources["Player.FooterPalette"]);
        DetailPlayerHost.Content = footer;
        DetailRecord.ArtworkLoadCompleted += (_, e) => module.ArtworkLoadCompleted(e.Address, e.Succeeded);
        DetailRecord.PaletteUpdated += (_, _) => module.RefreshPlayerAppearance(DetailRecord.PaletteBrush, animate: IsVisible);
        lyricResumeTimer.Tick += (_, _) =>
        {
            lyricResumeTimer.Stop();
            if (!IsVisible) return;
            module.CancelLyricPreview();
            CenterLyric();
        };
        LyricList.RequestBringIntoView += (_, e) =>
        {
            if (!realizingLyric) e.Handled = true;
        };
        LyricViewport.PreviewMouseWheel += (_, e) =>
        {
            if (Descendant<ScrollViewer>(LyricList) is not { } scroll) return;
            var distance = SystemParameters.WheelScrollLines < 0
                ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 24d;
            BrowseLyrics(scroll, -e.Delta / 120d * distance);
            e.Handled = true;
        };
        LyricViewport.PreviewKeyDown += (_, e) =>
        {
            if (Descendant<ScrollViewer>(LyricList) is not { } scroll) return;
            double? distance = e.Key switch
            {
                Key.Up => -48, Key.Down => 48,
                Key.PageUp => -scroll.ViewportHeight, Key.PageDown => scroll.ViewportHeight,
                Key.Home => -scroll.ScrollableHeight, Key.End => scroll.ScrollableHeight,
                _ => null
            };
            if (distance is null) return;
            BrowseLyrics(scroll, distance.Value);
            e.Handled = true;
        };
        LyricList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (lyricResumeTimer.IsEnabled && e.OriginalSource is ScrollViewer scroll)
                UpdateLyricPreview(scroll);
        }));
        LyricList.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) InvalidateLyricCenter();
            UpdateLyricViewport();
        };
        LyricFadeViewport.SizeChanged += (_, _) =>
            ((LinearGradientBrush)LyricFadeViewport.OpacityMask).EndPoint = new(0, LyricFadeViewport.ActualHeight);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { QueueCenterLyric(); SyncNeedle(); }
            else
            {
                playbackSpacePressed = false;
                centeredLyric = centeringLyric = -1;
                StopLyricMotion();
                NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        };
        Loaded += (_, _) =>
        {
            if (IsVisible) { QueueCenterLyric(); SyncNeedle(); }
        };
        Unloaded += (_, _) =>
        {
            playbackSpacePressed = false;
            StopLyricMotion();
            NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            needlePlaying = null;
            artworkRequest?.Cancel();
            artworkPresentation?.Dispose(); artworkPresentation = null;
        };
        PropertyChangedEventManager.AddHandler(module, OnModuleChanged, "");
        CollectionChangedEventManager.AddHandler(module.Lyrics, OnLyricRowsChanged);
    }

    private async void Detail_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !IsVisible || !module.IsLyrics || !IsPlaybackKeySource(e.OriginalSource)) return;
        if (playbackSpacePressed) { e.Handled = true; return; }
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None ||
            e.KeyboardDevice.IsKeyDown(Key.LWin) || e.KeyboardDevice.IsKeyDown(Key.RWin) ||
            !module.CanTogglePlayback) return;
        playbackSpacePressed = true;
        // Consume before ButtonBase can arm its Space KeyUp click.
        e.Handled = true;
        if (!e.IsRepeat) await module.TogglePlaybackAsync();
    }

    private void DelayLyric_Click(object sender, RoutedEventArgs e) => module.AdjustLyricOffset(.5);
    private void AdvanceLyric_Click(object sender, RoutedEventArgs e) => module.AdjustLyricOffset(-.5);

    private void Detail_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !playbackSpacePressed) return;
        playbackSpacePressed = false;
        if (IsVisible && module.IsLyrics && IsPlaybackKeySource(e.OriginalSource)) e.Handled = true;
    }

    private void Detail_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsKeyboardFocusWithin) playbackSpacePressed = false;
    }

    private bool IsPlaybackKeySource(object originalSource)
    {
        for (var node = originalSource as DependencyObject; node is not null;
            node = node is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(node))
        {
            if (node is MeowFilm.Desktop.PlayerDialogHost or TextBoxBase or PasswordBox or ComboBox { IsEditable: true } or MenuBase or Popup) return false;
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
        // Demand exists even while the shell's transform is outside the viewport.
        // Both owners start now; neither is an entrance/close gate.
        var artwork = PrepareArtwork();
        var lyrics = module.RestoreLyricsAsync().WaitAsync(cancellationToken);
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
        ((MusicPlayerView)DetailPlayerHost.Content).Deactivate();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCapability.PlaybackPresentationKey) &&
            lyricPresentationKey != module.PlaybackPresentationKey)
        {
            lyricPresentationKey = module.PlaybackPresentationKey;
            StopLyricMotion();
            QueueCenterLyric();
        }
        if (e.PropertyName == nameof(MusicCapability.IsPlaying))
        {
            if (IsVisible) SyncNeedle();
        }
        if (e.PropertyName == nameof(MusicCapability.LyricDisplayIndex) && IsVisible &&
            !module.HasLyricPreview && !lyricResumeTimer.IsEnabled &&
            module.LyricDisplayIndex != centeredLyric && module.LyricDisplayIndex != centeringLyric)
            QueueCenterLyric();
        if (e.PropertyName == nameof(MusicCapability.HasLyricPreview) && !lyricResumeTimer.IsEnabled)
        {
            if (module.HasLyricPreview) InvalidateLyricCenter();
            else QueueCenterLyric();
        }
        if (e.PropertyName == nameof(MusicCapability.ShowLyricTranslation))
        {
            InvalidateLyricCenter();
            if (module.HasLyricPreview) QueuePreviewSnap();
            else QueueCenterLyric();
        }
    }

    private void OnLyricRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateLyricCenter();
        QueueCenterLyric();
    }

    private void InvalidateLyricCenter()
    {
        UnwatchCenteredRow();
        lyricMotionEpoch++;
        snappingLyric = false;
        centeredLyric = centeringLyric = -1;
        if (Descendant<ScrollViewer>(LyricList) is { } scroll) MusicLyricScroll.Stop(scroll);
    }

    private void UnwatchCenteredRow()
    {
        if (centeredRow is not null) centeredRow.SizeChanged -= OnCenteredRowSizeChanged;
        centeredRow = null;
    }

    private void OnCenteredRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateLyricCenter();
        if (module.HasLyricPreview) QueuePreviewSnap();
        else QueueCenterLyric();
    }

    private void UpdateLyricViewport()
    {
        if (Descendant<ScrollViewer>(LyricList) is not { ViewportHeight: > 0 } scroll) return;
        if (lyricViewportHeight != scroll.ViewportHeight)
        {
            InvalidateLyricCenter();
            lyricViewportHeight = scroll.ViewportHeight;
        }
        if (module.HasLyricPreview) QueuePreviewSnap();
        else if (!lyricResumeTimer.IsEnabled) QueueCenterLyric();
    }

    private void SyncNeedle()
    {
        if (needlePlaying == module.IsPlaying) return;
        needlePlaying = module.IsPlaying;
        var from = NeedleRotation.Angle;
        NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        NeedleRotation.Angle = module.IsPlaying ? 25 : 0;
        if (IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            NeedleRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(from, NeedleRotation.Angle, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
    }

    private void BrowseLyrics(ScrollViewer scroll, double distance)
    {
        // Accumulate wheel input against the unfinished manual destination.
        var target = MusicLyricScroll.ManualTarget(scroll) + distance;
        UnwatchCenteredRow();
        lyricMotionEpoch++;
        snappingLyric = false;
        centeredLyric = centeringLyric = -1;
        lyricResumeTimer.Stop();
        lyricResumeTimer.Start();
        var epoch = lyricMotionEpoch;
        MusicLyricScroll.MoveManual(scroll, target, () =>
        {
            if (epoch == lyricMotionEpoch && IsVisible && lyricResumeTimer.IsEnabled)
                SnapLyricPreview(scroll);
        });
        UpdateLyricPreview(scroll);
    }

    private void UpdateLyricPreview(ScrollViewer scroll)
    {
        if (Descendant<VirtualizingStackPanel>(LyricList) is not { } panel) return;
        MusicLyricRow? nearest = null;
        var distance = double.PositiveInfinity;
        var middle = scroll.ViewportHeight / 2;
        foreach (FrameworkElement item in panel.Children)
        {
            if (item.DataContext is not MusicLyricRow row ||
                LyricList.ItemContainerGenerator.IndexFromContainer(item) < 0) continue;
            var bounds = item.TranslatePoint(new Point(), scroll);
            var gap = Math.Abs(bounds.Y + item.ActualHeight / 2 - middle);
            if (gap >= distance) continue;
            distance = gap;
            nearest = row;
        }
        if (!snappingLyric && nearest is not null) module.PreviewLyric(nearest);
        UpdatePreviewFrame();
    }

    private void UpdatePreviewFrame()
    {
        if (!module.HasLyricPreview ||
            LyricList.ItemContainerGenerator.ContainerFromIndex(module.LyricDisplayIndex) is not FrameworkElement row)
            return;
        LyricPreviewFrame.Height = row.ActualHeight;
    }

    private void QueuePreviewSnap()
    {
        if (centerRequest is not null) return;
        centerRequest = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            centerRequest = null;
            if (IsLoaded && IsVisible && module.HasLyricPreview &&
                Descendant<ScrollViewer>(LyricList) is { } scroll) SnapLyricPreview(scroll);
        }));
    }

    private void SnapLyricPreview(ScrollViewer scroll)
    {
        if (!module.HasLyricPreview) return;
        UpdateLyricPreview(scroll);
        var index = module.LyricDisplayIndex;
        if (LyricList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row) return;
        UpdatePreviewFrame();
        var correction = row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y -
            scroll.ViewportHeight * MusicLyricRowMarginConverter.ReadingAnchor;
        var epoch = ++lyricMotionEpoch;
        snappingLyric = true;
        MusicLyricScroll.MoveManual(scroll, scroll.VerticalOffset + correction, () =>
        {
            if (epoch != lyricMotionEpoch || !module.HasLyricPreview) return;
            // Virtualized row reflow can adjust the offset after the original target.
            LyricList.UpdateLayout();
            if (LyricList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement settledRow)
            {
                var gap = settledRow.TranslatePoint(new Point(0, settledRow.ActualHeight / 2), scroll).Y -
                    scroll.ViewportHeight * MusicLyricRowMarginConverter.ReadingAnchor;
                if (Math.Abs(gap) >= .5)
                {
                    MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + gap);
                    scroll.UpdateLayout();
                }
            }
            snappingLyric = false;
            UpdatePreviewFrame();
            UnwatchCenteredRow();
            centeredRow = LyricList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
            if (centeredRow is not null) centeredRow.SizeChanged += OnCenteredRowSizeChanged;
        });
    }

    private void StopLyricMotion()
    {
        lyricResumeTimer.Stop();
        snappingLyric = false;
        module.CancelLyricPreview();
        centerRequest?.Abort(); centerRequest = null;
        InvalidateLyricCenter();
    }

    private void QueueCenterLyric()
    {
        if (centerRequest is not null) return;
        centerRequest = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            centerRequest = null;
            if (IsLoaded && IsVisible && !lyricResumeTimer.IsEnabled && !module.HasLyricPreview)
                CenterLyric();
        }));
    }

    private void CenterLyric()
    {
        var index = Math.Max(0, module.LyricDisplayIndex);
        if (index < 0 || index >= module.Lyrics.Count ||
            index == centeringLyric || index == centeredLyric && centeringLyric < 0) return;
        if (Descendant<ScrollViewer>(LyricList) is not { } scroll) return;
        UnwatchCenteredRow();
        MusicLyricScroll.Stop(scroll);
        scroll.UpdateLayout();
        var row = RealizeLyric(index);
        if (row is null) return;
        var absoluteCenter = scroll.VerticalOffset +
            row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y;
        var epoch = ++lyricMotionEpoch;
        var anchor = MusicLyricRowMarginConverter.ReadingAnchor;
        var presenter = Descendant<ItemsPresenter>(LyricList)!;
        // Template Freezables are shared/frozen; the retained presenter owns its clock.
        if (presenter.RenderTransform is not TranslateTransform translation)
        { translation = new(); presenter.RenderTransform = translation; }
        centeringLyric = index;
        var target = absoluteCenter - scroll.ViewportHeight * anchor;
        if (centeredLyric < 0 && module.ActiveLyricIndex < 0)
        {
            MusicLyricScroll.Settle(scroll, target);
            SettleCenteredLyric(index, scroll, epoch, anchor);
        }
        else
            MusicLyricScroll.Move(scroll, target,
                settled: () => SettleCenteredLyric(index, scroll, epoch, anchor),
                presentation: translation, list: LyricList);
    }

    private void SettleCenteredLyric(int index, ScrollViewer scroll, long epoch, double anchor)
    {
        if (epoch != lyricMotionEpoch || !IsVisible || module.HasLyricPreview ||
            lyricResumeTimer.IsEnabled || Math.Max(0, module.LyricDisplayIndex) != index) return;
        LyricList.UpdateLayout();
        var row = LyricList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (row is null)
        {
            if (centeringLyric == index) centeringLyric = -1;
            return;
        }
        var rowCenter = row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y;
        var correction = rowCenter - scroll.ViewportHeight * anchor;
        if (Math.Abs(correction) >= .5)
        {
            MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + correction);
            scroll.UpdateLayout();
        }
        centeredLyric = index;
        centeringLyric = -1;
        centeredRow = row;
        centeredRow.SizeChanged += OnCenteredRowSizeChanged;
    }

    private FrameworkElement? RealizeLyric(int index)
    {
        if (LyricList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row) return row;
        realizingLyric = true;
        try
        {
            LyricList.ScrollIntoView(module.Lyrics[index]);
            LyricList.UpdateLayout();
            return LyricList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        }
        finally { realizingLyric = false; }
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private async void Heartbeat_Click(object sender, RoutedEventArgs e) =>
        await module.HeartbeatCurrentAsync(module.PlaybackPresentationKey);
    private async void PlayLyric_Click(object sender, RoutedEventArgs e)
    {
        if (!module.CanConfirmLyricPreview) return;
        if (Descendant<ScrollViewer>(LyricList) is { } scroll) MusicLyricScroll.Stop(scroll);
        lyricResumeTimer.Stop();
        await module.ConfirmLyricPreviewAsync();
        QueueCenterLyric();
    }
    private async void DetailRecord_SwipeCommitted(object? sender, MusicRecordSwipeEventArgs e)
    {
        try { await module.StepFromPresentationAsync(e.Direction > 0, e.Identity); }
        finally { DetailRecord.ResolveSwipe(e.Identity); }
    }
}
