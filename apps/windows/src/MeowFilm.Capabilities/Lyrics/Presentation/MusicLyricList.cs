using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Microsoft.UI.Dispatching;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>One sampled playback timeline for the realized words of a virtualized lyric list.</summary>
public sealed class MusicLyricList : ListView
{
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
    /// <summary>The rate accepted by the current media occurrence; null disables extrapolation.</summary>
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }

    private readonly HashSet<MusicLyricWordView> words = [];
    private readonly HashSet<MusicLyricWordView> visibleWords = [];
    private readonly HashSet<MusicLyricWordView> animatedWords = [];
    private bool refresh;
    private long refreshEpoch;
    private double sample;
    private long sampledAt;
    private double? sampleRate;
    private double? boundary;
    private DispatcherQueueTimer? boundaryClock;
    private long boundaryEpoch;
    private bool recalibrate;
    private bool viewportDirty = true;
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private DesktopWindow? window;

    public MusicLyricList()
    {
        SelectionMode = ListViewSelectionMode.None;
        Loaded += (_, _) =>
        {
            if (MusicVisuals.Descendant<ScrollViewer>(this) is { } scroll)
            { scroll.ViewChanged -= OnViewChanged; scroll.ViewChanged += OnViewChanged; }
            uiSettings.ColorValuesChanged += AccessibilityChanged;
            uiSettings.AnimationsEnabledChanged += AnimationsChanged;
            window = DesktopWindow.GetWindow(this);
            if (window is not null) window.VisibilityChanged += WindowVisibilityChanged;
            InvalidateViewport(); Rebase();
        };
        Unloaded += (_, _) =>
        {
            uiSettings.ColorValuesChanged -= AccessibilityChanged;
            uiSettings.AnimationsEnabledChanged -= AnimationsChanged;
            if (window is not null) window.VisibilityChanged -= WindowVisibilityChanged;
            window = null;
            Suspend();
        };
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Rebase());
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (IsVisible) { InvalidateViewport(); Rebase(); } else Suspend();
        });
        ContainerContentChanging += (_, _) => InvalidateViewport();
    }
    private bool IsVisible => DesktopVisual.IsVisible(this);
    private void WindowVisibilityChanged(object? sender, EventArgs e)
    { if (IsVisible) { InvalidateViewport(); Rebase(); } else Suspend(); }
    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => InvalidateViewport();

    private static void OnPlaybackChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var list = (MusicLyricList)owner;
        list.Rebase(retireClocks: args.Property == PresentationKeyProperty);
    }

    private bool CanAdvance => IsLoaded && IsVisible && IsPlaying && !string.IsNullOrEmpty(PresentationKey) &&
        double.IsFinite(Position) && PlaybackRate is > 0 && double.IsFinite(PlaybackRate.Value) &&
        MusicVisuals.CanAnimate(this);

    private void Rebase(bool retireClocks = false)
    {
        sample = Position;
        sampledAt = Stopwatch.GetTimestamp();
        sampleRate = CanAdvance ? PlaybackRate : null;
        // A binding batch replaces running clips once at render priority. Pausing or retiring
        // an occurrence still removes its clocks immediately, visiting only clock owners.
        if (retireClocks || sampleRate is null) StopClocks();
        else StopBoundary();
        recalibrate = true;
        RefreshWords();
    }

    private double CurrentPosition => sampleRate is { } rate
        ? sample + Stopwatch.GetElapsedTime(sampledAt).TotalSeconds * rate : sample;

    internal void Register(MusicLyricWordView word) { words.Add(word); InvalidateViewport(); }
    internal void Unregister(MusicLyricWordView word)
    {
        words.Remove(word);
        visibleWords.Remove(word);
        animatedWords.Remove(word);
        word.StopHighlight();
        InvalidateViewport();
    }

    internal new void InvalidateViewport()
    {
        viewportDirty = true;
        RefreshWords();
    }

    private void AccessibilityChanged(Windows.UI.ViewManagement.UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => Rebase());
    private void AnimationsChanged(Windows.UI.ViewManagement.UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => Rebase());

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var result = base.ArrangeOverride(arrangeBounds);
        InvalidateViewport();
        return result;
    }

    internal void RefreshWords()
    {
        if (!IsLoaded || !IsVisible || refresh) return;
        refresh = true;
        var epoch = refreshEpoch;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (epoch != refreshEpoch) return;
            refresh = false; PresentWords();
        });
    }

    private void PresentWords()
    {
        if (!IsLoaded || !IsVisible) return;
        var position = CurrentPosition;
        var rate = CanAdvance ? sampleRate : null;
        if (viewportDirty)
        {
            viewportDirty = false;
            visibleWords.Clear();
            var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
            foreach (var view in words)
            {
                var visible = view.Visibility == Visibility.Visible && view.ActualWidth > 0 &&
                    view.ActualHeight > 0 && MusicVisuals.IsWithin(view, this);
                if (visible)
                {
                    var bounds = view.TransformToVisual(this).TransformBounds(new Rect(0, 0, view.ActualWidth, view.ActualHeight));
                    bounds.Intersect(viewport); visible = !bounds.IsEmpty;
                }
                if (visible) visibleWords.Add(view);
                else
                {
                    view.Present(position, null, recalibrate: true);
                    animatedWords.Remove(view);
                }
            }
        }
        double? next = null;
        foreach (var view in visibleWords)
        {
            if (view.Present(position, rate, recalibrate)) animatedWords.Add(view);
            else animatedWords.Remove(view);
            if (rate is null || view.Word is not { } word) continue;
            // Only the next boundary needs a clock; future/completed/offscreen words have none.
            var change = position < word.Time ? word.Time :
                position < word.Time + word.Duration ? word.Time + word.Duration : (double?)null;
            if (change is not null && (next is null || change < next)) next = change;
        }
        recalibrate = false;
        ScheduleBoundary(next, position, rate);
    }

    private void ScheduleBoundary(double? next, double position, double? rate)
    {
        if (boundary == next) return;
        StopBoundary();
        if (next is null || rate is null) return;
        boundary = next;
        var epoch = boundaryEpoch;
        var clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromSeconds(Math.Max(.001, (next.Value - position) / rate.Value));
        clock.IsRepeating = false; boundaryClock = clock;
        clock.Tick += (_, _) =>
        {
            if (epoch != boundaryEpoch) return;
            StopBoundary(); PresentWords();
        };
        clock.Start();
    }

    private void StopBoundary()
    {
        boundaryEpoch++;
        if (boundary is null) return;
        boundary = null;
        boundaryClock?.Stop();
        boundaryClock = null;
    }

    private void StopClocks()
    {
        StopBoundary();
        foreach (var word in animatedWords) word.StopHighlight();
        animatedWords.Clear();
    }

    private void Suspend()
    {
        refreshEpoch++; refresh = false;
        StopClocks();
        sampleRate = null;
    }

}
