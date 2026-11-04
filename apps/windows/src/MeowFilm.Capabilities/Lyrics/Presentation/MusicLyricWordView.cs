using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Retained native text; WPF animates only the highlight clip.</summary>
public sealed class MusicLyricWordView : Control
{
    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(nameof(Word),
        typeof(MusicLyricWord), typeof(MusicLyricWordView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender, OnWordChanged));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position),
        typeof(double), typeof(MusicLyricWordView), new PropertyMetadata(0d,
            (owner, _) => ((MusicLyricWordView)owner).RefreshPosition()));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight),
        typeof(Brush), typeof(MusicLyricWordView), new FrameworkPropertyMetadata(Brushes.Crimson, FrameworkPropertyMetadataOptions.AffectsRender));
    public MusicLyricWord? Word { get => (MusicLyricWord?)GetValue(WordProperty); set => SetValue(WordProperty, value); }
    // A standalone word accepts a static sample. In a MusicLyricList the list owns samples and clocks.
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public double WordProgress => highlightClip.HasAnimatedProperties && textSize.Width > 0
        ? Math.Clamp(highlightClip.Rect.Width / textSize.Width, 0, 1) : progress;

    private readonly RectangleGeometry highlightClip = new();
    private DrawingGroup? normalText, highlightedText;
    private Size textSize;
    private double progress;
    private MusicLyricList? presenter;

    public MusicLyricWordView()
    {
        Loaded += (_, _) =>
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
                 parent = VisualTreeHelper.GetParent(parent))
                if (parent is MusicLyricList list) { presenter = list; list.Register(this); break; }
            RefreshPosition();
        };
        Unloaded += (_, _) =>
        {
            presenter?.Unregister(this);
            presenter = null;
            StopHighlight();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopHighlight();
            presenter?.RefreshWords();
        };
        SizeChanged += (_, _) => presenter?.RefreshWords();
    }

    private static void OnWordChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var view = (MusicLyricWordView)owner;
        if ((args.OldValue as MusicLyricWord)?.Text != (args.NewValue as MusicLyricWord)?.Text)
            view.InvalidateText();
        view.StopHighlight();
        view.RefreshPosition();
    }

    private void RefreshPosition()
    {
        if (presenter is not null) presenter.RefreshWords();
        else Present(Position, null, recalibrate: true);
    }

    internal void Present(double position, double? rate, bool recalibrate)
    {
        var next = Word is { } word && double.IsFinite(position)
            ? MusicLyricTiming.Progress(word.Time, word.Duration, position) : 0;
        var remaining = rate is > 0 && Word is { Duration: > 0 } timed && position >= timed.Time
            ? (timed.Time + timed.Duration - position) / rate.Value : 0;
        var animate = remaining > 0 && textSize.Width > 0;
        // Viewport/layout events do not restart a clock already following this sample.
        if (!recalibrate && animate && highlightClip.HasAnimatedProperties) return;
        if (!animate && !highlightClip.HasAnimatedProperties && next == progress) return;
        highlightClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        progress = next;
        highlightClip.Rect = ProgressClip(next);
        if (!animate) return;
        var animation = new RectAnimation(ProgressClip(next), ProgressClip(1),
            TimeSpan.FromSeconds(remaining))
        { FillBehavior = FillBehavior.Stop };
        highlightClip.Rect = ProgressClip(1);
        highlightClip.BeginAnimation(RectangleGeometry.RectProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal void StopHighlight()
    {
        if (!highlightClip.HasAnimatedProperties) return;
        progress = WordProgress;
        highlightClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        highlightClip.Rect = ProgressClip(progress);
    }

    private Rect ProgressClip(double value) => new(
        FlowDirection == FlowDirection.RightToLeft ? textSize.Width * (1 - value) : 0,
        0, textSize.Width * value, textSize.Height);

    private void InvalidateText()
    {
        StopHighlight();
        normalText = highlightedText = null;
        InvalidateMeasure();
        InvalidateVisual();
        presenter?.RefreshWords();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty ||
            e.Property == FontStyleProperty || e.Property == FontWeightProperty ||
            e.Property == FontStretchProperty || e.Property == FlowDirectionProperty ||
            e.Property == LanguageProperty || e.Property == TextOptions.TextFormattingModeProperty)
            InvalidateText();
        else if (e.Property == DesktopTheme.IsHighContrastProperty) InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateText();
    }

    private void EnsureText()
    {
        if (normalText is not null) return;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = Language.GetSpecificCulture();
        var text = new FormattedText(Word?.Text ?? "", culture, FlowDirection, typeface, FontSize,
            Brushes.Black, null, TextOptions.GetTextFormattingMode(this), dpi);
        textSize = new(text.WidthIncludingTrailingWhitespace, text.Height);
        var shaped = new DrawingGroup();
        using (var drawing = shaped.Open()) drawing.DrawText(text, new());
        // Clone gives each color layer mutable brushes while retaining native glyph runs and guidelines.
        normalText = shaped.Clone();
        highlightedText = shaped.Clone();
        highlightClip.Rect = ProgressClip(progress);
        presenter?.RefreshWords();
    }

    private static void Paint(DrawingGroup drawing, Brush brush)
    {
        foreach (var child in drawing.Children)
            if (child is GlyphRunDrawing glyph) glyph.ForegroundBrush = brush;
            else if (child is DrawingGroup group) Paint(group, brush);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureText();
        return textSize;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        EnsureText();
        var highContrast = SystemParameters.HighContrast || DesktopTheme.GetIsHighContrast(this);
        Paint(normalText!, highContrast ? SystemColors.WindowTextBrush : Foreground ?? SystemColors.WindowTextBrush);
        Paint(highlightedText!, highContrast ? SystemColors.HighlightBrush : Highlight);
        drawing.DrawDrawing(normalText);
        // The mutable clip is retained by WPF. Animation never invalidates this control's text layout.
        drawing.PushClip(highlightClip);
        drawing.DrawDrawing(highlightedText);
        drawing.Pop();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new WordPeer(this);
    private sealed class WordPeer(MusicLyricWordView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Word?.Text ?? "";
        protected override string GetClassNameCore() => nameof(MusicLyricWordView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
