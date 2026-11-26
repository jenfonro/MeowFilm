using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>One compositor-owned playback timeline and scroll/preview owner for the virtualized lyric list.</summary>
public sealed class MusicLyricList : ListView
{
    private const double DriftRecalibrateThresholdSeconds = 0.20;
    private const double TimelineHorizonMinimumSeconds = 3600.0;

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position),
        typeof(double), typeof(MusicLyricList), new PropertyMetadata(0d, OnPlaybackChanged));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying),
        typeof(bool), typeof(MusicLyricList), new PropertyMetadata(false, OnPlaybackChanged));
    public static readonly DependencyProperty PlaybackRateProperty = DependencyProperty.Register(nameof(PlaybackRate),
        typeof(double?), typeof(MusicLyricList), new PropertyMetadata(null, OnPlaybackChanged));
    public static readonly DependencyProperty PresentationKeyProperty = DependencyProperty.Register(nameof(PresentationKey),
        typeof(string), typeof(MusicLyricList), new PropertyMetadata("", OnPlaybackChanged));

    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }

    internal event Action<MusicLyricRow?, bool>? BrowsePreviewChanged;
    internal event Action? BrowseInterrupted;

    private readonly Border leadingSpace = new() { Height = 0 };
    private readonly Border trailingSpace = new() { Height = 0 };
    private readonly Dictionary<FrameworkElement, HashSet<MusicLyricWordView>> rowWords = [];
    private readonly Dictionary<MusicLyricWordView, FrameworkElement> wordOwners = [];
    private readonly HashSet<MusicLyricWordView> visibleWords = [];
    private readonly HashSet<MusicLyricWordView> boundWords = [];
    private CompositionPropertySet? timeline;
    private LinearEasingFunction? linearEasing;
    private ScalarKeyFrameAnimation? timelineAnimation;
    private bool timelineRunning;
    private bool timelineDirty = true;
    private bool refresh;
    private double sample;
    private long sampledAt;
    private double? sampleRate;
    private bool viewportDirty = true;
    private Size lastArrangeSize;
    private ScrollViewer? scrollViewer;
    private IDisposable? visibility;
    private bool isVisible;
    private double viewportHeight;
    private double firstRowHeight = 47, lastRowHeight = 47;
    private int centeredIndex = -1;
    private (int Index, bool Animate)? pendingCenter;
    private bool browsing, snapping;
    private FrameworkElement? watchedRow;

    public MusicLyricList()
    {
        SelectionMode = ListViewSelectionMode.None;
        Header = leadingSpace;
        Footer = trailingSpace;
        Loaded += (_, _) => { visibility?.Dispose(); visibility = DesktopVisual.ObserveVisibility(this, SetVisible); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose(); visibility = null;
            SetVisible(false);
            DetachScrollViewer();
            DisposeTimeline();
        };
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Rebase(retireClocks: true));
        ContainerContentChanging += (_, _) =>
        {
            InvalidateViewport();
            UpdateSpaces();
            FlushPendingCenter();
        };
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        AttachScrollViewer(GetTemplateChild("ScrollViewer") as ScrollViewer);
    }

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (BrowseWheel(e.GetCurrentPoint(this).Properties.MouseWheelDelta))
        {
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (BrowseKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void AttachScrollViewer(ScrollViewer? next)
    {
        if (ReferenceEquals(scrollViewer, next)) return;
        DetachScrollViewer();
        scrollViewer = next;
        if (scrollViewer is not null && isVisible) SubscribeScrollViewer(scrollViewer);
    }

    private void SubscribeScrollViewer(ScrollViewer viewer)
    {
        viewer.ViewChanged -= OnViewChanged;
        viewer.ViewChanged += OnViewChanged;
        viewer.DirectManipulationStarted -= OnManipulationStarted;
        viewer.DirectManipulationStarted += OnManipulationStarted;
        viewer.DirectManipulationCompleted -= OnManipulationCompleted;
        viewer.DirectManipulationCompleted += OnManipulationCompleted;
    }

    private void DetachScrollViewer()
    {
        if (scrollViewer is null) return;
        scrollViewer.ViewChanged -= OnViewChanged;
        scrollViewer.DirectManipulationStarted -= OnManipulationStarted;
        scrollViewer.DirectManipulationCompleted -= OnManipulationCompleted;
        scrollViewer = null;
    }

    private void SetVisible(bool value)
    {
        isVisible = value;
        if (scrollViewer is not null)
        {
            scrollViewer.ViewChanged -= OnViewChanged;
            scrollViewer.DirectManipulationStarted -= OnManipulationStarted;
            scrollViewer.DirectManipulationCompleted -= OnManipulationCompleted;
        }
        SystemAppearance.Changed -= OnSystemAppearanceChanged;
        if (!value)
        {
            StopMotion();
            Suspend();
            return;
        }
        if (scrollViewer is not null) SubscribeScrollViewer(scrollViewer);
        SystemAppearance.Changed += OnSystemAppearanceChanged;
        InvalidateViewport();
        UpdateSpaces();
        Rebase();
    }

    internal void ResetRows()
    {
        firstRowHeight = lastRowHeight = 47;
        InvalidateAlignment();
    }

    internal void InvalidateAlignment()
    {
        UnwatchRow();
        pendingCenter = null;
        snapping = false;
        centeredIndex = -1;
        if (scrollViewer is { } scroll) MusicLyricScroll.Stop(scroll);
    }

    internal void StopMotion()
    {
        browsing = false;
        InvalidateAlignment();
    }

    internal double RowHeight(int index) =>
        index >= 0 && index < Items.Count && ContainerFromIndex(index) is FrameworkElement { ActualHeight: > 0 } row
            ? row.ActualHeight
            : 47;

    internal bool BrowseKey(VirtualKey key)
    {
        if (!isVisible || Items.Count == 0 || scrollViewer is not { } scroll) return false;
        if (key is VirtualKey.Home or VirtualKey.End)
        {
            int rowIndex = key == VirtualKey.Home ? 0 : Items.Count - 1;
            BeginBrowse();
            snapping = true;
            BrowsePreviewChanged?.Invoke(Items[rowIndex] as MusicLyricRow, false);
            AlignIndex(rowIndex, animate: true, preview: true);
            return true;
        }
        double? distance = key switch
        {
            VirtualKey.Up => -48,
            VirtualKey.Down => 48,
            VirtualKey.PageUp => -scroll.ViewportHeight,
            VirtualKey.PageDown => scroll.ViewportHeight,
            _ => null
        };
        if (distance is null) return false;
        BrowseDistance(scroll, distance.Value);
        return true;
    }

    internal bool BrowseWheel(int delta)
    {
        if (!isVisible || Items.Count == 0 || scrollViewer is not { } scroll) return false;
        int lines = DesktopSystemSettings.WheelScrollLines;
        double distance = lines < 0 ? scroll.ViewportHeight : lines * 24d;
        BrowseDistance(scroll, -delta / 120d * distance);
        return true;
    }

    private void BeginBrowse()
    {
        UnwatchRow();
        pendingCenter = null;
        snapping = false;
        centeredIndex = -1;
        browsing = true;
        BrowseInterrupted?.Invoke();
    }

    private void BrowseDistance(ScrollViewer scroll, double distance)
    {
        double target = MusicLyricScroll.ManualTarget(scroll) + distance;
        BeginBrowse();
        MusicLyricScroll.Move(scroll, target, () =>
        {
            if (isVisible && browsing) SnapNearest(scroll);
        });
        PublishNearest(scroll);
    }

    private void OnManipulationStarted(object? sender, object e)
    {
        if (!isVisible || Items.Count == 0 || sender is not ScrollViewer scroll) return;
        BeginBrowse();
        PublishNearest(scroll);
    }

    private void OnManipulationCompleted(object? sender, object e)
    {
        if (!isVisible || !browsing || sender is not ScrollViewer scroll) return;
        BrowseInterrupted?.Invoke();
        SnapNearest(scroll);
    }

    private void SnapNearest(ScrollViewer scroll)
    {
        int index = NearestIndex(scroll);
        if (index < 0) { snapping = false; return; }
        snapping = true;
        BrowsePreviewChanged?.Invoke(Items[index] as MusicLyricRow, true);
        AlignIndex(index, animate: true, preview: true);
    }

    private void PublishNearest(ScrollViewer scroll)
    {
        if (snapping) return;
        int index = NearestIndex(scroll);
        if (index >= 0) BrowsePreviewChanged?.Invoke(Items[index] as MusicLyricRow, false);
    }

    private int NearestIndex(ScrollViewer scroll)
    {
        if (ItemsPanelRoot is not { } panel) return -1;
        int nearest = -1;
        double best = double.PositiveInfinity;
        double middle = scroll.ViewportHeight * 0.5;
        foreach (FrameworkElement item in panel.Children)
        {
            int index = IndexFromContainer(item);
            if (index < 0) continue;
            var bounds = item.TransformToVisual(scroll).TransformPoint(new Point());
            double gap = Math.Abs(bounds.Y + item.ActualHeight * 0.5 - middle);
            if (gap >= best) continue;
            best = gap;
            nearest = index;
        }
        return nearest;
    }

    internal void AlignIndex(int index, bool animate, bool preview = false)
    {
        if (!preview) browsing = false;
        if (!isVisible || index < 0 || index >= Items.Count || scrollViewer is not { } scroll) return;
        if (!preview && index == centeredIndex && pendingCenter is null) return;
        UnwatchRow();
        UpdateSpaces();
        if (ContainerFromIndex(index) is not FrameworkElement { ActualHeight: > 0 } row)
        {
            pendingCenter = (index, animate);
            ScrollIntoView(Items[index]);
            return;
        }
        pendingCenter = null;
        double offset = scroll.VerticalOffset +
            row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight * 0.5)).Y -
            scroll.ViewportHeight * 0.5;
        if (!animate)
        {
            MusicLyricScroll.Settle(scroll, offset);
            FinishAlign(index, scroll, preview);
        }
        else
        {
            MusicLyricScroll.Move(scroll, offset, () =>
            {
                if (isVisible && (preview == browsing))
                    FinishAlign(index, scroll, preview);
            });
        }
    }

    private void FlushPendingCenter()
    {
        if (pendingCenter is not var (index, animate)) return;
        if (index < 0 || index >= Items.Count || ContainerFromIndex(index) is not FrameworkElement { ActualHeight: > 0 }) return;
        pendingCenter = null;
        AlignIndex(index, animate, browsing);
    }

    private void FinishAlign(int index, ScrollViewer scroll, bool preview)
    {
        if (ContainerFromIndex(index) is FrameworkElement { ActualHeight: > 0 } settled)
        {
            double gap = settled.TransformToVisual(scroll).TransformPoint(new Point(0, settled.ActualHeight * 0.5)).Y -
                scroll.ViewportHeight * 0.5;
            if (Math.Abs(gap) >= 0.5)
                MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + gap);
            WatchRow(settled);
        }
        snapping = false;
        if (!preview) centeredIndex = index;
        else BrowsePreviewChanged?.Invoke(Items[index] as MusicLyricRow, true);
    }

    private void WatchRow(FrameworkElement row)
    {
        UnwatchRow();
        watchedRow = row;
        watchedRow.SizeChanged += OnWatchedRowSizeChanged;
    }

    private void UnwatchRow()
    {
        if (watchedRow is not null) watchedRow.SizeChanged -= OnWatchedRowSizeChanged;
        watchedRow = null;
    }

    private void OnWatchedRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int index = watchedRow is not null ? IndexFromContainer(watchedRow) : centeredIndex;
        InvalidateAlignment();
        UpdateSpaces();
        if (index >= 0) AlignIndex(index, animate: false, preview: browsing);
    }

    private void UpdateSpaces()
    {
        if (scrollViewer is not { ViewportHeight: > 0 } scroll) return;
        if (Items.Count > 0 && ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 } first)
            firstRowHeight = first.ActualHeight;
        if (Items.Count > 0 && ContainerFromIndex(Items.Count - 1) is FrameworkElement { ActualHeight: > 0 } last)
            lastRowHeight = last.ActualHeight;
        double leading = Math.Max(0, scroll.ViewportHeight * 0.5 - firstRowHeight * 0.5);
        double trailing = Math.Max(0, scroll.ViewportHeight * 0.5 - lastRowHeight * 0.5);
        if (Math.Abs(leadingSpace.Height - leading) >= 0.5) leadingSpace.Height = leading;
        if (Math.Abs(trailingSpace.Height - trailing) >= 0.5) trailingSpace.Height = trailing;
        if (Math.Abs(viewportHeight - scroll.ViewportHeight) >= 0.5)
        {
            viewportHeight = scroll.ViewportHeight;
            centeredIndex = -1;
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (browsing && sender is ScrollViewer scroll) PublishNearest(scroll);
        if (!e.IsIntermediate) InvalidateViewport();
    }

    private static void OnPlaybackChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var list = (MusicLyricList)owner;
        if (args.Property == PositionProperty)
        {
            list.SyncPositionSample();
            return;
        }
        list.Rebase(retireClocks: args.Property == PresentationKeyProperty);
    }

    private double EffectiveRate => PlaybackRate is { } r && double.IsFinite(r) && r > 0 ? r : 1.0;

    private bool CanAdvance => IsLoaded && isVisible && IsPlaying && !string.IsNullOrEmpty(PresentationKey) &&
        double.IsFinite(Position) && DesktopVisual.CanAnimate(this);

    internal double CurrentPosition => sampleRate is { } rate
        ? sample + Stopwatch.GetElapsedTime(sampledAt).TotalSeconds * rate : sample;

    private void SyncPositionSample()
    {
        double? nextRate = CanAdvance ? EffectiveRate : null;
        if (sampleRate is { } currentRate && nextRate is { } activeRate &&
            Math.Abs(currentRate - activeRate) < 1e-6 && double.IsFinite(Position))
        {
            double drift = Math.Abs(Position - CurrentPosition);
            if (drift < DriftRecalibrateThresholdSeconds) return;
        }
        Rebase(retireClocks: false);
    }

    private void Rebase(bool retireClocks = false)
    {
        sample = Position;
        sampledAt = Stopwatch.GetTimestamp();
        sampleRate = CanAdvance ? EffectiveRate : null;
        timelineDirty = true;
        if (retireClocks) StopAllBindings();
        else if (sampleRate is null) StopTimelineAnimation(sample);
        RefreshWords();
    }

    internal void Register(MusicLyricWordView word, FrameworkElement rowHost)
    {
        if (wordOwners.TryGetValue(word, out var previousOwner))
        {
            if (ReferenceEquals(previousOwner, rowHost)) { RefreshWords(); return; }
            if (rowWords.TryGetValue(previousOwner, out var previousGroup))
            {
                previousGroup.Remove(word);
                if (previousGroup.Count == 0) rowWords.Remove(previousOwner);
            }
        }
        wordOwners[word] = rowHost;
        if (!rowWords.TryGetValue(rowHost, out var group))
            rowWords[rowHost] = group = [];
        group.Add(word);
        InvalidateViewport();
    }

    internal void Unregister(MusicLyricWordView word)
    {
        if (wordOwners.Remove(word, out var owner) && rowWords.TryGetValue(owner, out var group))
        {
            group.Remove(word);
            if (group.Count == 0) rowWords.Remove(owner);
        }
        visibleWords.Remove(word);
        boundWords.Remove(word);
        word.StopHighlight();
    }

    internal new void InvalidateViewport()
    {
        viewportDirty = true;
        RefreshWords();
    }

    private void OnSystemAppearanceChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => Rebase(retireClocks: true));

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var result = base.ArrangeOverride(arrangeBounds);
        if (Math.Abs(arrangeBounds.Width - lastArrangeSize.Width) > 0.5 ||
            Math.Abs(arrangeBounds.Height - lastArrangeSize.Height) > 0.5)
        {
            bool widthChanged = Math.Abs(arrangeBounds.Width - lastArrangeSize.Width) > 0.5;
            lastArrangeSize = arrangeBounds;
            if (widthChanged) InvalidateAlignment();
            UpdateSpaces();
            InvalidateViewport();
            FlushPendingCenter();
        }
        return result;
    }

    internal void RefreshWords()
    {
        if (!IsLoaded || !isVisible || refresh) return;
        refresh = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            refresh = false;
            PresentWords();
        });
    }

    private void PresentWords()
    {
        if (!IsLoaded || !isVisible) return;
        double position = CurrentPosition;
        double? rate = CanAdvance ? EffectiveRate : null;
        if (rate != sampleRate)
        {
            sample = position;
            sampledAt = Stopwatch.GetTimestamp();
            sampleRate = rate;
            timelineDirty = true;
        }

        if (viewportDirty)
        {
            viewportDirty = false;
            RebuildVisibleRows(position);
        }

        SyncTimeline(position, rate);

        foreach (var word in visibleWords)
        {
            if (word.BindTimeline(timeline, position, timelineRunning))
                boundWords.Add(word);
            else
                boundWords.Remove(word);
        }
    }

    private void RebuildVisibleRows(double position)
    {
        visibleWords.Clear();
        double bufferY = Math.Max(120.0, ActualHeight * 0.5);
        var viewport = new Rect(0, -bufferY, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight) + bufferY * 2.0);
        foreach (var (rowHost, group) in rowWords)
        {
            bool rowVisible = rowHost.Visibility == Visibility.Visible &&
                rowHost.ActualWidth > 0 && rowHost.ActualHeight > 0 &&
                DesktopVisual.Contains(this, rowHost);
            if (rowVisible)
            {
                var bounds = rowHost.TransformToVisual(this).TransformBounds(new Rect(0, 0, rowHost.ActualWidth, rowHost.ActualHeight));
                bounds.Intersect(viewport);
                rowVisible = !bounds.IsEmpty;
            }
            if (rowVisible)
            {
                foreach (var word in group)
                {
                    if (word.Visibility == Visibility.Visible) visibleWords.Add(word);
                    else
                    {
                        word.PresentStatic(position);
                        boundWords.Remove(word);
                    }
                }
            }
            else
            {
                foreach (var word in group)
                {
                    word.PresentStatic(position);
                    boundWords.Remove(word);
                }
            }
        }
    }

    private void EnsureTimeline()
    {
        if (timeline is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        timeline = compositor.CreatePropertySet();
        timeline.InsertScalar("Position", (float)CurrentPosition);
        linearEasing = compositor.CreateLinearEasingFunction();
    }

    private void SyncTimeline(double position, double? rate)
    {
        if (rate is not { } activeRate)
        {
            StopTimelineAnimation(position);
            timelineDirty = false;
            return;
        }
        EnsureTimeline();
        if (!timelineDirty && timelineRunning) return;
        timelineDirty = false;
        double maxWordEnd = position;
        foreach (var word in visibleWords)
        {
            if (word.Word is { } data)
            {
                double end = data.Time + Math.Max(0, data.Duration);
                if (end > maxWordEnd) maxWordEnd = end;
            }
        }
        double horizon = Math.Max(maxWordEnd + 30.0, position + TimelineHorizonMinimumSeconds);
        double durationSeconds = Math.Max(0.001, (horizon - position) / activeRate);
        timelineAnimation?.Dispose();
        var anim = timeline!.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, (float)position);
        anim.InsertKeyFrame(1f, (float)horizon, linearEasing!);
        anim.Duration = TimeSpan.FromSeconds(durationSeconds);
        timelineAnimation = anim;
        timeline.StartAnimation("Position", anim);
        timelineRunning = true;
    }

    private void StopTimelineAnimation(double position)
    {
        if (timeline is not null)
        {
            timeline.StopAnimation("Position");
            timeline.InsertScalar("Position", double.IsFinite(position) ? (float)position : 0f);
        }
        timelineAnimation?.Dispose();
        timelineAnimation = null;
        timelineRunning = false;
    }

    private void StopAllBindings()
    {
        StopTimelineAnimation(sample);
        foreach (var word in boundWords) word.StopHighlight();
        boundWords.Clear();
    }

    private void DisposeTimeline()
    {
        timelineAnimation?.Dispose();
        timelineAnimation = null;
        linearEasing?.Dispose();
        linearEasing = null;
        timeline?.Dispose();
        timeline = null;
        timelineRunning = false;
        timelineDirty = true;
    }

    private void Suspend()
    {
        refresh = false;
        StopAllBindings();
        sampleRate = null;
        timelineDirty = true;
    }
}
