using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>One compositor-owned playback timeline for the realized words of a virtualized lyric list.</summary>
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
    /// <summary>The rate accepted by the current media occurrence; uninitialized/non-positive values default to 1.0.</summary>
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }

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
    private long refreshEpoch;
    private double sample;
    private long sampledAt;
    private double? sampleRate;
    private bool viewportDirty = true;
    private Size lastArrangeSize;
    private ScrollViewer? scrollViewer;
    private DesktopWindow? window;

    public MusicLyricList()
    {
        SelectionMode = ListViewSelectionMode.None;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Rebase(retireClocks: true));
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (IsVisible) { InvalidateViewport(); Rebase(); } else Suspend();
        });
        ContainerContentChanging += (_, _) => InvalidateViewport();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        scrollViewer = MusicVisuals.Descendant<ScrollViewer>(this);
        if (scrollViewer is not null)
        {
            scrollViewer.ViewChanged -= OnViewChanged;
            scrollViewer.ViewChanged += OnViewChanged;
        }
        SystemAppearance.Changed -= OnSystemAppearanceChanged;
        SystemAppearance.Changed += OnSystemAppearanceChanged;
        window = DesktopWindow.GetWindow(this);
        if (window is not null) window.VisibilityChanged += WindowVisibilityChanged;
        InvalidateViewport();
        Rebase();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (scrollViewer is not null)
        {
            scrollViewer.ViewChanged -= OnViewChanged;
            scrollViewer = null;
        }
        SystemAppearance.Changed -= OnSystemAppearanceChanged;
        if (window is not null) window.VisibilityChanged -= WindowVisibilityChanged;
        window = null;
        Suspend();
        DisposeTimeline();
    }

    private bool IsVisible => DesktopVisual.IsVisible(this);

    private void WindowVisibilityChanged(object? sender, EventArgs e)
    {
        if (IsVisible) { InvalidateViewport(); Rebase(); } else Suspend();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        InvalidateViewport();
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

    private bool CanAdvance => IsLoaded && IsVisible && IsPlaying && !string.IsNullOrEmpty(PresentationKey) &&
        double.IsFinite(Position) && MusicVisuals.CanAnimate(this);

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
            lastArrangeSize = arrangeBounds;
            InvalidateViewport();
        }
        return result;
    }

    internal void RefreshWords()
    {
        if (!IsLoaded || !IsVisible || refresh) return;
        refresh = true;
        var epoch = refreshEpoch;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (epoch != refreshEpoch) return;
            refresh = false;
            PresentWords();
        });
    }

    private void PresentWords()
    {
        if (!IsLoaded || !IsVisible) return;
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
                MusicVisuals.IsWithin(rowHost, this);
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
        refreshEpoch++;
        refresh = false;
        StopAllBindings();
        sampleRate = null;
        timelineDirty = true;
    }
}
