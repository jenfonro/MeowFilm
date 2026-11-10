using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>One sampled playback timeline for the realized words of a virtualized lyric list.</summary>
public sealed class MusicLyricList : ListBox
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
    private DispatcherOperation? refresh;
    private double sample;
    private long sampledAt;
    private double? sampleRate;
    private double? boundary;
    private Clock? boundaryClock;
    private long boundaryEpoch;
    private bool recalibrate;
    private bool viewportDirty = true;
    private TranslateTransform? presentationTranslation;
    private double presentationFrom;

    public MusicLyricList()
    {
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
            InvalidateViewport();
            Rebase();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
            Suspend();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { InvalidateViewport(); Rebase(); }
            else Suspend();
        };
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateViewport()));
    }

    private static void OnPlaybackChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var list = (MusicLyricList)owner;
        list.Rebase(retireClocks: args.Property == PresentationKeyProperty);
    }

    private bool CanAdvance => IsLoaded && IsVisible && IsPlaying && !string.IsNullOrEmpty(PresentationKey) &&
        double.IsFinite(Position) && PlaybackRate is > 0 && double.IsFinite(PlaybackRate.Value) &&
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && !DesktopTheme.GetIsHighContrast(this);

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

    internal void InvalidateViewport()
    {
        viewportDirty = true;
        RefreshWords();
    }

    // The scroll owner translates its ItemsPresenter vertically from this list-space
    // DIP offset to zero, then resets the transform before ending or cancelling.
    internal void BeginPresentationMotion(TranslateTransform translation, double from)
    {
        presentationTranslation = translation;
        presentationFrom = from;
        InvalidateViewport();
    }

    internal void EndPresentationMotion(TranslateTransform translation)
    {
        if (!ReferenceEquals(presentationTranslation, translation)) return;
        presentationTranslation = null;
        InvalidateViewport();
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var result = base.ArrangeOverride(arrangeBounds);
        InvalidateViewport();
        return result;
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        InvalidateViewport();
    }

    internal void RefreshWords()
    {
        if (!IsLoaded || !IsVisible || refresh is not null) return;
        refresh = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            refresh = null;
            PresentWords();
        }));
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
            var viewport = new Rect(RenderSize);
            var translation = presentationTranslation?.Y ?? 0;
            foreach (var view in words)
            {
                var visible = view.IsVisible && view.IsArrangeValid && view.RenderSize.Width > 0 &&
                    view.RenderSize.Height > 0 && IsAncestorOf(view);
                if (visible)
                {
                    var bounds = view.TransformToAncestor(this).TransformBounds(new Rect(view.RenderSize));
                    if (presentationTranslation is not null)
                    {
                        // Normalize away the current animated value, including when layout
                        // invalidates this cache partway through the compositor motion.
                        bounds.Offset(0, -translation);
                        var start = bounds;
                        start.Offset(0, presentationFrom);
                        bounds.Union(start);
                    }
                    visible = bounds.IntersectsWith(viewport);
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
        var timeline = new ParallelTimeline
        {
            Duration = TimeSpan.FromSeconds((next.Value - position) / rate.Value),
            FillBehavior = FillBehavior.Stop
        };
        var clock = timeline.CreateClock(true);
        boundaryClock = clock;
        clock.Completed += (_, _) =>
        {
            if (epoch != boundaryEpoch) return;
            StopBoundary();
            // WPF completion is a boundary event, not a frame callback or a business notification.
            PresentWords();
        };
        clock.Controller!.Begin();
    }

    private void StopBoundary()
    {
        boundaryEpoch++;
        if (boundary is null) return;
        boundary = null;
        boundaryClock?.Controller?.Remove();
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
        refresh?.Abort(); refresh = null;
        StopClocks();
        sampleRate = null;
        presentationTranslation = null;
    }

    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
        {
            foreach (var word in words) word.InvalidateVisual();
            Rebase();
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty) Rebase();
    }
}
