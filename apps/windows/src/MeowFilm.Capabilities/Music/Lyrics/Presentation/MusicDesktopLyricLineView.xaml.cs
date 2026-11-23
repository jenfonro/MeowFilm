using System.Diagnostics;
using MeowFilm.Desktop;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>
/// Renders one desktop lyric line via Win2D with a unified solid-backed vector outer stroke
/// and complementary GPU compositor InsetClips for zero-fringe word-by-word karaoke sweep.
/// </summary>
public partial class MusicDesktopLyricLineView : UserControl
{
    private const double BaseMainFontSize = 32.0;
    private const double DriftRecalibrateThresholdSeconds = 0.20;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row),
        typeof(MusicLyricRow), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (o, _) => ((MusicDesktopLyricLineView)o).OnRowChanged()));
    public static readonly DependencyProperty NormalProperty = DependencyProperty.Register(nameof(Normal),
        typeof(Brush), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (o, _) => ((MusicDesktopLyricLineView)o).RenderMainLine()));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight),
        typeof(Brush), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (o, _) => ((MusicDesktopLyricLineView)o).RenderMainLine()));
    public static readonly DependencyProperty ShowTranslationProperty = DependencyProperty.Register(nameof(ShowTranslation),
        typeof(bool), typeof(MusicDesktopLyricLineView), new PropertyMetadata(true, (o, _) => ((MusicDesktopLyricLineView)o).UpdateTranslation()));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position),
        typeof(double), typeof(MusicDesktopLyricLineView), new PropertyMetadata(0d, (o, _) => ((MusicDesktopLyricLineView)o).OnPositionChanged()));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying),
        typeof(bool), typeof(MusicDesktopLyricLineView), new PropertyMetadata(false, (o, _) => ((MusicDesktopLyricLineView)o).RebaseSweep(force: true)));
    public static readonly DependencyProperty PlaybackRateProperty = DependencyProperty.Register(nameof(PlaybackRate),
        typeof(double?), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (o, _) => ((MusicDesktopLyricLineView)o).RebaseSweep(force: true)));
    public static readonly DependencyProperty PresentationKeyProperty = DependencyProperty.Register(nameof(PresentationKey),
        typeof(string), typeof(MusicDesktopLyricLineView), new PropertyMetadata("", (o, _) => ((MusicDesktopLyricLineView)o).RebaseSweep(force: true)));

    public MusicLyricRow? Row { get => (MusicLyricRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public Brush Normal { get => (Brush)GetValue(NormalProperty); set => SetValue(NormalProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public bool ShowTranslation { get => (bool)GetValue(ShowTranslationProperty); set => SetValue(ShowTranslationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }

    private InsetClip? normalClip;
    private InsetClip? highlightClip;
    private CompositionPropertySet? sweepProps;
    private ExpressionAnimation? normalExpr;
    private ExpressionAnimation? highlightExpr;
    private ScalarKeyFrameAnimation? sweepAnim;
    private LinearEasingFunction? linearEasing;
    private XamlRoot? xamlRoot;
    private IDisposable? themeObserver;

    private MusicDesktopLyricRenderer.WordSpan[] activeSpans = [];
    private double renderedWidth;
    private bool renderedRtl;
    private bool clipsBound;
    private bool sweepAnimating;
    private double sampledPos;
    private long sampledAt;
    private double? sampledRate;
    private (MusicLyricRow? Row, double MaxWidth, double Scale, FlowDirection Direction, Windows.UI.Color Normal, Windows.UI.Color Highlight, Windows.UI.Color Stroke)? lastRenderKey;

    public MusicDesktopLyricLineView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => RenderMainLine());
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) { RenderMainLine(); RebaseSweep(force: true); }
            else StopSweepAnimation();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (xamlRoot is not null) xamlRoot.Changed -= OnRootChanged;
        xamlRoot = XamlRoot;
        if (xamlRoot is not null) xamlRoot.Changed += OnRootChanged;
        themeObserver?.Dispose();
        themeObserver = DesktopTheme.Observe(this, () => { RenderMainLine(); RebaseSweep(force: true); });
        EnsureClips();
        UpdateTranslation();
        RenderMainLine();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        if (xamlRoot is not null) xamlRoot.Changed -= OnRootChanged;
        xamlRoot = null;
        themeObserver?.Dispose();
        themeObserver = null;
        DisposeSweepResources();
        lastRenderKey = null;
        StrokeImage.Source = NormalImage.Source = HighlightImage.Source = null;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RenderMainLine();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double available = Math.Max(0, ActualWidth - 4);
        TranslationText.MaxLineWidth = available > 0 ? available : double.PositiveInfinity;
        RenderMainLine();
    }

    private void OnRowChanged()
    {
        if (StrokeImage is null) return;
        Opacity = Row is null ? 0 : 1;
        AutomationProperties.SetName(this, Row?.Text ?? "");
        UpdateTranslation();
        RenderMainLine();
    }

    private void UpdateTranslation()
    {
        if (TranslationText is null) return;
        TranslationText.Visibility = ShowTranslation && !string.IsNullOrWhiteSpace(Row?.Translation)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private double EffectiveRate => PlaybackRate is { } r && double.IsFinite(r) && r > 0 ? r : 1.0;

    private double CurrentPosition => sampledRate is { } rate
        ? sampledPos + Stopwatch.GetElapsedTime(sampledAt).TotalSeconds * rate
        : sampledPos;

    private void OnPositionChanged()
    {
        if (!IsLoaded || activeSpans.Length == 0) return;
        bool canAnimate = CanAnimateSweep();
        double? nextRate = canAnimate ? EffectiveRate : null;
        if (sampledRate is { } curRate && nextRate is { } targetRate &&
            Math.Abs(curRate - targetRate) < 1e-6 && double.IsFinite(Position) && sweepAnimating)
        {
            if (Math.Abs(Position - CurrentPosition) < DriftRecalibrateThresholdSeconds)
                return;
        }
        RebaseSweep(force: false);
    }

    private void RenderMainLine()
    {
        if (!IsLoaded || StrokeImage is null) return;
        var row = Row;
        if (row is null || string.IsNullOrWhiteSpace(row.Text))
        {
            StopSweepAnimation();
            activeSpans = [];
            renderedWidth = 0;
            lastRenderKey = null;
            StrokeImage.Source = NormalImage.Source = HighlightImage.Source = null;
            MainLineSurface.Width = 0;
            MainLineSurface.Height = 0;
            return;
        }

        double maxWidth = ActualWidth > 8 ? Math.Floor(ActualWidth - 4) : double.PositiveInfinity;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        bool highContrast = DesktopTheme.GetIsHighContrast(this);
        var normalColor = MusicDesktopLyricRenderer.ResolveColor(Normal, Microsoft.UI.Colors.White);
        var highlightColor = MusicDesktopLyricRenderer.ResolveColor(Highlight, Windows.UI.Color.FromArgb(255, 255, 181, 197));
        var strokeColor = MusicDesktopLyricRenderer.ResolveStrokeColor(highContrast);

        var key = (row, maxWidth, Math.Round(scale, 3), FlowDirection, normalColor, highlightColor, strokeColor);
        if (lastRenderKey == key) return;
        lastRenderKey = key;

        bool timed = row.Words is { Count: > 0 };
        if (timed)
        {
            var rendered = MusicDesktopLyricRenderer.RenderTimedLine(
                row.Words!, BaseMainFontSize, maxWidth, scale, FlowDirection, normalColor, highlightColor, strokeColor);
            if (rendered is null)
            {
                ClearVisuals();
                return;
            }
            activeSpans = rendered.Spans;
            renderedWidth = rendered.WidthDip;
            renderedRtl = rendered.IsRtl;
            MainLineSurface.Width = rendered.WidthDip;
            MainLineSurface.Height = rendered.HeightDip;
            StrokeImage.Width = NormalImage.Width = HighlightImage.Width = rendered.WidthDip;
            StrokeImage.Height = NormalImage.Height = HighlightImage.Height = rendered.HeightDip;
            StrokeImage.Source = rendered.StrokeImage;
            NormalImage.Source = rendered.NormalImage;
            HighlightImage.Source = rendered.HighlightImage;
            NormalImage.Visibility = Visibility.Visible;
            HighlightImage.Visibility = Visibility.Visible;
            BindClipsForCurrentWidth();
            RebaseSweep(force: true);
        }
        else
        {
            StopSweepAnimation();
            activeSpans = [];
            renderedWidth = 0;
            var rendered = MusicDesktopLyricRenderer.RenderSingle(
                row.Text, BaseMainFontSize, maxWidth, scale, FlowDirection, normalColor, strokeColor);
            if (rendered is null)
            {
                ClearVisuals();
                return;
            }
            MainLineSurface.Width = rendered.WidthDip;
            MainLineSurface.Height = rendered.HeightDip;
            StrokeImage.Width = rendered.WidthDip;
            StrokeImage.Height = rendered.HeightDip;
            StrokeImage.Source = rendered.Image;
            NormalImage.Source = HighlightImage.Source = null;
            NormalImage.Visibility = HighlightImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearVisuals()
    {
        StopSweepAnimation();
        activeSpans = [];
        renderedWidth = 0;
        StrokeImage.Source = NormalImage.Source = HighlightImage.Source = null;
        MainLineSurface.Width = 0;
        MainLineSurface.Height = 0;
    }

    private void EnsureClips()
    {
        if (normalClip is not null) return;
        var normalVisual = ElementCompositionPreview.GetElementVisual(NormalImage);
        var highlightVisual = ElementCompositionPreview.GetElementVisual(HighlightImage);
        var compositor = normalVisual.Compositor;
        normalClip = compositor.CreateInsetClip();
        highlightClip = compositor.CreateInsetClip();
        normalVisual.Clip = normalClip;
        highlightVisual.Clip = highlightClip;
        sweepProps = compositor.CreatePropertySet();
        sweepProps.InsertScalar("X", 0f);
        linearEasing = compositor.CreateLinearEasingFunction();
    }

    private void BindClipsForCurrentWidth()
    {
        EnsureClips();
        if (normalClip is null || highlightClip is null || sweepProps is null) return;
        var compositor = sweepProps.Compositor;
        normalClip.StopAnimation("LeftInset");
        normalClip.StopAnimation("RightInset");
        highlightClip.StopAnimation("LeftInset");
        highlightClip.StopAnimation("RightInset");
        normalClip.LeftInset = normalClip.RightInset = 0f;
        highlightClip.LeftInset = highlightClip.RightInset = 0f;

        normalExpr?.Dispose();
        highlightExpr?.Dispose();
        // Complementary non-overlapping clips: Normal covers [X, W] and Highlight covers [0, X] (mirrored for RTL).
        normalExpr = compositor.CreateExpressionAnimation("Clamp(p.X, 0.0, w)");
        normalExpr.SetReferenceParameter("p", sweepProps);
        normalExpr.SetScalarParameter("w", (float)renderedWidth);

        highlightExpr = compositor.CreateExpressionAnimation("Clamp(w - p.X, 0.0, w)");
        highlightExpr.SetReferenceParameter("p", sweepProps);
        highlightExpr.SetScalarParameter("w", (float)renderedWidth);

        normalClip.StartAnimation(renderedRtl ? "RightInset" : "LeftInset", normalExpr);
        highlightClip.StartAnimation(renderedRtl ? "LeftInset" : "RightInset", highlightExpr);
        clipsBound = true;
    }

    private bool CanAnimateSweep() =>
        IsLoaded && Visibility == Visibility.Visible && IsPlaying &&
        !string.IsNullOrEmpty(PresentationKey) && double.IsFinite(Position) &&
        activeSpans.Length > 0 && renderedWidth > 0 && DesktopVisual.CanAnimate(this);

    private void RebaseSweep(bool force)
    {
        if (!IsLoaded || activeSpans.Length == 0 || renderedWidth <= 0) return;
        if (!clipsBound) BindClipsForCurrentWidth();
        if (sweepProps is null || linearEasing is null) return;

        double pos = double.IsFinite(Position) ? Position : 0;
        sampledPos = pos;
        sampledAt = Stopwatch.GetTimestamp();
        bool animate = CanAnimateSweep();
        sampledRate = animate ? EffectiveRate : null;

        float currentX = EvaluateX(pos);
        if (!animate)
        {
            StopSweepAnimation();
            sweepProps.InsertScalar("X", currentX);
            return;
        }

        double lastEnd = activeSpans[^1].Time + activeSpans[^1].Duration;
        if (pos >= lastEnd)
        {
            StopSweepAnimation();
            sweepProps.InsertScalar("X", (float)renderedWidth);
            return;
        }

        double rate = sampledRate!.Value;
        double totalDurationSeconds = (lastEnd - pos) / rate;
        if (totalDurationSeconds <= 0.001)
        {
            StopSweepAnimation();
            sweepProps.InsertScalar("X", (float)renderedWidth);
            return;
        }

        sweepAnim?.Dispose();
        var anim = sweepProps.Compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromSeconds(totalDurationSeconds);
        double totalSpan = lastEnd - pos;

        void AddFrame(double time, float x)
        {
            float progress = (float)Math.Clamp((time - pos) / totalSpan, 0.0, 1.0);
            anim.InsertKeyFrame(progress, x, linearEasing);
        }

        AddFrame(pos, currentX);
        double cursorTime = pos;
        for (int i = 0; i < activeSpans.Length; i++)
        {
            var span = activeSpans[i];
            double endTime = span.Time + span.Duration;
            if (endTime <= cursorTime) continue;
            if (span.Time > cursorTime)
            {
                AddFrame(span.Time, span.StartX);
                cursorTime = span.Time;
            }
            AddFrame(endTime, span.EndX);
            cursorTime = endTime;
        }
        if (cursorTime < lastEnd) AddFrame(lastEnd, (float)renderedWidth);

        sweepProps.StopAnimation("X");
        sweepProps.InsertScalar("X", currentX);
        sweepProps.StartAnimation("X", anim);
        sweepAnim = anim;
        sweepAnimating = true;
    }

    private float EvaluateX(double position)
    {
        if (activeSpans.Length == 0) return 0f;
        for (int i = 0; i < activeSpans.Length; i++)
        {
            var span = activeSpans[i];
            if (position <= span.Time) return span.StartX;
            double end = span.Time + span.Duration;
            if (position < end && span.Duration > 0)
            {
                double t = (position - span.Time) / span.Duration;
                return (float)(span.StartX + (span.EndX - span.StartX) * t);
            }
        }
        return (float)renderedWidth;
    }

    private void StopSweepAnimation()
    {
        sweepAnimating = false;
        sweepProps?.StopAnimation("X");
        sweepAnim?.Dispose();
        sweepAnim = null;
    }

    private void DisposeSweepResources()
    {
        StopSweepAnimation();
        clipsBound = false;
        normalClip?.StopAnimation("LeftInset");
        normalClip?.StopAnimation("RightInset");
        highlightClip?.StopAnimation("LeftInset");
        highlightClip?.StopAnimation("RightInset");
        normalExpr?.Dispose(); normalExpr = null;
        highlightExpr?.Dispose(); highlightExpr = null;
        linearEasing?.Dispose(); linearEasing = null;
        sweepProps?.Dispose(); sweepProps = null;
        normalClip = null;
        highlightClip = null;
    }
}
