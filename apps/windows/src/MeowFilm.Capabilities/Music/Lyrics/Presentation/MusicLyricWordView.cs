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

/// <summary>Retained native text with complementary compositor-owned normal and highlight clips.</summary>
public sealed class MusicLyricWordView : UserControl
{
    private const string HighlightClipExpression = "w * Clamp(1.0 - (t.Position - start) * invDur, 0.0, 1.0)";
    private const string NormalClipExpression = "w * Clamp((t.Position - start) * invDur, 0.0, 1.0)";
    private static readonly SolidColorBrush DefaultHighlight = DesktopVisual.Brush("#DC143C");
    [ThreadStatic] private static (Windows.UI.Color Color, SolidColorBrush Brush)? highContrastNormalCache;
    [ThreadStatic] private static (Windows.UI.Color Color, SolidColorBrush Brush)? highContrastHighlightCache;

    private static SolidColorBrush CachedBrush(ref (Windows.UI.Color Color, SolidColorBrush Brush)? cache, Windows.UI.Color color)
    {
        if (cache is { } existing && existing.Color == color) return existing.Brush;
        var created = new SolidColorBrush(color);
        cache = (color, created);
        return created;
    }

    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(nameof(Word), typeof(MusicLyricWord),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshWord()));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double),
        typeof(MusicLyricWordView), new PropertyMetadata(0d, (owner, _) => ((MusicLyricWordView)owner).RefreshPosition()));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(Brush),
        typeof(MusicLyricWordView), new PropertyMetadata(null, (owner, _) => ((MusicLyricWordView)owner).RefreshHighlightColor()));

    public MusicLyricWord? Word { get => (MusicLyricWord?)GetValue(WordProperty); set => SetValue(WordProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public Brush? Highlight { get => (Brush?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    private readonly Grid rootGrid = new();
    private readonly TextBlock normal = new(), highlighted = new();
    private readonly InsetClip normalClip, highlightClip;
    private MusicLyricList? presenter;
    private FrameworkElement? rowHost;
    private ExpressionAnimation? normalExpression, highlightExpression;
    private CompositionPropertySet? boundTimeline;
    private string? boundHighlightProp, boundNormalProp;
    private double progress;
    private double boundWidth;
    private double boundStart;
    private double boundDuration;
    private bool animating;

    public double WordProgress => animating && presenter is not null && Word is { } word
        ? MusicLyricTiming.Progress(word.Time, word.Duration, presenter.CurrentPosition)
        : progress;

    public MusicLyricWordView()
    {
        IsTabStop = false; IsHitTestVisible = false;
        rootGrid.Children.Add(normal); rootGrid.Children.Add(highlighted); Content = rootGrid;
        var normalVisual = ElementCompositionPreview.GetElementVisual(normal);
        var highlightVisual = ElementCompositionPreview.GetElementVisual(highlighted);
        normalClip = normalVisual.Compositor.CreateInsetClip(); normalVisual.Clip = normalClip;
        highlightClip = highlightVisual.Compositor.CreateInsetClip(); highlightVisual.Clip = highlightClip;
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, FlowDirectionProperty, LanguageProperty })
            RegisterPropertyChangedCallback(property, (_, _) => RefreshTypography());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => RefreshNormalColor());
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) =>
        {
            RefreshNormalColor(); RefreshHighlightColor();
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
        rowHost = null;
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (rowHost is null && node is MusicLyricRowView or ListViewItem)
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
        presenter?.Unregister(this);
        presenter = null;
        rowHost = null;
        StopHighlight();
        normalExpression?.Dispose(); normalExpression = null;
        highlightExpression?.Dispose(); highlightExpression = null;
        boundTimeline = null;
        boundHighlightProp = boundNormalProp = null;
        boundWidth = 0;
        boundStart = double.NaN;
        boundDuration = double.NaN;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (animating && highlightExpression is not null && normalExpression is not null && ActualWidth > 0)
        {
            if (Math.Abs(boundWidth - ActualWidth) > 0.01)
            {
                boundWidth = ActualWidth;
                highlightExpression.SetScalarParameter("w", (float)ActualWidth);
                normalExpression.SetScalarParameter("w", (float)ActualWidth);
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
        InvalidateMeasure(); RefreshPosition();
    }

    private void RefreshNormalColor()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        normal.Foreground = contrast
            ? CachedBrush(ref highContrastNormalCache, SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground))
            : Foreground;
    }

    private void RefreshHighlightColor()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        highlighted.Foreground = contrast
            ? CachedBrush(ref highContrastHighlightCache, SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent))
            : Highlight ?? DefaultHighlight;
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
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        string targetHighlightProp = rtl ? "LeftInset" : "RightInset";
        string targetNormalProp = rtl ? "RightInset" : "LeftInset";
        bool created = highlightExpression is null || normalExpression is null;
        highlightExpression ??= highlightClip.Compositor.CreateExpressionAnimation(HighlightClipExpression);
        normalExpression ??= normalClip.Compositor.CreateExpressionAnimation(NormalClipExpression);
        bool needsBind = created || !animating || !ReferenceEquals(boundTimeline, timeline) ||
            boundHighlightProp != targetHighlightProp || boundNormalProp != targetNormalProp;
        if (created || !ReferenceEquals(boundTimeline, timeline))
        {
            highlightExpression.SetReferenceParameter("t", timeline);
            normalExpression.SetReferenceParameter("t", timeline);
            boundTimeline = timeline;
        }
        if (needsBind || Math.Abs(boundWidth - ActualWidth) > 0.01)
        {
            boundWidth = ActualWidth;
            highlightExpression.SetScalarParameter("w", (float)ActualWidth);
            normalExpression.SetScalarParameter("w", (float)ActualWidth);
        }
        if (needsBind || Math.Abs(boundStart - word.Time) > 1e-6)
        {
            boundStart = word.Time;
            highlightExpression.SetScalarParameter("start", (float)word.Time);
            normalExpression.SetScalarParameter("start", (float)word.Time);
        }
        if (needsBind || Math.Abs(boundDuration - word.Duration) > 1e-6)
        {
            boundDuration = word.Duration;
            float invDur = word.Duration > 0 ? (float)(1.0 / word.Duration) : 1e6f;
            highlightExpression.SetScalarParameter("invDur", invDur);
            normalExpression.SetScalarParameter("invDur", invDur);
        }
        progress = next;
        if (needsBind)
        {
            highlightClip.StopAnimation("LeftInset");
            highlightClip.StopAnimation("RightInset");
            normalClip.StopAnimation("LeftInset");
            normalClip.StopAnimation("RightInset");
            highlightClip.LeftInset = highlightClip.RightInset = 0f;
            normalClip.LeftInset = normalClip.RightInset = 0f;
            normal.Visibility = Visibility.Visible;
            highlighted.Visibility = Visibility.Visible;
            highlightClip.StartAnimation(targetHighlightProp, highlightExpression);
            normalClip.StartAnimation(targetNormalProp, normalExpression);
            boundHighlightProp = targetHighlightProp;
            boundNormalProp = targetNormalProp;
            animating = true;
        }
        return true;
    }

    internal void StopHighlight(double? settledProgress = null)
    {
        double target = settledProgress ?? WordProgress;
        bool wasAnimating = animating;
        animating = false;
        boundHighlightProp = boundNormalProp = null;
        progress = target;
        if (wasAnimating)
        {
            highlightClip.StopAnimation("LeftInset");
            highlightClip.StopAnimation("RightInset");
            normalClip.StopAnimation("LeftInset");
            normalClip.StopAnimation("RightInset");
        }
        ApplyClip(progress);
    }

    private void ApplyClip(double value)
    {
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        normal.Visibility = value < 1 || animating ? Visibility.Visible : Visibility.Collapsed;
        highlighted.Visibility = value > 0 || animating ? Visibility.Visible : Visibility.Collapsed;
        float highlightInset = (float)(ActualWidth * (1 - value));
        float normalInset = (float)(ActualWidth * value);
        highlightClip.LeftInset = rtl ? highlightInset : 0f;
        highlightClip.RightInset = rtl ? 0f : highlightInset;
        normalClip.LeftInset = rtl ? 0f : normalInset;
        normalClip.RightInset = rtl ? normalInset : 0f;
    }
}
