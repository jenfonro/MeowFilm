using MeowFilm.Desktop;
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
    private const string ClipExpressionFormula = "w * Clamp(1.0 - (t.Position - start) * invDur, 0.0, 1.0)";
    private static readonly SolidColorBrush DefaultHighlight = DesktopVisual.Brush("#DC143C");
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

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

    private readonly Grid rootGrid = new();
    private readonly TextBlock normal = new(), highlighted = new();
    private readonly Border textSurface;
    private readonly InsetClip highlightClip;
    private MusicLyricOutline? outline;
    private MusicLyricList? presenter;
    private FrameworkElement? rowHost;
    private MusicDesktopLyricsInputRegion? inputRegion;
    private ExpressionAnimation? clipExpression;
    private CompositionPropertySet? boundTimeline;
    private string? boundProperty;
    private double progress;
    private double boundWidth;
    private double boundStart;
    private double boundDuration;
    private bool animating;

    internal bool IsHighlightAnimating => animating;
    internal MusicLyricOutline? Outline => outline;
    internal FrameworkElement? RowHost => rowHost;

    public double WordProgress => animating && presenter is not null && Word is { } word
        ? MusicLyricTiming.Progress(word.Time, word.Duration, presenter.CurrentPosition)
        : progress;

    public MusicLyricWordView()
    {
        IsTabStop = false; IsHitTestVisible = false;
        textSurface = new Border { Background = TransparentBrush, Child = normal };
        rootGrid.Children.Add(textSurface); rootGrid.Children.Add(highlighted); Content = rootGrid;
        var visual = ElementCompositionPreview.GetElementVisual(highlighted);
        highlightClip = visual.Compositor.CreateInsetClip(); visual.Clip = highlightClip;
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, FlowDirectionProperty, LanguageProperty })
            RegisterPropertyChangedCallback(property, (_, _) => RefreshTypography());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => RefreshNormalColor());
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        RegisterPropertyChangedCallback(MeowFilm.Desktop.DesktopTheme.IsHighContrastProperty, (_, _) =>
        {
            RefreshNormalColor(); RefreshHighlightColor();
            if (!MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this)) RefreshOutline();
            StopHighlight(); RefreshPosition();
        });
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible) StopHighlight();
            presenter?.InvalidateViewport();
        });
        RefreshTypography(); RefreshNormalColor(); RefreshHighlightColor();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (StrokeThickness > 0)
        {
            RefreshOutline();
            EnsureInputRegionRegistered();
        }
        rowHost = null;
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (rowHost is null && node is MusicLyricRowView or ListViewItem or MusicDesktopLyricLineView)
                rowHost = (FrameworkElement)node;
            if (node is MusicLyricList list)
            {
                presenter = list;
                list.Register(this, rowHost ?? this);
                break;
            }
        }
        RefreshPosition();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        if (inputRegion is not null)
        {
            inputRegion.Unregister(this);
            inputRegion = null;
        }
        presenter?.Unregister(this);
        presenter = null;
        rowHost = null;
        StopHighlight();
        clipExpression?.Dispose();
        clipExpression = null;
        boundTimeline = null;
        boundProperty = null;
        boundWidth = 0;
        boundStart = double.NaN;
        boundDuration = double.NaN;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (animating && clipExpression is not null && ActualWidth > 0)
        {
            if (Math.Abs(boundWidth - ActualWidth) > 0.01)
            {
                boundWidth = ActualWidth;
                clipExpression.SetScalarParameter("w", (float)ActualWidth);
            }
            return;
        }
        StopHighlight();
        if (presenter is not null) presenter.RefreshWords();
        else ApplyClip(progress);
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
        string text = Word?.Text ?? "";
        if (normal.Text != text)
        {
            normal.Text = highlighted.Text = text;
            AutomationProperties.SetName(this, text);
            InvalidateMeasure();
            RefreshOutline();
        }
        RefreshPosition();
    }

    private void RefreshTypography()
    {
        bool changed = normal.FontFamily != FontFamily || Math.Abs(normal.FontSize - FontSize) > 1e-6 ||
            normal.FlowDirection != FlowDirection || normal.Language != Language ||
            normal.FontWeight.Weight != Microsoft.UI.Text.FontWeights.Normal.Weight;
        if (!changed) return;
        StopHighlight();
        normal.FontSize = highlighted.FontSize = FontSize;
        normal.FontFamily = highlighted.FontFamily = FontFamily;
        normal.FontWeight = highlighted.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        normal.FlowDirection = highlighted.FlowDirection = FlowDirection;
        normal.Language = highlighted.Language = Language;
        InvalidateMeasure(); RefreshOutline(); RefreshPosition();
    }

    private void RefreshNormalColor()
    {
        bool contrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        normal.Foreground = contrast
            ? new SolidColorBrush(MeowFilm.Desktop.SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground))
            : Foreground;
        if (contrast && StrokeThickness > 0) RefreshOutline();
    }

    private void RefreshHighlightColor()
    {
        bool contrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        highlighted.Foreground = contrast
            ? new SolidColorBrush(MeowFilm.Desktop.SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent))
            : Highlight ?? DefaultHighlight;
    }

    private void EnsureInputRegionRegistered()
    {
        if (!IsLoaded || outline is null) return;
        inputRegion ??= MusicDesktopLyricsInputRegion.Find(this);
        inputRegion?.Register(this, outline);
    }

    private void RefreshOutline()
    {
        if (StrokeThickness <= 0)
        {
            if (outline is not null)
            {
                inputRegion?.Unregister(this);
                inputRegion = null;
                outline.Detach();
                outline = null;
            }
            return;
        }
        var brush = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this) ? normal.Foreground : Stroke;
        if (brush is not SolidColorBrush)
        {
            outline?.Update(normal, textSurface, null, 0);
            return;
        }
        if (outline is null)
        {
            outline = new MusicLyricOutline(rootGrid, insertAtBottom: true);
            EnsureInputRegionRegistered();
        }
        outline.Update(normal, textSurface, brush, StrokeThickness);
    }

    private void RefreshPosition()
    {
        if (presenter is not null) presenter.RefreshWords();
        else PresentStatic(Position);
    }

    internal void PresentStatic(double position)
    {
        double next = Word is { } word && double.IsFinite(position)
            ? MusicLyricTiming.Progress(word.Time, word.Duration, position) : 0;
        if (!animating && Math.Abs(progress - next) < 1e-6) return;
        StopHighlight(next);
    }

    internal bool BindTimeline(CompositionPropertySet? timeline, double position, bool timelineActive)
    {
        if (Word is not { } word || !double.IsFinite(position))
        {
            StopHighlight(0);
            return false;
        }
        double next = MusicLyricTiming.Progress(word.Time, word.Duration, position);
        if (!timelineActive || timeline is null || ActualWidth <= 0 || Visibility != Visibility.Visible || next >= 1.0)
        {
            if (animating || Math.Abs(progress - next) >= 1e-6) StopHighlight(next);
            return false;
        }
        string targetProp = FlowDirection == FlowDirection.RightToLeft ? "LeftInset" : "RightInset";
        bool created = clipExpression is null;
        clipExpression ??= highlightClip.Compositor.CreateExpressionAnimation(ClipExpressionFormula);
        bool needsBind = created || !animating || !ReferenceEquals(boundTimeline, timeline) || boundProperty != targetProp;
        if (created || !ReferenceEquals(boundTimeline, timeline))
        {
            clipExpression.SetReferenceParameter("t", timeline);
            boundTimeline = timeline;
        }
        if (needsBind || Math.Abs(boundWidth - ActualWidth) > 0.01)
        {
            boundWidth = ActualWidth;
            clipExpression.SetScalarParameter("w", (float)ActualWidth);
        }
        if (needsBind || Math.Abs(boundStart - word.Time) > 1e-6)
        {
            boundStart = word.Time;
            clipExpression.SetScalarParameter("start", (float)word.Time);
        }
        if (needsBind || Math.Abs(boundDuration - word.Duration) > 1e-6)
        {
            boundDuration = word.Duration;
            float invDur = word.Duration > 0 ? (float)(1.0 / word.Duration) : 1e6f;
            clipExpression.SetScalarParameter("invDur", invDur);
        }
        progress = next;
        if (needsBind)
        {
            highlightClip.StopAnimation("LeftInset");
            highlightClip.StopAnimation("RightInset");
            highlightClip.LeftInset = 0f;
            highlightClip.RightInset = 0f;
            highlighted.Visibility = Visibility.Visible;
            highlightClip.StartAnimation(targetProp, clipExpression);
            boundProperty = targetProp;
            animating = true;
        }
        return true;
    }

    internal void StopHighlight(double? settledProgress = null)
    {
        double target = settledProgress ?? WordProgress;
        bool wasAnimating = animating;
        animating = false;
        boundProperty = null;
        progress = target;
        if (wasAnimating)
        {
            highlightClip.StopAnimation("LeftInset");
            highlightClip.StopAnimation("RightInset");
        }
        ApplyClip(progress);
    }

    private void ApplyClip(double value)
    {
        highlighted.Visibility = value > 0 || animating ? Visibility.Visible : Visibility.Collapsed;
        float inset = (float)(ActualWidth * (1 - value));
        highlightClip.LeftInset = FlowDirection == FlowDirection.RightToLeft ? inset : 0f;
        highlightClip.RightInset = FlowDirection == FlowDirection.LeftToRight ? inset : 0f;
    }
}
