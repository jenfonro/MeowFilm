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
public sealed class MusicLyricWordView : Grid
{
    private const string HighlightClipExpression = "w * Clamp(1.0 - (t.Position - start) * invDur, 0.0, 1.0)";
    private const string NormalClipExpression = "w * Clamp((t.Position - start) * invDur, 0.0, 1.0)";
    [ThreadStatic] private static SolidColorBrush? defaultHighlight;
    private static SolidColorBrush DefaultHighlight => defaultHighlight ??= DesktopVisual.Brush("#DC143C");
    [ThreadStatic] private static (Windows.UI.Color Color, SolidColorBrush Brush)? highContrastNormalCache;
    [ThreadStatic] private static (Windows.UI.Color Color, SolidColorBrush Brush)? highContrastHighlightCache;

    private static SolidColorBrush CachedBrush(ref (Windows.UI.Color Color, SolidColorBrush Brush)? cache, Windows.UI.Color color)
    {
        if (cache is { } existing && existing.Color == color) return existing.Brush;
        var created = new SolidColorBrush(color);
        cache = (color, created);
        return created;
    }

    private readonly TextBlock normal = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Normal };
    private readonly TextBlock highlighted = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Normal };
    private readonly InsetClip normalClip, highlightClip;
    private MusicLyricWord? word;
    private MusicLyricList? presenter;
    private ExpressionAnimation? normalExpression, highlightExpression;
    private CompositionPropertySet? boundTimeline;
    private string? boundHighlightProp, boundNormalProp;
    private double progress;
    private double boundWidth;
    private double boundStart;
    private double boundDuration;
    private bool animating;

    public MusicLyricWord? Word
    {
        get => word;
        set
        {
            if (ReferenceEquals(word, value)) return;
            word = value;
            RefreshWord();
        }
    }

    public double WordProgress => animating && presenter is not null && word is { } current
        ? MusicLyricTiming.Progress(current.Time, current.Duration, presenter.CurrentPosition)
        : progress;

    public MusicLyricWordView()
    {
        IsHitTestVisible = false;
        Children.Add(normal);
        Children.Add(highlighted);
        var normalVisual = ElementCompositionPreview.GetElementVisual(normal);
        var highlightVisual = ElementCompositionPreview.GetElementVisual(highlighted);
        normalClip = normalVisual.Compositor.CreateInsetClip(); normalVisual.Clip = normalClip;
        highlightClip = highlightVisual.Compositor.CreateInsetClip(); highlightVisual.Clip = highlightClip;
        SizeChanged += OnSizeChanged;
    }

    internal void AttachPresenter(MusicLyricList? list, FrameworkElement? rowHost)
    {
        if (ReferenceEquals(presenter, list))
        {
            if (list is not null) list.Register(this, rowHost ?? this);
            return;
        }
        DetachPresenter();
        presenter = list;
        if (list is not null) list.Register(this, rowHost ?? this);
        RefreshPosition();
    }

    internal void DetachPresenter()
    {
        presenter?.Unregister(this);
        presenter = null;
        StopHighlight();
        normalExpression?.Dispose(); normalExpression = null;
        highlightExpression?.Dispose(); highlightExpression = null;
        boundTimeline = null;
        boundHighlightProp = boundNormalProp = null;
        boundWidth = 0;
        boundStart = double.NaN;
        boundDuration = double.NaN;
    }

    internal void SetColors(Brush? normalBrush, Brush? highlightBrush, bool highContrast)
    {
        normal.Foreground = highContrast
            ? CachedBrush(ref highContrastNormalCache, SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground))
            : normalBrush;
        highlighted.Foreground = highContrast
            ? CachedBrush(ref highContrastHighlightCache, SystemAppearance.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent))
            : highlightBrush ?? DefaultHighlight;
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
        string text = word?.Text ?? "";
        if (normal.Text != text)
        {
            normal.Text = highlighted.Text = text;
            AutomationProperties.SetName(this, text);
            InvalidateMeasure();
        }
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        if (presenter is not null) presenter.RefreshWords();
        else PresentStatic(0);
    }

    internal void PresentStatic(double position)
    {
        double next = word is { } current && double.IsFinite(position)
            ? MusicLyricTiming.Progress(current.Time, current.Duration, position) : 0;
        if (!animating && Math.Abs(progress - next) < 1e-6) return;
        StopHighlight(next);
    }

    internal bool BindTimeline(CompositionPropertySet? timeline, double position, bool timelineActive)
    {
        if (word is not { } current || !double.IsFinite(position))
        {
            StopHighlight(0);
            return false;
        }
        double next = MusicLyricTiming.Progress(current.Time, current.Duration, position);
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
        if (needsBind || Math.Abs(boundStart - current.Time) > 1e-6)
        {
            boundStart = current.Time;
            highlightExpression.SetScalarParameter("start", (float)current.Time);
            normalExpression.SetScalarParameter("start", (float)current.Time);
        }
        if (needsBind || Math.Abs(boundDuration - current.Duration) > 1e-6)
        {
            boundDuration = current.Duration;
            float invDur = current.Duration > 0 ? (float)(1.0 / current.Duration) : 1e6f;
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
