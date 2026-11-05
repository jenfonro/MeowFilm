using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private DispatcherOperation? refresh;
    private double sample;
    private long sampledAt;
    private double? sampleRate;
    private double? boundary;
    private Clock? boundaryClock;
    private long boundaryEpoch;
    private bool recalibrate;

    public MusicLyricList()
    {
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
            Rebase();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
            Suspend();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Rebase(); else Suspend(); };
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => RefreshWords()));
        SizeChanged += (_, _) => RefreshWords();
    }

    private static void OnPlaybackChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var list = (MusicLyricList)owner;
        // Retire old clocks immediately, before the coalesced binding batch is applied.
        list.StopClocks();
        list.Rebase();
    }

    private bool CanAdvance => IsLoaded && IsVisible && IsPlaying && !string.IsNullOrEmpty(PresentationKey) &&
        double.IsFinite(Position) && PlaybackRate is > 0 && double.IsFinite(PlaybackRate.Value) &&
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && !DesktopTheme.GetIsHighContrast(this);

    private void Rebase()
    {
        sample = Position;
        sampledAt = Stopwatch.GetTimestamp();
        sampleRate = CanAdvance ? PlaybackRate : null;
        recalibrate = true;
        RefreshWords();
    }

    private double CurrentPosition => sampleRate is { } rate
        ? sample + Stopwatch.GetElapsedTime(sampledAt).TotalSeconds * rate : sample;

    internal void Register(MusicLyricWordView word) { words.Add(word); RefreshWords(); }
    internal void Unregister(MusicLyricWordView word) { words.Remove(word); word.StopHighlight(); RefreshWords(); }

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
        var viewport = new Rect(RenderSize);
        double? next = null;
        foreach (var view in words)
        {
            var visible = view.IsVisible && view.IsArrangeValid && view.RenderSize.Width > 0 &&
                view.RenderSize.Height > 0 && IsAncestorOf(view) &&
                view.TransformToAncestor(this).TransformBounds(new Rect(view.RenderSize)).IntersectsWith(viewport);
            view.Present(position, visible ? rate : null, recalibrate);
            if (!visible || rate is null || view.Word is not { } word) continue;
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
        foreach (var word in words) word.StopHighlight();
    }

    private void Suspend()
    {
        refresh?.Abort(); refresh = null;
        StopClocks();
        sampleRate = null;
    }

    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
        {
            StopClocks();
            foreach (var word in words) word.InvalidateVisual();
            Rebase();
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty) { StopClocks(); Rebase(); }
    }
}
