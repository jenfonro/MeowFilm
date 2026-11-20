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
using Microsoft.UI.Dispatching;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerDetailView : UserControl
{
    private readonly MusicCapability module;
    private readonly DispatcherTimer lyricResumeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int centeredLyric = -1;
    private int centeringLyric = -1;
    private long lyricMotionEpoch;
    private bool centerRequest;
    private long centerEpoch;
    private Storyboard? needleMotion;
    private bool IsVisible => DesktopVisual.IsVisible(this);
    private IDisposable? visibility;
    private FrameworkElement? centeredRow;
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
        ArtworkScope.SetLoader(DetailRecord, module.ArtworkLoader);
        DetailRecord.SetBinding(MusicRecordView.ArtworkIdentityProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(MusicCapability.CurrentArtworkIdentity)) });
        var footer = new MusicPlayerView(module, showArtwork: false);
        foreach (var resource in (ResourceDictionary)Resources["Player.FooterPalette"])
            footer.Resources[resource.Key] = resource.Value;
        DetailPlayerHost.Content = footer;
        DesktopTheme.Observe(this, RefreshDetailPalette);
        foreach (var tab in new[] { LyricsTab, SimilarTab })
        {
            bool pointer = false;
            void Refresh() => RefreshDetailTab(tab, pointer);
            tab.PointerEntered += (_, _) => { pointer = true; Refresh(); };
            tab.PointerExited += (_, _) => { pointer = false; Refresh(); };
            tab.Unloaded += (_, _) => pointer = false;
            tab.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => Refresh());
            DesktopTheme.Observe(tab, Refresh);
        }
        DetailRecord.PaletteUpdated += (_, _) => module.RefreshPlayerAppearance(DetailRecord.PaletteBrush, animate: IsVisible);
        lyricResumeTimer.Tick += (_, _) =>
        {
            lyricResumeTimer.Stop();
            if (!IsVisible) return;
            module.CancelLyricPreview();
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
            module.PropertyChanged -= OnModuleChanged; module.PropertyChanged += OnModuleChanged;
            module.Lyrics.CollectionChanged -= OnLyricRowsChanged; module.Lyrics.CollectionChanged += OnLyricRowsChanged;
            if (Descendant<ScrollViewer>(LyricList) is { } scroll)
            { scroll.ViewChanged -= OnLyricViewChanged; scroll.ViewChanged += OnLyricViewChanged; }
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                if (visible) { QueueCenterLyric(); SyncNeedle(); }
                else { StopLyricMotion(); playbackSpacePressed = false; needleMotion?.Stop(); needlePlaying = null; }
            });
        };
        Unloaded += (_, _) =>
        {
            module.PropertyChanged -= OnModuleChanged; module.Lyrics.CollectionChanged -= OnLyricRowsChanged;
            visibility?.Dispose(); visibility = null;
            playbackSpacePressed = false;
            StopLyricMotion();
            needleMotion?.Stop(); needleMotion = null;
            needlePlaying = null;
            artworkRequest?.Cancel();
            artworkPresentation?.Dispose(); artworkPresentation = null;
        };
        RefreshTranslation();
    }

    private void RefreshDetailPalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        var settings = new global::Windows.UI.ViewManagement.UISettings();
        ((SolidColorBrush)Resources["Player.DetailText"]).Color = contrast ? settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText) :
            global::Windows.UI.Color.FromArgb(255, 235, 234, 237);
        Foreground = (Brush)Resources["Player.DetailText"];
        DetailMetadata.Opacity = contrast ? 1 : .68;
        if (contrast) PageBackdrop.Background = new SolidColorBrush(settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window));
        else MusicVisuals.Bind(PageBackdrop, Border.BackgroundProperty, nameof(MusicCapability.PlayerBackground), module);
    }
    private static void RefreshDetailTab(RadioButton tab, bool pointer)
    {
        bool selected = tab.IsChecked == true;
        if (DesktopTheme.GetIsHighContrast(tab))
        {
            var settings = new global::Windows.UI.ViewManagement.UISettings();
            tab.Foreground = new SolidColorBrush(settings.UIElementColor(selected || pointer ? global::Windows.UI.ViewManagement.UIElementType.HighlightText : global::Windows.UI.ViewManagement.UIElementType.WindowText));
            tab.Background = new SolidColorBrush(settings.UIElementColor(selected || pointer ? global::Windows.UI.ViewManagement.UIElementType.Highlight : global::Windows.UI.ViewManagement.UIElementType.Window));
        }
        else
        {
            tab.Foreground = MusicVisuals.Brush(selected ? "#F3F1F4" : "#9B969B");
            tab.Background = MusicVisuals.Brush(pointer ? "#10FFFFFF" : selected ? "#18FFFFFF" : "#00000000");
        }
    }

    internal bool BrowseLyric(VirtualKey key)
    {
        if (!IsVisible || Descendant<ScrollViewer>(LyricList) is not { } scroll) return false;
        if (key is VirtualKey.Home or VirtualKey.End)
        {
            if (module.Lyrics.Count == 0) return false;
            BrowseLyrics(scroll, 0, key == VirtualKey.Home ? 0 : module.Lyrics.Count - 1);
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
        if (!IsVisible || Descendant<ScrollViewer>(LyricList) is not { } scroll) return false;
        int lines = MeowFilm.Desktop.DesktopSystemSettings.WheelScrollLines;
        double distance = lines < 0 ? scroll.ViewportHeight : lines * 24d;
        BrowseLyrics(scroll, -delta / 120d * distance); return true;
    }
    private void RefreshTranslation()
    {
        string label = module.ShowLyricTranslation ? "隐藏译文歌词" : "显示译文歌词";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LyricTranslationButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(LyricTranslationButton, module.ShowLyricTranslation ? "已显示" : "已隐藏");
        ToolTipService.SetToolTip(LyricTranslationButton, label);
        LyricTranslationButton.Background = MusicVisuals.Brush(module.ShowLyricTranslation ? "#18FFFFFF" : "#00000000");
    }
    private async void Detail_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space) return;
        var command = HandlePlaybackSpace(e.OriginalSource, true, e.KeyStatus.WasKeyDown, DesktopVisual.Modifiers);
        e.Handled |= command.Handled;
        await command.Work;
    }

    private void DelayLyric_Click(object sender, RoutedEventArgs e) => module.AdjustLyricOffset(.5);
    private void AdvanceLyric_Click(object sender, RoutedEventArgs e) => module.AdjustLyricOffset(-.5);
    private async void Translation_Click(object sender, RoutedEventArgs e) =>
        await module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);

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
            return (IsVisible && module.IsLyrics && IsPlaybackKeySource(originalSource), Task.CompletedTask);
        }
        if (!IsVisible || !module.IsLyrics || !IsPlaybackKeySource(originalSource) ||
            modifiers != VirtualKeyModifiers.None) return (false, Task.CompletedTask);
        if (playbackSpacePressed) return (true, Task.CompletedTask);
        if (!module.CanTogglePlayback) return (false, Task.CompletedTask);
        playbackSpacePressed = true;
        // The routed handler consumes the key before ButtonBase can arm its release.
        return (true, repeat ? Task.CompletedTask : module.TogglePlaybackAsync());
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
            RefreshTranslation();
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
        var firstHeight = (LyricList.ContainerFromIndex(0) as FrameworkElement)?.ActualHeight ?? 47;
        var lastHeight = (LyricList.ContainerFromIndex(module.Lyrics.Count - 1) as FrameworkElement)?.ActualHeight ?? 47;
        LyricLeadingSpace.Height = Math.Max(0, scroll.ViewportHeight / 2 - firstHeight / 2);
        LyricTrailingSpace.Height = Math.Max(0, scroll.ViewportHeight / 2 - lastHeight / 2);
        if (module.HasLyricPreview) QueuePreviewSnap();
        else if (!lyricResumeTimer.IsEnabled) QueueCenterLyric();
    }

    private void SyncNeedle()
    {
        if (needlePlaying == module.IsPlaying) return;
        needlePlaying = module.IsPlaying; double from = NeedleRotation.Angle;
        needleMotion?.Stop(); NeedleRotation.Angle = module.IsPlaying ? 25 : 0;
        if (IsVisible && MusicVisuals.CanAnimate(this))
            needleMotion = MusicVisuals.AnimateDouble(NeedleRotation, "Angle", from, NeedleRotation.Angle, TimeSpan.FromMilliseconds(450));
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
            module.PreviewLyric(module.Lyrics[rowIndex]);
            SnapLyricPreview(scroll, rowIndex);
            return;
        }
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
        if (!snappingLyric && nearest is not null) module.PreviewLyric(nearest);
        UpdatePreviewFrame();
    }

    private void UpdatePreviewFrame()
    {
        if (!module.HasLyricPreview ||
            LyricList.ContainerFromIndex(module.LyricDisplayIndex) is not FrameworkElement row)
            return;
        LyricPreviewFrame.Height = row.ActualHeight;
    }

    private void QueuePreviewSnap()
    {
        if (centerRequest) return;
        centerRequest = true; var epoch = centerEpoch;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != centerEpoch) return;
            centerRequest = false;
            if (IsLoaded && IsVisible && module.HasLyricPreview &&
                Descendant<ScrollViewer>(LyricList) is { } scroll && !MusicLyricScroll.IsMoving(scroll))
                SnapLyricPreview(scroll);
        });
    }

    private void SnapLyricPreview(ScrollViewer scroll, int? requestedIndex = null)
    {
        if (!module.HasLyricPreview) return;
        if (requestedIndex is null) UpdateLyricPreview(scroll);
        var index = requestedIndex ?? module.LyricDisplayIndex;
        snappingLyric = true;
        if (RealizeLyric(index) is not { } row) { snappingLyric = false; return; }
        UpdatePreviewFrame();
        var correction = row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y -
            scroll.ViewportHeight * .5;
        var epoch = ++lyricMotionEpoch;
        MusicLyricScroll.MoveManual(scroll, scroll.VerticalOffset + correction, () =>
        {
            if (epoch != lyricMotionEpoch || !module.HasLyricPreview) return;
            // Virtualized row reflow can adjust the offset after the original target.
            LyricList.UpdateLayout();
            if (LyricList.ContainerFromIndex(index) is FrameworkElement settledRow)
            {
                var gap = settledRow.TransformToVisual(scroll).TransformPoint(new Point(0, settledRow.ActualHeight / 2)).Y -
                    scroll.ViewportHeight * .5;
                if (Math.Abs(gap) >= .5)
                {
                    MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + gap);
                    scroll.UpdateLayout();
                }
            }
            snappingLyric = false;
            UpdatePreviewFrame();
            UnwatchCenteredRow();
            centeredRow = LyricList.ContainerFromIndex(index) as FrameworkElement;
            if (centeredRow is not null) centeredRow.SizeChanged += OnCenteredRowSizeChanged;
        });
    }

    private void StopLyricMotion()
    {
        lyricResumeTimer.Stop();
        snappingLyric = false;
        module.CancelLyricPreview();
        centerEpoch++; centerRequest = false;
        InvalidateLyricCenter();
    }

    private void QueueCenterLyric()
    {
        if (centerRequest) return;
        centerRequest = true; var epoch = centerEpoch;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != centerEpoch) return;
            centerRequest = false;
            if (IsLoaded && IsVisible && !lyricResumeTimer.IsEnabled && !module.HasLyricPreview)
                CenterLyric();
        });
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
            row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y;
        var epoch = ++lyricMotionEpoch;
        var anchor = .5;
        centeringLyric = index;
        var target = absoluteCenter - scroll.ViewportHeight * anchor;
        if (centeredLyric < 0 && module.ActiveLyricIndex < 0)
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
        if (epoch != lyricMotionEpoch || !IsVisible || module.HasLyricPreview ||
            lyricResumeTimer.IsEnabled || Math.Max(0, module.LyricDisplayIndex) != index) return;
        LyricList.UpdateLayout();
        var row = LyricList.ContainerFromIndex(index) as FrameworkElement;
        if (row is null)
        {
            if (centeringLyric == index) centeringLyric = -1;
            return;
        }
        var rowCenter = row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y;
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
        if (LyricList.ContainerFromIndex(index) is FrameworkElement row) return row;
        {
            LyricList.ScrollIntoView(module.Lyrics[index]);
            LyricList.UpdateLayout();
            return LyricList.ContainerFromIndex(index) as FrameworkElement;
        }
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
