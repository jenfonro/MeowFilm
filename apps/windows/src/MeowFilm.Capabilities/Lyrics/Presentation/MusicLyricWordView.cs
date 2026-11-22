using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities.Library;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

/// <summary>Retained native text with a compositor-owned highlight clip.</summary>
public sealed class MusicLyricWordView : UserControl
{
    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(nameof(Word), typeof(MusicLyricWord),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshWord()));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double),
        typeof(MusicLyricWordView), new PropertyMetadata(0d, (owner, _) => ((MusicLyricWordView)owner).RefreshPosition()));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(Brush),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshHighlightColor()));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshOutline()));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double),
        typeof(MusicLyricWordView), new PropertyMetadata(0d, (owner, _) => ((MusicLyricWordView)owner).RefreshOutline()));
    public MusicLyricWord? Word { get => (MusicLyricWord?)GetValue(WordProperty); set => SetValue(WordProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public Brush? Highlight { get => (Brush?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    private readonly TextBlock normal = new(), highlighted = new();
    private readonly Border textSurface;
    private readonly MusicLyricOutline outline;
    private readonly InsetClip highlightClip;
    private MusicLyricList? presenter;
    private MusicDesktopLyricsInputRegion? inputRegion;
    private double progress, animationSeconds;
    private long animationStart;
    private bool animating;
    internal bool IsHighlightAnimating => animating;
    internal MusicLyricOutline Outline => outline;
    public double WordProgress => animating
        ? Math.Clamp(progress + (1 - progress) * Stopwatch.GetElapsedTime(animationStart).TotalSeconds / animationSeconds, 0, 1)
        : progress;

    public MusicLyricWordView()
    {
        IsTabStop = false; IsHitTestVisible = false;
        var grid = new Grid(); outline = new(grid);
        textSurface = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = normal };
        grid.Children.Add(textSurface); grid.Children.Add(highlighted); Content = grid;
        var visual = ElementCompositionPreview.GetElementVisual(highlighted);
        highlightClip = visual.Compositor.CreateInsetClip(); visual.Clip = highlightClip;
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, FlowDirectionProperty, LanguageProperty })
            RegisterPropertyChangedCallback(property, (_, _) => RefreshTypography());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => RefreshNormalColor());
        Loaded += (_, _) =>
        {
            inputRegion = MusicDesktopLyricsInputRegion.Find(this);
            inputRegion?.Register(this, outline);
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicLyricList list) { presenter = list; list.Register(this); break; }
            RefreshPosition();
        };
        Unloaded += (_, _) =>
        {
            inputRegion?.Unregister(this); inputRegion = null;
            presenter?.Unregister(this); presenter = null; StopHighlight();
        };
        SizeChanged += (_, _) => { StopHighlight(); presenter?.InvalidateViewport(); RefreshPosition(); };
        RegisterPropertyChangedCallback(MeowFilm.Desktop.DesktopTheme.IsHighContrastProperty, (_, _) =>
        {
            RefreshNormalColor(); RefreshHighlightColor();
            if (!MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this)) RefreshOutline();
            StopHighlight(); RefreshPosition();
        });
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        { if (Visibility != Visibility.Visible) StopHighlight(); presenter?.InvalidateViewport(); });
        RefreshTypography(); RefreshNormalColor(); RefreshHighlightColor();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        string text = normal.Text;
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        // TextBlock's desired width excludes trailing whitespace. Its automatic
        // Run exposes the actual text edges without the container's closing positions.
        // ContentStart initializes TextBlock's native TextView as well as its
        // automatic Run. Run pointers alone cannot resolve character geometry.
        Run? run = null;
        if (end < text.Length)
        {
            _ = normal.ContentStart;
            run = (Run)normal.Inlines[0];
        }
        var size = base.MeasureOverride(availableSize);
        if (run is null) return size;
        var suffix = run.ContentEnd.GetPositionAtOffset(end - text.Length, LogicalDirection.Forward)
            .GetCharacterRect(LogicalDirection.Forward);
        var last = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        return new(size.Width + Math.Abs(last.X - suffix.X), size.Height);
    }
    private void RefreshWord()
    {
        StopHighlight();
        normal.Text = highlighted.Text = Word?.Text ?? "";
        AutomationProperties.SetName(this, Word?.Text ?? "");
        InvalidateMeasure(); RefreshOutline(); RefreshPosition();
    }
    private void RefreshTypography()
    {
        StopHighlight();
        foreach (var text in new[] { normal, highlighted })
        {
            text.FontSize = FontSize; text.FontFamily = FontFamily;
            text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            text.FlowDirection = FlowDirection; text.Language = Language;
        }
        InvalidateMeasure(); RefreshOutline(); RefreshPosition();
    }
    private void RefreshNormalColor()
    {
        bool contrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        normal.Foreground = contrast
            ? new SolidColorBrush(MeowFilm.Desktop.SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground))
            : Foreground;
        if (contrast) RefreshOutline();
    }
    private void RefreshHighlightColor()
    {
        bool contrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        highlighted.Foreground = contrast
            ? new SolidColorBrush(MeowFilm.Desktop.SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent))
            : Highlight ?? DefaultHighlight;
    }
    private static readonly SolidColorBrush DefaultHighlight = MusicVisuals.Brush("#DC143C");
    private void RefreshOutline() => outline.Update(normal, textSurface,
        MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this) ? normal.Foreground : Stroke, StrokeThickness);
    private void RefreshPosition()
    {
        if (presenter is not null) presenter.RefreshWords(); else Present(Position, null, true);
    }
    internal bool Present(double position, double? rate, bool recalibrate)
    {
        double next = Word is { } word && double.IsFinite(position)
            ? MusicLyricTiming.Progress(word.Time, word.Duration, position) : 0;
        double remaining = rate is > 0 && Word is { Duration: > 0 } timed && position >= timed.Time
            ? (timed.Time + timed.Duration - position) / rate.Value : 0;
        bool animate = remaining > 0 && ActualWidth > 0 && Visibility == Visibility.Visible && MusicVisuals.CanAnimate(this);
        if (!recalibrate && animate && animating) return true;
        if (!animate && !animating && Math.Abs(progress - next) < 1e-6) return false;
        StopHighlight(); progress = next; ApplyClip(next);
        if (!animate) return false;
        var animation = highlightClip.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, (float)(ActualWidth * (1 - next)));
        animation.InsertKeyFrame(1, 0, highlightClip.Compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromSeconds(remaining);
        animationSeconds = remaining; animationStart = Stopwatch.GetTimestamp(); animating = true;
        highlighted.Visibility = Visibility.Visible;
        highlightClip.StartAnimation(FlowDirection == FlowDirection.RightToLeft ? "LeftInset" : "RightInset", animation);
        return true;
    }
    internal void StopHighlight()
    {
        progress = WordProgress; animating = false;
        highlightClip.StopAnimation("LeftInset"); highlightClip.StopAnimation("RightInset"); ApplyClip(progress);
    }
    private void ApplyClip(double value)
    {
        highlighted.Visibility = value > 0 || animating ? Visibility.Visible : Visibility.Collapsed;
        highlightClip.LeftInset = FlowDirection == FlowDirection.RightToLeft ? (float)(ActualWidth * (1 - value)) : 0;
        highlightClip.RightInset = FlowDirection == FlowDirection.LeftToRight ? (float)(ActualWidth * (1 - value)) : 0;
    }
}
