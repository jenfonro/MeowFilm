using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>Retained native text with a compositor-owned highlight clip.</summary>
public sealed class MusicLyricWordView : UserControl
{
    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(nameof(Word), typeof(MusicLyricWord),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshText()));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double),
        typeof(MusicLyricWordView), new PropertyMetadata(0d, (owner, _) => ((MusicLyricWordView)owner).RefreshPosition()));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(Brush),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshText()));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshText()));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double),
        typeof(MusicLyricWordView), new PropertyMetadata(0d, (owner, _) => ((MusicLyricWordView)owner).RefreshText()));
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
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, ForegroundProperty, FlowDirectionProperty, LanguageProperty })
            RegisterPropertyChangedCallback(property, (_, _) => RefreshText());
        Loaded += (_, _) =>
        {
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicLyricList list) { presenter = list; list.Register(this); break; }
            RefreshText(); RefreshPosition();
        };
        Unloaded += (_, _) => { presenter?.Unregister(this); presenter = null; StopHighlight(); };
        SizeChanged += (_, _) => { StopHighlight(); presenter?.InvalidateViewport(); RefreshPosition(); };
        RegisterPropertyChangedCallback(MeowFilm.Desktop.DesktopTheme.IsHighContrastProperty, (_, _) => RefreshText());
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        { if (Visibility != Visibility.Visible) StopHighlight(); presenter?.InvalidateViewport(); });
    }
    private void RefreshText()
    {
        if (normal is null) return;
        StopHighlight();
        foreach (var text in new[] { normal, highlighted })
        {
            text.Text = Word?.Text ?? ""; text.FontSize = FontSize; text.FontFamily = FontFamily;
            text.FontWeight = Microsoft.UI.Text.FontWeights.Normal; text.FlowDirection = FlowDirection; text.Language = Language;
        }
        bool contrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        var settings = new Windows.UI.ViewManagement.UISettings();
        normal.Foreground = contrast ? new SolidColorBrush(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground)) : Foreground;
        highlighted.Foreground = contrast ? new SolidColorBrush(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent)) : Highlight ?? MusicVisuals.Brush("#DC143C");
        outline.Update(normal, textSurface, contrast ? normal.Foreground : Stroke, StrokeThickness);
        AutomationProperties.SetName(this, Word?.Text ?? "");
        RefreshPosition();
    }
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
